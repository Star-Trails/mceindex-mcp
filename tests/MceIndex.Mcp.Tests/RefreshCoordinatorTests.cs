using Microsoft.Extensions.Logging.Abstractions;
using MceIndex.Mcp.Configuration;
using MceIndex.Mcp.Crawling;
using MceIndex.Mcp.Domain;
using MceIndex.Mcp.Persistence;
using MceIndex.Mcp.Services;

namespace MceIndex.Mcp.Tests;

public sealed class RefreshCoordinatorTests
{
    [Fact]
    public async Task CoalescesConcurrentRefreshesAndPreservesIndexedDataOnFailure()
    {
        using var store = new MceIndexStore(":memory:");
        var timeProvider = new ManualTimeProvider();
        var crawler = new FakeCrawler();
        await using var coordinator = new RefreshCoordinator(Options(), store, crawler, timeProvider,
            NullLogger<RefreshCoordinator>.Instance);

        var reports = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => coordinator.RefreshAsync(true, CancellationToken.None)));

        Assert.Equal(1, crawler.CallCount);
        Assert.All(reports, report => Assert.Equal(RefreshOutcome.Completed, report.Outcome));
        Assert.Equal(1, store.CountPages());
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        crawler.Fail = true;
        var partial = await coordinator.RefreshAsync(true, CancellationToken.None);
        Assert.Equal(RefreshOutcome.Partial, partial.Outcome);
        Assert.All(partial.Failures, failure => Assert.Equal("LOAD_TIMEOUT", failure.Code));
        Assert.Equal(1, store.CountPages());

        var skipped = await coordinator.RefreshAsync(false, CancellationToken.None);
        Assert.Equal(RefreshOutcome.Skipped, skipped.Outcome);
        Assert.False(coordinator.ShouldRefresh());
        Assert.Equal(4, crawler.CallCount);
        Assert.NotNull(store.FindPage("月度总览"));
    }

    [Fact]
    public async Task AppliesRefreshCooldownAfterEmptyIndexFailure()
    {
        using var store = new MceIndexStore(":memory:");
        var crawler = new FakeCrawler { Fail = true };
        await using var coordinator = new RefreshCoordinator(
            Options(),
            store,
            crawler,
            TimeProvider.System,
            NullLogger<RefreshCoordinator>.Instance);

        var error = await Assert.ThrowsAsync<MceIndexException>(
            () => coordinator.RefreshAsync(false, CancellationToken.None));
        Assert.Equal(MceIndexErrorCode.IndexEmpty, error.Code);
        Assert.False(coordinator.ShouldRefresh());

        var skipped = await coordinator.RefreshAsync(false, CancellationToken.None);
        Assert.Equal(RefreshOutcome.Skipped, skipped.Outcome);
        Assert.Equal(3, crawler.CallCount);
    }
    [Fact]
    public async Task EnforcesHardCooldownForForcedRefresh()
    {
        using var store = new MceIndexStore(":memory:");
        var timeProvider = new ManualTimeProvider();
        var crawler = new FakeCrawler();
        await using var coordinator = new RefreshCoordinator(
            Options(),
            store,
            crawler,
            timeProvider,
            NullLogger<RefreshCoordinator>.Instance);

        var first = await coordinator.RefreshAsync(true, CancellationToken.None);
        var skipped = await coordinator.RefreshAsync(true, CancellationToken.None);

        Assert.Equal(RefreshOutcome.Completed, first.Outcome);
        Assert.Equal(RefreshOutcome.Skipped, skipped.Outcome);
        Assert.Equal(1, crawler.CallCount);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var afterCooldown = await coordinator.RefreshAsync(true, CancellationToken.None);

        Assert.Equal(RefreshOutcome.Completed, afterCooldown.Outcome);
        Assert.Equal(2, crawler.CallCount);
    }

    [Fact]
    public async Task SanitizesUnexpectedAcquisitionFailure()
    {
        using var store = new MceIndexStore(":memory:");
        var timeProvider = new ManualTimeProvider();
        var crawler = new FakeCrawler();
        await using var coordinator = new RefreshCoordinator(
            Options(),
            store,
            crawler,
            timeProvider,
            NullLogger<RefreshCoordinator>.Instance);
        _ = await coordinator.RefreshAsync(true, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        crawler.UnexpectedFailure = true;

        var report = await coordinator.RefreshAsync(true, CancellationToken.None);

        var failure = Assert.Single(report.Failures);
        Assert.Equal("ACQUISITION_FAILED", failure.Code);
        Assert.Equal("MCEIndex acquisition failed.", failure.Message);
        Assert.DoesNotContain("private-profile", store.GetMeta("last_error"), StringComparison.Ordinal);
    }


    private static MceIndexOptions Options() => new()
    {
        BaseUri = new Uri("http://127.0.0.1:3000/"),
        DatabasePath = ":memory:",
        BrowserUserAgent = MceIndexOptions.DefaultBrowserUserAgent,
        Headless = true,
        RequestTimeout = TimeSpan.FromSeconds(5),
        DomQuietPeriod = TimeSpan.FromMilliseconds(100),
        RefreshInterval = TimeSpan.FromHours(6),
        CrawlConcurrency = 2,
        MaxPages = 20,
    };

    private sealed class FakeCrawler : IMceIndexCrawler
    {
        public int CallCount;
        public bool Fail;
        public bool UnexpectedFailure;

        public async Task<CrawledPage> CrawlAsync(Uri target, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            await Task.Delay(50, cancellationToken);
            if (UnexpectedFailure)
            {
                throw new InvalidOperationException("secret /home/user/private-profile");
            }
            if (Fail)
            {
                throw new MceIndexException(MceIndexErrorCode.LoadTimeout, "fixture timeout");
            }

            return new CrawledPage(new PageSnapshot
            {
                SourceUrl = target.AbsoluteUri,
                FetchedAt = DateTimeOffset.UtcNow,
                Title = "有意义中国经济指数",
                AppTitle = "月度总览",
                Headings = [new Heading(1, "月度总览")],
                Navigation = [],
                Metrics = [],
                Tables = [],
                Text = ["LEI-GDP", "10.54%", "2026-06", "新能源汽车产量"],
            }, ["<main>fixture</main>"]);
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
