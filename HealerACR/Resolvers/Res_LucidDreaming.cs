using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Resolvers;

/// <summary>
/// 醒梦。奶妈蓝量是真的会见底，尤其日随里连续拉人 + 群奶的时候。
/// 放在 OffGcd 队列，不占输出。四个奶妈 24 级左右学会，低等级自动跳过。
/// </summary>
public class Res_LucidDreaming : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_LucidDreaming(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (!HealQt.GetQt("醒梦")) return -100;
        if (_t.醒梦 == 0) return -102;
        if (!SpellUtil.已解锁(_t.醒梦)) return -2;

        // 两次能力技之间别插太挤
        if (!CharacterExt.可以插能力技()) return -6;

        if (CharacterExt.我的当前蓝量() <= HealSettings.Instance.醒梦蓝量阈值)
        {
            return SpellUtil.可用(_t.醒梦) ? 3 : -1;
        }

        return -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(_t.醒梦);
        if (spell != null) slot.Add(spell);
    }
}
