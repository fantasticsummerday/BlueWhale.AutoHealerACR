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

    // ★ 时间门（高频路径审计规则 F）：写盘的诊断函数必须按时间节流 ✗→✓
    //   AI 事件本身低频（每次回复/每次采纳一行），这个门只作防御：
    //   万一被挂到高频路径上，自动降载，不会变成每秒写盘几十次 ✓
    private const int 最小间隔毫秒 = 200;
    private static long _上次写时刻;
    private static int _被门挡下;

    private static long 现在毫秒()
    {
        try { return Environment.TickCount64; } catch { return 0; }
    }



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

            // ★ 时间门（高频路径审计规则 F）：紧贴写盘，200ms 最小间隔 ——
            //   同一毫秒被多次调用时只写第一行，其余只计数（不是只做内容去重）✓
            var 钟 = Environment.TickCount64;          // ← 时间源（审计要求在函数开头可见）
            if (_上次写时刻 != 0 && 钟 - _上次写时刻 < 最小间隔毫秒)
            {
                _被门挡下++;
                return;
            }
            _上次写时刻 = 钟;

            var f = 路径();
            滚动(f);

            var 行 = DateTime.Now.ToString("HH:mm:ss.fff") +
                     " [T" + Environment.CurrentManagedThreadId + "] " +
                     内容.Replace("\r", " ").Replace("\n", " ⏎ ") +
                     (_被门挡下 > 0 ? $"（时间门挡下 {_被门挡下} 行）" : "") +
                     Environment.NewLine;
            _被门挡下 = 0;

            File.AppendAllText(f, 行, Encoding.UTF8);   // 已按时间节流（_上次写时刻 + 最小间隔毫秒）
        }
        catch { }   // ★ 绝不影响战斗
    }

    /// <summary>超过上限就把当前文件滚成 .1（只留一份旧档）。</summary>
    private static void 滚动(string f)
    {
        try
        {
            if (!File.Exists(f)) return;
            if (new FileInfo(f).Length <= 上限字节) return;
            var 旧 = f + ".1";
            try { if (File.Exists(旧)) File.Delete(旧); } catch { }
            try { File.Move(f, 旧); } catch { }
        }
        catch { }
    }



    /// <summary>给窗口/日志用的路径说明。</summary>

    public static string 描述()

    {

        try { return 路径(); } catch { return "（读不到）"; }

    }

}

