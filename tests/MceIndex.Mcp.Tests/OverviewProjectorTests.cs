using MceIndex.Mcp.Domain;
using MceIndex.Mcp.Services;

namespace MceIndex.Mcp.Tests;

public sealed class OverviewProjectorTests
{
    [Fact]
    public void ReturnsEveryReadingVisibleOnMonthlyOverview()
    {
        var sections = OverviewProjector.Build(Cards(), Charts());

        Assert.Equal(["LEI-GDP", "LEI-EMP", "LEI-FIS", "MRS", "MCPI", "MSF"],
            sections.Select(section => section.Code).ToArray());
        AssertReading(sections, "LEI-GDP", "industryScaleShare", 10.904161745383, "10.90%");
        AssertReading(sections, "LEI-GDP", "movingAverage12m", 9.763559266762666, "9.76%");
        AssertReading(sections, "LEI-GDP", "historicalPercentile", 99, "P99");
        AssertReading(sections, "LEI-EMP", "theoreticalEmploymentStock", 833.8270888002147, "833.8 万人");
        AssertReading(sections, "LEI-EMP", "employmentContribution", 1.15, "1.15%");
        AssertReading(sections, "LEI-EMP", "graduates2026", 1270, "≈1270.0 万人");
        AssertReading(sections, "LEI-EMP", "rideHailingDrivers", 780, "≈780.0 万人");
        AssertReading(sections, "LEI-EMP", "deliveryRiders", 1450, "≈1450.0 万人");
        AssertReading(sections, "LEI-FIS", "annualizedNetFiscalContribution", -945.9985215091683, "-946 亿元");
        AssertReading(sections, "LEI-FIS", "fiscalContribution", -0.52, "-0.52%");
        AssertReading(sections, "MRS", "meaningfulRetail", 0, "+0.0%");
        AssertReading(sections, "MRS", "belowDesignated", 3.170064531755501, "+3.2%");
        AssertReading(sections, "MRS", "aboveDesignated", -2, "-2.0%");
        AssertReading(sections, "MRS", "durablesPropertyChain", -10.475, "-10.5%");
        AssertReading(sections, "MCPI", "officialCpi", 1, "1.0%");
        AssertReading(sections, "MCPI", "meaningfulCpi", 0.4, "0.4%");
        AssertReading(sections, "MCPI", "officialPpi", 4.1, "4.1%");
        AssertReading(sections, "MCPI", "meaningfulPpi", 1, "1.0%");
        AssertReading(sections, "MSF", "meaningfulSocialFinancing", 68.15178571428572, "68.2%");
        AssertReading(sections, "MSF", "governmentBonds", 22.916666666666664, "22.9%");
        AssertReading(sections, "MSF", "billsAndOther", 8.93154761904762, "8.9%");
        AssertReading(sections, "MSF", "effectiveFinancingMidpoint", 22899, "22,899 亿元");
    }

    [Fact]
    public void LabelsEveryReadingWithPeriodScopedVerification()
    {
        var sections = OverviewProjector.Build(Cards(), Charts());
        var readings = sections.SelectMany(section => section.Readings).ToArray();

        Assert.Equal(27, readings.Length);
        Assert.All(readings, reading =>
        {
            Assert.NotNull(reading.Verification);
            Assert.Equal("2026-06", reading.Verification.AuditedPeriod);
            Assert.True(reading.Verification.AppliesToCurrentPeriod);
            Assert.NotEqual(ConclusionStatus.NotAssessed, reading.Verification.Status);
            Assert.False(string.IsNullOrWhiteSpace(reading.Verification.Summary));
        });

        AssertStatus(readings, "industryScaleShare", ConclusionStatus.NotFound);
        AssertStatus(readings, "deliveryRiders", ConclusionStatus.UnverifiedEstimate);
        AssertStatus(readings, "graduates2026", ConclusionStatus.Verified);
        AssertStatus(readings, "meaningfulCpi", ConclusionStatus.PartiallyVerified);
        AssertStatus(readings, "effectiveFinancingMidpoint", ConclusionStatus.PartiallyVerified);
        AssertStatus(readings, "defenseBudget", ConclusionStatus.Verified);
        Assert.Contains(
            "87.5%",
            Assert.Single(readings, reading => reading.Key == "effectiveFinancingMidpoint")
                .Verification!.Formula!,
            StringComparison.Ordinal);
        var industryProvenance = Assert.Single(
            readings,
            reading => reading.Key == "industryScaleShare").Verification!.ConceptualProvenance;
        Assert.NotNull(industryProvenance);
        Assert.Equal(ConceptualProvenanceStatus.PartiallyVerified, industryProvenance.Status);
        Assert.Contains(
            industryProvenance.Sources,
            source => source.Url == "https://www.youtube.com/watch?v=d5jEroGqoLc");
        Assert.Contains(
            industryProvenance.Limitations,
            limitation => limitation.Contains("生物医药", StringComparison.Ordinal) &&
                limitation.Contains("医药制造", StringComparison.Ordinal));

        Assert.Equal(
            ConceptualProvenanceStatus.PartiallyVerified,
            Assert.Single(readings, reading => reading.Key == "theoreticalEmploymentStock")
                .Verification!.ConceptualProvenance!.Status);
        Assert.Equal(
            ConceptualProvenanceStatus.PartiallyVerified,
            Assert.Single(readings, reading => reading.Key == "annualizedNetFiscalContribution")
                .Verification!.ConceptualProvenance!.Status);

    }

    [Fact]
    public void DoesNotApplyJuneAuditToAnotherPeriod()
    {
        var verification = OverviewVerificationProjector.Build(
            "LEI-GDP",
            "industryScaleShare",
            "2026-07");

        Assert.Equal(ConclusionStatus.NotAssessed, verification.Status);
        Assert.False(verification.AppliesToCurrentPeriod);
        Assert.Empty(verification.Sources);
        Assert.NotNull(verification.ConceptualProvenance);
        Assert.Equal(
            ConceptualProvenanceStatus.PartiallyVerified,
            verification.ConceptualProvenance.Status);
    }

    private static void AssertStatus(
        OverviewReading[] readings,
        string key,
        ConclusionStatus expected)
    {
        var reading = Assert.Single(readings, reading => reading.Key == key);
        Assert.Equal(expected, reading.Verification!.Status);
    }

    [Fact]
    public void AttachesPublishedFormulasAndDataSourcesToEverySection()
    {
        var pages = EvidencePages();

        var sections = OverviewProjector.Build(Cards(), Charts(), pages);

        Assert.All(sections, section =>
        {
            Assert.Contains(section.Notes, note => note.Kind == OverviewNoteKind.Formula);
            Assert.Contains(section.Notes, note => note.Kind == OverviewNoteKind.DataSource);
            Assert.All(section.Notes, note =>
            {
                Assert.False(string.IsNullOrWhiteSpace(note.Text));
                Assert.Equal($"https://mceindex.com/{note.SourcePage}", note.SourceUrl);
            });
        });
        var employment = Assert.Single(sections, section => section.Code == "LEI-EMP");
        Assert.Contains(employment.Notes,
            note => note.Text.Contains("直接就业人月 ÷ 产业规模毛额", StringComparison.Ordinal));
        var prices = Assert.Single(sections, section => section.Code == "MCPI");
        Assert.Contains(prices.Notes,
            note => note.Text.Contains("上金所Au99.99代理", StringComparison.Ordinal));
    }

    private static void AssertReading(
        OverviewSection[] sections,
        string code,
        string key,
        double value,
        string displayValue)
    {
        var section = Assert.Single(sections, section => section.Code == code);
        var reading = Assert.Single(section.Readings, reading => reading.Key == key);
        Assert.Equal(value, reading.Value);
        Assert.Equal(displayValue, reading.DisplayValue);
    }

    private static Dictionary<string, StoredPage> EvidencePages()
    {
        var liTables = new[]
        {
            new DataTable(
                ["数据类别", "主要用途", "项目内落点", "口径说明"],
                [
                    ["正式HS发布包", "提供总指标", "headline.csv", "不可变发布包"],
                    ["海关出口与HS映射", "定义出口规模", "海关HS月度出口金额", "不做价值增加折算"],
                    ["行业产业规模与直接就业", "计算就业", "sector_monthly.csv", "直接就业人月"],
                    ["财政现金与支持成本", "计算净财政", "sector_monthly.csv", "扣除退税和支持"],
                    ["2026年1—5月月度正式估算", "连续延伸曲线", "月度账本", "出口实测、国内估算"],
                ])
        };
        return new Dictionary<string, StoredPage>(StringComparer.Ordinal)
        {
            ["LI_Monthly"] = Page(
                "LI_Monthly",
                [
                    "产业规模 = 五行业产业规模毛额相加 − 行业内部交易抵销",
                    "出口产业规模 = 海关HS月度出口金额 国内产业规模 = 国内生产者交付毛额 − 国内抵销",
                    "直接就业能力系数 = 直接就业贡献占比 ÷ 产业规模占GDP比重 直接就业密度 = 直接就业人月 ÷ 产业规模毛额",
                    "净财政贡献 = 毛税收现金 − 当期法定出口退税 − 直接补助 − 递延支持",
                ],
                liTables),
            ["Meaningful_Retail"] = Page(
                "Meaningful_Retail",
                [
                    "按总额与限额以上金额倒算，限额以下隐含同比约 +3.2%。",
                    "实际化读数采用 Meaningful Macro 比值法，并按价格指数剔除价格扰动。",
                    "总量、限上和品类同比来自国家统计局；限额以下为金额隐含值。",
                    "限额以下增速由金额倒算而非直接发布。",
                ]),
            ["Meaningful_CPI_PPI"] = Page(
                "Meaningful_CPI_PPI",
                [
                    "官方CPI与研究调整值之差是全部公式项的净和。",
                    "研究口径由上游冲击法与内需大类法等权合成。",
                    "有意义 CPI 保留猪肉，移除短期扰动。",
                    "黄金项使用上金所Au99.99代理及历史官方贡献校准。",
                    "制造业主要原材料购进价格PMI为54.2。",
                    "固定权重只用于跨月比较；官方行业权重未公开。",
                ]),
            ["Meaningful_TSF"] = Page(
                "Meaningful_TSF",
                [
                    "按既定风险折扣规则计算研究情景中点。",
                    "低、中、高情景分别为不同折扣参数的估算。",
                    "有意义社融沿用参考备忘录，剔除政府债和票据冲量。",
                    "由累计值差分得到政府债券净融资和金融统计票据融资。",
                    "有效融资是研究情景，不是人民银行统计口径。",
                ]),
        };
    }

    private static StoredPage Page(string slug, string[] text, DataTable[]? tables = null)
    {
        var fetchedAt = new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        var summary = new StoredPageSummary(
            slug,
            slug,
            slug,
            $"https://mceindex.com/{slug}",
            fetchedAt,
            fetchedAt,
            text.Length,
            1);
        return new StoredPage(summary, new PageSnapshot
        {
            SourceUrl = summary.SourceUrl,
            FetchedAt = fetchedAt,
            Title = slug,
            Headings = [],
            Navigation = [],
            Metrics = [],
            Tables = tables ?? [],
            Text = text,
        });
    }

    private static IndexCard[] Cards() =>
    [
        Card("LEI-GDP", "五大新产业规模占 GDP", "10.90%", "2026-06 · 历史 P99"),
        Card("LEI-EMP", "五大新产业理论就业贡献", "833.8 万人", "2026-06 · 就业贡献 1.15%"),
        Card("LEI-FIS", "五大新产业净财政贡献", "-946 亿元", "2026-06 · 财政贡献 -0.52%"),
        Card("MRS", "有意义社零", "+0.0%", "2026-06 · 限额以下 +3.2%"),
        Card("MCPI", "有意义 CPI", "+0.4%", "2026-06 · 官方 +1.0%"),
        Card("MSF", "有意义社融", "68.2%", "2026-06 · 22,899 亿元"),
    ];

    private static IndexCard Card(string code, string label, string value, string detail) =>
        new(code, label, value, detail, "2026-06", $"{label}说明");

    private static ChartData[] Charts() =>
    [
        Chart("新产业占经济多大？",
            Series("新产业经济规模占比", Point("2026-06", 10.904161745383)),
            Series("12M 均线", Point("2026-06", 9.763559266762666))),
        Chart("新产业能吸收多少就业？",
            Series("理论就业规模",
                Point("理论就业规模", 8338270.888002147, "833.8 万人"),
                Point("2026届高校毕业生", 12700000, "≈1270.0 万人"),
                Point("网约车持证司机", 7800000, "≈780.0 万人"),
                Point("外卖骑手", 14500000, "≈1450.0 万人"))),
        Chart("新产业形成多少净财政贡献？",
            Series("估算年化净财政贡献", Point("净财政贡献", -945.9985215091683, "-946 亿元")),
            Series("公共财政量级参照",
                Point("国防预算", 19095.61, "19,096 亿元"),
                Point("债务付息", 13491, "13,491 亿元"),
                Point("教育支出", 43417, "43,417 亿元"),
                Point("土地出让收入", 41518, "41,518 亿元"),
                Point("中央转移支付", 104150, "104,150 亿元"))),
        Chart("消费哪里在撑、哪里在拖？",
            Series("trace 0",
                Point("限额以下", 3.170064531755501, "+3.2%"),
                Point("限额以上", -2, "-2.0%"),
                Point("耐用品/地产链", -10.475, "-10.5%"))),
        Chart("物价中有多少来自选定短期扰动？",
            Series("官方", Point("CPI", 1), Point("PPI", 4.1)),
            Series("有意义", Point("CPI", 0.4), Point("PPI", 1))),
        Chart("融资结构的研究折扣有多大？",
            Series("有意义社融", Point("0", 68.15178571428572)),
            Series("政府债券", Point("0", 22.916666666666664)),
            Series("票据及其他", Point("0", 8.93154761904762))),
    ];

    private static ChartData Chart(string title, params ChartSeries[] series) =>
        new(title, $"{title}说明", ["数据截至 2026-06"], null, null, series);

    private static ChartSeries Series(string name, params ChartPoint[] points) =>
        new(name, "bar", points);

    private static ChartPoint Point(string category, double value, string? text = null) =>
        new(category, value, text);
}
