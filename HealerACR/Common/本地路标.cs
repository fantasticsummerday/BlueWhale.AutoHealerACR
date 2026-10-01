using System;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// **本地层的限流路标** —— 和 BlueWhale 侧的 `崩溃路标.记()` **规则完全一致**。
///
/// ══════════════════════════════════════════════════════════════════
///  [!] 为什么需要它（实测踩的坑）
///      本地层看不到 BlueWhale 的 `崩溃路标` 类（跨程序集），
///      所以 `HealTargetHelper` 里的 3490~3507 是**直接 `LogHelper.Info`** 的 ——
///      **没有节流**。
///      而 `可治疗队友()` 被 resolver **每帧**调、也被采集调，
///      ==> 实测 **16 秒写了 10 MB 日志**，其中 **99% 是这些路标**
///          （AEAssist 把日志截断成 `_010.log` / `_011.log` / `_012.log`）。
///      ==> 日志几乎没法读了，**而诊断能力一点没多**。
///
///  [!] 所以规则和 BlueWhale 侧统一：
///        · 号**变了** -> 立刻打（一次不丢 —— 崩点信息永远保留）
///        · 号**没变** -> 最多 1 秒 1 条
///
///  [!] 想恢复"每条都打"：把 `详细模式` 设成 true（排查时用）。
/// ══════════════════════════════════════════════════════════════════
public static class 本地路标
{
    /// <summary>打开后**每条都打**（排查用，日志会迅速变大）。</summary>
    public static bool 详细模式 = false;

    private static int _上次号 = -1;
    private static long _上次毫秒;
    private static long _累计;

    /// <summary>打一个本地路标。号变了立刻打；号没变最多 1 秒 1 条。</summary>
    public static void 记(int 号, string 说明)
    {
        try
        {
            var 现在 = TimeHelper.Now();
            if (号 == _上次号 && !详细模式 && 现在 - _上次毫秒 < 1000) return;
            _上次号 = 号;
            _上次毫秒 = 现在;
            _累计++;
            LogHelper.Info($"[HealerACR.路标] {号}（{说明}）｜累计 {_累计} 次");
        }
        catch { }
    }

    /// <summary>**详细路标** —— 只在 `详细模式` 打开时打（逐对象那种高频的用它）。</summary>
    public static void 记详(int 号, string 说明)
    {
        if (!详细模式) return;
        记(号, 说明);
    }

    /// <summary>重置（换职业 / 重载时用）。</summary>
    public static void 重置()
    {
        _上次号 = -1;
        _上次毫秒 = 0;
        _累计 = 0;
    }
}
