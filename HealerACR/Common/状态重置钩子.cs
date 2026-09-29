namespace HealerACR.Common;

/// <summary>
/// 状态重置钩子 —— 换本 / 换职业时通知外部模块"局面完全变了"。
///
/// **为什么需要这一层**：
///   `HealerEntryBase` 是 HealerACR 和 BlueWhale **共用**的，
///   它不知道 AI 层存在（AI 层在 BlueWhale 里）。
///
///   但换本时 AI 层的状态也必须清：
///     · 上个本一直在打小怪（AI 判"激进"）→ 进 Boss 本还保持激进
///     · 阈值偏移的平滑值会从上个本带过来
///
///   所以加这个钩子：
///     · HealerACR 侧只负责"通知"
///     · BlueWhale 侧挂上自己的清理逻辑
///     · 原版不挂 → 什么也不发生
///
///   和 `阈值钩子` / `记忆钩子` 是同一个套路。
/// </summary>
public static class 状态重置钩子
{
    /// <summary>换本 / 换职业时调用</summary>
    public static Action? 重置;

    /// <summary>安全通知：没挂载就什么也不做</summary>
    public static void 通知()
    {
        try { 重置?.Invoke(); }
        catch { }
    }

    public static void 卸载() => 重置 = null;
}
