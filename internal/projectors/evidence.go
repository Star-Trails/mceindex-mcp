package projectors

import (
	"fmt"
	"strings"

	"github.com/Star-Trails/mceindex-mcp/internal/domain"
)

// BuildOverviewNotes extracts evidence notes (formulas, data sources, caveats) from evidence pages.
func BuildOverviewNotes(code string, pages map[string]*domain.StoredPage) []domain.OverviewNote {
	slug := ""
	switch code {
	case "LEI-GDP", "LEI-EMP", "LEI-FIS":
		slug = "LI_Monthly"
	case "MRS":
		slug = "Meaningful_Retail"
	case "MCPI":
		slug = "Meaningful_CPI_PPI"
	case "MSF":
		slug = "Meaningful_TSF"
	default:
		return []domain.OverviewNote{}
	}

	page, ok := pages[slug]
	if !ok || page == nil {
		return []domain.OverviewNote{}
	}

	var notes []domain.OverviewNote

	switch code {
	case "LEI-GDP":
		addText(&notes, page, domain.NoteFormula, func(t string) bool { return strings.HasPrefix(t, "产业规模 =") })
		addText(&notes, page, domain.NoteFormula, func(t string) bool { return strings.HasPrefix(t, "出口产业规模 =") })
		addTableRows(&notes, page, "正式HS发布包", "海关出口与HS映射", "2026年1—5月月度正式估算")
		addText(&notes, page, domain.NoteMethodology, func(t string) bool { return strings.HasPrefix(t, "页面中的总指标、行业值和时间序列") })

	case "LEI-EMP":
		addText(&notes, page, domain.NoteFormula, func(t string) bool { return strings.HasPrefix(t, "直接就业能力系数 =") })
		addTableRows(&notes, page, "行业产业规模与直接就业", "2026年1—5月月度正式估算")
		addText(&notes, page, domain.NoteCaveat, func(t string) bool { return strings.HasPrefix(t, "就业代理量由产业交付与固定就业密度推算") })

	case "LEI-FIS":
		addText(&notes, page, domain.NoteFormula, func(t string) bool { return strings.HasPrefix(t, "净财政贡献 =") })
		addTableRows(&notes, page, "财政现金与支持成本", "2026年1—5月月度正式估算")
		addText(&notes, page, domain.NoteCaveat, func(t string) bool { return strings.HasPrefix(t, "净财政额及加权比率均为LI研究估算") })

	case "MRS":
		addText(&notes, page, domain.NoteFormula, func(t string) bool { return strings.Contains(t, "按总额与限额以上金额倒算") })
		addText(&notes, page, domain.NoteMethodology, func(t string) bool { return strings.HasPrefix(t, "实际化读数采用 Meaningful Macro 比值法") })
		addText(&notes, page, domain.NoteDataSource, func(t string) bool { return strings.HasPrefix(t, "总量、限上和品类同比来自国家统计局") })
		addText(&notes, page, domain.NoteCaveat, func(t string) bool { return strings.HasPrefix(t, "限额以下增速由金额倒算而非直接发布") })

	case "MCPI":
		addText(&notes, page, domain.NoteFormula, func(t string) bool { return strings.Contains(t, "全部公式项的净和") })
		addText(&notes, page, domain.NoteFormula, func(t string) bool { return strings.HasPrefix(t, "研究口径由上游冲击法") })
		addText(&notes, page, domain.NoteMethodology, func(t string) bool { return strings.HasPrefix(t, "有意义 CPI 保留猪肉") })
		addText(&notes, page, domain.NoteDataSource, func(t string) bool { return strings.Contains(t, "上金所Au99.99代理") })
		addText(&notes, page, domain.NoteDataSource, func(t string) bool { return strings.HasPrefix(t, "制造业主要原材料购进价格PMI") })
		addText(&notes, page, domain.NoteCaveat, func(t string) bool { return strings.Contains(t, "官方行业权重未公开") })

	case "MSF":
		addText(&notes, page, domain.NoteFormula, func(t string) bool { return strings.Contains(t, "按既定风险折扣规则计算") })
		addText(&notes, page, domain.NoteFormula, func(t string) bool { return strings.Contains(t, "低、中、高情景分别为") })
		addText(&notes, page, domain.NoteMethodology, func(t string) bool { return strings.HasPrefix(t, "有意义社融沿用参考备忘录") })
		addText(&notes, page, domain.NoteDataSource, func(t string) bool { return strings.HasPrefix(t, "由累计值差分得到") })
		addText(&notes, page, domain.NoteCaveat, func(t string) bool { return strings.HasPrefix(t, "有效融资是研究情景") })
	}

	return deduplicateNotes(notes)
}

func addText(notes *[]domain.OverviewNote, page *domain.StoredPage, kind domain.OverviewNoteKind, pred func(string) bool) {
	for _, txt := range page.Snapshot.Text {
		if pred(txt) {
			norm := strings.TrimSpace(txt)
			if norm != "" {
				*notes = append(*notes, domain.OverviewNote{
					Kind:       kind,
					Text:       norm,
					SourcePage: page.Summary.Slug,
					SourceURL:  page.Summary.SourceURL,
				})
			}
			break
		}
	}
}

func addTableRows(notes *[]domain.OverviewNote, page *domain.StoredPage, categories ...string) {
	for _, cat := range categories {
		for _, tbl := range page.Snapshot.Tables {
			for _, row := range tbl.Rows {
				if len(row) >= 4 && row[0] == cat {
					txt := fmt.Sprintf("%s：%s；项目内落点：%s；口径说明：%s", row[0], row[1], row[2], row[3])
					*notes = append(*notes, domain.OverviewNote{
						Kind:       domain.NoteDataSource,
						Text:       txt,
						SourcePage: page.Summary.Slug,
						SourceURL:  page.Summary.SourceURL,
					})
					break
				}
			}
		}
	}
}

func deduplicateNotes(notes []domain.OverviewNote) []domain.OverviewNote {
	seen := make(map[string]struct{}, len(notes))
	var result []domain.OverviewNote
	for _, n := range notes {
		key := fmt.Sprintf("%s:%s", n.Kind, n.Text)
		if _, ok := seen[key]; !ok {
			seen[key] = struct{}{}
			result = append(result, n)
		}
	}
	return result
}
