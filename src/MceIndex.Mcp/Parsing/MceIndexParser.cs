using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using MceIndex.Mcp.Domain;

namespace MceIndex.Mcp.Parsing;

public sealed partial class MceIndexParser
{
    internal const int MaxHtmlDocuments = 32;
    internal const int MaxHtmlDocumentCharacters = 5_000_000;
    internal const int MaxTotalHtmlCharacters = 20_000_000;
    private readonly HtmlParser parser = new();

    public static bool IsAccessChallenge(string html) =>
        html.Contains("cf-chl-", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("just a moment...", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("verify you are human", StringComparison.OrdinalIgnoreCase);

    public PageSnapshot Extract(
        IReadOnlyList<string> htmlDocuments,
        Uri sourceUri,
        DateTimeOffset fetchedAt)
    {
        if (htmlDocuments.Count == 0)
        {
            throw new MceIndexException(MceIndexErrorCode.ExtractionFailed, "No HTML documents were supplied for extraction.");
        }
        if (htmlDocuments.Count > MaxHtmlDocuments)
        {
            throw new MceIndexException(
                MceIndexErrorCode.ExtractionFailed,
                $"MCEIndex returned more than {MaxHtmlDocuments} HTML documents.");
        }

        var totalCharacters = 0;
        foreach (var html in htmlDocuments)
        {
            totalCharacters = IncludeDocument(html, totalCharacters);
        }


        var headings = new List<Heading>();
        var navigation = new List<NavigationItem>();
        var metrics = new List<Metric>();
        var tables = new List<DataTable>();
        var cards = new List<IndexCard>();
        var text = new List<string>();
        var title = string.Empty;
        string? description = null;

        foreach (var html in htmlDocuments)
        {
            var document = parser.ParseDocument(html);
            if (title.Length == 0)
            {
                title = Normalize(document.Title);
            }

            description ??= EmptyToNull(Normalize(document.QuerySelector("meta[name='description']")?.GetAttribute("content")));
            var root = document.QuerySelector("[data-testid='stMain']") ?? document.QuerySelector("main") ?? document.Body;
            if (root is null)
            {
                continue;
            }
            foreach (var element in root.QuerySelectorAll(".terminal-ticker-item"))
            {
                var code = Normalize(element.QuerySelector(".terminal-ticker-code")?.TextContent);
                var value = Normalize(element.QuerySelector(".terminal-ticker-value")?.TextContent);
                if (value.Length == 0 || !IndicatorCatalog.TryGet(code, out var definition))
                {
                    continue;
                }

                var detail = EmptyToNull(Normalize(element.QuerySelector(".terminal-ticker-comparison")?.TextContent));
                cards.Add(new IndexCard(
                    definition.Code,
                    definition.Label,
                    value,
                    detail,
                    ExtractPeriod(detail),
                    definition.Description));
            }


            foreach (var removable in root.QuerySelectorAll("script,style,noscript,svg").ToArray())
            {
                removable.Remove();
            }

            foreach (var element in root.QuerySelectorAll("h1,h2,h3,h4,h5,h6"))
            {
                var value = Normalize(element.TextContent);
                if (value.Length > 0 && int.TryParse(element.TagName.AsSpan(1), out var level))
                {
                    headings.Add(new Heading(level, value));
                }
            }

            foreach (var element in document.QuerySelectorAll(
                         "[data-testid='stSidebar'] a, [data-testid='stSidebar'] button, " +
                         "[data-testid='stSidebarNav'] a, nav a, [role='tab']"))
            {
                var value = Normalize(element.GetAttribute("aria-label") ?? element.TextContent);
                if (value.Length == 0)
                {
                    continue;
                }

                string? url = null;
                var href = element.GetAttribute("href");
                if (!string.IsNullOrWhiteSpace(href) && Uri.TryCreate(sourceUri, href, out var resolved))
                {
                    url = resolved.AbsoluteUri;
                }

                navigation.Add(new NavigationItem(value, NavigationKindFor(element), url));
            }

            foreach (var element in root.QuerySelectorAll("[data-testid='stMetric']"))
            {
                var label = Normalize(element.QuerySelector("[data-testid='stMetricLabel']")?.TextContent);
                var value = Normalize(element.QuerySelector("[data-testid='stMetricValue']")?.TextContent);
                if (label.Length == 0 && value.Length == 0)
                {
                    continue;
                }

                var delta = EmptyToNull(Normalize(element.QuerySelector("[data-testid='stMetricDelta']")?.TextContent));
                var help = EmptyToNull(Normalize(
                    element.GetAttribute("title") ?? element.QuerySelector("[aria-label]")?.GetAttribute("aria-label")));
                if (string.Equals(help, label, StringComparison.Ordinal))
                {
                    help = null;
                }

                metrics.Add(new Metric(label, value, delta, help));
            }

            foreach (var element in root.QuerySelectorAll("table"))
            {
                var headers = element.QuerySelectorAll("thead th")
                    .Select(cell => Normalize(cell.TextContent))
                    .ToArray();
                var rows = element.QuerySelectorAll("tbody tr")
                    .Select(row => row.QuerySelectorAll("th,td").Select(cell => Normalize(cell.TextContent)).ToArray())
                    .Where(row => row.Any(value => value.Length > 0))
                    .ToArray();
                if (headers.Length == 0 && rows.Length == 0)
                {
                    continue;
                }

                tables.Add(new DataTable(headers, rows, FindTableTitle(element)));
            }

            foreach (var element in root.QuerySelectorAll("h1,h2,h3,h4,h5,h6,p,li,blockquote,figcaption,[role='alert']"))
            {
                AddNormalized(text, element.TextContent);
            }

            foreach (var element in root.QuerySelectorAll("div,span,strong,small"))
            {
                var directText = string.Concat(element.ChildNodes.OfType<IText>().Select(node => node.Data));
                AddNormalized(text, directText);
            }
        }

        var uniqueHeadings = Unique(headings, item => $"{item.Level}:{item.Text}");
        return new PageSnapshot
        {
            SourceUrl = sourceUri.AbsoluteUri,
            FetchedAt = fetchedAt,
            Title = title.Length > 0 ? title : "MCEIndex",
            Description = description,
            AppTitle = uniqueHeadings.FirstOrDefault(item => item.Level == 1)?.Text,
            Headings = uniqueHeadings,
            Navigation = Unique(navigation, item => $"{item.Kind}:{item.Text}:{item.Url}"),
            Metrics = Unique(metrics, item => $"{item.Label}:{item.Value}:{item.Delta}"),
            Tables = Unique(tables, TableKey),
            Cards = Unique(cards, item => item.Code),
            Text = Unique(text, item => item),
        };
    }

    internal static int IncludeDocument(string html, int totalCharacters)
    {
        if (html.Length > MaxHtmlDocumentCharacters ||
            totalCharacters > MaxTotalHtmlCharacters - html.Length)
        {
            throw new MceIndexException(
                MceIndexErrorCode.ExtractionFailed,
                "MCEIndex HTML exceeded safe extraction limits.");
        }

        return totalCharacters + html.Length;
    }

    private static NavigationKind NavigationKindFor(IElement element)
    {
        if (element.TagName.Equals("A", StringComparison.OrdinalIgnoreCase))
        {
            return NavigationKind.Link;
        }

        return element.GetAttribute("role")?.Equals("tab", StringComparison.OrdinalIgnoreCase) is true
            ? NavigationKind.Tab
            : NavigationKind.Button;
    }

    private static string? FindTableTitle(IElement table)
    {
        for (var sibling = table.PreviousElementSibling; sibling is not null; sibling = sibling.PreviousElementSibling)
        {
            if (sibling.TagName is "H2" or "H3" or "H4" or "H5" or "H6" or "CAPTION")
            {
                return EmptyToNull(Normalize(sibling.TextContent));
            }
        }

        return null;
    }

    private static string TableKey(DataTable table) =>
        $"{table.Title}\u001f{string.Join('\u001e', table.Headers)}\u001d" +
        string.Join("\u001c", table.Rows.Select(row => string.Join('\u001e', row)));

    private static string? ExtractPeriod(string? detail)
    {
        if (detail is null)
        {
            return null;
        }

        var match = PeriodRegex().Match(detail);
        return match.Success ? match.Value : null;
    }


    private static T[] Unique<T>(IEnumerable<T> items, Func<T, string> keySelector)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return items.Where(item => seen.Add(keySelector(item))).ToArray();
    }

    private static void AddNormalized(List<string> values, string? value)
    {
        var normalized = Normalize(value);
        if (normalized.Length > 0)
        {
            values.Add(normalized);
        }
    }

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private static string Normalize(string? value) => value is null
        ? string.Empty
        : WhitespaceRegex().Replace(value.Replace('\u00a0', ' '), " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\b\d{4}-\d{2}\b")]
    private static partial Regex PeriodRegex();
}
