using System.Collections.Frozen;
using MceIndex.Mcp.Domain;

namespace MceIndex.Mcp.Services;

internal static class OverviewVerificationProjector
{
    private const string AuditedPeriod = "2026-06";

    private static readonly EvidenceSource MceLifeIndex = new(
        "MCEIndex", "五大新产业续命指数", "https://mceindex.com/LI_Monthly", AuditedPeriod);
    private static readonly EvidenceSource MceRetail = new(
        "MCEIndex", "有意义社零", "https://mceindex.com/Meaningful_Retail", AuditedPeriod);
    private static readonly EvidenceSource McePrices = new(
        "MCEIndex", "有意义CPI/PPI", "https://mceindex.com/Meaningful_CPI_PPI", AuditedPeriod);
    private static readonly EvidenceSource MceFinancing = new(
        "MCEIndex", "有意义社融", "https://mceindex.com/Meaningful_TSF", AuditedPeriod);
    private static readonly EvidenceSource NbsGdp = new(
        "国家统计局", "2026年二季度和上半年国内生产总值初步核算结果",
        "https://www.stats.gov.cn/sj/zxfb/202607/t20260716_1964142.html", "2026 H1/Q2");
    private static readonly EvidenceSource NbsEmployment = new(
        "国家统计局", "中华人民共和国2025年国民经济和社会发展统计公报",
        "https://www.stats.gov.cn/sj/zxfbhjd/202602/t20260228_1962662.html", "2025 year-end");
    private static readonly EvidenceSource MoeGraduates = new(
        "教育部", "扩大岗位供给 提升服务效能——教育部门多措并举推进高校毕业生就业工作",
        "https://hudong.moe.gov.cn/jyb_xwfb/s5147/202606/t20260615_1440719.html", "2026 graduates");
    private static readonly EvidenceSource ChinaJobRiders = new(
        "中国就业网", "为千万外卖骑手撑起权益伞",
        "https://chinajob.mohrss.gov.cn/c/2025-02-20/425597.shtml", "2025");
    private static readonly EvidenceSource Meituan = new(
        "美团", "骑手与商户公开数据",
        "https://www.meituan.com/news/NN250321082001991", "2024");
    private static readonly EvidenceSource MotRideHailing = new(
        "交通运输部", "2026年6月网约车行业运行基本情况",
        "https://www.mot.gov.cn/shuju/fenxigongbao/yunlifenxi/202607/t20260714_4209608.html", AuditedPeriod);
    private static readonly EvidenceSource MofExecution = new(
        "财政部", "2025年财政收支执行情况",
        "https://bgt.mof.gov.cn/zhuantilanmu/rdwyh/ysbgjyszx/202601/t20260130_3982923.htm", "2025 execution");
    private static readonly EvidenceSource MofBudget = new(
        "财政部", "2025年预算执行与2026年预算草案报告",
        "https://www.mof.gov.cn/zhengwuxinxi/caizhengxinwen/202603/t20260316_3985331.htm", "2026 budget");
    private static readonly EvidenceSource NbsRetail = new(
        "国家统计局", "2026年6月份社会消费品零售总额",
        "https://www.stats.gov.cn/sj/zxfb/202607/t20260715_1964127.html", AuditedPeriod);
    private static readonly EvidenceSource NbsCpi = new(
        "国家统计局", "2026年6月份居民消费价格同比上涨1.0%",
        "https://www.stats.gov.cn/sj/zxfb/202607/t20260709_1964084.html", AuditedPeriod);
    private static readonly EvidenceSource NbsPpi = new(
        "国家统计局", "国家统计局解读2026年6月份CPI和PPI数据",
        "https://www.stats.gov.cn/sj/zxfbhjd/202607/t20260709_1964083.html", AuditedPeriod);
    private static readonly EvidenceSource SgeGold = new(
        "上海黄金交易所", "2026年6月市场月报",
        "https://www.sge.com.cn/upload/file/202607/02/9a1fd9b9be654e46a96d6e5a9754e638.pdf", AuditedPeriod);
    private static readonly EvidenceSource PbcJune = new(
        "中国人民银行", "2026年上半年金融统计数据报告",
        "https://www.pbc.gov.cn/goutongjiaoliu/113456/113469/2026071512340454869/index.html", "2026 H1");
    private static readonly EvidenceSource PbcMay = new(
        "中国人民银行", "2026年5月金融统计数据报告",
        "https://www.pbc.gov.cn/goutongjiaoliu/113456/113469/2026061214273613328/index.html", "2026 Jan-May");
    private static readonly EvidenceSource FearNationE249 = new(
        "FearNation 世界苦茶",
        "E249 打消一切中国经济悬念！工业利润暴涨18.8%背后的真相",
        "https://www.youtube.com/watch?v=d5jEroGqoLc",
        Detail: "发布方方法论材料；YouTube oEmbed可确认标题与频道，当前无可读取字幕。");

    private static readonly FrozenDictionary<string, ConceptualProvenance> ConceptualProvenances =
        new Dictionary<string, ConceptualProvenance>(StringComparer.Ordinal)
        {
            ["LEI-GDP:industryScaleShare"] = new(
                ConceptualProvenanceStatus.PartiallyVerified,
                "节目索引从“五大暴涨行业拆解”切入，关联材料列出的新能源汽车、新能源、集成电路、生物医药和电气化设备与MCE五产业近似对应；这支持行业选择动机，不验证统计边界或10.90%。",
                [FearNationE249],
                [
                    "节目使用“生物医药”，MCE页面使用“医药制造”，两者统计边界不能视为相同。",
                    "影片未提供行业代码、HS映射、逐行业金额、内部交易抵销或月度GDP构造。",
                    "当前无可读取字幕，章节内容依赖公开节目索引，尚未逐字转录核对。",
                ]),
            ["LEI-EMP:theoreticalEmploymentStock"] = new(
                ConceptualProvenanceStatus.PartiallyVerified,
                "节目“暴涨利润为何不交税、不增就业”章节支持检验高利润行业能否传导至就业的设计动机，不验证833.8万人或就业密度模型。",
                [FearNationE249],
                [
                    "影片未提供行业就业人数、每亿元就业密度、人月转存量或跨行业去重规则。",
                    "当前无可读取字幕，章节内容依赖公开节目索引，尚未逐字转录核对。",
                ]),
            ["LEI-EMP:employmentContribution"] = new(
                ConceptualProvenanceStatus.PartiallyVerified,
                "节目明确提出高利润行业的就业传导问题，与“理论就业存量/全国就业人口”指标目的相符；它只支持语义来源，不验证1.15%的分子。",
                [FearNationE249],
                [
                    "影片未提供理论就业存量的底层数据。",
                    "当前无可读取字幕，章节内容依赖公开节目索引，尚未逐字转录核对。",
                ]),
            ["LEI-FIS:annualizedNetFiscalContribution"] = new(
                ConceptualProvenanceStatus.PartiallyVerified,
                "节目把工业利润增长与企业所得税弱增长并列，并追问利润为何没有转化为税收；这支持净财政贡献指标的设计动机，不验证-946亿元。",
                [FearNationE249],
                [
                    "影片未提供毛税收、出口退税、直接补助和递延支持逐项金额。",
                    "当前无可读取字幕，章节内容依赖公开节目索引，尚未逐字转录核对。",
                ]),
            ["LEI-FIS:fiscalContribution"] = new(
                ConceptualProvenanceStatus.PartiallyVerified,
                "节目支持考察产业利润向税收传导的概念来源，与财政续命读数的语义一致；它不验证-946亿元分子或-0.52%的模型口径。",
                [FearNationE249],
                [
                    "影片未披露财政模型底表及现金/权责发生、中央/地方边界。",
                    "当前无可读取字幕，章节内容依赖公开节目索引，尚未逐字转录核对。",
                ]),
        }.ToFrozenDictionary(StringComparer.Ordinal);


    private static readonly FrozenDictionary<string, ConclusionVerification> Audits =
        new Dictionary<string, ConclusionVerification>(StringComparer.Ordinal)
        {
            ["LEI-GDP:industryScaleShare"] = V(ConclusionStatus.NotFound, EvidenceStatus.Partial,
                AlgorithmStatus.Published, ReproductionStatus.Impossible, false,
                "五行业选择动机已有发布方方法论线索；GDP分母有官方来源，但行业规模、HS映射、国内交付、内部抵销和月度GDP规则缺失，10.90%仍不能独立复算。",
                "(Σ五行业产业规模毛额 − 行业内部交易抵销) ÷ 月度GDP × 100%", null,
                [MceLifeIndex, NbsGdp],
                "正式发布包及行业底表没有公开下载地址。", "官方只发布季度和半年GDP，没有6月单月GDP。"),
            ["LEI-GDP:movingAverage12m"] = V(ConclusionStatus.NotFound, EvidenceStatus.Missing,
                AlgorithmStatus.Published, ReproductionStatus.Conditional, false,
                "12个月算术均值可由MCE自身历史序列复算，但底层产业规模序列不能脱离MCE验证。",
                "最近12个月产业规模占GDP比重的算术平均", null, [MceLifeIndex],
                "条件复现依赖MCE自身序列，不构成独立验证。"),
            ["LEI-GDP:historicalPercentile"] = V(ConclusionStatus.NotFound, EvidenceStatus.Missing,
                AlgorithmStatus.Inferred, ReproductionStatus.Conditional, false,
                "MCE图表的78个月序列可得到约98.7%的排名并显示P99；精确排名规则和底层序列不能独立验证。",
                "[INFERENCE] 当前值在历史月度序列中的百分位排名",
                "[INFERENCE] 2026-06值排名77/78；77÷78=98.718%，显示P99。", [MceLifeIndex],
                "样本起止、并列值、插值和修订规则未正式披露。"),

            ["LEI-EMP:theoreticalEmploymentStock"] = V(ConclusionStatus.NotFound, EvidenceStatus.Missing,
                AlgorithmStatus.Published, ReproductionStatus.Impossible, false,
                "影片支持考察高利润行业就业传导的设计动机；理论公式公开，但五行业产业规模和就业密度底表均缺失，833.8万人不能独立复算。",
                "Σ(行业产业规模毛额 × 行业直接就业密度)", null, [MceLifeIndex],
                "就业密度来源、样本、人月转存量和跨行业去重规则未知。"),
            ["LEI-EMP:employmentContribution"] = V(ConclusionStatus.PartiallyVerified, EvidenceStatus.Partial,
                AlgorithmStatus.Inferred, ReproductionStatus.Conditional, false,
                "使用MCE理论就业分子和国家统计局2025年末就业分母可精确得到1.15%；分子仍未验证。",
                "[INFERENCE] 理论就业存量 ÷ 全国就业人员 × 100%",
                "8,338,271 ÷ 725,040,000 × 100% = 1.150042894% → 1.15%。",
                [MceLifeIndex, NbsEmployment], "2026-06模型分子使用2025年末就业分母，存在时点错位。"),
            ["LEI-EMP:graduates2026"] = V(ConclusionStatus.Verified, EvidenceStatus.Verified,
                AlgorithmStatus.NotApplicable, ReproductionStatus.DirectSource, true,
                "教育部网站转载新华社明确给出2026届高校毕业生预计1270万人。", null, null,
                [MoeGraduates], "预计规模，不是已实现毕业人数。"),
            ["LEI-EMP:rideHailingDrivers"] = V(ConclusionStatus.PartiallyVerified, EvidenceStatus.Partial,
                AlgorithmStatus.Missing, ReproductionStatus.Impossible, false,
                "历史累计发放网约车驾驶员证超过700万本，780万数量级合理；2026-06官方月报未给精确全国证件数。",
                null, null, [MotRideHailing], "累计发证不等于活跃司机；注销、多地持证和实际营运状态未知。"),
            ["LEI-EMP:deliveryRiders"] = V(ConclusionStatus.UnverifiedEstimate, EvidenceStatus.Missing,
                AlgorithmStatus.Missing, ReproductionStatus.Impossible, false,
                "未找到1450万唯一外卖骑手的权威来源；同一精确数字出现在美团年活跃商户口径，存在指标错配风险。",
                null, null, [ChinaJobRiders, Meituan],
                "平台注册、年活跃、月有单、实际从业和跨平台去重是不同口径。"),

            ["LEI-FIS:annualizedNetFiscalContribution"] = V(ConclusionStatus.NotFound, EvidenceStatus.Missing,
                AlgorithmStatus.Published, ReproductionStatus.Impossible, false,
                "影片支持考察产业利润向税收传导的设计动机；净财政顶层公式及单月乘12年化规则公开，但五行业税收、退税、补助和递延支持底表缺失。",
                "12 × (毛税收现金 − 法定出口退税 − 直接补助 − 递延支持)", null,
                [MceLifeIndex], "现金/权责发生、中央/地方、税惠和政策性融资边界未公开。"),
            ["LEI-FIS:fiscalContribution"] = V(ConclusionStatus.PartiallyVerified, EvidenceStatus.Partial,
                AlgorithmStatus.Inferred, ReproductionStatus.Conditional, false,
                "以财政部2026年全国税收收入预算181520亿元为分母可精确得到-0.52%；分子-946亿元未验证。",
                "[INFERENCE] 年化净财政贡献 ÷ 2026全国税收收入预算 × 100%",
                "-946 ÷ 181,520 × 100% = -0.5211547% → -0.52%。", [MceLifeIndex, MofBudget],
                "模型年化值与全年预算税收收入混合口径。"),
            ["LEI-FIS:defenseBudget"] = Direct("财政部2026年中央本级国防支出预算19095.61亿元，四舍五入为19096亿元。", MofBudget,
                "中央本级2026预算数，只能用于量级参照。"),
            ["LEI-FIS:debtInterest"] = Direct("财政部2025年全国一般公共预算债务付息支出13491亿元。", MofExecution,
                "2025全国执行数，只能用于量级参照。"),
            ["LEI-FIS:educationSpending"] = Direct("财政部2025年全国一般公共预算教育支出43417亿元。", MofExecution,
                "2025全国执行数，只能用于量级参照。"),
            ["LEI-FIS:landSaleRevenue"] = Direct("财政部2025年地方国有土地使用权出让收入41518亿元。", MofExecution,
                "属于地方政府性基金收入，不是一般公共预算。"),
            ["LEI-FIS:centralTransfers"] = Direct("财政部2026年中央对地方转移支付预算104150亿元。", MofBudget,
                "2026中央预算数，只能用于量级参照。"),

            ["MRS:meaningfulRetail"] = V(ConclusionStatus.PartiallyVerified, EvidenceStatus.Partial,
                AlgorithmStatus.Missing, ReproductionStatus.Impossible, false,
                "社零和价格大类输入有官方来源，但Meaningful Macro比值法的平减项、映射、权重和未舍入结果未公开。",
                "候选公式：(1 + 名义同比) ÷ (1 + 价格同比) − 1", null,
                [MceRetail, NbsRetail, NbsCpi], "显示+0.0%不能区分真实值为零或舍入后的近零值。"),
            ["MRS:belowDesignated"] = V(ConclusionStatus.PartiallyVerified, EvidenceStatus.Verified,
                AlgorithmStatus.Inferred, ReproductionStatus.Conditional, false,
                "总量和限额以上金额来自国家统计局；限额以下同比由金额倒算，不是官方直接发布序列。",
                "[INFERENCE] 由社零总额与限额以上金额的本期、上年同期差额计算同比", null,
                [MceRetail, NbsRetail], "需要同口径未舍入金额才能精确重算3.1700645%。"),
            ["MRS:aboveDesignated"] = Direct("限额以上单位消费品零售同比由国家统计局直接发布。", NbsRetail,
                "仅覆盖达到统计门槛的单位。"),
            ["MRS:durablesPropertyChain"] = V(ConclusionStatus.PartiallyVerified, EvidenceStatus.Verified,
                AlgorithmStatus.Inferred, ReproductionStatus.Verified, false,
                "四个组成项均为国家统计局数据；简单平均可精确得到-10.475%，但聚合规则未正式说明。",
                "[INFERENCE] (汽车 + 建筑装潢 + 家电 + 家具) ÷ 4",
                "(-16.1 - 10.5 - 8.7 - 6.6) ÷ 4 = -10.475%。", [MceRetail, NbsRetail],
                "无权重算术平均不是国家统计局正式指标，也不等同于经济权重加总。"),

            ["MCPI:officialCpi"] = Direct("国家统计局直接发布2026年6月CPI同比1.0%。", NbsCpi),
            ["MCPI:meaningfulCpi"] = V(ConclusionStatus.PartiallyVerified, EvidenceStatus.Partial,
                AlgorithmStatus.Inferred, ReproductionStatus.Conditional, false,
                "顶层算术可复现0.44%，但燃油、黄金和鲜食调整项的权重与校准过程不可复现。",
                "[INFERENCE] 官方CPI − 燃油代理贡献 − 黄金代理贡献 + 鲜食调整 + 租金调整",
                "1.00 - 0.49 - 0.09 + 0.02 + 0.00 = 0.44%。", [McePrices, NbsCpi, SgeGold],
                "缺燃油权重、黄金历史校准、鲜菜鲜果权重和租金序列绑定。"),
            ["MCPI:officialPpi"] = Direct("国家统计局直接发布2026年6月PPI同比4.1%。", NbsPpi),
            ["MCPI:meaningfulPpi"] = V(ConclusionStatus.PartiallyVerified, EvidenceStatus.Partial,
                AlgorithmStatus.Inferred, ReproductionStatus.Conditional, false,
                "1.20%与0.89%的等权平均可复现顶层结果；两个子模型的行业清单、权重和基期缺失。",
                "[INFERENCE] (上游冲击法 + 内需大类法) ÷ 2",
                "(1.20 + 0.89) ÷ 2 = 1.045%；页面显示1.04%。", [McePrices, NbsPpi],
                "1.04%的舍入规则不明；子模型不能独立复算。"),

            ["MSF:meaningfulSocialFinancing"] = V(ConclusionStatus.PartiallyVerified, EvidenceStatus.Verified,
                AlgorithmStatus.Inferred, ReproductionStatus.Conditional, false,
                "人民银行核心输入均可验证；有效融资中点依赖未公开的风险折扣参数。",
                "[INFERENCE] 有效融资中点 ÷ 官方社融增量 × 100%",
                "22,899 ÷ 33,600 × 100% = 68.1518% → 68.2%。", [MceFinancing, PbcJune, PbcMay],
                "风险折扣参数只有数学反推支持。"),
            ["MSF:governmentBonds"] = V(ConclusionStatus.Verified, EvidenceStatus.Verified,
                AlgorithmStatus.Inferred, ReproductionStatus.Verified, false,
                "政府债月度增量和官方社融增量均可由人民银行累计数据差分并复算占比。",
                "政府债券净融资 ÷ 官方社融增量 × 100%",
                "7,700 ÷ 33,600 × 100% = 22.9167% → 22.9%。", [PbcJune, PbcMay],
                "占比是派生值，人民银行不发布同名MCE指标。"),
            ["MSF:billsAndOther"] = V(ConclusionStatus.PartiallyVerified, EvidenceStatus.Verified,
                AlgorithmStatus.Inferred, ReproductionStatus.Conditional, false,
                "官方输入可验证；8.9%是未公开风险折扣形成的剩余占比。",
                "[INFERENCE] (官方社融 − 政府债 − 有效融资中点) ÷ 官方社融 × 100%",
                "(33,600 - 7,700 - 22,899) ÷ 33,600 × 100% = 8.9315% → 8.9%。",
                [MceFinancing, PbcJune, PbcMay], "票据和企业债折扣参数未正式披露。"),
            ["MSF:effectiveFinancingMidpoint"] = V(ConclusionStatus.PartiallyVerified, EvidenceStatus.Verified,
                AlgorithmStatus.Inferred, ReproductionStatus.Conditional, false,
                "宏观输入可验证，22899亿元可由反推参数精确重现；参考备忘录和正式折扣参数未找到。",
                "[INFERENCE] 非政府融资 − 87.5%×票据融资 − 50%×企业债融资",
                "25,900 - 0.875×1,144 - 0.50×4,000 = 22,899亿元。",
                [MceFinancing, PbcJune, PbcMay], "87.5%和50%仅为结果反推，不是已发布方法。"),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static ConclusionVerification Build(string code, string key, string? period)
    {
        var auditKey = $"{code}:{key}";
        ConceptualProvenances.TryGetValue(auditKey, out var conceptualProvenance);
        Audits.TryGetValue(auditKey, out var verification);

        if (!string.Equals(period, AuditedPeriod, StringComparison.Ordinal))
        {
            return verification is not null
                ? verification with
                {
                    AppliesToCurrentPeriod = false,
                    DataUpdated = true,
                    ConceptualProvenance = conceptualProvenance,
                }
                : new ConclusionVerification(
                    AuditedPeriod,
                    false,
                    true,
                    ConclusionStatus.NotAssessed,
                    EvidenceStatus.NotAssessed,
                    AlgorithmStatus.NotAssessed,
                    ReproductionStatus.NotAssessed,
                    false,
                    "该读数尚未建立独立核验记录。",
                    null,
                    null,
                    [],
                    ["需要补充逐项审计。"],
                    conceptualProvenance);
        }

        return verification is not null
            ? verification with
            {
                AppliesToCurrentPeriod = true,
                DataUpdated = false,
                ConceptualProvenance = conceptualProvenance,
            }
            : new ConclusionVerification(
                AuditedPeriod,
                true,
                false,
                ConclusionStatus.NotAssessed,
                EvidenceStatus.NotAssessed,
                AlgorithmStatus.NotAssessed,
                ReproductionStatus.NotAssessed,
                false,
                "该读数尚未建立独立核验记录。",
                null,
                null,
                [],
                ["需要补充逐项审计。"],
                conceptualProvenance);
    }

    private static ConclusionVerification Direct(
        string summary,
        EvidenceSource source,
        params string[] limitations) =>
        V(ConclusionStatus.Verified, EvidenceStatus.Verified, AlgorithmStatus.NotApplicable,
            ReproductionStatus.DirectSource, true, summary, null, null, [source], limitations);

    private static ConclusionVerification V(
        ConclusionStatus status,
        EvidenceStatus sourceStatus,
        AlgorithmStatus algorithmStatus,
        ReproductionStatus reproductionStatus,
        bool independentExactMatch,
        string summary,
        string? formula,
        string? reproduction,
        EvidenceSource[] sources,
        params string[] limitations) =>
        new(AuditedPeriod, true, false, status, sourceStatus, algorithmStatus, reproductionStatus,
            independentExactMatch, summary, formula, reproduction, sources, limitations);
}
