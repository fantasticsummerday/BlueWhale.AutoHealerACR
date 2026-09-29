using System.Numerics;
using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;
using Dalamud.Game.ClientState.Objects.Types;

namespace HealerACR.Common;

/// <summary>
/// **输出目标选择器** —— 决定"这一发打谁"，带粘滞防乱切。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么需要它（实测指出的问题）★
///
///  ── 现象 ──
///     · 一直切目标
///     · 坦克拉到怪了，却不去打那只，反而打远处的
///
///  ── 根因 ──
///    我们原来**根本没有"选目标"这一步** —— 输出和 DoT 全部直接读
///    <c>Core.Me.GetCurrTarget()</c>，也就是**玩家手动选中的那个**。
///    于是：玩家选中远处的怪 → 打远处；玩家手动换来换去 → 跟着切。
///    ACR 完全没有自己的判断。
///
///  ── 参考实现是怎么做的（IL 直证）──
///    它们有一层 `HealerEnemyTargetHelper` + `SwitchTargetIfNeeded`：
///    自己算该打谁，需要时**主动切**过去。
///
///  ── 我们的做法：算，但**不抢选中** ──
///    本类只**返回**该打的目标，由调用方用
///    `new Spell(id, 目标)` 打出去（技能自带目标，框架按它施放）。
///
///    ⇒ **玩家选中的目标不会被改** —— 比"主动 SetTarget"克制得多，
///      少了"ACR 抢我目标"这种烦人行为，也不会和玩家抢操作。
///
///  ══════════════════════════════════════════════════════════════════
///  ★ 粘滞（防乱切）是这里的核心 ★
///
///    纯打分函数有两个致命毛病：
///      ① **抖动** —— 两个怪分数接近时，每帧算出来的赢家会来回换，
///         表现就是"一直切目标"
///      ② **快死的不补刀** —— 一只怪 5% 血了，另一只满血，
///         如果按"满血优先"打分就会放掉快死的那只，
///         结果它被队友收掉、或者更糟：拖到它读完一个 AOE
///
///    ⇒ 所以：
///      · 目标还有效（活着 + 有仇恨 + 在打得到的范围内）时**继续用它**
///      · 只有它失效才重新挑 —— 这就是粘滞
///      · 快死的怪给**加分**（收掉它 = 少一个敌人），不是减分
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 输出目标
{
    /// <summary>
    /// 认定"还能打得到"的距离（米）。
    ///
    /// ⚠️ 用 25 米是**施法距离**，不是伤害范围。
    ///    超过它连技能都放不出去，再"有价值"也没用。
    /// </summary>
    private const float 可打距离 = 25f;

    /// <summary>
    /// 目标血量低于这个比例时**加分**（优先收掉）。
    ///
    /// 收掉一个快死的怪 = 场上少一个敌人（少一份伤害、少一次读条）。
    /// 但加分**不能盖过"近"** —— 远处的快死怪跑过去打反而亏。
    /// </summary>
    private const float 补刀血线 = 0.25f;

    /// <summary>补刀加分（乘以自身血量越低加得越多）</summary>
    private const float 补刀权重 = 60f;

    /// <summary>距离权重：每米扣多少分</summary>
    private const float 距离权重 = 2f;

    /// <summary>上一帧选中的目标（粘滞用）</summary>
    private static ulong _粘住的目标;

    /// <summary>上次重新选择的时间（防止高频重算）</summary>
    private static long _上次选择;

    /// <summary>最短保持时间（毫秒）—— 这期间不换，除非当前目标失效</summary>
    private const int 最短保持毫秒 = 400;

    /// <summary>
    /// **这一发该打谁**。
    ///
    /// 拿不到合适的就返回 null，调用方应当退回"不放技能"
    /// （而不是硬打一个远处的怪）。
    /// </summary>
    public static IBattleChara? 选()
    {
        try
        {
            // ── ① 粘滞：上一个目标还有效就继续用 ──
            var 粘住的 = 找个( _粘住的目标);
            if (粘住的 != null) return 粘住的;

            // ── ② 没粘住 → 重新挑 ──
            //
            //  ⚠️ 最短保持时间只用来限制"**主动**换目标"的频率。
            //     目标死了/跑了属于**失效**，上面那步就返回 null 了，
            //     这里必须立刻重挑 —— 否则会空等 400ms 不出手。
            var 选中的 = 挑一个();

            if (选中的 != null)
            {
                _粘住的目标 = 选中的.GameObjectId;
                _上次选择 = TimeHelper.Now();
            }

            return 选中的;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>当前粘住的目标（诊断 / 显示用）</summary>
    public static ulong 粘住的目标 => _粘住的目标;

    /// <summary>换本 / 脱战时清掉，别把上个副本的目标带过来</summary>
    public static void 重置()
    {
        _粘住的目标 = 0;
        _上次选择 = 0;
    }

    /// <summary>按 ID 找回目标，并检查它**还值不值得继续打**</summary>
    private static IBattleChara? 找个(ulong id)
    {
        if (id == 0) return null;

        try
        {
            foreach (var e in 候选())
            {
                if (e.GameObjectId != id) continue;

                // 活着 + 有仇恨 + 够得着，三个都满足才继续粘
                if (e.CurrentHp == 0) return null;
                if (!HealTargetHelper.有仇恨(e)) return null;

                var 距离 = Vector3.Distance(Core.Me.Position, e.Position);
                if (距离 > 可打距离) return null;

                return e;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// 从候选里**挑一个**。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 打分规则（按重要性排）★
    ///
    ///    ① **只考虑有仇恨的** —— 没被坦克拉住的怪打了就是 ADD，
    ///       这在日随里是会被骂的。所以这是**硬门槛**，不是扣分项。
    ///
    ///    ② **近的优先**（扣 距离×2 分）
    ///       坦克拉住的怪一定在近战位附近 —— 打它既安全又不掉输出。
    ///       而远处的怪很可能是**还没进战**的（打了就 ADD）。
    ///
    ///    ③ **快死的加分**（血量 < 25% 时最多 +60）
    ///       收掉它 = 场上少一个敌人。但权重压在"近"之下：
    ///       远处的快死怪让近战去收，我们不去追。
    ///
    ///    ④ **当前选中目标小幅加分**（+15）
    ///       尊重玩家的选择 —— 他手动选了一个，通常是有意的
    ///       （比如"就打这只"）。但只是**微调**，不足以让我们
    ///       放着一堆近处的怪不打、跑去打他选的远处怪。
    ///
    ///  ⚠️ 全部拿不到时返回 **null**，不返回一个乱挑的 ——
    ///     "不放技能"比"打错目标"好：打错会 ADD，不放只是少一点输出。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static IBattleChara? 挑一个()
    {
        try
        {
            IBattleChara? 最佳 = null;
            var 最高分 = float.MinValue;

            ulong 选中 = 0;
            try { 选中 = Core.Me.GetCurrTarget()?.GameObjectId ?? 0; } catch { }

            foreach (var e in 候选())
            {
                var 分 = 0f;

                // ① 硬门槛：没仇恨直接跳过（绝不 ADD）
                if (!HealTargetHelper.有仇恨(e)) continue;

                var 距离 = Vector3.Distance(Core.Me.Position, e.Position);
                if (距离 > 可打距离) continue;

                // ② 近的优先
                分 -= 距离 * 距离权重;

                // ③ 快死的加分（越接近死加得越多）
                var 血比 = e.MaxHp > 0 ? e.CurrentHp / (float)e.MaxHp : 1f;
                if (血比 < 补刀血线)
                    分 += 补刀权重 * (1f - 血比 / 补刀血线);

                // ④ 玩家选中的小幅倾斜
                if (选中 != 0 && e.GameObjectId == 选中) 分 += 15f;

                if (分 > 最高分)
                {
                    最高分 = 分;
                    最佳 = e;
                }
            }

            return 最佳;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>候选敌人列表（统一的取法，不在这里过滤）</summary>
    private static IEnumerable<IBattleChara> 候选()
    {
        var 列表 = new List<IBattleChara>();

        try
        {
            var 全部 = Data.AllHostileTargets;
            if (全部 == null) return 列表;

            foreach (var o in 全部)
            {
                if (o is IBattleChara b && b.CurrentHp > 0) 列表.Add(b);
            }
        }
        catch { }

        return 列表;
    }
}
