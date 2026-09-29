using AEAssist;
using AEAssist.Extension;      // HasAura 的扩展方法在这里
using HealerACR.Common;

namespace HealerACR.Resolvers;

/// <summary>
/// **召唤宠物**（学者的「朝日召唤」= 小仙女）。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么单独一个 resolver，而不是塞进 `脱战准备技能` ★
///
///    `Res_PrepareResources` 只在**脱战**时工作
///    （`if (Core.Me.InCombat()) return -1;`）。
///    而宠物在**战斗中也会没**：
///      · 被「转化」（Dissipation）主动牺牲掉换以太
///      · 被打死
///
///    那时候不补，小仙女给的免费治疗就全没了 —— 学者的治疗量会明显下降，
///    而且「异想的祥光」「异想的幻光」「以太契约」「炽天召唤」**全部放不出来**。
///
///  ★ 触发条件 ★
///
///    ① 这个职业有宠物（`召唤宠物 != 0`）
///    ② **宠物不在场**（`JobApiHelper.有小仙女 == false`）
///    ③ 技能已解锁（Lv4 起）
///    ④ 技能可用（CD —— 朝日召唤 2.5 秒，几乎总是可用）
///    ⑤ **不在「转化」状态**（`AuraIds.转化中`）
///       —— 转化期间小仙女是被**主动牺牲**的，那时召唤无效；
///          硬召会白白占一个 GCD（同类 ACR 用的也是这个判据）
///
///  ⚠️ **不限制"必须在副本里"** —— 野外/城里召唤也完全无害，
///     而且能覆盖"忘了召就进本了"的情况。多一个限制只会多一种失效方式。
///
///  ⚠️ 放在队列**很靠前**的位置：宠物是一切的先决条件，
///     晚一拍召唤就晚一拍开始享受它的收益。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class Res_SummonPet : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_SummonPet(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (_t.召唤宠物 == 0) return -102;                  // 这个职业没有宠物

        // ★ Qt 开关 —— 用户能关掉（和「小仙女」那个开关分开：
        //   那个管"用不用小仙女的技能"，这个管"召不召唤"）
        if (!HealQt.GetQt("自动召唤", true)) return -101;

        // 已经在场就不重复召
        if (JobApiHelper.有小仙女) return -1;

        // 转化期间召唤无效（小仙女被主动牺牲了）
        try
        {
            if (Core.Me.HasAura(AuraIds.转化中)) return -4;
        }
        catch { }

        if (!SpellUtil.已解锁(_t.召唤宠物)) return -2;
        if (!SpellUtil.可用(_t.召唤宠物)) return -5;

        return 8;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(_t.召唤宠物);
        if (spell == null) return;

        // ⚠️ 「朝日召唤」是**对自己放**的（`Range = 0`，查官方技能表），
        //    不带目标就是默认行为 —— 不要写 `new Spell(id, 队友)`。
        slot.Add(spell);
    }
}
