using AEAssist.CombatRoutine;
using HealerACR.Common;

namespace HealerACR.Resolvers;

// ============================================================================
//  资源溢出控制。
//
//  奶妈的资源都有上限，攒满之后自然回复/积累就**永久浪费**了：
//    白魔 百合 0-3      —— 满 3 颗不再增长，而且不花百合就没有血百合
//    贤者 蛇胆 0-3      —— 每 20 秒自然回 1 颗，满了就白回
//    学者 以太 0-3      —— 满 3 颗不再回（这个交给能量吸收处理，见 SCH 的输出能力技）
//    占星 手牌 0-2      —— 满了抽不了（出卡逻辑已经覆盖）
//
//  白魔的百合在 WHM_AfflatusSolace / Rapture 里处理。
//  这里放贤者的蛇胆溢出（灵橡清汁）。
// ============================================================================

/// <summary>
/// 贤者蛇胆溢出保护：攒到 3 颗就用**灵橡清汁**花掉一颗。
///
/// 为什么要有：蛇胆每 20 秒自然回 1 颗，满 3 颗之后自然回复就停了 ——
/// 也就是说"攒着"本身就在亏。哪怕没人掉血也值得花一颗（治疗溢出总比资源溢出强）。
/// </summary>
public class SGE_CholeOverflow : ISlotResolver
{
    private static uint 技能 => SpellIds.取("灵橡清汁");

    public int Check()
    {
        if (!SpellUtil.已解锁(技能)) return -2;

        // 只有满了才走这条（不满的时候留给真正的治疗场景用）
        if (JobApiHelper.蛇胆 < 3) return -1;

        // 满了就是资源溢出，**无视一切治疗开关**（木桩模式 / 奶人）--
        // 攒着本身就是亏，卸掉总比浪费强

        // 目标兜底到自己（单人环境可能没有队友）
        if (HealTargetHelper.最危险队友() == null && AEAssist.Core.Me.CurrentHp <= 0) return -1;

        return SpellUtil.可用(技能) ? 12 : -1;
    }

    public void Build(Slot slot)
    {
        var 目标 = HealTargetHelper.最危险队友()
                   ?? HealTargetHelper.血量最低的坦克()
                   ?? AEAssist.Core.Me;
        if (目标 == null) return;

        var spell = SpellUtil.Get(技能);
        if (spell == null) return;
        slot.Add(new Spell(spell.Id, 目标));
    }
}
