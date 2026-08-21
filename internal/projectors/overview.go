package projectors

import (
	"fmt"
	"regexp"
	"strconv"
	"strings"

	"github.com/Star-Trails/mceindex-mcp/internal/domain"
)

var numberRegex = regexp.MustCompile(`[-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?`)

// BuildOverviewSections constructs the 6 structured overview sections with readings and verifications.
func BuildOverviewSections(cards []domain.IndexCard, charts []domain.ChartData, evidencePages map[string]*domain.StoredPage) []domain.OverviewSection {
	var sections []domain.OverviewSection

	addIndustry(&sections, cards, charts)
	addEmployment(&sections, cards, charts)
	addFiscal(&sections, cards, charts)
	addRetail(&sections, cards, charts)
	addPrices(&sections, cards, charts)
	addFinancing(&sections, cards, charts)

	result := make([]domain.OverviewSection, len(sections))
	for i, sec := range sections {
		readings := make([]domain.OverviewReading, len(sec.Readings))
		for rIdx, r := range sec.Readings {
			v := BuildVerification(sec.Code, r.Key, sec.Period)
			readings[rIdx] = domain.OverviewReading{
				Key:          r.Key,
				Label:        r.Label,
				Value:        r.Value,
				DisplayValue: r.DisplayValue,
				Unit:         r.Unit,
				Verification: &v,
			}
		}
		result[i] = domain.OverviewSection{
			Code:        sec.Code,
			Title:       sec.Title,
			Period:      sec.Period,
			Description: sec.Description,
			Readings:    readings,
			Notes:       BuildOverviewNotes(sec.Code, evidencePages),
		}
	}
	return result
}

func addIndustry(sections *[]domain.OverviewSection, cards []domain.IndexCard, charts []domain.ChartData) {
	card := findCard(cards, "LEI-GDP")
	chart := findChart(charts, "新产业占经济多大？")
	var readings []domain.OverviewReading

	var share *float64
	if chart != nil {
		for _, s := range chart.Series {
			if s.Name != nil && *s.Name == "新产业经济规模占比" && len(s.Points) > 0 {
				share = s.Points[len(s.Points)-1].Value
				break
			}
		}
	}
	if share == nil && card != nil {
		share = parseNumber(card.Value)
	}

	dispShare := formatPercent(share, 2)
	if card != nil && card.Value != "" {
		dispShare = card.Value
	}
	addReading(&readings, "industryScaleShare", "五大新产业规模占 GDP", share, dispShare, new("%"))

	var avg12m *float64
	if chart != nil {
		for _, s := range chart.Series {
			if s.Name != nil && *s.Name == "12M 均线" && len(s.Points) > 0 {
				avg12m = s.Points[len(s.Points)-1].Value
				break
			}
		}
	}
	addReading(&readings, "movingAverage12m", "12M 均线", avg12m, formatPercent(avg12m, 2), new("%"))

	var percentile *float64
	if card != nil && card.Detail != nil {
		percentile = numberAfter(*card.Detail, "P")
	}
	var dispP *string
	if percentile != nil {
		dispP = new(fmt.Sprintf("P%.0f", *percentile))
	}
	dispPStr := ""
	if dispP != nil {
		dispPStr = *dispP
	}
	addReading(&readings, "historicalPercentile", "历史分位", percentile, dispPStr, nil)

	addSection(sections, "LEI-GDP", "新产业占经济多大？", card, chart, readings)
}

func addEmployment(sections *[]domain.OverviewSection, cards []domain.IndexCard, charts []domain.ChartData) {
	card := findCard(cards, "LEI-EMP")
	chart := findChart(charts, "新产业能吸收多少就业？")
	var readings []domain.OverviewReading

	stockPoint := findPoint(chart, "理论就业规模", "理论就业规模", 0)
	var stock *float64
	if stockPoint != nil {
		pv := pointValue(stockPoint, "理论就业规模")
		if pv != nil {
			val := *pv / 10_000.0
			stock = &val
		}
	}
	if stock == nil && card != nil {
		stock = parseNumber(card.Value)
	}
	dispStock := pointDisplay(stockPoint)
	if card != nil && card.Value != "" {
		dispStock = card.Value
	}
	addReading(&readings, "theoreticalEmploymentStock", "五大新产业理论就业存量", stock, dispStock, new("万人"))

	var contribution *float64
	if card != nil && card.Detail != nil {
		contribution = numberAfter(*card.Detail, "就业贡献")
	}
	addReading(&readings, "employmentContribution", "就业续命读数", contribution, formatPercent(contribution, 2), new("%"))

	addEmploymentReference(&readings, chart, "graduates2026", "2026届高校毕业生", 1)
	addEmploymentReference(&readings, chart, "rideHailingDrivers", "网约车持证司机", 2)
	addEmploymentReference(&readings, chart, "deliveryRiders", "外卖骑手", 3)

	addSection(sections, "LEI-EMP", "新产业能吸收多少就业？", card, chart, readings)
}

func addEmploymentReference(readings *[]domain.OverviewReading, chart *domain.ChartData, key, label string, index int) {
	pt := findPoint(chart, "理论就业规模", label, index)
	var val *float64
	if pt != nil {
		pv := pointValue(pt, label)
		if pv != nil {
			v := *pv / 10_000.0
			val = &v
		}
	}
	addReading(readings, key, label, val, pointDisplay(pt), new("万人"))
}

func addFiscal(sections *[]domain.OverviewSection, cards []domain.IndexCard, charts []domain.ChartData) {
	card := findCard(cards, "LEI-FIS")
	chart := findChart(charts, "新产业形成多少净财政贡献？")
	var readings []domain.OverviewReading

	contribPt := findPoint(chart, "估算年化净财政贡献", "净财政贡献", 0)
	var contrib *float64
	if contribPt != nil {
		contrib = pointValue(contribPt, "净财政贡献")
	}
	if contrib == nil && card != nil {
		contrib = parseNumber(card.Value)
	}
	dispContrib := pointDisplay(contribPt)
	if card != nil && card.Value != "" {
		dispContrib = card.Value
	}
	addReading(&readings, "annualizedNetFiscalContribution", "估算年化净财政贡献", contrib, dispContrib, new("亿元"))

	var contribRate *float64
	if card != nil && card.Detail != nil {
		contribRate = numberAfter(*card.Detail, "财政贡献")
	}
	addReading(&readings, "fiscalContribution", "财政续命读数", contribRate, formatPercent(contribRate, 2), new("%"))

	addFiscalReference(&readings, chart, "defenseBudget", "国防预算", 0)
	addFiscalReference(&readings, chart, "debtInterest", "债务付息", 1)
	addFiscalReference(&readings, chart, "educationSpending", "教育支出", 2)
	addFiscalReference(&readings, chart, "landSaleRevenue", "土地出让收入", 3)
	addFiscalReference(&readings, chart, "centralTransfers", "中央转移支付", 4)

	addSection(sections, "LEI-FIS", "新产业形成多少净财政贡献？", card, chart, readings)
}

func addFiscalReference(readings *[]domain.OverviewReading, chart *domain.ChartData, key, label string, index int) {
	pt := findPoint(chart, "公共财政量级参照", label, index)
	addReading(readings, key, label, pointValue(pt, label), pointDisplay(pt), new("亿元"))
}

func addRetail(sections *[]domain.OverviewSection, cards []domain.IndexCard, charts []domain.ChartData) {
	card := findCard(cards, "MRS")
	chart := findChart(charts, "消费哪里在撑、哪里在拖？")
	var readings []domain.OverviewReading

	var meaningful *float64
	if card != nil {
		meaningful = parseNumber(card.Value)
	}
	dispMeaningful := formatPercent(meaningful, 1)
	if card != nil && card.Value != "" {
		dispMeaningful = card.Value
	}
	addReading(&readings, "meaningfulRetail", "有意义社零同比", meaningful, dispMeaningful, new("%"))

	addRetailReading(&readings, chart, "belowDesignated", "限额以下", 0)
	addRetailReading(&readings, chart, "aboveDesignated", "限额以上", 1)
	addRetailReading(&readings, chart, "durablesPropertyChain", "耐用品/地产链", 2)

	addSection(sections, "MRS", "消费哪里在撑、哪里在拖？", card, chart, readings)
}

func addRetailReading(readings *[]domain.OverviewReading, chart *domain.ChartData, key, label string, index int) {
	pt := findPoint(chart, "", label, index)
	addReading(readings, key, label, pointValue(pt, label), pointDisplay(pt), new("%"))
}

func addPrices(sections *[]domain.OverviewSection, cards []domain.IndexCard, charts []domain.ChartData) {
	card := findCard(cards, "MCPI")
	chart := findChart(charts, "物价中有多少来自选定短期扰动？")
	var readings []domain.OverviewReading

	addPriceReading(&readings, chart, "officialCpi", "官方 CPI", "官方", "CPI", 0)
	addPriceReading(&readings, chart, "meaningfulCpi", "有意义 CPI", "有意义", "CPI", 0)
	addPriceReading(&readings, chart, "officialPpi", "官方 PPI", "官方", "PPI", 1)
	addPriceReading(&readings, chart, "meaningfulPpi", "有意义 PPI", "有意义", "PPI", 1)

	addSection(sections, "MCPI", "物价中有多少来自选定短期扰动？", card, chart, readings)
}

func addPriceReading(readings *[]domain.OverviewReading, chart *domain.ChartData, key, label, series, category string, index int) {
	pt := findPoint(chart, series, category, index)
	val := pointValue(pt, category)
	addReading(readings, key, label, val, formatPercent(val, 1), new("%"))
}

func addFinancing(sections *[]domain.OverviewSection, cards []domain.IndexCard, charts []domain.ChartData) {
	card := findCard(cards, "MSF")
	chart := findChart(charts, "融资结构的研究折扣有多大？")
	var readings []domain.OverviewReading

	addFinancingReading(&readings, chart, "meaningfulSocialFinancing", "有意义社融", "有意义社融")
	addFinancingReading(&readings, chart, "governmentBonds", "政府债券", "政府债券")
	addFinancingReading(&readings, chart, "billsAndOther", "票据及其他", "票据及其他")

	var flow *float64
	if card != nil && card.Detail != nil {
		flow = numberAfter(*card.Detail, "·")
	}
	var dispFlow string
	if flow != nil {
		dispFlow = fmt.Sprintf("%.0f 亿元", *flow)
	}
	addReading(&readings, "effectiveFinancingMidpoint", "有效融资中点", flow, dispFlow, new("亿元"))

	addSection(sections, "MSF", "融资结构的研究折扣有多大？", card, chart, readings)
}

func addFinancingReading(readings *[]domain.OverviewReading, chart *domain.ChartData, key, label, seriesName string) {
	pt := findPoint(chart, seriesName, "", 0)
	val := pointValue(pt, "")
	addReading(readings, key, label, val, formatPercent(val, 1), new("%"))
}

func addSection(sections *[]domain.OverviewSection, code, title string, card *domain.IndexCard, chart *domain.ChartData, readings []domain.OverviewReading) {
	if len(readings) == 0 {
		return
	}
	var period *string
	desc := ""
	if card != nil {
		period = card.Period
		desc = card.Description
	} else if chart != nil {
		desc = chart.Description
	}

	*sections = append(*sections, domain.OverviewSection{
		Code:        code,
		Title:       title,
		Period:      period,
		Description: desc,
		Readings:    readings,
	})
}

func addReading(readings *[]domain.OverviewReading, key, label string, val *float64, disp string, unit *string) {
	if val == nil && strings.TrimSpace(disp) == "" {
		return
	}
	*readings = append(*readings, domain.OverviewReading{
		Key:          key,
		Label:        label,
		Value:        val,
		DisplayValue: disp,
		Unit:         unit,
	})
}

func findCard(cards []domain.IndexCard, code string) *domain.IndexCard {
	for _, c := range cards {
		if strings.EqualFold(c.Code, code) {
			return &c
		}
	}
	return nil
}

func findChart(charts []domain.ChartData, title string) *domain.ChartData {
	for _, ch := range charts {
		if ch.Title == title {
			return &ch
		}
	}
	return nil
}

func findPoint(chart *domain.ChartData, seriesName, category string, fallbackIndex int) *domain.ChartPoint {
	if chart == nil || len(chart.Series) == 0 {
		return nil
	}
	var series *domain.ChartSeries
	if seriesName == "" {
		series = &chart.Series[0]
	} else {
		for _, s := range chart.Series {
			if s.Name != nil && *s.Name == seriesName {
				series = &s
				break
			}
		}
	}
	if series == nil {
		return nil
	}

	if category != "" {
		for _, pt := range series.Points {
			if pt.Category != nil && *pt.Category == category {
				return &pt
			}
		}
	}

	if fallbackIndex >= 0 && fallbackIndex < len(series.Points) {
		return &series.Points[fallbackIndex]
	}
	return nil
}

func pointValue(pt *domain.ChartPoint, expectedCategory string) *float64 {
	if pt == nil {
		return nil
	}
	var categoryVal *float64
	if pt.Category != nil {
		categoryVal = parseNumber(*pt.Category)
	}

	if expectedCategory != "" && pt.Category != nil && *pt.Category == expectedCategory {
		if pt.Value != nil {
			return pt.Value
		}
		return categoryVal
	}

	if pt.Value == nil {
		return categoryVal
	}
	if categoryVal == nil {
		return pt.Value
	}
	if *categoryVal == 0 && *pt.Value != 0 {
		return pt.Value
	}
	if *pt.Value == 0 && *categoryVal != 0 {
		return categoryVal
	}
	return categoryVal
}

func pointDisplay(pt *domain.ChartPoint) string {
	if pt == nil || pt.Text == nil {
		return ""
	}
	t := *pt.Text
	t = strings.ReplaceAll(t, "<b>", "")
	t = strings.ReplaceAll(t, "</b>", "")
	t = strings.ReplaceAll(t, "<br>", " · ")
	return strings.TrimSpace(t)
}

func parseNumber(val string) *float64 {
	val = strings.TrimSpace(val)
	if val == "" {
		return nil
	}
	m := numberRegex.FindString(val)
	if m == "" {
		return nil
	}
	f, err := strconv.ParseFloat(m, 64)
	if err != nil {
		return nil
	}
	return &f
}

func numberAfter(text, marker string) *float64 {
	idx := strings.Index(text, marker)
	if idx < 0 {
		return nil
	}
	tail := text[idx+len(marker):]
	return parseNumber(tail)
}

func formatPercent(val *float64, decimals int) string {
	if val == nil {
		return ""
	}
	format := fmt.Sprintf("%%.%df%%%%", decimals)
	return fmt.Sprintf(format, *val)
}
