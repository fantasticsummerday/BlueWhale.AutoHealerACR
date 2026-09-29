using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// DoT 补判 —— **「现在该不该补 DoT」的唯一权威判断**。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么要单独抽一个类 ★
///
///    原来这套判断只写在 `Res_Dot.该补Dot()` 里（private）。
///    于是**只有走优先级队列那条路才会经过它**，
///    而 AI 建议走的是另一条路：
///
///      `AiSuggestionResolver` 的优先级是 **100**，
///      **直接插到 `Res_Dot`(6) 前面**，而且采纳时只看
///      "技能解锁了吗 / 可用吗 / 需要目标有没有目标" ——
///      **完全不问"现在该不该补 DoT"**。
///
///    后果（用户实测的日志）：
///      ```
///      [小鲸鱼] 原始回复：激进|全队血量 100%...
///      采纳建议：16532 = 天辉（单体Boss目标，先补DoT）   ← AI 每轮都这么建议
///      CastSpell success: 16532 天辉
///      ```
///      天辉每 ~4.9 秒补一次，**绕过了一切剩余时间判断**。
///
///    所以判断必须**上移到共享层**：
///      · `Res_Dot`（优先级队列）用它
///      · `AiSuggestionResolver`（AI 建议）也用它
///    两边看同一份数据 —— 这正是开发约定 F③ 的精神
///    （"Check 和 Build 必须看同一份数据"的推广：
///      **同一个决策，不管从哪条路进来，判断必须一致**）。
///
///  ★ 为什么保险丝也放这里 ★
///
///    "刚放过 2.5 秒内不再放" 是防死循环的最后一道闸。
///    如果只有 `Res_Dot` 有、AI 路径没有，那 AI 就能绕过它 ——
///    等于这道闸形同虚设。**闸必须装在两条路都必经的地方。**
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class Dot补判
{
    /// <summary>
    /// 防死循环保险丝（毫秒）——**在同一个目标身上**刚放过就别连着放。
    ///
    /// 万一 buff id 对不上（客户端版本差异、新等级段没收录…），
    /// 这条能防止**无限补 DoT**。它是最后一道闸，**不要拿掉**。
    ///
    /// ⚠️ 它**只在"目标身上确实有 DoT"时才生效**（见 <see cref="该补"/> ①）。
    ///    这一点很关键，是踩过的坑：
    ///      原来它是无条件生效的，于是**切目标 / DoT 刚掉光**时
    ///      会被白白挡住 2.5 秒 —— 表现为"少补一次 DoT"，DPS 掉一截。
    ///      保险丝的本意是"别对同一个目标重复补"，
    ///      而"给新目标补"和"给掉光的目标重新补"都不该被它拦。
    /// </summary>
    private const int 保险丝毫秒 = 2500;

    /// <summary>上一次真正放出 DoT 的时间（**两条路径共用**）</summary>
    private static long _上次挂Dot;

    /// <summary>距离上次放 DoT 过了多久（毫秒）；没放过返回 -1。给诊断用</summary>
    public static long 距上次毫秒 => _上次挂Dot == 0 ? -1 : TimeHelper.Now() - _上次挂Dot;

    /// <summary>
    /// **记录一次 DoT 施放** —— 必须在技能**真正进了 slot 之后**调用
    /// （和开发约定 F③ 的"消费动作放在 Build 成功后"同一个道理）。
    ///
    /// ⚠️ 两条路径都要调：`Res_Dot.Build` 和 `AiSuggestionResolver.Build`。
    ///    漏一处，那个路径就能绕过保险丝。
    /// </summary>
    public static void 记一次施放()
    {
        try { _上次挂Dot = TimeHelper.Now(); }
        catch { }
    }

    /// <summary>
    /// **现在该补 DoT 吗** —— 这是唯一权威判断。
    ///
    /// 判断顺序（**顺序不能反**，见下面每一段的说明）：
    ///   ① 保险丝：刚放过 → 不补
    ///   ② 逐档检查所有等级的 DoT buff：
    ///        · 这一档不在身上 → 看下一档
    ///        · 在身上且撑得过 N 个 GCD → 不补
    ///        · 在身上且撑不过 → **补**
    ///   ③ 一个 buff 都没配 → 按设置的持续时间兜底
    /// </summary>
    /// <param name="目标">要检查的目标</param>
    /// <param name="所有DotBuff">本职业所有等级段的 DoT buff（一个都不在身上 = 还没上 DoT）</param>
    /// <param name="兜底间隔秒">没配 buff 时用：DoT 持续时间 - 3 秒</param>
    public static bool 该补(IBattleChara? 目标, uint[]? 所有DotBuff, float 兜底间隔秒 = 27f)
    {
        if (目标 == null) return false;

        try
        {
            var 现在 = TimeHelper.Now();

            // ① 逐档检查所有等级的 DoT buff
            if (所有DotBuff != null && 所有DotBuff.Length > 0)
            {
                var 有配置 = false;

                foreach (var b in 所有DotBuff)
                {
                    if (b == 0) continue;
                    有配置 = true;

                    // ⚠️ **必须先判"有没有"，再判"快没快"** —— 顺序不能反。
                    //
                    //    原因：`HasMyAuraWithTimeleft` / 剩余时间接口
                    //    对"目标身上根本没这个 buff"的返回值不可靠
                    //    （可能返回 0 或负，被当成"快没了"）。
                    //    反过来先判时间的话：没 buff → 判成"快没了" → 每帧都补。
                    if (!目标.HasLocalPlayerAura(b)) continue;   // 这档不在身上 → 看下一档

                    // ★ 保险丝：**只对"身上已经有 DoT"的情况生效** ★
                    //   走到这里说明"这个目标身上有我的 DoT 且刚放完" →
                    //   这才是保险丝要防的"重复补"。
                    //   （新目标 / DoT 掉光走不到这里，不受影响 —— 见常量注释）
                    if (_上次挂Dot != 0 && 现在 - _上次挂Dot < 保险丝毫秒) return false;

                    // 在身上、而且还能撑一会儿 → 确实不用补
                    // 按"还能放几个 GCD"算，而不是固定秒数（急速高时 GCD 变短）
                    if (!目标.撑不过N个Gcd(b, 2, 1.5f)) return false;

                    // 在身上、而且**撑不过 2 个 GCD** → 该补了
                    return true;
                }

                // 有配 buff、但一个都不在身上 → 还没上 DoT（或已掉光）→ 补
                //   ⚠️ 这种"完全没有 DoT"的情况**不受保险丝限制** ——
                //      它是"补上"，不是"重复补"。
                if (有配置) return true;
            }

            // ② 没配 buff：按时间兜底（这里保险丝仍然要生效，
            //    因为无法判断目标身上有没有，只能保守）
            if (_上次挂Dot != 0 && 现在 - _上次挂Dot < 保险丝毫秒) return false;

            var 间隔 = Math.Max(6f, 兜底间隔秒) * 1000f;
            if (_上次挂Dot == 0) return true;
            return 现在 - _上次挂Dot >= 间隔;
        }
        catch
        {
            // 判断不了 → **不要放行**。放行的后果是"每帧都补"的刷屏；
            // 不放行的后果只是这一轮没补上，下一轮还会再判。
            return false;
        }
    }

    /// <summary>战斗重置 / 换本 / 切职业时清（配合 OnResetBattle 等）</summary>
    public static void 重置() => _上次挂Dot = 0;
}
