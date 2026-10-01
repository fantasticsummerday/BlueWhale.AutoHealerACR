using System;
using System.IO;

namespace HealerACR.Common;

/// <summary>
/// **设置面板绘制门** —— 把 `OnDrawSetting()` 限到"每帧最多一次"。
///
/// ══════════════════════════════════════════════════════════════════
///  [!] 这是本类的第 **3** 版，前两版都实测失败，原因值得记住：
///
///      ① 第一版：`TimeHelper.Now()` + 本类静态字段
///         ⇒ 那个时钟语义不明（门设 8ms，实测仍被画 680~8800 次/秒）
///
///      ② 第二版：`Environment.TickCount64` + 独立静态类
///         ⇒ 时钟是可靠的，但**实测仍然 480 次/秒**
///         ⇒ 核对过：门代码正确、5 个入口各只调一次 `base.OnDrawSetting()`
///         ==> 唯一解释：**`_面板上次` 这个静态字段在这一刻不止一份**
///             （同一份源码被编译进多个程序集 / 多个 AssemblyLoadContext
///              各自加载 ⇒ 每份有自己的静态字段）
///         ==> 教训：**"独立静态类" 并不等于 "全进程唯一"**。
///             `Mutex` 也救不了 —— 每份仍读改写自己的时间戳。
///
///      ③ 本版：用**文件时间戳**当共享状态。
///         文件系统是**真正跨程序集共享**的，
///         拿它当"上次放行时刻"，所有副本看到的是同一个值。
///
///  [!] 开销：只在**放行时**写一次文件 ⇒ 约 60 次/秒
///      （对比：放行一次要跑 13 个 ImGui 段 + 数个反射调用，
///        写一个时间戳便宜得多）。
///
///  [!] ⚠️ **不要删这个门**：
///      实测「只有切到 ACR 设置页时才闪」—— 正因为只有那时这一页才被画。
///      而被画 480 次/秒（= 8 次/帧）正是闪烁的成因：
///      13 个 ImGui 段在同一帧内反复重建 ⇒ 控件位置抖动 + 同位置双重曝光。
/// ══════════════════════════════════════════════════════════════════
public static class 绘制节流
{
    /// <summary>门限：16ms ≈ 60 次/秒（与帧率同量级）。</summary>
    private const int 面板最小间隔毫秒 = 16;

    private static string? _门文件;

    private static string 取门文件()
    {
        if (_门文件 != null) return _门文件;
        try
        {
            var 目录 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueWhale");
            Directory.CreateDirectory(目录);
            _门文件 = Path.Combine(目录, "面板绘制门.stamp");
        }
        catch
        {
            try
            {
                _门文件 = Path.Combine(Path.GetTempPath(), "BlueWhale_面板绘制门.stamp");
            }
            catch { _门文件 = "BlueWhale_面板绘制门.stamp"; }
        }
        return _门文件;
    }

    // 诊断统计（每秒报一次，见 `取节流统计`）
    private static long _调用数;
    private static long _放行数;
    private static long _窗口毫秒;
    private static bool _用过文件;

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
    /// </summary>
    public static bool 该画面板()
    {
        _调用数++;
        try
        {
            var 文件 = 取门文件();
            var 现在 = DateTime.UtcNow;

            // 读"上次放行时刻"（第一次用就放行）
            DateTime 上次;
            try
            {
                上次 = File.Exists(文件) ? File.GetLastWriteTimeUtc(文件) : DateTime.MinValue;
            }
            catch
            {
                上次 = DateTime.MinValue;
                _用过文件 = false;
            }

            if (上次 != DateTime.MinValue && (现在 - 上次).TotalMilliseconds < 面板最小间隔毫秒)
                return false;

            // 放行 —— 并把时间戳写回去（所有副本共享这一个文件）
            try
            {
                File.WriteAllText(文件, 现在.ToString("O"));
                File.SetLastWriteTimeUtc(文件, 现在);
                _用过文件 = true;
            }
            catch
            {
                // 文件写不了就退回"只用内存时间戳"（至少不比没有门差）
                if (_用过文件) { /* 之前能用，现在不能 —— 继续放行以免面板不显示 */ }
            }

            _放行数++;
            return true;
        }
        catch
        {
            return true;   // 出错就画（宁可多画，也不要"面板不显示"）
        }
    }
}
