using System;
using AEAssist.Helper;

namespace BlueWhale.AutoHealerACR
{
    /// <summary>
    /// ★★★ **依赖解析探针** —— 只为回答一个问题：★★★
    ///
    ///   **AEAssist 加载 `BlueWhale.dll` 时，会不会解析同目录的 `HealerACR.dll`？**
    ///
    /// [!] 为什么需要回答它
    ///     现在 `BlueWhale.AutoHealerACR.csproj` 里有
    ///         &lt;Compile Include="..\HealerACR\**\*.cs" /&gt;
    ///     也就是把 HealerACR 的**全部源码又编译了一遍**。
    ///     ==> `HealTargetHelper` / `TimelineManager` / `HealSettings` / `候选集`
    ///         **在两个程序集里各有一份独立静态状态**。
    ///
    ///     正确结构应该是「一份基础设施 + 一个 AI 层」：
    ///         HealerACR.dll   <- 全部基础设施（唯一一份）
    ///         BlueWhale.dll   <- 只含 AI，**引用** HealerACR
    ///
    ///     但那要先确认加载器能解析同目录依赖 —— 就是本探针存在的唯一理由。
    ///
    /// [!] 本类**不参与任何业务逻辑**，也不被任何代码调用。
    ///     它只在**类型签名**上引用 `HealerACR` 的类型 ——
    ///     于是 AEAssist 扫描/加载本程序集时，CLR 必须先把 `HealerACR.dll` 解析出来。
    ///
    /// [!] 怎么读结果
    ///     · 日志里出现 `[BlueWhale.依赖探针] 解析成功`  ==> **可以合并** ✅
    ///     · 出现 `FileNotFoundException` / `TypeLoadException`
    ///       或干脆加载失败                              ==> **不能合并** ❌
    /// </summary>
    public static class 依赖解析探针
    {
        /// <summary>引用 HealerACR 的类型 —— 强迫 CLR 解析 HealerACR.dll。</summary>
        public static Type? 基础设施类型 =>
            typeof(HealerACR.Common.HealTargetHelper);

        /// <summary>引用 HealerACR 的程序集 —— 用于报告它的实际位置。</summary>
        public static System.Reflection.Assembly? 基础设施程序集 =>
            typeof(HealerACR.Common.HealTargetHelper).Assembly;

        public static void 报告()
        {
            try
            {
                var asm = 基础设施程序集;
                LogHelper.Info("════════ [BlueWhale.依赖探针] ════════");
                LogHelper.Info("  HealerACR 类型解析：**成功**");
                LogHelper.Info($"    程序集名 = {asm?.GetName().Name}");
                LogHelper.Info($"    版本     = {asm?.GetName().Version}");
                LogHelper.Info($"    位置     = {(string.IsNullOrEmpty(asm?.Location) ? "(内存加载)" : asm!.Location)}");
                var alc = asm == null
                    ? null
                    : System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm);
                LogHelper.Info($"    ALC      = {alc?.Name ?? "(默认)"}");
                LogHelper.Info("  ==> 结论：**同目录依赖可以被解析** —— 可以合并成「一份基础设施」的结构。");
                LogHelper.Info("══════════════════════════════════════");
            }
            catch (Exception e)
            {
                try
                {
                    LogHelper.Info("[BlueWhale.依赖探针] 解析**失败**：" +
                                   e.GetType().Name + " " + e.Message);
                }
                catch { }
            }
        }
    }
}
