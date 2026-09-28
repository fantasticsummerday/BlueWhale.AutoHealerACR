using AEAssist.CombatRoutine;
using HealerACR.Common;
using HealerACR.Timeline;

namespace HealerACR.Resolvers;

// ============================================================================
//  补上"鍚岀被 ACR 有而我没有"的几个技能。
//
//  对照来源：反编译 鍚岀被 ACR.dll 的类型表看到它有
//    Scholar_WhisperingDawn / Scholar_FeyIllumination / AST.Horoscope
//  这三个东西我原先没有，技能 ID 都是从游戏 Action 表 dump 出来核对过的。
// ============================================================================

/// <summary>
/// 仙光的低语（Whispering Dawn，学者 20 级）。
/// 小仙女给的群体 HoT —— 不占自己的 GCD，掉血就能放。
/// </summary>
public class SCH_WhisperingDawn : ISlotResolver
{
    private static uint 技能 => SpellIds.取("仙光的低语");

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("群奶")) return -101;
        if (!HealQt.GetQt("小仙女", true)) return -103;
        if (!SpellUtil.已解锁(技能)) return -2;

        var s = HealSettings.Instance;
        var 要求人数 = Math.Max(1, s.群奶最少人数 - 1);
        if (HealTargetHelper.低于阈值人数(s.群体治疗阈值) < 要求人数) return -1;

        return SpellUtil.可用(技能) ? 17 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>
/// 异想的幻光（Fey Illumination，学者 40 级）。
/// 小仙女给的**群体减伤 + 治疗量提升**，所以用法偏"减伤"：
/// boss 要打 AOE 之前铺，或者团队已经在掉血时补。
/// </summary>
public class SCH_FeyIllumination : ISlotResolver
{
    private static uint 技能 => SpellIds.取("异想的幻光");

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("小仙女", true)) return -103;
        if (!SpellUtil.已解锁(技能)) return -2;

        var 要来伤害 = TimelineManager.未来有减伤(4.0) || 减伤Helper.即将来大伤害();
        var 团队掉血 = HealTargetHelper.低于阈值人数(HealSettings.Instance.群体治疗阈值)
                       >= Math.Max(1, HealSettings.Instance.群奶最少人数 - 1);

        if (!要来伤害 && !团队掉血) return -1;

        return SpellUtil.可用(技能) ? 16 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>
/// 天宫图（Horoscope，占星 76 级）。
///
/// 机制：先给全队挂一层"待触发治疗"，之后用阳星/阳星相位（或再按一次天宫图）
/// 才会结算成实际治疗。所以**用法跟群盾一样 —— 伤害来之前提前铺**。
/// 铺完不用管，后面的群奶会自动把它触发掉。
/// </summary>
public class AST_Horoscope : ISlotResolver
{
    private static uint 技能 => SpellIds.取("天宫图");

    public int Check()
    {
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("群奶")) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 木桩也要放（练循环用）—— 原来这里被木桩整个拦掉了
        if (HealTargetHelper.木桩模式) return SpellUtil.可用(技能) ? 13 : -1;

        // 时间轴预报 / boss 读条 → 提前铺
        var 要来了 = TimelineManager.未来有减伤(4.0) || 减伤Helper.即将来大伤害();
        if (!要来了) return -1;

        // 已经铺过就不重复（buff id 通常和技能一致）
        if (Core.Me.有该技能的Buff(技能)) return -3;

        return SpellUtil.可用(技能) ? 13 : -1;
    }

    public void Build(Slot slot)
    {
        // 用当前形态：这类"预铺"技能有可能被游戏替换
        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;
        slot.Add(new Spell(spell.Id, SpellTargetType.Self));
    }
}
