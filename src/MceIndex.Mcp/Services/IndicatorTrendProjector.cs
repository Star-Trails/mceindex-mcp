using System.Collections.Frozen;
using System.Globalization;
using MceIndex.Mcp.Domain;

namespace MceIndex.Mcp.Services;

internal static class IndicatorTrendProjector
{
    private enum AssessmentMode
    {
        HigherIsBetter,
        ContextDependent,
    }

    private sealed record TrendDefinition(
        string Code,
        string PageSlug,
        string SeriesName,
        string SeriesKey,
        string Label,
        string Unit,
        double StableTolerance,
        AssessmentMode AssessmentMode,
        string Interpretation);

    private static readonly FrozenDictionary<string, TrendDefinition> Definitions =
        new Dictionary<string, TrendDefinition>(StringComparer.Ordinal)
        {
            ["LEI-GDP"] = new(
                "LEI-GDP", "LI_Monthly", "产业规模占GDP比重",
                "industryScaleShare", "五大新产业规模占 GDP", "%", 0.05,
                AssessmentMode.HigherIsBetter, "占比上升通常表示五大新产业在经济中的体量改善。"),
            ["LEI-EMP"] = new(
                "LEI-EMP", "LI_Monthly", "直接就业能力系数",
                "directEmploymentCapacity", "五大新产业直接就业能力系数", "系数", 0.001,
                AssessmentMode.HigherIsBetter, "系数上升通常表示五大新产业的直接就业支撑能力改善。"),
            ["LEI-FIS"] = new(
                "LEI-FIS", "LI_Monthly", "净财政贡献能力系数",
                "netFiscalCapacity", "五大新产业净财政贡献能力系数", "系数", 0.001,
                AssessmentMode.HigherIsBetter, "系数上升（包括负值收窄）通常表示净财政贡献改善。"),
            ["MRS"] = new(
                "MRS", "Meaningful_Retail", "有意义社零同比",
                "meaningfulRetail", "有意义社零同比", "%", 0.1,
                AssessmentMode.HigherIsBetter, "同比增速上升通常表示消费动能改善。"),
            ["MCPI"] = new(
                "MCPI", "Meaningful_CPI_PPI", "有意义 CPI",
                "meaningfulCpi", "有意义 CPI", "%", 0.1,
                AssessmentMode.ContextDependent, "通胀升降本身不能机械解释为改善或恶化，需结合通缩、目标区间与需求背景。"),
            ["MSF"] = new(
                "MSF", "Meaningful_TSF", "有意义社融",
                "meaningfulSocialFinancing", "有意义社融月度流量", "亿元", 100,
                AssessmentMode.ContextDependent, "融资流量具有季节性且不能证明资金最终用途，单凭升降不能判断经济改善或恶化。"),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IndicatorTrend? Build(
        string code,
        IReadOnlyDictionary<string, StoredPage> pages,
        int months)
    {
        if (!Definitions.TryGetValue(code, out var definition) ||
            !pages.TryGetValue(definition.PageSlug, out var page))
        {
            return null;
        }

        var chart = page.Snapshot.Charts.FirstOrDefault(candidate =>
            candidate.Series.Any(series => series.Name == definition.SeriesName));
        var series = chart?.Series.FirstOrDefault(candidate => candidate.Name == definition.SeriesName);
        if (series is null)
        {
            return null;
        }

        var observations = new List<HistoricalObservation>(series.Points.Length);
        foreach (var point in series.Points)
        {
            var period = NormalizePeriod(point.Category);
            if (period is null || point.Value is null)
            {
                continue;
            }

            if (observations.Count > 0 && observations[^1].Period == period)
            {
                observations[^1] = new HistoricalObservation(period, point.Value.Value);
            }
            else
            {
                observations.Add(new HistoricalObservation(period, point.Value.Value));
            }
        }

        if (observations.Count == 0)
        {
            return null;
        }

        observations.Sort(static (left, right) => string.CompareOrdinal(left.Period, right.Period));
        var current = observations[^1];
        var previous = observations.Count >= 2 ? observations[^2] : null;
        var yearAgoPeriod = PreviousYear(current.Period);
        var yearAgo = yearAgoPeriod is null
            ? null
            : observations.LastOrDefault(observation => observation.Period == yearAgoPeriod);
        double? monthOverMonthChange = previous is null ? null : current.Value - previous.Value;
        double? yearOverYearChange = yearAgo is null ? null : current.Value - yearAgo.Value;
        double? recentAverage = observations.Count >= 3 ? Average(observations, observations.Count - 3, 3) : null;
        double? previousAverage = observations.Count >= 6 ? Average(observations, observations.Count - 6, 3) : null;
        var momentum = recentAverage is null || previousAverage is null ? null : recentAverage - previousAverage;
        var direction = AssessDirection(
            monthOverMonthChange,
            yearOverYearChange,
            momentum,
            definition.StableTolerance);
        var assessment = AssessEconomy(direction, definition.AssessmentMode);
        var historyOffset = Math.Max(0, observations.Count - months);

        return new IndicatorTrend(
            definition.SeriesKey,
            definition.Label,
            definition.Unit,
            observations.Count,
            [.. observations.Skip(historyOffset)],
            current.Period,
            current.Value,
            previous?.Period,
            previous?.Value,
            monthOverMonthChange,
            yearAgo?.Period,
            yearAgo?.Value,
            yearOverYearChange,
            recentAverage,
            previousAverage,
            momentum,
            direction,
            assessment,
            BuildBasis(monthOverMonthChange, yearOverYearChange, momentum, definition.Unit),
            definition.Interpretation);
    }

    private static TrendDirection AssessDirection(
        double? monthOverMonthChange,
        double? yearOverYearChange,
        double? momentum,
        double tolerance)
    {
        Span<int> signals = stackalloc int[3];
        var count = 0;
        AddSignal(monthOverMonthChange, tolerance, signals, ref count);
        AddSignal(yearOverYearChange, tolerance, signals, ref count);
        AddSignal(momentum, tolerance, signals, ref count);
        if (count == 0)
        {
            return TrendDirection.InsufficientData;
        }

        var hasPositive = false;
        var hasNegative = false;
        for (var index = 0; index < count; index++)
        {
            hasPositive |= signals[index] > 0;
            hasNegative |= signals[index] < 0;
        }

        if (hasPositive && hasNegative)
        {
            return TrendDirection.Mixed;
        }
        if (hasPositive)
        {
            return TrendDirection.Rising;
        }
        if (hasNegative)
        {
            return TrendDirection.Falling;
        }
        return TrendDirection.Stable;
    }

    private static void AddSignal(double? change, double tolerance, Span<int> signals, ref int count)
    {
        if (change is null)
        {
            return;
        }

        signals[count++] = Math.Abs(change.Value) <= tolerance ? 0 : Math.Sign(change.Value);
    }

    private static EconomicAssessment AssessEconomy(TrendDirection direction, AssessmentMode mode)
    {
        if (direction == TrendDirection.InsufficientData)
        {
            return EconomicAssessment.InsufficientData;
        }
        if (mode == AssessmentMode.ContextDependent)
        {
            return EconomicAssessment.Indeterminate;
        }

        return direction switch
        {
            TrendDirection.Rising => EconomicAssessment.Improving,
            TrendDirection.Falling => EconomicAssessment.Deteriorating,
            TrendDirection.Stable => EconomicAssessment.Stable,
            TrendDirection.Mixed => EconomicAssessment.Mixed,
            _ => EconomicAssessment.InsufficientData,
        };
    }

    private static string BuildBasis(
        double? monthOverMonthChange,
        double? yearOverYearChange,
        double? momentum,
        string unit)
    {
        var parts = new List<string>(3);
        AddBasis(parts, "较上月", monthOverMonthChange, unit);
        AddBasis(parts, "较上年同期", yearOverYearChange, unit);
        AddBasis(parts, "近3月均值较前3月", momentum, unit);
        return parts.Count == 0 ? "可比较历史不足。" : string.Join('；', parts) + "。";
    }

    private static void AddBasis(List<string> parts, string label, double? change, string unit)
    {
        if (change is null)
        {
            return;
        }

        var sign = change.Value > 0 ? "+" : string.Empty;
        parts.Add($"{label} {sign}{change.Value.ToString("0.###", CultureInfo.InvariantCulture)} {unit}");
    }

    private static double Average(List<HistoricalObservation> observations, int offset, int count)
    {
        var sum = 0d;
        for (var index = offset; index < offset + count; index++)
        {
            sum += observations[index].Value;
        }
        return sum / count;
    }

    private static string? NormalizePeriod(string? category)
    {
        if (category is null || category.Length < 7 || category[4] != '-' ||
            !int.TryParse(category.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(category.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            month is < 1 or > 12)
        {
            return null;
        }

        return $"{year:D4}-{month:D2}";
    }

    private static string? PreviousYear(string period)
    {
        if (!int.TryParse(period.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            return null;
        }
        return $"{year - 1:D4}{period[4..]}";
    }
}
