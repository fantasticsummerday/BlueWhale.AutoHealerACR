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
        if (!SpellUtil.已解锁(技能)) return -2;

        var s = HealSettings.Instance;
        var 要求人数 = HealTargetHelper.群疗能力技人数要求(s.群奶最少人数);
        // ★ 用**这个技能自己的**阈值（`治疗阈值表`），查不到才回落统一群疗阈值 ★
        //   参考：shiyuvi WhisperingDawn 0.7 ｜ youshu 低语 70
        var 本技血线 = 治疗阈值表.取(技能, s.群体治疗阈值);
        if (HealTargetHelper.低于阈值人数(本技血线, 20f) < 要求人数) return -1;

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
        if (!SpellUtil.已解锁(技能)) return -2;

        var 要来伤害 = TimelineManager.未来有减伤(4.0) || 减伤Helper.即将来大伤害();
        // ★ 用这个技能自己的阈值 ★
        //   ⚠️ 参考实现里 `异想的幻光` 是「减伤 + 治疗量提升」，触发点该比普通群疗更早。
        //      表里目前没登记它 -> 回落统一群疗阈值（行为不变），
        //      要改的话在 `治疗阈值表` 里加一条即可。
        var 本技血线 = 治疗阈值表.取(技能, HealSettings.Instance.群体治疗阈值);
        var 团队掉血 = HealTargetHelper.低于阈值人数(本技血线)
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
        if (!SpellUtil.已解锁(技能)) return -2;

        // ⚠️ 脱战不放：没接怪就没有"即将到来的伤害"可言，
        //    而且满血铺预备等于空铺。
        if (!CharacterExt.我在战斗()) return -4;

        // 时间轴预报 / boss 读条 → 提前铺
        var 要来了 = TimelineManager.未来有减伤(4.0) || 减伤Helper.即将来大伤害();
        if (!要来了) return -1;

        // ⚠️ 全队满血时别铺 —— 它是"受治疗才触发的预备"，
        //    没人需要治疗就等于空铺（还白搭一个 60 秒 CD）。
        var 阈值 = HealSettings.Instance.群体治疗阈值;
        var 要求人数 = HealTargetHelper.群疗能力技人数要求(HealSettings.Instance.群奶最少人数);
        if (HealTargetHelper.低于阈值人数(阈值, 20f) < 要求人数) return -5;

        // 已经铺过就不重复（buff id 通常和技能一致）
        if (CharacterExt.我有该技能的Buff(技能)) return -3;

        return SpellUtil.可用(技能) ? 13 : -1;
    }

    public void Build(Slot slot)
    {
        // 用当前形态：这类"预铺"技能有可能被游戏替换
        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;
        slot.Add(new Spell(spell.Id, SpellTargetType.Self));
    }
}
