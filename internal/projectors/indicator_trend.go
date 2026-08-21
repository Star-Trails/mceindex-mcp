package projectors

import (
	"fmt"
	"math"
	"sort"
	"strconv"
	"strings"

	"github.com/Star-Trails/mceindex-mcp/internal/domain"
)

type assessmentMode int

const (
	higherIsBetter assessmentMode = iota
	contextDependent
)

type trendDefinition struct {
	Code            string
	PageSlug        string
	SeriesName      string
	SeriesKey       string
	Label           string
	Unit            string
	StableTolerance float64
	AssessmentMode  assessmentMode
	Interpretation  string
}

var trendDefinitions = map[string]trendDefinition{
	"LEI-GDP": {
		Code:            "LEI-GDP",
		PageSlug:        "LI_Monthly",
		SeriesName:      "产业规模占GDP比重",
		SeriesKey:       "industryScaleShare",
		Label:           "五大新产业规模占 GDP",
		Unit:            "%",
		StableTolerance: 0.05,
		AssessmentMode:  higherIsBetter,
		Interpretation:  "占比上升通常表示五大新产业在经济中的体量改善。",
	},
	"LEI-EMP": {
		Code:            "LEI-EMP",
		PageSlug:        "LI_Monthly",
		SeriesName:      "直接就业能力系数",
		SeriesKey:       "directEmploymentCapacity",
		Label:           "五大新产业直接就业能力系数",
		Unit:            "系数",
		StableTolerance: 0.001,
		AssessmentMode:  higherIsBetter,
		Interpretation:  "系数上升通常表示五大新产业的直接就业支撑能力改善。",
	},
	"LEI-FIS": {
		Code:            "LEI-FIS",
		PageSlug:        "LI_Monthly",
		SeriesName:      "净财政贡献能力系数",
		SeriesKey:       "netFiscalCapacity",
		Label:           "五大新产业净财政贡献能力系数",
		Unit:            "系数",
		StableTolerance: 0.001,
		AssessmentMode:  higherIsBetter,
		Interpretation:  "系数上升（包括负值收窄）通常表示净财政贡献改善。",
	},
	"MRS": {
		Code:            "MRS",
		PageSlug:        "Meaningful_Retail",
		SeriesName:      "有意义社零同比",
		SeriesKey:       "meaningfulRetail",
		Label:           "有意义社零同比",
		Unit:            "%",
		StableTolerance: 0.1,
		AssessmentMode:  higherIsBetter,
		Interpretation:  "同比增速上升通常表示消费动能改善。",
	},
	"MCPI": {
		Code:            "MCPI",
		PageSlug:        "Meaningful_CPI_PPI",
		SeriesName:      "有意义 CPI",
		SeriesKey:       "meaningfulCpi",
		Label:           "有意义 CPI",
		Unit:            "%",
		StableTolerance: 0.1,
		AssessmentMode:  contextDependent,
		Interpretation:  "通胀升降本身不能机械解释为改善或恶化，需结合通缩、目标区间与需求背景。",
	},
	"MSF": {
		Code:            "MSF",
		PageSlug:        "Meaningful_TSF",
		SeriesName:      "有意义社融",
		SeriesKey:       "meaningfulSocialFinancing",
		Label:           "有意义社融月度流量",
		Unit:            "亿元",
		StableTolerance: 100,
		AssessmentMode:  contextDependent,
		Interpretation:  "融资流量具有季节性且不能证明资金最终用途，单凭升降不能判断经济改善或恶化。",
	},
}

// BuildIndicatorTrend computes trend analytics for a specific indicator code.
func BuildIndicatorTrend(code string, pages map[string]*domain.StoredPage, months int) *domain.IndicatorTrend {
	def, ok := trendDefinitions[code]
	if !ok {
		return nil
	}
	page, ok := pages[def.PageSlug]
	if !ok || page == nil {
		return nil
	}

	var targetSeries *domain.ChartSeries
	for _, ch := range page.Snapshot.Charts {
		for _, s := range ch.Series {
			if s.Name != nil && *s.Name == def.SeriesName {
				targetSeries = &s
				break
			}
		}
		if targetSeries != nil {
			break
		}
	}
	if targetSeries == nil {
		return nil
	}

	observations := make([]domain.HistoricalObservation, 0, len(targetSeries.Points))
	for _, pt := range targetSeries.Points {
		if pt.Category == nil || pt.Value == nil {
			continue
		}
		period := normalizePeriod(*pt.Category)
		if period == "" {
			continue
		}

		if len(observations) > 0 && observations[len(observations)-1].Period == period {
			observations[len(observations)-1] = domain.HistoricalObservation{
				Period: period,
				Value:  *pt.Value,
			}
		} else {
			observations = append(observations, domain.HistoricalObservation{
				Period: period,
				Value:  *pt.Value,
			})
		}
	}

	if len(observations) == 0 {
		return nil
	}

	sort.Slice(observations, func(i, j int) bool {
		return observations[i].Period < observations[j].Period
	})

	curr := observations[len(observations)-1]
	var prev *domain.HistoricalObservation
	if len(observations) >= 2 {
		prev = &observations[len(observations)-2]
	}

	yearAgoPeriod := previousYear(curr.Period)
	var yearAgo *domain.HistoricalObservation
	if yearAgoPeriod != "" {
		for i := len(observations) - 1; i >= 0; i-- {
			if observations[i].Period == yearAgoPeriod {
				yearAgo = &observations[i]
				break
			}
		}
	}

	var momChange *float64
	if prev != nil {
		diff := curr.Value - prev.Value
		momChange = &diff
	}

	var yoyChange *float64
	if yearAgo != nil {
		diff := curr.Value - yearAgo.Value
		yoyChange = &diff
	}

	var recentAvg *float64
	if len(observations) >= 3 {
		avg := averageObservations(observations, len(observations)-3, 3)
		recentAvg = &avg
	}

	var prevAvg *float64
	if len(observations) >= 6 {
		avg := averageObservations(observations, len(observations)-6, 3)
		prevAvg = &avg
	}

	var momentum *float64
	if recentAvg != nil && prevAvg != nil {
		diff := *recentAvg - *prevAvg
		momentum = &diff
	}

	direction := assessDirection(momChange, yoyChange, momentum, def.StableTolerance)
	assessment := assessEconomy(direction, def.AssessmentMode)

	historyOffset := 0
	if len(observations) > months {
		historyOffset = len(observations) - months
	}

	var prevPeriod *string
	var prevVal *float64
	if prev != nil {
		prevPeriod = &prev.Period
		prevVal = &prev.Value
	}

	var yoyPeriod *string
	var yoyVal *float64
	if yearAgo != nil {
		yoyPeriod = &yearAgo.Period
		yoyVal = &yearAgo.Value
	}

	return &domain.IndicatorTrend{
		SeriesKey:                 def.SeriesKey,
		Label:                     def.Label,
		Unit:                      def.Unit,
		AvailablePeriods:          len(observations),
		History:                   observations[historyOffset:],
		CurrentPeriod:             curr.Period,
		Current:                   curr.Value,
		PreviousPeriod:            prevPeriod,
		Previous:                  prevVal,
		MonthOverMonthChange:      momChange,
		YearAgoPeriod:             yoyPeriod,
		YearAgo:                   yoyVal,
		YearOverYearChange:        yoyChange,
		RecentThreeMonthAverage:   recentAvg,
		PreviousThreeMonthAverage: prevAvg,
		ThreeMonthMomentum:        momentum,
		Direction:                 direction,
		Assessment:                assessment,
		Basis:                     buildBasis(momChange, yoyChange, momentum, def.Unit),
		Interpretation:            def.Interpretation,
	}
}

func assessDirection(mom, yoy, momentum *float64, tol float64) domain.TrendDirection {
	var signals []int
	addSignal := func(val *float64) {
		if val == nil {
			return
		}
		if math.Abs(*val) <= tol {
			signals = append(signals, 0)
		} else if *val > 0 {
			signals = append(signals, 1)
		} else {
			signals = append(signals, -1)
		}
	}

	addSignal(mom)
	addSignal(yoy)
	addSignal(momentum)

	if len(signals) == 0 {
		return domain.TrendInsufficientData
	}

	hasPos := false
	hasNeg := false
	for _, sig := range signals {
		if sig > 0 {
			hasPos = true
		}
		if sig < 0 {
			hasNeg = true
		}
	}

	if hasPos && hasNeg {
		return domain.TrendMixed
	}
	if hasPos {
		return domain.TrendRising
	}
	if hasNeg {
		return domain.TrendFalling
	}
	return domain.TrendStable
}

func assessEconomy(dir domain.TrendDirection, mode assessmentMode) domain.EconomicAssessment {
	if dir == domain.TrendInsufficientData {
		return domain.AssessmentInsufficientData
	}
	if mode == contextDependent {
		return domain.AssessmentIndeterminate
	}

	switch dir {
	case domain.TrendRising:
		return domain.AssessmentImproving
	case domain.TrendFalling:
		return domain.AssessmentDeteriorating
	case domain.TrendStable:
		return domain.AssessmentStable
	case domain.TrendMixed:
		return domain.AssessmentMixed
	default:
		return domain.AssessmentInsufficientData
	}
}

func buildBasis(mom, yoy, momentum *float64, unit string) string {
	var parts []string
	addPart := func(label string, change *float64) {
		if change == nil {
			return
		}
		sign := ""
		if *change > 0 {
			sign = "+"
		}
		parts = append(parts, fmt.Sprintf("%s %s%.3f %s", label, sign, *change, unit))
	}

	addPart("较上月", mom)
	addPart("较上年同期", yoy)
	addPart("近3月均值较前3月", momentum)

	if len(parts) == 0 {
		return "可比较历史不足。"
	}
	return strings.Join(parts, "；") + "。"
}

func averageObservations(obs []domain.HistoricalObservation, offset, count int) float64 {
	sum := 0.0
	for i := offset; i < offset+count; i++ {
		sum += obs[i].Value
	}
	return sum / float64(count)
}

func normalizePeriod(cat string) string {
	cat = strings.TrimSpace(cat)
	if len(cat) < 7 || cat[4] != '-' {
		return ""
	}
	year, err1 := strconv.Atoi(cat[:4])
	month, err2 := strconv.Atoi(cat[5:7])
	if err1 != nil || err2 != nil || month < 1 || month > 12 {
		return ""
	}
	return fmt.Sprintf("%04d-%02d", year, month)
}

func previousYear(period string) string {
	if len(period) < 4 {
		return ""
	}
	year, err := strconv.Atoi(period[:4])
	if err != nil {
		return ""
	}
	return fmt.Sprintf("%04d%s", year-1, period[4:])
}
