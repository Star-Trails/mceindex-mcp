using System.Globalization;
using MceIndex.Mcp.Domain;

namespace MceIndex.Mcp.Services;

internal static class OverviewProjector
{
    public static OverviewSection[] Build(
        IndexCard[] cards,
        ChartData[] charts,
        IReadOnlyDictionary<string, StoredPage>? evidencePages = null)
    {
        var sections = new List<OverviewSection>(6);
        AddIndustry(sections, cards, charts);
        AddEmployment(sections, cards, charts);
        AddFiscal(sections, cards, charts);
        AddRetail(sections, cards, charts);
        AddPrices(sections, cards, charts);
        AddFinancing(sections, cards, charts);
        return [.. sections.Select(section => section with
        {
            Readings = [.. section.Readings.Select(reading => reading with
            {
                Verification = OverviewVerificationProjector.Build(section.Code, reading.Key, section.Period),
            })],
            Notes = OverviewEvidenceProjector.Build(section.Code, evidencePages),
        })];
    }

    private static void AddIndustry(List<OverviewSection> sections, IndexCard[] cards, ChartData[] charts)
    {
        var card = FindCard(cards, "LEI-GDP");
        var chart = FindChart(charts, "新产业占经济多大？");
        var readings = new List<OverviewReading>();
        var share = chart?.Series.FirstOrDefault(series => series.Name == "新产业经济规模占比")?.Points.LastOrDefault()?.Value
            ?? ParseNumber(card?.Value);
        Add(readings, "industryScaleShare", "五大新产业规模占 GDP", share,
            card?.Value ?? FormatPercent(share, 2), "%");
        var average = chart?.Series.FirstOrDefault(series => series.Name == "12M 均线")?.Points.LastOrDefault()?.Value;
        Add(readings, "movingAverage12m", "12M 均线", average, FormatPercent(average, 2), "%");
        var percentile = NumberAfter(card?.Detail, "P");
        Add(readings, "historicalPercentile", "历史分位", percentile,
            percentile is null ? null : $"P{percentile.Value.ToString("0", CultureInfo.InvariantCulture)}");
        AddSection(sections, "LEI-GDP", "新产业占经济多大？", card, chart, readings);
    }

    private static void AddEmployment(List<OverviewSection> sections, IndexCard[] cards, ChartData[] charts)
    {
        var card = FindCard(cards, "LEI-EMP");
        var chart = FindChart(charts, "新产业能吸收多少就业？");
        var readings = new List<OverviewReading>();
        var stockPoint = FindPoint(chart, "理论就业规模", "理论就业规模", 0);
        var stock = PointValue(stockPoint, "理论就业规模") / 10_000d ?? ParseNumber(card?.Value);
        Add(readings, "theoreticalEmploymentStock", "五大新产业理论就业存量", stock,
            card?.Value ?? PointDisplay(stockPoint), "万人");
        var contribution = NumberAfter(card?.Detail, "就业贡献");
        Add(readings, "employmentContribution", "就业续命读数", contribution,
            FormatPercent(contribution, 2), "%");
        AddEmploymentReference(readings, chart, "graduates2026", "2026届高校毕业生", 1);
        AddEmploymentReference(readings, chart, "rideHailingDrivers", "网约车持证司机", 2);
        AddEmploymentReference(readings, chart, "deliveryRiders", "外卖骑手", 3);
        AddSection(sections, "LEI-EMP", "新产业能吸收多少就业？", card, chart, readings);
    }

    private static void AddEmploymentReference(
        List<OverviewReading> readings,
        ChartData? chart,
        string key,
        string label,
        int index)
    {
        var point = FindPoint(chart, "理论就业规模", label, index);
        var value = PointValue(point, label) / 10_000d;
        Add(readings, key, label, value, PointDisplay(point), "万人");
    }

    private static void AddFiscal(List<OverviewSection> sections, IndexCard[] cards, ChartData[] charts)
    {
        var card = FindCard(cards, "LEI-FIS");
        var chart = FindChart(charts, "新产业形成多少净财政贡献？");
        var readings = new List<OverviewReading>();
        var contributionPoint = FindPoint(chart, "估算年化净财政贡献", "净财政贡献", 0);
        var contribution = PointValue(contributionPoint, "净财政贡献") ?? ParseNumber(card?.Value);
        Add(readings, "annualizedNetFiscalContribution", "估算年化净财政贡献", contribution,
            card?.Value ?? PointDisplay(contributionPoint), "亿元");
        var contributionRate = NumberAfter(card?.Detail, "财政贡献");
        Add(readings, "fiscalContribution", "财政续命读数", contributionRate,
            FormatPercent(contributionRate, 2), "%");
        AddFiscalReference(readings, chart, "defenseBudget", "国防预算", 0);
        AddFiscalReference(readings, chart, "debtInterest", "债务付息", 1);
        AddFiscalReference(readings, chart, "educationSpending", "教育支出", 2);
        AddFiscalReference(readings, chart, "landSaleRevenue", "土地出让收入", 3);
        AddFiscalReference(readings, chart, "centralTransfers", "中央转移支付", 4);
        AddSection(sections, "LEI-FIS", "新产业形成多少净财政贡献？", card, chart, readings);
    }

    private static void AddFiscalReference(
        List<OverviewReading> readings,
        ChartData? chart,
        string key,
        string label,
        int index)
    {
        var point = FindPoint(chart, "公共财政量级参照", label, index);
        Add(readings, key, label, PointValue(point, label), PointDisplay(point), "亿元");
    }

    private static void AddRetail(List<OverviewSection> sections, IndexCard[] cards, ChartData[] charts)
    {
        var card = FindCard(cards, "MRS");
        var chart = FindChart(charts, "消费哪里在撑、哪里在拖？");
        var readings = new List<OverviewReading>();
        var meaningful = ParseNumber(card?.Value);
        Add(readings, "meaningfulRetail", "有意义社零同比", meaningful,
            card?.Value ?? FormatPercent(meaningful, 1), "%");
        AddRetailReading(readings, chart, "belowDesignated", "限额以下", 0);
        AddRetailReading(readings, chart, "aboveDesignated", "限额以上", 1);
        AddRetailReading(readings, chart, "durablesPropertyChain", "耐用品/地产链", 2);
        AddSection(sections, "MRS", "消费哪里在撑、哪里在拖？", card, chart, readings);
    }

    private static void AddRetailReading(
        List<OverviewReading> readings,
        ChartData? chart,
        string key,
        string label,
        int index)
    {
        var point = FindPoint(chart, null, label, index);
        Add(readings, key, label, PointValue(point, label), PointDisplay(point), "%");
    }

    private static void AddPrices(List<OverviewSection> sections, IndexCard[] cards, ChartData[] charts)
    {
        var card = FindCard(cards, "MCPI");
        var chart = FindChart(charts, "物价中有多少来自选定短期扰动？");
        var readings = new List<OverviewReading>();
        AddPriceReading(readings, chart, "officialCpi", "官方 CPI", "官方", "CPI", 0);
        AddPriceReading(readings, chart, "meaningfulCpi", "有意义 CPI", "有意义", "CPI", 0);
        AddPriceReading(readings, chart, "officialPpi", "官方 PPI", "官方", "PPI", 1);
        AddPriceReading(readings, chart, "meaningfulPpi", "有意义 PPI", "有意义", "PPI", 1);
        AddSection(sections, "MCPI", "物价中有多少来自选定短期扰动？", card, chart, readings);
    }

    private static void AddPriceReading(
        List<OverviewReading> readings,
        ChartData? chart,
        string key,
        string label,
        string series,
        string category,
        int index)
    {
        var point = FindPoint(chart, series, category, index);
        var value = PointValue(point, category);
        Add(readings, key, label, value, FormatPercent(value, 1), "%");
    }

    private static void AddFinancing(List<OverviewSection> sections, IndexCard[] cards, ChartData[] charts)
    {
        var card = FindCard(cards, "MSF");
        var chart = FindChart(charts, "融资结构的研究折扣有多大？");
        var readings = new List<OverviewReading>();
        AddFinancingReading(readings, chart, "meaningfulSocialFinancing", "有意义社融", "有意义社融");
        AddFinancingReading(readings, chart, "governmentBonds", "政府债券", "政府债券");
        AddFinancingReading(readings, chart, "billsAndOther", "票据及其他", "票据及其他");
        var flow = NumberAfter(card?.Detail, "·");
        Add(readings, "effectiveFinancingMidpoint", "有效融资中点", flow,
            flow is null ? null : $"{flow.Value.ToString("N0", CultureInfo.InvariantCulture)} 亿元", "亿元");
        AddSection(sections, "MSF", "融资结构的研究折扣有多大？", card, chart, readings);
    }

    private static void AddFinancingReading(
        List<OverviewReading> readings,
        ChartData? chart,
        string key,
        string label,
        string seriesName)
    {
        var point = FindPoint(chart, seriesName, null, 0);
        var value = PointValue(point, null);
        Add(readings, key, label, value, FormatPercent(value, 1), "%");
    }

    private static void AddSection(
        List<OverviewSection> sections,
        string code,
        string title,
        IndexCard? card,
        ChartData? chart,
        List<OverviewReading> readings)
    {
        if (readings.Count == 0)
        {
            return;
        }
        sections.Add(new OverviewSection(
            code,
            title,
            card?.Period,
            card?.Description ?? chart?.Description ?? string.Empty,
            [.. readings],
            []));
    }

    private static void Add(
        List<OverviewReading> readings,
        string key,
        string label,
        double? value,
        string? displayValue,
        string? unit = null)
    {
        if (value is null && string.IsNullOrWhiteSpace(displayValue))
        {
            return;
        }
        readings.Add(new OverviewReading(key, label, value, displayValue ?? string.Empty, unit));
    }

    private static IndexCard? FindCard(IndexCard[] cards, string code) =>
        cards.FirstOrDefault(card => card.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    private static ChartData? FindChart(ChartData[] charts, string title) =>
        charts.FirstOrDefault(chart => chart.Title.Equals(title, StringComparison.Ordinal));

    private static ChartPoint? FindPoint(
        ChartData? chart,
        string? seriesName,
        string? category,
        int fallbackIndex)
    {
        var series = seriesName is null
            ? chart?.Series.FirstOrDefault()
            : chart?.Series.FirstOrDefault(item => item.Name == seriesName);
        if (series is null)
        {
            return null;
        }
        if (category is not null)
        {
            var exact = series.Points.FirstOrDefault(point => point.Category == category);
            if (exact is not null)
            {
                return exact;
            }
        }
        return fallbackIndex >= 0 && fallbackIndex < series.Points.Length
            ? series.Points[fallbackIndex]
            : null;
    }

    private static double? PointValue(ChartPoint? point, string? expectedCategory)
    {
        if (point is null)
        {
            return null;
        }
        var categoryValue = ParseNumber(point.Category);
        if (expectedCategory is not null && point.Category == expectedCategory)
        {
            return point.Value ?? categoryValue;
        }
        if (point.Value is null)
        {
            return categoryValue;
        }
        if (categoryValue is null)
        {
            return point.Value;
        }
        if (categoryValue == 0 && point.Value != 0)
        {
            return point.Value;
        }
        if (point.Value == 0 && categoryValue != 0)
        {
            return categoryValue;
        }
        return categoryValue;
    }

    private static string? PointDisplay(ChartPoint? point)
    {
        if (string.IsNullOrWhiteSpace(point?.Text))
        {
            return null;
        }
        return point.Text
            .Replace("<b>", string.Empty, StringComparison.Ordinal)
            .Replace("</b>", string.Empty, StringComparison.Ordinal)
            .Replace("<br>", " · ", StringComparison.Ordinal)
            .Trim();
    }

    private static double? NumberAfter(string? text, string marker)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? null : ParseNumber(text[(index + marker.Length)..]);
    }

    private static double? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        Span<char> buffer = stackalloc char[text.Length];
        var length = 0;
        var started = false;
        foreach (var character in text)
        {
            if (!started && (character == '+' || character == '-' || char.IsDigit(character)))
            {
                started = true;
            }
            if (!started)
            {
                continue;
            }
            if (char.IsDigit(character) || character == '.' || character == '+' || character == '-')
            {
                buffer[length++] = character;
                continue;
            }
            if (character == ',')
            {
                continue;
            }
            break;
        }
        return length > 0 && double.TryParse(
            buffer[..length],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static string? FormatPercent(double? value, int decimals) => value is null
        ? null
        : $"{value.Value.ToString($"F{decimals}", CultureInfo.InvariantCulture)}%";
}
