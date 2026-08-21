package projectors

import (
	"fmt"

	"github.com/Star-Trails/mceindex-mcp/internal/domain"
)

var whyItMattersByCode = map[string]string{
	"LEI-GDP": "观察五大新产业在整体经济中的体量及历史位置。",
	"LEI-EMP": "比较五大新产业可支撑的理论就业规模与毕业生、网约车司机和外卖骑手等人群规模。",
	"LEI-FIS": "观察扣除出口退税和补助后的净财政贡献，并与主要公共财政项目比较量级。",
	"MRS":     "识别消费增长由限额以下、限额以上或耐用品与地产链中的哪些部分支撑和拖累。",
	"MCPI":    "比较官方通胀与剔除选定短期扰动后的研究读数。",
	"MSF":     "观察剔除政府债并折扣票据和企业债后的融资结构。",
}

// BuildDataDiscovery constructs the comprehensive data discovery result for the discover_data tool.
func BuildDataDiscovery(latest *domain.LatestOverview, pages []domain.StoredPageSummary) domain.DataDiscoveryResult {
	topics := make([]domain.DiscoveryTopic, len(latest.AtAGlance))
	readingCount := 0

	for i, sec := range latest.AtAGlance {
		readings := make([]domain.DiscoveryReading, len(sec.Readings))
		for rIdx, r := range sec.Readings {
			var u *string
			if r.Unit != nil {
				u = r.Unit
			}
			readings[rIdx] = domain.DiscoveryReading{
				Key:          r.Key,
				Label:        r.Label,
				DisplayValue: r.DisplayValue,
				Unit:         u,
			}
		}
		readingCount += len(readings)

		why, ok := whyItMattersByCode[sec.Code]
		if !ok {
			why = "提供该主题的当前读数和历史比较线索。"
		}

		topics[i] = domain.DiscoveryTopic{
			Code:              sec.Code,
			Title:             sec.Title,
			Period:            sec.Period,
			Meaning:           sec.Description,
			WhyItMatters:      why,
			SuggestedQuestion: sec.Title,
			CurrentReadings:   readings,
			DetailTool:        "get_indicator",
			DetailArgument:    sec.Code,
			Trend:             sec.Trend,
		}
	}

	suggestedQuestions := make([]string, len(topics))
	for i, t := range topics {
		suggestedQuestions[i] = t.SuggestedQuestion
	}

	nextSteps := []domain.ToolRecommendation{
		{
			Need: "查看六组完整读数、历史趋势、公式、来源和核验状态",
			Tool: "get_latest",
		},
		{
			Need:    "深入一个核心指标并调整历史窗口",
			Tool:    "get_indicator",
			Example: new("indicator=LEI-GDP, months=24"),
		},
		{
			Need:    "读取某个栏目的正文、表格或图表",
			Tool:    "get_page",
			Example: new("page=Monthly_Overview, view=charts"),
		},
		{
			Need:    "按关键词搜索全部栏目",
			Tool:    "search_index",
			Example: new("query=新能源汽车"),
		},
	}

	summary := fmt.Sprintf("当前索引包含 %d 个主题、%d 个结构化读数和 %d 个页面，并提供历史趋势及改善或恶化判断。", len(topics), readingCount, len(pages))

	return domain.DataDiscoveryResult{
		Summary:            summary,
		SourceURL:          latest.SourceURL,
		FetchedAt:          latest.FetchedAt,
		Generation:         latest.Generation,
		Topics:             topics,
		Pages:              pages,
		SuggestedQuestions: suggestedQuestions,
		NextSteps:          nextSteps,
	}
}
