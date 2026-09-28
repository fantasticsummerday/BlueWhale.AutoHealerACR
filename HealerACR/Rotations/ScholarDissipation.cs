using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Rotations;

/// <summary>
/// 自动转化 —— 复刻 鍚岀被 ACR 的 Scholar_AutoDissipation。
///
/// **转化的作用**：牺牲小仙女，换 **3 颗以太**（CD 180 秒）。
///
/// **用途**：以太超流还在 CD 里、但你又急需豆子的时候用它顶上。
/// 以太超流 CD 45 秒、转化 CD 180 秒，两者错开正好覆盖大部分时间。
///
/// ⚠️ **代价**：小仙女会消失，之后得重新召唤（朝日召唤）。
///    所以它**依赖「自动以太」开关** —— 保证之后能把小仙女叫回来。
///    这也是它**默认关闭**的原因：开着它小仙女会周期性消失。
///
/// 对照它的 Check（IL 常量）：
///     Qt.GetQt("自动转化") / Qt.GetQt("自动以太") / HasAura(304)
///     / IsReadyWithCanCast / RecentlyUsed
/// </summary>
public class SCH_AutoDissipation : ISlotResolver
{
    /// <summary>转化（3587）—— CD 180 秒</summary>
    private static uint 技能 => SpellIds.取("转化");

    public int Check()
    {
        // 默认关：会让小仙女消失，交给用户决定
        if (!HealQt.GetQt("自动转化", false)) return -101;

        // 依赖「自动以太」—— 转化掉的小仙女之后要能叫回来，
        // 否则就是纯粹拿小仙女换豆子，得不偿失（对照 鍚岀被 ACR 也检查这个开关）
        if (!HealQt.GetQt("自动以太", true)) return -100;

        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 已经有豆子就别转化，省着小仙女
        if (JobApiHelper.读得到("以太") && JobApiHelper.以太 > 0) return -3;

        // 已经在转化中就别再放
        if (AuraIds.转化中 != 0 && Core.Me.HasAura(AuraIds.转化中)) return -4;

        // 小仙女不在场就没得牺牲（也用不出这个技能）
        if (!JobApiHelper.有小仙女) return -5;

        if (!CharacterExt.可以插能力技()) return -6;

        return SpellUtil.可用(技能) ? 3 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}
