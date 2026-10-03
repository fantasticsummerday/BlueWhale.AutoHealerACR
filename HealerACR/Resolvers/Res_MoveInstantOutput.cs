using AEAssist;
using AEAssist.CombatRoutine.Module;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Resolvers;

/// <summary>
/// **移动中的即刻输出通道** —— 移动中把即刻咏唱用在最高档的输出 GCD 上。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么需要它 ★
///
///    奶妈的**所有输出 GCD 都是读条的**（占星落陷凶星、白魔闪灼、
///    学者极炎法、贤者注药…全是 1.5 秒读条）。
///    `SpellUtil.移动中能放()` 只看"技能本身有没有读条" ⇒ 移动中**全被挡住**；
///    而 `Res_MoveGcd` 只会退到本职业的「移动填充技」，
///    占星/白魔/贤者**根本没有填这一栏**（空数组）⇒ 移动中一个输出 GCD 都放不出来 ✗
///
///    ⇒ 每次走位白丢一个 GCD 的伤害。而即刻咏唱（60 秒 CD）本来就是
///      "让一个读条技能变瞬发"的，正好用在这里。
///
///  ── 判据 ──
///    ① 在移动 / 空中
///    ② **即刻类 buff 已经在身上**（开即刻是 `Res_移动开即刻` 的活，
///       本 resolver 只用、不开 —— 所以它必须排在开即刻那个**之后**）
///    ③ 有活着的敌人、且在射程内
///    ④ MP 门：落陷凶星这类取值 400~800（见 `蓝量`），别在低蓝时把即刻喂给输出
///
///  ── 打哪一发 ──
///    群体输出（中重力）在**敌人够多**时优先，否则基础输出（落陷凶星）。
///    门槛按等级分档 —— 低等级群体技威力低、怪也少，硬放不如单体。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class Res_MoveInstantOutput : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_MoveInstantOutput(JobSpellTable table) => _t = table;

    /// <summary>Check 里选好的技能，给 Build 用（避免判 A 放 B）</summary>
    private static Spell? 本帧技能;

    /// <summary>100 级时群体输出要几个敌人才值得（低于这个数打单体）</summary>
    private const int AOE门槛_满级 = 3;

    /// <summary>80~99 级时群体输出的门槛</summary>
    private const int AOE门槛_八十级 = 2;

    public int Check()
    {
        本帧技能 = null;

        try
        {
            if (HealTargetHelper.木桩模式) return -300;
            if (!HealQt.GetQt("输出")) return -100;

            // ① 在移动 / 空中
            if (!(SpellUtil.在移动() || 空中检测.在空中)) return -1;

            // ② 即刻类 buff 在身上（开即刻由 Res_移动开即刻 负责）
            if (!有即刻()) return -2;

            // ③ 目标 / 射程
            var 目标 = 输出目标.选();
            if (目标 == null) return -3;

            var 技能 = 选技能(目标);
            if (技能 == null) return -4;

            var 射程 = 技能数据.取有效射程(技能.Id);
            if (!技能数据.打得到(目标, 射程 > 0 ? 射程 : 25f)) return -6;

            // ④ MP 门（低蓝优先留给治疗）
            if (蓝量.低蓝停手()) return -9;

            // ★ 按技能分档的 MP 门：
            //   占星 走位即刻重力/中重力 要求 **1000** 蓝（它自己只要 400）——
            //     那是一次性瞬发资源换来的输出，参考口径是"手里得留够蓝才肯用"。
            //   白魔 走位即刻闪灼 要求 **800** 蓝（即刻路径那一档）。
            if (!技能MP表.蓝够(技能.Id)) return -9;

            if (!技能.IsReadyWithCanCast()) return -8;

            本帧技能 = 技能;
            return 15;
        }
        catch
        {
            return -1;
        }
    }

    public void Build(Slot slot)
    {
        try
        {
            if (本帧技能 != null) slot.Add(本帧技能);
        }
        catch { }
    }

    /// <summary>身上有没有"让读条变瞬发"的 buff（即刻 / 光速 / 连续咏唱…）</summary>
    private static bool 有即刻()
    {
        try
        {
            foreach (var id in AuraIds.瞬发豁免)
            {
                if (id != 0 && CharacterExt.我有光环(id)) return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 这一发打群体还是单体。
    ///
    /// [!] 群体门槛按**等级**分档（不是固定 2）：
    ///     低等级群体技威力低、副本里也凑不出那么多怪，
    ///     硬放群体技反而比单体低 —— 所以低等级要更多怪才换。
    ///     拿不到等级就退回门槛 2（宁可打群体，别空转）。
    /// </summary>
    private Spell? 选技能(IBattleChara 目标)
    {
        try
        {
            var 群体 = _t.群体输出 != 0 ? SpellUtil.当前形态(_t.群体输出) : null;
            var 单体 = _t.基础输出 != 0 ? SpellUtil.当前形态(_t.基础输出) : null;

            if (群体 != null)
            {
                var 敌数 = HealTargetHelper.周围敌人数量(_t.AOE伤害范围);
                var 门槛 = 等级门槛();

                if (敌数 >= 门槛 && 群体.IsReadyWithCanCast()) return 群体;
            }

            if (单体 != null) return 单体;

            return 群体;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>群体输出的敌人门槛（按等级分档）</summary>
    private static int 等级门槛()
    {
        try
        {
            var 等级 = CharacterExt.我的等级();
            if (等级 >= 100) return AOE门槛_满级;
            if (等级 >= 80) return AOE门槛_八十级;
            return 1;   // 低等级：有 1 个敌人也直接打群体（它本来就是当前档位的输出技）
        }
        catch
        {
            return AOE门槛_八十级;
        }
    }
}
