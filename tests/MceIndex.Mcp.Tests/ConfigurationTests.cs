using MceIndex.Mcp.Configuration;

namespace MceIndex.Mcp.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void LoadsDefaultsAndExplicitValues()
    {
        var options = MceIndexOptions.Load(new Dictionary<string, string?>
        {
            ["MCEINDEX_BASE_URL"] = "http://127.0.0.1:3000/",
            ["MCEINDEX_DB_PATH"] = ":memory:",
            ["MCEINDEX_CAMOFOX_URL"] = "http://127.0.0.1:9477/api/",
            ["MCEINDEX_CAMOFOX_EXECUTABLE"] = "/opt/camofox/bin/camofox-browser",
            ["MCEINDEX_CAMOFOX_ACCESS_KEY"] = "test-key",
            ["MCEINDEX_CAMOFOX_PROFILE"] = "/tmp/camofox-profile",
            ["MCEINDEX_TIMEOUT_MS"] = "12000",
            ["MCEINDEX_SETTLE_MS"] = "500",
            ["MCEINDEX_REFRESH_INTERVAL_MS"] = "60000",
            ["MCEINDEX_CRAWL_DELAY_MS"] = "250",
            ["MCEINDEX_CRAWL_CONCURRENCY"] = "3",
        });

        Assert.Equal(new Uri("http://127.0.0.1:3000/"), options.BaseUri);
        Assert.Equal(":memory:", options.DatabasePath);
        Assert.Equal(new Uri("http://127.0.0.1:9477/api/"), options.CamofoxUri);
        Assert.Equal("/opt/camofox/bin/camofox-browser", options.CamofoxExecutable);
        Assert.Equal("test-key", options.CamofoxAccessKey);
        Assert.Equal("/tmp/camofox-profile", options.CamofoxProfile);
        Assert.Equal(TimeSpan.FromSeconds(12), options.RequestTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.DomQuietPeriod);
        Assert.Equal(TimeSpan.FromMinutes(1), options.RefreshInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.CrawlDelay);
        Assert.Equal(3, options.CrawlConcurrency);

        var defaults = MceIndexOptions.Load(new Dictionary<string, string?>
        {
            ["MCEINDEX_BASE_URL"] = "http://127.0.0.1:3000/",
            ["PATH"] = string.Empty,
            ["XDG_CACHE_HOME"] = "/tmp/mceindex-test-cache",
        });
        Assert.Equal(MceIndexOptions.DefaultCamofoxUri, defaults.CamofoxUri);
        Assert.Null(defaults.CamofoxExecutable);
        Assert.Null(defaults.CamofoxAccessKey);
        Assert.Equal("/tmp/mceindex-test-cache/mceindex_mcp/camofox", defaults.CamofoxProfile);
        Assert.Equal(TimeSpan.FromHours(24), defaults.RefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), defaults.CrawlDelay);
        Assert.Equal(1, defaults.CrawlConcurrency);
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("not-a-url")]
    public void RejectsUnsafeBaseUrls(string url)
    {
        var error = Assert.Throws<MceIndexException>(() => MceIndexOptions.Load(
            new Dictionary<string, string?> { ["MCEINDEX_BASE_URL"] = url }));

        Assert.Equal(MceIndexErrorCode.InvalidConfiguration, error.Code);
    }

    [Fact]
    public void RequiresAccessKeyForRemoteCamofox()
    {
        var error = Assert.Throws<MceIndexException>(() => MceIndexOptions.Load(
            new Dictionary<string, string?>
            {
                ["MCEINDEX_BASE_URL"] = "http://127.0.0.1:3000/",
                ["MCEINDEX_CAMOFOX_URL"] = "https://camofox.example.com/",
            }));

        Assert.Equal(MceIndexErrorCode.InvalidConfiguration, error.Code);
        Assert.Contains("MCEINDEX_CAMOFOX_ACCESS_KEY", error.Message, StringComparison.Ordinal);
    }
}
