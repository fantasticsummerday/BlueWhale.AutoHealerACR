using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Rotations;

/// <summary>
/// **贤者「群 DOT」—— 均衡失衡（AOE 毒）**。
///
/// ══════════════════════════════════════════════════════════════════
///  [!] 原来**完全没有这条路线**（表 #104 / #105）：
///      · `AuraIds` 里连 3897「均衡失衡」都没登记 ⇒ AI 不知道"它是一个 DoT"
///      · 也没有 resolver 去交它 ⇒ 3 怪以上时少一条输出路线
///
///  ── 机制（和均衡注药同一套"形态"机制）──
///      先按 `均衡(24290)`（= `护盾前置`，别人的叫法不同）进入均衡状态，
///      再按 `失衡(24297)` —— 这两个 id 是**技能位**，
///      真正放出去的是游戏 `CheckActionChange` 换出来的形态：
///        均衡注药 24293（单体毒）/ 均衡失衡 24315（群毒）
///      所以 Build 必须先 `AddSpell2NextSlot(均衡)` 再排失衡 ——
///      和 `预铺起手` 里"先护盾前置再单体盾"是同一个套路。
///
///  ── 判据（IL 直读 `群DOT.txt`）──
///    ① QT「群DOT」开着、等级 ≥ 30、没被静默
///    ② 有目标、目标不是"打上去没用"（无敌/反射）
///    ③ **等级 ≥ 82 且 5 米内 ≥ 3 个敌人**（`ShouldRefreshAoeDot` 的前置）
///    ④ 目标距离 **≤ 5 米**（群毒的落点范围就是 5 米）
///    ⑤ 目标不"快死了"、不在 DoT 黑名单
///    ⑥ 目标身上**已有的群毒剩余 > 4000ms** ⇒ 不用补
///    ⑦ 1000ms 内刚下过 ⇒ 不重复（本地施放记录）
///    ⑧ 刷新判据两条（任一成立就补）：
///         · 25 米内有 **2 个以上"完全没毒"**的敌人 ⇒ 打一发铺开
///         · 有敌人的群毒剩余 **≤ 4000ms** ⇒ 该续了
///
///  [!] 返回值给 60（高于普通 DoT 的 6 与多目标 DoT 的 4）——
///      但**真正决定顺序的是槽位行号**：它排在 `Res_MultiDot` / `SGE_Dot` 之前。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class SGE_AoeDot : ISlotResolver
{
    /// <summary>群毒技能位 = 失衡（均衡状态下变成 均衡失衡）</summary>
    private static uint 技能 => SpellIds.取("失衡");

    /// <summary>均衡（进入均衡状态的那个 oGCD）</summary>
    private static uint 均衡 => SpellIds.取("均衡");

    /// <summary>等级门槛：82 级才有群毒（失衡II 档），30 级才有均衡本身</summary>
    private const int 等级门槛 = 82;

    /// <summary>群毒的落点半径（米）</summary>
    private const float 落点半径 = 5f;

    /// <summary>敌人数量门槛（5 米内至少这么多才值得群毒）</summary>
    private const int 敌人门槛 = 3;

    /// <summary>剩余少于它就该续（毫秒）</summary>
    private const int 续期线毫秒 = 4000;

    /// <summary>刚下过就不重复的窗口（毫秒）</summary>
    private const int 去重毫秒 = 1000;

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("群DOT", true)) return -100;
        if (!HealQt.GetQt("输出", true)) return -101;
        if (技能 == 0 || 均衡 == 0) return -102;

        try
        {
            if (CharacterExt.我的等级() < 等级门槛) return -100;
        }
        catch { }

        if (!SpellUtil.已解锁(技能)) return -2;
        if (!SpellUtil.已解锁(均衡)) return -2;

        var 目标 = 输出目标.选();
        if (目标 == null || !目标.对象有效()) return -1;

        // ② 打上去没用（无敌 / 魔法反射）
        if (敌人状态.攻击无效(目标)) return -1;

        if (!SpellUtil.可用(技能)) return -3;

        // ⑤ 快死的怪不值得上毒；黑名单（免疫毒）同理
        if (HealTargetHelper.目标快死了(目标)) return -200;
        if (!Dot黑名单.可以上Dot(目标)) return -10;

        // ④ 群毒的落点半径是 5 米 —— 太远打不到
        try
        {
            if (目标.Distance(Core.Me!) > 落点半径) return -200;
        }
        catch { }

        // ③ 5 米内敌人不够 ⇒ 群毒不划算（参考的门槛）
        try
        {
            if (HealTargetHelper.自身周围敌人数量(落点半径) < 敌人门槛) return -200;
        }
        catch { }

        // ⑥ 目标身上已有的群毒还剩得久 ⇒ 不用补
        if (已有群毒剩余毫秒(目标) > 续期线毫秒) return -1;

        // ⑦ 刚下过（本地记录，服务器状态滞后时靠它）
        if (本地施放记录.刚放过(技能, 去重毫秒)) return -1;

        // ⑧ 刷新判据
        if (!该刷新(目标)) return -1;

        return 60;
    }

    public void Build(Slot slot)
    {
        // ⚠️ **先均衡再失衡** —— 均衡是 oGCD，必须"插"进下一槽而不是自己占一个 GCD。
        //    和 `预铺起手` 里"先护盾前置再单体盾"同一个套路。
        try
        {
            if (!JobApiHelper.均衡中)
            {
                var pre = SpellUtil.Get(均衡);
                if (pre != null) AI.Instance.BattleData.AddSpell2NextSlot(pre);
            }
        }
        catch { }

        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;

        slot.Add(new Spell(spell.Id, 输出目标.选()));

        // 记账：本地去重 + DoT 保险丝（和 Res_Dot / Res_MultiDot 共用）
        try { 本地施放记录.记(技能); } catch { }
        try { Dot补判.记一次施放(); } catch { }
    }

    // ==================================================================
    //  判据
    // ==================================================================

    /// <summary>目标身上**群毒**（3897）还剩多少毫秒（没有返回 0）</summary>
    private static int 已有群毒剩余毫秒(IBattleChara 目标)
    {
        // ★ 入口判有效性：读游戏对象属性会因【已释放对象】触发原生访问违例（穿 catch）
        if (目标 == null || !目标.对象有效()) return 0;

        try
        {
            var id = AuraIds.均衡失衡;
            if (id == 0) return 0;
            if (!目标.HasAura(id)) return 0;

            return (int)目标.我的Buff剩余毫秒(id);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>目标身上有没有**任意**我的毒（单体三档 + 群毒）</summary>
    private static bool 有任意我的毒(IBattleChara 目标)
    {
        // ★ 入口判有效性（同上）
        if (目标 == null || !目标.对象有效()) return false;

        try
        {
            foreach (var b in 全部毒Ids)
            {
                if (b != 0 && 目标.HasLocalPlayerAura(b)) return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>贤者的全部毒 buff（单体三档 + 均衡失衡）</summary>
    private static uint[] 全部毒Ids => new[]
    {
        AuraIds.贤者Dot, AuraIds.贤者DotAlt, AuraIds.贤者DotAlt2, AuraIds.贤者DotAlt3,
        AuraIds.贤者Dot2, AuraIds.贤者Dot1,
        AuraIds.均衡失衡,
    };

    /// <summary>
    /// **该不该打这一发群毒**（IL 直读 `ShouldRefreshAoeDot`）：
    ///   · 25 米内有 **2 个以上"完全没毒"**的敌人 ⇒ 打（铺开）
    ///   · 或 有敌人的**群毒剩余 ≤ 4000ms** ⇒ 打（续期）
    /// </summary>
    private static bool 该刷新(IBattleChara 落点)
    {
        var 没毒的 = 0;

        try
        {
            foreach (var 敌人 in Data.AllHostileTargets)
            {
                if (敌人 == null || !敌人.对象有效()) continue;
                if (敌人.CurrentHp <= 0) continue;
                if (敌人状态.攻击无效(敌人)) continue;

                // 距离：参考按"25 米内"枚举（群毒虽然落点 5 米，
                // 但"值得打一发"的判据看的是战场上还有多少活敌人）
                try { if (敌人.Distance(Core.Me!) > 25f) continue; } catch { }

                if (!有任意我的毒(敌人))
                {
                    没毒的++;
                    if (没毒的 >= 2) return true;
                    continue;
                }

                // 有毒的：看群毒是不是快到期
                var 剩余 = 已有群毒剩余毫秒(敌人);
                if (剩余 > 0 && 剩余 <= 续期线毫秒) return true;
            }
        }
        catch { }

        // 落点本身一个毒都没有时也值得（上面已经数过，这里是兜底）
        try
        {
            if (落点.对象有效() && !有任意我的毒(落点)) return true;
        }
        catch { }

        return false;
    }
}
