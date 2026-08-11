using System.Text.Encodings.Web;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using MceIndex.Mcp.Domain;
using MceIndex.Mcp.Services;

namespace MceIndex.Mcp.Tools;

[McpServerToolType]
public sealed class MceIndexTools(MceIndexService service)
{
    private static readonly JsonSerializerOptions ErrorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "discover_data", Title = "发现可查询的中国经济数据", ReadOnly = false,
        Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("数据发现入口。用户尚未指定指标、询问有哪些数据、提出宽泛的中国经济问题或需要选择分析方向时优先调用。返回六个主题、当前读数、历史趋势、改善或恶化判断、指标意义、典型问题、页面目录和建议的后续工具。当前 MCP 服务进程的首次查询会先刷新数据，后续调用只读取本地 SQLite。")]
    public Task<DataDiscoveryResult> DiscoverDataAsync(CancellationToken cancellationToken) =>
        InvokeAsync(() => service.DiscoverAsync(cancellationToken));

    [McpServerTool(Name = "get_latest", Title = "获取最新中国经济指数", ReadOnly = false,
        Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("返回月度总览的六组结构化读数及最近 13 个月历史趋势。每组 trend 包含环比、同比、近 3 个月动量、方向、改善或恶化判断及判断依据；对 CPI 和社融等无法仅凭升降判断的指标明确返回 indeterminate。verification 包含可信度、来源、算法、复现、公式和限制条件。当前 MCP 服务进程的首次查询会先刷新数据，后续调用只读取本地 SQLite。")]
    public Task<LatestOverview> GetLatestAsync(CancellationToken cancellationToken) =>
        InvokeAsync(() => service.GetLatestAsync(cancellationToken));

    [McpServerTool(Name = "get_indicator", Title = "读取单项中国经济指标", ReadOnly = false,
        Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("按代码或中文名称读取指标，并返回可调历史窗口、环比、同比、近 3 个月动量及改善或恶化判断。当前 MCP 服务进程的首次查询会先刷新数据，后续调用只读取本地 SQLite。代码：LEI-GDP、LEI-EMP、LEI-FIS、MRS、MCPI、MSF。")]
    public Task<IndicatorResult> GetIndicatorAsync(
        [Description("指标代码或完整中文名称，例如 LEI-GDP 或 有意义社融")] string indicator,
        [Description("返回最近多少个月的历史序列，范围 2-120，默认 24")] int months = 24,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => service.GetIndicatorAsync(indicator, months, cancellationToken));

    [McpServerTool(Name = "list_pages", Title = "浏览 MCEIndex 页面目录", ReadOnly = false,
        Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("列出本地索引栏目和刷新状态。当前 MCP 服务进程的首次查询会先刷新数据，后续调用只读取本地 SQLite。")]
    public Task<PageListResult> ListPagesAsync(CancellationToken cancellationToken) =>
        InvokeAsync(() => service.ListPagesAsync(cancellationToken));

    [McpServerTool(Name = "get_page", Title = "读取指数栏目", ReadOnly = false,
        Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("按 slug 或中文栏目名读取结构化页面。当前 MCP 服务进程的首次查询会先刷新数据，后续调用只读取本地 SQLite；支持 summary、content、tables、charts。")]
    public Task<PageResult> GetPageAsync(
        [Description("页面 slug 或中文栏目名")] string page,
        [Description("summary、content、tables 或 charts；默认 summary")] PageView view = PageView.Summary,
        [Description("从 0 开始的结果偏移量")] int offset = 0,
        [Description("每页数量，范围 1-100")] int limit = 50,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => service.GetPageAsync(page, view, offset, limit, cancellationToken));

    [McpServerTool(Name = "search_index", Title = "搜索中国经济指数", ReadOnly = false,
        Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("使用 SQLite FTS5 trigram 搜索栏目。当前 MCP 服务进程的首次查询会先刷新数据，后续调用只读取本地 SQLite。")]
    public Task<SearchResult> SearchIndexAsync(
        [Description("搜索词，支持中文和英文指标代码")] string query,
        [Description("可选页面 slug 或中文栏目名")] string? page = null,
        [Description("可选内容类型：heading、metric、text、table 或 chart")] ContentKind? kind = null,
        [Description("and 或 phrase；默认 and")] SearchMode mode = SearchMode.And,
        [Description("从 0 开始的结果偏移量")] int offset = 0,
        [Description("每页数量，范围 1-50")] int limit = 20,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => service.SearchAsync(query, page, kind, mode, offset, limit, cancellationToken));

    [McpServerTool(Name = "refresh_index", Title = "刷新 MCEIndex 本地索引", ReadOnly = false,
        Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("低频全量抓取公开页面并以单个事务更新本地 SQLite；默认遵守 24 小时刷新间隔，force=true 仍不能绕过 60 秒硬冷却。")]
    public Task<RefreshResult> RefreshIndexAsync(
        [Description("false 仅在刷新间隔到期时更新；true 绕过 24 小时间隔，但仍遵守 60 秒硬冷却")] bool force = false,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => service.RefreshAsync(force, cancellationToken));

    private static async Task<T> InvokeAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (MceIndexException error)
        {
            var envelope = new
            {
                error = new
                {
                    code = MceIndexErrorCodes.ToProtocolCode(error.Code),
                    message = error.Message,
                    details = error.Details,
                },
            };
            throw new McpException(JsonSerializer.Serialize(envelope, ErrorJsonOptions), error);
        }
    }
}
