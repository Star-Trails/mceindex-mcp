using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MceIndex.Mcp.Configuration;
using MceIndex.Mcp.Crawling;
using MceIndex.Mcp.Domain;
using MceIndex.Mcp.Parsing;

namespace MceIndex.Mcp.Tests;

public sealed class CrawlerIntegrationTests
{
    [Fact]
    public void RejectsTruncatedCanonicalChartCoverage()
    {
        static ChartData Chart(string title, int pointCount) => new(
            title,
            title,
            [],
            null,
            null,
            [new ChartSeries(title, "scatter",
                Enumerable.Range(0, pointCount)
                    .Select(index => new ChartPoint(index.ToString(System.Globalization.CultureInfo.InvariantCulture), index))
                    .ToArray())]);

        var source = new Uri("https://mceindex.com/Meaningful_Retail");
        MceIndexCrawler.ValidateProductionChartCoverage(
            source,
            [Chart("有意义社零", 128), Chart("社零关键增速", 128)]);

        var error = Assert.Throws<MceIndexException>(() =>
            MceIndexCrawler.ValidateProductionChartCoverage(
                source,
                [Chart("有意义社零", 128), Chart("社零关键增速", 127)]));
        Assert.Equal(MceIndexErrorCode.ExtractionFailed, error.Code);
        Assert.Contains("expected at least 2 charts and 256 points", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RendersDynamicPageAndExtractsIframeContent()
    {
        var browserExecutable = Environment.GetEnvironmentVariable("MCEINDEX_TEST_BROWSER");
        if (string.IsNullOrWhiteSpace(browserExecutable))
        {
            Assert.Skip("Set MCEINDEX_TEST_BROWSER to run the Playwright browser integration test.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await HtmlFixture.StartAsync(cancellationToken);
        var options = MceIndexOptions.Load(new Dictionary<string, string?>
        {
            ["MCEINDEX_BASE_URL"] = fixture.Url.AbsoluteUri,
            ["MCEINDEX_BROWSER_EXECUTABLE"] = browserExecutable,
            ["MCEINDEX_TIMEOUT_MS"] = "10000",
            ["MCEINDEX_SETTLE_MS"] = "100",
        });
        await using var crawler = new MceIndexCrawler(
            options,
            new MceIndexParser(),
            TimeProvider.System,
            NullLogger<MceIndexCrawler>.Instance);

        var crawled = await crawler.CrawlAsync(fixture.Url, cancellationToken);

        Assert.Equal("月度总览", crawled.Snapshot.AppTitle);
        Assert.Contains(crawled.Snapshot.Metrics, metric => metric.Label == "GDP 续命指数" && metric.Value == "10.54%");
        Assert.Contains(crawled.Snapshot.Navigation,
            item => item.Text == "价格" && item.Url == new Uri(fixture.Url, "/prices").AbsoluteUri);
        Assert.Contains("动态内容已稳定", crawled.Snapshot.Text);
        Assert.Contains("iframe 中的社融数据", crawled.Snapshot.Text);
        var card = Assert.Single(crawled.Snapshot.Cards);
        Assert.Equal(("LEI-GDP", "2026-06"), (card.Code, card.Period));
        Assert.Contains("产业规模占GDP比重", card.Description, StringComparison.Ordinal);
        var chart = Assert.Single(crawled.Snapshot.Charts);
        Assert.Equal("新产业占经济多大？", chart.Title);
        Assert.Contains("产业规模", chart.Description, StringComparison.Ordinal);
        Assert.Collection(
            Assert.Single(chart.Series).Points,
            point => Assert.Equal(("2026-05", 10.1), (point.Category, point.Value)),
            point => Assert.Equal(("2026-06", 10.54), (point.Category, point.Value)));
        Assert.Equal(2, crawled.HtmlDocuments.Length);
    }
    [Fact]
    public async Task CapturesEveryLifeIndexView()
    {
        var browserExecutable = Environment.GetEnvironmentVariable("MCEINDEX_TEST_BROWSER");
        if (string.IsNullOrWhiteSpace(browserExecutable))
        {
            Assert.Skip("Set MCEINDEX_TEST_BROWSER to run the Playwright browser integration test.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await HtmlFixture.StartAsync(cancellationToken, LifeIndexViewsHtml);
        var options = MceIndexOptions.Load(new Dictionary<string, string?>
        {
            ["MCEINDEX_BASE_URL"] = fixture.Url.AbsoluteUri,
            ["MCEINDEX_BROWSER_EXECUTABLE"] = browserExecutable,
            ["MCEINDEX_TIMEOUT_MS"] = "10000",
            ["MCEINDEX_SETTLE_MS"] = "100",
        });
        await using var crawler = new MceIndexCrawler(
            options,
            new MceIndexParser(),
            TimeProvider.System,
            NullLogger<MceIndexCrawler>.Instance);

        var crawled = await crawler.CrawlAsync(
            new Uri(fixture.Url, "/LI_Monthly"),
            cancellationToken);

        var expectedViews = new[] { "产业规模占比", "直接就业能力", "净财政能力", "行业下钻", "方法来源" };
        var expectedIndustries = new[] { "集成电路", "新能源汽车", "新能源", "电气化设备", "医药制造" };
        Assert.Equal(10, crawled.HtmlDocuments.Length);
        Assert.All(expectedViews,
            view => Assert.Contains($"{view}内容已加载", crawled.Snapshot.Text));
        Assert.All(expectedIndustries,
            industry => Assert.Contains($"{industry}行业内容已加载", crawled.Snapshot.Text));
        Assert.Equal(9, crawled.Snapshot.Charts.Length);
        Assert.All(expectedViews.Where(view => view != "行业下钻"),
            view => Assert.Contains(crawled.Snapshot.Charts, chart => chart.Title.StartsWith($"{view} ·", StringComparison.Ordinal)));
        Assert.All(expectedIndustries,
            industry => Assert.Contains(crawled.Snapshot.Charts, chart => chart.Title.StartsWith($"行业下钻 / {industry} ·", StringComparison.Ordinal)));
        Assert.All(crawled.Snapshot.Charts, chart =>
        {
            var series = Assert.Single(chart.Series);
            Assert.Equal(2, series.Points.Length);
        });
    }

    [Fact]
    public async Task ScrollsLazyChartsIntoViewBeforeExtraction()
    {
        var browserExecutable = Environment.GetEnvironmentVariable("MCEINDEX_TEST_BROWSER");
        if (string.IsNullOrWhiteSpace(browserExecutable))
        {
            Assert.Skip("Set MCEINDEX_TEST_BROWSER to run the Playwright browser integration test.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await HtmlFixture.StartAsync(cancellationToken, LazyChartsHtml);
        var options = MceIndexOptions.Load(new Dictionary<string, string?>
        {
            ["MCEINDEX_BASE_URL"] = fixture.Url.AbsoluteUri,
            ["MCEINDEX_BROWSER_EXECUTABLE"] = browserExecutable,
            ["MCEINDEX_TIMEOUT_MS"] = "10000",
            ["MCEINDEX_SETTLE_MS"] = "100",
        });
        await using var crawler = new MceIndexCrawler(
            options,
            new MceIndexParser(),
            TimeProvider.System,
            NullLogger<MceIndexCrawler>.Instance);

        var crawled = await crawler.CrawlAsync(
            new Uri(fixture.Url, "/Meaningful_Retail"),
            cancellationToken);

        Assert.Collection(
            crawled.Snapshot.Charts,
            chart => Assert.Equal("首屏图表", Assert.Single(chart.Series).Name),
            chart => Assert.Equal("懒加载图表", Assert.Single(chart.Series).Name));
    }

    [Fact]
    public async Task PreservesHorizontalAndTickMappedChartLabels()
    {
        var browserExecutable = Environment.GetEnvironmentVariable("MCEINDEX_TEST_BROWSER");
        if (string.IsNullOrWhiteSpace(browserExecutable))
        {
            Assert.Skip("Set MCEINDEX_TEST_BROWSER to run the Playwright browser integration test.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await HtmlFixture.StartAsync(cancellationToken, AxisMappedChartsHtml);
        var options = MceIndexOptions.Load(new Dictionary<string, string?>
        {
            ["MCEINDEX_BASE_URL"] = fixture.Url.AbsoluteUri,
            ["MCEINDEX_BROWSER_EXECUTABLE"] = browserExecutable,
            ["MCEINDEX_TIMEOUT_MS"] = "10000",
            ["MCEINDEX_SETTLE_MS"] = "100",
        });
        await using var crawler = new MceIndexCrawler(
            options,
            new MceIndexParser(),
            TimeProvider.System,
            NullLogger<MceIndexCrawler>.Instance);

        var crawled = await crawler.CrawlAsync(fixture.Url, cancellationToken);

        Assert.Collection(
            crawled.Snapshot.Charts,
            chart => Assert.Collection(
                Assert.Single(chart.Series).Points,
                point => Assert.Equal(("限额以下", 3.2), (point.Category, point.Value)),
                point => Assert.Equal(("限额以上", -2), (point.Category, point.Value))),
            chart => Assert.Collection(
                Assert.Single(chart.Series).Points,
                point => Assert.Equal(("CPI", 1), (point.Category, point.Value)),
                point => Assert.Equal(("PPI", 4.1), (point.Category, point.Value))));
    }

    [Fact]
    public async Task RejectsOversizedChartSeries()
    {
        var browserExecutable = Environment.GetEnvironmentVariable("MCEINDEX_TEST_BROWSER");
        if (string.IsNullOrWhiteSpace(browserExecutable))
        {
            Assert.Skip("Set MCEINDEX_TEST_BROWSER to run the Playwright browser integration test.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await HtmlFixture.StartAsync(cancellationToken, OversizedChartHtml);
        var options = MceIndexOptions.Load(new Dictionary<string, string?>
        {
            ["MCEINDEX_BASE_URL"] = fixture.Url.AbsoluteUri,
            ["MCEINDEX_BROWSER_EXECUTABLE"] = browserExecutable,
            ["MCEINDEX_TIMEOUT_MS"] = "10000",
            ["MCEINDEX_SETTLE_MS"] = "100",
        });
        await using var crawler = new MceIndexCrawler(
            options,
            new MceIndexParser(),
            TimeProvider.System,
            NullLogger<MceIndexCrawler>.Instance);

        var error = await Assert.ThrowsAsync<MceIndexException>(
            () => crawler.CrawlAsync(fixture.Url, cancellationToken));

        Assert.Equal(MceIndexErrorCode.ExtractionFailed, error.Code);
        Assert.Equal("MCEIndex chart data exceeded safe extraction limits.", error.Message);
    }


    [Fact]
    public async Task ReportsMissingBrowserWithStableErrorCode()
    {
        var options = MceIndexOptions.Load(new Dictionary<string, string?>
        {
            ["MCEINDEX_BASE_URL"] = "http://127.0.0.1:1/",
            ["MCEINDEX_BROWSER_EXECUTABLE"] = "/does/not/exist",
        });
        await using var crawler = new MceIndexCrawler(
            options,
            new MceIndexParser(),
            TimeProvider.System,
            NullLogger<MceIndexCrawler>.Instance);

        var error = await Assert.ThrowsAsync<MceIndexException>(
            () => crawler.CrawlAsync(options.BaseUri, TestContext.Current.CancellationToken));

        Assert.Equal(MceIndexErrorCode.BrowserNotFound, error.Code);
    }

    private const string LifeIndexViewsHtml = """
        <!doctype html><html lang="zh-CN"><head><title>续命指数</title></head><body>
        <main data-testid="stMain">
          <h1>续命指数</h1>
          <div aria-label="button group" role="radiogroup">
            <button class="view" type="button" onclick="setView(this.textContent)">产业规模占比</button>
            <button class="view" type="button" onclick="setView(this.textContent)">直接就业能力</button>
            <button class="view" type="button" onclick="setView(this.textContent)">净财政能力</button>
            <button class="view" type="button" onclick="setView(this.textContent)">行业下钻</button>
            <button class="view" type="button" onclick="setView(this.textContent)">方法来源</button>
          </div>
          <div aria-label="history range" role="radiogroup">
            <button class="range" type="button" data-testid="stBaseButton-segmented_controlActive" onclick="setRange(this.textContent)">3Y</button>
            <button class="range" type="button" data-testid="stBaseButton-segmented_control" onclick="setRange(this.textContent)">All</button>
          </div>
          <section id="view"></section>
        </main>
        <script>
          const views = ['产业规模占比', '直接就业能力', '净财政能力', '行业下钻', '方法来源'];
          const industries = ['集成电路', '新能源汽车', '新能源', '电气化设备', '医药制造'];
          let currentView = views[0];
          let selectedIndustry = industries[0];
          let allHistory = false;
          function setRange(range) {
            document.querySelectorAll('.range').forEach(button => button.setAttribute(
              'data-testid',
              button.textContent === range
                ? 'stBaseButton-segmented_controlActive'
                : 'stBaseButton-segmented_control'));
            allHistory = range === 'All';
            setView(currentView);
          }
          function showIndustries() {
            document.querySelector('#industry-options').style.display = 'block';
          }
          function selectIndustry(industry) {
            selectedIndustry = industry;
            setView('行业下钻');
          }
          function setView(view) {
            currentView = view;
            document.querySelectorAll('.view').forEach(button => button.setAttribute(
              'data-testid',
              button.textContent === view
                ? 'stBaseButton-segmented_controlActive'
                : 'stBaseButton-segmented_control'));
            const index = views.indexOf(view);
            const content = document.querySelector('#view');
            const industryMarkup = view === '行业下钻'
              ? `<div data-testid="stSelectbox"><div role="combobox" tabindex="0" onclick="showIndustries()">
                   <div value="${selectedIndustry}">${selectedIndustry}</div></div></div>
                 <div id="industry-options" style="display:none">
                   ${industries.map(industry => `<div role="option" onclick="selectIndustry(this.textContent)">${industry}</div>`).join('')}
                 </div>
                 <p>${selectedIndustry}行业内容已加载</p>`
              : '';
            content.innerHTML = `<h2>${view}数据</h2><p>${view}内容已加载</p>${industryMarkup}
              <div data-testid="stPlotlyChart"><div class="js-plotly-plot"></div></div>`;
            const plot = content.querySelector('.js-plotly-plot');
            const categories = allHistory ? ['2026-05', '2026-06'] : ['2026-06'];
            const values = allHistory ? [index, index + 1] : [index + 1];
            plot._fullData = [];
            plot.data = [{
              name: view === '行业下钻' ? selectedIndustry : view,
              type: 'bar',
              x: categories,
              y: values,
            }];
            plot.layout = { title: { text: `${view}图表` } };
          }
          setView(views[0]);
        </script>
        </body></html>
        """;

    private const string AxisMappedChartsHtml = """
        <!doctype html><html lang="zh-CN"><head><title>坐标映射图表</title></head><body>
        <main data-testid="stMain">
          <h1>坐标映射图表</h1>
          <div data-testid="stPlotlyChart"><div id="horizontal" class="js-plotly-plot"></div></div>
          <div data-testid="stPlotlyChart"><div id="ticks" class="js-plotly-plot"></div></div>
        </main>
        <script>
          const horizontal = document.querySelector('#horizontal');
          horizontal.data = [{
            name: '社零结构', type: 'bar', orientation: 'h',
            x: [3.2, -2], y: ['限额以下', '限额以上']
          }];
          horizontal.layout = { title: { text: '消费结构' } };
          const ticks = document.querySelector('#ticks');
          ticks.data = [{ name: '官方', type: 'scatter', x: [1, 4.1], y: [1, 0] }];
          ticks.layout = {
            title: { text: '物价读数' },
            yaxis: { tickvals: [1, 0], ticktext: ['CPI', 'PPI'] }
          };
        </script>
        </body></html>
        """;

    private const string LazyChartsHtml = """
        <!doctype html><html lang="zh-CN"><head><title>懒加载图表</title></head><body>
        <main data-testid="stMain">
          <h1>懒加载图表</h1>
          <div data-testid="stPlotlyChart"><div id="first" class="js-plotly-plot" style="height:300px"></div></div>
          <div data-testid="stPlotlyChart" style="margin-top:2000px">
            <div id="lazy" class="js-plotly-plot" style="height:300px"></div>
          </div>
        </main>
        <script>
          const first = document.querySelector('#first');
          first.data = [{ name: '首屏图表', type: 'scatter', x: ['2026-06'], y: [1] }];
          first.layout = {};
          const lazy = document.querySelector('#lazy');
          new IntersectionObserver(entries => {
            if (!entries.some(entry => entry.isIntersecting)) return;
            lazy.data = [{ name: '懒加载图表', type: 'scatter', x: ['2026-06'], y: [2] }];
            lazy.layout = {};
          }).observe(lazy);
        </script>
        </body></html>
        """;

    private const string OversizedChartHtml = """
        <!doctype html><html><head><title>Oversized chart</title></head><body>
        <main data-testid="stMain"><h1>月度总览</h1>
          <div data-testid="stPlotlyChart"><div id="plot" class="js-plotly-plot"></div></div>
        </main>
        <script>
          const plot = document.querySelector('#plot');
          const values = Array.from({ length: 10001 }, (_, index) => index);
          plot.data = [{ name: 'oversized', type: 'scatter', x: values, y: values }];
          plot.layout = {};
        </script>
        </body></html>
        """;

    private sealed class HtmlFixture : IAsyncDisposable
    {
        private const string Html = """
            <!doctype html>
            <html lang="zh-CN"><head><title>MCEIndex fixture</title></head><body>
              <aside data-testid="stSidebar"><a href="/prices">价格</a></aside>
              <main data-testid="stMain"></main>
              <iframe srcdoc="&lt;html&gt;&lt;body&gt;&lt;p&gt;iframe 中的社融数据&lt;/p&gt;&lt;/body&gt;&lt;/html&gt;"></iframe>
              <script>
                setTimeout(() => {
                  document.querySelector('main').innerHTML = `
                    <h1>月度总览</h1>
                    <div data-testid="stMetric">
                      <div data-testid="stMetricLabel">GDP 续命指数</div>
                      <div data-testid="stMetricValue">10.54%</div>
                    </div>
                    <div class="terminal-ticker-item">
                      <span class="terminal-ticker-code">LEI-GDP</span>
                      <span class="terminal-ticker-value">10.54%</span>
                      <span class="terminal-ticker-comparison">2026-06 · 12M均值 9.52%</span>
                    </div>
                    <div data-testid="stElementContainer">
                      <div class="chart-header"><h3>新产业占经济多大？</h3>
                      <p class="chart-header-summary">产业规模的结构化说明。</p><p>数据截至 2026-06</p></div>
                    </div>
                    <div data-testid="stElementContainer">
                      <div data-testid="stPlotlyChart"><div id="plot" class="js-plotly-plot"></div></div>
                    </div>
                    <p>动态内容已稳定</p>`;
                  const plot = document.querySelector('#plot');
                  plot._fullData = [];
                  plot.data = [{ name: '产业规模占比', type: 'scatter', x: ['2026-05', '2026-06'], y: [10.1, 10.54] }];
                  plot.layout = { xaxis: { title: { text: '月份' } }, yaxis: { title: { text: '占 GDP 比重（%）' } } };
                }, 250);
              </script>
            </body></html>
            """;

        private readonly TcpListener listener;
        private readonly CancellationTokenSource stopping;
        private readonly Task serverTask;

        private HtmlFixture(TcpListener listener, CancellationTokenSource stopping, Task serverTask, Uri url)
        {
            this.listener = listener;
            this.stopping = stopping;
            this.serverTask = serverTask;
            Url = url;
        }

        public Uri Url { get; }

        public static Task<HtmlFixture> StartAsync(
            CancellationToken cancellationToken,
            string? html = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var body = Encoding.UTF8.GetBytes(html ?? Html);
            var serverTask = ServeAsync(listener, body, stopping.Token);
            return Task.FromResult(new HtmlFixture(listener, stopping, serverTask, new Uri($"http://127.0.0.1:{port}/")));
        }

        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            listener.Stop();
            try
            {
                await serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            stopping.Dispose();
        }

        private static async Task ServeAsync(
            TcpListener listener,
            byte[] body,
            CancellationToken cancellationToken)
        {
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                await using var stream = client.GetStream();
                var request = new byte[4096];
                _ = await stream.ReadAsync(request, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
