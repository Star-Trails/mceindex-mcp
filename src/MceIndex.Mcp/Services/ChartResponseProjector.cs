using System.Globalization;
using System.Net;
using System.Text;
using MceIndex.Mcp.Domain;

namespace MceIndex.Mcp.Services;

internal static class ChartResponseProjector
{
    public static ChartData[] Project(IEnumerable<ChartData> charts) =>
        charts.Select(Project).ToArray();

    private static ChartData Project(ChartData chart)
    {
        var series = chart.Series.Select(Project).ToArray();
        var xAxisTitle = EmptyToNull(PlainText(chart.XAxisTitle));
        var yAxisTitle = EmptyToNull(PlainText(chart.YAxisTitle));
        var originalTitle = chart.Title;
        var title = PlainText(originalTitle);
        if (title.Length == 0)
        {
            title = yAxisTitle ?? xAxisTitle ?? series
                .Select(item => item.Name)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "图表";
        }

        var description = PlainText(chart.Description);
        if (description.Length == 0 ||
            originalTitle.Contains('<', StringComparison.Ordinal) &&
            chart.Description.Contains(originalTitle, StringComparison.Ordinal))
        {
            description = $"MCEIndex 页面中的“{title}”图表。";
        }

        return new ChartData(
            title,
            description,
            chart.Notes.Select(PlainText).Where(note => note.Length > 0).ToArray(),
            xAxisTitle,
            yAxisTitle,
            series);
    }

    private static ChartSeries Project(ChartSeries series) => new(
        EmptyToNull(PlainText(series.Name)),
        series.Type,
        series.Points.Select(Project).ToArray());

    private static ChartPoint Project(ChartPoint point)
    {
        double? value = point.Value is { } raw ? NormalizeNumber(raw) : null;
        var text = EmptyToNull(PlainText(point.Text));
        var displayValue = EmptyToNull(PlainText(point.DisplayValue)) ?? text ??
            value?.ToString("G15", CultureInfo.InvariantCulture);
        return new ChartPoint(NormalizeCategory(point.Category), value, text, displayValue);
    }

    private static double NormalizeNumber(double value)
    {
        if (!double.IsFinite(value) || value == 0)
        {
            return value;
        }

        var decimalPlaces = 14 - (int)Math.Floor(Math.Log10(Math.Abs(value)));
        if (decimalPlaces >= 0)
        {
            return decimalPlaces <= 15 ? Math.Round(value, decimalPlaces) : value;
        }

        var scale = Math.Pow(10, -decimalPlaces);
        return Math.Round(value / scale) * scale;
    }

    private static string? NormalizeCategory(string? value)
    {
        var category = EmptyToNull(PlainText(value));
        if (category is null || IsYearMonth(category))
        {
            return category;
        }

        if (!DateTimeOffset.TryParse(
                category,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return category;
        }

        if (timestamp.Day == 1 && timestamp.TimeOfDay == TimeSpan.Zero)
        {
            return timestamp.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        }

        return timestamp.ToString("O", CultureInfo.InvariantCulture);
    }

    private static bool IsYearMonth(string value) =>
        value.Length == 7 &&
        value[4] == '-' &&
        int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
        int.TryParse(value.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month) &&
        month is >= 1 and <= 12;

    private static string PlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '<' && index + 1 < value.Length &&
                (char.IsAsciiLetter(value[index + 1]) || value[index + 1] is '/' or '!' or '?'))
            {
                var close = value.IndexOf('>', index + 1);
                if (close >= 0)
                {
                    index = close;
                    continue;
                }
            }

            builder.Append(value[index]);
        }

        return WebUtility.HtmlDecode(builder.ToString()).Trim();
    }

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;
}
