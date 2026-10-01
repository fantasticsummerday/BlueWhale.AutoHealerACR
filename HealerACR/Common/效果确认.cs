using System.Runtime.InteropServices;
using AEAssist;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Common;

/// <summary>
/// 技能效果确认 —— hook 游戏的 ActionEffect 回调。
///
/// ⚠️ **风险说明（务必读）**
///   这是**替换游戏回调**级别的操作。搞错了不是"技能不放"这种小故障，
///   而是整个 ACR 崩、甚至游戏行为异常。所以做了三重保护：
///     1. **默认关闭** —— 设置里 `启用效果确认` 手动打开才挂
///     2. **全程 try-catch** —— 任何一步失败都只是降级
///     3. **挂不上就退回 RecentlyUsed** —— 不影响任何其他功能
///
/// 原理（逆向同类 ACR 的 ActionEffectTracker 得出）：
///   ActionEffect 是"技能命中"的回调，但它**不含技能 ID** ——
///   所以必须"放技能前记下打算放什么，回调来了再匹配"，这也是 同类 ACR 用
///   MatchesAction（内部走 CheckActionChange）的原因。
///
/// 签名串来源：AEAssist.ACT.ActionHook 的 .cctor（我把它扒出来的，
/// 函数序言 40 55 53 56 57 41 56 48 8D 6C 24 ?? 48 81 EC 是典型的六寄存器保存）
/// </summary>
public static class 效果确认
{
    /// <summary>
    /// 游戏回调的委托签名（对应 ReceiveActionEffect）。
    /// 参数按 FFXIV 的惯例：sourceId / source / targetId / target / effectArray / effectCount。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ReceiveActionEffectDelegate(
        uint sourceId,
        IntPtr source,
        uint targetId,
        IntPtr target,
        IntPtr effectArray,
        uint effectCount);

    private static bool _已挂;
    private static bool _已失败;

    /// <summary>打算放的技能（回调来了才知道有没有真的命中）</summary>
    private static uint _待确认技能;
    private static long _待确认时间;

    /// <summary>技能 id → 最后一次确认命中的时间戳</summary>
    private static readonly Dictionary<uint, long> _确认记录 = new();

    /// <summary>确认窗口：超过这个时间还没等到回调，就当没放出去</summary>
    private const long 确认窗口毫秒 = 2000;

    public static bool 已挂载 => _已挂;

    public static bool 挂载失败过 => _已失败;

    /// <summary>挂载 hook。失败不抛异常，只记一次日志。</summary>
    public static void 尝试挂载()
    {
        if (_已挂 || _已失败) return;

        try
        {
            if (!HealSettings.Instance.启用效果确认) return;
        }
        catch
        {
            return;
        }

        try
        {
            // 从 AEAssist.ACT.ActionHook..cctor 扒出来的签名
            var sig = new CompSig("40 55 53 56 57 41 56 48 8D 6C 24 ?? 48 81 EC");
            sig.GetHook<ReceiveActionEffectDelegate>(回调);

            _已挂 = true;
            LogHelper.Info("[HealerACR] ActionEffect hook 已挂载，开始追踪技能命中");
        }
        catch (Exception e)
        {
            _已失败 = true;
            LogHelper.Error($"[HealerACR] ActionEffect hook 挂载失败（已降级，不影响其他功能）：{e.Message}");
        }
    }

    /// <summary>放技能前登记一下，等回调来确认</summary>
    public static void 登记尝试(uint 技能Id)
    {
        if (!_已挂 || 技能Id == 0) return;

        _待确认技能 = 技能Id;
        _待确认时间 = TimeHelper.Now();
    }

    /// <summary>
    /// 这个技能最近是不是**真的命中过**。
    /// 挂载失败时返回 -1，调用方据此退回 RecentlyUsed 判断。
    /// </summary>
    public static int 是否确认命中(uint 技能Id, int 窗口毫秒 = 3000)
    {
        if (!_已挂) return -1;   // -1 = 没有 hook，判断不了

        try
        {
            if (!_确认记录.TryGetValue(技能Id, out var t)) return 0;
            return TimeHelper.Now() - t <= 窗口毫秒 ? 1 : 0;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>游戏回调本体。任何异常都吞掉 —— 绝不能让它冒泡回游戏线程。</summary>
    private static void 回调(uint sourceId, IntPtr source, uint targetId, IntPtr target,
                             IntPtr effectArray, uint effectCount)
    {
        try
        {
            // 只认自己打出去的
            if (sourceId != CharacterExt.我的ObjectId()) return;

            // 窗口内 + 有登记 → 判定为"这个技能确实命中/生效了"
            if (_待确认技能 == 0) return;
            if (TimeHelper.Now() - _待确认时间 > 确认窗口毫秒) return;

            _确认记录[_待确认技能] = TimeHelper.Now();
            _待确认技能 = 0;
        }
        catch
        {
            // 吞掉：回调运行在游戏线程上，抛出去会出大事
        }
    }

    /// <summary>重置（换职业 / 重载时调用）</summary>
    public static void 重置()
    {
        _待确认技能 = 0;
        _待确认时间 = 0;
        _确认记录.Clear();
    }
}
