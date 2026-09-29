using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;
using HealerACR.Resolvers;
using HealerACR.Timeline;

namespace HealerACR.Rotations;

/// <summary>贤者技能表。技能 ID 走 SpellIds 内置表（中文名）。</summary>
public class SGESpellTable : JobSpellTable
{
    public override Jobs Job => Jobs.Sage;
    public override string 职业名 => "贤者";

    public override uint 基础输出 => SpellUtil.取已解锁(
        SpellIds.取("注药III"), SpellIds.取("注药II"), SpellIds.取("注药"));
    public override uint 群体输出 => SpellUtil.取已解锁(
        SpellIds.取("失衡II"), SpellIds.取("失衡"));
    // 写"注药III/II/注药"的等级链：均衡注药是它们的形态，
    // 只写 1 级的"注药"的话，高等级取不到"均衡注药III"
    public override uint Dot技能 => SpellUtil.取已解锁(
        SpellIds.取("注药III"), SpellIds.取("注药II"), SpellIds.取("注药"));
    public override uint DotBuff => AuraIds.贤者Dot;
    // 均衡注药有三档 buff（2614/2615/2616，另有 2864 同名的另一档），
    // 只查一档的话满级会一直认为"没上 DoT"→ 无限补
    public override uint[] 所有DotBuff => new[]
    {
        AuraIds.贤者Dot, AuraIds.贤者DotAlt, AuraIds.贤者DotAlt2, AuraIds.贤者DotAlt3,
        AuraIds.贤者Dot2, AuraIds.贤者Dot1
    };

    public override uint 单体治疗GCD => SpellIds.取("诊断");
    public override uint 群体治疗GCD => SpellIds.取("预后");
    public override uint 紧急单奶 => SpellUtil.取已解锁(
        SpellIds.取("输血"), SpellIds.取("白牛清汁"), SpellIds.取("灵橡清汁"));

    /// <summary>
    /// 白牛清汁 / 灵橡清汁：瞬发单体治疗（蛇胆资源，不读条）。
    ///
    /// ⚠️ 参考实现的贤者能力技顺序（IL 直证）：
    ///      输血 → 白牛清汁 → 灵橡清汁
    ///    而且有"最近用过就不重复"的时间窗（2000ms）。
    /// </summary>
    public override uint 预铺单奶能力技 => SpellUtil.取已解锁(SpellIds.取("白牛清汁"));

    /// <summary>输血 / 白牛：不读条，移动中也能用。</summary>
    public override uint 瞬发单奶能力技 => SpellUtil.取已解锁(
        SpellIds.取("输血"), SpellIds.取("白牛清汁"), SpellIds.取("灵橡清汁"));
    public override uint 群体治疗能力技 => SpellIds.取("消化");

    // ⚠️ 写"诊断/预后"而不是"均衡诊断/均衡预后"：
    //    后者是前者在均衡状态下的 action change 形态，硬放会被游戏拒绝。
    //    配合 SpellUtil.当前形态() 使用，游戏自己会变成均衡版。
    public override uint 单体盾 => SpellIds.取("诊断");
    public override uint 群体盾 => SpellIds.取("预后");
    public override uint 护盾前置 => SpellIds.取("均衡");
    public override uint 团队减伤 => SpellIds.取("坚角清汁");

    /// <summary>
    /// 均衡状态下"注药"会被游戏替换成"均衡注药"。
    /// 这时候基础输出必须让路 —— 否则 DoT 已经上过之后，
    /// Res_BaseDamage 还会再放一次均衡注药，白白覆盖掉 DoT。
    /// 交给 SGE_Dot 统一处理。
    /// </summary>
    public override bool 有特殊输出形态 => JobApiHelper.均衡中;

    public override uint 复活 => SpellIds.取("复苏");
    public override uint 驱散 => SpellsDefine.Esuna;
    public override uint 醒梦 => SpellsDefine.LucidDreaming;

    // 需求 3：蛇胆就是贤者的"群奶能力技资源"，没蛇胆就退回 GCD 群奶
    // ★ 保留数按队伍规模调整：四人本（单奶）留更多，八人本有搭档可以少留 ★
    public override bool 治疗资源充足 => JobApiHelper.蛇胆 >=
        HealTargetHelper.资源保留调整(HealSettings.Instance.蛇胆保留数);

    // 需求 4：脱战补蛇胆
    public override uint[] 脱战准备技能 => new[] { SpellIds.取("根素") };
}

public class SGERotationEntry : HealerEntryBase
{
    private readonly SGESpellTable _spells = new();

    public override Jobs TargetJob => Jobs.Sage;
    public override string OverlayTitle => "小鲸鱼 · 贤者（日随）";

    public override string Description =>
        "日随用贤者 ACR。自动心关、均衡 DoT、蛇胆优先、毒刺不浪费。\n" +
        "贤者 70 级转职，实际可用 70-100；脱战自动补蛇胆。";
    public override JobSpellTable Spells => _spells;

    protected override List<SlotResolverData> 构建决策队列_职业专属()
    {
        return new List<SlotResolverData>
        {
            new SlotResolverData(new Res_Sprint(), SlotMode.Always),              // 脱战自动疾跑
            new SlotResolverData(new Res_OffensiveAbility(_spells), SlotMode.Always),   // 按错题集：能力技进 Always，不受 GCD 就绪的影响
            new SlotResolverData(new Res_PrepareResources(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_Raise(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_Esuna(_spells), SlotMode.Gcd),
            // ★ 「必须奶满」机制 —— 插在最前面，理由是行号才是优先级。
            //   这类 debuff 是“奶到 100% 才解除”，晚一个 GCD 人就没了；
            //   而 Check() 的返回值**不参与仲裁**（反汇编已证），只有行号算数。
            //   原来它排在所有治疗 GCD 之后 —— 场上有人被再生/医济抢走 GCD 时，
            //   就永远轮不到它。详见 Res_MustFullHeal 的类注释。
            new SlotResolverData(new Res_MustFullHeal(_spells), SlotMode.Gcd),
            // ★ 预铺 / 瞬发治疗能力技 —— **必须在所有治疗 GCD 之前**。
            //   用户实测报过“治疗应该优先能力技 / 不读条的技能”，
            //   而 Check() 的返回值**不参与仲裁** —— 排在哪一行才算数。
            //   参考实现的 resolver 列表也是能力技在前（IL 直证）。
            //   详见 Res_InstantHealAbility 的类注释。
            new SlotResolverData(new Res_InstantHealAbility(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_GroupShield(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealAoEGcd(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealSingleGcd(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealShield(_spells), SlotMode.Gcd),
            // AOE 优先：3 个以上敌人时不该先给单只怪挂 DoT
            new SlotResolverData(new Res_AoEDamage(_spells), SlotMode.Gcd),
            new SlotResolverData(new SGE_Dot(_spells), SlotMode.Gcd),
            new SlotResolverData(new SGE_Phlegma(), SlotMode.Gcd),   // 发炎：DoT 之后才轮到它
            new SlotResolverData(new SGE_Toxikon(), SlotMode.Gcd),
            new SlotResolverData(new Res_MoveGcd(_spells), SlotMode.Gcd),      // 移动填充
            new SlotResolverData(new Res_BaseDamage(_spells), SlotMode.Gcd),

            new SlotResolverData(new Res_HealEmergency(_spells), SlotMode.OffGcd),
            new SlotResolverData(new SGE_Philosophia(), SlotMode.OffGcd),            // 智慧之爱（Lv100 群疗大招）
            new SlotResolverData(new Res_HealAoEAbility(_spells), SlotMode.OffGcd),
            new SlotResolverData(new SGE_Kardia(_spells), SlotMode.OffGcd),
            new SlotResolverData(new SGE_CholeOverflow(), SlotMode.OffGcd),
            new SlotResolverData(new SGE_Rhizomata(), SlotMode.OffGcd),
            new SlotResolverData(new Res_SelfMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_TeamMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_LucidDreaming(_spells), SlotMode.OffGcd),
            new SlotResolverData(new SGE_Psyche(), SlotMode.OffGcd),
            new SlotResolverData(new Res_HealBooster(_spells), SlotMode.OffGcd),          // 活化
            new SlotResolverData(new Res_HealAmp(_spells), SlotMode.OffGcd),              // 混合
            new SlotResolverData(new Res_KardiaBoost(_spells), SlotMode.OffGcd),          // 拯救
            new SlotResolverData(new Res_Emergency(_spells), SlotMode.OffGcd),            // 寄生清汁
            new SlotResolverData(new Res_GroupHoT(_spells), SlotMode.OffGcd),             // 自生
            new SlotResolverData(new Res_GroupShieldAbility(_spells), SlotMode.OffGcd),   // 泛输血
            new SlotResolverData(new Res_BigAoEHeal(_spells), SlotMode.OffGcd),           // 整体论/魂灵风息
            new SlotResolverData(new Res_LimitBreak(), SlotMode.OffGcd),                  // 极限技
        };
    }

    protected override void 构建QT()
    {
        base.构建QT();
        加职业开关("自动心关", true);
        加职业开关("根素", true);
        加职业开关("箭毒", true);
        加职业开关("发炎", true);
    }
}

// ============================================================================
//  贤者专属
// ============================================================================

/// <summary>均衡注药（DoT）：没开均衡就先开均衡。</summary>
public class SGE_Dot : ISlotResolver
{
    private readonly JobSpellTable _t;

    public SGE_Dot(JobSpellTable table) => _t = table;

    private static uint 均衡 => SpellIds.取("均衡");
    private static uint 技能 => SpellIds.取("均衡注药");

    public int Check()
    {
        if (!HealQt.GetQt("DOT", true)) return -100;
        if (!HealQt.GetQt("输出", true)) return -101;
        if (蓝量.低蓝停手()) return -9;   // 蓝留给治疗
        if (!SpellUtil.已解锁(均衡)) return -2;

        var target = HealTargetHelper.当前目标();
        if (target == null) return -1;
        if (!HealTargetHelper.木桩模式 && target.血量比例() <= HealSettings.Instance.不挂Dot血线) return -3;

        // ⚠️ 判断走**共享**的 Dot补判（含防死循环保险丝）——
        //    原来这里是自己写的一套（自留 `上次DoT` 字段），
        //    和 `Res_Dot` 的逻辑各写一份 → 改一处漏一处。
        //    而且 AI 建议那条路也走 Dot补判，三边必须看同一份数据
        //    （开发约定 F③ 的推广）。
        if (!Dot补判.该补(target, _t.所有DotBuff, HealSettings.Instance.Dot持续时间 - 3f))
            return -4;

        return SpellUtil.可用(均衡) ? 6 : -1;
    }

    public void Build(Slot slot)
    {
        var target = HealTargetHelper.当前目标();
        if (target == null) return;

        // 不在均衡中：这一轮只开均衡，等服务器确认了下一轮再放 DoT。
        // 同一帧里按完均衡就立刻放"均衡注药"是放不出来的。
        if (!JobApiHelper.均衡中)
        {
            var ek = SpellUtil.Get(均衡);
            if (ek != null) slot.Add(ek);
            return;
        }

        var 当前 = SpellUtil.当前形态(技能);
        if (当前 == null) return;

        slot.Add(new Spell(当前.Id, target));

        // ⚠️ 记到**共享**的 Dot补判（不是本类的静态字段）——
        //    保险丝必须和 Res_Dot / AI 建议路径共用，否则那条路能绕过它。
        Dot补判.记一次施放();
    }
}

/// <summary>箭毒：毒刺快溢出或打 AOE 时泄掉。</summary>
public class SGE_Toxikon : ISlotResolver
{
    // 箭毒 66 级、箭毒II 82 级，用等级链取当前该用的那个
    private static uint 技能 => SpellUtil.取已解锁(SpellIds.取("箭毒II"), SpellIds.取("箭毒"));

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;
        if (!HealQt.GetQt("箭毒", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        var 毒刺 = JobApiHelper.毒刺;
        if (毒刺 <= 0) return -3;
        if (毒刺 < HealSettings.Instance.箭毒泄刺阈值 && HealTargetHelper.周围敌人数量() < 2) return -4;

        if (!HealTargetHelper.木桩模式)
        {
            if (HealSettings.Instance.时间轴攒资源 && TimelineManager.未来有减伤(8.0)) return -5;
        }

        return SpellUtil.可用(技能) ? 4 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>心关：给坦克挂上，之后打输出顺带回血。</summary>
public class SGE_Kardia : ISlotResolver
{
    private readonly JobSpellTable _t;

    public SGE_Kardia(JobSpellTable table) => _t = table;

    private static uint 技能 => SpellIds.取("心关");

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("自动心关", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        var tank = HealTargetHelper.血量最低的坦克(1f);
        if (tank == null) return -1;
        if (AuraIds.有心关(tank)) return -3;

        return SpellUtil.可用(技能) ? 3 : -1;
    }

    public void Build(Slot slot)
    {
        var tank = HealTargetHelper.血量最低的坦克(1f);
        if (tank == null) return;
        slot.Add(new Spell(技能, tank));
    }
}

/// <summary>根素：补蛇胆，有豆子就不放。</summary>
public class SGE_Rhizomata : ISlotResolver
{
    private static uint 技能 => SpellIds.取("根素");

    public int Check()
    {
        // 木桩也要补蛇胆 —— 补了才有得花，属于资源循环
        if (!HealQt.GetQt("根素", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;
        if (JobApiHelper.蛇胆 >= 1) return -3;

        // ★ 低蓝时不补蛇胆（和学者的补豆同一个道理）★
        //   补资源是为了花出去换治疗/输出，低蓝时该省蓝。
        if (蓝量.低蓝停手()) return -4;

        if (!CharacterExt.可以插能力技()) return -6;

        return SpellUtil.可用(技能) ? 2 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}
