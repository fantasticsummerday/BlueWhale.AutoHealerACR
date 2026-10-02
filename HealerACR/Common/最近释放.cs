using System;
using AEAssist;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// **本地最近实际释放出去的技能** —— 给 AI 终审判"与本地一致"用（2026-10-03 新增）。
///
/// ══════════════════════════════════════════════════════════════════════
///  [!] 为什么必须有（用户实测原话）：
///      「AI 应该是预铺技能建议 —— 比如本地判定打毁灭、AI 也建议毁灭，
///        但这时候因为延迟，AI 建议落地时本地**已经把毁灭打出去了**，
///        这种就不应该算作没有命中。」
///
///  [!] 现状（错在哪）：终审只问"此刻能不能放"（`SpellUtil.可用`）——
///      刚放出去的技能当然在 CD 里 ⇒ 判定"不可用" ⇒ **整条建议被丢弃**，
///      而且不计入命中 ⇒ 命中率被算低、AI 看起来像没在工作 ✗
///
///  [!] 做法：订阅 AEAssist 的施法成功事件（和 `记录模式` 用的是同一个 hook），
///      记下"最近放出的技能 + 时刻"；终审发现"AI 建议的正是这个" ⇒
///      判为**与本地一致**：计入命中、不丢弃、日志写清楚。
///
///  [!] ⚠️ 只读观察，不影响任何施放行为；订阅失败也不影响其他功能
///      （失败方向 = 记录不到 ⇒ 回到原来的判法）。
/// ══════════════════════════════════════════════════════════════════════
public static class 最近释放
{
    /// <summary>"刚放过"的时间窗 —— 2.5 秒 ≈ 一个 GCD + 一点余量。</summary>
    public const int 默认窗口毫秒 = 2500;

    private static uint _技能;
    private static long _时刻;

    private static bool _已订阅;
    private static bool _订阅试过;

    /// <summary>最近放出的技能 id（0 = 没有记录）。</summary>
    public static uint 最近技能 => _技能;

    /// <summary>最近放出的时刻（TickCount64 毫秒；0 = 没有记录）。</summary>
    public static long 最近时刻 => _时刻;

    /// <summary>确保订阅了施法成功事件（幂等、只试一次；失败不影响其他功能）。</summary>
    public static void 确保订阅()
    {
        if (_已订阅 || _订阅试过) return;
        _订阅试过 = true;

        try
        {
            var api = Core.Resolve<AEAssist.MemoryApi.MemApiSpellCastSuccess>();
            if (api == null) return;

            api.OnCastSucces += 收到施法成功;
            _已订阅 = true;
            LogHelper.Info("[HealerACR] 最近释放：已订阅施法成功事件（用于判定 AI 建议与本地一致）");
        }
        catch (Exception e)
        {
            // 订阅失败只是退化成"没有记录"，不影响施放
            LogHelper.Info("[HealerACR] 最近释放：订阅施法事件失败（不影响功能，只是判不出与本地一致）："
                           + e.GetType().Name);
        }
    }

    /// <summary>事件回调 —— 只记 id 与时刻。</summary>
    private static void 收到施法成功(AEAssist.CombatRoutine.SpellType 类型, uint 技能)
    {
        try
        {
            if (技能 == 0) return;
            _技能 = 技能;
            _时刻 = TimeHelper.Now();
        }
        catch { }
    }

    /// <summary>**这个技能是不是"本地刚刚放出去的那个"** —— 终审用它判"与本地一致"。</summary>
    public static bool 刚刚放过(uint 技能, int 窗口毫秒 = 默认窗口毫秒)
    {
        try
        {
            if (技能 == 0 || _技能 == 0) return false;
            if (技能 != _技能) return false;
            return TimeHelper.Now() - _时刻 <= 窗口毫秒;
        }
        catch { return false; }
    }

    /// <summary>给日志/窗口用的一句话描述。</summary>
    public static string 描述()
    {
        try
        {
            if (_技能 == 0) return "（还没记录到）";
            var 秒 = (TimeHelper.Now() - _时刻) / 1000.0;
            return $"{_技能}（{秒:F1} 秒前）";
        }
        catch { return "（读不到）"; }
    }
}
