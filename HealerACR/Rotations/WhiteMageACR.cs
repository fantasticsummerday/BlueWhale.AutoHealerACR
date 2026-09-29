using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;
using HealerACR.Resolvers;
using HealerACR.Timeline;

namespace HealerACR.Rotations;

/// <summary>
/// 白魔法师技能表。技能 ID 走 SpellIds 内置表（中文名，已用 Lumina dump 核对）。
/// </summary>
public class WHMSpellTable : JobSpellTable
{
    public override Jobs Job => Jobs.WhiteMage;
    public override string 职业名 => "白魔法师";

    // 输出：写最初形态，运行时 CheckActionChange 自动升到当前等级那个
    // 直接列等级链，比依赖 CheckActionChange 可靠（它不保证一路递归到最高级）
    public override uint 基础输出 => SpellUtil.取已解锁(
        SpellIds.取("闪灼"),    // 82 级 Glare III
        SpellIds.取("闪耀"),    // 72 级 Glare
        SpellIds.取("崩石"),    // 64
        SpellIds.取("垒石"),    // 54
        SpellIds.取("坚石"),    // 18
        SpellIds.取("飞石"));   // 1
    public override uint 群体输出 => SpellUtil.取已解锁(SpellIds.取("豪圣"), SpellIds.取("神圣"));
    public override uint Dot技能 => SpellUtil.取已解锁(
        SpellIds.取("天辉"), SpellIds.取("烈风"), SpellIds.取("疾风"));
    public override uint DotBuff => AuraIds.白魔Dot;
    // 把所有档位都列上：天辉有两个 id（1871 / 2035），漏一个就可能无限补 DoT
    public override uint[] 所有DotBuff => new[]
    {
        AuraIds.白魔Dot, AuraIds.白魔DotAlt, AuraIds.白魔Dot2, AuraIds.白魔Dot1
    };

    public override uint 单体治疗GCD => SpellUtil.取已解锁(SpellIds.取("救疗"), SpellIds.取("治疗"));
    public override uint 群体治疗GCD => SpellUtil.取已解锁(SpellIds.取("医养"), SpellIds.取("医济"), SpellIds.取("医治"));
    public override uint 紧急单奶 => SpellUtil.取已解锁(SpellIds.取("天赐祝福"), SpellIds.取("神名"));
    public override uint 群体治疗能力技 => SpellIds.取("法令");
    public override bool 群体治疗能力技是输出型 => true;   // 法令要卡 CD
    public override uint 团队减伤 => SpellIds.取("节制");

    /// <summary>神圣/豪圣是 8 米范围（比其他奶妈的 AOE 大）</summary>
    public override int AOE伤害范围 => 8;
    public override uint 个人减伤 => SpellIds.取("神祝祷");

    /// <summary>神速咏唱期间基础输出会被替换成闪飒，这时候不能再硬放闪灼</summary>
    public override bool 有特殊输出形态 =>
        (AuraIds.闪飒预备 != 0 && Core.Me.HasAura(AuraIds.闪飒预备))
        || (AuraIds.闪飒预备2 != 0 && Core.Me.HasAura(AuraIds.闪飒预备2));

    public override uint 复活 => SpellIds.取("复活");
    public override uint 驱散 => SpellsDefine.Esuna;
    public override uint 醒梦 => SpellsDefine.LucidDreaming;

    // 白魔的百合是被动攒的，脱战不用主动做什么
}

public class WHMRotationEntry : HealerEntryBase
{
    private readonly WHMSpellTable _spells = new();

    public override Jobs TargetJob => Jobs.WhiteMage;
    public override string OverlayTitle => "小鲸鱼 · 白魔（日随）";
    public override string Description =>
        "日随用白魔 ACR。治疗优先，输出顺手；1-100 级通用。\n" +
        "百合技 / 节制减伤 / cactbot 时间轴 / 木桩最优输出 都接好了。";
    public override JobSpellTable Spells => _spells;

    protected override List<SlotResolverData> 构建决策队列()
    {
        return new List<SlotResolverData>
        {
            new SlotResolverData(new Res_Sprint(), SlotMode.Always),              // 脱战自动疾跑
            new SlotResolverData(new Res_OffensiveAbility(_spells), SlotMode.Always),   // 按错题集：能力技进 Always，不受 GCD 就绪的影响
            new SlotResolverData(new Res_PrepareResources(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_Raise(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_Esuna(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_SingleHoT(_spells), SlotMode.Gcd),   // 再生
            new SlotResolverData(new WHM_AfflatusRapture(_spells), SlotMode.Gcd),
            new SlotResolverData(new WHM_AfflatusSolace(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealAoEGcd(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealSingleGcd(_spells), SlotMode.Gcd),
            // 输出顺序：AOE（敌人>=3 才触发）→ DoT → 职业输出 → 兜底
            // DoT 必须排在职业输出前面：起手先让它开始跳伤害，
            // 已上之后它自己的 Check 会跳过，不会重复占 GCD
            new SlotResolverData(new Res_AoEDamage(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_Dot(_spells), SlotMode.Gcd),
            new SlotResolverData(new WHM_AfflatusMisery(_spells), SlotMode.Gcd),
            new SlotResolverData(new WHM_GlareIV(), SlotMode.Gcd),   // 神速期间打闪飒
            new SlotResolverData(new Res_MoveGcd(_spells), SlotMode.Gcd),      // 移动填充
            new SlotResolverData(new Res_BaseDamage(_spells), SlotMode.Gcd),

            new SlotResolverData(new Res_HealEmergency(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_HealAoEAbility(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_SelfMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_TeamMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_LucidDreaming(_spells), SlotMode.OffGcd),
            new SlotResolverData(new WHM_PresenceOfMind(), SlotMode.OffGcd),
            new SlotResolverData(new Res_FreeCast(_spells), SlotMode.OffGcd),        // 无中生有
            new SlotResolverData(new Res_HealBooster(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_GroupHoT(_spells), SlotMode.OffGcd),        // 庇护所
            new SlotResolverData(new Res_PlacedHeal(_spells), SlotMode.OffGcd),      // 礼仪之铃
            new SlotResolverData(new Res_BigAoEHeal(_spells), SlotMode.OffGcd),      // 全大赦
            new SlotResolverData(new Res_SingleMitigation(_spells), SlotMode.OffGcd), // 水流幕
            new SlotResolverData(new Res_LimitBreak(), SlotMode.OffGcd),                  // 极限技
        };
    }

    protected override void 构建QT()
    {
        base.构建QT();
        加职业开关("神速魔", true);
    }

    /// <summary>爆发轴（勾上「一键爆发」才生效）</summary>
    protected override AEAssist.CombatRoutine.Module.ISlotSequence[] 构建爆发轴()
        => new AEAssist.CombatRoutine.Module.ISlotSequence[] { new 白魔爆发轴(Spells) };
}

// ============================================================================
//  白魔专属
// ============================================================================

/// <summary>安慰之心：百合单体治疗，免蓝。</summary>
public class WHM_AfflatusSolace : ISlotResolver
{
    private readonly JobSpellTable _t;

    public WHM_AfflatusSolace(JobSpellTable table) => _t = table;

    private static uint 技能 => SpellIds.取("安慰之心");

    /// <summary>
    /// 选目标。两种场景：
    ///   1. 百合满 3 颗（溢出）→ 花掉一颗，哪怕全队满血 —— 因为不花就没有血百合，
    ///      血百合满 3 才能打苦难之心，攒着纯亏
    ///   2. 正常情况 → 只在血线更低时才动（百合要攒着用在刀刃上）
    /// </summary>
    private static IBattleChara? 选目标()
    {
        if (JobApiHelper.百合 >= 3)
        {
            // 兜底到自己：打木桩是单人环境，可能一个"队友"都没有（包括自己没被算进去），
            // 那样就永远找不到目标，百合也就永远卸不掉
            return HealTargetHelper.最危险队友()
                   ?? HealTargetHelper.血量最低的坦克()
                   ?? AEAssist.Core.Me;
        }

        var 血线 = Math.Min(HealSettings.Instance.单体治疗阈值, HealSettings.Instance.百合使用血线);
        return HealTargetHelper.最低血量队友(血线) ?? AEAssist.Core.Me;
    }

    public int Check()
    {
        if (!SpellUtil.已解锁(技能)) return -2;
        if (JobApiHelper.百合 < 1) return -3;

        // 百合满 3 颗 = 资源快溢出了。这时候**无视一切治疗开关**（木桩模式、奶人、单奶），
        // 因为花百合是为了攒血百合打苦难之心，属于资源循环，不是纯治疗。
        var 溢出 = JobApiHelper.百合 >= 3;
        if (!溢出)
        {
            if (HealTargetHelper.木桩模式) return -300;
            if (!HealQt.GetQt("奶人")) return -100;
            if (!HealQt.GetQt("单奶")) return -101;
        }

        var target = 选目标();
        if (target == null) return -1;

        // 溢出时优先级更高一点，尽早把百合花出去
        var 优先级 = JobApiHelper.百合 >= 3 ? 15 : 14;
        return SpellUtil.可用(技能) ? 优先级 : -1;
    }

    public void Build(Slot slot)
    {
        var target = 选目标();
        if (target == null) return;
        slot.Add(new Spell(技能, target));
    }
}

/// <summary>狂喜之心：百合群体治疗，免蓝。</summary>
public class WHM_AfflatusRapture : ISlotResolver
{
    private readonly JobSpellTable _t;

    public WHM_AfflatusRapture(JobSpellTable table) => _t = table;

    private static uint 技能 => SpellIds.取("狂喜之心");

    public int Check()
    {
        if (!SpellUtil.已解锁(技能)) return -2;
        if (JobApiHelper.百合 < 1) return -3;

        // 同安慰之心：溢出时无视治疗开关
        var 溢出 = JobApiHelper.百合 >= 3;
        if (!溢出)
        {
            if (HealTargetHelper.木桩模式) return -300;
            if (!HealQt.GetQt("奶人")) return -100;
            if (!HealQt.GetQt("群奶")) return -101;
        }

        var s = HealSettings.Instance;
        var 需要群奶 = HealTargetHelper.低于阈值人数(s.群体治疗阈值) >= s.群奶最少人数;

        // 百合满 3 颗 + 有两个以上人不在满血 → 顺手用群奶花掉（覆盖更划算）
        var 溢出可用 = JobApiHelper.百合 >= 3
                       && HealTargetHelper.低于阈值人数(0.95f) >= 2;

        if (!需要群奶 && !溢出可用) return -1;

        return SpellUtil.可用(技能) ? 16 : -1;
    }

    public void Build(Slot slot)
    {
        slot.Add(new Spell(技能, SpellTargetType.Self));
    }
}

/// <summary>苦难之心：血百合满 3 颗的免费大伤害。木桩模式不省。</summary>
public class WHM_AfflatusMisery : ISlotResolver
{
    private readonly JobSpellTable _t;

    public WHM_AfflatusMisery(JobSpellTable table) => _t = table;

    private static uint 技能 => SpellIds.取("苦难之心");

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;
        if (!SpellUtil.已解锁(技能)) return -2;
        if (!HealSettings.Instance.用苦难之心) return -6;
        if (JobApiHelper.血百合 < 3) return -3;
        if (HealTargetHelper.当前目标() == null) return -1;

        // 需求 2 + 5：残血小怪不交，但木桩模式不省
        if (!HealTargetHelper.木桩模式)
        {
            if (HealTargetHelper.目标快死了()) return -4;
            if (HealSettings.Instance.时间轴攒资源 && TimelineManager.未来有减伤(8.0)) return -5;
        }

        return SpellUtil.可用(技能) ? 7 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>神速咏唱：输出窗口。木桩模式卡 CD 开。</summary>
public class WHM_PresenceOfMind : ISlotResolver
{
    private static uint 技能 => SpellIds.取("神速咏唱");

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;
        if (!HealQt.GetQt("神速魔", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;
        // 神速咏唱是**自身 buff**，不需要选中目标（跟法令/王冠领主同一个教训）
        if (!CharacterExt.可以插能力技()) return -6;
        if (AuraIds.神速魔 != 0 && Core.Me.HasAura(AuraIds.神速魔)) return -7;

        // 需求 2：残血小怪不给爆发（木桩模式不判断）
        if (!HealTargetHelper.木桩模式 && HealTargetHelper.目标快死了()) return -4;

        return SpellUtil.可用(技能) ? 2 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}


/// <summary>
/// 闪飒（Glare IV）。
///
/// 这是"神速咏唱期间"的强化闪耀 —— 游戏里只有在神速 buff 下才可用，
/// 所以不需要额外判断 buff，<c>SpellUtil.可用</c> 自己就会在非神速期间返回 false。
/// 优先级放在普通输出之前，神速一转好就能打上。
/// </summary>
public class WHM_GlareIV : ISlotResolver
{
    private static uint 技能 => SpellIds.取("闪飒");

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;
        if (!SpellUtil.已解锁(技能)) return -2;
        if (HealTargetHelper.当前目标() == null) return -1;

        // 精确判断：闪飒只在"闪飒预备"buff 下可用。
        // 不能只靠 SpellUtil.可用() —— 万一它不检查 buff 条件，
        // 非神速期间就会一直尝试放一个放不出来的技能，白占 GCD。
        // 两个 id 都查，防御版本差异。
        var 有预备 = (AuraIds.闪飒预备 != 0 && Core.Me.HasAura(AuraIds.闪飒预备))
                     || (AuraIds.闪飒预备2 != 0 && Core.Me.HasAura(AuraIds.闪飒预备2));
        if (!有预备) return -1;

        if (!SpellUtil.可用(技能)) return -1;

        return 8;   // 高于普通输出（普通输出是 1）
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}
