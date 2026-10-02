using System;
using System.IO;

namespace HealerACR.Common;

/// <summary>
/// **调试窗调用链诊断** —— 独立、无上限、用户能直接发出来的证据文件。
///
/// ══════════════════════════════════════════════════════════════════════════
///  [!] 为什么又开一个文件（前一个为什么不够）：
///
///      `HealerEntryBase.写诊断` 有 **500 次上限**（`if (_写诊断次数 > 500) return;`），
///      而它在启动阶段就被用满了 ⇒ **后面所有的失败都写不出去**。
///      结果：用户报"开关没用"，而我这边的诊断文件**停在几百行前**，
///      完全看不到当次发生了什么 —— 这是这一晚反复卡住的直接原因。
///
///  [!] 所以本类：
///        · **不设次数上限**（只在**内容变化**时写，所以不会刷爆）
///        · **不设大小上限**（同上）
///        · 用独立的 `StreamWriter`（AutoFlush，异常也能留下最后一行）
///        · 路径固定 `我的文档\BlueWhale-调试窗调用链.txt`
///
///  [!] ⚠️ 只在**内容变化**时写 —— 它在每帧路径上被调，
///      写盘本身不能是每帧动作（那是本项目 P0-5 那类错误的形状）。
/// ══════════════════════════════════════════════════════════════════════════
/// </summary>
public static class 调试窗调用链
{
    private static StreamWriter? _流;
    private static string _上次 = "";
    private static int _行数;

    /// <summary>上一次真正落盘的时刻（毫秒）—— **按时间节流**，见 `记()` 的说明。</summary>
    private static long _上次落盘毫秒;

    /// <summary>两条日志之间至少隔这么久（毫秒）。</summary>
    /// <remarks>
    /// [!] 为什么**内容去重之外还要时间节流**（审计规则 F 要求的）：
    ///     去重只能挡住"内容一模一样"的重复；而调用链上有些值**每帧都在变**
    ///     （比如"第 N 次调用"里的 N），去重就完全失效 ⇒ 变成每帧写盘。
    ///     本项目 P0-5 那类错误（每帧 4 次文件操作）就是这么来的。
    ///     ==> 两道一起用：**内容变了 **且** 距上次够久** 才写。
    /// </remarks>
    private const long 最小间隔毫秒 = 500;

    /// <summary>写一行（**内容变化 且 距上次 ≥500ms** 才真正落盘）。</summary>
    public static void 记(string 内容)
    {
        try
        {
            var 现在 = Environment.TickCount64;

            // ★ 两道闸：内容去重 + 时间节流
            if (内容 == _上次) return;

            // [!] 例外：**头几行必须写**（否则"从没被调"和"调了但被节流"分不清）
            if (_行数 > 5 && 现在 - _上次落盘毫秒 < 最小间隔毫秒) return;

            _上次 = 内容;
            _上次落盘毫秒 = 现在;
            _行数++;

            if (_流 == null)
            {
                var 文档 = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var 路径 = Path.Combine(文档, "BlueWhale-调试窗调用链.txt");
                _流 = new StreamWriter(路径, append: true) { AutoFlush = true };
                _流.WriteLine($"===== 会话开始 {DateTime.Now:MM-dd HH:mm:ss} =====");
            }

            _流.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] #{_行数} {内容}");
        }
        catch { }
    }
}
