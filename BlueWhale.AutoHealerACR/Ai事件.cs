using System;
using System.IO;
using System.Text;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// **AI 事件旁路文件**（2026-10-03）—— 不依赖宿主的日志过滤。
///
/// ══════════════════════════════════════════════════════════════════════
///  [!] 为什么需要（实机实证）：
///      0.6.6.1 那次运行里，聊天栏**明明有** AI 的回复
///      （`原始回复：C1|Boss 正在读条…`、`AI 策略参数：…`），
///      但 `D:\FF14\Logs\Log-*.log` 里**一条都没有** ✗
///      ⇒ 宿主的日志过滤会吞掉这些行 ⇒ 我无法用日志判断 AI 有没有在工作 ✗
///
///  [!] 做法：关键事件（原始回复 / 采纳 / 拦截）**直接追加写我们自己的文件**：
///        %APPDATA%\BlueWhale\AI-事件.txt
///      · 单文件上限 4 MB，超过就滚成 .1（只留一份旧档）
///      · 全部包在 try/catch 里 —— 写文件失败绝不影响战斗 ✓
///      · 只在"最新副本"写（见 独立设置窗 的世代戳）——
///        否则重载 7 次会写 7 份、文件 7 倍大 ✗
/// ══════════════════════════════════════════════════════════════════════
public static class Ai事件
{
    private const long 上限字节 = 4L * 1024 * 1024;

    private static string 路径()
    {
        var 目录 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueWhale");
        try { Directory.CreateDirectory(目录); } catch { }
        return Path.Combine(目录, "AI-事件.txt");
    }

    /// <summary>写一行（自动带时间戳与原线程 id，便于对齐主日志）。</summary>
    public static void 记(string 内容)
    {
        try
        {
            if (string.IsNullOrEmpty(内容)) return;

            var f = 路径();
            try
            {
                var fi = new FileInfo(f);
                if (fi.Exists && fi.Length > 上限字节)
                {
                    var 旧 = f + ".1";
                    try { if (File.Exists(旧)) File.Delete(旧); } catch { }
                    try { File.Move(f, 旧); } catch { }
                }
            }
            catch { }

            var 行 = DateTime.Now.ToString("HH:mm:ss.fff") +
                     " [T" + Environment.CurrentManagedThreadId + "] " +
                     内容.Replace("\r", " ").Replace("\n", " ⏎ ") + Environment.NewLine;

            File.AppendAllText(f, 行, Encoding.UTF8);
        }
        catch { }   // ★ 绝不影响战斗
    }

    /// <summary>给窗口/日志用的路径说明。</summary>
    public static string 描述()
    {
        try { return 路径(); } catch { return "（读不到）"; }
    }
}
