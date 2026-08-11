using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MceIndex.Mcp.Domain;
using MceIndex.Mcp.Serialization;

namespace MceIndex.Mcp.Persistence;

public sealed class MceIndexStore : IDisposable
{
    public const int CurrentSchemaVersion = 4;
    static MceIndexStore()
    {
        if (OperatingSystem.IsWindows())
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
            return;
        }

        var libraryName = OperatingSystem.IsMacOS() ? "libsqlite3.dylib" : "libsqlite3.so.0";
        SQLitePCL.SQLite3Provider_dynamic_cdecl.Setup(
            libraryName,
            new NativeLibraryAdapter(libraryName));
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_dynamic_cdecl());
    }

    internal static void EnsureSqliteInitialized()
    {
    }

    private sealed class NativeLibraryAdapter(string libraryName) : SQLitePCL.IGetFunctionPointer
    {
        private readonly IntPtr library = System.Runtime.InteropServices.NativeLibrary.Load(libraryName);

        public IntPtr GetFunctionPointer(string name) =>
            System.Runtime.InteropServices.NativeLibrary.TryGetExport(library, name, out var address)
                ? address
                : IntPtr.Zero;
    }


    private readonly string connectionString;
    private readonly SqliteConnection? memoryKeeper;
    private bool disposed;

    public MceIndexStore(string path)
    {
        Path = path;
        if (path == ":memory:")
        {
            var name = $"mceindex-{Guid.NewGuid():N}";
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = name,
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
            }.ToString();
            memoryKeeper = OpenConnection();
        }
        else
        {
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = true,
            }.ToString();
        }

        using var connection = OpenConnection();
        Initialize(connection);
    }

    public string Path { get; }

    public int CountPages()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pages";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public long GetGeneration() => long.TryParse(GetMeta("index_generation"), out var value) ? value : 0;

    public string? GetMeta(string key)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public StoredPageSummary[] ListPages()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT slug,label,title,source_url,fetched_at,last_checked_at,text_count,generation
            FROM pages ORDER BY slug
            """;
        using var reader = command.ExecuteReader();
        var pages = new List<StoredPageSummary>();
        while (reader.Read())
        {
            pages.Add(ReadPageSummary(reader));
        }

        return [.. pages];
    }

    public StoredPage? FindPage(string query)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT slug,label,title,source_url,fetched_at,last_checked_at,text_count,generation,snapshot_json
            FROM pages
            WHERE slug = $query COLLATE NOCASE OR label = $query COLLATE NOCASE
            ORDER BY CASE WHEN slug = $query COLLATE NOCASE THEN 0 ELSE 1 END
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$query", query.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var snapshot = JsonSerializer.Deserialize(reader.GetString(8), MceJsonContext.Default.PageSnapshot)
            ?? throw new MceIndexException(MceIndexErrorCode.DatabaseError, "Stored page snapshot is invalid JSON.");
        return new StoredPage(ReadPageSummary(reader), snapshot);
    }

    public IndexCard[] GetCards(string pageSlug)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT code,label,value,detail,period,description FROM cards WHERE page_slug=$page ORDER BY seq";
        command.Parameters.AddWithValue("$page", pageSlug);
        using var reader = command.ExecuteReader();
        var cards = new List<IndexCard>();
        while (reader.Read())
        {
            var code = reader.GetString(0);
            var description = reader.GetString(5);
            if (description.Length == 0 && IndicatorCatalog.TryGet(code, out var definition))
            {
                description = definition.Description;
            }
            cards.Add(new IndexCard(
                code,
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                description));
        }

        return [.. cards];
    }

    public PageContentItem[] GetContent(string pageSlug, PageView view, int offset, int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var filter = view == PageView.Tables ? " AND kind = 'table'" : "";
        command.CommandText = $"""
            SELECT id,kind,text,seq FROM content_entries
            WHERE page_slug=$page{filter}
            ORDER BY seq LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$page", pageSlug);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var entries = new List<PageContentItem>();
        while (reader.Read())
        {
            entries.Add(new PageContentItem(reader.GetInt64(0), ParseContentKind(reader.GetString(1)), reader.GetString(2), reader.GetInt32(3)));
        }

        return [.. entries];
    }

    public SearchHit[] Search(
        string query,
        string? pageSlug,
        ContentKind? kind,
        int offset,
        int limit,
        SearchMode mode)
    {
        var terms = mode == SearchMode.Phrase
            ? [query.Trim()]
            : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return [];
        }

        if (terms.All(term => term.EnumerateRunes().Count() >= 3))
        {
            var ftsHits = SearchFts(query, pageSlug, kind, offset, limit, mode);
            if (ftsHits.Length > 0)
            {
                return ftsHits;
            }
        }

        return SearchLike(terms, pageSlug, kind, offset, limit);
    }

    public ApplyPagesResult ApplyPages(IReadOnlyList<IndexedPage> pages, DateTimeOffset checkedAt)
    {
        if (pages.Count == 0)
        {
            return new ApplyPagesResult(0, 0, GetGeneration());
        }

        var prepared = pages.Select(page => new PreparedPage(page, SemanticHash(page.Crawled.Snapshot), BuildEntries(page.Crawled.Snapshot), ExtractCards(page.Crawled.Snapshot))).ToArray();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var currentGeneration = ReadGeneration(connection, transaction);
        var changed = new bool[prepared.Length];
        for (var index = 0; index < prepared.Length; index++)
        {
            changed[index] = !string.Equals(ReadContentHash(connection, transaction, prepared[index].Page.Slug), prepared[index].Hash, StringComparison.Ordinal);
        }

        var changedCount = changed.Count(value => value);
        var nextGeneration = changedCount > 0 ? currentGeneration + 1 : currentGeneration;
        for (var index = 0; index < prepared.Length; index++)
        {
            if (changed[index])
            {
                ReplaceChangedPage(connection, transaction, prepared[index], checkedAt, nextGeneration);
            }
            else
            {
                UpdateUnchangedPage(connection, transaction, prepared[index], checkedAt);
            }
        }

        if (changedCount > 0)
        {
            SetMeta(connection, transaction, "index_generation", nextGeneration.ToString(CultureInfo.InvariantCulture));
        }

        transaction.Commit();
        return new ApplyPagesResult(changedCount, prepared.Length - changedCount, nextGeneration);
    }

    public void RecordRefresh(DateTimeOffset finishedAt, IReadOnlyList<CrawlFailure> failures, bool fullSuccess)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        SetMeta(connection, transaction, "last_refresh_attempt", finishedAt.ToString("O", CultureInfo.InvariantCulture));
        SetMeta(connection, transaction, "last_failure_count", failures.Count.ToString(CultureInfo.InvariantCulture));
        SetMeta(connection, transaction, "last_error", failures.Count == 0
            ? string.Empty
            : string.Join("; ", failures.Select(failure => $"{failure.Url}: {failure.Code}: {failure.Message}"))[..Math.Min(4096, string.Join("; ", failures.Select(failure => $"{failure.Url}: {failure.Code}: {failure.Message}")).Length)]);
        if (fullSuccess)
        {
            SetMeta(connection, transaction, "last_successful_refresh", finishedAt.ToString("O", CultureInfo.InvariantCulture));
        }

        transaction.Commit();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        memoryKeeper?.Dispose();
        if (Path != ":memory:")
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private SqliteConnection OpenConnection()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void Initialize(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        using var versionCommand = connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
        {
            throw new MceIndexException(MceIndexErrorCode.DatabaseError, $"Database schema {version} is newer than supported schema {CurrentSchemaVersion}.");
        }

        using var existsCommand = connection.CreateCommand();
        existsCommand.Transaction = transaction;
        existsCommand.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='pages'";
        var hasLegacySchema = existsCommand.ExecuteScalar() is not null;
        if (!hasLegacySchema)
        {
            Execute(connection, transaction, CreateSchemaSql);
        }
        else if (version < CurrentSchemaVersion)
        {
            MigrateLegacy(connection, transaction);
        }

        Execute(connection, transaction, $"PRAGMA user_version={CurrentSchemaVersion};");
        SetMeta(connection, transaction, "schema_version", CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
        if (ReadMeta(connection, transaction, "index_generation") is null)
        {
            SetMeta(connection, transaction, "index_generation", "0");
        }

        transaction.Commit();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
    }

    private static void MigrateLegacy(SqliteConnection connection, SqliteTransaction transaction)
    {
        var columns = GetColumns(connection, transaction, "pages");
        if (!columns.Contains("content_hash"))
        {
            Execute(connection, transaction, "ALTER TABLE pages ADD COLUMN content_hash TEXT NOT NULL DEFAULT ''; ");
        }
        if (!columns.Contains("last_checked_at"))
        {
            Execute(connection, transaction, "ALTER TABLE pages ADD COLUMN last_checked_at TEXT; UPDATE pages SET last_checked_at=fetched_at WHERE last_checked_at IS NULL;");
        }
        if (!columns.Contains("generation"))
        {
            Execute(connection, transaction, "ALTER TABLE pages ADD COLUMN generation INTEGER NOT NULL DEFAULT 0;");
        }

        var cardColumns = GetColumns(connection, transaction, "cards");
        if (!cardColumns.Contains("period"))
        {
            Execute(connection, transaction, "ALTER TABLE cards ADD COLUMN period TEXT;");
        }
        if (!cardColumns.Contains("description"))
        {
            Execute(connection, transaction, "ALTER TABLE cards ADD COLUMN description TEXT NOT NULL DEFAULT '';");
        }

        Execute(connection, transaction, """
            DROP TRIGGER IF EXISTS content_entries_ai;
            DROP TRIGGER IF EXISTS content_entries_ad;
            DROP TRIGGER IF EXISTS content_entries_au;
            DROP TABLE IF EXISTS content_fts;
            CREATE VIRTUAL TABLE content_fts USING fts5(
                page_slug UNINDEXED, kind UNINDEXED, text,
                content='content_entries', content_rowid='id', tokenize='trigram');
            CREATE TRIGGER content_entries_ai AFTER INSERT ON content_entries BEGIN
                INSERT INTO content_fts(rowid,page_slug,kind,text) VALUES(new.id,new.page_slug,new.kind,new.text);
            END;
            CREATE TRIGGER content_entries_ad AFTER DELETE ON content_entries BEGIN
                INSERT INTO content_fts(content_fts,rowid,page_slug,kind,text)
                VALUES('delete',old.id,old.page_slug,old.kind,old.text);
            END;
            CREATE TRIGGER content_entries_au AFTER UPDATE ON content_entries BEGIN
                INSERT INTO content_fts(content_fts,rowid,page_slug,kind,text)
                VALUES('delete',old.id,old.page_slug,old.kind,old.text);
                INSERT INTO content_fts(rowid,page_slug,kind,text) VALUES(new.id,new.page_slug,new.kind,new.text);
            END;
            INSERT INTO content_fts(content_fts) VALUES('rebuild');
            """);
    }

    private static HashSet<string> GetColumns(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private SearchHit[] SearchFts(string query, string? pageSlug, ContentKind? kind, int offset, int limit, SearchMode mode)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var filters = string.Empty;
        if (pageSlug is not null) filters += " AND f.page_slug=$page";
        if (kind is not null) filters += " AND f.kind=$kind";
        command.CommandText = $"""
            SELECT f.rowid,f.page_slug,p.label,p.source_url,p.fetched_at,f.kind,f.text,bm25(content_fts) AS rank
            FROM content_fts f JOIN pages p ON p.slug=f.page_slug
            WHERE content_fts MATCH $match{filters}
            ORDER BY rank,f.rowid LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$match", BuildFtsQuery(query, mode));
        if (pageSlug is not null) command.Parameters.AddWithValue("$page", pageSlug);
        if (kind is not null) command.Parameters.AddWithValue("$kind", ToStorage(kind.Value));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        return ReadSearchHits(command);
    }

    private SearchHit[] SearchLike(string[] terms, string? pageSlug, ContentKind? kind, int offset, int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var clauses = new List<string>();
        for (var index = 0; index < terms.Length; index++)
        {
            clauses.Add($"e.text LIKE $term{index} ESCAPE '\\'");
            command.Parameters.AddWithValue($"$term{index}", $"%{EscapeLike(terms[index])}%");
        }
        if (pageSlug is not null)
        {
            clauses.Add("e.page_slug=$page");
            command.Parameters.AddWithValue("$page", pageSlug);
        }
        if (kind is not null)
        {
            clauses.Add("e.kind=$kind");
            command.Parameters.AddWithValue("$kind", ToStorage(kind.Value));
        }
        command.CommandText = $"""
            SELECT e.id,e.page_slug,p.label,p.source_url,p.fetched_at,e.kind,e.text,0.0 AS rank
            FROM content_entries e JOIN pages p ON p.slug=e.page_slug
            WHERE {string.Join(" AND ", clauses)}
            ORDER BY e.page_slug,e.seq LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        return ReadSearchHits(command);
    }

    private static SearchHit[] ReadSearchHits(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var hits = new List<SearchHit>();
        while (reader.Read())
        {
            hits.Add(new SearchHit(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                ParseTimestamp(reader.GetString(4)), ParseContentKind(reader.GetString(5)), reader.GetString(6), reader.GetDouble(7)));
        }
        return [.. hits];
    }

    private static void ReplaceChangedPage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PreparedPage prepared,
        DateTimeOffset checkedAt,
        long generation)
    {
        ExecuteNonQuery(connection, transaction, "DELETE FROM cards WHERE page_slug=$slug", ("$slug", prepared.Page.Slug));
        ExecuteNonQuery(connection, transaction, "DELETE FROM content_entries WHERE page_slug=$slug", ("$slug", prepared.Page.Slug));
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO pages(slug,label,title,source_url,fetched_at,snapshot_json,raw_documents_json,text_count,content_hash,last_checked_at,generation)
                VALUES($slug,$label,$title,$url,$fetched,$snapshot,$raw,$count,$hash,$checked,$generation)
                ON CONFLICT(slug) DO UPDATE SET label=excluded.label,title=excluded.title,source_url=excluded.source_url,
                    fetched_at=excluded.fetched_at,snapshot_json=excluded.snapshot_json,raw_documents_json=excluded.raw_documents_json,
                    text_count=excluded.text_count,content_hash=excluded.content_hash,last_checked_at=excluded.last_checked_at,generation=excluded.generation
                """;
            BindPage(command, prepared, checkedAt, generation);
            command.ExecuteNonQuery();
        }

        for (var index = 0; index < prepared.Entries.Length; index++)
        {
            ExecuteNonQuery(connection, transaction,
                "INSERT INTO content_entries(page_slug,kind,text,seq) VALUES($slug,$kind,$text,$seq)",
                ("$slug", prepared.Page.Slug), ("$kind", ToStorage(prepared.Entries[index].Kind)),
                ("$text", prepared.Entries[index].Text), ("$seq", index));
        }
        for (var index = 0; index < prepared.Cards.Length; index++)
        {
            var card = prepared.Cards[index];
            ExecuteNonQuery(connection, transaction,
                "INSERT INTO cards(page_slug,code,label,value,detail,period,description,seq) VALUES($slug,$code,$label,$value,$detail,$period,$description,$seq)",
                ("$slug", prepared.Page.Slug), ("$code", card.Code), ("$label", card.Label),
                ("$value", card.Value), ("$detail", card.Detail), ("$period", card.Period),
                ("$description", card.Description), ("$seq", index));
        }
    }

    private static void UpdateUnchangedPage(SqliteConnection connection, SqliteTransaction transaction, PreparedPage prepared, DateTimeOffset checkedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE pages SET label=$label,title=$title,source_url=$url,fetched_at=$fetched,
                snapshot_json=$snapshot,raw_documents_json=$raw,last_checked_at=$checked WHERE slug=$slug
            """;
        var snapshot = prepared.Page.Crawled.Snapshot;
        command.Parameters.AddWithValue("$slug", prepared.Page.Slug);
        command.Parameters.AddWithValue("$label", prepared.Page.Label);
        command.Parameters.AddWithValue("$title", snapshot.Title);
        command.Parameters.AddWithValue("$url", snapshot.SourceUrl);
        command.Parameters.AddWithValue("$fetched", snapshot.FetchedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(snapshot, MceJsonContext.Default.PageSnapshot));
        command.Parameters.AddWithValue("$raw", JsonSerializer.Serialize(prepared.Page.Crawled.HtmlDocuments, MceJsonContext.Default.StringArray));
        command.Parameters.AddWithValue("$checked", checkedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void BindPage(SqliteCommand command, PreparedPage prepared, DateTimeOffset checkedAt, long generation)
    {
        var snapshot = prepared.Page.Crawled.Snapshot;
        command.Parameters.AddWithValue("$slug", prepared.Page.Slug);
        command.Parameters.AddWithValue("$label", prepared.Page.Label);
        command.Parameters.AddWithValue("$title", snapshot.Title);
        command.Parameters.AddWithValue("$url", snapshot.SourceUrl);
        command.Parameters.AddWithValue("$fetched", snapshot.FetchedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(snapshot, MceJsonContext.Default.PageSnapshot));
        command.Parameters.AddWithValue("$raw", JsonSerializer.Serialize(prepared.Page.Crawled.HtmlDocuments, MceJsonContext.Default.StringArray));
        command.Parameters.AddWithValue("$count", prepared.Entries.Length);
        command.Parameters.AddWithValue("$hash", prepared.Hash);
        command.Parameters.AddWithValue("$checked", checkedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$generation", generation);
    }

    private static StoredPageSummary ReadPageSummary(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        ParseTimestamp(reader.GetString(4)), ParseTimestamp(reader.GetString(5)), reader.GetInt32(6), reader.GetInt64(7));

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string? ReadContentHash(SqliteConnection connection, SqliteTransaction transaction, string slug)
        => ExecuteScalar(connection, transaction, "SELECT content_hash FROM pages WHERE slug=$slug", ("$slug", slug)) as string;

    private static long ReadGeneration(SqliteConnection connection, SqliteTransaction transaction)
        => long.TryParse(ReadMeta(connection, transaction, "index_generation"), out var generation) ? generation : 0;

    private static string? ReadMeta(SqliteConnection connection, SqliteTransaction transaction, string key)
        => ExecuteScalar(connection, transaction, "SELECT value FROM meta WHERE key=$key", ("$key", key)) as string;

    private static void SetMeta(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
        => ExecuteNonQuery(connection, transaction,
            "INSERT INTO meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            ("$key", key), ("$value", value));

    private static object? ExecuteScalar(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        return command.ExecuteScalar();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        command.ExecuteNonQuery();
    }

    private static void AddParameters(SqliteCommand command, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string BuildFtsQuery(string query, SearchMode mode)
    {
        var escaped = query.Trim().Replace("\"", "\"\"", StringComparison.Ordinal);
        return mode == SearchMode.Phrase
            ? $"\"{escaped}\""
            : string.Join(" AND ", query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(term => $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static string SemanticHash(PageSnapshot snapshot)
    {
        var canonical = snapshot with { FetchedAt = DateTimeOffset.UnixEpoch };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, MceJsonContext.Default.PageSnapshot);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static ContentEntry[] BuildEntries(PageSnapshot snapshot)
    {
        var entries = new List<ContentEntry>();
        entries.AddRange(snapshot.Headings.Select(heading => new ContentEntry(ContentKind.Heading, heading.Text)));
        entries.AddRange(snapshot.Metrics.Select(metric => new ContentEntry(ContentKind.Metric,
            $"{metric.Label}: {metric.Value}{(metric.Delta is null ? string.Empty : $" ({metric.Delta})")}")));
        entries.AddRange(snapshot.Text.Select(value => new ContentEntry(ContentKind.Text, value)));
        foreach (var table in snapshot.Tables)
        {
            if (table.Title is not null) entries.Add(new ContentEntry(ContentKind.Table, table.Title));
            entries.AddRange(table.Rows.Select(row => new ContentEntry(ContentKind.Table, string.Join(" | ", row))));
        }
        entries.AddRange(snapshot.Cards.Select(card => new ContentEntry(
            ContentKind.Metric,
            $"{card.Code} | {card.Label}: {card.Value} | {card.Period} | {card.Detail} | {card.Description}")));
        foreach (var chart in snapshot.Charts)
        {
            entries.Add(new ContentEntry(
                ContentKind.Chart,
                $"{chart.Title} | {chart.Description} | {string.Join(" | ", chart.Notes)}"));
            entries.AddRange(chart.Series.Select(series => new ContentEntry(
                ContentKind.Chart,
                $"{chart.Title} | {series.Name} | {string.Join("; ", series.Points.Select(FormatChartPoint))}")));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return entries.Where(entry => seen.Add($"{entry.Kind}\0{entry.Text}")).ToArray();
    }

    private static IndexCard[] ExtractCards(PageSnapshot snapshot)
    {
        if (snapshot.Cards.Length > 0)
        {
            return snapshot.Cards;
        }

        var cards = new List<IndexCard>();
        for (var index = 0; index < snapshot.Text.Length; index++)
        {
            var code = snapshot.Text[index];
            if (!IndicatorCatalog.TryGet(code, out var definition)) continue;
            var cursor = index + 1;
            while (cursor < snapshot.Text.Length && snapshot.Text[cursor] == "UPDATED") cursor++;
            if (cursor >= snapshot.Text.Length) continue;
            var detail = cursor + 1 < snapshot.Text.Length && !IndicatorCatalog.TryGet(snapshot.Text[cursor + 1], out _)
                ? snapshot.Text[cursor + 1]
                : null;
            cards.Add(new IndexCard(
                definition.Code,
                definition.Label,
                snapshot.Text[cursor],
                detail,
                ExtractPeriod(detail),
                definition.Description));
        }
        return [.. cards];
    }

    private static string? ExtractPeriod(string? detail)
    {
        if (detail is null)
        {
            return null;
        }

        var candidate = detail.Split('·', 2)[0].Trim();
        return candidate.Length == 7 &&
               candidate[4] == '-' &&
               int.TryParse(candidate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
               int.TryParse(candidate.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? candidate
            : null;
    }

    private static string FormatChartPoint(ChartPoint point)
    {
        var value = point.Value?.ToString("G17", CultureInfo.InvariantCulture) ?? "null";
        return $"{point.Category}={value}{(point.Text is null ? string.Empty : $" ({point.Text})")}";
    }

    private static string ToStorage(ContentKind kind) => kind.ToString().ToLowerInvariant();

    private static ContentKind ParseContentKind(string value) => Enum.Parse<ContentKind>(value, true);

    private const string CreateSchemaSql = """
        CREATE TABLE pages (
            slug TEXT PRIMARY KEY,label TEXT NOT NULL,title TEXT NOT NULL,source_url TEXT NOT NULL UNIQUE,
            fetched_at TEXT NOT NULL,snapshot_json TEXT NOT NULL,raw_documents_json TEXT NOT NULL,text_count INTEGER NOT NULL,
            content_hash TEXT NOT NULL,last_checked_at TEXT NOT NULL,generation INTEGER NOT NULL);
        CREATE TABLE cards (
            page_slug TEXT NOT NULL REFERENCES pages(slug) ON DELETE CASCADE,code TEXT NOT NULL,label TEXT NOT NULL,
            value TEXT NOT NULL,detail TEXT,period TEXT,description TEXT NOT NULL,seq INTEGER NOT NULL,PRIMARY KEY(page_slug,code));
        CREATE TABLE content_entries (
            id INTEGER PRIMARY KEY,page_slug TEXT NOT NULL REFERENCES pages(slug) ON DELETE CASCADE,
            kind TEXT NOT NULL,text TEXT NOT NULL,seq INTEGER NOT NULL);
        CREATE INDEX idx_content_page ON content_entries(page_slug,seq);
        CREATE VIRTUAL TABLE content_fts USING fts5(
            page_slug UNINDEXED,kind UNINDEXED,text,content='content_entries',content_rowid='id',tokenize='trigram');
        CREATE TRIGGER content_entries_ai AFTER INSERT ON content_entries BEGIN
            INSERT INTO content_fts(rowid,page_slug,kind,text) VALUES(new.id,new.page_slug,new.kind,new.text);
        END;
        CREATE TRIGGER content_entries_ad AFTER DELETE ON content_entries BEGIN
            INSERT INTO content_fts(content_fts,rowid,page_slug,kind,text)
            VALUES('delete',old.id,old.page_slug,old.kind,old.text);
        END;
        CREATE TRIGGER content_entries_au AFTER UPDATE ON content_entries BEGIN
            INSERT INTO content_fts(content_fts,rowid,page_slug,kind,text)
            VALUES('delete',old.id,old.page_slug,old.kind,old.text);
            INSERT INTO content_fts(rowid,page_slug,kind,text) VALUES(new.id,new.page_slug,new.kind,new.text);
        END;
        CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        """;

    private sealed record ContentEntry(ContentKind Kind, string Text);
    private sealed record PreparedPage(IndexedPage Page, string Hash, ContentEntry[] Entries, IndexCard[] Cards);
}

public sealed record IndexedPage(string Slug, string Label, CrawledPage Crawled);
public sealed record ApplyPagesResult(int ChangedPages, int UnchangedPages, long Generation);
