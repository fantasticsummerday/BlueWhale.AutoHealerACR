using AEAssist.CombatRoutine;
using HealerACR.Common;
using HealerACR.Timeline;

namespace HealerACR.Resolvers;

// ============================================================================
//  三层治疗结构：
//    能力技大加（不占 GCD）→ 群体治疗 → 单体治疗 GCD
//
//  两个横切规则：
//    · 木桩模式（需求 5）：治疗全部让路，输出按最优策略打
//    · 资源充足度（需求 3）：能力技资源不够时退回 GCD 群奶，
//      把资源留给更急的时候
// ============================================================================

/// <summary>紧急单奶（OffGcd）：天赐祝福 / 深谋远虑之策 / 先天禀赋 / 输血 这类。</summary>
public class Res_HealEmergency : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_HealEmergency(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;          // 木桩不奶
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("单奶")) return -101;
        if (_t.紧急单奶 == 0) return -102;
        if (!SpellUtil.已解锁(_t.紧急单奶)) return -2;

        var target = HealTargetHelper.最低血量队友(HealSettings.Instance.紧急单奶阈值);
        if (target == null) return -1;

        return SpellUtil.可用(_t.紧急单奶) ? 30 : -1;
    }

    public void Build(Slot slot)
    {
        var target = HealTargetHelper.最低血量队友(HealSettings.Instance.紧急单奶阈值);
        if (target == null) return;
        slot.Add(new Spell(_t.紧急单奶, target));
    }
}

/// <summary>群体治疗能力技（OffGcd）：不屈不挠之策 / 天星冲日 / 消化 / 法令 之类。</summary>
public class Res_HealAoEAbility : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_HealAoEAbility(JobSpellTable table) => _t = table;

    public int Check()
    {
        // 木桩模式治疗让路 —— 但"输出型群奶技"（白魔法令）不算治疗，
        // 它是卡 CD 打的输出，必须放行，否则木桩就永远不打法令了
        if (HealTargetHelper.木桩模式 && !_t.群体治疗能力技是输出型) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("群奶")) return -101;
        if (_t.群体治疗能力技 == 0) return -102;
        if (!SpellUtil.已解锁(_t.群体治疗能力技)) return -2;

        var s = HealSettings.Instance;
        var 低于血线 = HealTargetHelper.低于阈值人数(s.群体治疗阈值);

        // 输出型（白魔法令）：卡 CD 打，不等掉血。
        // 注意**不要**检查当前目标 —— 法令是"以自己为中心"的范围技，
        // 和之前王冠之领主犯的是同一个错。
        if (_t.群体治疗能力技是输出型)
        {
            return SpellUtil.可用(_t.群体治疗能力技) ? 25 : -1;
        }

        // 治疗型：能力技比 GCD 群奶更"舍得用"，门槛比设置里少一个人
        var 要求人数 = Math.Max(1, s.群奶最少人数 - 1);
        if (低于血线 < 要求人数) return -1;

        // 资源不够（学者没以太、贤者没蛇胆）→ 让给 GCD 群奶，除非掉血的人明显更多
        if (!_t.治疗资源充足 && 低于血线 < s.群奶最少人数) return -103;

        // 时间轴预报：更大的伤害马上要来，这个能力技留着那时候用
        if (HealSettings.Instance.时间轴攒资源 && TimelineManager.未来有减伤(5.0)
            && 低于血线 < s.群奶最少人数 + 1)
        {
            return -5;
        }

        return SpellUtil.可用(_t.群体治疗能力技) ? 20 : -1;
    }

    public void Build(Slot slot)
    {
        // 能力技不等服务器回包（文档建议），避免能力技之间互相卡顿
        slot.Add(CharacterExt.能力技(_t.群体治疗能力技));
    }
}

/// <summary>群体治疗 GCD：医济 / 士气高扬之策 / 阳星 / 预后。</summary>
public class Res_HealAoEGcd : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_HealAoEGcd(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("群奶")) return -101;
        if (_t.群体治疗GCD == 0) return -102;
        if (!SpellUtil.已解锁(_t.群体治疗GCD)) return -2;

        var s = HealSettings.Instance;
        if (HealTargetHelper.低于阈值人数(s.群体治疗阈值) < HealTargetHelper.群奶人数要求(s.群奶最少人数)) return -1;

        return SpellUtil.可用(_t.群体治疗GCD) ? 12 : -1;
    }

    public void Build(Slot slot)
    {
        slot.Add(new Spell(_t.群体治疗GCD, SpellTargetType.Self));
    }
}

/// <summary>单体治疗 GCD。目标永远是"血最少的那个人"。</summary>
public class Res_HealSingleGcd : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_HealSingleGcd(JobSpellTable table) => _t = table;

    public int Check()
    {
            // ⚠️ 已经有护盾就别再刷治疗 —— 血量 + 盾已经溢出，再读条是纯浪费 GCD。
            //    （用户实测：学者会"每次都奶两次"，就是这里没检查盾。）
            try
            {
                if (_t.单体盾 != 0)
                {
                    var 有盾的 = HealTargetHelper.血量最低的坦克(0.99f);
                    // 盾还在、而且还能撑一会儿 —— 那就不用再刷治疗了
                    if (有盾的 != null
                        && 有盾的.有该技能的Buff(_t.单体盾)
                        && !有盾的.我的Buff快没了(AuraIds.技能转Buff(_t.单体盾), 5))
                    {
                        return -3;
                    }
                }
            }
            catch { }

        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("单奶")) return -101;
        if (_t.单体治疗GCD == 0) return -102;
        if (!SpellUtil.已解锁(_t.单体治疗GCD)) return -2;

        var target = HealTargetHelper.最低血量队友(HealSettings.Instance.单体治疗阈值);
        if (target == null) return -1;

        return SpellUtil.可用(_t.单体治疗GCD) ? 10 : -1;
    }

    public void Build(Slot slot)
    {
        var target = HealTargetHelper.最低血量队友(HealSettings.Instance.单体治疗阈值);
        if (target == null) return;
        slot.Add(new Spell(_t.单体治疗GCD, target));
    }
}

/// <summary>单体盾（预铺）：学者鼓舞 / 贤者均衡诊断。</summary>
public class Res_HealShield : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_HealShield(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (_t.单体盾 == 0) return -102;
        if (!SpellUtil.已解锁(_t.单体盾)) return -2;

        var 该铺 = 减伤Helper.即将来大伤害();
        var 坦克 = HealTargetHelper.血量最低的坦克(该铺 ? 1f : 0.7f);
        if (坦克 == null) return -1;
        // ⚠️ 对照 鍚岀被 ACR 的 Scholar_SingleGCDHeal：
        //    它判的是"目标身上 buff 时间小于或不存在"，**不是"有没有 buff"**。
        //
        //    区别很大：鼓舞的盾只有 30 秒，快过期的盾等于没有。
        //    我原来只判"有没有"，于是：
        //      · 盾快断了也不补（等它真断才反应，已经晚了）
        //      · 而且判太粗会导致"该补的时候没补、不该补的时候补了"
        //
        //    现在改成：**剩下的盾撑不过 5 秒，就当没有**。
        //
        //    ⚠️ 再加上本地施放记录（官方文档 L129：服务器状态是滞后的）：
        //       刚放完盾的那 0.x 秒里，内存里还看不到盾 buff ——
        //       光看服务器状态会**又放一次**。本地记录不滞后，能盖住这个空窗。
        if (本地施放记录.刚放过(_t.单体盾)) return -3;

        if (坦克.有该技能的Buff(_t.单体盾) && !坦克.我的Buff快没了(AuraIds.技能转Buff(_t.单体盾), 5))
        {
            return -3;
        }

        return SpellUtil.可用(_t.单体盾) ? 3 : -1;
    }

    public void Build(Slot slot)
    {
        var 该铺 = 减伤Helper.即将来大伤害();
        var 坦克 = HealTargetHelper.血量最低的坦克(该铺 ? 1f : 0.7f);
        if (坦克 == null) return;

        if (_t.护盾前置 != 0 && !JobApiHelper.均衡中)
        {
            var pre = SpellUtil.Get(_t.护盾前置);
            if (pre != null) slot.Add(pre);
        }

        // 用当前形态：贤者的"诊断"在均衡状态下会变成"均衡诊断"
        var 盾 = SpellUtil.当前形态(_t.单体盾);
        if (盾 == null) return;
        slot.Add(new Spell(盾.Id, 坦克));
    }
}
