using System;
using System.Diagnostics;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// **调用深度追踪器** —— 用来抓"无限递归导致的栈溢出"（`0xc00000fd`）。
///
/// ══════════════════════════════════════════════════════════════════════════
///  [!] 为什么需要它（实测背景）
///
///      游戏在**开战 / 召唤小仙女**那一刻闪退，Windows 事件日志：
///          异常代码 `0xc00000fd` = **STACK_OVERFLOW**
///      第一次崩溃留下了转储，托管栈显示 `AiSettings.get_Instance()` 在栈顶、
///      下面几个地址**无限循环**；但第二次崩溃**没有再生成转储**。
///
///      而栈溢出**不会**触发 `AppDomain.CurrentDomain.UnhandledException`
///      （.NET 的已知行为，栈已经没地方放处理逻辑了）
///      ==> 想抓这个环，只能**自己记调用深度**，在崩溃之前把它打出来。
///
///  [!] 用法（包住怀疑参与环的方法体）：
///      ```csharp
///      using var _ = 调用深度.进("方法名");
///      ```
///      进入时深度 +1，离开时 -1（`Dispose` 保证异常路径也 -1）。
///      深度超过阈值时**打一次栈**（每秒最多一次），然后照常继续。
///
///  [!] 为什么阈值是 25：正常的绘制/采集调用深度一般在 10 以内，
///      25 已经明显异常，而离栈溢出（通常几千层）还很远
///      ==> 能在崩溃前好几秒就把环抓出来。
/// ══════════════════════════════════════════════════════════════════════════
/// </summary>
public static class 调用深度
{
    /// <summary>超过这个深度就报一次（正常调用深度远小于它）。</summary>
    private const int 阈值 = 25;

    /// <summary>
    /// 深度报警写到哪 —— **文档目录下**，方便直接打开看。
    ///
    /// [!] 为什么不用 AEAssist 的 LogHelper：见下面写文件那段注释
    ///     （栈快满时那条链不可靠，实测一次都没打出来）。
    /// </summary>
    private static readonly string 诊断文件 = 取诊断文件();

    private static string 取诊断文件()
    {
        try
        {
            var 文档 = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(文档))
                return System.IO.Path.Combine(文档, "BlueWhale递归诊断.txt");
        }
        catch { }
        return System.IO.Path.Combine(AppContext.BaseDirectory, "BlueWhale递归诊断.txt");
    }

    [ThreadStatic] private static int _深度;

    /// <summary>上一次报栈的时刻（每秒最多一条，避免刷屏）。</summary>
    private static long _上次报毫秒;

    /// <summary>当前线程的调用深度（调试用）。</summary>
    public static int 当前 => _深度;

    public readonly struct 守卫 : IDisposable
    {
        public void Dispose() => _深度--;
    }

    /// <summary>进入一个受监视的方法（配 `using` 用）。</summary>
    public static 守卫 进(string 名字)
    {
        _深度++;

        if (_深度 == 阈值)
        {
            // ⚠️ 只打一次 "== 阈值"（不是 ">= 阈值"）——
            //    否则越深打得越多，日志会在崩溃前被刷爆。
            try
            {
                var 现在 = Environment.TickCount64;
                if (现在 - _上次报毫秒 >= 1000)
                {
                    _上次报毫秒 = 现在;

                    // ★ 打完整栈 —— 环里的方法名全在这里
                    //
                    //  [!] **直接写文件，不走 AEAssist 的日志** ★
                    //      实测教训：原来用 `LogHelper.Error`，
                    //      而崩溃前那几次**一条都没打出来** —
                    //      怀疑是"栈快满时 AEAssist 的日志链自己也分配内存/抛异常"，
                    //      被外面的 `catch { }` 吞掉了。
                    //      ==> 改成自己 `File.AppendAllText`：
                    //          依赖最少、路径最短，栈再紧也能写出去。
                    var 栈 = new StackTrace(1, true);
                    var 文本 = $"[{DateTime.Now:HH:mm:ss.fff}] 深度达到 {阈值}" +
                               $"（疑似无限递归 ⇒ 会栈溢出）当前={名字}\n{栈}\n";
                    try
                    {
                        System.IO.File.AppendAllText(诊断文件, 文本);
                    }
                    catch { }
                }
            }
            catch { }
        }

        return default;
    }
}
