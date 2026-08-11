using Microsoft.Data.Sqlite;
using MceIndex.Mcp.Domain;
using MceIndex.Mcp.Persistence;

namespace MceIndex.Mcp.Tests;

public sealed class StoreTests
{
    [Fact]
    public void AppliesPagesIdempotentlyAndSearchesChinese()
    {
        using var store = new MceIndexStore(":memory:");
        var first = store.ApplyPages([new IndexedPage("Monthly_Overview", "月度总览", Page("10.54%", DateTimeOffset.UnixEpoch))], DateTimeOffset.UnixEpoch);
        var unchanged = store.ApplyPages([new IndexedPage("Monthly_Overview", "月度总览", Page("10.54%", DateTimeOffset.UnixEpoch.AddHours(1)))], DateTimeOffset.UnixEpoch.AddHours(1));
        var changed = store.ApplyPages([new IndexedPage("Monthly_Overview", "月度总览", Page("10.90%", DateTimeOffset.UnixEpoch.AddHours(2)))], DateTimeOffset.UnixEpoch.AddHours(2));

        Assert.Equal((1, 0, 1L), (first.ChangedPages, first.UnchangedPages, first.Generation));
        Assert.Equal((0, 1, 1L), (unchanged.ChangedPages, unchanged.UnchangedPages, unchanged.Generation));
        Assert.Equal((1, 0, 2L), (changed.ChangedPages, changed.UnchangedPages, changed.Generation));
        Assert.Contains(store.Search("新能源汽车", null, null, 0, 10, SearchMode.Phrase), hit => hit.Text.Contains("新能源汽车", StringComparison.Ordinal));
        Assert.Contains(store.Search("汽车", null, ContentKind.Text, 0, 10, SearchMode.And), hit => hit.Text.Contains("汽车", StringComparison.Ordinal));
        Assert.Contains(store.Search("产业规模", null, ContentKind.Chart, 0, 10, SearchMode.Phrase), hit => hit.Text.Contains("图表说明", StringComparison.Ordinal));
        var card = Assert.Single(store.GetCards("Monthly_Overview"));
        Assert.Equal(("10.90%", "2026-06", "指标解释"), (card.Value, card.Period, card.Description));
        Assert.Single(store.FindPage("Monthly_Overview")!.Snapshot.Charts);
    }

    [Fact]
    public void MigratesTheTypeScriptV2SchemaWithoutRecrawling()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mceindex-v2-{Guid.NewGuid():N}.db");
        try
        {
            CreateLegacyDatabase(path);
            using var store = new MceIndexStore(path);

            Assert.Equal(MceIndexStore.CurrentSchemaVersion, int.Parse(store.GetMeta("schema_version")!, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("月度总览", store.FindPage("Monthly_Overview")?.Snapshot.AppTitle);
            Assert.Single(store.Search("新能源汽车", null, null, 0, 10, SearchMode.Phrase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static CrawledPage Page(string value, DateTimeOffset fetchedAt)
    {
        var snapshot = new PageSnapshot
        {
            SourceUrl = "https://mceindex.com/Monthly_Overview",
            FetchedAt = fetchedAt,
            Title = "有意义中国经济指数",
            AppTitle = "月度总览",
            Headings = [new Heading(1, "月度总览")],
            Navigation = [],
            Metrics = [new Metric("GDP 综合指数", value)],
            Tables = [],
            Cards = [new IndexCard("LEI-GDP", "五大新产业规模占 GDP", value, "2026-06 · 同比", "2026-06", "指标解释")],
            Charts =
            [
                new ChartData(
                    "产业规模图",
                    "产业规模图表说明",
                    ["数据截至 2026-06"],
                    "月份",
                    "占比",
                    [new ChartSeries("产业规模", "scatter", [new ChartPoint("2026-06", 10.54)])]),
            ],
            Text = ["LEI-GDP", "UPDATED", value, "2026-06", "新能源汽车产量持续增长"],
        };
        return new CrawledPage(snapshot, ["<main>fixture</main>"]);
    }

    private static void CreateLegacyDatabase(string path)
    {
        MceIndexStore.EnsureSqliteInitialized();
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE pages(slug TEXT PRIMARY KEY,label TEXT NOT NULL,title TEXT NOT NULL,source_url TEXT NOT NULL UNIQUE,
              fetched_at TEXT NOT NULL,snapshot_json TEXT NOT NULL,raw_documents_json TEXT NOT NULL,text_count INTEGER NOT NULL);
            CREATE TABLE cards(page_slug TEXT NOT NULL REFERENCES pages(slug) ON DELETE CASCADE,code TEXT NOT NULL,label TEXT NOT NULL,
              value TEXT NOT NULL,detail TEXT,seq INTEGER NOT NULL,PRIMARY KEY(page_slug,code));
            CREATE TABLE content_entries(id INTEGER PRIMARY KEY,page_slug TEXT NOT NULL REFERENCES pages(slug) ON DELETE CASCADE,
              kind TEXT NOT NULL,text TEXT NOT NULL,seq INTEGER NOT NULL);
            CREATE VIRTUAL TABLE content_fts USING fts5(page_slug UNINDEXED,kind UNINDEXED,text,tokenize='trigram');
            CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            INSERT INTO pages VALUES('Monthly_Overview','月度总览','有意义中国经济指数','https://mceindex.com/Monthly_Overview',
              '2026-08-10T00:00:00.000Z',
              '{"sourceUrl":"https://mceindex.com/Monthly_Overview","fetchedAt":"2026-08-10T00:00:00Z","title":"有意义中国经济指数","appTitle":"月度总览","headings":[{"level":1,"text":"月度总览"}],"navigation":[],"metrics":[],"tables":[],"text":["新能源汽车产量"]}',
              '["<main>fixture</main>"]',1);
            INSERT INTO content_entries(page_slug,kind,text,seq) VALUES('Monthly_Overview','text','新能源汽车产量',0);
            INSERT INTO content_fts(page_slug,kind,text) VALUES('Monthly_Overview','text','新能源汽车产量');
            """;
        command.ExecuteNonQuery();
    }
}
