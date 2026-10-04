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

        // ★ 2026-10-04 修（补深度发现 R-1 / 表 #117）：对照实现**没有"满 3 卸豆"这条通道** ✗
        //   灵橡清汁只是它 `自动单奶.SelectAction` 的**最后选择**，而且要求目标 ≤ 灵橡阈值(0.50) ✓
        //   原来我们满 3 就花、还无视一切治疗开关 ⇒ 很可能花在**满血的人**身上 ✗
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;

        // ★ 单奶开关也要尊重（参考的 `自动单奶` 在「奶人 + 单奶」之下）★
        if (!HealQt.GetQt("单奶")) return -101;

        // ★ **蛇胆互斥**（表 #118 / #119）：两次蛇胆消费至少隔 2000ms ★
        //   灵橡和 输血 / 白牛清汁 吃**同一池蛇胆** ——
        //   不带这条时，"刚交过输血 → 这一帧又交灵橡"会连着倒两颗，
        //   第二发常常打在刚被奶满的人身上。
        if (!蛇胆节流.可以花()) return -3;

        // 必须有人真的低于灵橡阈值才花（否则宁可留着豆子）
        // 灵橡阈值 0.50（对照实现的 `灵橡阈值` 默认 50）✓
        if (HealTargetHelper.低于阈值人数(灵橡线) == 0) return -1;

        // ⚠️ 血线**必须带阈值** —— 不能用 `最危险队友()`（它没有阈值，
        //    队伍全员健康时也会返回"相对最惨的那个"，等于把豆子花在满血的人身上）
        if (选目标() == null) return -1;

        return SpellUtil.可用(技能) ? 12 : -1;
    }

    /// <summary>灵橡清汁的血线（对照实现的 `灵橡阈值` 默认 50）</summary>
    private const float 灵橡线 = 0.50f;

    /// <summary>
    /// 灵橡清汁的目标：**血线之下的最低者**，没有就退到自己（但**自己也要过线**）。
    ///
    /// [!] 和 `Check` 同源（开发约定 F③）—— 判谁就放谁，不能这边判"有人低于 0.5"、
    ///     那边抓一个 0.9 的人来治。
    /// </summary>
    private static IBattleChara? 选目标()
    {
        try
        {
            var 队友 = HealTargetHelper.最低血量队友(灵橡线, 30f);
            if (队友 != null) return 队友;

            // 没队友在血线之下 ⇒ 看自己（自己也要过线才值得花）
            var 我 = AEAssist.Core.Me;
            if (我 != null && 我.对象有效() && 我.活着()
                && 我.有效血量比例() <= 灵橡线) return 我;
        }
        catch { }

        return null;
    }

    public void Build(Slot slot)
    {
        var 目标 = 选目标();
        if (目标 == null) return;

        var spell = SpellUtil.Get(技能);
        if (spell == null) return;
        slot.Add(new Spell(spell.Id, 目标));

        // ★ 花掉一颗蛇胆 ⇒ 记账（和另外两个消费点共用同一条节流）
        蛇胆节流.记一次消费();
    }
}
