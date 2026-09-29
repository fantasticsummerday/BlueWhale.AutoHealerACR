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

/// <summary>
/// 「必须奶满」—— 独立的高优先 GCD 治疗。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么要独立成一个 resolver（审计发现的问题）★
///
///  这段逻辑原来写在 `Res_HealSingleGcd` 里（返回 30，注释写"优先级高于一切"）。
///  但 **`Check()` 的返回值不参与仲裁** —— 真正决定顺序的是它在队列里的**行号**
///  （反汇编证据见 `Res_SingleHoT` 的注释）。
///
///  而 `Res_HealSingleGcd` 在各职业队列里排在**几乎最后**：
///      再生 → 狂喜之心 → 安慰之心 → 医济 → **单体治疗**
///  于是中「塞壬的歌声(370)」「渐渐石化(1628)」「混沌之泥土(1604)」的人，
///  只要场上①有 1 人 <52%（被再生抢走 GCD）或②≥2 人 <62%（被医济抢走），
///  这个"必须奶满"就**永远拿不到 GCD** —— 倒计时归零人直接没了。
///
///  ⇒ 拆成独立 resolver，插到决策队列**最前面**（复活/驱散之后），
///    这样才配得上"优先级高于一切"这句话。
///
///  ⚠️ 它只在**真的有人中机制**时才返回 >=0，
///     平时完全不占位（返回 -1，框架继续试后面的）。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class Res_MustFullHeal : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_MustFullHeal(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("单奶")) return -101;

        // 没有人为"必须奶满"机制时立刻让位（常态路径，不占 GCD）
        if (必须奶满.找目标() == null) return -1;

        var 技 = 治疗量最大的单体();
        if (技 == 0) return -1;

        // ⚠️ 不加重移动守卫的判断：中这类机制时**不能因为走位就放弃救人**
        //    （这条是有意为之，和 `Res_HealSingleGcd` 里那句注释一致）
        return SpellUtil.可用(技) ? 40 : -1;
    }

    public void Build(Slot slot)
    {
        var 目标 = 必须奶满.找目标();
        if (目标 == null) return;

        var 技 = 治疗量最大的单体();
        if (技 == 0) return;

        slot.Add(new Spell(技, 目标));
    }

    /// <summary>
    /// 「必须奶满」时该用的单体治疗 —— 挑**治疗量最大**的那个。
    ///
    /// ⚠️ 不能直接用 <c>_t.单体治疗GCD</c>："最低级优先"是它给普通掉血用的，
    ///    而中继发症状病时**每一秒都在倒计时**，需要用最高效的治疗尽快奶满。
    ///
    /// 白魔的候选顺序：**救疗(135) → 治疗(120)**（治疗量从高到低）。
    ///
    ///   ⚠️ 这里原来还列了「愈疗(131)」并注释"治疗量从高到低"。**那是错的**：
    ///      已核实 dump_actions.tsv —— 131 愈疗 = `CastType=2 / EffectRange=10`，
    ///      它是**群疗**（以目标为中心 10 米），不是单体，治疗量也不是最高档。
    ///      而且它 Lv40 比救疗 Lv30 后解锁，`取已解锁` 永远选不到它（死代码）。
    ///
    /// 其余职业目前只有单档单体 GCD 治疗，直接用表里的值。
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
                    SpellIds.取("治疗"));

                if (最优 != 0) return 最优;
            }

            return _t.单体治疗GCD;
        }
        catch
        {
            return _t.单体治疗GCD;
        }
    }
}

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

            if (技 != 0 && SpellUtil.已解锁(技) && SpellUtil.可用(技)) return 35;

            // ⚠️ 没有"一次到满"的能力技时，**不要 return -1** ——
            //    那会把**整个 resolver**（包括下面的常规急救）一起否掉。
            //
            //    审计发现的后果：只要场上有**任何一个人**中 370/1604/1628
            //    （最长 20 秒），而一次性到满的技能在 CD
            //    （占星永远返回 0 / 白魔天赐 180s / 学者贤者的对应技能转 CD），
            //    这条 `return -1` 会让**另一个濒死队友也拿不到常规急救** ——
            //    因为 :62 那条路被跳过，整条能力技急救链全部失效。
            //
            //    ⇒ 正确做法：**落下去走常规急救**。机制那个人交给 GCD 治疗链
            //      （`Res_HealSingleGcd` 里有"必须奶满"分支，不会被血线挡住），
            //      而这条能力技留给真正濒死的另一个人。
        }

        if (_t.紧急单奶 == 0) return -102;
        if (!SpellUtil.已解锁(_t.紧急单奶)) return -2;

        var target = HealTargetHelper.最低血量队友(HealSettings.Instance.紧急单奶阈值);
        if (target == null) return -1;

        return SpellUtil.可用(_t.紧急单奶) ? 30 : -1;
    }

    public void Build(Slot slot)
    {
        // ⚠️ 必须和 Check 同源（开发约定 F③）。
        //
        //    ⚠️ 这里曾经**不同源**：Build 一看到「有奶满目标」就 return，
        //       但 Check 在"没有一次性到满技能"时会**落到常规急救**。
        //       结果：Check 返回 30（走常规急救那条路），Build 却因为
        //       奶满目标 != null 直接 return → **slot 是空的** →
        //       框架看到 Build 没产出就继续试下一个 resolver →
        //       这条能力技急救**静默失效**。
        //
        //    ⇒ 两边用**同一个判断**：只有在"奶满目标存在**且**奶满技能真的可用"时
        //      才走奶满分支，否则一致地落到常规急救。
        var 奶满目标 = 必须奶满.找目标();
        var 技 = 奶满技能;

        if (奶满目标 != null && 技 != 0 && SpellUtil.已解锁(技) && SpellUtil.可用(技))
        {
            slot.Add(new Spell(技, 奶满目标));
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
        // ⚠️ 必须传**技能真实半径**（20 米）—— 不传就落到默认 30 米，
        //    会把 20 米外的人也算成"该群疗" → 交掉一个大 CD 却只治到 1 个人。
        //    （不屈不挠之策 3583 / 天星冲日 16553 / 消化 24301 都是 20 米）
        var 低于血线 = HealTargetHelper.低于阈值人数(s.群体治疗阈值, 20f);

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
        if (HealTargetHelper.低于阈值人数(s.群体治疗阈值, 20f) < HealTargetHelper.群奶人数要求(s.群奶最少人数)) return -1;

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
        // ★ 木桩 / 开关仍然要尊重 —— 用户关掉治疗就该真的不治
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("单奶")) return -101;
        if (_t.单体治疗GCD == 0) return -102;
        if (!SpellUtil.已解锁(_t.单体治疗GCD)) return -2;

        // ══════════════════════════════════════════════════════════════
        //  ⚠️ 「必须奶满」已经**移出去**了 —— 见 `Res_MustFullHeal`
        //
        //  这里原来是：
        //      if (奶满目标 != null) return 治疗量最大的单体() != 0 ? 30 : -1;
        //  注释写"优先级高于一切血线判断"。**但返回值不参与仲裁** ——
        //  决定顺序的是**队列行号**，而本 resolver 在各职业队列里排**几乎最后**
        //  （再生 → 狂喜 → 安慰 → 医济 → 单体治疗）。
        //
        //  后果：中「塞壬的歌声」的人只要场上有 1 人 <52%（被再生抢）
        //  或 ≥2 人 <62%（被医济抢），这条"必须奶满"就**拿不到 GCD**。
        //
        //  ⇒ 现在由 `Res_MustFullHeal` 承担，它插在决策队列**最前面**。
        //    本 resolver 只负责普通血线治疗。
        //    （历史教训见 `Res_SingleHoT` 的注释：**别再用返回值表达优先级**。）
        // ══════════════════════════════════════════════════════════════

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

    public void Build(Slot slot)
    {
        // ⚠️ 必须和 Check 用**同一套**选目标逻辑（开发约定 F③）。
        //    「必须奶满」那条已经移给 `Res_MustFullHeal`，
        //    这里只管普通血线治疗 —— 两边都统一才不会有"判 A 放 B"。
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
        var 坦克 = 选目标(该铺);
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

        // ⚠️ **移动守卫**：护盾 GCD 基本都读条
        //    （学者鼓舞激励之策 2 秒读条；贤者诊断是瞬发，这条对它自动放行）。
        //
        //    审计发现这里**漏了**这一行 —— 同文件的 Res_HealAoEGcd 和
        //    Res_HealSingleGcd 都加了，只有单体盾漏掉。
        //    后果：学者移动中反复把读条盾塞进 slot、被中断、再塞
        //    （正是 `SpellUtil.移动中能放` 那段注释里记的用户实测问题）。
        if (!SpellUtil.移动中可用(_t.单体盾)) return -7;

        return SpellUtil.可用(_t.单体盾) ? 3 : -1;
    }

    /// <summary>
    /// 选盾的目标。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 修正说明（与 Res_SingleHoT 同一类问题）★
    ///
    ///  原来**只给坦克**：
    ///      var 坦克 = HealTargetHelper.血量最低的坦克(该铺 ? 1f : 0.7f);
    ///      if (坦克 == null) return -1;        // ← 没坦克就整个不触发
    ///
    ///  但 `_t.单体盾` 配的是：
    ///      · 学者 → **鼓舞激励之策**（学者核心单盾）
    ///      · 贤者 → **诊断**
    ///  这两个技能在游戏里都是给**任何要挨打的人**的，不是坦克专属。
    ///  于是没坦克的场景（单人 / 特殊内容）**学者核心单盾永远不放** ——
    ///  静默失效，不报错。
    ///
    ///  ── 为什么这个"坦克替身"写法反复出问题 ──
    ///    上面那条盾判断的注释里已经记过一次：
    ///    "曾经写成'血量最低的坦克有盾 -> return -3'，那是个真 bug"。
    ///    同一个思路（拿坦克当全队的替身）错了两次，所以这次彻底按
    ///    "**先坦克、后其他人**"的两层来写，而不是只留坦克。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private IBattleChara? 选目标(bool 该铺)
    {
        try
        {
            // ── ① 坦克优先（盾给坦克收益最稳）──
            var 坦克 = HealTargetHelper.血量最低的坦克(该铺 ? 1f : 0.7f);
            if (坦克 != null) return 坦克;

            // ── ② 没有坦克 / 坦克不需要 → 给血线最低的那个人 ──
            //
            //   ⚠️ 这一层原来缺了。没它的话，学者在无坦克场景下
            //      **一次鼓舞都放不出来**。
            var 阈值 = 该铺 ? 0.95f : HealSettings.Instance.单体治疗阈值;
            var 队友 = HealTargetHelper.最低血量队友(阈值);

            if (队友 != null && 队友.GameObjectId != Core.Me.GameObjectId && 队友.可以治())
                return 队友;

            return null;
        }
        catch
        {
            return null;
        }
    }

    public void Build(Slot slot)
    {
        var 该铺 = 减伤Helper.即将来大伤害();

        // ⚠️ **必须和 Check 同源**（开发约定 F③）：判谁就放谁，
        //    否则会出现"Check 说该给 A 放盾，Build 却给了 B"。
        var 目标 = 选目标(该铺);
        if (目标 == null) return;

        if (_t.护盾前置 != 0 && !JobApiHelper.均衡中)
        {
            var pre = SpellUtil.Get(_t.护盾前置);
            if (pre != null) slot.Add(pre);
        }

        // 用当前形态：贤者的"诊断"在均衡状态下会变成"均衡诊断"
        var 盾 = SpellUtil.当前形态(_t.单体盾);
        if (盾 == null) return;
        slot.Add(new Spell(盾.Id, 目标));
    }
}
