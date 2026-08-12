namespace MceIndex.Mcp.Configuration;

public sealed record MceIndexOptions
{
    public static readonly Uri DefaultCamofoxUri = new("http://127.0.0.1:9377/");

    public required Uri BaseUri { get; init; }
    public required string DatabasePath { get; init; }
    public required Uri CamofoxUri { get; init; }
    public string? CamofoxExecutable { get; init; }
    public string? CamofoxAccessKey { get; init; }
    public string? CamofoxProfile { get; init; }
    public TimeSpan RequestTimeout { get; init; }
    public TimeSpan DomQuietPeriod { get; init; }
    public TimeSpan CrawlDelay { get; init; }
    public TimeSpan RefreshInterval { get; init; }
    public int CrawlConcurrency { get; init; }
    public int MaxPages { get; init; }

    public static MceIndexOptions Load(IReadOnlyDictionary<string, string?>? values = null)
    {
        string? Get(string key) => values is null ? Environment.GetEnvironmentVariable(key) : values.GetValueOrDefault(key);

        var baseUri = ParseApplicationUri(
            Get("MCEINDEX_BASE_URL") ?? "https://mceindex.com/",
            "MCEINDEX_BASE_URL");
        var camofoxUri = ParseCamofoxUri(Get("MCEINDEX_CAMOFOX_URL") ?? DefaultCamofoxUri.AbsoluteUri);
        var camofoxAccessKey = EmptyToNull(Get("MCEINDEX_CAMOFOX_ACCESS_KEY"));
        if (!camofoxUri.IsLoopback && camofoxAccessKey is null)
        {
            throw new MceIndexException(
                MceIndexErrorCode.InvalidConfiguration,
                "MCEINDEX_CAMOFOX_ACCESS_KEY is required for a non-loopback Camofox service.");
        }

        var cacheRoot = Get("XDG_CACHE_HOME");
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        }

        var configuredExecutable = EmptyToNull(Get("MCEINDEX_CAMOFOX_EXECUTABLE"));
        var executableName = OperatingSystem.IsWindows() ? "camofox-browser.cmd" : "camofox-browser";
        var camofoxExecutable = configuredExecutable is null
            ? FindExecutable(Get("PATH"), executableName)
            : Path.GetFullPath(configuredExecutable);

        return new MceIndexOptions
        {
            BaseUri = baseUri,
            DatabasePath = Get("MCEINDEX_DB_PATH") ?? Path.Combine(cacheRoot, "mceindex_mcp", "mceindex.db"),
            CamofoxUri = camofoxUri,
            CamofoxExecutable = camofoxExecutable,
            CamofoxAccessKey = camofoxAccessKey,
            CamofoxProfile = EmptyToNull(Get("MCEINDEX_CAMOFOX_PROFILE")) ??
                Path.Combine(cacheRoot, "mceindex_mcp", "camofox"),
            RequestTimeout = TimeSpan.FromMilliseconds(ParseInteger(Get("MCEINDEX_TIMEOUT_MS"), 45_000, 1, 300_000, "MCEINDEX_TIMEOUT_MS")),
            DomQuietPeriod = TimeSpan.FromMilliseconds(ParseInteger(Get("MCEINDEX_SETTLE_MS"), 1_200, 100, 30_000, "MCEINDEX_SETTLE_MS")),
            RefreshInterval = TimeSpan.FromMilliseconds(ParseInteger(Get("MCEINDEX_REFRESH_INTERVAL_MS"), 86_400_000, 60_000, int.MaxValue, "MCEINDEX_REFRESH_INTERVAL_MS")),
            CrawlDelay = TimeSpan.FromMilliseconds(ParseInteger(Get("MCEINDEX_CRAWL_DELAY_MS"), 3_000, 0, 60_000, "MCEINDEX_CRAWL_DELAY_MS")),
            CrawlConcurrency = ParseInteger(Get("MCEINDEX_CRAWL_CONCURRENCY"), 1, 1, 4, "MCEINDEX_CRAWL_CONCURRENCY"),
            MaxPages = ParseInteger(Get("MCEINDEX_MAX_PAGES"), 20, 5, 100, "MCEINDEX_MAX_PAGES"),
        };
    }

    private static Uri ParseApplicationUri(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            throw new MceIndexException(
                MceIndexErrorCode.InvalidConfiguration,
                $"{name} must be an absolute HTTPS URL; HTTP is allowed only for loopback tests.");
        }
        return uri;
    }

    private static Uri ParseCamofoxUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback))
        {
            throw new MceIndexException(
                MceIndexErrorCode.InvalidConfiguration,
                "MCEINDEX_CAMOFOX_URL must use HTTPS; HTTP is allowed only for a loopback service.");
        }

        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }
        return builder.Uri;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? FindExecutable(string? searchPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(searchPath))
        {
            return null;
        }

        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim().Trim('"'), fileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    private static int ParseInteger(string? value, int fallback, int minimum, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            throw new MceIndexException(
                MceIndexErrorCode.InvalidConfiguration,
                $"{name} must be an integer between {minimum} and {maximum}.");
        }
        return parsed;
    }
}
