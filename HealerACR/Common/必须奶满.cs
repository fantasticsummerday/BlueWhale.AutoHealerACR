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
///  ══════════════════════════════════════════════════════════════════
///  ★★ 保守策略：**ID + 名字 双保险**（用户要求）★★
///
///    只认 ID 有个风险：游戏里 **ID 复用很常见**，同一个 ID
///    在不同副本/版本里可能是完全不相干的东西。
///    一旦撞上，我们就会"对着一个无关 buff 猛灌治疗"。
///
///    所以这里多加一道**名字核对**：读出目标身上那个 aura 的**实际名字**，
///    必须和我们记录的名字（或别名）一致，才认为它真是这个机制。
///
///    为什么不按地图限定（同类 ACR 的做法）：
///      要按地图限定就得知道"这机制出在哪张图"，而这需要一个小型
///      副本名→ZoneId 对照表。我们的 `dump_territory.tsv` 里只有
///      「区域名 + 地名」，**没有可靠的副本名** —— 硬填就是猜。
///      而**猜错地图 ID 的后果是机制彻底失效**（人直接死），
///      比名字核对的漏判严重得多。所以选了名字核对这条更可靠的路。
///
///    **名字核对不通过 → 当作不是这个机制，按普通治疗走。**
///    （漏判的代价是"回到改动前的行为"，误判的代价是"治疗被一个无关 buff 骗走"。）
///
///  ══════════════════════════════════════════════════════════════════
///  ★★ 本地优先：这个判断**不依赖 AI** ★★
///
///    识别（本类）和处理（`Res_HealSingleGcd` 的优先级 30）**全在 HealerACR 本地层**。
///    AI 那侧（`AiSituation`）只是把结果**读出来转述**给模型，不参与判断。
///
///    已验证：`HealerACR` 对 BlueWhale 的编译期引用是 **0 处**
///    （`记忆钩子.cs` 里那一处是注释），且该工程**能单独编译通过**。
///
///    所以下面这些情况，机制照样生效：
///      · 没填 API Key / AI 未配置
///      · 断网 / 请求超时 / 三层熔断降级
///      · DeepSeek 返回了胡说八道（白名单会丢，但本地判断不受影响）
///    **AI 是增强层，不是必需品** —— 这是本项目的既定原则，新功能都要守住。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 必须奶满
{
    /// <summary>
    /// 必须奶满的状态表。
    ///
    /// <c>别名</c> 用于容忍中英文 / 版本差异 —— **任意一个名字对上就算命中**。
    /// 留空表示"只要 ID 对上就行"（不确定名字时才这么用）。
    ///
    /// 维护方式：新副本出现这类机制时，用 `Status.csv` 按描述关键词反查
    /// （「体力没有恢复到最大值」「体力完全恢复时」），
    /// **不要凭印象加 ID**（开发约定 ③ 的教训）。
    /// </summary>
    private static readonly (uint Id, string 主名, string[] 别名)[] 表 =
    {
        // 塞壬的歌声：效果结束前没奶满 → 变僵尸
        (370u, "塞壬的歌声", new[] { "Siren's Song", "Sirensong", "塞壬之歌" }),

        // 渐渐石化：不断石化，体力完全恢复时解除
        (1628u, "渐渐石化", new[] { "Gradual Petrification", "Petrification", "石化" }),
    };

    /// <summary>
    /// 这个状态下，要不要按"必须奶满"处理。
    ///
    /// ⚠️ <paramref name="目标"/> 为空时**只按 ID 判断**（无法核对名字）。
    ///    调用方应该尽量传目标 —— 传了才会走双保险。
    /// </summary>
    public static bool 是必须奶满状态(uint 状态Id, IBattleChara? 目标 = null)
    {
        if (状态Id == 0) return false;

        try
        {
            foreach (var 项 in 表)
            {
                if (项.Id != 状态Id) continue;

                // 没有目标 → 没法核对名字，只能信 ID（保守起见仍放行，
                // 因为漏判的代价是人死；但调用方都会传目标）
                if (目标 == null) return true;

                return 名字对得上(目标, 状态Id, 项.主名, 项.别名);
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 保守核对：目标身上这个 aura 的**实际名字**必须和记录的一致。
    ///
    /// 拿不到名字时返回 false（= 不认）—— **这是有意的**：
    /// 保守策略宁可漏判（退回普通治疗，人还有机会被血量判断救到），
    /// 也不要误判（把所有治疗都砸在一个无关 buff 上，真正掉血的人没人管）。
    /// </summary>
    private static bool 名字对得上(IBattleChara 目标, uint 状态Id, string 主名, string[] 别名)
    {
        var 实际 = 取状态名(目标, 状态Id);
        if (string.IsNullOrWhiteSpace(实际)) return false;   // 拿不到名字 → 不认

        if (!string.IsNullOrWhiteSpace(主名) && 实际.Contains(主名)) return true;

        if (别名 != null)
        {
            foreach (var a in 别名)
            {
                if (string.IsNullOrWhiteSpace(a)) continue;
                if (实际.Contains(a)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 从目标身上取出指定 aura 的**显示名**。
    ///
    /// 走 Dalamud 的 StatusList（AEAssist 也是这么读的），
    /// 拿不到返回空串。
    /// </summary>
    private static string 取状态名(IBattleChara 目标, uint 状态Id)
    {
        try
        {
            var 列表 = 目标.StatusList;
            if (列表 == null) return "";

            foreach (var s in 列表)
            {
                if (s == null) continue;
                if (s.StatusId != 状态Id) continue;

                // ⚠️ Status.GameData 是 RowRef<Status>（struct），**不能用 ?.**
                //    取不到就抛异常，靠外层 catch 兜住
                var 名 = s.GameData.Value.Name.ToString();
                return string.IsNullOrWhiteSpace(名) ? "" : 名;
            }
        }
        catch { }

        return "";
    }

    /// <summary>这个队友身上有没有"必须奶满"的状态；返回命中的状态 ID（0 = 没有）</summary>
    public static uint 命中状态(IBattleChara? 目标)
    {
        if (目标 == null) return 0;

        try
        {
            foreach (var 项 in 表)
            {
                if (项.Id == 0) continue;
                if (!目标.HasAura(项.Id)) continue;      // 没有这个 aura，跳过
                if (是必须奶满状态(项.Id, 目标)) return 项.Id;
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
