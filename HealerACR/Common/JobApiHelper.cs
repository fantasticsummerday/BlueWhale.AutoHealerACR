using AEAssist;
using AEAssist.Helper;
using AEAssist.JobApi;

namespace HealerACR.Common;

/// <summary>
/// 职业资源（豆子 / 百合 / 妖精能量 / 手牌）的统一读法。
///
/// ⚠️ 0.2.0 之前的写法是 <c>catch { return 0; }</c> ——
/// 结果"JobApi 读不到"和"资源真的是 0"长得一模一样，排查时完全没线索。
/// 现在异常会记一次日志（去重），QT 面板上也会显示实时数值。
/// </summary>
public static class JobApiHelper
{
    private static readonly HashSet<string> 已报异常 = new(StringComparer.Ordinal);

    private static int 安全读(string 名字, Func<int> 读法)
    {
        try
        {
            return 读法();
        }
        catch (Exception e)
        {
            if (已报异常.Add(名字))
            {
                LogHelper.Error($"[HealerACR] 读不到职业资源「{名字}」：{e.GetType().Name} {e.Message}");
            }

            return 0;
        }
    }

    /// <summary>这个资源读成功过吗（给 QT 面板显示用）</summary>
    public static bool 读得到(string 名字) => !已报异常.Contains(名字);

    // ---------------- 贤者 ----------------

    public static int 蛇胆 => 安全读("蛇胆", () => Core.Resolve<JobApi_Sage>().Addersgall);

    public static int 毒刺 => 安全读("毒刺", () => Core.Resolve<JobApi_Sage>().Addersting);

    public static bool 均衡中
    {
        get
        {
            try { return Core.Resolve<JobApi_Sage>().Eukrasia; }
            catch { return false; }
        }
    }

    // ---------------- 学者 ----------------

    /// <summary>
    /// 以太（0-3）。
    ///
    /// ⚠️ 参考同类 ACR 的 Scholar_EnergyDrain2：它用的是
    ///    <c>GetAuraStack(自己, 304)</c> —— 也就是**读「以太超流」buff 的层数**，
    ///    而不是 JobApi_Scholar.Aetherflow。
    ///
    ///    我原来用 JobApi，它**读不到时静默返回 0** —— 于是"以太 >= 3 才卸豆子"
    ///    这类判断永远不成立，表现就是"能量吸收死活不打"。
    ///    buff 层数是直接从游戏状态读的，更可靠。
    /// </summary>
    public static int 以太
    {
        get
        {
            // 首选：buff 层数（同类 ACR 的做法）
            try
            {
                var n = CharacterExt.我的光环层数(以太BuffId);
                if (n >= 0) return n;
            }
            catch
            {
            }

            // 兜底：JobApi
            return 安全读("以太", () => Core.Resolve<JobApi_Scholar>().Aetherflow);
        }
    }

    /// <summary>以太超流的 buff ID（从游戏 Status 表 dump 核对）</summary>
    public const uint 以太BuffId = 304;

    public static int 妖精能量 => 安全读("妖精能量", () => Core.Resolve<JobApi_Scholar>().FairyGauge);

    public static bool 有小仙女
    {
        get
        {
            try { return Core.Resolve<JobApi_Scholar>().HasPet; }
            catch { return false; }
        }
    }

    public static int 炽天使剩余 => 安全读("炽天使剩余", () => Core.Resolve<JobApi_Scholar>().SeraphTimer);

    // ---------------- 白魔 ----------------

    /// <summary>百合（0-3）：满了放 安慰之心 / 狂喜之心（免蓝），并攒血百合</summary>
    public static int 百合 => 安全读("百合", () => Core.Resolve<JobApi_WhiteMage>().Lily);

    /// <summary>血百合（0-3）：满了能放苦难之心</summary>
    public static int 血百合 => 安全读("血百合", () => Core.Resolve<JobApi_WhiteMage>().BloodLily);

    // ---------------- 占星 ----------------

    /// <summary>
    /// 手上的牌（卡面数组）。
    ///
    /// [!] **读得到，而且一直是权威数据源** —— 原来那句"JobApi 的 DrawnCards
    ///     读不到"是错的（两套参考都在直接读它）：手上的牌没有任何技能 id 可查，
    ///     唯一能拿到卡面的地方就是这里。
    ///     `CheckActionChange` 只能告诉你"某个槽位上现在挂着哪张卡"，
    ///     拿不到"总共有几张、分别是什么"，所以它只能当兜底、当不了主判断。
    ///
    /// [!] 读不到时返回**空数组**（= 没牌），与"真的是 0 张"同形 ——
    ///     所以调用方不要用它做"要不要抽卡"的唯一依据，
    ///     还要留一条"抽了还没出"的时间保险丝（见 `AST卡牌状态`）。
    /// </summary>
    public static CardType[] 手牌()
    {
        try
        {
            return Core.Resolve<JobApi_Astrologian>().DrawnCards ?? Array.Empty<CardType>();
        }
        catch (Exception e)
        {
            if (已报异常.Add("手牌"))
            {
                LogHelper.Error($"[HealerACR] 读不到职业资源「手牌」：{e.GetType().Name} {e.Message}");
            }

            return Array.Empty<CardType>();
        }
    }

    /// <summary>手上有几张牌</summary>
    public static int 手牌数 => 手牌().Length;

    /// <summary>手上每张牌的名字（CardType 的运行时名字，给日志/诊断看）</summary>
    public static string[] 手牌名字()
    {
        var 卡 = 手牌();
        if (卡.Length == 0) return Array.Empty<string>();

        var 结果 = new string[卡.Length];
        for (var i = 0; i < 卡.Length; i++)
        {
            结果[i] = 卡[i].ToString();
        }

        return 结果;
    }

    /// <summary>
    /// 手上有没有近战卡。
    ///
    /// [!] 判据是**卡面本身**（`CardType.Balance` / `CardType.Spear`），
    ///     不是字符串匹配 —— 卡面是枚举，直接比数值最可靠。
    ///     设置里的关键词只是**备用**判据：一旦哪天卡面读不到，
    ///     还能靠运行时名字兜一层，不至于直接当成"没有近战卡"。
    /// </summary>
    public static bool 有近战卡()
    {
        var 卡 = 手牌();
        foreach (var c in 卡)
        {
            if (占星卡.是近战卡(c)) return true;
        }

        // 卡面读不到（空数组）时，才退回关键词匹配
        if (卡.Length > 0) return false;

        var 名单 = HealSettings.Instance.近战卡关键词;
        if (string.IsNullOrWhiteSpace(名单)) return false;

        var 关键词 = 名单.Split(',', '，', ' ', ';');

        foreach (var 名 in 手牌名字())
        {
            if (string.IsNullOrEmpty(名)) continue;

            foreach (var k in 关键词)
            {
                if (k.Length == 0) continue;
                if (名.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }

    /// <summary>有没有小阿卡纳（大阿卡纳）在手</summary>
    public static bool 有大阿卡纳()
    {
        try
        {
            var c = Core.Resolve<JobApi_Astrologian>().DrawnCrownCard;
            var s = c.ToString();
            return s.Length > 0 && !s.Equals("None", StringComparison.OrdinalIgnoreCase) && s != "0";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 现在是"灵极"状态吗（`JobApi.ActiveDraw`）。
    ///
    /// [!] 它是枚举 `DrawType`：`Astral = 0`（星极）/ `Umbral = 1`（灵极）。
    ///     参考实现把它当布尔用（非 0 就是灵极），这里照做 ——
    ///     作用只是"抽卡该按哪一张"，**不参与任何资源判断**。
    ///
    /// [!] 用途：抽卡会在**星极抽卡(37017)** 与 **灵极抽卡(37018)** 之间轮换，
    ///     共享 55 秒 CD。正常情况下 `CheckActionChange` 会把技能换成当前那一档，
    ///     万一查不到，就靠它自己选出该按哪一个（见 <see cref="抽卡形态"/>）。
    /// </summary>
    public static bool 灵极中
    {
        get
        {
            try { return (int)Core.Resolve<JobApi_Astrologian>().ActiveDraw != 0; }
            catch { return false; }
        }
    }

    /// <summary>
    /// 该按哪一张抽卡（`CheckActionChange` 失灵时的兜底形态）。
    /// 拿不到技能返回 null —— 调用方据此放过这一拍，**不要瞎猜一个 id**。
    /// </summary>
    public static Spell? 抽卡形态()
    {
        try
        {
            var id = 灵极中 ? SpellIds.取("灵极抽卡") : SpellIds.取("星极抽卡");
            return id == 0 ? null : SpellUtil.Get(id);
        }
        catch
        {
            return null;
        }
    }
}
