using System;
using System.Collections.Generic;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// **治疗间隔** —— 同一个目标短时间内被治疗两次时，把两次都写进日志（2026-10-03 新增）。
///
/// ══════════════════════════════════════════════════════════════════════
///  [!] 为什么需要（用户实测：「重复奶」，但没法量化）：
///      原来只能凭感觉说"重复奶"，日志里没有任何一行能证明
///      —— 哪两次、间隔多久、分别是什么技能 ✗
///
///  [!] 做法：挂在**施法成功事件**上（AEAssist 的 `MemApiSpellCastSuccess`，
///      它同时给出 `LastTarget`），所以**不用改任何决策代码**：
///      每次真的把技能放出去时记一笔，若同一目标在窗口内第二次被照顾 ⇒ 打一行。
///
///  [!] ⚠️ 第一版是**保守的启发式**：只按"同一目标 + 时间窗"判，
///      不区分技能类别（不猜哪些算"奶"）。所以它会给**提示**，不是结论 ——
///      但足以把"重复奶"从感觉变成可数的行数 + 具体技能对 ✓
///
///  [!] 只读观察：不参与决策，不改施放行为。
/// ══════════════════════════════════════════════════════════════════════
public static class 治疗间隔
{
    /// <summary>窗口：同一目标在这个时间内再次被放技能 ⇒ 记一行（默认 4 秒 ≈ 1.5 个 GCD）。</summary>
    public const int 重复窗口毫秒 = 4000;

    private static readonly Dictionary<ulong, (uint 技能, long 时刻, string 目标名)> _上次 = new();

    /// <summary>由施法成功事件调用。</summary>
    public static void 记一次(uint 技能, ulong 目标Id, string 目标名)
    {
        try
        {
            if (技能 == 0) return;

            var 现在 = TimeHelper.Now();

            if (目标Id != 0 && _上次.TryGetValue(目标Id, out var 旧))
            {
                var 间隔 = 现在 - 旧.时刻;
                if (间隔 >= 0 && 间隔 <= 重复窗口毫秒)
                {
                    LogHelper.Info($"[HealerACR] ★ 同一目标短时间内被照顾两次 ★ " +
                                   $"目标={目标名}({目标Id})" +
                                   $"｜间隔={间隔 / 1000.0:F1}s" +
                                   $"｜上一次={旧.技能}｜这一次={技能}");
                }
            }

            if (目标Id != 0) _上次[目标Id] = (技能, 现在, 目标名);
        }
        catch { }
    }

    /// <summary>给调试窗用的一句话（最近一次记录）。</summary>
    public static string 描述()
    {
        try
        {
            if (_上次.Count == 0) return "（还没有记录）";
            return $"已记录 {_上次.Count} 个目标";
        }
        catch { return "（读不到）"; }
    }
}
