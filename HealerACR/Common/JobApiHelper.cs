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
    /// ⚠️ 对照 鍚岀被 ACR 的 Scholar_EnergyDrain2：它用的是
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
            // 首选：buff 层数（鍚岀被 ACR 的做法）
            try
            {
                var n = Core.Me.GetAuraStack(以太BuffId);
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

    public static int 手牌数
    {
        get
        {
            try
            {
                var cards = Core.Resolve<JobApi_Astrologian>().DrawnCards;
                return cards?.Length ?? 0;
            }
            catch (Exception e)
            {
                if (已报异常.Add("手牌"))
                {
                    LogHelper.Error($"[HealerACR] 读不到职业资源「手牌」：{e.GetType().Name} {e.Message}");
                }

                return 0;
            }
        }
    }

    /// <summary>手上每张牌的名字（CardType 的运行时名字）</summary>
    public static string[] 手牌名字()
    {
        try
        {
            var cards = Core.Resolve<JobApi_Astrologian>().DrawnCards;
            if (cards == null) return Array.Empty<string>();

            var 结果 = new string[cards.Length];
            for (var i = 0; i < cards.Length; i++)
            {
                结果[i] = cards[i].ToString();
            }

            return 结果;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>手上有没有近战卡（关键词可在设置里改）</summary>
    public static bool 有近战卡()
    {
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
}
