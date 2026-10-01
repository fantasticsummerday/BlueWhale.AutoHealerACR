using System;
using System.Collections.Generic;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// **Resolver 重入断路器** —— 递归环的运行时保险丝。
///
/// ══════════════════════════════════════════════════════════════════════════
///  [!] 为什么需要它（崩溃转储实证）
///
///      游戏以 `0xc00000fd`（**栈溢出**）崩溃，转储的托管栈共 14706 行，
///      最外层能对上名字的帧是：
///          HealerACR.Resolvers.Res_HealEmergency.Check()
///          AEAssist…PVE_RunSlotHelper.CheckNext()
///          AEAssist.CombatRoutine2.Update()
///      它下面三个地址**无限循环**（约 4900 层）==> 环从 `Check()` 的
///      内部调用链绕回了 `Check()`。
///
///      ⚠️ 静态排查已经把能查的都查完了，**没有找到闭合的环**：
///         · `AiThresholdAdapter` 全链（偏移 / 倾向偏移量 / 同步到表 /
///           推类别 / 算倾向偏移量 / 目标偏移）—— 逐条走完，不回到起点
///         · `HealSettings.Instance` 是普通自动属性，没有钩子
///         · `治疗决策` / `伤害预测` 那条链也是终点
///
///      ==> 所以改用**运行时断路**：与其继续猜环在哪，
///          不如让环在很浅的地方就被切断。
///
///  [!] 用法（包住 resolver 的 `Check()`）：
///      ```csharp
///      public int Check()
///      {
///          if (断路器.该断("Res_HealEmergency.Check")) return -900;
///          try { ...原来的逻辑... }
///          finally { 断路器.出(); }
///      }
///      ```
///
///  [!] 为什么"重入"一定不正常：resolver 的 `Check()` 由框架**当帧调度**，
///      正常只会单层进入。**重入 ⇒ 一定有回调把它又拉进来了** ——
///      那正是这个崩溃的形态。
///
///  [!] 阈值 8 的理由：正常调用深度是 1；8 层没有合法场景，
///      而离栈溢出（几千层）极远 ⇒ 能在崩溃前很早就切断。
/// ══════════════════════════════════════════════════════════════════════════
/// </summary>
public static class Resolver断路器
{
    private const int 最大重入层数 = 8;

    [ThreadStatic] private static int _层数;
    [ThreadStatic] private static string? _最内层;

    /// <summary>诊断输出（文档目录）—— 直接写文件，不走宿主日志（栈紧时它不可靠）。</summary>
    private static readonly string 诊断文件 = 取文件();

    private static string 取文件()
    {
        try
        {
            var 文档 = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(文档))
                return System.IO.Path.Combine(文档, "BlueWhale递归断路器.txt");
        }
        catch { }
        return System.IO.Path.Combine(AppContext.BaseDirectory, "BlueWhale递归断路器.txt");
    }

    /// <summary>
    /// **进一次 resolver 的 Check**。返回 true = **必须立刻中止**
    /// （重入太深，再进去就会栈溢出）。
    /// </summary>
    public static bool 该断(string 名字)
    {
        _层数++;

        if (_层数 <= 最大重入层数)
        {
            if (_层数 == 1) _最内层 = 名字;
            return false;
        }

        // 超过就断 —— 每次只写一行（避免刷爆磁盘）
        if (_层数 == 最大重入层数 + 1)
        {
            try
            {
                var 文本 = $"[{DateTime.Now:HH:mm:ss.fff}] resolver 重入达到 {最大重入层数} 层" +
                           $" ⇒ 已切断（疑似无限递归）\\n" +
                           $"    最外层={_最内层}  当前={名字}\\n" +
                           $"    调用栈：\\n{new System.Diagnostics.StackTrace(1, true)}\\n\\n";
                System.IO.File.AppendAllText(诊断文件, 文本);
            }
            catch { }
        }

        return true;
    }

    /// <summary>**出一次**（必须放在 `finally` 里，异常路径也要出）。</summary>
    public static void 出()
    {
        if (_层数 > 0) _层数--;
    }

    /// <summary>当前线程的重入层数（诊断用）。</summary>
    public static int 层数 => _层数;
}
