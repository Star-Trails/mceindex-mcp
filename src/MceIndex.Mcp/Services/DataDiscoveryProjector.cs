using System.Collections.Frozen;
using MceIndex.Mcp.Domain;

namespace MceIndex.Mcp.Services;

internal static class DataDiscoveryProjector
{
    private static readonly FrozenDictionary<string, string> WhyItMattersByCode =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LEI-GDP"] = "观察五大新产业在整体经济中的体量及历史位置。",
            ["LEI-EMP"] = "比较五大新产业可支撑的理论就业规模与毕业生、网约车司机和外卖骑手等人群规模。",
            ["LEI-FIS"] = "观察扣除出口退税和补助后的净财政贡献，并与主要公共财政项目比较量级。",
            ["MRS"] = "识别消费增长由限额以下、限额以上或耐用品与地产链中的哪些部分支撑和拖累。",
            ["MCPI"] = "比较官方通胀与剔除选定短期扰动后的研究读数。",
            ["MSF"] = "观察剔除政府债并折扣票据和企业债后的融资结构。",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static DataDiscoveryResult Build(LatestOverview latest, StoredPageSummary[] pages)
    {
        var topics = new DiscoveryTopic[latest.AtAGlance.Length];
        var readingCount = 0;

        for (var index = 0; index < latest.AtAGlance.Length; index++)
        {
            var section = latest.AtAGlance[index];
            var readings = new DiscoveryReading[section.Readings.Length];
            for (var readingIndex = 0; readingIndex < section.Readings.Length; readingIndex++)
            {
                var reading = section.Readings[readingIndex];
                readings[readingIndex] = new DiscoveryReading(
                    reading.Key,
                    reading.Label,
                    reading.DisplayValue,
                    reading.Unit);
            }

            readingCount += readings.Length;
            topics[index] = new DiscoveryTopic(
                section.Code,
                section.Title,
                section.Period,
                section.Description,
                WhyItMattersByCode.GetValueOrDefault(section.Code, "提供该主题的当前读数和历史比较线索。"),
                section.Title,
                readings,
                "get_indicator",
                section.Code);
        }

        return new DataDiscoveryResult(
            $"当前索引包含 {topics.Length} 个主题、{readingCount} 个结构化读数和 {pages.Length} 个页面。",
            latest.SourceUrl,
            latest.FetchedAt,
            latest.Generation,
            topics,
            pages,
            [.. topics.Select(topic => topic.SuggestedQuestion)],
            [
                new ToolRecommendation("查看六组完整读数、公式、来源和核验状态", "get_latest", null),
                new ToolRecommendation("深入一个核心指标", "get_indicator", "indicator=LEI-GDP"),
                new ToolRecommendation("读取某个栏目的正文、表格或图表", "get_page", "page=Monthly_Overview, view=charts"),
                new ToolRecommendation("按关键词搜索全部栏目", "search_index", "query=新能源汽车"),
            ]);
    }
}
