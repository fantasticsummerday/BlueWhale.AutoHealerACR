using AEAssist;
using AEAssist.CombatRoutine.Module;
using HealerACR.Common;
using HealerACR.Resolvers;

namespace HealerACR.Rotations;

/// <summary>
/// 幻术师（Conjurer, CNJ）—— 白魔法师的**前置职业**，等级上限 50。
///
/// ══════════════════════════════════════════════════════════════════
///  ⚠️ 幻术师**不是**白魔的低等级形态 —— 它是一套**独立的技能 ID**：
///
///      幻术师              白魔法师
///      119 飞石            131 愈疗
///      120 治疗            136 神速咏唱
///      121 疾风            137 再生
///      124 医治            139 神圣
///      125 复活            140 天赐祝福
///      127 坚石            3568 垒石
///      132 烈风            ...
///      133 医济
///      135 救疗
///
///  所以不能直接复用白魔的技能表 —— 得按幻术师自己的 ID 配。
///
///  好消息是**技能构成是白魔的子集**：
///      有：单体输出（飞石→坚石）、单体治疗（治疗→救疗）、
///          群体治疗（医治→医济）、DoT（疾风→烈风）、复活
///      没有：群体输出（神圣）、团队减伤（节制）、紧急单奶（天赐/神名）、
///            群体治疗能力技（法令）、个人减伤（神祝祷）
///
///  所以实现上**继承白魔表、把没有的覆写成 0** —— 这样将来白魔表改进
///  （比如新的 DoT 档位处理）会自动同步过来，不用维护两份。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class 幻术师SpellTable : WHMSpellTable
{
    public override Jobs Job => Jobs.Conjurer;
    public override string 职业名 => "幻术师";

    /// <summary>幻术师没有 AOE 输出 —— 神圣是白魔 45 级才学的</summary>
    public override uint 群体输出 => 0;

    /// <summary>幻术师没有天赐祝福和神名，紧急单奶只能靠普通治疗</summary>
    public override uint 紧急单奶 => 0;

    /// <summary>法令是白魔 56 级技能</summary>
    public override uint 群体治疗能力技 => 0;

    /// <summary>节制是白魔技能，幻术师没有团队减伤</summary>
    public override uint 团队减伤 => 0;

    /// <summary>神祝祷是白魔技能</summary>
    public override uint 个人减伤 => 0;

    /// <summary>没有神速咏唱 → 也就没有闪飒形态</summary>
    public override bool 有特殊输出形态 => false;
}

/// <summary>
/// 幻术师入口。
///
/// **架构上直接复用基类** —— 队列、resolver、事件处理全在
/// <see cref="HealerEntryBase"/> 里，幻术师只是换了一张技能表。
///
/// 这也意味着：幻术师会**自动获得**所有对 HealerACR 的改进
/// （DoT 窗口、盾判断、减伤触发、复活优先级、移动检测……），
/// 不需要单独维护。
/// </summary>
public class 幻术师RotationEntry : HealerEntryBase
{
    private readonly 幻术师SpellTable _spells = new();

    public override Jobs TargetJob => Jobs.Conjurer;
    public override string OverlayTitle => "小鲸鱼 · 幻术师（日随）";
    public override string Description =>
        "日随用幻术师 ACR（1-50 级）。\n" +
        "白魔的前置职业，共用同一套治疗/输出逻辑。\n" +
        "注意：幻术师没有神圣、节制、天赐祝福 —— 那些是白魔技能。";
    public override JobSpellTable Spells => _spells;

    protected override List<SlotResolverData> 构建决策队列_职业专属()
    {
        return new List<SlotResolverData>
        {
            new SlotResolverData(new Res_Sprint(), SlotMode.Always),
            new SlotResolverData(new Res_OffensiveAbility(_spells), SlotMode.Always),
            new SlotResolverData(new Res_PrepareResources(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_Raise(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_Esuna(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_SingleHoT(_spells), SlotMode.Gcd),

            // ── 治疗（幻术师只有 GCD 治疗，没有百合/天赐那些能力技）──
            new SlotResolverData(new Res_HealAoEGcd(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealSingleGcd(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealShield(_spells), SlotMode.Gcd),

            // ── 输出 ──
            new SlotResolverData(new Res_AoEDamage(_spells), SlotMode.Gcd),   // 幻术师没 AOE，会自己跳过
            new SlotResolverData(new Res_Dot(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_MoveGcd(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_BaseDamage(_spells), SlotMode.Gcd),

            // ── 能力技（减伤/资源）──
            new SlotResolverData(new Res_HealEmergency(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_HealAoEAbility(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_SelfMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_TeamMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_LucidDreaming(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_HealBooster(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_SingleMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_LimitBreak(), SlotMode.OffGcd),
        };
    }
}
