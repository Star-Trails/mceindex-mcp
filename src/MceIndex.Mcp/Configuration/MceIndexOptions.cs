namespace MceIndex.Mcp.Configuration;

public sealed record MceIndexOptions
{
    public const string DefaultBrowserUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36";
    private static readonly string[] BrowserCandidates =
    [
        "/usr/bin/google-chrome-stable",
        "/usr/bin/google-chrome",
        "/usr/bin/chromium",
        "/usr/bin/chromium-browser",
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    ];

    public required Uri BaseUri { get; init; }
    public required string DatabasePath { get; init; }
    public string? BrowserExecutable { get; init; }
    public string? NodeExecutable { get; init; }
    public required string BrowserUserAgent { get; init; }
    public string? BrowserProfile { get; init; }
    public string? CfClearance { get; init; }
    public bool Headless { get; init; }
    public TimeSpan RequestTimeout { get; init; }
    public TimeSpan DomQuietPeriod { get; init; }
    public TimeSpan CrawlDelay { get; init; }
    public TimeSpan RefreshInterval { get; init; }
    public int CrawlConcurrency { get; init; }
    public int MaxPages { get; init; }

    public static MceIndexOptions Load(IReadOnlyDictionary<string, string?>? values = null)
    {
        string? Get(string key) => values is null ? Environment.GetEnvironmentVariable(key) : values.GetValueOrDefault(key);

        var baseValue = Get("MCEINDEX_BASE_URL") ?? "https://mceindex.com/";
        if (!Uri.TryCreate(baseValue, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback))
        {
            throw new MceIndexException(
                MceIndexErrorCode.InvalidConfiguration,
                "MCEINDEX_BASE_URL must be an absolute HTTPS URL; HTTP is allowed only for loopback tests.");
        }

        var cacheRoot = Get("XDG_CACHE_HOME");
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        }

        var configuredBrowser = Get("MCEINDEX_BROWSER_EXECUTABLE");
        var browserExecutable = !string.IsNullOrWhiteSpace(configuredBrowser)
            ? configuredBrowser
            : BrowserCandidates.FirstOrDefault(File.Exists);
        var nodeExecutable = EmptyToNull(Get("PLAYWRIGHT_NODEJS_PATH")) ??
            FindExecutable(Get("PATH"), OperatingSystem.IsWindows() ? "node.exe" : "node");


        return new MceIndexOptions
        {
            BaseUri = baseUri,
            DatabasePath = Get("MCEINDEX_DB_PATH") ?? Path.Combine(cacheRoot, "mceindex_mcp", "mceindex.db"),
            BrowserExecutable = browserExecutable,
            NodeExecutable = nodeExecutable,
            BrowserUserAgent = EmptyToNull(Get("MCEINDEX_BROWSER_USER_AGENT")) ?? DefaultBrowserUserAgent,
            BrowserProfile = EmptyToNull(Get("MCEINDEX_BROWSER_PROFILE")),
            CfClearance = EmptyToNull(Get("MCEINDEX_CF_CLEARANCE")),
            Headless = ParseBoolean(Get("MCEINDEX_HEADLESS"), true, "MCEINDEX_HEADLESS"),
            RequestTimeout = TimeSpan.FromMilliseconds(ParseInteger(Get("MCEINDEX_TIMEOUT_MS"), 45_000, 1, 300_000, "MCEINDEX_TIMEOUT_MS")),
            DomQuietPeriod = TimeSpan.FromMilliseconds(ParseInteger(Get("MCEINDEX_SETTLE_MS"), 1_200, 100, 30_000, "MCEINDEX_SETTLE_MS")),
            RefreshInterval = TimeSpan.FromMilliseconds(ParseInteger(Get("MCEINDEX_REFRESH_INTERVAL_MS"), 86_400_000, 60_000, int.MaxValue, "MCEINDEX_REFRESH_INTERVAL_MS")),
            CrawlDelay = TimeSpan.FromMilliseconds(ParseInteger(Get("MCEINDEX_CRAWL_DELAY_MS"), 3_000, 0, 60_000, "MCEINDEX_CRAWL_DELAY_MS")),
            CrawlConcurrency = ParseInteger(Get("MCEINDEX_CRAWL_CONCURRENCY"), 1, 1, 4, "MCEINDEX_CRAWL_CONCURRENCY"),
            MaxPages = ParseInteger(Get("MCEINDEX_MAX_PAGES"), 20, 5, 100, "MCEINDEX_MAX_PAGES"),
        };
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


    private static bool ParseBoolean(string? value, bool fallback, string name) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => fallback,
        "true" or "1" => true,
        "false" or "0" => false,
        _ => throw new MceIndexException(MceIndexErrorCode.InvalidConfiguration, $"{name} must be true, false, 1, or 0."),
    };

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
