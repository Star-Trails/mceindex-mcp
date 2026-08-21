package services

import (
	"context"
	"net/url"
	"testing"
	"time"

	"github.com/Star-Trails/mceindex-mcp/internal/config"
	"github.com/Star-Trails/mceindex-mcp/internal/domain"
	"github.com/Star-Trails/mceindex-mcp/internal/store"
	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

type mockCrawler struct{}

func (m *mockCrawler) Crawl(ctx context.Context, target *url.URL) (*domain.CrawledPage, error) {
	return nil, nil
}
func (m *mockCrawler) CloseBrowser() error { return nil }
func (m *mockCrawler) Close() error        { return nil }

func setupTestService(t *testing.T) (*MceIndexService, *store.Store) {
	st, err := store.NewStore(":memory:")
	require.NoError(t, err)

	now := time.Now().UTC()
	period := "2026-06"
	detail := "2026-06 · 同比"
	appTitle := "月度总览"
	fVal := 10.90

	overviewSnapshot := domain.PageSnapshot{
		SourceURL: "https://mceindex.com/Monthly_Overview",
		FetchedAt: now,
		Title:     "有意义中国经济指数",
		AppTitle:  &appTitle,
		Headings:  []domain.Heading{{Level: 1, Text: "月度总览"}},
		Metrics:   []domain.Metric{{Label: "产业规模", Value: "10.90%"}},
		Tables: []domain.DataTable{
			{
				Title:   new("统计表"),
				Headers: []string{"项目", "数值"},
				Rows:    [][]string{{"项目1", "100"}},
			},
		},
		Cards: []domain.IndexCard{
			{
				Code:        "LEI-GDP",
				Label:       "五大新产业规模占 GDP",
				Value:       "10.90%",
				Detail:      &detail,
				Period:      &period,
				Description: "指标描述",
			},
		},
		Charts: []domain.ChartData{
			{
				Title: "新产业占经济多大？",
				Series: []domain.ChartSeries{
					{
						Name: new("新产业经济规模占比"),
						Points: []domain.ChartPoint{
							{Category: &period, Value: &fVal},
						},
					},
				},
			},
		},
		Text: []string{"月度总览概况内容足够长以作为说明文本"},
	}

	_, err = st.ApplyPages([]domain.IndexedPage{
		{
			Slug:    "Monthly_Overview",
			Label:   "月度总览",
			Crawled: domain.CrawledPage{Snapshot: overviewSnapshot},
		},
	}, now)
	require.NoError(t, err)

	u, _ := url.Parse("https://mceindex.com/")
	opts := &config.Options{
		BaseURL:         u,
		RefreshInterval: 24 * time.Hour,
	}

	coord := NewRefreshCoordinator(opts, st, &mockCrawler{})
	svc := NewMceIndexService(st, coord)
	return svc, st
}

func TestMceServiceGetLatestAndDiscover(t *testing.T) {
	svc, st := setupTestService(t)
	defer st.Close()

	ctx := context.Background()
	latest, err := svc.GetLatest(ctx)
	require.NoError(t, err)
	require.NotNil(t, latest)
	assert.Equal(t, "https://mceindex.com/Monthly_Overview", latest.SourceURL)
	assert.NotEmpty(t, latest.AtAGlance)

	disc, err := svc.Discover(ctx)
	require.NoError(t, err)
	require.NotNil(t, disc)
	assert.NotEmpty(t, disc.Topics)
	assert.NotEmpty(t, disc.NextSteps)
}

func TestMceServiceGetIndicator(t *testing.T) {
	svc, st := setupTestService(t)
	defer st.Close()

	ctx := context.Background()
	ind, err := svc.GetIndicator(ctx, "LEI-GDP", 24)
	require.NoError(t, err)
	require.NotNil(t, ind)
	assert.Equal(t, "LEI-GDP", ind.Indicator.Code)

	// Bounds error check
	_, err = svc.GetIndicator(ctx, "LEI-GDP", 1)
	assert.Error(t, err)

	_, err = svc.GetIndicator(ctx, "UNKNOWN_CODE", 24)
	assert.Error(t, err)
}

func TestMceServiceGetPageAndSearch(t *testing.T) {
	svc, st := setupTestService(t)
	defer st.Close()

	ctx := context.Background()
	// Summary view
	resSum, err := svc.GetPage(ctx, "Monthly_Overview", domain.PageViewSummary, 0, 50)
	require.NoError(t, err)
	assert.Equal(t, domain.PageViewSummary, resSum.View)

	// Tables view
	resTbl, err := svc.GetPage(ctx, "Monthly_Overview", domain.PageViewTables, 0, 50)
	require.NoError(t, err)
	assert.Len(t, resTbl.Tables, 1)

	// Charts view
	resCh, err := svc.GetPage(ctx, "Monthly_Overview", domain.PageViewCharts, 0, 50)
	require.NoError(t, err)
	assert.Len(t, resCh.Charts, 1)

	// Search
	searchRes, err := svc.Search(ctx, "产业规模", nil, nil, domain.SearchModePhrase, 0, 20)
	require.NoError(t, err)
	assert.NotEmpty(t, searchRes.Hits)
}
