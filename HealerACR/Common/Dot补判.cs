using AEAssist.Helper;
using System.Collections.Generic;

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
///    后果（日志实证）：
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

    /// <summary>距上次放 DoT 过了多久（毫秒）；没放过返回 -1。给诊断用</summary>
    public static long 距上次毫秒 => _上次挂Dot == 0 ? -1 : TimeHelper.Now() - _上次挂Dot;

    // ==================== 按目标的双窗口 ====================

    /// <summary>某个目标的挂 DoT 记录</summary>
    private sealed class 单目标记录
    {
        public long 按下时间;
        public bool 已确认成功;
    }

    private static readonly Dictionary<ulong, 单目标记录> _按目标 = new();

    /// <summary>挂起窗口（毫秒）—— 按下之后这段时间不重判（等 buff 读出来）</summary>
    private const int 挂起窗口毫秒 = 5000;

    /// <summary>
    /// 成功抑制窗口的**上限**（毫秒）。
    ///
    /// ⚠️ 这是**上限不是定值**（审计指出第一版写死 27 秒有问题）：
    ///    `Dot持续时间` 是**用户可调**的（3~30 秒），写死 27 秒会让
    ///    **短 DoT 空转** —— 比如 15 秒的 DoT，掉光后仍被挡到第 27 秒才准补，
    ///    **空窗最长 (27 − 实际时长) 秒**，DPS 白掉一截。
    ///
    /// ⇒ 实际窗口取 `min(这个上限, 配置时长 − 3 秒)`，见 `抑制窗口毫秒()`。
    /// </summary>
    private const int 成功抑制上限毫秒 = 27000;

    /// <summary>
    /// 实际的成功抑制窗口 —— **跟用户配的 DoT 时长走**，并夹在上限内。
    ///
    /// ⚠️ 为什么减 3 秒：和 `该补()` 的兜底判据同一个余量
    ///    （`Dot持续时间 - 3f`）—— 剩 3 秒以内就该续了，
    ///    抑制窗口不能比"该续的时间点"还长。
    /// </summary>
    private static int 抑制窗口毫秒()
    {
        try
        {
            var 配置毫秒 = (int)(HealSettings.Instance.Dot持续时间 * 1000f) - 3000;
            if (配置毫秒 < 挂起窗口毫秒) 配置毫秒 = 挂起窗口毫秒;   // 至少不短于挂起窗口
            return Math.Min(配置毫秒, 成功抑制上限毫秒);
        }
        catch
        {
            return 成功抑制上限毫秒;
        }
    }

    /// <summary>记录表上限 —— 防止打一整场小怪后无限增长</summary>
    private const int 记录上限 = 32;

    /// <summary>
    /// 按目标判断：**现在该不该跳过**。
    /// 返回 true = 在窗口内、别补；false = 窗口外、走正常判断。
    /// </summary>
    private static bool 在抑制窗口内(IBattleChara 目标, uint[]? 所有DotBuff)
    {
        // ★ 入口判有效性：参数是游戏对象，读它的属性会因【已释放对象】而
        //   触发原生访问违例（穿 catch / 无转储 / 进程直接没）。
        //   本项目 12 次崩溃全部是这一类 —— 不假设调用方判过。
        if (目标 == null || !目标.对象有效()) return false;
        try
        {
            var id = 目标.GameObjectId;
            if (id == 0 || !_按目标.TryGetValue(id, out var r)) return false;

            var 经过 = TimeHelper.Now() - r.按下时间;

            // ① 读到了我们的 DoT ⇒ 确认成功，之后走 27 秒完全抑制
            if (!r.已确认成功 && 所有DotBuff != null)
            {
                foreach (var b in 所有DotBuff)
                {
                    if (b != 0 && 目标.HasLocalPlayerAura(b)) { r.已确认成功 = true; break; }
                }
            }

            // ② 确认成功 → 27 秒内完全不补（防 buff 读取延迟骗过剩余时间判断）
            if (r.已确认成功) return 经过 < 抑制窗口毫秒();

            // ③ 还没确认成功 → 挂起窗口内不重判（等 buff 读出来）
            return 经过 < 挂起窗口毫秒;
        }
        catch
        {
            return false;   // 判不了就别拦
        }
    }

    /// <summary>
    /// **记录一次 DoT 施放** —— 必须在技能**真正进了 slot 之后**调用
    /// （和开发约定 F③ 的"消费动作放在 Build 成功后"同一个道理）。
    ///
    /// ⚠️ 两条路径都要调：`Res_Dot.Build` 和 `AiSuggestionResolver.Build`。
    ///    漏一处，那个路径就能绕过保险丝。
    /// </summary>
    public static void 记一次施放() => 记一次施放(null);

    /// <summary>
    /// **按目标**记录一次 DoT 施放。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么要按目标（取自对照实现的 `HealerDotCastTracker`）★
    ///
    ///  ── 全局保险丝的缺陷 ──
    ///    原来只有一个 `_上次挂Dot`，**不区分目标** ——
    ///    给 A 挂了 DoT 之后 2.5 秒内给 B 挂**也会被挡掉**。
    ///    表现：一波小怪想连铺 2~3 个 DoT 时，**后面几个永远排不上**。
    ///
    ///  ── 参考实现的两个窗口（IL 直证）──
    ///      · **挂起窗口 5000ms**：刚按下、buff 还没读到 ——
    ///        这段期间不重判，否则会因为"读不到 buff"而重复补。
    ///      · **成功抑制窗口 27000ms**：DoT 真上去了 ——
    ///        这段期间**完全不重补**，防"buff 读取延迟"骗过剩余时间判断。
    ///
    ///  ⚠️ 两个窗口缺一不可：
    ///     只留挂起 → buff 读到后就没抑制了，延迟仍会骗过判断；
    ///     只留成功 → 从"按下"到"成功"这段没人管，每 GCD 重判一次。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    /// <summary>
    /// **按目标 id 记一次施放**（不碰 live 对象）。
    ///
    /// [!] 为什么需要这个重载（外部审查 P0-4，我核实确认为真）：
    ///      `收尾()` 是在 `slot.Add()` **之后**调的 —— 那一刻读 live 目标属性
    ///      既可能因对象刚好失效而崩，更不该影响"要不要消费 AI 建议"。
    ///      ==> 改成调用方**提前**把 id 取出来（纯托管 `ulong`）传进来。
    /// </summary>
    public static void 记一次施放(ulong 已知Id) => 记一次施放(null, 已知Id);

    public static void 记一次施放(IBattleChara? 目标, ulong 已知Id = 0)
    {
        // ★ 入口判有效性：参数是游戏对象，读它的属性会因【已释放对象】而
        //   触发原生访问违例（穿 catch / 无转储 / 进程直接没）。
        //   本项目 12 次崩溃全部是这一类 —— 不假设调用方判过。
        // ⚠️ 这是 void 函数 —— 用 `return;`，不是 `return null;`
        //
        // [!] **不再因目标失效而整体放弃**（P0-4）：
        //     `_上次挂Dot`（保险丝主体）与"按目标那份记录"是**两件事**，
        //     前者不该被"这次读不到对象"连累 —— 否则 AI 放出的 DoT
        //     不会记保险丝，下一步 Dot补判 又会判"该补"。
        try
        {
            _上次挂Dot = TimeHelper.Now();

            var id = 已知Id;
            if (id == 0)
            {
                if (目标 == null || !目标.对象有效()) return;
                id = 目标.GameObjectId;
            }
            if (id == 0) return;

            // 超上限就丢掉最旧的（简单清理，够用）
            if (!_按目标.ContainsKey(id) && _按目标.Count >= 记录上限)
            {
                ulong 最旧 = 0;
                long 最旧时间 = long.MaxValue;
                foreach (var kv in _按目标)
                {
                    if (kv.Value.按下时间 < 最旧时间) { 最旧时间 = kv.Value.按下时间; 最旧 = kv.Key; }
                }
                _按目标.Remove(最旧);
            }

            _按目标[id] = new 单目标记录 { 按下时间 = TimeHelper.Now(), 已确认成功 = false };
        }
        catch { }
    }

    /// <summary>当前有多少条按目标记录（诊断用）</summary>
    public static int 记录条数 => _按目标.Count;

    /// <summary>
    /// **现在该补 DoT 吗** —— 这是唯一权威判断。
    ///
    /// 判断顺序（**顺序不能反**，见下面每一段的说明）：
    ///   ⓪ 按目标的双窗口（挂起 5s / 成功抑制 27s）—— 在窗口内直接不补
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

            // ⓪ 按目标的双窗口（最优先 —— 它比"剩余时间"更可靠）
            if (在抑制窗口内(目标, 所有DotBuff)) return false;

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
    public static void 重置()
    {
        _上次挂Dot = 0;
        // ⚠️ 按目标表也要清 —— 换本/换职业后 GameObjectId 会变，
        //    不清的话旧记录会占着表位（虽然不会误伤，但会提前触发清理）。
        _按目标.Clear();
    }
}
