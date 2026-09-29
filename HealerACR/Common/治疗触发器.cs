using AEAssist.CombatRoutine.Trigger;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// 治疗触发器 —— 把时间轴 / 编辑器里的"动作"接进 ACR。
///
/// ══════════════════════════════════════════════════════════════════
///  ⚠️ 和"我自己写的轮询"的区别：
///
///  我原来的做法：
///      OnBattleUpdate(ms) → TimelineManager.更新(ms) → 到点了就设标志
///      → resolver 里查标志
///    这是**轮询**：每帧问一次"到了吗"。
///
///  AEAssist 的 Trigger 体系：
///      Rotation.AddTriggerAction(actions)
///      → 框架在合适的时机**回调** Handle()
///    这是**事件驱动**：不用自己轮询，而且能接进它的时间轴编辑器。
///
///  两者不冲突 —— **现在的方案是保留原有轮询，额外提供 Trigger 入口**，
///  这样既不会改坏现有逻辑，又能在 AEAssist 的时间轴编辑器里用上。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class 减伤触发器 : ITriggerAction
{
    public string DisplayName => "治疗：铺减伤";

    public string Remark { get; set; } = "";

    /// <summary>不需要配置界面</summary>
    public bool Draw() => false;

    public bool Handle()
    {
        try
        {
            减伤信号.请求();
            return true;
        }
        catch (Exception e)
        {
            LogHelper.Info("[HealerACR.Trigger] 减伤触发失败：" + e.Message);
            return false;
        }
    }
}

/// <summary>
/// 攒资源触发器 —— 让时间轴能在"大招前"通知治疗留资源。
///
/// 场景：Boss 马上要放全屏 AOE，
///   挂着这个触发器 → 治疗停止卸豆 / 不打百合 → 留着应急。
/// </summary>
public class 攒资源触发器 : ITriggerAction
{
    public string DisplayName => "治疗：攒资源";

    public string Remark { get; set; } = "";

    public bool Draw() => false;

    public bool Handle()
    {
        try
        {
            减伤信号.请求攒资源();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 治疗信号 —— Trigger 和 resolver 之间的通信。
///
/// **为什么要中转一层**：
///   Trigger 是 AEAssist 框架回调的，resolver 是我自己队列里的，
///   两者互不知道对方。用这个静态类传信号，
///   谁也不用依赖谁的具体实现。
/// </summary>
public static class 减伤信号
{
    /// <summary>信号有效时长（毫秒）—— 超过就当过期</summary>
    private const int 有效时长 = 3000;

    private static long _减伤请求;
    private static long _攒资源请求;

    /// <summary>请求铺减伤（由 Trigger 调用）</summary>
    public static void 请求() => _减伤请求 = TimeHelper.Now();

    /// <summary>请求攒资源</summary>
    public static void 请求攒资源() => _攒资源请求 = TimeHelper.Now();

    /// <summary>现在有减伤请求吗</summary>
    public static bool 该减伤()
    {
        try { return TimeHelper.Now() - _减伤请求 < 有效时长; }
        catch { return false; }
    }

    /// <summary>现在有攒资源请求吗</summary>
    public static bool 该攒资源()
    {
        try { return TimeHelper.Now() - _攒资源请求 < 有效时长; }
        catch { return false; }
    }

    public static void 重置()
    {
        _减伤请求 = 0;
        _攒资源请求 = 0;
    }
}
