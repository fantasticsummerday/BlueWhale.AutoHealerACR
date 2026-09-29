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
///
///      学者：连环计 → 士气高扬 → 能量吸收×N → 气炎法…
///      占星：占卜 → 光速 → 发卡 → 重力…
///      贤者：发炎×N → 失衡…
///      白魔：神速咏唱 → 闪飒×2 → 豪圣…
///
///    这些顺序**不该被优先级逻辑打断** ——
///    比如连环计刚放完，优先级队列可能因为"有人掉血了"就转头去治疗，
///    结果爆发期什么都没打出来。
///
///  ── 设计原则 ──
///
///    · **爆发轴只负责输出** —— 一旦队友濒死，立刻中断去救人
///      （StopCheck 里判血量，这是奶妈和 DPS 的根本区别）
///    · 每个职业一条，逻辑独立
///    · **默认关闭**（爆发轴会占用 GCD，日随里不一定划算）
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

    /// <summary>整条轴的超时（毫秒）—— 防止卡在某一步出不来</summary>
    protected virtual int 超时毫秒 => 20000;

    protected JobSpellTable 表 { get; }

    protected 爆发轴基类(JobSpellTable 表)
    {
        this.表 = 表;
        构建();
    }

    /// <summary>子类在这里往 Sequence 里加技能</summary>
    protected abstract void 构建();

    /// <summary>
    /// 现在该不该开始这条爆发轴。
    /// 返回 >= 0 表示开始（值本身是优先级，越大越优先）。
    /// </summary>
    public virtual int StartCheck()
    {
        try
        {
            if (!HealQt.GetQt("爆发轴", false)) return -1;     // 默认关
            if (HealTargetHelper.木桩模式 && !HealQt.GetQt("木桩爆发", false)) return -1;

            // ── 奶妈的根本原则：有人快死了，立刻中断 ──
            if (有人濒死()) return -1;

            return 子类开始判据();
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>子类实现：什么条件下开这条轴</summary>
    protected abstract int 子类开始判据();

    /// <summary>
    /// 走到第 index 步时该不该停。
    /// 返回 >= 0 表示停止（放弃剩余步骤）。
    /// </summary>
    public virtual int StopCheck(int index)
    {
        try
        {
            // ① 走完了
            if (index >= Sequence.Count) return 1;

            // ② 超时保护
            if (开始时间 > 0 && TimeHelper.Now() - 开始时间 > 超时毫秒) return 2;

            // ③ **有队友濒死 → 立刻中断去救人**
            //    这是奶妈爆发轴和 DPS 最大的区别 ——
            //    输出可以等，人命不能等。
            if (有人濒死()) return 3;

            // ④ 脱战了
            if (!Core.Me.InCombat()) return 4;

            return -1;
        }
        catch
        {
            return 5;
        }
    }

    /// <summary>有人掉到危险血线（默认 35%）</summary>
    protected static bool 有人濒死(float 阈值 = 0.35f)
    {
        try
        {
            return HealTargetHelper.低于阈值人数(阈值) > 0;
        }
        catch
        {
            return false;
        }
    }

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
}

/// <summary>学者的爆发轴：连环计 → 士气高扬 → 能量吸收 → 气炎法</summary>
public class 学者爆发轴 : 爆发轴基类
{
    public 学者爆发轴(JobSpellTable 表) : base(表)
    {
    }

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
        // 连环计可用 + 有豆子 → 开爆发
        var 连环计 = SpellIds.取("连环计");
        if (连环计 == 0) return -1;
        if (!SpellUtil.可用(连环计)) return -1;

        var 以太 = JobApiHelper.读得到("以太") ? JobApiHelper.以太 : 0;
        if (以太 <= 0) return -1;

        return 8;
    }
}

/// <summary>白魔的爆发轴：神速咏唱 → 闪飒 → 豪圣</summary>
public class 白魔爆发轴 : 爆发轴基类
{
    public 白魔爆发轴(JobSpellTable 表) : base(表)
    {
    }

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
        if (神速 == 0) return -1;
        if (!SpellUtil.可用(神速)) return -1;

        return 8;
    }
}
