using AEAssist.CombatRoutine;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Rotations;

// ============================================================================
//  贤者的输出技能（0.3.6 补）。
//
//  这两个之前一直没实现 —— SpellIds 表里有 ID，但没有任何 resolver 引用它们。
// ============================================================================

/// <summary>
/// 发炎（Phlegma）：贤者的 **GCD 输出技**，对目标造成伤害并治疗周围队友。
///
/// ⚠️ 它有 **2 层充能、40 秒复唱**（从游戏 Action 表 dump 出来的 Confirmed 数据），
///    所以既要防"同一瞬间把两层全交"，又不能间隔太长 —— 不然充能会白白溢出。
///    这里用 2 秒限流：既不会连按，也不至于让充能满着不动。
/// </summary>
public class SGE_Phlegma : ISlotResolver
{
    /// <summary>发炎 III / II / I 的等级链（82 / 72 / 26 级）</summary>
    private static uint 技能 => SpellUtil.取已解锁(
        SpellIds.取("发炎III"), SpellIds.取("发炎II"), SpellIds.取("发炎"));

    private static long 上次发炎;

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;
        if (!HealQt.GetQt("发炎", true)) return -101;
        if (技能 == 0 || !SpellUtil.已解锁(技能)) return -2;

        // 对目标的伤害技，需要选中目标
        if (HealTargetHelper.当前目标() == null) return -1;

        // 发炎 2 层充能（对照 Shiyuvi 的 GetCharges）：
        //   满 2 层尽快交掉防溢出；只剩 1 层时保持较长限流。
        var 充能 = CharacterExt.充能数(技能);
        var 限流 = 充能 >= 2 ? 600 : 2000;
        if (TimeHelper.Now() - 上次发炎 < 限流) return -7;

        // 残血小怪不交（木桩例外）—— 留给下一个目标
        if (!HealTargetHelper.木桩模式 && HealTargetHelper.目标快死了()) return -4;

        var spell = SpellUtil.当前形态(技能);
        return spell != null && spell.IsReadyWithCanCast() ? 4 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;

        slot.Add(spell);
        上次发炎 = TimeHelper.Now();
    }
}

/// <summary>
/// 心神风息（Psyche，92 级）：**能力技**，对目标造成伤害并给周围队友上护盾。
///
/// 属于"输出型能力技"，跟白魔法令一个道理 —— 卡 CD 打，不等掉血。
/// </summary>
public class SGE_Psyche : ISlotResolver
{
    private static uint 技能 => SpellIds.取("心神风息");

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 对目标的伤害技，需要选中目标
        if (HealTargetHelper.当前目标() == null) return -1;

        // 插能力技的时机
        if (!CharacterExt.可以插能力技()) return -6;

        // 残血小怪不交（木桩例外）
        if (!HealTargetHelper.木桩模式 && HealTargetHelper.目标快死了()) return -4;

        return SpellUtil.可用(技能) ? 20 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(spell);
    }
}
