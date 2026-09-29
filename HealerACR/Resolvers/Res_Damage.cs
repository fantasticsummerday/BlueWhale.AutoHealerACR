using AEAssist.CombatRoutine;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Resolvers;

// ============================================================================
//  输出。
//  需求 1：AOE 用 GetMostCanTargetObjects 挑最佳落点
//  需求 2：残血小怪不交爆发
//  需求 5：木桩模式不保守
// ============================================================================
// ============================================================================

/// <summary>
/// 挂 DoT。
///
/// ⚠️ 这里有个坑：**DoT 升级会换 buff id**
///    （占星 烧灼838 → 炽灼843 → 焚灼1881，学者 毒菌179 → 猛毒菌189 → 蛊毒法1895）。
///    只检查一个 buff 的话，满级打最高级 DoT 时永远对不上 → 无限补 DoT。
///    所以走 <see cref="JobSpellTable.所有DotBuff"/> 把所有等级段都查一遍。
///
///    另外还有一根保险丝：刚放过 2.5 秒内不再放，防止任何形式的死循环。
/// </summary>
public class Res_Dot : ISlotResolver
{
    private readonly JobSpellTable _t;

    /// <summary>诊断只打一次的开关（每个 DoT buff 各一条）</summary>
    private static readonly HashSet<uint> _已诊断 = new();

    public Res_Dot(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (!HealQt.GetQt("DOT", true)) return -100;
        if (!HealQt.GetQt("输出", true)) return -101;
        if (蓝量.低蓝停手()) return -9;   // 蓝留给治疗
        if (_t.Dot技能 == 0) return -102;
        if (!SpellUtil.已解锁(_t.Dot技能)) return -2;

        var target = HealTargetHelper.当前目标();
        if (target == null) return -1;

        // ── 视线检查：目标在柱子/墙后面时距离够也打不到 ──
        //    不检查的话会一直"选中了技能但打不出去"，表现为输出卡住。
        //    （兜底偏向放行，见 技能数据.打得到 的说明）
        if (!技能数据.打得到(target)) return -6;

        if (!HealTargetHelper.木桩模式 && HealTargetHelper.目标快死了()) return -3;

        // ── DoT 黑名单（参考同类 ACR 的 DotBlacklistHelper）──
        //   有些怪免疫 DoT 或者吃不上，往它们身上补 = 每 30 秒白费一个 GCD，
        //   而且因为 buff 永远上不去，DoT 会反复触发、把输出循环卡死。
        if (!Dot黑名单.可以上Dot(target)) return -5;

        诊断剩余时间(target);

        // ⚠️ 判断走**共享**的 Dot补判（含防死循环保险丝）——
        //    不要在这里自己写一套：AI 建议那条路也用它，
        //    两边必须看同一份数据（否则就是开发约定 F③ 那个坑）。
        if (!Dot补判.该补(target, _t.所有DotBuff, HealSettings.Instance.Dot持续时间 - 3f))
            return -4;

        var spell = SpellUtil.当前形态(_t.Dot技能);
        return spell != null && spell.IsReadyWithCanCast() ? 6 : -1;
    }

    /// <summary>
    /// **一次性诊断**：把这个 DoT 的"剩余时间"接口实际返回什么打出来。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么需要它 ★
    ///
    ///    用户实测：天辉每 ~4.9 秒放一次（日志里连续 15 次）。
    ///    正常应该是 30 秒一次 —— 说明下面这条判断**恒为真**：
    ///        `撑不过N个Gcd(buff, 2, 1.5)`  → 以为"DoT 快没了"
    ///
    ///    候选根因（光看代码分不出来）：
    ///      ① `GetAuraTimeleft` 返回的其实是"秒×100"(3000) 而不是毫秒(30000)
    ///         → 除以 1000 = 3 秒 → 恒判该补
    ///      ② `fromMe=true` 取不到我挂的 buff → 返回 0/负数 → 恒判该补
    ///      ③ `HasLocalPlayerAura` 失效 → 走"一个 buff 都没配"的兜底分支
    ///
    ///    ⚠️ 所以这里**不管有没有 buff 都打一条** —— 否则情况③
    ///       会因为"没 buff 就 return"而永远看不到输出。
    ///       每个 buff 各一行，第一场战斗打一次就够。
    ///
    ///  确认结论后，把本方法和 `_已诊断` 一起删掉。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private void 诊断剩余时间(IBattleChara target)
    {
        try
        {
            if (_已诊断.Count > 0) return;   // 已经打过一整轮 → 不再刷屏

            var 候选 = _t.所有DotBuff;
            var 描述 = new System.Text.StringBuilder();
            描述.Append($"[HealerACR][DoT诊断] 技能={_t.Dot技能} 候选buff数={(候选?.Length ?? 0)}");

            if (候选 != null)
            {
                foreach (var b in 候选)
                {
                    if (b == 0) continue;

                    var 有 = target.HasLocalPlayerAura(b);
                    var 原始 = target.我的Buff剩余毫秒(b);
                    var 秒 = target.我的Buff剩余秒(b);
                    var 撑不过 = target.撑不过N个Gcd(b, 2, 1.5f);

                    描述.Append($" || buff={b} 在身上={有} 接口返回={原始}" +
                                $" 按毫秒={原始 / 1000f:F1}s 按秒x100={原始 / 100f:F1}s" +
                                $" 剩余秒()={秒:F2} 撑不过2GCD={撑不过}");
                    _已诊断.Add(b);
                }
            }

            LogHelper.Info(描述.ToString());
        }
        catch (Exception e)
        {
            LogHelper.Info("[HealerACR][DoT诊断] 异常：" + e.Message);
        }
    }

    public void Build(Slot slot)
    {
        var target = HealTargetHelper.当前目标();
        if (target == null) return;

        var spell = SpellUtil.当前形态(_t.Dot技能);
        if (spell == null) return;

        slot.Add(new Spell(spell.Id, target));

        // ⚠️ 记到**共享**的 Dot补判 里（不是本类的静态字段）——
        //    保险丝必须两条路径（优先级队列 / AI 建议）共用，
        //    否则 AI 那条能绕过它，等于没装。
        Dot补判.记一次施放();
    }
}

/// <summary>群体输出：需求 1，自动挑能打到最多敌人的目标。</summary>
public class Res_AoEDamage : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_AoEDamage(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;
        if (!HealQt.GetQt("AOE")) return -101;
        if (蓝量.低蓝停手()) return -9;   // 蓝留给治疗
        if (_t.群体输出 == 0) return -102;
        if (!SpellUtil.已解锁(_t.群体输出)) return -2;

        var spell = SpellUtil.当前形态(_t.群体输出);
        if (spell == null) return -1;

        // ⚠️ 直接用 GetMostCanTargetObjects 找"能打到最多敌人"的目标，
        //    而不是"数当前目标周围有几个"。
        //
        //    这个差别在本内小怪场景里很致命：当前目标常常站在怪群**边缘**，
        //    以它为中心数只能数到 2 个 → 判定"不够 3 个"→ 不放 AOE；
        //    而实际上换个目标（怪群中心）能一次打到 5 个。
        //    表现出来就是"明明一群小怪，却在打单体"。
        //
        //    另外这样 Check 和 Build 用的是同一个判断，
        //    不会再出现"Check 说不够、Build 却找到了最佳目标"的不一致。
        IBattleChara? 最佳 = null;
        try
        {
            if (智能选目标.是直线技能(spell.Id))
            {
                最佳 = 智能选目标.按形状选最优(spell.Id, _t.AOE伤害范围, _t.AOE最少敌人数);
            }
            else
            {
            最佳 = TargetHelper.GetMostCanTargetObjects(spell.Id, _t.AOE最少敌人数);
            }
        }
        catch
        {
            // 拿不到就退回"数邻居"的老办法，别因为异常直接不放 AOE
            if (HealTargetHelper.周围敌人数量(_t.AOE伤害范围) < _t.AOE最少敌人数) return -1;
            return spell.IsReadyWithCanCast() ? 5 : -1;
        }

        if (最佳 == null) return -1;

        // 落点也得看得见（AOE 是打在地上的，视线被墙挡住同样打不到）
        if (!技能数据.打得到(最佳)) return -6;

        return spell.IsReadyWithCanCast() ? 5 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(_t.群体输出);
        if (spell == null) return;

        var 目标 = HealTargetHelper.AOE最佳目标(spell.Id, _t.AOE最少敌人数);
        if (目标 != null)
        {
            slot.Add(new Spell(spell.Id, 目标));
            return;
        }

        slot.Add(spell);
    }
}

/// <summary>兜底单体输出。必须是 GCD 队列的最后一个。</summary>
public class Res_BaseDamage : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_BaseDamage(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;
        if (蓝量.低蓝停手()) return -9;   // 蓝留给治疗
        if (!SpellUtil.已解锁(_t.基础输出)) return -2;
        if (HealTargetHelper.当前目标() == null) return -1;

        // 视线被挡就别按了 —— 按了也放不出去，白白占着 GCD 让循环卡住
        if (!技能数据.打得到(HealTargetHelper.当前目标())) return -6;

        // 基础输出被游戏替换掉了（白魔神速期间 = 闪飒预备），硬放会失败
        if (_t.有特殊输出形态) return -5;

        var spell = SpellUtil.当前形态(_t.基础输出);
        return spell != null && spell.IsReadyWithCanCast() ? 1 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(_t.基础输出);
        if (spell != null) slot.Add(spell);
    }
}
