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

    /// <summary>
    /// 「必须奶满」时用的技能 —— 只挑"一次到满"那类（拿不到返回 0）。
    ///
    /// ⚠️ 和 <see cref="_t.紧急单奶"/> 的区别见 `必须奶满.最该用的能力技` 的注释：
    ///    那张表里有"血高时无效"的技能（学者深谋远虑之策要降到阈值才触发、
    ///    占星先天禀赋随血量降低而增强），**中 Doom 的人血还很高**，
    ///    交那些技能等于没治。
    /// </summary>
    private uint 奶满技能 => 必须奶满.最该用的能力技(_t);

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;          // 木桩不奶
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("单奶")) return -101;

        // ══════════════════════════════════════════════════════════════
        //  ★ 「必须奶满」优先：**不受紧急单奶阈值限制** ★
        //
        //    下面那条 `最低血量队友(紧急单奶阈值)` 是给普通急救用的。
        //    中 Doom/石化/塞壬之歌的人**血量可能很高**（80%+），
        //    按血线判断会直接把他过滤掉 —— 白白浪费"一次到满"的机会，
        //    然后倒计时归零人没了。
        //
        //    所以这里先无条件检查有没有人中机制；有就把能力技交给他。
        // ══════════════════════════════════════════════════════════════
        var 奶满目标 = 必须奶满.找目标();
        if (奶满目标 != null)
        {
            var 技 = 奶满技能;
            // 没有"一次到满"的能力技（如占星）→ 让位给 GCD 治疗链
            if (技 != 0 && SpellUtil.已解锁(技) && SpellUtil.可用(技)) return 35;
            return -1;
        }

        if (_t.紧急单奶 == 0) return -102;
        if (!SpellUtil.已解锁(_t.紧急单奶)) return -2;

        var target = HealTargetHelper.最低血量队友(HealSettings.Instance.紧急单奶阈值);
        if (target == null) return -1;

        return SpellUtil.可用(_t.紧急单奶) ? 30 : -1;
    }

    public void Build(Slot slot)
    {
        // ⚠️ 必须和 Check 同源（开发约定 F③）：Check 判的是奶满目标，
        //    Build 也得放同一个人 + 同一个技能。
        var 奶满目标 = 必须奶满.找目标();
        if (奶满目标 != null)
        {
            var 技 = 奶满技能;
            if (技 != 0) slot.Add(new Spell(技, 奶满目标));
            return;
        }

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

        // ⚠️ **移动守卫**：群体治疗 GCD 基本都是读条的（阳星 / 医治 / 预后…），
        //    移动中硬读会一直被打断。
        //    ⚠️ 但要注意：**能力技群奶不受影响**（天星冲日 / 不屈不挠之策 /
        //       法令…），它们走 Res_HealAoEAbility，没有移动限制 ——
        //       所以移动中的群体治疗**不会完全没有**，只是从 GCD 版
        //       降级到能力技版。这正是想要的行为。
        if (!SpellUtil.移动中可用(_t.群体治疗GCD)) return -7;

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
        // 先算出"必须奶满"的那个人（没有就是 null）。
        // ⚠️ Check 和 Build 都用这一个变量，保证判谁就放谁（开发约定 F③）。
        var 奶满目标 = 必须奶满.找目标();

        // ★ 木桩 / 开关仍然要尊重 —— 用户关掉治疗就该真的不治
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("单奶")) return -101;
        if (_t.单体治疗GCD == 0) return -102;
        if (!SpellUtil.已解锁(_t.单体治疗GCD)) return -2;

        // ══════════════════════════════════════════════════════════════
        //  ★★ 「必须奶满」机制 —— 优先级高于一切血线判断，且**绕过盾判断** ★★
        //
        //    这类 debuff 的解除条件是"把血奶到 100%"，
        //    而**不是"血量低"** —— 所以绝对不能走下面的血线判断。
        //    如果按血线走，一个 80% 血但中了「塞壬的歌声」的队友
        //    会被判成"没事" → 效果结束 → 直接变僵尸。
        //
        //    ⚠️ 还必须**绕过下面那条"有盾就不再治"**：
        //       盾不等于满血，状态照样解不掉。
        //       所以这里直接 return，不走后面的盾判断。
        // ══════════════════════════════════════════════════════════════
        if (奶满目标 != null)
        {
            return 治疗量最大的单体() != 0 ? 30 : -1;
        }

        // ⚠️ 先锁定"该奶谁"，再判断"这个人还需要奶吗" —— 顺序不能反。
        var target = HealTargetHelper.最低血量队友(HealSettings.Instance.单体治疗阈值);
        if (target == null) return -1;

        // ── 目标已经有厚盾就别再刷治疗：血量 + 盾已经溢出，再读条是纯浪费 GCD ──
        //    （用户实测：学者会"每次都奶两次"，就是这里没检查盾。）
        //
        // ⚠️ 这个判断**必须针对"真正要奶的那个人"**，不能拿坦克当替身。
        //
        //    曾经写成"血量最低的坦克有盾 -> return -3"，那是个真 bug：
        //      `-3` 否决的是**整个单体 GCD 治疗**，不分对象 ——
        //      于是坦克挂着 30 秒鼓舞的期间，队伍里掉到 30% 血的 DPS
        //      **一次单奶都拿不到**。单奶链等于被坦克的盾绑架了。
        //      （静默失效：不报错，只是"该救的人没被救"。）
        try
        {
            if (_t.单体盾 != 0
                && target.有该技能的Buff(_t.单体盾)
                && target.我的Buff还剩超过N秒(AuraIds.技能转Buff(_t.单体盾), 5f))
            {
                return -3;
            }
        }
        catch { }

        // ⚠️ **移动守卫**：单体治疗 GCD 基本都读条（治疗 / 救疗 / 鼓舞…），
        //    移动中硬读会被打断。
        //
        //    ⚠️ 注意它**只挡 GCD 治疗** —— 上面「必须奶满」那条已经提前 return 了
        //       （中继死机制时不能因为走位就放弃救人），
        //       而能力技治疗（天赐祝福 / 神名 / 活性法…）走
        //       Res_HealEmergency，不受这条限制。
        //       所以移动中**仍然有救急手段**，只是不用读条那条路。
        if (!SpellUtil.移动中可用(_t.单体治疗GCD)) return -7;

        return SpellUtil.可用(_t.单体治疗GCD) ? 10 : -1;
    }

    /// <summary>
    /// 「必须奶满」时该用的单体治疗 —— 挑**治疗量最大**的那个。
    ///
    /// ⚠️ 不能直接用 <c>_t.单体治疗GCD</c>："最低级优先"是它给普通掉血用的，
    ///    而中继发症状病时**每一秒都在倒计时**，需要用最高效的治疗尽快奶满。
    ///
    /// 白魔的候选顺序：**救疗(135) → 愈疗(131) → 治疗(120)**（治疗量从高到低）。
    /// 其余职业目前只有单档单体 GCD 治疗，直接用表里的值。
    ///
    /// 拿不到任何候选时退回 <c>_t.单体治疗GCD</c>（宁可放一个弱治疗，也别不放）。
    /// </summary>
    private uint 治疗量最大的单体()
    {
        try
        {
            if (_t.Job == Jobs.WhiteMage)
            {
                var 最优 = SpellUtil.取已解锁(
                    SpellIds.取("救疗"),
                    SpellIds.取("愈疗"),
                    SpellIds.取("治疗"));
                if (最优 != 0) return 最优;
            }
        }
        catch { }

        return _t.单体治疗GCD;
    }

    public void Build(Slot slot)
    {
        // ⚠️ 必须和 Check 用**同一套**选目标逻辑（开发约定 F③）：
        //    Check 判的是"必须奶满那个人"，Build 也得放同一个人，
        //    否则就是"判 A 放 B" —— 那会让机制解不掉、人直接没了。
        var 奶满目标 = 必须奶满.找目标();
        if (奶满目标 != null)
        {
            slot.Add(new Spell(治疗量最大的单体(), 奶满目标));
            return;
        }

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

        // ⚠️ **盾的剩余时间**判断：鼓舞的盾有 30 秒，快过期的盾等于没有。
        //
        //    原来只判"有没有 buff"，于是盾快断了也不补（等它真断才反应，已经晚了）。
        //    现在改成：**剩下的盾撑不过 5 秒，就当没有**。
        //
        //    📌 注：这段注释原来写着"参考同类 ACR 的 Scholar_SingleGCDHeal：
        //       它判的是 buff 时间小于或不存在" —— **那个归因是错的，已删**。
        //       对照分析逐行读过它的 IL：它对盾**只判有没有**（`!HasAura(297)`），
        //       那个"3 秒剩余窗口"是用在 `DeadBuffs`（死亡类 debuff）上的。
        //       按 开发约定.md ⑧：**错误归因的注释比没有注释更危险** ——
        //       它会让人以为"对方也这么做"，从而不敢改、或照着错的抄。
        //       结论不变（剩余时间判断是对的，而且三家只有我们做了），
        //       但理由是"我们自己的判断"，不是"跟对方学的"。
        //
        //    ⚠️ 再加上本地施放记录（官方文档 L129：服务器状态是滞后的）：
        //       刚放完盾的那 0.x 秒里，内存里还看不到盾 buff ——
        //       光看服务器状态会**又放一次**。本地记录不滞后，能盖住这个空窗。
        if (本地施放记录.刚放过(_t.单体盾)) return -3;

        if (坦克.有该技能的Buff(_t.单体盾) && 坦克.我的Buff还剩超过N秒(AuraIds.技能转Buff(_t.单体盾), 5f))
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
