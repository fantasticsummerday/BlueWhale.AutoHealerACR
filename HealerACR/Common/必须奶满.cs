using AEAssist;
using AEAssist.Helper;
using AEAssist.MemoryApi;
using HealerACR.Timeline;

namespace HealerACR.Common;

/// <summary>
/// 「必须奶满」类副本机制 —— **不奶满就会死**。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 这是什么机制 ★
///
///    某些副本会给队友挂一个 **必须把血奶到满** 才解除的 debuff，
///    效果结束前没奶满 → 直接变僵尸 / 石化 / 无法战斗。
///
///    这跟"血少了要奶"完全不是一个东西：
///      · 普通掉血：**血量低**才需要奶
///      · 这类机制：**血量可能还很健康**，但必须**奶到满**才解除
///    如果只按血线判断，AI 会觉得"这人 80% 血没事" → 时间到 → 人没了。
///
///    所以必须**单独识别**，并且给 AI 一份明确的说明。
///
///  ★ 数据来源（不是凭印象）★
///
///    `Status.csv` 的 **Description** 直接写明了机制，例如：
///      · 370  塞壬的歌声：「在效果结束之前**如果体力没有恢复到最大值**便会变成僵尸」
///      · 1628 渐渐石化：  「不断石化，**体力完全恢复时**效果解除」
///    这两条是"必须奶满"的教科书式定义，直接采用。
///
///  ⚠️ **明确排除**的几个（曾经差点被误收）：
///      · 811 死而不僵 / 3255 出死入生 / 2303 纯正死而不僵
///        —— **版本更新后不再需要奶满**（用户确认 + 同类 ACR 的表里也没有它们）。
///        硬把 811 当"必须奶满"会把坦克的正常减伤循环搅乱。
///      · 210 / 1769 死亡宣告 —— 描述只说"倒计时为 0 时无法战斗"，
///        **解除条件是驱散，不是奶满**。走 `Res_Esuna` 那条路。
///
///  ★ 为什么要带"地图"维度 ★
///
///    同类 ACR 的表是 `(状态, 地图)` 配对 —— 同一个 buff ID
///    在不同副本里可能是完全不同的东西（游戏里 ID 复用很常见）。
///    地图填 0 = 任何地图都适用；填了具体地图 = 只在那张图里才当机制处理。
///    这样**不会在别的副本里因为撞 ID 而误判**。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 必须奶满
{
    /// <summary>
    /// (状态 ID, 限定地图 ID)。地图为 0 表示不限定。
    ///
    /// 维护方式：新副本出现这类机制时，用 `dump_status` / `Status.csv`
    /// 按描述关键词反查（「体力没有恢复到最大值」「体力完全恢复时」），
    /// **不要凭印象加 ID**（开发约定 ③ 的教训）。
    /// </summary>
    private static readonly (uint 状态, uint 地图)[] 表 =
    {
        // 塞壬的歌声：效果结束前没奶满 → 变僵尸（Sastasha 系 / 沉没神殿系）
        (370u, 0u),
        // 渐渐石化：不断石化，体力完全恢复时解除
        (1628u, 0u),
    };

    /// <summary>当前地图 ID（拿不到返回 0）</summary>
    private static uint 当前地图
    {
        get
        {
            try { return Core.Resolve<MemApiZoneInfo>().GetCurrTerrId(); }
            catch { return 0; }
        }
    }

    /// <summary>
    /// 这个状态下，**当前地图**要不要按"必须奶满"处理。
    ///
    /// 地图不匹配时返回 false —— 宁可漏判也不要误判：
    /// 误判的代价是"在普通副本里对着一个无关 buff 猛灌治疗"。
    /// </summary>
    public static bool 是必须奶满状态(uint 状态Id)
    {
        if (状态Id == 0) return false;

        try
        {
            var 地图 = 当前地图;

            foreach (var (状态, 限定地图) in 表)
            {
                if (状态 != 状态Id) continue;

                // 不限定地图 → 直接算
                if (限定地图 == 0) return true;

                // 限定了地图 → 必须对得上
                if (地图 != 0 && 地图 == 限定地图) return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>这个队友身上有没有"必须奶满"的状态；返回命中的状态 ID（0 = 没有）</summary>
    public static uint 命中状态(IBattleChara? 目标)
    {
        if (目标 == null) return 0;

        try
        {
            foreach (var (状态, _) in 表)
            {
                if (状态 == 0) continue;
                if (!是必须奶满状态(状态)) continue;      // 地图不匹配就跳过
                if (目标.HasAura(状态)) return 状态;
            }
        }
        catch { }

        return 0;
    }

    /// <summary>有没有人正处在"必须奶满"状态</summary>
    public static bool 有人需要奶满() => 找目标() != null;

    /// <summary>
    /// 找出**最该被奶满**的队友。
    ///
    /// 选择规则：谁的这个 debuff **剩余时间最短**谁最急
    /// （时间一到就死，所以按紧迫度排，而不是按血量排）。
    /// 拿不到剩余时间时退回"血量最低的那个"。
    /// </summary>
    public static IBattleChara? 找目标()
    {
        try
        {
            IBattleChara? 最优 = null;
            var 最优剩余 = float.MaxValue;

            // 自己也可能中这个机制
            foreach (var 队员 in 候选())
            {
                if (队员 == null) continue;

                var 状态 = 命中状态(队员);
                if (状态 == 0) continue;

                var 剩余 = 队员.我的Buff剩余秒(状态);
                if (剩余 < 0f) 剩余 = float.MaxValue;   // 拿不到就排最后（但仍算命中）

                if (剩余 < 最优剩余)
                {
                    最优剩余 = 剩余;
                    最优 = 队员;
                }
            }

            return 最优;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>候选：所有可治疗的队友 + 自己</summary>
    private static List<IBattleChara> 候选()
    {
        var 结果 = new List<IBattleChara>(8);

        try
        {
            if (Core.Me.活着()) 结果.Add(Core.Me);

            var 队友 = PartyHelper.CastableAlliesWithin30;
            if (队友 != null)
            {
                foreach (var r in 队友)
                {
                    if (r == null) continue;
                    if (r.GameObjectId == Core.Me.GameObjectId) continue;   // 自己已经加过
                    if (!r.可以治()) continue;
                    结果.Add(r);
                }
            }
        }
        catch { }

        return 结果;
    }

    /// <summary>
    /// 给 AI 看的一段描述。
    ///
    /// ⚠️ **必须明确写出"这不是按血线判断"** ——
    ///    否则 AI 会按常识回一句"目标血量健康，不需要治疗"，
    ///    而这正是这个机制最危险的地方。
    /// </summary>
    public static string 状态描述()
    {
        try
        {
            var 目标 = 找目标();
            if (目标 == null) return "";

            var 状态 = 命中状态(目标);
            var 名 = SpellIds.反查(状态);
            if (string.IsNullOrEmpty(名)) 名 = "状态 " + 状态;

            var 剩余 = 目标.我的Buff剩余秒(状态);
            var 血 = 目标.血量比例() * 100f;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("⚠️⚠️ 【必须奶满机制】有人中了会致死的状态，**必须把血奶到 100% 才能解除**");
            sb.AppendLine($"   目标：{目标.Name}  当前血量 {血:F0}%  状态：{名}（{状态}）"
                          + (剩余 >= 0f ? $"  剩余 {剩余:F1} 秒" : ""));
            sb.AppendLine("   ⚠️ 判断依据**不是血量**：就算这个人血量看着健康，只要这个状态还在，"
                          + "效果结束时会直接无法战斗 / 变僵尸。");
            sb.AppendLine("   正确处理：**优先对他使用治疗量最大的单体治疗**，直到状态消失。");
            sb.AppendLine("   ⚠️ 不要因为\"他血还挺多\"而把治疗给别人 —— 这是这个机制唯一的解。");

            return sb.ToString();
        }
        catch (Exception e)
        {
            return "（必须奶满状态读取失败：" + e.Message + "）" + Environment.NewLine;
        }
    }
}
