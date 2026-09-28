using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Extension;
using HealerACR.Common;

namespace HealerACR.Resolvers;

/// <summary>
/// 驱散（康复 / Esuna）。日随里中毒、麻痹、减速这些不处理很容易滚雪球。
/// 四个奶妈都是 10 级学会，低等级自动跳过。
/// </summary>
public class Res_Esuna : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_Esuna(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (!HealQt.GetQt("驱散")) return -100;
        if (_t.驱散 == 0) return -101;
        if (!SpellUtil.已解锁(_t.驱散)) return -2;

        var target = HealTargetHelper.需要驱散队友();
        if (target == null) return -1;

        return SpellUtil.可用(_t.驱散) ? 8 : -1;
    }

    public void Build(Slot slot)
    {
        var target = HealTargetHelper.需要驱散队友();
        if (target == null) return;
        slot.Add(new Spell(_t.驱散, target));
    }
}
