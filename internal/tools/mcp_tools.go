package tools

import (
	"context"
	"encoding/json"
	"fmt"

	"github.com/Star-Trails/mceindex-mcp/internal/domain"
	"github.com/Star-Trails/mceindex-mcp/internal/services"
	"github.com/mark3labs/mcp-go/mcp"
	"github.com/mark3labs/mcp-go/server"
)

// RegisterTools registers all 7 MCEIndex MCP tools to the server.
func RegisterTools(s *server.MCPServer, svc *services.MceIndexService) {
	// 1. discover_data
	discoverTool := mcp.NewTool("discover_data",
		mcp.WithDescription("数据发现入口。用户尚未指定指标、询问有哪些数据、提出宽泛的中国经济问题或需要选择分析方向时优先调用。返回六个主题、当前读数、历史趋势、改善或恶化判断、指标意义、典型问题、页面目录和建议的后续工具。当前 MCP 服务进程的首次查询会先刷新数据，后续调用只读取本地 SQLite。"),
	)
	s.AddTool(discoverTool, func(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
		res, err := svc.Discover(ctx)
		if err != nil {
			return formatError(err)
		}
		return formatJSON(res)
	})

	// 2. get_latest
	getLatestTool := mcp.NewTool("get_latest",
		mcp.WithDescription("返回月度总览的六组结构化读数及最近 13 个月历史趋势。每组 trend 包含环比、同比、近 3 个月动量、方向、改善或恶化判断及判断依据；对 CPI 和社融等无法仅凭升降判断的指标明确返回 indeterminate。verification 包含可信度、来源、算法、复现、公式和限制条件。当前 MCP 服务进程的首次查询会先刷新数据，后续调用只读取本地 SQLite。"),
	)
	s.AddTool(getLatestTool, func(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
		res, err := svc.GetLatest(ctx)
		if err != nil {
			return formatError(err)
		}
		return formatJSON(res)
	})

	// 3. get_indicator
	getIndicatorTool := mcp.NewTool("get_indicator",
		mcp.WithDescription("按代码或中文名称读取指标，并返回可调历史窗口、环比、同比、近 3 个月动量及改善或恶化判断。当前 MCP 服务进程的首次查询会先刷新数据，后续调用只读取本地 SQLite。代码：LEI-GDP、LEI-EMP、LEI-FIS、MRS、MCPI、MSF。"),
		mcp.WithString("indicator",
			mcp.Required(),
			mcp.Description("指标代码或完整中文名称，例如 LEI-GDP 或 有意义社融"),
		),
		mcp.WithNumber("months",
			mcp.Description("返回最近多少个月的历史序列，范围 2-120，默认 24"),
			mcp.DefaultNumber(24),
		),
	)
	s.AddTool(getIndicatorTool, func(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
		indicator, ok := request.Params.Arguments["indicator"].(string)
		if !ok || indicator == "" {
			return mcp.NewToolResultError("indicator parameter is required"), nil
		}

		months := 24
		if mVal, ok := request.Params.Arguments["months"]; ok {
			if mFloat, ok := mVal.(float64); ok {
				months = int(mFloat)
			}
		}

		res, err := svc.GetIndicator(ctx, indicator, months)
		if err != nil {
			return formatError(err)
		}
		return formatJSON(res)
	})

	// 4. list_pages
	listPagesTool := mcp.NewTool("list_pages",
		mcp.WithDescription("列出本地索引栏目和刷新状态。当前 MCP 服务进程的首次查询会先刷新数据，后续调用只读取本地 SQLite。"),
	)
	s.AddTool(listPagesTool, func(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
		res, err := svc.ListPages(ctx)
		if err != nil {
			return formatError(err)
		}
		return formatJSON(res)
	})

	// 5. get_page
	getPageTool := mcp.NewTool("get_page",
		mcp.WithDescription("按 slug 或中文栏目名读取结构化页面。当前 MCP 服务进程的首次查询会先刷新数据，后续调用只读取本地 SQLite；支持 summary、content、tables、charts。charts 仅返回当前页图表，不夹带指标卡；每个数据点包含规范日期、清洗后的数值和 displayValue。"),
		mcp.WithString("page",
			mcp.Required(),
			mcp.Description("页面 slug 或中文栏目名"),
		),
		mcp.WithString("view",
			mcp.Description("summary、content、tables 或 charts；默认 summary"),
			mcp.Enum("summary", "content", "tables", "charts"),
			mcp.DefaultString("summary"),
		),
		mcp.WithNumber("offset",
			mcp.Description("从 0 开始的结果偏移量"),
			mcp.DefaultNumber(0),
		),
		mcp.WithNumber("limit",
			mcp.Description("每页数量，范围 1-100"),
			mcp.DefaultNumber(50),
		),
	)
	s.AddTool(getPageTool, func(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
		page, ok := request.Params.Arguments["page"].(string)
		if !ok || page == "" {
			return mcp.NewToolResultError("page parameter is required"), nil
		}

		viewStr := "summary"
		if v, ok := request.Params.Arguments["view"].(string); ok && v != "" {
			viewStr = v
		}

		offset := 0
		if oVal, ok := request.Params.Arguments["offset"].(float64); ok {
			offset = int(oVal)
		}

		limit := 50
		if lVal, ok := request.Params.Arguments["limit"].(float64); ok {
			limit = int(lVal)
		}

		res, err := svc.GetPage(ctx, page, domain.PageView(viewStr), offset, limit)
		if err != nil {
			return formatError(err)
		}
		return formatJSON(res)
	})

	// 6. search_index
	searchTool := mcp.NewTool("search_index",
		mcp.WithDescription("使用 SQLite FTS5 trigram 搜索栏目。当前 MCP 服务进程的首次查询会先刷新数据，后续调用只读取本地 SQLite。"),
		mcp.WithString("query",
			mcp.Required(),
			mcp.Description("搜索词，支持中文和英文指标代码"),
		),
		mcp.WithString("page",
			mcp.Description("可选页面 slug 或中文栏目名"),
		),
		mcp.WithString("kind",
			mcp.Description("可选内容类型：heading、metric、text、table 或 chart"),
			mcp.Enum("heading", "metric", "text", "table", "chart"),
		),
		mcp.WithString("mode",
			mcp.Description("and 或 phrase；默认 and"),
			mcp.Enum("and", "phrase"),
			mcp.DefaultString("and"),
		),
		mcp.WithNumber("offset",
			mcp.Description("从 0 开始的结果偏移量"),
			mcp.DefaultNumber(0),
		),
		mcp.WithNumber("limit",
			mcp.Description("每页数量，范围 1-50"),
			mcp.DefaultNumber(20),
		),
	)
	s.AddTool(searchTool, func(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
		query, ok := request.Params.Arguments["query"].(string)
		if !ok || query == "" {
			return mcp.NewToolResultError("query parameter is required"), nil
		}

		var pagePtr *string
		if p, ok := request.Params.Arguments["page"].(string); ok && p != "" {
			pagePtr = &p
		}

		var kindPtr *domain.ContentKind
		if k, ok := request.Params.Arguments["kind"].(string); ok && k != "" {
			ck := domain.ContentKind(k)
			kindPtr = &ck
		}

		mode := domain.SearchModeAnd
		if m, ok := request.Params.Arguments["mode"].(string); ok && m == "phrase" {
			mode = domain.SearchModePhrase
		}

		offset := 0
		if oVal, ok := request.Params.Arguments["offset"].(float64); ok {
			offset = int(oVal)
		}

		limit := 20
		if lVal, ok := request.Params.Arguments["limit"].(float64); ok {
			limit = int(lVal)
		}

		res, err := svc.Search(ctx, query, pagePtr, kindPtr, mode, offset, limit)
		if err != nil {
			return formatError(err)
		}
		return formatJSON(res)
	})

	// 7. refresh_index
	refreshTool := mcp.NewTool("refresh_index",
		mcp.WithDescription("低频全量抓取公开页面并以单个事务更新本地 SQLite；默认遵守 24 小时刷新间隔，force=true 仍不能绕过 60 秒硬冷却。"),
		mcp.WithBoolean("force",
			mcp.Description("false 仅在刷新间隔到期时更新；true 绕过 24 小时间隔，但仍遵守 60 秒硬冷却"),
			mcp.DefaultBool(false),
		),
	)
	s.AddTool(refreshTool, func(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
		force := false
		if fVal, ok := request.Params.Arguments["force"].(bool); ok {
			force = fVal
		}

		res, err := svc.Refresh(ctx, force)
		if err != nil {
			return formatError(err)
		}
		return formatJSON(res)
	})
}

func formatJSON(v interface{}) (*mcp.CallToolResult, error) {
	bytes, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return nil, err
	}
	return mcp.NewToolResultText(string(bytes)), nil
}

func formatError(err error) (*mcp.CallToolResult, error) {
	if domainErr, ok := err.(*domain.MceIndexError); ok {
		return mcp.NewToolResultError(domainErr.ToProtocolEnvelope()), nil
	}
	envelope := fmt.Sprintf(`{"error":{"code":"INTERNAL_ERROR","message":%q}}`, err.Error())
	return mcp.NewToolResultError(envelope), nil
}
