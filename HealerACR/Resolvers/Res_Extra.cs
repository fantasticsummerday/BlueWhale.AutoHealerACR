using AEAssist.CombatRoutine;
using HealerACR.Common;
using HealerACR.Timeline;

namespace HealerACR.Resolvers;

// ============================================================================
//  补上"同类 ACR 有而我没有"的几个技能。
//
//  对照来源：反编译 目标 dll 的类型表看到它有
//    Scholar_WhisperingDawn / Scholar_FeyIllumination / AST.Horoscope
//  这三个东西我原先没有，技能 ID 都是从游戏 Action 表 dump 出来核对过的。
// ============================================================================

/// <summary>
/// 仙光的低语（Whispering Dawn，学者 20 级）。
/// 小仙女给的群体 HoT —— 不占自己的 GCD，掉血就能放。
/// </summary>
public class SCH_WhisperingDawn : ISlotResolver
{
    private static uint 技能 => SpellIds.取("仙光的低语");

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("群奶")) return -101;
        if (!HealQt.GetQt("小仙女", true)) return -103;
        if (!HealQt.GetQt("低语")) return -104;      // 每技能开关（复刻 shiyuvi）
        if (!SpellUtil.已解锁(技能)) return -2;

        // ══════════════════════════════════════════════════════════════
        //  ★ 表外审计补的两条（参考 `仙光的低语.txt` 有，我们原来没有）
        //
        //  [!] ① **身上已经有低语 HoT(315) 就别再放** ⇒ -8
        //        参考 IL：`IL_004d ldc.i4 315` / `IL_0052 HasAura` / `IL_0057 ldc.i4.s -8`
        //        HoT 是"叠时间不叠效果"的 —— 重复放只延长，不增加治疗量 ✗
        //  [!] ② **炽天使快结束了就让路**（`IsSeraphEndingSoon(3000)`）⇒ -5
        //        参考 IL：`IL_00ba ldc.i4 3000` / `IsSeraphEndingSoon` / `ldc.i4.s -5`
        //        炽天使马上要消失，这时候再叫小仙女做动作会**用不出来**（白占一拍）。
        // ══════════════════════════════════════════════════════════════
        try
        {
            if (AuraIds.仙光的低语 != 0 && CharacterExt.我有光环(AuraIds.仙光的低语)) return -8;
        }
        catch { }

        try
        {
            // [!] `IsSeraphEndingSoon(3000)` 的形参名是 **thresholdMs**（参考 IL 直读）——
            //     所以这里比的是**毫秒**。我们读的是 AEAssist 的
            //     `JobApi_Scholar.SeraphTimer`（`JobApiHelper.炽天使剩余`），
            //     同一个字段在参考里也当毫秒用（`IsSeraphActive()` 只判 `> 0`）。
            //     ⚠️ 若实机发现炽天使快结束时这条不触发，先查这个字段的单位。
            if (JobApiHelper.炽天使剩余 > 0 && JobApiHelper.炽天使剩余 <= 3000) return -5;
        }
        catch { }

        var s = HealSettings.Instance;
        var 要求人数 = HealTargetHelper.群疗能力技人数要求(s.群奶最少人数);
        // ★ 用**这个技能自己的**阈值（`治疗阈值表`），查不到才回落统一群疗阈值 ★
        //   参考：shiyuvi WhisperingDawn 0.7 ｜ youshu 低语 70
        var 本技血线 = 治疗阈值表.取(技能, s.群体治疗阈值);
        // ★ 以小仙女为圆心数人（表外审计 C3）：低语是召唤物技能，范围圆心是仙女不是玩家
        if (HealTargetHelper.小仙女中心低于阈值人数(本技血线, 20f) < 要求人数) return -1;

        return SpellUtil.可用(技能) ? 17 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>
/// 异想的幻光（Fey Illumination，学者 40 级）。
/// 小仙女给的**群体减伤 + 治疗量提升**，所以用法偏"减伤"：
/// boss 要打 AOE 之前铺，或者团队已经在掉血时补。
/// </summary>
public class SCH_FeyIllumination : ISlotResolver
{
    private static uint 技能 => SpellIds.取("异想的幻光");

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("小仙女", true)) return -103;
        if (!HealQt.GetQt("幻光")) return -104;      // 每技能开关（复刻 shiyuvi）
        if (!SpellUtil.已解锁(技能)) return -2;

        var 要来伤害 = TimelineManager.未来有减伤(4.0) || 减伤Helper.即将来大伤害();
        // ★ 用这个技能自己的阈值 ★
        //   ⚠️ 参考实现里 `异想的幻光` 是「减伤 + 治疗量提升」，触发点该比普通群疗更早。
        //      表里目前没登记它 -> 回落统一群疗阈值（行为不变），
        //      要改的话在 `治疗阈值表` 里加一条即可。
        var 本技血线 = 治疗阈值表.取(技能, HealSettings.Instance.群体治疗阈值);
        // ★ P2·半径/口径：以**小仙女为圆心 + 20 米**数人（参考 `CountLowEffectiveHpPartyMembersWithin20`）
        //   幻光是仙女技能，仙女被 Place 到别处时不改圆心就算错人；20 米对齐参考口径。
        var 团队掉血 = HealTargetHelper.小仙女中心低于阈值人数(本技血线, 20f)
                       >= HealTargetHelper.群疗能力技人数要求(HealSettings.Instance.群奶最少人数);

        if (!要来伤害 && !团队掉血) return -1;

        return SpellUtil.可用(技能) ? 16 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>
/// 天宫图（Horoscope，占星 76 级）。
///
/// 机制：先给全队挂一层"待触发治疗"，之后用阳星/阳星相位（或再按一次天宫图）
/// 才会结算成实际治疗。所以**用法跟群盾一样 —— 伤害来之前提前铺**。
/// 铺完不用管，后面的群奶会自动把它触发掉。
///
/// ══════════════════════════════════════════════════════════════════
///  ⚠️ 修过两个判定错误（现象："占星起手循环多了天宫图，这是治疗技能"）：
///
///  ① **木桩模式不该放** —— 原来写着
///        `if (木桩模式) return SpellUtil.可用(技能) ? 13 : -1;`
///     意思是"木桩也要放（练循环用）"。但按 开发约定.md ⑤ 的分类：
///        · 纯治疗 / 减伤 → **拦**
///        · 资源循环 / 输出 / 团辅 → 放
///     天宫图是**纯治疗**（不给任何资源、不打伤害），属于"拦"那一类，
///     而且它不占输出节奏、没有"练循环"的价值。**这是分类错了，不是漏判。**
///
///  ② **脱战 / 开场不该放** —— `即将来大伤害()` 是"boss 正在读 AOE 条"，
///     而**进战斗的第一个 AOE 读条往往就是开场的那个** ——
///     于是刚接怪、全队满血、伤害还没落地，天宫图就交出去了（60 秒 CD 白费）。
///     而且它铺的是"受到治疗才触发的预备"，满血时铺等于空铺。
///     所以加：**必须在战斗中，且真的有人需要治疗**。
///
///  这两条的修法都遵循同一个原则：**治疗技能只在该治疗的时候放**。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class AST_Horoscope : ISlotResolver
{
    private static uint 技能 => SpellIds.取("天宫图");

    public int Check()
    {
        // ⚠️ 木桩拦掉（纯治疗 —— 见开发约定 ⑤ 的分类表）
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("群奶")) return -101;
        // ★ 职业级 QT（表 #66）—— 参考给天宫图单开了一个开关
        if (!HealQt.GetQt("天宫图", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        var s = HealSettings.Instance;
        // ★ 读本技能登记值（表里 0.75；参考 youshu 天宫图阈值 75）
        var 基础阈值 = 治疗阈值表.取(技能, s.群体治疗阈值);
        var 要求人数 = HealTargetHelper.群疗能力技人数要求(s.群奶最少人数);

        // ══════════════════════════════════════════════════════════════
        //  ★ 表外审计 A6：**引爆分支**（原来只会铺、从不引爆，400 威力白存）★
        //
        //  [!] 天宫图的机制是「先铺、再引爆」：
        //       铺下(16557) → 身上出「天宫图」(1890，200 威力) →
        //       再放阳星/阳星相位 → 升级成「阳星天宫图」(1891，400 威力) →
        //       再按一下才把存的治疗结算出去（游戏把按钮换成 16558）。
        //
        //  [!] 原来这里只有「时间轴要来才铺」，铺完**从没有引爆那条路**
        //      ⇒ 存的治疗到期自动消失，等于白放一个 60 秒 CD 的群疗 ✗
        //
        //  [!] 参考 `天宫图.txt:27-57`（IL 直读）：
        //        ① 有 1890（基础）→ 阈值 = 天宫图阈值 - 35 → 引爆(return 9)
        //        ② 有 1891（升级）→ 阈值 = 天宫图阈值        → 引爆(return 8)
        //        ③ 都没有          → 铺(16557, return 7)
        //      两者都用 `当前形态` 去放（按钮会自动换成引爆用的 16558）。
        // ══════════════════════════════════════════════════════════════

        // ① 已铺「天宫图」（1890，200 威力）：血线更低（阈值 -0.35）才值得引爆
        if (AuraIds.天宫图 != 0 && CharacterExt.我有光环(AuraIds.天宫图))
        {
            var 引爆阈值 = Math.Clamp(基础阈值 - 0.35f, 0f, 1f);
            if (HealTargetHelper.低于阈值人数(引爆阈值, 30f) < 要求人数) return -8;
            return 9;   // 引爆
        }

        // ② 已铺「阳星天宫图」（1891，400 威力）：到线就引爆
        if (AuraIds.阳星天宫图 != 0 && CharacterExt.我有光环(AuraIds.阳星天宫图))
        {
            if (HealTargetHelper.低于阈值人数(基础阈值, 30f) < 要求人数) return -7;
            return 8;   // 引爆
        }

        // ③ 没铺：预铺（原有逻辑）
        // ⚠️ 脱战不放：没接怪就没有"即将到来的伤害"可言，
        //    而且满血铺预备等于空铺。
        if (!CharacterExt.我在战斗()) return -4;

        var 要来了 = TimelineManager.未来有减伤(4.0) || 减伤Helper.即将来大伤害();
        if (!要来了) return -1;

        if (HealTargetHelper.低于阈值人数(基础阈值, 20f) < 要求人数) return -5;

        return SpellUtil.可用(技能) ? 7 : -1;
    }

    public void Build(Slot slot)
    {
        // ⚠️ 用当前形态：铺下后游戏会把按钮换成引爆技（16558），
        //    所以「铺」和「引爆」在这里是同一个动作、交给游戏自己换形态（表外审计 A6）。
        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;
        slot.Add(new Spell(spell.Id, SpellTargetType.Self));
    }
}
