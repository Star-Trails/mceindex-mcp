package projectors

import (
	"testing"
	"time"

	"github.com/Star-Trails/mceindex-mcp/internal/domain"
	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestIndicatorTrendCalculation(t *testing.T) {
	points := []domain.ChartPoint{
		{Category: new("2025-05"), Value: new(10.0)},
		{Category: new("2025-06"), Value: new(10.1)},
		{Category: new("2025-07"), Value: new(10.2)},
		{Category: new("2025-12"), Value: new(10.3)},
		{Category: new("2026-01"), Value: new(10.4)},
		{Category: new("2026-02"), Value: new(10.5)},
		{Category: new("2026-03"), Value: new(10.6)},
		{Category: new("2026-04"), Value: new(10.7)},
		{Category: new("2026-05"), Value: new(10.8)},
		{Category: new("2026-06"), Value: new(10.9)},
	}

	pages := map[string]*domain.StoredPage{
		"LI_Monthly": {
			Summary: domain.StoredPageSummary{Slug: "LI_Monthly"},
			Snapshot: domain.PageSnapshot{
				Charts: []domain.ChartData{
					{
						Title: "五大新产业",
						Series: []domain.ChartSeries{
							{
								Name:   new("产业规模占GDP比重"),
								Points: points,
							},
						},
					},
				},
			},
		},
	}

	trend := BuildIndicatorTrend("LEI-GDP", pages, 24)
	require.NotNil(t, trend)
	assert.Equal(t, "2026-06", trend.CurrentPeriod)
	assert.Equal(t, 10.9, trend.Current)
	require.NotNil(t, trend.Previous)
	assert.Equal(t, 10.8, *trend.Previous)
	require.NotNil(t, trend.MonthOverMonthChange)
	assert.InDelta(t, 0.1, *trend.MonthOverMonthChange, 0.0001)

	// YearAgo is 2025-06 -> 10.1
	require.NotNil(t, trend.YearAgo)
	assert.Equal(t, 10.1, *trend.YearAgo)
	require.NotNil(t, trend.YearOverYearChange)
	assert.InDelta(t, 0.8, *trend.YearOverYearChange, 0.0001)

	assert.Equal(t, domain.TrendRising, trend.Direction)
	assert.Equal(t, domain.AssessmentImproving, trend.Assessment)
}

func TestIndeterminateAssessmentForCPIAndFinancing(t *testing.T) {
	cpiPoints := []domain.ChartPoint{
		{Category: new("2026-04"), Value: new(0.1)},
		{Category: new("2026-05"), Value: new(0.3)},
		{Category: new("2026-06"), Value: new(0.5)},
	}

	pages := map[string]*domain.StoredPage{
		"Meaningful_CPI_PPI": {
			Summary: domain.StoredPageSummary{Slug: "Meaningful_CPI_PPI"},
			Snapshot: domain.PageSnapshot{
				Charts: []domain.ChartData{
					{
						Title: "有意义 CPI 图表",
						Series: []domain.ChartSeries{
							{
								Name:   new("有意义 CPI"),
								Points: cpiPoints,
							},
						},
					},
				},
			},
		},
	}

	trend := BuildIndicatorTrend("MCPI", pages, 12)
	require.NotNil(t, trend)
	assert.Equal(t, domain.TrendRising, trend.Direction)
	// MCPI must always evaluate to Indeterminate even if Rising
	assert.Equal(t, domain.AssessmentIndeterminate, trend.Assessment)
}

func TestOverviewProjector(t *testing.T) {
	cards := []domain.IndexCard{
		{
			Code:        "LEI-GDP",
			Label:       "五大新产业规模占 GDP",
			Value:       "10.90%",
			Period:      new("2026-06"),
			Detail:      new("2026-06 · P99"),
			Description: "五大新产业规模说明",
		},
	}

	charts := []domain.ChartData{
		{
			Title: "新产业占经济多大？",
			Series: []domain.ChartSeries{
				{
					Name: new("新产业经济规模占比"),
					Points: []domain.ChartPoint{
						{Category: new("2026-06"), Value: new(10.90)},
					},
				},
				{
					Name: new("12M 均线"),
					Points: []domain.ChartPoint{
						{Category: new("2026-06"), Value: new(9.76)},
					},
				},
			},
		},
	}

	sections := BuildOverviewSections(cards, charts, nil)
	require.NotEmpty(t, sections)
	sec := sections[0]
	assert.Equal(t, "LEI-GDP", sec.Code)
	assert.Equal(t, "新产业占经济多大？", sec.Title)
	require.Len(t, sec.Readings, 3)
	assert.Equal(t, "industryScaleShare", sec.Readings[0].Key)
	assert.Equal(t, "10.90%", sec.Readings[0].DisplayValue)
	assert.Equal(t, "movingAverage12m", sec.Readings[1].Key)
	assert.Equal(t, "9.76%", sec.Readings[1].DisplayValue)
	assert.Equal(t, "historicalPercentile", sec.Readings[2].Key)
	assert.Equal(t, "P99", sec.Readings[2].DisplayValue)
	require.NotNil(t, sec.Readings[0].Verification)
	assert.True(t, sec.Readings[0].Verification.AppliesToCurrentPeriod)
}

func TestChartResponseProjector(t *testing.T) {
	rawCharts := []domain.ChartData{
		{
			Title:       "<b>产业规模</b> 趋势",
			Description: "包含 <i>HTML</i> 标签的描述",
			Notes:       []string{"<b>注：</b>口径说明"},
			Series: []domain.ChartSeries{
				{
					Name: new("规模占比"),
					Type: new("scatter"),
					Points: []domain.ChartPoint{
						{
							Category: new("2026-06-01T00:00:00Z"),
							Value:    new(10.900000000000002),
							Text:     new("<b>10.90%</b>"),
						},
					},
				},
			},
		},
	}

	projected := ProjectCharts(rawCharts)
	require.Len(t, projected, 1)
	assert.Equal(t, "产业规模 趋势", projected[0].Title)
	assert.Equal(t, "包含 HTML 标签的描述", projected[0].Description)
	assert.Equal(t, []string{"注：口径说明"}, projected[0].Notes)

	pt := projected[0].Series[0].Points[0]
	require.NotNil(t, pt.Category)
	assert.Equal(t, "2026-06", *pt.Category)
	require.NotNil(t, pt.Text)
	assert.Equal(t, "10.90%", *pt.Text)
}

func TestDataDiscoveryProjector(t *testing.T) {
	latest := domain.LatestOverview{
		SourceURL:  "https://mceindex.com/Monthly_Overview",
		FetchedAt:  time.Now(),
		Generation: 1,
		AtAGlance: []domain.OverviewSection{
			{
				Code:        "LEI-GDP",
				Title:       "新产业占经济多大？",
				Period:      new("2026-06"),
				Description: "产业规模说明",
				Readings: []domain.OverviewReading{
					{
						Key:          "industryScaleShare",
						Label:        "五大新产业规模占 GDP",
						DisplayValue: "10.90%",
						Unit:         new("%"),
					},
				},
			},
		},
	}

	pages := []domain.StoredPageSummary{
		{
			Slug:  "Monthly_Overview",
			Label: "月度总览",
		},
	}

	disc := BuildDataDiscovery(&latest, pages)
	assert.Contains(t, disc.Summary, "1 个主题")
	require.Len(t, disc.Topics, 1)
	assert.Equal(t, "LEI-GDP", disc.Topics[0].Code)
	assert.NotEmpty(t, disc.NextSteps)
}
