using Microsoft.Extensions.Logging.Abstractions;
using MceIndex.Mcp.Configuration;
using MceIndex.Mcp.Crawling;
using MceIndex.Mcp.Domain;
using MceIndex.Mcp.Persistence;
using MceIndex.Mcp.Services;

namespace MceIndex.Mcp.Tests;

public sealed class ServiceTests
{
    [Fact]
    public async Task FirstConcurrentQueriesRefreshOnceAndLaterQueriesStayLocal()
    {
        using var store = new MceIndexStore(":memory:");
        var timeProvider = new ManualTimeProvider();
        var crawler = new FakeCrawler(timeProvider);
        await using var coordinator = new RefreshCoordinator(
            Options(), store, crawler, timeProvider, NullLogger<RefreshCoordinator>.Instance);
        var service = new MceIndexService(store, coordinator);

        var firstQueries = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => service.ListPagesAsync(CancellationToken.None)));

        Assert.All(firstQueries, result => Assert.Single(result.Pages));
        Assert.Equal(1, crawler.CallCount);

        timeProvider.Advance(TimeSpan.FromDays(2));
        var search = await service.SearchAsync(
            "新能源汽车", null, null, SearchMode.Phrase, 0, 20, CancellationToken.None);
        Assert.Single(search.Hits);
        Assert.Equal(1, crawler.CallCount);

        var refresh = await service.RefreshAsync(true, CancellationToken.None);
        Assert.Equal(RefreshOutcome.Completed, refresh.Report.Outcome);
        Assert.Equal(2, crawler.CallCount);

        _ = await service.ListPagesAsync(CancellationToken.None);
        Assert.Equal(2, crawler.CallCount);
    }

    [Fact]
    public async Task FailedSessionRefreshUsesExistingIndexWithoutRetrying()
    {
        using var store = new MceIndexStore(":memory:");
        var timeProvider = new ManualTimeProvider();
        Seed(store, timeProvider.GetUtcNow() - TimeSpan.FromMinutes(2));
        var crawler = new FakeCrawler(timeProvider) { Fail = true };
        await using var coordinator = new RefreshCoordinator(
            Options(), store, crawler, timeProvider, NullLogger<RefreshCoordinator>.Instance);
        var service = new MceIndexService(store, coordinator);

        var first = await service.ListPagesAsync(CancellationToken.None);
        var second = await service.ListPagesAsync(CancellationToken.None);

        Assert.Single(first.Pages);
        Assert.Single(second.Pages);
        Assert.Equal(1, crawler.CallCount);
    }

    [Fact]
    public async Task EmptyFailedSessionDoesNotRetryUntilNewServiceSession()
    {
        using var store = new MceIndexStore(":memory:");
        var timeProvider = new ManualTimeProvider();
        var crawler = new FakeCrawler(timeProvider) { Fail = true };
        await using var coordinator = new RefreshCoordinator(
            Options(), store, crawler, timeProvider, NullLogger<RefreshCoordinator>.Instance);
        var firstSession = new MceIndexService(store, coordinator);

        var first = await Assert.ThrowsAsync<MceIndexException>(
            () => firstSession.ListPagesAsync(CancellationToken.None));
        var repeated = await Assert.ThrowsAsync<MceIndexException>(
            () => firstSession.ListPagesAsync(CancellationToken.None));

        Assert.Equal(MceIndexErrorCode.IndexEmpty, first.Code);
        Assert.Equal(MceIndexErrorCode.IndexEmpty, repeated.Code);
        Assert.Equal(1, crawler.CallCount);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var nextSession = new MceIndexService(store, coordinator);
        var next = await Assert.ThrowsAsync<MceIndexException>(
            () => nextSession.ListPagesAsync(CancellationToken.None));

        Assert.Equal(MceIndexErrorCode.IndexEmpty, next.Code);
        Assert.Equal(2, crawler.CallCount);
    }

    [Fact]
    public async Task ChartViewOmitsCardsAndProjectsChartData()
    {
        using var store = new MceIndexStore(":memory:");
        var timeProvider = new ManualTimeProvider();
        var source = CreatePage(new Uri("http://127.0.0.1:3000/Meaningful_TSF"), timeProvider.GetUtcNow());
        var crawled = source with
        {
            Snapshot = source.Snapshot with
            {
                Cards =
                [
                    new IndexCard("MSF", "有意义社融", "68.2%", "2026-06", "2026-06", "说明"),
                ],
                Charts =
                [
                    new ChartData(
                        "<b><b></b></b>",
                        "MCEIndex 页面中的“<b><b></b></b>”图表。",
                        [],
                        "月份",
                        "三项流量（亿元）",
                        [new ChartSeries("有意义社融", "bar",
                            [new ChartPoint("2026-06-01T00:00:00.000000", 0.30000000000000004)])]),
                ],
            },
        };
        store.ApplyPages([new IndexedPage("Meaningful_TSF", "有意义社融", crawled)], timeProvider.GetUtcNow());
        store.RecordRefresh(timeProvider.GetUtcNow(), [], true);
        var crawler = new FakeCrawler(timeProvider);
        await using var coordinator = new RefreshCoordinator(
            Options(), store, crawler, timeProvider, NullLogger<RefreshCoordinator>.Instance);
        var service = new MceIndexService(store, coordinator);

        var result = await service.GetPageAsync(
            "有意义社融", PageView.Charts, 0, 50, CancellationToken.None);

        Assert.Empty(result.Cards);
        var chart = Assert.Single(result.Charts);
        Assert.Equal("三项流量（亿元）", chart.Title);
        var point = Assert.Single(Assert.Single(chart.Series).Points);
        Assert.Equal(("2026-06", 0.3, "0.3"), (point.Category, point.Value, point.DisplayValue));
        Assert.Equal(0, crawler.CallCount);
    }

    private static MceIndexOptions Options() => new()
    {
        BaseUri = new Uri("http://127.0.0.1:3000/"),
        DatabasePath = ":memory:",
        CamofoxUri = MceIndexOptions.DefaultCamofoxUri,
        RequestTimeout = TimeSpan.FromSeconds(5),
        DomQuietPeriod = TimeSpan.FromMilliseconds(100),
        RefreshInterval = TimeSpan.FromHours(24),
        CrawlDelay = TimeSpan.Zero,
        CrawlConcurrency = 1,
        MaxPages = 20,
    };

    private static void Seed(MceIndexStore store, DateTimeOffset fetchedAt)
    {
        var crawled = CreatePage(new Uri("http://127.0.0.1:3000/"), fetchedAt);
        store.ApplyPages([new IndexedPage("home", "月度总览", crawled)], fetchedAt);
        store.RecordRefresh(fetchedAt, [], true);
    }

    private static CrawledPage CreatePage(Uri target, DateTimeOffset fetchedAt) => new(
        new PageSnapshot
        {
            SourceUrl = target.AbsoluteUri,
            FetchedAt = fetchedAt,
            Title = "有意义中国经济指数",
            AppTitle = "月度总览",
            Headings = [new Heading(1, "月度总览")],
            Navigation = [],
            Metrics = [],
            Tables = [],
            Text = ["新能源汽车产量"],
        },
        ["<main>fixture</main>"]);

    private sealed class FakeCrawler(ManualTimeProvider timeProvider) : IMceIndexCrawler
    {
        public int CallCount;
        public bool Fail;

        public async Task<CrawledPage> CrawlAsync(Uri target, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            await Task.Delay(50, cancellationToken);
            if (Fail)
            {
                throw new MceIndexException(MceIndexErrorCode.AccessChallenge, "fixture challenge");
            }

            return CreatePage(target, timeProvider.GetUtcNow());
        }

        public Task CloseBrowserAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
