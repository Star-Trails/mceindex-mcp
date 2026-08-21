package parsing

import (
	"net/url"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestIsAccessChallenge(t *testing.T) {
	assert.True(t, IsAccessChallenge("<html><body>cf-chl-widget</body></html>"))
	assert.True(t, IsAccessChallenge("<script src='https://challenges.cloudflare.com/turnstile'></script>"))
	assert.True(t, IsAccessChallenge("Just a moment..."))
	assert.True(t, IsAccessChallenge("Verify you are human"))
	assert.False(t, IsAccessChallenge("<html><head><title>MCEIndex</title></head><body><h1>月度总览</h1></body></html>"))
}

func TestParserExtract(t *testing.T) {
	htmlFixture := `
<!DOCTYPE html>
<html>
<head>
    <title>MCEIndex - 有意义中国经济指数</title>
    <meta name="description" content="中国宏观经济指数月度总览">
</head>
<body>
    <div data-testid="stSidebar">
        <a href="/Monthly_Overview">月度总览</a>
        <a href="/LI_Monthly">五大新产业续命指数</a>
    </div>
    <div data-testid="stMain">
        <h1>有意义中国经济指数</h1>
        <h2>核心指标</h2>
        <div class="terminal-ticker-item">
            <span class="terminal-ticker-code">LEI-GDP</span>
            <span class="terminal-ticker-value">10.90%</span>
            <span class="terminal-ticker-comparison">2026-06 · 同比 +1.2%</span>
        </div>
        <div class="terminal-ticker-item">
            <span class="terminal-ticker-code">MRS</span>
            <span class="terminal-ticker-value">+0.0%</span>
            <span class="terminal-ticker-comparison">2026-06</span>
        </div>
        <div data-testid="stMetric" title="GDP 综合指数">
            <div data-testid="stMetricLabel">产业规模占比</div>
            <div data-testid="stMetricValue">10.90%</div>
            <div data-testid="stMetricDelta">+0.36%</div>
        </div>
        <h3>重点行业数据表</h3>
        <table>
            <thead>
                <tr><th>行业</th><th>规模 (亿元)</th><th>增速</th></tr>
            </thead>
            <tbody>
                <tr><td>新能源汽车</td><td>1200</td><td>+18.8%</td></tr>
                <tr><td>集成电路</td><td>850</td><td>+15.2%</td></tr>
            </tbody>
        </table>
        <p>新能源汽车与电池产业保持强劲增长势头。</p>
    </div>
</body>
</html>
`
	p := NewParser()
	u, err := url.Parse("https://mceindex.com/Monthly_Overview")
	require.NoError(t, err)

	now := time.Now()
	snapshot, err := p.Extract([]string{htmlFixture}, u, now)
	require.NoError(t, err)
	require.NotNil(t, snapshot)

	assert.Equal(t, "https://mceindex.com/Monthly_Overview", snapshot.SourceURL)
	assert.Equal(t, "MCEIndex - 有意义中国经济指数", snapshot.Title)
	require.NotNil(t, snapshot.Description)
	assert.Equal(t, "中国宏观经济指数月度总览", *snapshot.Description)
	require.NotNil(t, snapshot.AppTitle)
	assert.Equal(t, "有意义中国经济指数", *snapshot.AppTitle)

	// Cards
	require.Len(t, snapshot.Cards, 2)
	assert.Equal(t, "LEI-GDP", snapshot.Cards[0].Code)
	assert.Equal(t, "10.90%", snapshot.Cards[0].Value)
	require.NotNil(t, snapshot.Cards[0].Period)
	assert.Equal(t, "2026-06", *snapshot.Cards[0].Period)

	assert.Equal(t, "MRS", snapshot.Cards[1].Code)
	assert.Equal(t, "+0.0%", snapshot.Cards[1].Value)

	// Metrics
	require.Len(t, snapshot.Metrics, 1)
	assert.Equal(t, "产业规模占比", snapshot.Metrics[0].Label)
	assert.Equal(t, "10.90%", snapshot.Metrics[0].Value)
	require.NotNil(t, snapshot.Metrics[0].Delta)
	assert.Equal(t, "+0.36%", *snapshot.Metrics[0].Delta)

	// Tables
	require.Len(t, snapshot.Tables, 1)
	assert.Equal(t, []string{"行业", "规模 (亿元)", "增速"}, snapshot.Tables[0].Headers)
	assert.Len(t, snapshot.Tables[0].Rows, 2)
	assert.Equal(t, "重点行业数据表", *snapshot.Tables[0].Title)

	// Navigation
	require.Len(t, snapshot.Navigation, 2)
	assert.Equal(t, "月度总览", snapshot.Navigation[0].Text)
	require.NotNil(t, snapshot.Navigation[0].URL)
	assert.Equal(t, "https://mceindex.com/Monthly_Overview", *snapshot.Navigation[0].URL)
}

func TestParserErrorHandling(t *testing.T) {
	p := NewParser()
	u, _ := url.Parse("https://mceindex.com/")
	_, err := p.Extract([]string{}, u, time.Now())
	assert.Error(t, err)
}
