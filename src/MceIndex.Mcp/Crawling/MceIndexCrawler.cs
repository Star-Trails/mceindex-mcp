using System.Text.Json;
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

public sealed class MceIndexCrawler(
    MceIndexOptions options,
    MceIndexParser parser,
    TimeProvider timeProvider,
    CamofoxClient camofox) : IMceIndexCrawler
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
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private DateTimeOffset nextRequestAt;
    private bool disposed;

    public async Task<CrawledPage> CrawlAsync(Uri target, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await WaitForRequestSlotAsync(cancellationToken).ConfigureAwait(false);
        var tab = await camofox.OpenTabAsync(target, cancellationToken).ConfigureAwait(false);
        try
        {
            await WaitForReadyAsync(tab, target, cancellationToken).ConfigureAwait(false);
            await AssertNoChallengeAsync(tab, cancellationToken).ConfigureAwait(false);

            var documents = new List<string>(MceIndexParser.MaxHtmlDocuments);
            var totalDocumentCharacters = await CaptureDocumentsAsync(
                tab, documents, 0, cancellationToken).ConfigureAwait(false);
            var charts = await ExtractChartsAsync(tab, cancellationToken).ConfigureAwait(false);
            if (await SelectAllHistoryAsync(tab, cancellationToken).ConfigureAwait(false))
            {
                totalDocumentCharacters = await CaptureDocumentsAsync(
                    tab, documents, totalDocumentCharacters, cancellationToken).ConfigureAwait(false);
                charts = MergeCharts(charts, await ExtractChartsAsync(tab, cancellationToken).ConfigureAwait(false));
            }

            var source = await GetSourceAsync(tab, target, cancellationToken).ConfigureAwait(false);
            if (source.AbsolutePath.TrimEnd('/').EndsWith("/LI_Monthly", StringComparison.OrdinalIgnoreCase))
            {
                charts = await CaptureLifeIndexViewsAsync(
                    tab,
                    documents,
                    totalDocumentCharacters,
                    charts,
                    cancellationToken).ConfigureAwait(false);
            }
            ValidateProductionChartCoverage(source, charts);

            var snapshot = parser.Extract(documents, source, timeProvider.GetUtcNow()) with { Charts = charts };
            if (snapshot.Headings.Length == 0 ||
                (snapshot.Metrics.Length == 0 && snapshot.Tables.Length == 0 &&
                 snapshot.Cards.Length == 0 && snapshot.Charts.Length == 0 && snapshot.Text.Length == 0))
            {
                throw new MceIndexException(
                    MceIndexErrorCode.ExtractionFailed,
                    $"MCEIndex returned no complete page content for {source}.");
            }
            return new CrawledPage(snapshot, [.. documents]);
        }
        finally
        {
            await camofox.CloseTabAsync(tab).ConfigureAwait(false);
        }
    }

    public Task CloseBrowserAsync() => camofox.CloseBrowserAsync();

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        await camofox.CloseBrowserAsync().ConfigureAwait(false);
        requestGate.Dispose();
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

    private async Task WaitForReadyAsync(CamofoxTab tab, Uri target, CancellationToken cancellationToken)
    {
        var expectedCharts = IsCanonicalMceIndex(target) &&
            ProductionChartMinimums.TryGetValue(target.AbsolutePath.TrimEnd('/'), out var minimum)
                ? minimum.Initial
                : 0;
        var expression = $$"""
            (async () => {
              const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
              const deadline = Date.now() + {{Milliseconds(options.RequestTimeout)}};
              while (Date.now() < deadline) {
                const main = document.querySelector("[data-testid='stMain'], main");
                const heading = main?.querySelector("h1")?.innerText.trim();
                const charts = document.querySelectorAll("[data-testid='stPlotlyChart'] .js-plotly-plot").length;
                if (heading && charts >= {{expectedCharts}}) return { ready: true, html: document.documentElement.outerHTML };
                await sleep(200);
              }
              return { ready: false, html: document.documentElement.outerHTML };
            })()
            """;
        var result = await camofox.EvaluateAsync(tab, expression, cancellationToken).ConfigureAwait(false);
        var html = result.GetProperty("html").GetString() ?? string.Empty;
        if (MceIndexParser.IsAccessChallenge(html))
        {
            throw new MceIndexException(
                MceIndexErrorCode.AccessChallenge,
                "Cloudflare verification blocked MCEIndex acquisition.");
        }
        if (!result.GetProperty("ready").GetBoolean())
        {
            throw new MceIndexException(
                MceIndexErrorCode.LoadTimeout,
                $"MCEIndex did not become ready within {options.RequestTimeout.TotalMilliseconds:F0}ms.");
        }
        await WaitForDomQuietAsync(tab, cancellationToken).ConfigureAwait(false);
        await ScrollLazyContentAsync(tab, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> CaptureDocumentsAsync(
        CamofoxTab tab,
        List<string> documents,
        int totalDocumentCharacters,
        CancellationToken cancellationToken)
    {
        var remainingDocuments = MceIndexParser.MaxHtmlDocuments - documents.Count;
        var remainingCharacters = MceIndexParser.MaxTotalHtmlCharacters - totalDocumentCharacters;
        if (remainingDocuments <= 0 || remainingCharacters <= 0)
        {
            throw new MceIndexException(MceIndexErrorCode.ExtractionFailed, "MCEIndex HTML exceeded safe extraction limits.");
        }
        var expression = $$"""
            (() => {
              const documents = [document.documentElement.outerHTML];
              for (const frame of document.querySelectorAll("iframe")) {
                try {
                  const html = frame.contentDocument?.documentElement?.outerHTML;
                  if (html) documents.push(html);
                } catch {}
              }
              if (documents.length > {{remainingDocuments}} || documents.reduce((sum, value) => sum + value.length, 0) > {{remainingCharacters}}) {
                return { limitExceeded: true, documents: [] };
              }
              return { limitExceeded: false, documents };
            })()
            """;
        var result = await camofox.EvaluateAsync(tab, expression, cancellationToken).ConfigureAwait(false);
        if (result.GetProperty("limitExceeded").GetBoolean())
        {
            throw new MceIndexException(MceIndexErrorCode.ExtractionFailed, "MCEIndex HTML exceeded safe extraction limits.");
        }
        foreach (var document in result.GetProperty("documents").EnumerateArray())
        {
            var html = document.GetString() ?? string.Empty;
            totalDocumentCharacters = MceIndexParser.IncludeDocument(html, totalDocumentCharacters);
            documents.Add(html);
        }
        if (documents.Any(MceIndexParser.IsAccessChallenge))
        {
            throw new MceIndexException(MceIndexErrorCode.AccessChallenge, "Cloudflare verification blocked MCEIndex acquisition.");
        }
        return totalDocumentCharacters;
    }

    private async Task<ChartData[]> CaptureLifeIndexViewsAsync(
        CamofoxTab tab,
        List<string> documents,
        int totalDocumentCharacters,
        ChartData[] initialCharts,
        CancellationToken cancellationToken)
    {
        var activeView = await GetActiveViewAsync(tab, cancellationToken).ConfigureAwait(false);
        var charts = new List<ChartData>();
        if (activeView == "行业下钻")
        {
            var industry = await GetSelectedIndustryAsync(tab, cancellationToken).ConfigureAwait(false);
            charts.AddRange(LabelCharts($"行业下钻 / {industry}", initialCharts));
            totalDocumentCharacters = await CaptureOtherIndustriesAsync(
                tab, documents, totalDocumentCharacters, charts, industry, cancellationToken).ConfigureAwait(false);
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
            await SelectViewAsync(tab, view, cancellationToken).ConfigureAwait(false);
            await AssertNoChallengeAsync(tab, cancellationToken).ConfigureAwait(false);
            totalDocumentCharacters = await CaptureDocumentsAsync(
                tab, documents, totalDocumentCharacters, cancellationToken).ConfigureAwait(false);
            var viewCharts = await ExtractChartsAsync(tab, cancellationToken).ConfigureAwait(false);
            if (view == "行业下钻")
            {
                var industry = await GetSelectedIndustryAsync(tab, cancellationToken).ConfigureAwait(false);
                charts.AddRange(LabelCharts($"行业下钻 / {industry}", viewCharts));
                totalDocumentCharacters = await CaptureOtherIndustriesAsync(
                    tab, documents, totalDocumentCharacters, charts, industry, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                charts.AddRange(LabelCharts(view, viewCharts));
            }
        }

        ValidateResourceLimits(charts);
        return [.. charts];
    }

    private async Task<int> CaptureOtherIndustriesAsync(
        CamofoxTab tab,
        List<string> documents,
        int totalDocumentCharacters,
        List<ChartData> charts,
        string selectedIndustry,
        CancellationToken cancellationToken)
    {
        foreach (var industry in LifeIndexIndustries)
        {
            if (industry == selectedIndustry)
            {
                continue;
            }
            await SelectIndustryAsync(tab, industry, cancellationToken).ConfigureAwait(false);
            totalDocumentCharacters = await CaptureDocumentsAsync(
                tab, documents, totalDocumentCharacters, cancellationToken).ConfigureAwait(false);
            charts.AddRange(LabelCharts(
                $"行业下钻 / {industry}",
                await ExtractChartsAsync(tab, cancellationToken).ConfigureAwait(false)));
        }
        return totalDocumentCharacters;
    }

    private async Task<string> GetActiveViewAsync(CamofoxTab tab, CancellationToken cancellationToken)
    {
        var result = await camofox.EvaluateAsync(
            tab,
            "(() => [...document.querySelectorAll(\"button[data-testid='stBaseButton-segmented_controlActive']\")].map(button => button.innerText.trim()).find(text => " +
            JsonSerializer.Serialize(LifeIndexViews) + ".includes(text)) || null)()",
            cancellationToken).ConfigureAwait(false);
        var view = result.GetString();
        if (view is null)
        {
            throw new MceIndexException(MceIndexErrorCode.ExtractionFailed, "MCEIndex did not expose the expected life-index view selector.");
        }
        return view;
    }

    private async Task SelectViewAsync(CamofoxTab tab, string view, CancellationToken cancellationToken)
    {
        var expression = $$"""
            (async () => {
              const target = {{JsonSerializer.Serialize(view)}};
              const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
              const button = [...document.querySelectorAll("button")].find(candidate => candidate.innerText.trim() === target && candidate.getClientRects().length > 0);
              if (!button) return false;
              if (button.getAttribute("data-testid") !== "stBaseButton-segmented_controlActive") button.click();
              const deadline = Date.now() + {{Milliseconds(options.RequestTimeout)}};
              while (Date.now() < deadline) {
                const active = [...document.querySelectorAll("button[data-testid='stBaseButton-segmented_controlActive']")]
                  .some(candidate => candidate.innerText.trim() === target);
                if (active) { await sleep({{Milliseconds(options.DomQuietPeriod)}}); return true; }
                await sleep(100);
              }
              return false;
            })()
            """;
        var result = await camofox.EvaluateAsync(tab, expression, cancellationToken).ConfigureAwait(false);
        if (!result.GetBoolean())
        {
            throw new MceIndexException(MceIndexErrorCode.ExtractionFailed, $"MCEIndex life-index view “{view}” was not available.");
        }
        await ScrollLazyContentAsync(tab, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetSelectedIndustryAsync(CamofoxTab tab, CancellationToken cancellationToken)
    {
        var result = await camofox.EvaluateAsync(
            tab,
            "(() => document.querySelector(\"[data-testid='stSelectbox'] [value]\")?.getAttribute('value') || null)()",
            cancellationToken).ConfigureAwait(false);
        var industry = result.GetString();
        if (industry is null || !LifeIndexIndustries.Contains(industry, StringComparer.Ordinal))
        {
            throw new MceIndexException(MceIndexErrorCode.ExtractionFailed, "MCEIndex did not expose a supported selected industry.");
        }
        return industry;
    }

    private async Task SelectIndustryAsync(CamofoxTab tab, string industry, CancellationToken cancellationToken)
    {
        var expression = $$"""
            (async () => {
              const target = {{JsonSerializer.Serialize(industry)}};
              const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
              const select = document.querySelector("[data-testid='stSelectbox'] [role='combobox']");
              if (!select) return false;
              select.click();
              const optionDeadline = Date.now() + {{Milliseconds(options.RequestTimeout)}};
              let option;
              while (Date.now() < optionDeadline && !option) {
                option = [...document.querySelectorAll("[role='option']")].find(candidate => candidate.innerText.trim() === target);
                if (!option) await sleep(100);
              }
              if (!option) return false;
              option.click();
              while (Date.now() < optionDeadline) {
                if (document.querySelector("[data-testid='stSelectbox'] [value]")?.getAttribute("value") === target) {
                  await sleep({{Milliseconds(options.DomQuietPeriod)}});
                  return true;
                }
                await sleep(100);
              }
              return false;
            })()
            """;
        var result = await camofox.EvaluateAsync(tab, expression, cancellationToken).ConfigureAwait(false);
        if (!result.GetBoolean())
        {
            throw new MceIndexException(MceIndexErrorCode.ExtractionFailed, $"MCEIndex industry “{industry}” was not available.");
        }
        await ScrollLazyContentAsync(tab, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SelectAllHistoryAsync(CamofoxTab tab, CancellationToken cancellationToken)
    {
        var expression = $$"""
            (async () => {
              const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
              const deadline = Date.now() + {{Milliseconds(options.RequestTimeout)}};
              let changed = false;
              while (Date.now() < deadline) {
                const control = [...document.querySelectorAll("button")].find(button =>
                  button.innerText.trim() === "All" && button.getClientRects().length > 0 &&
                  button.getAttribute("data-testid") !== "stBaseButton-segmented_controlActive");
                if (!control) break;
                changed = true;
                control.click();
                await sleep({{Milliseconds(options.DomQuietPeriod)}});
              }
              const inactive = [...document.querySelectorAll("button")].some(button =>
                button.innerText.trim() === "All" && button.getClientRects().length > 0 &&
                button.getAttribute("data-testid") !== "stBaseButton-segmented_controlActive");
              if (inactive) return { changed, complete: false };
              await sleep({{Milliseconds(options.DomQuietPeriod)}});
              return { changed, complete: true };
            })()
            """;
        var result = await camofox.EvaluateAsync(tab, expression, cancellationToken).ConfigureAwait(false);
        if (!result.GetProperty("complete").GetBoolean())
        {
            throw new MceIndexException(MceIndexErrorCode.ExtractionFailed, "MCEIndex did not activate every visible All-history control.");
        }
        if (result.GetProperty("changed").GetBoolean())
        {
            await WaitForChartsStableAsync(tab, cancellationToken).ConfigureAwait(false);
        }
        return result.GetProperty("changed").GetBoolean();
    }

    private async Task ScrollLazyContentAsync(CamofoxTab tab, CancellationToken cancellationToken)
    {
        var expression = $$"""
            (async () => {
              const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
              for (let offset = 0; offset < document.documentElement.scrollHeight; offset += 700) {
                window.scrollTo(0, offset);
                await sleep(80);
              }
              window.scrollTo(0, 0);
              await sleep({{Milliseconds(options.DomQuietPeriod)}});
              return true;
            })()
            """;
        await camofox.EvaluateAsync(tab, expression, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForChartsStableAsync(CamofoxTab tab, CancellationToken cancellationToken)
    {
        var expression = $$"""
            (async () => {
              const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
              const signature = () => [...document.querySelectorAll("[data-testid='stPlotlyChart'] .js-plotly-plot")].map(plot =>
                (plot._fullData?.length ? plot._fullData : (plot.data || [])).map(trace =>
                  Math.max(trace.x?.length || 0, trace.y?.length || 0, trace.labels?.length || 0, trace.values?.length || 0)).join(",")).join(";");
              const deadline = Date.now() + {{Milliseconds(options.RequestTimeout)}};
              let previous = signature();
              let unchangedSince = Date.now();
              while (Date.now() < deadline) {
                await sleep(200);
                const current = signature();
                if (current !== previous) { previous = current; unchangedSince = Date.now(); }
                if (current && Date.now() - unchangedSince >= {{Milliseconds(options.DomQuietPeriod)}}) return true;
              }
              return false;
            })()
            """;
        var stable = await camofox.EvaluateAsync(tab, expression, cancellationToken).ConfigureAwait(false);
        if (!stable.GetBoolean())
        {
            throw new MceIndexException(MceIndexErrorCode.LoadTimeout, "MCEIndex charts did not stabilize before extraction.");
        }
    }

    private async Task WaitForDomQuietAsync(CamofoxTab tab, CancellationToken cancellationToken)
    {
        var expression = $$"""
            (() => new Promise(resolve => {
              const root = document.querySelector("[data-testid='stMain'], main");
              if (!root) { resolve(false); return; }
              let quietTimer;
              let maximumTimer;
              const finish = value => { clearTimeout(quietTimer); clearTimeout(maximumTimer); observer.disconnect(); resolve(value); };
              const arm = () => { clearTimeout(quietTimer); quietTimer = setTimeout(() => finish(true), {{Milliseconds(options.DomQuietPeriod)}}); };
              const observer = new MutationObserver(arm);
              observer.observe(root, { subtree: true, childList: true, attributes: true, characterData: true });
              maximumTimer = setTimeout(() => finish(false), {{Milliseconds(options.RequestTimeout)}});
              arm();
            }))()
            """;
        await camofox.EvaluateAsync(tab, expression, cancellationToken).ConfigureAwait(false);
    }

    private async Task AssertNoChallengeAsync(CamofoxTab tab, CancellationToken cancellationToken)
    {
        var result = await camofox.EvaluateAsync(
            tab,
            "(() => document.documentElement.outerHTML)()",
            cancellationToken).ConfigureAwait(false);
        if (MceIndexParser.IsAccessChallenge(result.GetString() ?? string.Empty))
        {
            throw new MceIndexException(MceIndexErrorCode.AccessChallenge, "Cloudflare verification blocked MCEIndex acquisition.");
        }
    }

    private async Task<Uri> GetSourceAsync(CamofoxTab tab, Uri fallback, CancellationToken cancellationToken)
    {
        var result = await camofox.EvaluateAsync(tab, "(() => location.href)()", cancellationToken).ConfigureAwait(false);
        return Uri.TryCreate(result.GetString(), UriKind.Absolute, out var source) ? source : fallback;
    }

    private async Task<ChartData[]> ExtractChartsAsync(CamofoxTab tab, CancellationToken cancellationToken)
    {
        var result = await camofox.EvaluateAsync(tab, ChartExtractionExpression, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind == JsonValueKind.String && result.GetString() == "__MCEINDEX_RESOURCE_LIMIT__")
        {
            throw new MceIndexException(MceIndexErrorCode.ExtractionFailed, "MCEIndex chart data exceeded safe extraction limits.");
        }
        return result.Deserialize(MceJsonContext.Default.ChartDataArray) ?? [];
    }

    private static ChartData[] MergeCharts(ChartData[] initial, ChartData[] allHistory)
    {
        var merged = initial.ToList();
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < merged.Count; index++) indices[ChartKey(merged[index])] = index;
        foreach (var chart in allHistory)
        {
            var key = ChartKey(chart);
            if (!indices.TryGetValue(key, out var index))
            {
                indices[key] = merged.Count;
                merged.Add(chart);
            }
            else if (chart.Series.Sum(series => series.Points.Length) > merged[index].Series.Sum(series => series.Points.Length))
            {
                merged[index] = chart;
            }
        }
        ValidateResourceLimits(merged);
        return [.. merged];
    }

    private static string ChartKey(ChartData chart) =>
        string.Join('\u001F', chart.Title, string.Join('\u001E', chart.Series.Select(series => $"{series.Name}\u001D{series.Type}")));

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
            $"MCEIndex returned incomplete chart coverage for {source.AbsolutePath}: expected at least {minimum.Captured} charts and {minimum.Points} points, received {charts.Length} charts and {points} points.");
    }

    private static bool IsCanonicalMceIndex(Uri source) => source.Host.Equals("mceindex.com", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<ChartData> LabelCharts(string view, ChartData[] charts) =>
        charts.Select((chart, index) =>
        {
            var genericTitle = string.IsNullOrWhiteSpace(chart.Title) || chart.Title.StartsWith('<') || chart.Title.StartsWith("图表 ", StringComparison.Ordinal);
            var title = genericTitle ? $"图表 {index + 1}" : chart.Title;
            return chart with
            {
                Title = $"{view} · {title}",
                Description = genericTitle ? $"MCEIndex“{view}”视图中的图表。" : chart.Description,
            };
        });

    private static void ValidateResourceLimits(IEnumerable<ChartData> charts)
    {
        var materialized = charts as ICollection<ChartData> ?? charts.ToArray();
        if (materialized.Count > MaxCharts || materialized.Sum(chart => chart.Series.Sum(series => series.Points.Length)) > MaxTotalChartPoints)
        {
            throw new MceIndexException(MceIndexErrorCode.ExtractionFailed, "MCEIndex chart data exceeded safe extraction limits.");
        }
    }

    private static int Milliseconds(TimeSpan value) => checked((int)value.TotalMilliseconds);

    private const string ChartExtractionExpression = """
        (() => {
          const MAX_CHARTS = 32, MAX_SERIES_PER_CHART = 32, MAX_POINTS_PER_SERIES = 10000;
          const MAX_TOTAL_POINTS = 100000, MAX_BINARY_CHARACTERS = 1000000;
          let extractionLimitExceeded = false, totalPoints = 0;
          const valuesOf = input => {
            if (input == null) return [];
            if (Array.isArray(input) || ArrayBuffer.isView(input)) {
              if (input.length > MAX_POINTS_PER_SERIES) extractionLimitExceeded = true;
              return Array.from({ length: Math.min(input.length, MAX_POINTS_PER_SERIES) }, (_, index) => input[index]);
            }
            if (typeof input !== "object" || typeof input.bdata !== "string") return [input];
            if (input.bdata.length > MAX_BINARY_CHARACTERS) { extractionLimitExceeded = true; return []; }
            const binary = atob(input.bdata);
            const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
            const dtype = String(input.dtype || "f8").replace(/[<>=|]/g, "");
            const constructors = { f4: Float32Array, f8: Float64Array, i1: Int8Array, i2: Int16Array, i4: Int32Array, u1: Uint8Array, u2: Uint16Array, u4: Uint32Array };
            const TypedArray = constructors[dtype];
            if (!TypedArray) return [];
            const values = new TypedArray(bytes.buffer);
            if (values.length > MAX_POINTS_PER_SERIES) extractionLimitExceeded = true;
            return Array.from(values.subarray(0, MAX_POINTS_PER_SERIES));
          };
          const textOf = value => value == null ? null : String(value);
          const numberOf = value => {
            if (typeof value === "number") return Number.isFinite(value) ? value : null;
            if (typeof value !== "string" || value.trim() === "") return null;
            const parsed = Number(value);
            return Number.isFinite(parsed) ? parsed : null;
          };
          const headerFor = plot => {
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
            const header = headerFor(plot), layout = plot._fullLayout || plot.layout || {};
            const title = header?.querySelector("h1,h2,h3,h4,h5,h6")?.innerText || layout.title?.text || `图表 ${chartIndex + 1}`;
            const description = header?.querySelector(".chart-header-summary")?.innerText || `MCEIndex 页面中的“${title}”图表。`;
            const notes = Array.from(header?.querySelectorAll("p:not(.chart-header-summary)") || []).map(element => element.innerText.trim()).filter(Boolean);
            const traces = Array.from(plot._fullData?.length ? plot._fullData : (plot.data || []));
            if (traces.length > MAX_SERIES_PER_CHART) extractionLimitExceeded = true;
            const series = traces.slice(0, MAX_SERIES_PER_CHART).map(trace => {
              const x = valuesOf(trace.x), y = valuesOf(trace.y), labels = valuesOf(trace.labels), values = valuesOf(trace.values), texts = valuesOf(trace.text);
              const tickValues = valuesOf(layout.yaxis?.tickvals), tickLabels = valuesOf(layout.yaxis?.ticktext);
              const yCategories = tickValues.length === tickLabels.length && tickLabels.length > 0 ? y.map(value => {
                const index = tickValues.findIndex(tick => numberOf(tick) != null && numberOf(value) != null ? numberOf(tick) === numberOf(value) : String(tick) === String(value));
                return index >= 0 ? tickLabels[index] : value;
              }) : y;
              const horizontal = trace.orientation === "h";
              const categories = horizontal ? yCategories : (tickLabels.length > 0 && x.length > 0 ? yCategories : (x.length > 0 ? x : labels));
              const numericValues = horizontal ? x : (tickLabels.length > 0 && x.length > 0 ? x : (y.length > 0 ? y : values));
              const count = Math.max(categories.length, numericValues.length, texts.length);
              if (count > MAX_POINTS_PER_SERIES || totalPoints > MAX_TOTAL_POINTS - count) { extractionLimitExceeded = true; return { name: textOf(trace.name), type: textOf(trace.type), points: [] }; }
              totalPoints += count;
              return { name: textOf(trace.name), type: textOf(trace.type), points: Array.from({ length: count }, (_, index) => ({ category: textOf(categories[index]), value: numberOf(numericValues[index]), text: textOf(texts[index]) })) };
            }).filter(item => item.points.length > 0);
            return { title: String(title).trim(), description: String(description).trim(), notes, xAxisTitle: textOf(layout.xaxis?.title?.text), yAxisTitle: textOf(layout.yaxis?.title?.text), series };
          }).filter(chart => chart.series.length > 0);
          return extractionLimitExceeded ? "__MCEINDEX_RESOURCE_LIMIT__" : charts;
        })()
        """;
}
