using System.Globalization;
using MceIndex.Mcp.Domain;
using MceIndex.Mcp.Services;

namespace MceIndex.Mcp.Tests;

public sealed class IndicatorTrendProjectorTests
{
    [Fact]
    public void BuildsHistoryComparisonsAndImprovementAssessment()
    {
        var points = Enumerable.Range(0, 14)
            .Select(index => new ChartPoint(new DateTime(2025, 1, 1).AddMonths(index).ToString("yyyy-MM", CultureInfo.InvariantCulture), index + 1))
            .ToArray();
        var pages = Pages(
            "LI_Monthly",
            "产业规模占比 · 图表 1",
            "产业规模占GDP比重",
            points);

        var trend = IndicatorTrendProjector.Build("LEI-GDP", pages, 12);

        Assert.NotNull(trend);
        Assert.Equal(14, trend.AvailablePeriods);
        Assert.Equal(12, trend.History.Length);
        Assert.Equal("2026-02", trend.CurrentPeriod);
        Assert.Equal(14, trend.Current);
        Assert.Equal(1, trend.MonthOverMonthChange);
        Assert.Equal(12, trend.YearOverYearChange);
        Assert.Equal(3, trend.ThreeMonthMomentum);
        Assert.Equal(TrendDirection.Rising, trend.Direction);
        Assert.Equal(EconomicAssessment.Improving, trend.Assessment);
        Assert.Contains("较上月 +1 %", trend.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsMixedWhenShortAndAnnualSignalsDisagree()
    {
        double[] values = [12, 9, 9, 9, 9, 9, 9, 9, 9, 9, 8, 8, 10];
        var points = values.Select((value, index) =>
                new ChartPoint(new DateTime(2025, 1, 1).AddMonths(index).ToString("yyyy-MM", CultureInfo.InvariantCulture), value))
            .ToArray();
        var pages = Pages(
            "Meaningful_Retail",
            "<b><b></b></b>",
            "有意义社零同比",
            points);

        var trend = IndicatorTrendProjector.Build("MRS", pages, 24);

        Assert.NotNull(trend);
        Assert.Equal(2, trend.MonthOverMonthChange);
        Assert.Equal(-2, trend.YearOverYearChange);
        Assert.Equal(TrendDirection.Mixed, trend.Direction);
        Assert.Equal(EconomicAssessment.Mixed, trend.Assessment);
    }

    [Fact]
    public void KeepsContextDependentIndicatorsIndeterminate()
    {
        var points = Enumerable.Range(0, 13)
            .Select(index => new ChartPoint(
                new DateTime(2025, 1, 1).AddMonths(index).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
                index / 10d))
            .ToArray();
        var pages = Pages(
            "Meaningful_CPI_PPI",
            "<b><b></b></b>",
            "有意义 CPI",
            points);

        var trend = IndicatorTrendProjector.Build("MCPI", pages, 6);

        Assert.NotNull(trend);
        Assert.Equal("2026-01", trend.CurrentPeriod);
        Assert.Equal(TrendDirection.Rising, trend.Direction);
        Assert.Equal(EconomicAssessment.Indeterminate, trend.Assessment);
        Assert.Equal(6, trend.History.Length);
        Assert.Contains("不能机械解释", trend.Interpretation, StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnsNullWhenHistoricalSourceIsUnavailable()
    {
        var trend = IndicatorTrendProjector.Build(
            "LEI-GDP",
            new Dictionary<string, StoredPage>(StringComparer.Ordinal),
            12);

        Assert.Null(trend);
    }

    private static Dictionary<string, StoredPage> Pages(
        string slug,
        string chartTitle,
        string seriesName,
        ChartPoint[] points)
    {
        var fetchedAt = new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);
        var snapshot = new PageSnapshot
        {
            SourceUrl = $"https://mceindex.com/{slug}",
            FetchedAt = fetchedAt,
            Title = slug,
            Headings = [],
            Navigation = [],
            Metrics = [],
            Tables = [],
            Charts =
            [
                new ChartData(chartTitle, string.Empty, [], null, null,
                    [new ChartSeries(seriesName, "scatter", points)]),
            ],
            Text = [],
        };
        var summary = new StoredPageSummary(
            slug,
            slug,
            slug,
            snapshot.SourceUrl,
            fetchedAt,
            fetchedAt,
            0,
            1);
        return new Dictionary<string, StoredPage>(StringComparer.Ordinal)
        {
            [slug] = new StoredPage(summary, snapshot),
        };
    }
}
