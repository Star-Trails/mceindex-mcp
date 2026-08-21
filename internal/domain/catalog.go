package domain

import "strings"

// IndicatorDefinition contains metadata for an official MCEIndex indicator.
type IndicatorDefinition struct {
	Code        string `json:"code"`
	Label       string `json:"label"`
	Description string `json:"description"`
}

var catalog = map[string]IndicatorDefinition{
	"LEI-GDP": {
		Code:        "LEI-GDP",
		Label:       "五大新产业规模占 GDP",
		Description: "几乎是中国经济中最有活力和被寄予厚望的部分；黄色数字是按海关HS范围锁定、并对新能源汽车—电池等行业内部交易作合并抵销后估算的产业规模占GDP比重。",
	},
	"LEI-EMP": {
		Code:        "LEI-EMP",
		Label:       "五大新产业理论就业贡献",
		Description: "黄色条表示五大新产业能够支撑的理论就业存量，不是当月新增岗位；把它同高校毕业生、网约车司机和外卖骑手等人群规模相比，就能看出它在中国就业大盘中是什么量级。",
	},
	"LEI-FIS": {
		Code:        "LEI-FIS",
		Label:       "五大新产业净财政贡献",
		Description: "主条表示扣除出口退税和补助后的五大新产业估算年化净财政贡献；正值为黄色，零值及负值为红色。负值表示当月折年后的退税和补助高于毛税收现金。下方公共财政项目只作量级参照。",
	},
	"MRS": {
		Code:        "MRS",
		Label:       "有意义社零",
		Description: "限额以上主要是达到国家统计收入门槛的商场、连锁超市和品牌餐饮等较大经营单位，限额以下主要是小店、个体户和小餐馆，耐用品／地产链包括汽车、家电、建材和家具；横条向右表示正在增长、支撑消费，向左表示正在下降、拖累消费。",
	},
	"MCPI": {
		Code:        "MCPI",
		Label:       "有意义 CPI",
		Description: "蓝点是官方 CPI/PPI，黄点是按研究公式剔除选定能源、黄金、鲜菜鲜果和上游投入冲击后的读数；蓝黄距离表示这些调整项的净影响，不是对“官方水分”或真实需求强弱的直接测量。",
	},
	"MSF": {
		Code:        "MSF",
		Label:       "有意义社融",
		Description: "黄色部分是按既定规则剔除政府债，并对票据和企业债施加折扣后的研究情景；占比越大只表示在同一规则下保留值较多，不能证明资金最终用途或实际进入企业和居民。",
	},
}

// TryGetIndicator looks up an indicator by exact code.
func TryGetIndicator(code string) (IndicatorDefinition, bool) {
	def, ok := catalog[code]
	return def, ok
}

// FindIndicator looks up an indicator by code or Chinese label (case-insensitive).
func FindIndicator(query string) (IndicatorDefinition, bool) {
	q := strings.TrimSpace(query)
	for _, def := range catalog {
		if strings.EqualFold(def.Code, q) || strings.EqualFold(def.Label, q) {
			return def, true
		}
	}
	return IndicatorDefinition{}, false
}

// AllIndicators returns all defined indicators.
func AllIndicators() []IndicatorDefinition {
	defs := make([]IndicatorDefinition, 0, len(catalog))
	for _, k := range []string{"LEI-GDP", "LEI-EMP", "LEI-FIS", "MRS", "MCPI", "MSF"} {
		if def, ok := catalog[k]; ok {
			defs = append(defs, def)
		}
	}
	return defs
}
