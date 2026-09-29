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

    /// <summary>
    /// **进入本职业的循环时触发** —— 也就是"切换到本职业 / 重新加载本 ACR"。
    ///
    /// ⚠️ 和 <see cref="重置"/> 的区别（两个都要挂，各管一件事）：
    ///   · `重置`：**换副本**时清状态（OnTerritoryChanged）
    ///   · `进入循环`：**切职业**时清状态 **+ 重新初始化 AI**
    ///
    /// 为什么必须是两件事：换本是"同一个职业进新地图"，
    /// 而切职业是"换了一套技能表和一套判断逻辑" ——
    /// AI 的倾向 / 阈值偏移 / 预取队列全是按**上一个职业**的局面得出的，
    /// 不清掉就会拿学者的结论去指导白魔（和"跨职业静态污染"是同一类问题）。
    ///
    /// 另外切职业时**必须重新初始化**（不只是清空）：
    /// 否则 AI 会有一段"哑"的窗口（倾向=未知、阈值偏移=0），
    /// 开场那几秒等于没有 AI。
    /// </summary>
    public static Action? 进入循环;

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

    /// <summary>通知"进入了本职业的循环"（切职业 / 重载 ACR）</summary>
    public static void 通知进入循环()
    {
        try { 进入循环?.Invoke(); }
        catch { }
    }

    public static void 卸载()
    {
        记决策 = null;
        每帧 = null;
        重置 = null;
        进入循环 = null;
    }
}
