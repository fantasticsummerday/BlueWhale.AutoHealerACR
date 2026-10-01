using AEAssist;
using AEAssist.Extension;
using HealerACR.Common;

namespace HealerACR.Common;

/// <summary>
/// **减伤乘数** —— 把"队友身上有哪些减伤"折算成一个 0~1 的乘数。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 它解决什么问题 ★
///
///    `伤害预测` 原来是**纯观察外推** —— 只看"过去掉血多少"，
///    **完全不看减伤状态**。于是它回答不了用户真正要的那个问题：
///
///        "如果我现在铺个野战治疗阵，这一下会掉多少？"
///        ⇒ 它不知道减伤会改变结果，所以答不出来。
///
///  ★ 怎么算 ★
///
///    观察到的伤害 = 机制原始威力 x 当时的减伤乘数
///    => 反解：机制原始威力 = 观察到的伤害 / 当时的减伤乘数
///    => 预测：预计伤害     = 机制原始威力 x **当前**减伤乘数
///
///    这样就能比较"交减伤 / 不交减伤"的差别 —— 也就是"该不该交"的判据。
///
///  ★ 减伤百分比从哪来 ★
///
///    `减伤状态表`（由 `tools/MitigationDump` 从游戏 Status 表生成）：
///        Description 含「减轻所受到的伤害」-> ParamModifier 负值 = 减伤百分比
///    实测核对：野战治疗阵 -10% / 命运之轮 -10% / 擢升 -10% / 铁壁 -10%
///
///  ⚠️ **多个减伤是乘算不是加算**（游戏机制）：
///        两个 -10% 一起 ≠ -20%，而是 1-(0.9x0.9) = -19%
///     算成加算会**低估**承受的伤害。这里按乘算。
///
///  ⚠️ **护盾不在乘数里** —— 盾是"先扣盾再扣血"，不是百分比减伤，
///     所以它由调用方按"有效血量比例"（含盾）单独处理。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 减伤乘数
{
    /// <summary>乘数下限 —— 防止极端情况算出 0（那会让预测除零）。</summary>
    private const float 下限 = 0.10f;

    /// <summary>
    /// **这个队友当前的减伤乘数**（1.0 = 没有减伤；0.9 = 少受 10%）。
    ///
    /// ⚠️ 只看**在增伤/减伤类**的状态 —— 通过 `减伤状态表` 判定，
    ///    不靠 buff 名字猜（名字有重名、有版本差异）。
    /// </summary>
    public static float 取(IBattleChara? 目标)
    {
        try
        {
            if (目标 == null || !目标.对象有效()) return 1f;

            var 乘数 = 1f;
            var 有效 = 0;

            foreach (var kv in 减伤状态表.减伤)
            {
                bool 有;
                try { 有 = 目标.HasAura(kv.Key); } catch { continue; }
                if (!有) continue;

                var 减 = Math.Clamp(kv.Value, 0, 95) / 100f;
                乘数 *= (1f - 减);
                有效++;

                // 防御性上限：叠到 10 个以上基本不可能，出现就是数据有问题
                if (有效 > 16) break;
            }

            return MathF.Max(下限, 乘数);
        }
        catch
        {
            return 1f;      // 读不到 → 当作没有减伤（保守：预测伤害偏高，会多治）
        }
    }

    /// <summary>自己当前的减伤乘数</summary>
    public static float 取自己() => 取(Core.Me as IBattleChara);

    /// <summary>
    /// **如果再加一个减伤 buff，乘数会变成多少** —— 用来比较"交不交"。
    ///
    /// 用于回答："现在交野战治疗阵，这一下会从 x% 降到 y%"
    /// </summary>
    public static float 加上(IBattleChara? 目标, uint 减伤Buff)
    {
        var 当前 = 取(目标);
        try
        {
            if (减伤状态表.减伤.TryGetValue(减伤Buff, out var 百分比))
            {
                var 减 = Math.Clamp(百分比, 0, 95) / 100f;
                return MathF.Max(下限, 当前 * (1f - 减));
            }
        }
        catch { }
        return 当前;
    }

    /// <summary>有没有任何减伤（给日志 / AI 描述用）</summary>
    public static bool 有减伤(IBattleChara? 目标) => 取(目标) < 0.999f;

    /// <summary>
    /// **把队友身上的减伤打成人能读的一行**（诊断用）。
    ///
    /// 形如：`野战治疗阵-10% x 命运之轮-10% => x0.81`
    /// </summary>
    public static string 描述(IBattleChara? 目标)
    {
        try
        {
            if (目标 == null || !目标.对象有效()) return "";

            var 部分 = new List<string>();
            foreach (var kv in 减伤状态表.减伤)
            {
                bool 有;
                try { 有 = 目标.HasAura(kv.Key); } catch { continue; }
                if (有) 部分.Add($"-{kv.Value}%");
            }

            if (部分.Count == 0) return "";

            var 名 = "";
            try { 名 = 目标.Name.ToString(); } catch { }
            return $"{名} 减伤 x{取(目标):F2}（{string.Join(" ", 部分)}）";
        }
        catch
        {
            return "";
        }
    }
}
