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
            ["MCEINDEX_BROWSER_USER_AGENT"] = "test-agent",
            ["PLAYWRIGHT_NODEJS_PATH"] = "/opt/node/bin/node",
            ["MCEINDEX_HEADLESS"] = "false",
            ["MCEINDEX_TIMEOUT_MS"] = "12000",
            ["MCEINDEX_SETTLE_MS"] = "500",
            ["MCEINDEX_REFRESH_INTERVAL_MS"] = "60000",
            ["MCEINDEX_CRAWL_DELAY_MS"] = "250",
            ["MCEINDEX_CRAWL_CONCURRENCY"] = "3",
        });

        Assert.Equal(new Uri("http://127.0.0.1:3000/"), options.BaseUri);
        Assert.Equal(":memory:", options.DatabasePath);
        Assert.Equal("test-agent", options.BrowserUserAgent);
        Assert.Equal("/opt/node/bin/node", options.NodeExecutable);
        Assert.False(options.Headless);
        Assert.Equal(TimeSpan.FromSeconds(12), options.RequestTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.DomQuietPeriod);
        Assert.Equal(TimeSpan.FromMinutes(1), options.RefreshInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.CrawlDelay);
        Assert.Equal(3, options.CrawlConcurrency);

        var defaults = MceIndexOptions.Load(new Dictionary<string, string?>
        {
            ["MCEINDEX_BASE_URL"] = "http://127.0.0.1:3000/",
        });
        Assert.True(defaults.Headless);
        Assert.Null(defaults.BrowserProfile);
        Assert.Equal(MceIndexOptions.DefaultBrowserUserAgent, defaults.BrowserUserAgent);
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
}
