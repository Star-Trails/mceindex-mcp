using MceIndex.Mcp.Domain;

namespace MceIndex.Mcp.Services;

internal static class OverviewEvidenceProjector
{
    public static OverviewNote[] Build(
        string code,
        IReadOnlyDictionary<string, StoredPage>? pages)
    {
        var slug = code switch
        {
            "LEI-GDP" or "LEI-EMP" or "LEI-FIS" => "LI_Monthly",
            "MRS" => "Meaningful_Retail",
            "MCPI" => "Meaningful_CPI_PPI",
            "MSF" => "Meaningful_TSF",
            _ => null,
        };
        if (slug is null || pages is null || !pages.TryGetValue(slug, out var page))
        {
            return [];
        }

        var notes = new List<OverviewNote>();
        switch (code)
        {
            case "LEI-GDP":
                AddText(notes, page, OverviewNoteKind.Formula,
                    text => text.StartsWith("产业规模 =", StringComparison.Ordinal));
                AddText(notes, page, OverviewNoteKind.Formula,
                    text => text.StartsWith("出口产业规模 =", StringComparison.Ordinal));
                AddTableRows(notes, page, "正式HS发布包", "海关出口与HS映射", "2026年1—5月月度正式估算");
                AddText(notes, page, OverviewNoteKind.Methodology,
                    text => text.StartsWith("页面中的总指标、行业值和时间序列", StringComparison.Ordinal));
                break;
            case "LEI-EMP":
                AddText(notes, page, OverviewNoteKind.Formula,
                    text => text.StartsWith("直接就业能力系数 =", StringComparison.Ordinal));
                AddTableRows(notes, page, "行业产业规模与直接就业", "2026年1—5月月度正式估算");
                AddText(notes, page, OverviewNoteKind.Caveat,
                    text => text.StartsWith("就业代理量由产业交付与固定就业密度推算", StringComparison.Ordinal));
                break;
            case "LEI-FIS":
                AddText(notes, page, OverviewNoteKind.Formula,
                    text => text.StartsWith("净财政贡献 =", StringComparison.Ordinal));
                AddTableRows(notes, page, "财政现金与支持成本", "2026年1—5月月度正式估算");
                AddText(notes, page, OverviewNoteKind.Caveat,
                    text => text.StartsWith("净财政额及加权比率均为LI研究估算", StringComparison.Ordinal));
                break;
            case "MRS":
                AddText(notes, page, OverviewNoteKind.Formula,
                    text => text.Contains("按总额与限额以上金额倒算", StringComparison.Ordinal));
                AddText(notes, page, OverviewNoteKind.Methodology,
                    text => text.StartsWith("实际化读数采用 Meaningful Macro 比值法", StringComparison.Ordinal));
                AddText(notes, page, OverviewNoteKind.DataSource,
                    text => text.StartsWith("总量、限上和品类同比来自国家统计局", StringComparison.Ordinal));
                AddText(notes, page, OverviewNoteKind.Caveat,
                    text => text.StartsWith("限额以下增速由金额倒算而非直接发布", StringComparison.Ordinal));
                break;
            case "MCPI":
                AddText(notes, page, OverviewNoteKind.Formula,
                    text => text.Contains("全部公式项的净和", StringComparison.Ordinal));
                AddText(notes, page, OverviewNoteKind.Formula,
                    text => text.StartsWith("研究口径由上游冲击法", StringComparison.Ordinal));
                AddText(notes, page, OverviewNoteKind.Methodology,
                    text => text.StartsWith("有意义 CPI 保留猪肉", StringComparison.Ordinal));
                AddText(notes, page, OverviewNoteKind.DataSource,
                    text => text.Contains("上金所Au99.99代理", StringComparison.Ordinal));
                AddText(notes, page, OverviewNoteKind.DataSource,
                    text => text.StartsWith("制造业主要原材料购进价格PMI", StringComparison.Ordinal));
                AddText(notes, page, OverviewNoteKind.Caveat,
                    text => text.Contains("官方行业权重未公开", StringComparison.Ordinal));
                break;
            case "MSF":
                AddText(notes, page, OverviewNoteKind.Formula,
                    text => text.Contains("按既定风险折扣规则计算", StringComparison.Ordinal));
                AddText(notes, page, OverviewNoteKind.Formula,
                    text => text.Contains("低、中、高情景分别为", StringComparison.Ordinal));
                AddText(notes, page, OverviewNoteKind.Methodology,
                    text => text.StartsWith("有意义社融沿用参考备忘录", StringComparison.Ordinal));
                AddText(notes, page, OverviewNoteKind.DataSource,
                    text => text.StartsWith("由累计值差分得到", StringComparison.Ordinal));
                AddText(notes, page, OverviewNoteKind.Caveat,
                    text => text.StartsWith("有效融资是研究情景", StringComparison.Ordinal));
                break;
        }
        return [.. notes.DistinctBy(note => (note.Kind, note.Text))];
    }

    private static void AddText(
        List<OverviewNote> notes,
        StoredPage page,
        OverviewNoteKind kind,
        Func<string, bool> predicate)
    {
        var text = page.Snapshot.Text.FirstOrDefault(predicate);
        if (!string.IsNullOrWhiteSpace(text))
        {
            notes.Add(Note(kind, text, page));
        }
    }

    private static void AddTableRows(
        List<OverviewNote> notes,
        StoredPage page,
        params string[] categories)
    {
        foreach (var category in categories)
        {
            var row = page.Snapshot.Tables
                .SelectMany(table => table.Rows)
                .FirstOrDefault(candidate => candidate.Length >= 4 && candidate[0] == category);
            if (row is null)
            {
                continue;
            }
            notes.Add(Note(
                OverviewNoteKind.DataSource,
                $"{row[0]}：{row[1]}；项目内落点：{row[2]}；口径说明：{row[3]}",
                page));
        }
    }

    private static OverviewNote Note(OverviewNoteKind kind, string text, StoredPage page) =>
        new(kind, text, page.Summary.Slug, page.Summary.SourceUrl);
}
