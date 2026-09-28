namespace HealerACR.Common;

/// <summary>
/// 记忆采集钩子 —— 让共享代码（HealerEntryBase）能"通知"外部的采集器，
/// 而不必知道采集器是谁。
///
/// **为什么需要这个**：
///   `HealerEntryBase` 是 HealerACR 和 BlueWhale **共用**的。
///   如果它直接写 `BlueWhale.AutoHealerACR.战斗记忆.记决策(...)`，
///   原版 HealerACR 就编译不过（那个类在 BlueWhale 里）。
///
///   所以中间加一层钩子：
///     · HealerACR 里只定义"有这么个回调"
///     · BlueWhale 在入口 Build 时把它挂上
///     · 原版不挂 → 回调是 null → 什么也不发生
///
///   和 `阈值钩子` 是同一个套路（AI 通过它改治疗阈值）。
/// </summary>
public static class 记忆钩子
{
    /// <summary>
    /// 记录一次技能施放。
    /// 参数：(技能ID, 技能名)
    /// </summary>
    public static Action<uint, string>? 记决策;

    /// <summary>每帧驱动（采集器自己决定要不要做事）</summary>
    public static Action? 每帧;

    /// <summary>战斗重置</summary>
    public static Action? 重置;

    /// <summary>安全调用：没挂载就什么也不做</summary>
    public static void 通知决策(uint 技能Id, string 技能名)
    {
        try { 记决策?.Invoke(技能Id, 技能名); }
        catch { }
    }

    public static void 通知每帧()
    {
        try { 每帧?.Invoke(); }
        catch { }
    }

    public static void 卸载()
    {
        记决策 = null;
        每帧 = null;
        重置 = null;
    }
}
