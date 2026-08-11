using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using MceIndex.Mcp.Domain;
using MceIndex.Mcp.Persistence;

namespace MceIndex.Mcp.Tests;

public sealed class StdioIntegrationTests
{
    [Fact]
    public async Task ListsStructuredToolsAndQueriesExistingIndexWithoutBrowser()
    {
        var databasePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mceindex-stdio-{Guid.NewGuid():N}.db");
        try
        {
            SeedDatabase(databasePath);
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment["MCEINDEX_DB_PATH"] = databasePath;
            environment["MCEINDEX_BASE_URL"] = "http://127.0.0.1:9/";
            environment["MCEINDEX_BROWSER_EXECUTABLE"] = "/does/not/exist";
            var libraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            if (libraryPath is not null) environment["LD_LIBRARY_PATH"] = libraryPath;

            var repositoryRoot = System.IO.Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "mceindex-test",
                Command = System.IO.Path.Combine(repositoryRoot, ".dotnet", "dotnet"),
                Arguments = [System.IO.Path.Combine(repositoryRoot, "src", "MceIndex.Mcp", "bin", "Debug", "net10.0", "mceindex-mcp.dll")],
                WorkingDirectory = repositoryRoot,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment,
            });
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            Assert.Equal("MCEIndex", client.ServerInfo.Title);
            Assert.Equal("mceindex-mcp", client.ServerInfo.Name);
            Assert.Equal("3.8.0", client.ServerInfo.Version);

            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            Assert.Equal(
                ["discover_data", "get_indicator", "get_latest", "get_page", "list_pages", "refresh_index", "search_index"],
                tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());
            Assert.All(tools, tool => Assert.NotNull(tool.ProtocolTool.OutputSchema));
            Assert.All(
                tools.Where(tool => tool.Name != "refresh_index"),
                tool => Assert.False(tool.ProtocolTool.Annotations?.ReadOnlyHint));

            var discoverTool = tools.Single(tool => tool.Name == "discover_data");
            Assert.Contains("优先调用", discoverTool.ProtocolTool.Description, StringComparison.Ordinal);

            var discovery = await client.CallToolAsync("discover_data", cancellationToken: cancellationToken);
            Assert.NotNull(discovery.StructuredContent);
            var discoveryRoot = discovery.StructuredContent.Value;
            Assert.Equal(
                "当前索引包含 1 个主题、2 个结构化读数和 2 个页面。",
                discoveryRoot.GetProperty("summary").GetString());
            var topics = discoveryRoot.GetProperty("topics");
            Assert.Equal(1, topics.GetArrayLength());
            Assert.Equal("LEI-GDP", topics[0].GetProperty("code").GetString());
            Assert.Equal(
                "观察五大新产业在整体经济中的体量及历史位置。",
                topics[0].GetProperty("whyItMatters").GetString());
            Assert.Equal(
                "新产业占经济多大？",
                topics[0].GetProperty("suggestedQuestion").GetString());
            Assert.Equal(2, topics[0].GetProperty("currentReadings").GetArrayLength());
            Assert.Equal("10.54%", topics[0].GetProperty("currentReadings")[0].GetProperty("displayValue").GetString());
            Assert.Equal(2, discoveryRoot.GetProperty("pages").GetArrayLength());
            Assert.Equal(4, discoveryRoot.GetProperty("nextSteps").GetArrayLength());

            var latest = await client.CallToolAsync("get_latest", cancellationToken: cancellationToken);
            Assert.NotNull(latest.StructuredContent);
            Assert.Contains("10.54%", latest.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text, StringComparison.Ordinal);
            var latestRoot = latest.StructuredContent.Value;
            var atAGlance = latestRoot.GetProperty("atAGlance");
            Assert.Equal(1, atAGlance.GetArrayLength());
            Assert.Equal("LEI-GDP", atAGlance[0].GetProperty("code").GetString());
            var directReading = atAGlance[0].GetProperty("readings")[0];
            Assert.Equal("industryScaleShare", directReading.GetProperty("key").GetString());
            Assert.Equal(10.54, directReading.GetProperty("value").GetDouble());
            Assert.Equal("10.54%", directReading.GetProperty("displayValue").GetString());
            var verification = directReading.GetProperty("verification");
            Assert.Equal("notFound", verification.GetProperty("status").GetString());
            Assert.Equal("partial", verification.GetProperty("sourceStatus").GetString());
            Assert.Equal("published", verification.GetProperty("algorithmStatus").GetString());
            Assert.Equal("impossible", verification.GetProperty("reproductionStatus").GetString());
            Assert.True(verification.GetProperty("appliesToCurrentPeriod").GetBoolean());
            Assert.False(verification.GetProperty("dataUpdated").GetBoolean());
            Assert.True(verification.GetProperty("sources").GetArrayLength() >= 2);
            var conceptualProvenance = verification.GetProperty("conceptualProvenance");
            Assert.Equal(
                "partiallyVerified",
                conceptualProvenance.GetProperty("status").GetString());
            Assert.Contains(
                conceptualProvenance.GetProperty("sources").EnumerateArray(),
                source => source.GetProperty("url").GetString() ==
                    "https://www.youtube.com/watch?v=d5jEroGqoLc");
            var traceNotes = atAGlance[0].GetProperty("notes");
            Assert.True(traceNotes.GetArrayLength() >= 2);
            Assert.Contains(traceNotes.EnumerateArray(),
                note => note.GetProperty("kind").GetString() == "formula" &&
                    note.GetProperty("text").GetString()!.Contains("产业规模 =", StringComparison.Ordinal));
            Assert.All(traceNotes.EnumerateArray(),
                note => Assert.Equal(
                    "https://mceindex.com/LI_Monthly",
                    note.GetProperty("sourceUrl").GetString()));

            var indicator = await client.CallToolAsync("get_indicator", new Dictionary<string, object?>
            {
                ["indicator"] = "LEI-GDP",
            }, cancellationToken: cancellationToken);
            Assert.NotNull(indicator.StructuredContent);
            var indicatorText = indicator.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;
            Assert.Contains("2026-06", indicatorText, StringComparison.Ordinal);
            Assert.Contains("产业规模占GDP比重", indicatorText, StringComparison.Ordinal);

            var charts = await client.CallToolAsync("get_page", new Dictionary<string, object?>
            {
                ["page"] = "Monthly_Overview",
                ["view"] = "charts",
            }, cancellationToken: cancellationToken);
            Assert.NotNull(charts.StructuredContent);
            var chartText = charts.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;
            Assert.Contains("产业规模图表说明", chartText, StringComparison.Ordinal);
            Assert.Contains("2026-06", chartText, StringComparison.Ordinal);

            var search = await client.CallToolAsync("search_index", new Dictionary<string, object?>
            {
                ["query"] = "新能源汽车",
                ["mode"] = "phrase",
            }, cancellationToken: cancellationToken);
            Assert.NotNull(search.StructuredContent);
            Assert.Contains("新能源汽车", search.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text, StringComparison.Ordinal);

            var missing = await client.CallToolAsync("get_page", new Dictionary<string, object?>
            {
                ["page"] = "missing-page",
            }, cancellationToken: cancellationToken);
            Assert.True(missing.IsError);
            Assert.Contains(
                "PAGE_NOT_FOUND",
                missing.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text,
                StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    private static void SeedDatabase(string path)
    {
        using var store = new MceIndexStore(path);
        var now = DateTimeOffset.UtcNow;
        var snapshot = new PageSnapshot
        {
            SourceUrl = "https://mceindex.com/Monthly_Overview",
            FetchedAt = now,
            Title = "有意义中国经济指数",
            AppTitle = "月度总览",
            Headings = [new Heading(1, "月度总览")],
            Navigation = [],
            Metrics = [],
            Tables = [],
            Cards =
            [
                new IndexCard(
                    "LEI-GDP",
                    "五大新产业规模占 GDP",
                    "10.54%",
                    "2026-06 · 历史 P99",
                    "2026-06",
                    "产业规模占GDP比重"),
            ],
            Charts =
            [
                new ChartData(
                    "新产业占经济多大？",
                    "产业规模图表说明",
                    ["数据截至 2026-06"],
                    "月份",
                    "占比",
                    [new ChartSeries("新产业经济规模占比", "scatter", [new ChartPoint("2026-06", 10.54)])]),
            ],
            Text = ["LEI-GDP", "10.54%", "2026-06", "新能源汽车产量"],
        };
        var lifeIndex = new PageSnapshot
        {
            SourceUrl = "https://mceindex.com/LI_Monthly",
            FetchedAt = now,
            Title = "五大新产业续命指数",
            Headings = [],
            Navigation = [],
            Metrics = [],
            Tables =
            [
                new DataTable(
                    ["数据类别", "主要用途", "项目内落点", "口径说明"],
                    [["正式HS发布包", "提供总指标", "headline.csv", "不可变发布包"]]),
            ],
            Text =
            [
                "产业规模 = 五行业产业规模毛额相加 − 行业内部交易抵销",
                "出口产业规模 = 海关HS月度出口金额 国内产业规模 = 国内生产者交付毛额 − 国内抵销",
            ],
        };
        store.ApplyPages(
            [
                new IndexedPage("Monthly_Overview", "月度总览", new CrawledPage(snapshot, ["<main>fixture</main>"])),
                new IndexedPage("LI_Monthly", "五大新产业续命指数", new CrawledPage(lifeIndex, ["<main>fixture</main>"])),
            ],
            now);
        store.RecordRefresh(now, [], true);
    }
}
