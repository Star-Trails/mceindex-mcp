package projectors

import (
	"fmt"
	"html"
	"math"
	"strconv"
	"strings"
	"time"

	"github.com/Star-Trails/mceindex-mcp/internal/domain"
)

// ProjectCharts sanitizes chart titles, descriptions, categories and numbers for MCP responses.
func ProjectCharts(charts []domain.ChartData) []domain.ChartData {
	res := make([]domain.ChartData, len(charts))
	for i, ch := range charts {
		res[i] = projectChart(ch)
	}
	return res
}

func projectChart(chart domain.ChartData) domain.ChartData {
	series := make([]domain.ChartSeries, len(chart.Series))
	for i, s := range chart.Series {
		series[i] = projectSeries(s)
	}

	xAxisTitle := emptyToNil(plainText(chart.XAxisTitle))
	yAxisTitle := emptyToNil(plainText(chart.YAxisTitle))
	originalTitle := chart.Title
	title := plainText(&originalTitle)
	if title == "" {
		if yAxisTitle != nil {
			title = *yAxisTitle
		} else if xAxisTitle != nil {
			title = *xAxisTitle
		} else {
			for _, s := range series {
				if s.Name != nil && strings.TrimSpace(*s.Name) != "" {
					title = *s.Name
					break
				}
			}
			if title == "" {
				title = "图表"
			}
		}
	}

	description := plainText(&chart.Description)
	if description == "" || (strings.Contains(originalTitle, "<") && strings.Contains(chart.Description, originalTitle)) {
		description = fmt.Sprintf("MCEIndex 页面中的“%s”图表。", title)
	}

	var notes []string
	for _, n := range chart.Notes {
		pt := plainText(&n)
		if pt != "" {
			notes = append(notes, pt)
		}
	}

	return domain.ChartData{
		Title:       title,
		Description: description,
		Notes:       notes,
		XAxisTitle:  xAxisTitle,
		YAxisTitle:  yAxisTitle,
		Series:      series,
	}
}

func projectSeries(series domain.ChartSeries) domain.ChartSeries {
	name := emptyToNil(plainText(series.Name))
	points := make([]domain.ChartPoint, len(series.Points))
	for i, pt := range series.Points {
		points[i] = projectPoint(pt)
	}
	return domain.ChartSeries{
		Name:   name,
		Type:   series.Type,
		Points: points,
	}
}

func projectPoint(point domain.ChartPoint) domain.ChartPoint {
	var val *float64
	if point.Value != nil {
		norm := normalizeNumber(*point.Value)
		val = &norm
	}

	text := emptyToNil(plainText(point.Text))
	displayValue := emptyToNil(plainText(point.DisplayValue))
	if displayValue == nil {
		displayValue = text
	}
	if displayValue == nil && val != nil {
		s := strconv.FormatFloat(*val, 'g', 15, 64)
		displayValue = &s
	}

	return domain.ChartPoint{
		Category:     normalizeCategory(point.Category),
		Value:        val,
		Text:         text,
		DisplayValue: displayValue,
	}
}

func normalizeNumber(val float64) float64 {
	if math.IsNaN(val) || math.IsInf(val, 0) || val == 0 {
		return val
	}

	decPlaces := 14 - int(math.Floor(math.Log10(math.Abs(val))))
	if decPlaces >= 0 {
		if decPlaces <= 15 {
			pow := math.Pow(10, float64(decPlaces))
			return math.Round(val*pow) / pow
		}
		return val
	}

	scale := math.Pow(10, float64(-decPlaces))
	return math.Round(val/scale) * scale
}

func normalizeCategory(val *string) *string {
	if val == nil {
		return nil
	}
	cat := emptyToNil(plainText(val))
	if cat == nil || isYearMonth(*cat) {
		return cat
	}

	t, err := time.Parse(time.RFC3339, *cat)
	if err != nil {
		t, err = time.Parse("2006-01-02", *cat)
	}
	if err != nil {
		return cat
	}

	if t.Day() == 1 && t.Hour() == 0 && t.Minute() == 0 && t.Second() == 0 {
		ym := t.Format("2006-01")
		return &ym
	}

	iso := t.Format(time.RFC3339Nano)
	return &iso
}

func isYearMonth(val string) bool {
	if len(val) != 7 || val[4] != '-' {
		return false
	}
	_, err1 := strconv.Atoi(val[:4])
	m, err2 := strconv.Atoi(val[5:7])
	return err1 == nil && err2 == nil && m >= 1 && m <= 12
}

func plainText(val *string) string {
	if val == nil || strings.TrimSpace(*val) == "" {
		return ""
	}

	s := *val
	var b strings.Builder
	inTag := false
	for i := 0; i < len(s); i++ {
		if s[i] == '<' {
			inTag = true
			continue
		}
		if s[i] == '>' {
			inTag = false
			continue
		}
		if !inTag {
			b.WriteByte(s[i])
		}
	}
	return strings.TrimSpace(html.UnescapeString(b.String()))
}

func emptyToNil(val string) *string {
	if strings.TrimSpace(val) == "" {
		return nil
	}
	return &val
}
