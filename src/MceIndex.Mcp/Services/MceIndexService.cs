using MceIndex.Mcp.Domain;
using MceIndex.Mcp.Persistence;

namespace MceIndex.Mcp.Services;

public sealed class MceIndexService(MceIndexStore store, RefreshCoordinator refreshCoordinator)
{
    private readonly object sessionGate = new();
    private Task<CrawlReport>? sessionInitialization;
    public async Task<LatestOverview> GetLatestAsync(CancellationToken cancellationToken)
    {
        await EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        var overview = store.FindPage("Monthly_Overview") ?? store.FindPage("月度总览")
            ?? throw new MceIndexException(MceIndexErrorCode.IndexEmpty,
                "The local index does not contain the monthly overview.");
        var cards = store.GetCards(overview.Summary.Slug);
        var evidencePages = GetEvidencePages();
        var sections = OverviewProjector.Build(cards, overview.Snapshot.Charts, evidencePages);
        for (var index = 0; index < sections.Length; index++)
        {
            sections[index] = sections[index] with
            {
                Trend = IndicatorTrendProjector.Build(sections[index].Code, evidencePages, 13),
            };
        }
        return new LatestOverview(
            overview.Summary.SourceUrl,
            overview.Summary.FetchedAt,
            refreshCoordinator.GetStatus().Generation,
            sections,
            cards,
            overview.Snapshot.Headings.Select(heading => heading.Text).ToArray(),
            overview.Snapshot.Text.Where(value => value.Length >= 20).Take(12).ToArray());
    }

    public async Task<DataDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        var latest = await GetLatestAsync(cancellationToken).ConfigureAwait(false);
        return DataDiscoveryProjector.Build(latest, store.ListPages());
    }

    public async Task<IndicatorResult> GetIndicatorAsync(
        string query,
        int months,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 100)
        {
            throw new MceIndexException(
                MceIndexErrorCode.InvalidConfiguration,
                "Indicator must be a code or Chinese label between 1 and 100 characters.");
        }
        if (months is < 2 or > 120)
        {
            throw new MceIndexException(
                MceIndexErrorCode.InvalidConfiguration,
                "History window must be between 2 and 120 months.");
        }


        var latest = await GetLatestAsync(cancellationToken).ConfigureAwait(false);
        var indicator = latest.Cards.FirstOrDefault(card =>
            card.Code.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase) ||
            card.Label.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase));
        if (indicator is null)
        {
            throw new MceIndexException(
                MceIndexErrorCode.IndicatorNotFound,
                $"Indicator {query} was not found in the local index.",
                new Dictionary<string, object?>
                {
                    ["available"] = latest.Cards.Select(card => card.Code).ToArray(),
                });
        }

        var trend = IndicatorTrendProjector.Build(indicator.Code, GetEvidencePages(), months);
        return new IndicatorResult(indicator, latest.SourceUrl, latest.FetchedAt, latest.Generation, trend);
    }

    public async Task<PageListResult> ListPagesAsync(CancellationToken cancellationToken)
    {
        await EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        return new PageListResult(refreshCoordinator.GetStatus(), store.ListPages());
    }

    public async Task<PageResult> GetPageAsync(
        string page,
        PageView view,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidatePageRequest(page, offset, limit);
        await EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        var stored = FindPage(page);
        var cards = store.GetCards(stored.Summary.Slug);
        if (view == PageView.Summary)
        {
            return new PageResult(stored.Summary, view, cards, stored.Snapshot.Headings, stored.Snapshot.Metrics,
                [], [], [], 0, null, false);
        }

        if (view == PageView.Tables)
        {
            var window = stored.Snapshot.Tables.Skip(offset).Take(limit + 1).ToArray();
            var hasMore = window.Length > limit;
            return new PageResult(stored.Summary, view, cards, stored.Snapshot.Headings, stored.Snapshot.Metrics,
                [], window.Take(limit).ToArray(), [], offset, hasMore ? offset + limit : null, hasMore);
        }

        if (view == PageView.Charts)
        {
            var window = stored.Snapshot.Charts.Skip(offset).Take(limit + 1).ToArray();
            var hasMore = window.Length > limit;
            return new PageResult(stored.Summary, view, cards, stored.Snapshot.Headings, stored.Snapshot.Metrics,
                [], [], window.Take(limit).ToArray(), offset, hasMore ? offset + limit : null, hasMore);
        }

        var entries = store.GetContent(stored.Summary.Slug, view, offset, limit + 1);
        var contentHasMore = entries.Length > limit;
        return new PageResult(stored.Summary, view, cards, stored.Snapshot.Headings, stored.Snapshot.Metrics,
            entries.Take(limit).ToArray(), [], [], offset, contentHasMore ? offset + limit : null, contentHasMore);
    }

    public async Task<SearchResult> SearchAsync(
        string query,
        string? page,
        ContentKind? kind,
        SearchMode mode,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new MceIndexException(MceIndexErrorCode.InvalidConfiguration, "Search query must not be empty.");
        }
        if (query.Length > 500 || offset < 0 || offset > 10_000 || limit is < 1 or > 50)
        {
            throw new MceIndexException(MceIndexErrorCode.InvalidConfiguration,
                "Search query must be at most 500 characters; offset must be 0-10000 and limit must be 1-50.");
        }

        await EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        var pageSlug = page is null ? null : FindPage(page).Summary.Slug;
        var hits = store.Search(query, pageSlug, kind, offset, limit + 1, mode);
        var hasMore = hits.Length > limit;
        return new SearchResult(query, pageSlug, kind, hits.Take(limit).ToArray(), offset,
            hasMore ? offset + limit : null, hasMore, refreshCoordinator.GetStatus().Generation);
    }

    public async Task<RefreshResult> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        var report = await refreshCoordinator.RefreshAsync(force, cancellationToken).ConfigureAwait(false);
        lock (sessionGate)
        {
            sessionInitialization = Task.FromResult(report);
        }
        return new RefreshResult(report, refreshCoordinator.GetStatus());
    }


    private Dictionary<string, StoredPage> GetEvidencePages()
    {
        var pages = new Dictionary<string, StoredPage>(StringComparer.Ordinal);
        foreach (var slug in new[]
        {
            "LI_Monthly",
            "Meaningful_Retail",
            "Meaningful_CPI_PPI",
            "Meaningful_TSF",
        })
        {
            if (store.FindPage(slug) is { } page)
            {
                pages.Add(slug, page);
            }
        }
        return pages;
    }

    private async Task EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        Task<CrawlReport> initialization;
        lock (sessionGate)
        {
            sessionInitialization ??= refreshCoordinator.RefreshAsync(true, CancellationToken.None);
            initialization = sessionInitialization;
        }

        try
        {
            await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException && store.CountPages() > 0)
        {
            // Preserve the last successful local index for the rest of this MCP session.
        }

        if (store.CountPages() == 0)
        {
            throw new MceIndexException(MceIndexErrorCode.IndexEmpty,
                "No MCEIndex pages are available after the session refresh. Call refresh_index after the hard cooldown to retry.");
        }
    }

    private StoredPage FindPage(string query)
    {
        var result = store.FindPage(query.Trim());
        if (result is not null)
        {
            return result;
        }

        throw new MceIndexException(
            MceIndexErrorCode.PageNotFound,
            $"Page {query} was not found in the local index.",
            new Dictionary<string, object?> { ["available"] = store.ListPages().Select(candidate => candidate.Slug).ToArray() });
    }

    private static void ValidatePageRequest(string page, int offset, int limit)
    {
        if (string.IsNullOrWhiteSpace(page) || page.Length > 200 || offset < 0 || offset > 10_000 || limit is < 1 or > 100)
        {
            throw new MceIndexException(MceIndexErrorCode.InvalidConfiguration,
                "Page must be 1-200 characters; offset must be 0-10000 and limit must be 1-100.");
        }
    }
}
