using MceIndex.Mcp.Domain;
using MceIndex.Mcp.Parsing;

namespace MceIndex.Mcp.Tests;

public sealed class ParserTests
{
    private const string Html = """
        <!doctype html><html lang="zh-CN"><head><title>有意义中国经济指数</title>
        <meta name="description" content="读懂中国经济"></head><body>
        <aside data-testid="stSidebar"><a href="/prices">价格</a><button>月度总览</button></aside>
        <main data-testid="stMain"><h1>月度总览</h1>
        <div data-testid="stMetric"><span data-testid="stMetricLabel">GDP 综合指数</span>
        <span data-testid="stMetricValue">10.54%</span><span data-testid="stMetricDelta">+0.4%</span></div>
        <div class="terminal-ticker-item"><span class="terminal-ticker-code">LEI-GDP</span>
        <span class="terminal-ticker-value">10.54%</span>
        <span class="terminal-ticker-comparison">2026-06 · 12M均值 9.52%</span></div>
        <h2>数据表</h2><table><thead><tr><th>指标</th><th>值</th></tr></thead>
        <tbody><tr><td>CPI</td><td>1.2%</td></tr></tbody></table>
        <p>中国经济最有活力的部分。</p></main></body></html>
        """;

    [Fact]
    public void ExtractsStructuredSnapshot()
    {
        var now = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var snapshot = new MceIndexParser().Extract([Html], new Uri("https://mceindex.com/"), now);

        Assert.Equal("有意义中国经济指数", snapshot.Title);
        Assert.Equal("月度总览", snapshot.AppTitle);
        Assert.Contains(snapshot.Navigation, item => item is { Text: "价格", Kind: NavigationKind.Link, Url: "https://mceindex.com/prices" });
        Assert.Contains(snapshot.Metrics, metric => metric is { Label: "GDP 综合指数", Value: "10.54%", Delta: "+0.4%" });
        Assert.Single(snapshot.Tables);
        var card = Assert.Single(snapshot.Cards);
        Assert.Equal(("LEI-GDP", "10.54%", "2026-06"), (card.Code, card.Value, card.Period));
        Assert.Contains("产业规模占GDP比重", card.Description, StringComparison.Ordinal);
        Assert.Contains("中国经济最有活力的部分。", snapshot.Text);
    }

    [Fact]
    public void MergesDocumentsWithoutDuplicates()
    {
        var snapshot = new MceIndexParser().Extract([Html, Html], new Uri("https://mceindex.com/"), DateTimeOffset.UtcNow);
        Assert.Single(snapshot.Headings, heading => heading.Text == "月度总览");
        Assert.Single(snapshot.Metrics);
        Assert.Single(snapshot.Tables);
    }
    [Fact]
    public void RejectsTooManyHtmlDocuments()
    {
        var error = Assert.Throws<MceIndexException>(() => new MceIndexParser().Extract(
            Enumerable.Repeat(Html, 33).ToArray(),
            new Uri("https://mceindex.com/"),
            DateTimeOffset.UtcNow));

        Assert.Equal(MceIndexErrorCode.ExtractionFailed, error.Code);
        Assert.Contains("more than 32 HTML documents", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsOversizedHtmlDocument()
    {
        var error = Assert.Throws<MceIndexException>(() => new MceIndexParser().Extract(
            [new string('x', 5_000_001)],
            new Uri("https://mceindex.com/"),
            DateTimeOffset.UtcNow));

        Assert.Equal(MceIndexErrorCode.ExtractionFailed, error.Code);
        Assert.Equal("MCEIndex HTML exceeded safe extraction limits.", error.Message);
    }

    [Fact]
    public void RejectsOversizedCombinedHtml()
    {
        var segment = new string('x', 4_100_000);
        var error = Assert.Throws<MceIndexException>(() => new MceIndexParser().Extract(
            Enumerable.Repeat(segment, 5).ToArray(),
            new Uri("https://mceindex.com/"),
            DateTimeOffset.UtcNow));

        Assert.Equal(MceIndexErrorCode.ExtractionFailed, error.Code);
        Assert.Equal("MCEIndex HTML exceeded safe extraction limits.", error.Message);
    }


    [Theory]
    [InlineData("<title>Just a moment...</title><script src='https://challenges.cloudflare.com/a.js'></script>")]
    [InlineData("<div id='cf-chl-widget'>Verify you are human</div>")]
    public void DetectsCloudflareChallenge(string html) => Assert.True(MceIndexParser.IsAccessChallenge(html));
}
