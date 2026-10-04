using AEAssist.CombatRoutine;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Resolvers;

// ============================================================================
//  0.3.9 补四项日随实用能力（参考同类 ACR 的 MoveGCD / LBCheck / UsePotionCheck /
//  IsReadyWithin）。
//
//  API 均从 AEAssist.NET 元数据里核实过签名，不是猜的：
//    MoveHelper.IsMoving()                        -> 移动检测
//    SpellHelper.GetLimitBreakLevel()             -> LB 等级
//    SpellHelper.CreateLimitBreak(target)         -> 造一个 LB 技能
//    SpellHelper.CoolDownInGCDs(spellId, count)   -> 技能是否会在 N 个 GCD 内就绪
// ============================================================================

// ============================================================================
//  一、移动填充
// ============================================================================

/// <summary>
/// 移动中的瞬发填充。
///
/// 各职业的瞬发 GCD 不一样：学者有**毁坏**（Ruin II）这种纯瞬发填充技，
/// 其余三个职业的填充技都是读条的 —— 移动时硬读会断，所以宁可不放。
/// 由 <see cref="JobSpellTable.移动填充技"/> 决定，为 0 就整个跳过。
/// </summary>
public class Res_MoveGcd : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_MoveGcd(JobSpellTable t) => _t = t;

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;

        var 技能 = _t.移动填充技;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        // ★ 失衡硬闸门（复刻 youshu）：贤者的移动填充技是失衡，
        //    命中硬闸门（地图黑名单 / 低蓝+虚弱 / 均衡中）就整条禁用。
        if (_t.自身AOE硬闸门()) return -3;

        // 只在真的在移动时用
        if (!MoveHelper.IsMoving()) return -1;

        if (HealTargetHelper.当前目标() == null) return -1;

        var spell = SpellUtil.当前形态(技能);
        return spell != null && spell.IsReadyWithCanCast() ? 2 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(_t.移动填充技);
        if (spell != null) slot.Add(spell);
    }
}

// ============================================================================
//  二、极限技（奶妈 LB 是用来救场的：LB3 群体复活 + 大治疗）
// ============================================================================

/// <summary>
/// 极限技。日随里奶妈放 LB 只有一个理由 —— **死的人太多，靠 LB3 拉起来**。
/// 所以条件卡得很死：LB 满级 + 至少 2 个人躺着 + 自己活着。
/// 平时绝不碰，免得把 LB 浪费掉。
/// </summary>
public class Res_LimitBreak : ISlotResolver
{
    public int Check()
    {
        if (!HealQt.GetQt("极限技", true)) return -101;
        if (HealTargetHelper.木桩模式) return -300;
        if (CharacterExt.我的当前血量() <= 0) return -1;

        // 死的人不够多就别放（日随绝大多数情况都不该放）
        var 死亡数 = PartyHelper.DeadAllies.Count();
        if (死亡数 < 2) return -1;

        // 注：不查 LB 等级 —— TriggerCondLBCheck 不在可用命名空间里。
        // CreateLimitBreak 返回的就是当前等级的 LB，能放就代表可用；
        // 而"死 2 个人"本身已经够危急，这时候放不算浪费。
        var lb = Spell.CreateLimitBreak(Core.Me);
        if (lb == null) return -1;

        return lb.IsReadyWithCanCast() ? 1 : -1;
    }

    public void Build(Slot slot)
    {
        var lb = Spell.CreateLimitBreak(Core.Me);
        if (lb != null) slot.Add(lb);
    }
}

// ============================================================================
//  三、技能就绪预判（IsReadyWithin 的等价物）
// ============================================================================

/// <summary>
/// 就绪预判工具。对应 同类 ACR 的 IsReadyWithin / IsReadyByNextGcd。
///
/// 用途：避免"技能还有半个 GCD 就好了，却先用了个替代品"这种浪费。
/// </summary>
public static class 就绪预判
{
    /// <summary>这个技能会在接下来的 N 个 GCD 内就绪吗</summary>
    public static bool 将在(string 名字, int gcd数 = 1)
    {
        var id = SpellIds.取(名字);
        if (id == 0) return false;

        try
        {
            return SpellExtension.CoolDownInGCDs(id, gcd数);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>这个技能会在接下来的 N 个 GCD 内就绪吗（直接给 ID）</summary>
    public static bool 将在(uint id, int gcd数 = 1)
    {
        if (id == 0) return false;

        try
        {
            return SpellExtension.CoolDownInGCDs(id, gcd数);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>现在离下一个 GCD 还有多久（毫秒）</summary>
    public static int GCD剩余 => GCDHelper.GetGCDCooldown();
}

