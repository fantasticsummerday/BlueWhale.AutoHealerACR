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

    private static long 上次挂Dot;

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

        if (!该补Dot(target)) return -4;

        var spell = SpellUtil.当前形态(_t.Dot技能);
        return spell != null && spell.IsReadyWithCanCast() ? 6 : -1;
    }

    public void Build(Slot slot)
    {
        var target = HealTargetHelper.当前目标();
        if (target == null) return;

        var spell = SpellUtil.当前形态(_t.Dot技能);
        if (spell == null) return;

        slot.Add(new Spell(spell.Id, target));
        上次挂Dot = TimeHelper.Now();
    }

    private bool 该补Dot(IBattleChara target)
    {
        var 现在 = TimeHelper.Now();

        // 保险丝：刚放过就别连着放。
        // 万一 buff id 对不上（比如客户端版本差异），这条能防止无限补 DoT。
        if (上次挂Dot != 0 && 现在 - 上次挂Dot < 2500) return false;

        // 检查所有等级段的 DoT buff —— 身上有任意一个就算已上 DoT
        var 候选 = _t.所有DotBuff;
        if (候选.Length > 0)
        {
            var 有配置 = false;
            foreach (var b in 候选)
            {
                if (b == 0) continue;
                有配置 = true;

                // ⚠️ 用"剩余时间"而不是"有没有"来判断。
                //    参考同类 ACR 的 HasMyAuraWithTimeleft：
                //    DoT 还剩 20 秒时补上去纯属浪费 GCD，剩 3 秒才该补。
                //    之前只看 HasAura，等于"只要挂着就永远不补"——
                //    DoT 自然断档也发现不了。
                  // ══════════════════════════════════════════════════════
                  //  ⚠️ 必须**先判"有没有"，再判"快没快"** —— 顺序不能反。
                  //
                  //  原因：HasMyAuraWithTimeleft 对"目标身上根本没这个 buff"的
                  //  返回值不可靠。如果直接拿它当判断：
                  //      没 buff → 快没了=false → 判成"还很足" → 不补 DoT
                  //  结果就是**永远不续 DoT**（用户实测：50 级完全不续）。
                  //
                  //  低等级尤其容易踩：50 级用「猛毒菌」(buff 189)，
                  //  和其他档位完全不沾边，一个 false 就直接判"不用补"。
                  //
                  //  参考同类 ACR 的 Scholar_Dot —— 它同时调 HasAura 和
                  //  HasMyAuraWithTimeleft，就是因为单靠时间判断不够。
                  // ══════════════════════════════════════════════════════

                  // ① 这一档压根不在身上 → 换下一档看
                  if (!target.HasLocalPlayerAura(b)) continue;

                  // ② 在身上、而且还能撑一会儿 → 确实不用补
                  // **按"还能放几个 GCD"算，而不是固定秒数**（参考同类 ACR 用 GetAuraTimeleft）
                  //   急速高的时候 GCD 变短，固定秒数会补得太晚导致断档。
                  if (!target.撑不过N个Gcd(b, 2, 1.5f)) return false;

                  // ③ 在身上、而且**撑不过 2 个 GCD** → 该补了，放行
                  //
                  // ⚠️ 这里曾经写成 `return false` —— 和上面那行注释（"该补"）
                  //    **自相矛盾**，等于把唯一能提前续 DoT 的路径也堵死了。
                  //    后果：**DoT 只在彻底掉光之后才补，从不提前续**，
                  //    每轮白丢约一个 GCD 的覆盖时间。
                  //    （纯逻辑 bug，不报错、不崩溃，只能靠对着注释读代码发现。）
                  return true;
            }

            if (有配置) return true;
        }

        // 一个 buff 都没配：按时间兜底
        var 间隔 = Math.Max(6f, HealSettings.Instance.Dot持续时间 - 3f) * 1000f;
        if (上次挂Dot == 0) return true;
        return 现在 - 上次挂Dot >= 间隔;
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
