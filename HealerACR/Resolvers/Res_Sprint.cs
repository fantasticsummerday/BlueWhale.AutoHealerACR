using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Resolvers;

/// <summary>
/// 脱战自动疾跑 —— 日随跑图用（参考同类 ACR 的 外部 ACR.Sprint）。
///
/// 条件卡得很死，只在"确实要赶路"时才跑：
///   不在战斗 + 正在移动 + 疾跑可用
///
/// 放在 **Always 队列**：这个队列脱战时也会跑，
/// 而 Gcd/OffGcd 队列在脱战时不一定会走到这里。
/// </summary>
public class Res_Sprint : ISlotResolver
{
    /// <summary>疾跑（通用技能，所有职业共用）</summary>
    private static uint 技能 => SpellsDefine.Sprint;

    public int Check()
    {
        if (!HealQt.GetQt("自动疾跑", true)) return -101;
        if (技能 == 0) return -102;

        // 战斗中不跑 —— 疾跑在战斗里是拿来躲机制的，别乱交
        if (CharacterExt.我在战斗()) return -1;

        // 人没动就别按（站着不动还疾跑纯浪费）
        try
        {
            if (!MoveHelper.IsMoving()) return -1;
        }
        catch
        {
            return -1;
        }

        return SpellUtil.可用(技能) ? 1 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}
