using AEAssist;
using AEAssist.Extension;      // HasAura 的扩展方法在这里
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Resolvers;

/// <summary>
/// **召唤宠物**（学者的「朝日召唤」= 小仙女）。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 判据**只有一条**：宠物不在场就召 ★
///
///    前面几版堆了一堆条件（重召标记、有效期、冷却、移动守卫、待确认…），
///    结果互相干扰、越修越乱，实测表现为：
///      · 落地后 11 秒才召
///      · 召出来之后**又召一次，把刚召出的杀掉了**
///
///    现在砍到最简：**没宠物就召，有宠物就不召**。
///
///  ── 为什么这一条就够，不需要"进本重召" ──
///    小仙女跟着玩家进本，所以进本时它通常**已经在场** ——
///    那不需要重召（它跟着你，位置也是对的）。
///    而它**不在场**的几种情况本来就要召：
///      · 被「转化」主动牺牲掉换以太
///      · 被打死
///      · 刚进游戏 / 切职业还没召
///    ⇒ "不在场就召"自然覆盖全部情况，不需要额外的标记和窗口。
///
///  ── 唯一保留的额外限制 ──
///    ① Qt 开关「自动召唤」—— 用户能关掉
///    ② **转化中不召** —— 那时小仙女是被主动牺牲的，召了也无效
///    ③ 起手间隔 —— 防止读条被打断后**同一毫秒**疯狂起手
///
///  ⚠️ **没有移动守卫**。朝日召唤是 1.5 秒读条，移动会被游戏取消，
///     但那**不花额外代价**（框架自己会立刻重试，实测间隔 21 毫秒）。
///     反过来"因为可能在移动就不试"会让它**一直不召** —— 那是更糟的失败。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class Res_SummonPet : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_SummonPet(JobSpellTable table) => _t = table;

    /// <summary>上次**起手**的时刻（不是成功时刻）</summary>
    private static long _上次起手;

    /// <summary>
    /// 两次起手之间至少隔这么久。
    ///
    /// ⚠️ 1 秒的依据：朝日召唤读条约 1.18 秒。隔着这个间隔，
    ///    上一次要么已经成功、要么已经被取消 ——
    ///    不会出现"上一发还在读、又起手一发"。
    ///
    /// ⚠️ 不能更长：读条被打断后要能**很快重试**（那是唯一的恢复手段）。
    /// </summary>
    private const int 起手间隔毫秒 = 1000;

    public int Check()
    {
        if (_t.召唤宠物 == 0) return -102;                     // 这个职业没有宠物
        if (!HealQt.GetQt("自动召唤", true)) return -101;      // 用户关掉了

        // ★ 唯一的判据：宠物不在场就召 ★
        if (JobApiHelper.有小仙女) return -1;

        // 转化中召了也无效（小仙女被主动牺牲了）
        try
        {
            if (Core.Me.HasAura(AuraIds.转化中)) return -4;
        }
        catch { }

        if (!SpellUtil.已解锁(_t.召唤宠物)) return -2;
        if (!SpellUtil.可用(_t.召唤宠物)) return -5;

        // 起手间隔（防同一毫秒疯狂起手）
        var 现在 = TimeHelper.Now();
        if (现在 - _上次起手 < 起手间隔毫秒) return -6;

        return 20;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(_t.召唤宠物);
        if (spell == null) return;

        _上次起手 = TimeHelper.Now();

        // ⚠️ 「朝日召唤」是**对自己放**的（`Range = 0`，查官方技能表），
        //    不带目标就是默认行为 —— 不要写 `new Spell(id, 队友)`。
        slot.Add(spell);
    }
}
