using MceIndex.Mcp.Domain;
using MceIndex.Mcp.Services;

namespace MceIndex.Mcp.Tests;

public sealed class ChartResponseProjectorTests
{
    [Fact]
    public void CleansTitlesNumbersDisplayValuesAndDates()
    {
        var charts = ChartResponseProjector.Project(
        [
            new ChartData(
                "<b><b></b></b>",
                "MCEIndex 页面中的“<b><b></b></b>”图表。",
                ["<b>月度</b>数据"],
                "月份",
                "三项流量（亿元）",
                [
                    new ChartSeries(
                        "<b>有意义社融</b>",
                        "bar",
                        [
                            new ChartPoint("2026-06-01T00:00:00.000000", 0.30000000000000004),
                            new ChartPoint("2026-06-15T12:00:00.000000+00:00", 68.15178571428572, "+68.2%"),
                        ]),
                ]),
        ]);

        var chart = Assert.Single(charts);
        Assert.Equal("三项流量（亿元）", chart.Title);
        Assert.Equal("MCEIndex 页面中的“三项流量（亿元）”图表。", chart.Description);
        Assert.Equal("月度数据", Assert.Single(chart.Notes));
        var series = Assert.Single(chart.Series);
        Assert.Equal("有意义社融", series.Name);
        Assert.Collection(
            series.Points,
            point =>
            {
                Assert.Equal("2026-06", point.Category);
                Assert.Equal(0.3, point.Value);
                Assert.Equal("0.3", point.DisplayValue);
            },
            point =>
            {
                Assert.Equal("2026-06-15T12:00:00.0000000+00:00", point.Category);
                Assert.Equal(68.1517857142857, point.Value);
                Assert.Equal("+68.2%", point.DisplayValue);
            });
    }
}
