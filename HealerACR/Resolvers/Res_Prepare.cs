using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Resolvers;

/// <summary>
/// 脱战期间提前把战斗资源补上（需求 4）。
///
/// 各职业的"开战前该做的事"：
///   学者 —— 以太超流补满（开战就没机会慢慢补了）
///   贤者 —— 根素补蛇胆
///   占星 —— 抽卡（手上有牌，开战就能出）
///   白魔 —— 百合是被动攒的，不需要主动做什么
///
/// 具体做什么由 <see cref="JobSpellTable.脱战准备技能"/> 决定，这里只管循环放。
/// </summary>
public class Res_PrepareResources : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_PrepareResources(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (!HealQt.GetQt("脱战准备", true)) return -101;
        if (_t.脱战准备技能.Length == 0) return -102;

        // 只在脱战时做
        if (Core.Me.InCombat()) return -1;

        foreach (var id in _t.脱战准备技能)
        {
            if (id == 0) continue;
            if (!SpellUtil.已解锁(id)) continue;
            if (!SpellUtil.可用(id)) continue;
            return 9;
        }

        return -1;
    }

    public void Build(Slot slot)
    {
        foreach (var id in _t.脱战准备技能)
        {
            if (id == 0) continue;
            if (!SpellUtil.已解锁(id)) continue;
            if (!SpellUtil.可用(id)) continue;

            var spell = SpellUtil.Get(id);
            if (spell != null) slot.Add(spell);
            return;
        }
    }
}
