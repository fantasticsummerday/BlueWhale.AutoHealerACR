using AEAssist;
using AEAssist.CombatRoutine.Module;
using HealerACR.Common;

namespace HealerACR.Resolvers;

/// <summary>
/// **药尾声补刀** —— 爆发药快过期时，用「即刻咏唱 + 瞬发填充」把最后一发塞进窗口。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 参考实现 A 四职业统一的做法（对照分析发现的缺口）★
///
///  ── 它们的实现（IL 直证）──
///    白魔 `即刻闪灼` / 学者 `即刻极炎法` /
///    贤者 `即刻注药` / 占星 `即刻落陷凶星`
///
///    四条判据完全一样：
///        `HasAura(49 强化药) && BuffTimeLessThan(49, 3000)`
///    命中后放「即刻咏唱(7561) + 自己的主力输出技」，权重都是 **15**。
///
///  ── 为什么值得做 ──
///    爆发药不是"开打前磕一颗"，它是**整个爆发窗口的时钟**。
///    药快过期时用即刻把最后一发塞进去 = 白赚一个 GCD 的增伤。
///    我们原来磕完药就当没这回事（`AuraIds` 里连 49 都没有）。
///
///  ── 判据设计上的两个取舍 ──
///    ① **只在"药尾声"用**（剩余 &lt; 3000ms），不是整个窗口都用。
///       整个窗口都用会导致：只要嗑了药就无脑即刻，即刻被浪费在窗口开头
///       ——而开头本来就有足够时间读条，不需要即刻。
///    ② **不判断是否在移动**：移动中当然更该用（读条会被打断），
///       但站着用也不亏（省下读条时间 = 多塞一发）。
///       参考实现也是两个场景都用同一条判据。
///
///  ⚠️ 权重给 15：介于 `Res_BaseDamage`（1）和 `Res_Dot`（更高）之间 ——
///    它应该**抢在普通读条输出之前**，但不该抢在 DoT 续期之类的前面。
///    参考实现这四个技能权重也都是 15（IL 直读）。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class Res_PotionTailDamage : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_PotionTailDamage(JobSpellTable t) => _t = t;

    /// <summary>即刻咏唱（7561）—— 把下一个读条技变瞬发</summary>
    private static uint 即刻 => SpellIds.取("即刻咏唱");

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;
        if (蓝量.低蓝停手()) return -9;              // 蓝留给治疗

        var 技 = _t.药尾声填充技;
        if (技 == 0) return -102;                     // 这个职业没配
        if (!SpellUtil.已解锁(技)) return -2;

        // ① 药在不在、是不是尾声 —— 这是这条判据的**全部意义**
        if (!AuraIds.在药尾声()) return -1;

        // ② 输出环境
        if (HealTargetHelper.当前目标() == null) return -1;
        if (!技能数据.打得到(HealTargetHelper.当前目标())) return -6;

        // ⚠️ 基础输出被游戏替换掉了（白魔神速期间 = 闪飒预备）→ 现在该打闪飒，
        //    不是我们这条。硬放会被游戏拒绝。
        if (_t.有特殊输出形态) return -5;

        // ③ 没有即刻、也没有原生瞬发 → 打不出"补刀"
        //
        //    ⚠️ 两种情况都算可用：
        //       · 自己**已有即刻 buff**(167) → 直接用填充技
        //       · 即刻**可以放**(7561 转好了) → 先放即刻再放填充技
        //    学者这类填充技**本来就瞬发**的职业，即使没即刻也能补刀，
        //    所以第三个条件是"填充技自己就瞬发"。
        var 有即刻buff = Core.Me.有该技能的Buff(AuraIds.即刻);
        var 即刻可放 = 即刻 != 0 && SpellUtil.可用(即刻);

        // ⚠️ **必须用 `SpellUtil.是瞬发()`，不能用 `移动中可用()`** ——
        //    后者站着不动时对任何技能都返回 true（见 `是瞬发` 的注释），
        //    会把读条技判成瞬发 → 该放即刻的时候不放 → 补刀变硬读条。
        var 技本身瞬发 = SpellUtil.是瞬发(技);

        if (!有即刻buff && !即刻可放 && !技本身瞬发) return -1;

        // ④ 填充技本身要能放
        var spell = SpellUtil.当前形态(技);
        return spell != null && spell.IsReadyWithCanCast() ? 15 : -1;
    }

    public void Build(Slot slot)
    {
        // ⚠️ **必须和 Check 同源**（开发约定 F③）：
        //    Check 判"有没有即刻 / 要不要先放即刻"，Build 就得放同样的东西。
        var 技 = _t.药尾声填充技;
        if (技 == 0) return;

        var 有即刻buff = Core.Me.有该技能的Buff(AuraIds.即刻);
        var 即刻可放 = 即刻 != 0 && SpellUtil.可用(即刻);
        var 技本身瞬发 = SpellUtil.是瞬发(技);   // ⚠️ 不能用 移动中可用（见 Check 的注释）

        // 没有即刻 buff 但即刻能放 → 一个 slot 里先即刻、后填充技
        //   （即刻是能力技，和后面的 GCD 同一帧放得出去）
        if (!有即刻buff && 即刻可放 && !技本身瞬发)
        {
            var 即刻Spell = SpellUtil.Get(即刻);
            if (即刻Spell != null) slot.Add(即刻Spell);
        }

        var spell = SpellUtil.当前形态(技);
        if (spell != null) slot.Add(spell);
    }
}
