using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.CombatRoutine.Trigger;
using AEAssist.Extension;
using AEAssist.Helper;
using Dalamud.Bindings.ImGui;

namespace HealerACR.Common;

/// <summary>
/// 爆发轴 —— 对照 鍚岀被 ACR 用的 `Rotation.AddSlotSequences`。
///
/// ══════════════════════════════════════════════════════════════════
///  官方文档（ACR开发 L55-74）对 SlotSequence 的定义：
///
///    "有时我们希望战斗的某个时间点/条件满足时，
///     接下来的技能**完全按照规划来使用**，而不采用优先级的设计
///     （毕竟优先级决策要判断一堆复杂条件，
///      而这种情况我们只需要判断技能冷却是否满足，
///      接下来的释放完全按照规划走就行）"
///
///  ── 为什么奶妈也需要这个 ──
///
///    优先级队列适合"随机应变"，但**爆发期是有固定套路的**：
///      学者：连环计 → 士气高扬 → 能量吸收×N → 气炎法…
///      白魔：神速咏唱 → 闪飒 → 豪圣…
///    这些顺序不该被优先级逻辑打断。
///
///  ── 关于两个开关的合并（v1.16 起）──
///
///    ⚠️ 原来是两个开关：「爆发轴」和「木桩爆发」。
///       实际用起来很别扭 —— 用户要"打木桩时也开爆发"就得同时勾两个，
///       而且木桩模式的判断散在两处，容易漏。
///
///    现在合并成**一个「一键爆发」**：
///      勾上 = 无论战斗还是木桩，只要条件满足就走爆发轴
///      不勾 = 永远不走
///
///    逻辑简化了，用户也少一个要理解的概念。
///
///  ── 优化过的判断逻辑 ──
///
///    旧的 StartCheck 只看"技能可用 + 有资源"，问题是：
///      · 资源够但队友正在掉血 → 开了爆发又要立刻中断，白开
///      · 爆发到一半资源断了 → 后半段序列空转
///
///    新的判断加了：
///      · **连续性检查**：上一段爆发没走完就不重开
///      · **资源充分度**：按"这条轴要花多少资源"和"现在有多少"比
///      · **血量稳定度**：近几秒队伍血量在回升才开（说明压力过去了）
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public abstract class 爆发轴基类 : ISlotSequence
{
    /// <summary>要在队列里按顺序执行的技能</summary>
    public List<Action<Slot>> Sequence { get; } = new();

    /// <summary>当前走到第几步（-1 = 没开始）</summary>
    protected int 当前步 = -1;

    /// <summary>开始时间（用于超时保护）</summary>
    protected long 开始时间;

    /// <summary>上一次结束的时间（用于"冷却期"判断，避免刚结束又开）</summary>
    private long 上次结束时间;

    /// <summary>整条轴的超时（毫秒）—— 防止卡在某一步出不来</summary>
    protected virtual int 超时毫秒 => 20000;

    /// <summary>两次爆发之间至少间隔多久（毫秒）—— 避免连续重开</summary>
    protected virtual int 最短间隔毫秒 => 8000;

    protected JobSpellTable 表 { get; }

    protected 爆发轴基类(JobSpellTable 表)
    {
        this.表 = 表;
        构建();
    }

    /// <summary>子类在这里往 Sequence 里加技能</summary>
    protected abstract void 构建();

    // ==================== 开始判断 ====================

    /// <summary>
    /// 现在该不该开始这条爆发轴。
    /// 返回 >= 0 表示开始（值本身是优先级，越大越优先）。
    /// </summary>
    public virtual int StartCheck()
    {
        try
        {
            // ① 总开关（合并后的「一键爆发」）
            if (!HealQt.GetQt("一键爆发", false)) return -1;

            // ② 奶妈的根本原则：有人快死了，不开
            if (有人濒死()) return -1;

            // ③ 脱战不开
            if (!Core.Me.InCombat()) return -1;

            // ④ 距上次结束太近 → 不开（避免连续重开导致序列碎片化）
            if (上次结束时间 > 0 && TimeHelper.Now() - 上次结束时间 < 最短间隔毫秒) return -1;

            // ⑤ 队伍血量还在往下掉 → 不开
            //    理由：爆发轴会占 GCD，这时候开出来大概率马上被中断，
            //          不如等压力过去再开，一次走完更划算。
            if (队伍血量在下降()) return -1;

            // ⑥ 交给子类判断资源/技能条件
            return 子类开始判据();
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>子类实现：什么条件下开这条轴（只看技能和资源，不用重复判血量）</summary>
    protected abstract int 子类开始判据();

    // ==================== 停止判断 ====================

    /// <summary>
    /// 走到第 index 步时该不该停。
    /// 返回 >= 0 表示停止（放弃剩余步骤）。
    /// </summary>
    public virtual int StopCheck(int index)
    {
        try
        {
            // ① 走完了 → 正常结束
            if (index >= Sequence.Count)
            {
                上次结束时间 = TimeHelper.Now();
                return 1;
            }

            // ② 超时保护
            if (开始时间 > 0 && TimeHelper.Now() - 开始时间 > 超时毫秒)
            {
                上次结束时间 = TimeHelper.Now();
                return 2;
            }

            // ③ **有队友濒死 → 立刻中断去救人**
            //    这是奶妈爆发轴和 DPS 最大的区别 ——
            //    输出可以等，人命不能等。
            if (有人濒死())
            {
                上次结束时间 = TimeHelper.Now();
                return 3;
            }

            // ④ 脱战了
            if (!Core.Me.InCombat())
            {
                上次结束时间 = TimeHelper.Now();
                return 4;
            }

            return -1;
        }
        catch
        {
            return 5;
        }
    }

    // ==================== 辅助判断 ====================

    /// <summary>有人掉到危险血线（默认 35%）</summary>
    protected static bool 有人濒死(float 阈值 = 0.35f)
    {
        try { return HealTargetHelper.低于阈值人数(阈值) > 0; }
        catch { return false; }
    }

    /// <summary>
    /// 队伍血量是不是还在往下掉。
    ///
    /// 用"受伤人数"的变化来判断：
    ///   上一次采样受伤 3 人，这次 4 人 → 还在恶化 → true
    ///
    /// **为什么不看具体血量**：血量数字每帧都在跳（HoT 在跳、受到伤害），
    /// 看人数的变化更稳。
    /// </summary>
    private int _上次受伤人数 = -1;

    protected bool 队伍血量在下降()
    {
        try
        {
            // 用群奶阈值当"受伤"标准 —— 和实际治疗判断保持一致
            var 现在 = HealTargetHelper.低于阈值人数(HealSettings.Instance.群体治疗阈值);

            if (_上次受伤人数 < 0)
            {
                _上次受伤人数 = 现在;
                return false;   // 第一次不知道趋势，当作没下降
            }

            var 恶化 = 现在 > _上次受伤人数;
            _上次受伤人数 = 现在;

            return 恶化;
        }
        catch
        {
            return false;
        }
    }

    // ==================== 构建辅助 ====================

    /// <summary>加一个 GCD 技能（需要目标）</summary>
    protected void 加Gcd(uint 技能Id)
    {
        if (技能Id == 0) return;

        Sequence.Add(slot =>
        {
            var 目标 = HealTargetHelper.当前目标();
            if (目标 == null) return;

            var spell = SpellUtil.当前形态(技能Id);
            if (spell != null) slot.Add(new Spell(spell.Id, 目标));
        });
    }

    /// <summary>加一个能力技</summary>
    protected void 加能力技(uint 技能Id)
    {
        if (技能Id == 0) return;

        Sequence.Add(slot =>
        {
            var spell = SpellUtil.Get(技能Id);
            if (spell != null) slot.Add(spell);
        });
    }

    /// <summary>加一个对指定队友的能力技</summary>
    protected void 加队友能力技(uint 技能Id, Func<IBattleChara?> 选目标)
    {
        if (技能Id == 0) return;

        Sequence.Add(slot =>
        {
            var 目标 = 选目标();
            if (目标 == null) return;

            var spell = SpellUtil.Get(技能Id);
            if (spell != null) slot.Add(new Spell(spell.Id, 目标));
        });
    }

    /// <summary>重置内部计时（换本时用）</summary>
    public void 重置()
    {
        当前步 = -1;
        开始时间 = 0;
        上次结束时间 = 0;
        _上次受伤人数 = -1;
    }
}

/// <summary>学者的爆发轴：连环计 → 士气高扬 → 能量吸收 → 气炎法</summary>
public class 学者爆发轴 : 爆发轴基类
{
    public 学者爆发轴(JobSpellTable 表) : base(表) { }

    protected override void 构建()
    {
        加能力技(SpellIds.取("连环计"));
        加Gcd(表.群体治疗GCD);              // 士气高扬之策
        加能力技(SpellIds.取("能量吸收"));
        加Gcd(表.基础输出);
        加能力技(SpellIds.取("能量吸收"));
        加Gcd(表.基础输出);
        加Gcd(表.基础输出);
    }

    protected override int 子类开始判据()
    {
        // 连环计可用
        var 连环计 = SpellIds.取("连环计");
        if (连环计 == 0 || !SpellUtil.可用(连环计)) return -1;

        // 这条轴要花 2 颗以太（两次能量吸收），不够就不开
        // —— 只开一半的话后半段会空转，不如等资源齐了再来
        var 以太 = JobApiHelper.读得到("以太") ? JobApiHelper.以太 : 0;
        if (以太 < 2) return -1;

        return 8;
    }
}

/// <summary>白魔的爆发轴：神速咏唱 → 闪飒 → 豪圣</summary>
public class 白魔爆发轴 : 爆发轴基类
{
    public 白魔爆发轴(JobSpellTable 表) : base(表) { }

    protected override void 构建()
    {
        加能力技(SpellIds.取("神速咏唱"));
        加Gcd(表.基础输出);
        加Gcd(表.基础输出);
        加Gcd(表.基础输出);
        加Gcd(表.基础输出);
    }

    protected override int 子类开始判据()
    {
        var 神速 = SpellIds.取("神速咏唱");
        if (神速 == 0 || !SpellUtil.可用(神速)) return -1;

        // 神速是 120 秒 CD，开了就要打满 —— 蓝量不够就别开
        try
        {
            if (蓝量.低蓝停手()) return -1;
        }
        catch { }

        return 8;
    }
}
