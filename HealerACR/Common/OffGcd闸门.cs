using AEAssist;
using AEAssist.CombatRoutine.Module;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// **能力技队列深度闸门** —— 对照分析发现的缺口。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 这是参考实现 A「不会一帧倒一堆能力技」的**唯一手段** ★
///
///  ── 它的实现（IL 直证）──
///    `ACR.Tool.Resolvers.HealerResolverBase.CanUseOffGcd(int limit)`
///        => AI.Instance.BattleData.HighPrioritySlots_OffGCD.Count &lt; limit;
///
///    四个职业**几乎每个 oGCD resolver** 都有 `CanUseOffGcd(1)` 或 `CanUseOffGcd(2)`。
///
///  ── 为什么这个闸门是真机制（不是死代码）──
///    我反汇编了框架的 `RunSlotResolvers.MoveNext`，执行顺序是：
///        ① 排空 `HighPrioritySlots_GCD`（受 `CanUseHighPrioritySlots` 闸门控制）
///        ② 排空 `HighPrioritySlots_OffGCD`（同一个闸门）
///        ③ **然后**才进普通优先级循环（遍历 `Rotation.SlotResolverList`）
///    ⇒ 队列里的槽会被**优先执行**，所以队列深度是真实负载。
///
///    入队由 ACR 自己负责（IL 直证其一，A 的「场地中轴半程放置」节点）：
///        BattleData.HighPrioritySlots_OffGCD.Enqueue(new Slot().Add(地面技能))
///
///  ── 我们的问题 ──
///    我们**一个 Enqueue 都没有，也一个 Count 都没查**。
///    所以队列恒空 → 闸门恒真 → 没有节流。
///    血崩瞬间可能把 天赐 → 慰藉 → 不屈 → 法令 连着几帧全倒出去。
///
///  ⚠️ **本类只做闸门，不做入队** —— 入队是"我要抢先放这个技能"的语义，
///     属于强制插队，和"节流"是两件事。混在一起会让行为难以预测。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class OffGcd闸门
{
    /// <summary>
    /// 现在能不能再排一个能力技。
    ///
    /// <paramref name="上限"/> 语义 = "**允许积压几个**"：
    ///   · `1` = 最多允许 1 个在队列里（最严，参考实现里最常见）
    ///   · `2` = 允许 2 个（血崩时想连发两个就用这个）
    /// </summary>
    /// <remarks>
    /// ⚠️ **失败方向是"放行"**（`catch` 返回 true）——
    ///    读不到队列时宁可多放一个能力技，也不要**把治疗全哑掉**。
    ///    "少一个节流"是可接受的，"该救人的时候不放"不是。
    /// </remarks>
    public static bool 可以排(int 上限 = 1)
    {
        try
        {
            var 队列 = AI.Instance?.BattleData?.HighPrioritySlots_OffGCD;
            if (队列 == null) return true;      // 拿不到队列 → 放行（保守见上）
            return 队列.Count < 上限;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// **本职业该用几档**（口径统一）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  [!] 表 #120：原来这里写的是「**贤者 → 2，其余 → 1**」✗ —— 那是错的。
    ///
    ///  [!] 把所有参考 IL 里的 `CanUseOffGcd(N)` 数了一遍（按职业分目录统计）：
    ///        · **`2` 是绝对主流**：youshu 56 处 / cond 17 处
    ///        · **`1` 只有 12 处**，而且全是这几个特例：
    ///            占星 **出卡2 / 出卡3 / 神谕**
    ///            四奶各自的 **醒梦**
    ///            贤者 **心关**
    ///            学者 **埋伏之毒**
    ///        ⇒ **`1` 是"点名特例"，`2` 是默认** ——
    ///          反过来配（默认 1、特例 2）等于**把绝大多数 oGCD 都掐死**：
    ///          血崩时白魔/学者/占星只能积压一个能力技 ✗
    ///
    ///  [!] 所以改成**默认 2**；那几个特例在自己的 resolver 里**显式写 1**
    ///      （心关 / 醒梦 / 占星出卡 / 学者埋伏之毒）。
    ///
    ///  [!] 读不到职业时也返回 **2** —— 和参考的默认一致；
    ///      返回 1 会让"读不到职业"变成一个**静默的治疗降级**。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static int 默认上限()
    {
        // 参考里 `2` 是默认、`1` 是点名特例 ⇒ 默认给 2，特例由各自的 resolver 显式传 1
        return 2;
    }

    /// <summary>当前积压了几个能力技槽位（拿不到返回 -1）</summary>
    public static int 当前深度()
    {
        try        {
            var 队列 = AI.Instance?.BattleData?.HighPrioritySlots_OffGCD;
            if (队列 == null) return -1;
            return 队列.Count;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// 诊断：这个闸门**到底会不会真的关上**。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ⚠️ 为什么要有这个方法 —— 我差点又做了一个"不起作用的东西"
    ///
    ///    查证过程：
    ///      · 我的第一次 token 扫描说框架**从不读**这两个队列 → 判定死代码
    ///      · 精确反汇编后推翻：`RunSlotResolvers` 两个队列都排空
    ///      · 但**入队是谁做的**一直没定位到
    ///
    ///    如果没人入队，`Count` 恒为 0 → `0 < 1` 恒真 → 闸门**永远是开的**。
    ///    那样加它就是纯装饰（正是本项目反复出现过的"注册了但没人读"的反面：
    ///    "读了但永远是同一个值"）。
    ///
    ///  ⇒ 所以先用**只读诊断**确认 `Count` 在实战里会变成非 0，再信这个闸门。
    ///     诊断结果会打进日志，看到 `深度` 一直非 0 才算验证通过。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static int _观测到的最大深度;
    private static long _上次输出;

    /// <summary>每帧调用：记录观测到的最大深度，变化时打一行日志</summary>
    public static void 每帧更新()
    {
        try
        {
            var 深 = 当前深度();
            if (深 > _观测到的最大深度)
            {
                _观测到的最大深度 = 深;
                LogHelper.Info($"[HealerACR] OffGcd 队列深度创新高 = {深}（闸门上限 1，达到 1 即开始节流）");
            }

            // 十秒一行心跳，方便判断"是不是一直是 0"
            var 现在 = TimeHelper.Now();
            if (现在 - _上次输出 > 10000)
            {
                _上次输出 = 现在;
                LogHelper.Info($"[HealerACR] OffGcd 闸门诊断：当前深度 {深}，历史最大 {_观测到的最大深度}");
            }
        }
        catch { }
    }

    /// <summary>重置</summary>
    public static void 重置()
    {
        _观测到的最大深度 = 0;
        _上次输出 = 0;
    }
}
