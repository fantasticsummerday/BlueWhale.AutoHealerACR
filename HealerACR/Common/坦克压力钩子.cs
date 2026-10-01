using System.Reflection;

namespace HealerACR.Common;

/// <summary>
/// **坦克压力钩子** —— 让本地的每帧回调能驱动 BlueWhale 的 `坦克压力.每帧更新()`。
///
/// ══════════════════════════════════════════════════════════════════════
///  [!] 为什么必须有它（用户实测「血量波动数据不足、最高100% 最低100%」）
///
///      `坦克压力.每帧更新()` 原来**只有一个调用点** ——
///      `BlueWhaleEntries.cs` 的 AI 心跳里。而心跳在**记录模式**下不进队列
///      （`if (记录模式.开启 && !保留保命兜底) return 队列;`），AI 层没挂上时同理。
///      ==> 采样**永不执行** ==> `最高血量/最低血量` 停在初值 1f/1f
///      ==> 调试窗和 AI 都看到「数据不足｜最高=100% 最低=100%」。
///
///  [!] 这违反项目自己的「开发约定 G：AI 是增强层，本地逻辑不该依赖 AI 存活」。
///      同一个隐患之前已经在 `HealQt.每帧更新()` / `以太管理.每帧更新()` 上修过，
///      `坦克压力` 是**漏掉的那个**。
///
///  [!] 为什么走反射
///      `HealerACR` **不能编译期引用** `BlueWhale.AutoHealerACR`
///      （依赖方向单向：BlueWhale 引用 HealerACR 的源码）。
///      和 `指标钩子` / `调试窗` / `卸载钩子` 是同一套办法。
///
///  [!] 拿不到就静默跳过 —— 单装 HealerACR 时这个类什么都不做。
///      那时候 `坦克压力` 也不存在，采样本来就没意义。
///
///  [!] 重复调用是安全的：`坦克压力.每帧更新()` 内部有 250ms 节流闸门，
///      所以心跳里那次调用**保留**，两边都调也只是多一次空转。
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public static class 坦克压力钩子
{
    private static MethodInfo? _方法;
    private static bool _找过;

    /// <summary>驱动一次采样（拿不到 BlueWhale 侧就什么都不做）。</summary>
    public static void 每帧更新()
    {
        try
        {
            var m = 取方法();
            if (m == null) return;
            m.Invoke(null, null);
        }
        catch { }
    }

    private static MethodInfo? 取方法()
    {
        if (_找过) return _方法;
        _找过 = true;
        try
        {
            var 类型 = Type.GetType("BlueWhale.AutoHealerACR.坦克压力, BlueWhale");
            if (类型 == null) return null;
            _方法 = 类型.GetMethod("每帧更新",
                BindingFlags.Public | BindingFlags.Static, null,
                System.Type.EmptyTypes, null);
        }
        catch { }
        return _方法;
    }
}
