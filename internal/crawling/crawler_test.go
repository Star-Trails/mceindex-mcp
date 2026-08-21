package crawling

import (
	"net/url"
	"testing"

	"github.com/Star-Trails/mceindex-mcp/internal/domain"
	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestValidateProductionChartCoverage_MeaningfulRetail(t *testing.T) {
	u, err := url.Parse("https://mceindex.com/Meaningful_Retail")
	require.NoError(t, err)

	// Create 2 charts with total 150 points (75 each)
	val := 1.23
	makeSeries := func(count int) domain.ChartSeries {
		pts := make([]domain.ChartPoint, count)
		for i := range count {
			pts[i] = domain.ChartPoint{Value: &val}
		}
		return domain.ChartSeries{Points: pts}
	}

	charts := []domain.ChartData{
		{
			Title:  "图表 1",
			Series: []domain.ChartSeries{makeSeries(75)},
		},
		{
			Title:  "图表 2",
			Series: []domain.ChartSeries{makeSeries(75)},
		},
	}

	// 150 points should pass
	err = validateProductionChartCoverage(u, charts)
	assert.NoError(t, err)

	// 149 points should fail
	chartsUnder := []domain.ChartData{
		{
			Title:  "图表 1",
			Series: []domain.ChartSeries{makeSeries(75)},
		},
		{
			Title:  "图表 2",
			Series: []domain.ChartSeries{makeSeries(74)},
		},
	}
	err = validateProductionChartCoverage(u, chartsUnder)
	assert.Error(t, err)
}

func TestChartExtractionExpressionSyntax(t *testing.T) {
	// Ensure chartExtractionExpression starts with `() =>` or `function` and does not end with `()`
	assert.True(t, len(chartExtractionExpression) > 0)
	assert.Contains(t, chartExtractionExpression, "() =>")
	assert.NotContains(t, chartExtractionExpression, ")()")
}
