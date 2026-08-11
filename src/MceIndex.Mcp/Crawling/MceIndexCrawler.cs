using ManagedCode.Playwright.Stealth;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using MceIndex.Mcp.Configuration;
using MceIndex.Mcp.Domain;
using MceIndex.Mcp.Parsing;
using MceIndex.Mcp.Serialization;

namespace MceIndex.Mcp.Crawling;

public interface IMceIndexCrawler : IAsyncDisposable
{
    Task<CrawledPage> CrawlAsync(Uri target, CancellationToken cancellationToken);
    Task CloseBrowserAsync();
}

public sealed partial class MceIndexCrawler(
    MceIndexOptions options,
    MceIndexParser parser,
    TimeProvider timeProvider,
    ILogger<MceIndexCrawler> logger) : IMceIndexCrawler
{
    private const int MaxCharts = 32;
    private const int MaxTotalChartPoints = 100_000;
    private static readonly string[] LifeIndexViews =
        ["产业规模占比", "直接就业能力", "净财政能力", "行业下钻", "方法来源"];
    private static readonly string[] LifeIndexIndustries =
        ["集成电路", "新能源汽车", "新能源", "电气化设备", "医药制造"];
    private static readonly Dictionary<string, (int Initial, int Captured, int Points)> ProductionChartMinimums =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["/Monthly_Overview"] = (6, 6, 45),
            ["/LI_Monthly"] = (1, 23, 1_470),
            ["/Meaningful_CPI_PPI"] = (3, 3, 1_159),
            ["/Meaningful_TSF"] = (2, 2, 144),
            ["/Meaningful_Retail"] = (2, 2, 256),
        };
    private readonly SemaphoreSlim browserGate = new(1, 1);
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private DateTimeOffset nextRequestAt;
    private IPlaywright? playwright;
    private IBrowser? browser;
    private IBrowserContext? context;
    private bool disposed;

    public async Task<CrawledPage> CrawlAsync(Uri target, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            var browserContext = await EnsureContextAsync(cancellationToken).ConfigureAwait(false);
            await using var page = await browserContext.NewPageAsync().ConfigureAwait(false);
            page.SetDefaultTimeout((float)options.RequestTimeout.TotalMilliseconds);
            page.SetDefaultNavigationTimeout((float)options.RequestTimeout.TotalMilliseconds);

            await WaitForRequestSlotAsync(cancellationToken).ConfigureAwait(false);
            await page.GotoAsync(target.AbsoluteUri, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = (float)options.RequestTimeout.TotalMilliseconds,
            }).ConfigureAwait(false);
            try
            {
                await page.WaitForSelectorAsync("[data-testid='stMain'], main", new PageWaitForSelectorOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = (float)options.RequestTimeout.TotalMilliseconds,
                }).ConfigureAwait(false);
                await page.WaitForFunctionAsync(
                    "() => document.querySelector(\"[data-testid='stMain'], main\")?.querySelector(\"h1\")?.innerText.trim().length > 0",
                    null,
                    new PageWaitForFunctionOptions
                    {
                        Timeout = (float)options.RequestTimeout.TotalMilliseconds,
                    }).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await AssertNoChallengeAsync(page).ConfigureAwait(false);
                throw;
            }
            await WaitForDomQuietAsync(page).ConfigureAwait(false);
            await AssertNoChallengeAsync(page).ConfigureAwait(false);
            await WaitForExpectedInitialChartsAsync(page, target).ConfigureAwait(false);

            var documents = new List<string>(MceIndexParser.MaxHtmlDocuments);
            var mainDocument = await page.ContentAsync().ConfigureAwait(false);
            var totalDocumentCharacters = MceIndexParser.IncludeDocument(mainDocument, 0);
            documents.Add(mainDocument);
            foreach (var frame in page.Frames)
            {
                if (ReferenceEquals(frame, page.MainFrame))
                {
                    continue;
                }
                if (documents.Count >= MceIndexParser.MaxHtmlDocuments)
                {
                    throw new MceIndexException(
                        MceIndexErrorCode.ExtractionFailed,
                        $"MCEIndex returned more than {MceIndexParser.MaxHtmlDocuments} HTML documents.");
                }

                try
                {
                    var frameDocument = await frame.ContentAsync().ConfigureAwait(false);
                    totalDocumentCharacters = MceIndexParser.IncludeDocument(
                        frameDocument,
                        totalDocumentCharacters);
                    documents.Add(frameDocument);
                }
                catch (PlaywrightException error)
                {
                    LogInaccessibleFrame(logger, error, target);
                }
            }

            if (documents.Any(MceIndexParser.IsAccessChallenge))
            {
                throw new MceIndexException(MceIndexErrorCode.AccessChallenge,
                    "Cloudflare verification blocked MCEIndex acquisition.");
            }

            var charts = await ExtractChartsAsync(page).ConfigureAwait(false);
            if (await SelectAllHistoryAsync(page).ConfigureAwait(false))
            {
                var allHistoryDocument = await page.ContentAsync().ConfigureAwait(false);
                totalDocumentCharacters = MceIndexParser.IncludeDocument(
                    allHistoryDocument,
                    totalDocumentCharacters);
                documents.Add(allHistoryDocument);
                charts = MergeCharts(charts, await ExtractChartsAsync(page).ConfigureAwait(false));
            }

            var source = Uri.TryCreate(page.Url, UriKind.Absolute, out var finalUri) ? finalUri : target;
            if (source.AbsolutePath.TrimEnd('/').EndsWith("/LI_Monthly", StringComparison.OrdinalIgnoreCase))
            {
                charts = await CaptureLifeIndexViewsAsync(
                    page,
                    documents,
                    totalDocumentCharacters,
                    charts).ConfigureAwait(false);
            }
            ValidateProductionChartCoverage(source, charts);
            var snapshot = parser.Extract(documents, source, timeProvider.GetUtcNow()) with
            {
                Charts = charts,
            };
            if (snapshot.Headings.Length == 0 ||
                (snapshot.Metrics.Length == 0 &&
                 snapshot.Tables.Length == 0 &&
                 snapshot.Cards.Length == 0 &&
                 snapshot.Charts.Length == 0 &&
                 snapshot.Text.Length == 0))
            {
                throw new MceIndexException(MceIndexErrorCode.ExtractionFailed,
                    $"MCEIndex returned no complete page content for {source}.");
            }

            return new CrawledPage(snapshot, [.. documents]);
        }
        catch (TimeoutException error)
        {
            throw new MceIndexException(MceIndexErrorCode.LoadTimeout,
                $"MCEIndex did not become ready within {options.RequestTimeout.TotalMilliseconds:F0}ms.", innerException: error);
        }
        catch (PlaywrightException error) when (error.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            throw new MceIndexException(MceIndexErrorCode.BrowserNotFound,
                "Playwright Chromium is not installed and MCEINDEX_BROWSER_EXECUTABLE did not resolve to a browser.", innerException: error);
        }
        catch (PlaywrightException error) when (error.Message.Contains("Driver not found", StringComparison.OrdinalIgnoreCase))
        {
            throw new MceIndexException(MceIndexErrorCode.BrowserNotFound,
                "Node.js was not found. Put node on PATH or set PLAYWRIGHT_NODEJS_PATH to its absolute path.",
                innerException: error);
        }
    }

    public async Task CloseBrowserAsync()
    {
        await browserGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (context is not null)
            {
                await context.CloseAsync().ConfigureAwait(false);
                context = null;
            }
            if (browser is not null)
            {
                await browser.CloseAsync().ConfigureAwait(false);
                browser = null;
            }
            playwright?.Dispose();
            playwright = null;
        }
        finally
        {
            browserGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await CloseBrowserAsync().ConfigureAwait(false);
        browserGate.Dispose();
        requestGate.Dispose();
    }

    private async Task<IBrowserContext> EnsureContextAsync(CancellationToken cancellationToken)
    {
        if (context is not null)
        {
            return context;
        }

        await browserGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (context is not null)
            {
                return context;
            }

            if (options.NodeExecutable is not null)
            {
                Environment.SetEnvironmentVariable("PLAYWRIGHT_NODEJS_PATH", options.NodeExecutable);
            }

            playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
            var stealthConfig = new StealthConfig
            {
                NavigatorUserAgentValue = options.BrowserUserAgent,
            };
            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = options.Headless,
                ExecutablePath = options.BrowserExecutable,
            };
            if (options.BrowserProfile is not null)
            {
                context = await playwright.Chromium.LaunchPersistentContextAsync(
                    options.BrowserProfile,
                    new BrowserTypeLaunchPersistentContextOptions
                    {
                        Headless = launchOptions.Headless,
                        ExecutablePath = launchOptions.ExecutablePath,
                        UserAgent = options.BrowserUserAgent,
                        Locale = "zh-CN",
                        TimezoneId = "Asia/Shanghai",
                    }).ConfigureAwait(false);
                await context.ApplyStealthAsync(stealthConfig).ConfigureAwait(false);
            }
            else
            {
                var launched = await playwright.Chromium.LaunchStealthAsync(
                    stealthConfig,
                    launchOptions,
                    new BrowserNewContextOptions
                    {
                        ViewportSize = new ViewportSize { Width = 1440, Height = 1000 },
                        UserAgent = options.BrowserUserAgent,
                        Locale = "zh-CN",
                        TimezoneId = "Asia/Shanghai",
                    }).ConfigureAwait(false);
                browser = launched.Browser;
                context = launched.Context;
            }

            if (options.CfClearance is not null)
            {
                await context.AddCookiesAsync([
                    new Cookie
                    {
                        Name = "cf_clearance",
                        Value = options.CfClearance,
                        Domain = options.BaseUri.Host,
                        Path = "/",
                        Secure = options.BaseUri.Scheme == Uri.UriSchemeHttps,
                        HttpOnly = true,
                    },
                ]).ConfigureAwait(false);
            }

            return context;
        }
        finally
        {
            browserGate.Release();
        }
    }

    private async Task WaitForRequestSlotAsync(CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (nextRequestAt > now)
            {
                await Task.Delay(nextRequestAt - now, timeProvider, cancellationToken).ConfigureAwait(false);
            }

            nextRequestAt = timeProvider.GetUtcNow() + options.CrawlDelay;
        }
        finally
        {
            requestGate.Release();
        }
    }

    private async Task<ChartData[]> CaptureLifeIndexViewsAsync(
        IPage page,
        List<string> documents,
        int totalDocumentCharacters,
        ChartData[] initialCharts)
    {
        var activeView = await page.EvaluateAsync<string?>(
            """
            labels => {
              const active = document.querySelector("button[data-testid='stBaseButton-segmented_controlActive']");
              const text = active?.innerText.trim();
              return labels.includes(text) ? text : null;
            }
            """,
            LifeIndexViews).ConfigureAwait(false);
        if (activeView is null)
        {
            throw new MceIndexException(
                MceIndexErrorCode.ExtractionFailed,
                "MCEIndex did not expose the expected life-index view selector.");
        }

        var charts = new List<ChartData>();
        if (activeView == "行业下钻")
        {
            var industry = await GetSelectedIndustryAsync(page).ConfigureAwait(false);
            charts.AddRange(LabelCharts($"行业下钻 / {industry}", initialCharts));
            totalDocumentCharacters = await CaptureOtherIndustriesAsync(
                page,
                documents,
                totalDocumentCharacters,
                charts,
                industry).ConfigureAwait(false);
        }
        else
        {
            charts.AddRange(LabelCharts(activeView, initialCharts));
        }
        foreach (var view in LifeIndexViews)
        {
            if (view == activeView)
            {
                continue;
            }

            var control = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = view,
                Exact = true,
            });
            if (await control.CountAsync().ConfigureAwait(false) != 1)
            {
                throw new MceIndexException(
                    MceIndexErrorCode.ExtractionFailed,
                    $"MCEIndex life-index view “{view}” was not available.");
            }

            await control.ClickAsync(new LocatorClickOptions
            {
                Timeout = (float)options.RequestTimeout.TotalMilliseconds,
            }).ConfigureAwait(false);
            await page.WaitForFunctionAsync(
                """
                view => Array.from(
                  document.querySelectorAll("button[data-testid='stBaseButton-segmented_controlActive']")
                ).some(button => button.innerText.trim() === view)
                """,
                view,
                new PageWaitForFunctionOptions
                {
                    Timeout = (float)options.RequestTimeout.TotalMilliseconds,
                }).ConfigureAwait(false);
            await WaitForDomQuietAsync(page).ConfigureAwait(false);
            await AssertNoChallengeAsync(page).ConfigureAwait(false);

            var document = await page.ContentAsync().ConfigureAwait(false);
            totalDocumentCharacters = MceIndexParser.IncludeDocument(document, totalDocumentCharacters);
            documents.Add(document);
            var viewCharts = await ExtractChartsAsync(page).ConfigureAwait(false);
            if (view == "行业下钻")
            {
                var industry = await GetSelectedIndustryAsync(page).ConfigureAwait(false);
                charts.AddRange(LabelCharts($"行业下钻 / {industry}", viewCharts));
                totalDocumentCharacters = await CaptureOtherIndustriesAsync(
                    page,
                    documents,
                    totalDocumentCharacters,
                    charts,
                    industry).ConfigureAwait(false);
            }
            else
            {
                charts.AddRange(LabelCharts(view, viewCharts));
            }
        }

        var totalPoints = charts.Sum(chart => chart.Series.Sum(series => series.Points.Length));
        if (charts.Count > MaxCharts || totalPoints > MaxTotalChartPoints)
        {
            throw new MceIndexException(
                MceIndexErrorCode.ExtractionFailed,
                "MCEIndex chart data exceeded safe extraction limits.");
        }
        return [.. charts];
    }

    private async Task<bool> SelectAllHistoryAsync(IPage page)
    {
        var changed = false;
        var control = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "All",
            Exact = true,
        });
        var count = await control.CountAsync().ConfigureAwait(false);
        for (var index = 0; index < count; index++)
        {
            var item = control.Nth(index);
            if (!await item.IsVisibleAsync().ConfigureAwait(false) ||
                await item.GetAttributeAsync("data-testid").ConfigureAwait(false) ==
                "stBaseButton-segmented_controlActive")
            {
                continue;
            }

            changed = true;
            await item.ClickAsync(new LocatorClickOptions
            {
                Timeout = (float)options.RequestTimeout.TotalMilliseconds,
            }).ConfigureAwait(false);
            await page.WaitForFunctionAsync(
                """
                index => Array.from(document.querySelectorAll("button"))
                  .filter(button => button.innerText.trim() === "All")[index]
                  ?.getAttribute("data-testid") === "stBaseButton-segmented_controlActive"
                """,
                index,
                new PageWaitForFunctionOptions
                {
                    Timeout = (float)options.RequestTimeout.TotalMilliseconds,
                }).ConfigureAwait(false);
            await WaitForDomQuietAsync(page).ConfigureAwait(false);
            await AssertNoChallengeAsync(page).ConfigureAwait(false);
        }

        var inactiveVisibleControls = await page.EvaluateAsync<int>(
            """
            () => Array.from(document.querySelectorAll("button"))
              .filter(button =>
                button.innerText.trim() === "All" &&
                button.getClientRects().length > 0 &&
                button.getAttribute("data-testid") !== "stBaseButton-segmented_controlActive"
              ).length
            """).ConfigureAwait(false);
        if (inactiveVisibleControls > 0)
        {
            throw new MceIndexException(
                MceIndexErrorCode.ExtractionFailed,
                "MCEIndex did not activate every visible All-history control.");
        }
        return changed;
    }

    private async Task<int> CaptureOtherIndustriesAsync(
        IPage page,
        List<string> documents,
        int totalDocumentCharacters,
        List<ChartData> charts,
        string selectedIndustry)
    {
        foreach (var industry in LifeIndexIndustries)
        {
            if (industry == selectedIndustry)
            {
                continue;
            }

            var selectbox = page.Locator("[data-testid='stSelectbox'] [role='combobox']");
            if (await selectbox.CountAsync().ConfigureAwait(false) != 1)
            {
                throw new MceIndexException(
                    MceIndexErrorCode.ExtractionFailed,
                    "MCEIndex did not expose the expected industry selector.");
            }
            await selectbox.ClickAsync(new LocatorClickOptions
            {
                Timeout = (float)options.RequestTimeout.TotalMilliseconds,
            }).ConfigureAwait(false);

            var option = page.GetByRole(AriaRole.Option, new PageGetByRoleOptions
            {
                Name = industry,
                Exact = true,
            });
            if (await option.CountAsync().ConfigureAwait(false) != 1)
            {
                throw new MceIndexException(
                    MceIndexErrorCode.ExtractionFailed,
                    $"MCEIndex industry “{industry}” was not available.");
            }
            await option.ClickAsync(new LocatorClickOptions
            {
                Timeout = (float)options.RequestTimeout.TotalMilliseconds,
            }).ConfigureAwait(false);
            await page.WaitForFunctionAsync(
                """
                industry => document.querySelector("[data-testid='stSelectbox'] [value]")
                  ?.getAttribute("value") === industry
                """,
                industry,
                new PageWaitForFunctionOptions
                {
                    Timeout = (float)options.RequestTimeout.TotalMilliseconds,
                }).ConfigureAwait(false);
            await WaitForDomQuietAsync(page).ConfigureAwait(false);
            await AssertNoChallengeAsync(page).ConfigureAwait(false);

            var document = await page.ContentAsync().ConfigureAwait(false);
            totalDocumentCharacters = MceIndexParser.IncludeDocument(document, totalDocumentCharacters);
            documents.Add(document);
            charts.AddRange(LabelCharts(
                $"行业下钻 / {industry}",
                await ExtractChartsAsync(page).ConfigureAwait(false)));
        }
        return totalDocumentCharacters;
    }

    private static async Task<string> GetSelectedIndustryAsync(IPage page)
    {
        var industry = await page.EvaluateAsync<string?>(
            """
            () => document.querySelector("[data-testid='stSelectbox'] [value]")

              ?.getAttribute("value")
            """).ConfigureAwait(false);
        if (industry is null || !LifeIndexIndustries.Contains(industry, StringComparer.Ordinal))
        {
            throw new MceIndexException(
                MceIndexErrorCode.ExtractionFailed,
                "MCEIndex did not expose a supported selected industry.");
        }
        return industry;
    }
    private static ChartData[] MergeCharts(ChartData[] initial, ChartData[] allHistory)
    {
        var merged = initial.ToList();
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < merged.Count; index++)
        {
            indices[ChartKey(merged[index])] = index;
        }

        foreach (var chart in allHistory)
        {
            var key = ChartKey(chart);
            if (!indices.TryGetValue(key, out var index))
            {
                indices[key] = merged.Count;
                merged.Add(chart);
                continue;
            }

            var existingPoints = merged[index].Series.Sum(series => series.Points.Length);
            var candidatePoints = chart.Series.Sum(series => series.Points.Length);
            if (candidatePoints > existingPoints)
            {
                merged[index] = chart;
            }
        }

        var totalPoints = merged.Sum(chart => chart.Series.Sum(series => series.Points.Length));
        if (merged.Count > MaxCharts || totalPoints > MaxTotalChartPoints)
        {
            throw new MceIndexException(
                MceIndexErrorCode.ExtractionFailed,
                "MCEIndex chart data exceeded safe extraction limits.");
        }
        return [.. merged];
    }

    private static string ChartKey(ChartData chart) =>
        string.Join('\u001F',
            chart.Title,
            string.Join('\u001E', chart.Series.Select(series => $"{series.Name}\u001D{series.Type}")));

    private async Task WaitForExpectedInitialChartsAsync(IPage page, Uri target)
    {
        if (!IsCanonicalMceIndex(target) ||
            !ProductionChartMinimums.TryGetValue(target.AbsolutePath.TrimEnd('/'), out var minimum))
        {
            return;
        }

        await page.WaitForFunctionAsync(
            """
            expected => document.querySelectorAll(
              "[data-testid='stPlotlyChart'] .js-plotly-plot"
            ).length >= expected
            """,
            minimum.Initial,
            new PageWaitForFunctionOptions
            {
                Timeout = (float)options.RequestTimeout.TotalMilliseconds,
            }).ConfigureAwait(false);
    }

    internal static void ValidateProductionChartCoverage(Uri source, ChartData[] charts)
    {
        if (!IsCanonicalMceIndex(source) ||
            !ProductionChartMinimums.TryGetValue(source.AbsolutePath.TrimEnd('/'), out var minimum))
        {
            return;
        }

        var points = charts.Sum(chart => chart.Series.Sum(series => series.Points.Length));
        if (charts.Length >= minimum.Captured && points >= minimum.Points)
        {
            return;
        }

        throw new MceIndexException(
            MceIndexErrorCode.ExtractionFailed,
            $"MCEIndex returned incomplete chart coverage for {source.AbsolutePath}: " +
            $"expected at least {minimum.Captured} charts and {minimum.Points} points, " +
            $"received {charts.Length} charts and {points} points.");
    }

    private static bool IsCanonicalMceIndex(Uri source) =>
        source.Host.Equals("mceindex.com", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<ChartData> LabelCharts(string view, ChartData[] charts) =>
        charts.Select((chart, index) =>
        {
            var genericTitle = string.IsNullOrWhiteSpace(chart.Title) ||
                chart.Title.StartsWith('<') ||
                chart.Title.StartsWith("图表 ", StringComparison.Ordinal);
            var title = genericTitle ? $"图表 {index + 1}" : chart.Title;
            return chart with
            {
                Title = $"{view} · {title}",
                Description = genericTitle
                    ? $"MCEIndex“{view}”视图中的图表。"
                    : chart.Description,
            };
        });

    private async Task<ChartData[]> ExtractChartsAsync(IPage page)
    {
        const string selector = "[data-testid='stPlotlyChart'] .js-plotly-plot";
        var plot = page.Locator(selector);
        var plotCount = await plot.CountAsync().ConfigureAwait(false);
        if (plotCount > MaxCharts)
        {
            throw new MceIndexException(
                MceIndexErrorCode.ExtractionFailed,
                "MCEIndex chart data exceeded safe extraction limits.");
        }
        for (var index = 0; index < plotCount; index++)
        {
            await plot.Nth(index).ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions
            {
                Timeout = (float)options.RequestTimeout.TotalMilliseconds,
            }).ConfigureAwait(false);
            await page.WaitForFunctionAsync(
                """
                ({ selector, index }) => {
                  const plot = document.querySelectorAll(selector)[index];
                  const data = plot?._fullData?.length ? plot._fullData : plot?.data;
                  return Array.isArray(data) && data.length > 0;
                }
                """,
                new { selector, index },
                new PageWaitForFunctionOptions
                {
                    Timeout = (float)options.RequestTimeout.TotalMilliseconds,
                }).ConfigureAwait(false);
        }

        var json = await page.EvaluateAsync<string>(
            """
            () => {
              const MAX_CHARTS = 32;
              const MAX_SERIES_PER_CHART = 32;
              const MAX_POINTS_PER_SERIES = 10000;
              const MAX_TOTAL_POINTS = 100000;
              const MAX_BINARY_CHARACTERS = 1000000;
              let extractionLimitExceeded = false;
              let totalPoints = 0;
              const valuesOf = (input) => {
                if (input == null) return [];
                if (Array.isArray(input) || ArrayBuffer.isView(input)) {
                  if (input.length > MAX_POINTS_PER_SERIES) extractionLimitExceeded = true;
                  const length = Math.min(input.length, MAX_POINTS_PER_SERIES);
                  return Array.from({ length }, (_, index) => input[index]);
                }
                if (typeof input !== "object" || typeof input.bdata !== "string") return [input];
                if (input.bdata.length > MAX_BINARY_CHARACTERS) {
                  extractionLimitExceeded = true;
                  return [];
                }

                const binary = atob(input.bdata);
                const bytes = Uint8Array.from(binary, char => char.charCodeAt(0));
                const dtype = String(input.dtype || "f8").replace(/[<>=|]/g, "");
                const constructors = {
                  f4: Float32Array, f8: Float64Array,
                  i1: Int8Array, i2: Int16Array, i4: Int32Array,
                  u1: Uint8Array, u2: Uint16Array, u4: Uint32Array,
                };
                const TypedArray = constructors[dtype];
                if (!TypedArray) return [];
                const values = new TypedArray(bytes.buffer);
                if (values.length > MAX_POINTS_PER_SERIES) extractionLimitExceeded = true;
                return Array.from(values.subarray(0, MAX_POINTS_PER_SERIES));
              };
              const textOf = (value) => value == null ? null : String(value);
              const numberOf = (value) => {
                if (typeof value === "number") return Number.isFinite(value) ? value : null;
                if (typeof value !== "string" || value.trim() === "") return null;
                const parsed = Number(value);
                return Number.isFinite(parsed) ? parsed : null;
              };
              const headerFor = (plot) => {
                let cursor = plot.closest("[data-testid='stElementContainer']");
                for (let index = 0; cursor && index < 8; index += 1, cursor = cursor.previousElementSibling) {
                  const header = cursor.querySelector?.(".chart-header");
                  if (header) return header;
                }
                return null;
              };

              const plots = Array.from(document.querySelectorAll("[data-testid='stPlotlyChart'] .js-plotly-plot"));
              if (plots.length > MAX_CHARTS) return "__MCEINDEX_RESOURCE_LIMIT__";
              const charts = plots.map((plot, chartIndex) => {
                  const header = headerFor(plot);
                  const layout = plot._fullLayout || plot.layout || {};
                  const title = header?.querySelector("h1,h2,h3,h4,h5,h6")?.innerText
                    || layout.title?.text
                    || `图表 ${chartIndex + 1}`;
                  const description = header?.querySelector(".chart-header-summary")?.innerText
                    || `MCEIndex 页面中的“${title}”图表。`;
                  const notes = Array.from(header?.querySelectorAll("p:not(.chart-header-summary)") || [])
                    .map(element => element.innerText.trim())
                    .filter(Boolean);
                  const traces = Array.from(plot._fullData?.length ? plot._fullData : (plot.data || []));
                  if (traces.length > MAX_SERIES_PER_CHART) extractionLimitExceeded = true;
                  const series = traces.slice(0, MAX_SERIES_PER_CHART).map(trace => {
                    const x = valuesOf(trace.x);
                    const y = valuesOf(trace.y);
                    const labels = valuesOf(trace.labels);
                    const values = valuesOf(trace.values);
                    const texts = valuesOf(trace.text);
                    const tickValues = valuesOf(layout.yaxis?.tickvals);
                    const tickLabels = valuesOf(layout.yaxis?.ticktext);
                    const yCategories = tickValues.length === tickLabels.length && tickLabels.length > 0
                      ? y.map(value => {
                          const index = tickValues.findIndex(tick =>
                            numberOf(tick) != null && numberOf(value) != null
                              ? numberOf(tick) === numberOf(value)
                              : String(tick) === String(value));
                          return index >= 0 ? tickLabels[index] : value;
                        })
                      : y;
                    const horizontal = trace.orientation === "h";
                    const categories = horizontal
                      ? yCategories
                      : (tickLabels.length > 0 && x.length > 0 ? yCategories : (x.length > 0 ? x : labels));
                    const numericValues = horizontal
                      ? x
                      : (tickLabels.length > 0 && x.length > 0 ? x : (y.length > 0 ? y : values));
                    const count = Math.max(categories.length, numericValues.length, texts.length);
                    if (count > MAX_POINTS_PER_SERIES || totalPoints > MAX_TOTAL_POINTS - count) {
                      extractionLimitExceeded = true;
                      return { name: textOf(trace.name), type: textOf(trace.type), points: [] };
                    }
                    totalPoints += count;
                    const points = Array.from({ length: count }, (_, pointIndex) => ({
                      category: textOf(categories[pointIndex]),
                      value: numberOf(numericValues[pointIndex]),
                      text: textOf(texts[pointIndex]),
                    }));
                    return {
                      name: textOf(trace.name),
                      type: textOf(trace.type),
                      points,
                    };
                  }).filter(item => item.points.length > 0);
                  return {
                    title: String(title).trim(),
                    description: String(description).trim(),
                    notes,
                    xAxisTitle: textOf(layout.xaxis?.title?.text),
                    yAxisTitle: textOf(layout.yaxis?.title?.text),
                    series,
                  };
                }).filter(chart => chart.series.length > 0);
              if (extractionLimitExceeded) return "__MCEINDEX_RESOURCE_LIMIT__";
              return JSON.stringify(charts);
            }
            """).ConfigureAwait(false);
        if (json == "\"__MCEINDEX_RESOURCE_LIMIT__\"" || json == "__MCEINDEX_RESOURCE_LIMIT__")
        {
            throw new MceIndexException(
                MceIndexErrorCode.ExtractionFailed,
                "MCEIndex chart data exceeded safe extraction limits.");
        }


        return JsonSerializer.Deserialize(json, MceJsonContext.Default.ChartDataArray) ?? [];
    }

    private static async Task AssertNoChallengeAsync(IPage page)
    {
        var html = await page.ContentAsync().ConfigureAwait(false);
        if (MceIndexParser.IsAccessChallenge(html))
        {
            throw new MceIndexException(MceIndexErrorCode.AccessChallenge,
                "Cloudflare verification blocked MCEIndex acquisition.");
        }
    }

    private async Task WaitForDomQuietAsync(IPage page)
    {
        var quietMilliseconds = (int)options.DomQuietPeriod.TotalMilliseconds;
        var maximumMilliseconds = (int)options.RequestTimeout.TotalMilliseconds;
        await page.EvaluateAsync(
            """
            ({ quietMilliseconds, maximumMilliseconds }) => new Promise((resolve) => {
              const root = document.querySelector("[data-testid='stMain'], main");
              if (!root) { resolve(false); return; }
              let quietTimer;
              let maximumTimer;
              const finish = (value) => {
                clearTimeout(quietTimer);
                clearTimeout(maximumTimer);
                observer.disconnect();
                resolve(value);
              };
              const arm = () => {
                clearTimeout(quietTimer);
                quietTimer = setTimeout(() => finish(true), quietMilliseconds);
              };
              const observer = new MutationObserver(arm);
              observer.observe(root, { subtree: true, childList: true, attributes: true, characterData: true });
              maximumTimer = setTimeout(() => finish(false), maximumMilliseconds);
              arm();
            })
            """,
            new { quietMilliseconds, maximumMilliseconds }).ConfigureAwait(false);
    }
    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping inaccessible iframe on {Url}")]
    private static partial void LogInaccessibleFrame(ILogger logger, Exception error, Uri url);

}
