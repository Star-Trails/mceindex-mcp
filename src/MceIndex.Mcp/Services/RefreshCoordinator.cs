using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using MceIndex.Mcp.Configuration;
using MceIndex.Mcp.Crawling;
using MceIndex.Mcp.Domain;
using MceIndex.Mcp.Persistence;

namespace MceIndex.Mcp.Services;

public sealed partial class RefreshCoordinator(
    MceIndexOptions options,
    MceIndexStore store,
    IMceIndexCrawler crawler,
    TimeProvider timeProvider,
    ILogger<RefreshCoordinator> logger) : IAsyncDisposable
{
    private static readonly FrozenDictionary<string, string> ProductionRoutes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Monthly_Overview"] = "月度总览",
            ["LI_Monthly"] = "五大新产业续命指数",
            ["Meaningful_CPI_PPI"] = "有意义CPI/PPI",
            ["Meaningful_TSF"] = "有意义社融",
            ["Meaningful_Retail"] = "有意义社零",
        }.ToFrozenDictionary(StringComparer.Ordinal);
    private static readonly TimeSpan ForcedRefreshMinimumInterval = TimeSpan.FromMinutes(1);

    private readonly object refreshGate = new();
    private readonly CancellationTokenSource stopping = new();
    private Task<CrawlReport>? activeRefresh;
    private bool disposed;

    public bool IsRefreshing
    {
        get
        {
            lock (refreshGate)
            {
                return activeRefresh is not null;
            }
        }
    }

    public bool IsStale()
    {
        var value = store.GetMeta("last_successful_refresh");
        return !DateTimeOffset.TryParse(value, out var lastSuccess) || timeProvider.GetUtcNow() - lastSuccess > options.RefreshInterval;
    }

    public bool ShouldRefresh()
    {
        var lastAttempt = ParseTimestamp(store.GetMeta("last_refresh_attempt"));
        if (lastAttempt is not null && timeProvider.GetUtcNow() - lastAttempt < options.RefreshInterval)
        {
            return false;
        }

        if (store.CountPages() == 0)
        {
            return true;
        }

        return IsStale();
    }

    public IndexStatus GetStatus()
    {
        var error = store.GetMeta("last_error");
        return new IndexStatus(
            store.Path,
            MceIndexStore.CurrentSchemaVersion,
            store.CountPages(),
            store.GetGeneration(),
            ParseTimestamp(store.GetMeta("last_successful_refresh")),
            ParseTimestamp(store.GetMeta("last_refresh_attempt")),
            IsStale(),
            IsRefreshing,
            string.IsNullOrWhiteSpace(error) ? null : error);
    }


    public Task<CrawlReport> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        Task<CrawlReport> task;
        lock (refreshGate)
        {
            if (activeRefresh is not null)
            {
                task = activeRefresh;
            }
            else if (ShouldSkipRefresh(force))
            {
                var now = timeProvider.GetUtcNow();
                task = Task.FromResult(new CrawlReport(now, now, RefreshOutcome.Skipped, 0, 0, 0, []));
            }
            else
            {
                activeRefresh = RunAndResetAsync(stopping.Token);
                task = activeRefresh;
            }
        }

        return task.WaitAsync(cancellationToken);
    }
    private bool ShouldSkipRefresh(bool force)
    {
        if (!force)
        {
            return !ShouldRefresh();
        }

        var lastAttempt = ParseTimestamp(store.GetMeta("last_refresh_attempt"));
        return lastAttempt is not null &&
               timeProvider.GetUtcNow() - lastAttempt < ForcedRefreshMinimumInterval;
    }


    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        await stopping.CancelAsync().ConfigureAwait(false);
        Task<CrawlReport>? refresh;
        lock (refreshGate)
        {
            refresh = activeRefresh;
        }
        if (refresh is not null)
        {
            await IgnoreCancellation(refresh).ConfigureAwait(false);
        }
        await crawler.CloseBrowserAsync().ConfigureAwait(false);
        stopping.Dispose();
        await crawler.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<CrawlReport> RunAndResetAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await CrawlAllAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await crawler.CloseBrowserAsync().ConfigureAwait(false);
            lock (refreshGate)
            {
                activeRefresh = null;
            }
        }
    }

    private async Task<CrawlReport> CrawlAllAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var targets = InitialTargets();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var successful = new List<(CrawlTarget Target, CrawledPage Crawled)>();
        var failures = new List<CrawlFailure>();
        var cursor = 0;

        while (cursor < targets.Count && seen.Count < options.MaxPages)
        {
            var wave = new List<CrawlTarget>();
            while (cursor < targets.Count && seen.Count < options.MaxPages)
            {
                var target = targets[cursor++];
                var normalized = target.Uri.AbsoluteUri;
                if (seen.Add(normalized))
                {
                    wave.Add(target);
                }
            }

            using var semaphore = new SemaphoreSlim(options.CrawlConcurrency, options.CrawlConcurrency);
            var results = await Task.WhenAll(wave.Select(async target =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await CrawlWithRetryAsync(target, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    semaphore.Release();
                }
            })).ConfigureAwait(false);

            foreach (var result in results)
            {
                if (result.Crawled is null)
                {
                    failures.Add(result.Failure!);
                    continue;
                }

                successful.Add((result.Target, result.Crawled));
                foreach (var item in result.Crawled.Snapshot.Navigation)
                {
                    if (item.Url is null || !Uri.TryCreate(item.Url, UriKind.Absolute, out var discovered) ||
                        !SameOrigin(options.BaseUri, discovered) || seen.Contains(discovered.AbsoluteUri))
                    {
                        continue;
                    }
                    targets.Add(new CrawlTarget(discovered, item.Text));
                }
            }
        }

        var indexed = successful.Select(result =>
        {
            var source = new Uri(result.Crawled.Snapshot.SourceUrl);
            var slug = source.AbsolutePath.Trim('/');
            if (slug.Length == 0) slug = "home";
            var ownLabel = result.Crawled.Snapshot.Navigation.FirstOrDefault(item =>
                item.Url is not null && Uri.TryCreate(item.Url, UriKind.Absolute, out var itemUri) && itemUri.AbsolutePath == source.AbsolutePath)?.Text;
            return new IndexedPage(slug, ownLabel ?? result.Target.Label, result.Crawled);
        }).GroupBy(page => page.Slug, StringComparer.OrdinalIgnoreCase).Select(group => group.Last()).ToArray();

        var finishedAt = timeProvider.GetUtcNow();
        var applied = store.ApplyPages(indexed, finishedAt);
        var fullSuccess = failures.Count == 0 && indexed.Length > 0;
        store.RecordRefresh(finishedAt, failures, fullSuccess);
        if (indexed.Length == 0 && store.CountPages() == 0)
        {
            throw new MceIndexException(
                MceIndexErrorCode.IndexEmpty,
                "The initial MCEIndex crawl failed for every page.",
                new Dictionary<string, object?> { ["failures"] = failures });
        }

        var outcome = failures.Count == 0 ? RefreshOutcome.Completed : RefreshOutcome.Partial;
        return new CrawlReport(startedAt, finishedAt, outcome, indexed.Length,
            applied.ChangedPages, applied.UnchangedPages, [.. failures]);
    }

    private async Task<CrawlAttempt> CrawlWithRetryAsync(CrawlTarget target, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return new CrawlAttempt(target, await crawler.CrawlAsync(target.Uri, cancellationToken).ConfigureAwait(false), null);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                lastError = error;
                if (!IsRetryable(error) || attempt == 3)
                {
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(attempt), timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        var domainError = lastError as MceIndexException;
        var code = domainError is not null
            ? MceIndexErrorCodes.ToProtocolCode(domainError.Code)
            : "ACQUISITION_FAILED";
        if (domainError is null && lastError is not null)
        {
            LogUnexpectedAcquisitionFailure(logger, lastError, target.Uri);
        }
        return new CrawlAttempt(target, null, new CrawlFailure(
            target.Uri.AbsoluteUri,
            code,
            domainError?.Message ?? "MCEIndex acquisition failed."));
    }

    private List<CrawlTarget> InitialTargets()
    {
        var production = options.BaseUri.Host.Equals("mceindex.com", StringComparison.OrdinalIgnoreCase) ||
                         options.BaseUri.Host.EndsWith(".mceindex.com", StringComparison.OrdinalIgnoreCase);
        return production
            ? ProductionRoutes.Select(route => new CrawlTarget(new Uri(options.BaseUri, $"/{route.Key}"), route.Value)).ToList()
            : [new CrawlTarget(options.BaseUri, "月度总览")];
    }


    private static bool IsRetryable(Exception error) => error is not MceIndexException domain ||
        domain.Code is MceIndexErrorCode.LoadTimeout or MceIndexErrorCode.ExtractionFailed;

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static DateTimeOffset? ParseTimestamp(string? value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { }
    }

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "MCEIndex acquisition failed for {Target}")]
    private static partial void LogUnexpectedAcquisitionFailure(ILogger logger, Exception exception, Uri target);

    private sealed record CrawlTarget(Uri Uri, string Label);
    private sealed record CrawlAttempt(CrawlTarget Target, CrawledPage? Crawled, CrawlFailure? Failure);
}
