using System.Collections.Frozen;

namespace MceIndex.Mcp.Domain;

public enum NavigationKind
{
    Link,
    Button,
    Tab,
}

public enum ContentKind
{
    Heading,
    Metric,
    Text,
    Table,
    Chart,
}

public enum SearchMode
{
    And,
    Phrase,
}

public enum PageView
{
    Summary,
    Content,
    Tables,
    Charts,
}

public enum RefreshOutcome
{
    Completed,
    Partial,
    Skipped,
}

public sealed record Heading(int Level, string Text);

public sealed record NavigationItem(string Text, NavigationKind Kind, string? Url = null);

public sealed record Metric(string Label, string Value, string? Delta = null, string? Help = null);

public sealed record DataTable(string[] Headers, string[][] Rows, string? Title = null);
public sealed record ChartPoint(
    string? Category,
    double? Value,
    string? Text = null,
    string? DisplayValue = null);

public sealed record ChartSeries(string? Name, string? Type, ChartPoint[] Points);

public sealed record ChartData(
    string Title,
    string Description,
    string[] Notes,
    string? XAxisTitle,
    string? YAxisTitle,
    ChartSeries[] Series);

internal sealed record IndicatorDefinition(string Code, string Label, string Description);

internal static class IndicatorCatalog
{
    private static readonly FrozenDictionary<string, IndicatorDefinition> ByCode =
        new Dictionary<string, IndicatorDefinition>(StringComparer.Ordinal)
        {
            ["LEI-GDP"] = new(
                "LEI-GDP",
                "五大新产业规模占 GDP",
                "几乎是中国经济中最有活力和被寄予厚望的部分；黄色数字是按海关HS范围锁定、并对新能源汽车—电池等行业内部交易作合并抵销后估算的产业规模占GDP比重。"),
            ["LEI-EMP"] = new(
                "LEI-EMP",
                "五大新产业理论就业贡献",
                "黄色条表示五大新产业能够支撑的理论就业存量，不是当月新增岗位；把它同高校毕业生、网约车司机和外卖骑手等人群规模相比，就能看出它在中国就业大盘中是什么量级。"),
            ["LEI-FIS"] = new(
                "LEI-FIS",
                "五大新产业净财政贡献",
                "主条表示扣除出口退税和补助后的五大新产业估算年化净财政贡献；正值为黄色，零值及负值为红色。负值表示当月折年后的退税和补助高于毛税收现金。下方公共财政项目只作量级参照。"),
            ["MRS"] = new(
                "MRS",
                "有意义社零",
                "限额以上主要是达到国家统计收入门槛的商场、连锁超市和品牌餐饮等较大经营单位，限额以下主要是小店、个体户和小餐馆，耐用品／地产链包括汽车、家电、建材和家具；横条向右表示正在增长、支撑消费，向左表示正在下降、拖累消费。"),
            ["MCPI"] = new(
                "MCPI",
                "有意义 CPI",
                "蓝点是官方 CPI/PPI，黄点是按研究公式剔除选定能源、黄金、鲜菜鲜果和上游投入冲击后的读数；蓝黄距离表示这些调整项的净影响，不是对“官方水分”或真实需求强弱的直接测量。"),
            ["MSF"] = new(
                "MSF",
                "有意义社融",
                "黄色部分是按既定规则剔除政府债，并对票据和企业债施加折扣后的研究情景；占比越大只表示在同一规则下保留值较多，不能证明资金最终用途或实际进入企业和居民。"),
        }.ToFrozenDictionary(StringComparer.Ordinal);


    public static bool TryGet(string code, out IndicatorDefinition definition)
        => ByCode.TryGetValue(code, out definition!);
}


public sealed record PageSnapshot
{
    public required string SourceUrl { get; init; }
    public required DateTimeOffset FetchedAt { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? AppTitle { get; init; }
    public required Heading[] Headings { get; init; }
    public required NavigationItem[] Navigation { get; init; }
    public required Metric[] Metrics { get; init; }
    public required DataTable[] Tables { get; init; }
    public IndexCard[] Cards { get; init; } = [];
    public ChartData[] Charts { get; init; } = [];
    public required string[] Text { get; init; }
}

public sealed record CrawledPage(PageSnapshot Snapshot, string[] HtmlDocuments);

public sealed record IndexCard(
    string Code,
    string Label,
    string Value,
    string? Detail,
    string? Period,
    string Description);

public sealed record StoredPageSummary(
    string Slug,
    string Label,
    string Title,
    string SourceUrl,
    DateTimeOffset FetchedAt,
    DateTimeOffset LastCheckedAt,
    int TextCount,
    long Generation);

public sealed record StoredPage(StoredPageSummary Summary, PageSnapshot Snapshot);

public sealed record PageContentItem(long Id, ContentKind Kind, string Text, int Sequence);

public sealed record SearchHit(
    long EntryId,
    string PageSlug,
    string PageLabel,
    string SourceUrl,
    DateTimeOffset FetchedAt,
    ContentKind Kind,
    string Text,
    double Rank);

public sealed record CrawlFailure(string Url, string Code, string Message);

public sealed record CrawlReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    RefreshOutcome Outcome,
    int PagesChecked,
    int ChangedPages,
    int UnchangedPages,
    CrawlFailure[] Failures);

public sealed record IndexStatus(
    string DatabasePath,
    int SchemaVersion,
    int PageCount,
    long Generation,
    DateTimeOffset? LastSuccessfulRefresh,
    DateTimeOffset? LastAttempt,
    bool Stale,
    bool Refreshing,
    string? LastError);

public enum OverviewNoteKind
{
    Formula,
    DataSource,
    Methodology,
    Caveat,
}

public sealed record OverviewNote(
    OverviewNoteKind Kind,
    string Text,
    string SourcePage,
    string SourceUrl);

public enum ConclusionStatus
{
    Verified,
    PartiallyVerified,
    NotFound,
    UnverifiedEstimate,
    NotAssessed,
}

public enum EvidenceStatus
{
    Verified,
    Partial,
    Missing,
    NotAssessed,
}

public enum AlgorithmStatus
{
    Published,
    Inferred,
    Missing,
    NotApplicable,
    NotAssessed,
}

public enum ReproductionStatus
{
    Verified,
    Conditional,
    Impossible,
    DirectSource,
    NotAssessed,
}

public sealed record EvidenceSource(
    string Publisher,
    string Title,
    string Url,
    string? Period = null,
    string? Detail = null);
public enum ConceptualProvenanceStatus
{
    Verified,
    PartiallyVerified,
    NotFound,
    NotAssessed,
}

public sealed record ConceptualProvenance(
    ConceptualProvenanceStatus Status,
    string Summary,
    EvidenceSource[] Sources,
    string[] Limitations);


public sealed record ConclusionVerification(
    string AuditedPeriod,
    bool AppliesToCurrentPeriod,
    bool DataUpdated,
    ConclusionStatus Status,
    EvidenceStatus SourceStatus,
    AlgorithmStatus AlgorithmStatus,
    ReproductionStatus ReproductionStatus,
    bool IndependentExactMatch,
    string Summary,
    string? Formula,
    string? Reproduction,
    EvidenceSource[] Sources,
    string[] Limitations,
    ConceptualProvenance? ConceptualProvenance = null);

public enum TrendDirection
{
    Rising,
    Falling,
    Stable,
    Mixed,
    InsufficientData,
}

public enum EconomicAssessment
{
    Improving,
    Deteriorating,
    Stable,
    Mixed,
    Indeterminate,
    InsufficientData,
}

public sealed record HistoricalObservation(string Period, double Value);

public sealed record IndicatorTrend(
    string SeriesKey,
    string Label,
    string Unit,
    int AvailablePeriods,
    HistoricalObservation[] History,
    string CurrentPeriod,
    double Current,
    string? PreviousPeriod,
    double? Previous,
    double? MonthOverMonthChange,
    string? YearAgoPeriod,
    double? YearAgo,
    double? YearOverYearChange,
    double? RecentThreeMonthAverage,
    double? PreviousThreeMonthAverage,
    double? ThreeMonthMomentum,
    TrendDirection Direction,
    EconomicAssessment Assessment,
    string Basis,
    string Interpretation);

public sealed record OverviewReading(
    string Key,
    string Label,
    double? Value,
    string DisplayValue,
    string? Unit = null,
    ConclusionVerification? Verification = null);
public sealed record OverviewSection(
    string Code,
    string Title,
    string? Period,
    string Description,
    OverviewReading[] Readings,
    OverviewNote[] Notes,
    IndicatorTrend? Trend = null);

public sealed record LatestOverview(
    string SourceUrl,
    DateTimeOffset FetchedAt,
    long Generation,
    OverviewSection[] AtAGlance,
    IndexCard[] Cards,
    string[] Headings,
    string[] Notes);

public sealed record PageListResult(IndexStatus Status, StoredPageSummary[] Pages);

public sealed record PageResult(
    StoredPageSummary Page,
    PageView View,
    IndexCard[] Cards,
    Heading[] Headings,
    Metric[] Metrics,
    PageContentItem[] Items,
    DataTable[] Tables,
    ChartData[] Charts,
    int Offset,
    int? NextOffset,
    bool HasMore);

public sealed record SearchResult(
    string Query,
    string? Page,
    ContentKind? Kind,
    SearchHit[] Hits,
    int Offset,
    int? NextOffset,
    bool HasMore,
    long Generation);

public sealed record DiscoveryReading(
    string Key,
    string Label,
    string DisplayValue,
    string? Unit);

public sealed record DiscoveryTopic(
    string Code,
    string Title,
    string? Period,
    string Meaning,
    string WhyItMatters,
    string SuggestedQuestion,
    DiscoveryReading[] CurrentReadings,
    string DetailTool,
    string DetailArgument,
    IndicatorTrend? Trend = null);

public sealed record ToolRecommendation(
    string Need,
    string Tool,
    string? Example);

public sealed record DataDiscoveryResult(
    string Summary,
    string SourceUrl,
    DateTimeOffset FetchedAt,
    long Generation,
    DiscoveryTopic[] Topics,
    StoredPageSummary[] Pages,
    string[] SuggestedQuestions,
    ToolRecommendation[] NextSteps);

public sealed record RefreshResult(CrawlReport Report, IndexStatus Status);

public sealed record IndicatorResult(
    IndexCard Indicator,
    string SourceUrl,
    DateTimeOffset FetchedAt,
    long Generation,
    IndicatorTrend? Trend);
