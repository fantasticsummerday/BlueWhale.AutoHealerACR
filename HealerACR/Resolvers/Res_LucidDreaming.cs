using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Resolvers;

/// <summary>
/// 醒梦。奶妈蓝量是真的会见底，尤其日随里连续拉人 + 群奶的时候。
/// 放在 OffGcd 队列，不占输出。四个奶妈 24 级左右学会，低等级自动跳过。
///
/// [!] 表 #120：**醒梦是「1 档」的点名特例** ——
///     参考里四奶的醒梦**全部**写 `CanUseOffGcd(1)`，
///     而不是其他 oGCD 那个默认的 2。
///     含义：醒梦**不参与连发池**，队列里已经有能力技时它就等下一拍
///     （它是"补蓝"不是"救命"，该让位给真正需要连发的那些）。
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

        // ★ **读条中不放**（表 #34）—— 参考（`WhiteMage.Resolvers.Ability.醒梦`）
        //   的**第一条**判据就是 `Me.IsCasting ⇒ -1`。
        //   [!] 为什么：醒梦是补蓝，不救命；读条中插它没有收益，
        //       而 `可以插能力技()` 那一条只保证"能力技窗口开着"，
        //       并不排除"我正在读一个 2.5 秒的条" ⇒ 会插在半途，
        //       白白占掉一次能力技窗口（后续真正要连发的治疗就用不上了）。
        if (CharacterExt.我在读条()) return -5;

        // ★ oGCD 队列深度闸门（参考口径 `CanUseOffGcd(1)` —— 醒梦是点名特例）
        if (!OffGcd闸门.可以排(1)) return -4;

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
