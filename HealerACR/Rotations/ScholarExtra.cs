using AEAssist.CombatRoutine;
using AEAssist.Helper;
using HealerACR.Common;
using HealerACR.Timeline;

namespace HealerACR.Rotations;

// ============================================================================
//  学者 90-100 级的三个技能（0.3.8 补）。
//
//  参考同类 ACR 的类型表发现的缺口：
//    Scholar_Baneful_Impaction -> 埋伏之毒（92）
//    Scholar_Seraphism         -> 炽天附体（100）
//    96 级那个 同类 ACR 没单独建类，但它是独立的群盾强化
// ============================================================================

/// <summary>
/// 炽天附体（Seraphism，学者 100 级，180 秒 CD）。
/// 群体治疗强化窗口 —— 按"大群奶"来用：掉血人多或大伤害要来时开。
/// </summary>
public class SCH_Seraphism : ISlotResolver
{
    private static uint 技能 => SpellIds.取("炽天附体");

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 已经开着就不重复
        if (Core.Me.有该技能的Buff(技能)) return -3;

        var s = HealSettings.Instance;
        var 掉血多 = HealTargetHelper.低于阈值人数(s.群体治疗阈值) >= s.群奶最少人数;
        var 要来了 = TimelineManager.未来有减伤(3.0) || 减伤Helper.即将来大伤害();

        if (!掉血多 && !要来了) return -1;

        return SpellUtil.可用(技能) ? 18 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>
/// 意气轩昂之策（学者 96 级）。群体护盾/治疗强化，
/// 按"预铺"用：时间轴预报到伤害或 boss 正在读条时铺。
/// </summary>
public class SCH_AdloquiumUpgrade : ISlotResolver
{
    private static uint 技能 => SpellIds.取("意气轩昂之策");

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!SpellUtil.已解锁(技能)) return -2;

        if (!TimelineManager.未来有减伤(4.0) && !减伤Helper.即将来大伤害()) return -1;

        return SpellUtil.可用(技能) ? 15 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>
/// 强制以太超流 —— 参考实现的 Scholar_ForceAetherflow。
///
/// 它的 Check 只有两个条件：Qt.GetQt("强制以太") + IsReadyWithCanCast。
/// 也就是**完全交给用户手动控制**，不看豆子数量、不看 CD 之外的任何东西。
/// 用途：你知道下一波需要豆子，提前手动补满。
/// </summary>
public class SCH_ForceAetherflow : ISlotResolver
{
    private static uint 技能 => SpellIds.取("以太超流");

    public int Check()
    {
        if (!HealQt.GetQt("强制以太", false)) return -101;   // 默认关，用户手动开
        if (!SpellUtil.已解锁(技能)) return -2;
        if (!CharacterExt.可以插能力技()) return -6;
        return SpellUtil.可用(技能) ? 5 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>
/// 降临之章 —— 炽天附体期间的强化群疗。
///
/// 数据（dump_actions.tsv）：id=41501，CD 20s，
///   CastType=2（圆形），Range=0（自身），EffectRange=15。
///
/// ⚠️ 这是**炽天附体形态下才会变成可用**的技能（Lv0 说明它是形态技能）。
///   `SpellUtil.可用()` 会处理形态判断 —— 不在形态里它就不可用，
///   所以这里不需要额外判形态。
/// </summary>
public class SCH_Accession : ISlotResolver
{
    private static uint 技能 => SpellIds.取("降临之章");

    public int Check()
    {
        if (技能 == 0) return -101;
        if (!HealQt.GetQt("群奶", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 和普通群疗同一个门槛（它本身就是群疗的一种形态）
        if (HealTargetHelper.低于阈值人数(HealSettings.Instance.群体治疗阈值, 20f)
            < HealTargetHelper.群奶人数要求(2)) return -4;

        if (蓝量.低蓝停手()) return -5;

        return SpellUtil.可用(技能) ? 4 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}