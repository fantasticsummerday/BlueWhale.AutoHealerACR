using System;

namespace HealerACR.Common;

/// <summary>
/// **设置面板绘制门** —— 把 `OnDrawSetting()` 限到"每帧最多一次"。
///
/// ══════════════════════════════════════════════════════════════════
///  [!] 本类的演进（每一版都有实测结论，写在这里免得再走回去）
///
///      ① `TimeHelper.Now()` + 本类静态字段
///         ⇒ 那个时钟语义不明（门设 8ms，实测仍被画 680~8800 次/秒）
///
///      ② `Environment.TickCount64` + 独立静态类
///         ⇒ 时钟可靠了，但实测**仍然 480 次/秒**
///         ⇒ 说明"独立静态类"**不等于**"全进程唯一"
///            （同一份源码可能被编译进多个程序集 / 多个加载上下文）
///
///      ③ **文件时间戳**（用 `%APPDATA%` 下的 stamp 文件当共享状态）
///         ⇒ 确实跨程序集共享，但**引入每帧文件 IO**：
///              `File.Exists` + `File.GetLastWriteTimeUtc`
///              + `File.WriteAllText` + `File.SetLastWriteTimeUtc`
///         ⇒ **这是错的**：`OnDrawSetting` 是高频路径，
///            不该把文件系统当进程内同步原语。
///            （外部审查报告 P0-5 指出，我核实确认。）
///
///      ④ **本版：纯内存节流**（不碰文件系统）
///         ⇒ `Environment.TickCount64` + 静态字段，16ms 间隔。
///         ⇒ 已知局限：若静态字段真有多个副本，每个副本各放行 1 次/16ms
///            （最坏 5 个入口 = 每帧 5 次）。这**远好于每帧文件 IO**，
///            而且实测证明"每帧多次"本身不会让窗口闪 ——
///            当年 0.4.2.2 每帧被画两次也照样正常。
///
///  [!] ⚠️ **不要再用文件做节流**；也**不要删掉这个门**
///      （删了会回到 8800 次/秒那种量级）。
/// ══════════════════════════════════════════════════════════════════
public static class 绘制节流
{
    /// <summary>门限：16ms ≈ 60 次/秒（与帧率同量级）。</summary>
    private const int 面板最小间隔毫秒 = 16;

    private static long _面板上次;

    // 诊断统计（每秒报一次，见 `取节流统计`）
    private static long _调用数;
    private static long _放行数;
    private static long _窗口毫秒;

    /// <summary>诊断：返回 (被调, 放行, 窗口毫秒) 并把计数清零。</summary>
    public static (long 调用, long 放行, long 毫秒) 取节流统计()
    {
        try
        {
            var 现在 = Environment.TickCount64;
            var r = (_调用数, _放行数, 现在 - _窗口毫秒);
            _调用数 = 0;
            _放行数 = 0;
            _窗口毫秒 = 现在;
            return r;
        }
        catch { return (0, 0, 0); }
    }

    /// <summary>
    /// **这一帧该不该画设置面板**。返回 false = 跳过本次绘制。
    ///
    /// [!] **纯内存判断，不碰文件系统**（见类说明第 ③ 条的教训）。
    /// </summary>
    public static bool 该画面板()
    {
        _调用数++;
        try
        {
            var 现在 = Environment.TickCount64;
            if (现在 - _面板上次 < 面板最小间隔毫秒) return false;
            _面板上次 = 现在;
            _放行数++;
            return true;
        }
        catch
        {
            return true;   // 出错就画（宁可多画，也不要"面板不显示"）
        }
    }
}
