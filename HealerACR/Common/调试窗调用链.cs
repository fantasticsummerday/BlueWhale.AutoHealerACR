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

    /// <summary>写一行（**只在内容变化时**真正落盘）。</summary>
    public static void 记(string 内容)
    {
        try
        {
            if (内容 == _上次) return;      // 去重 ⇒ 天然节流
            _上次 = 内容;
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
