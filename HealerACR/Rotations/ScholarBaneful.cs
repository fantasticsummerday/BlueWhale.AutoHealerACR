using AEAssist;
using AEAssist.CombatRoutine;
using HealerACR.Common;

namespace HealerACR.Rotations;

/// <summary>
/// 埋伏之毒（Baneful Impaction，学者 92 级）。
///
/// ⚠️ **它不是普通 DoT** —— 需要「埋伏之毒预备」这个 buff 才能用（连环计给的）。
///    之前我把它当普通 DoT 塞进 DoT 等级链里，结果永远打不出来：
///    没预备 buff 时技能根本不可用，而 DoT 链又以为"该补 DoT"。
///
/// 正确用法：用完连环计之后，趁预备 buff 还在，把它打出去。
/// </summary>
public class SCH_BanefulImpaction : ISlotResolver
{
    private static uint 技能 => SpellIds.取("埋伏之毒");

    private static uint 预备 => AuraIds.埋伏之毒预备;

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;
        if (!HealQt.GetQt("链式策略", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        var 目标 = HealTargetHelper.当前目标();
        if (目标 == null) return -1;

        // 没有「埋伏之毒预备」就打不出来（这是关键条件，之前漏了）
        if (预备 == 0 || !Core.Me.HasAura(预备)) return -3;

        // 残血小怪不交（木桩例外）
        if (!HealTargetHelper.木桩模式 && HealTargetHelper.目标快死了()) return -4;

        return SpellUtil.可用(技能) ? 9 : -1;
    }

    public void Build(Slot slot)
    {
        var 目标 = HealTargetHelper.当前目标();
        if (目标 == null) return;

        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(new Spell(spell.Id, 目标));
    }
}
