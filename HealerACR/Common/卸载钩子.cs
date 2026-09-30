using System;

namespace HealerACR.Common;

/// <summary>
/// **ACR 卸载钩子** —— HealerACR 被卸载（重载 ACR / 切职业 / 退出）时通知外部层。
///
/// ══════════════════════════════════════════════════════════════════
///  [!] 为什么需要它（实测 bug）
///
///    `Build()` 里注册了每帧回调（挂在 `JobViewWindow.UpdateAction`），
///    框架**每帧无条件调**。用户实测：点了「重新加载 ACR」并切到另一个
///    ACR 之后，**左下角还在刷小鲸鱼的日志** ——
///    因为旧回调没摘、AI 的钩子也没卸。
///
///  [!] 为什么用钩子而不是直接引用 BlueWhale
///
///    `HealerACR.csproj` **只编译自己目录**，而两个项目的 dll 里
///    **各有一份 HealerACR 的类** —— 给 HealerACR 加
///    `ProjectReference -> BlueWhale` 会**循环依赖**。
///    => 只能反向注入，和既有的 `记忆钩子` / `状态重置钩子` / `阈值钩子`
///       完全同一个模式。
///
///  [!] 钩子为 null 时必须**安全降级**
///
///    单独编译 HealerACR（没有 BlueWhale）时，这个字段永远是 null。
///    所以 `<see cref="通知"/>` 里判空返回 —— **不能抛异常**，
///    否则卸载路径会把 ACR 卡死。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 卸载钩子
{
    /// <summary>由 BlueWhale 层注入（未注入时为 null，`通知()` 会安全跳过）</summary>
    public static Action? 卸载;

    /// <summary>通知外部层"本 ACR 正在卸载"。没有注入时什么都不做。</summary>
    public static void 通知()
    {
        try
        {
            卸载?.Invoke();
        }
        catch
        {
            // [!] 卸载路径**绝不能抛** —— 抛出会让 ACR 卸载不干净，
            //     下次加载可能带着脏状态。宁可吞掉。
        }
    }
}
