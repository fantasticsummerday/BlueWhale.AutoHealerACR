using System.Reflection;

namespace HealerACR.Common;

/// <summary>
/// **指标钩子** —— 让本地的 `AfterSpell` 能把"放了一次治疗"告诉 BlueWhale 的 `战斗指标`。
///
/// ══════════════════════════════════════════════════════════════════════
///  [!] 为什么必须走反射
///
///      `HealerACR` **不能编译期引用** `BlueWhale.AutoHealerACR`
///      （依赖方向是单向的：BlueWhale 引用 HealerACR 的源码）。
///      这和 `调试窗` / `阈值钩子` / `卸载钩子` 是同一套办法。
///
///  [!] 为什么记在这里而不是 AI 那条路
///
///      `AfterSpell` 是**本地和 AI 共用的出口** —— 在它里面记，
///      天然同时覆盖两条路，不会出现"只统计到 AI 放的"这种偏差。
///
///  [!] 恢复力在**这一侧**查好再传过去
///
///      `HealerACR` 手里有完整的技能表（`治疗候选` 的恢复力是人工核对过的），
///      而 BlueWhale 侧要拿同一份数据还得再反射一次。
///      在这边查一次更省事，也避开了"两边查到不同数字"的风险。
///
///  [!] 拿不到就静默跳过 —— 指标是**辅助**，绝不能影响战斗。
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public static class 指标钩子
{
    private static MethodInfo? _方法;
    private static bool _找过;

    /// <summary>记一次治疗落点（非治疗技/查不到恢复力时自动跳过）。</summary>
    public static void 记治疗(uint 技能Id)
    {
        try
        {
            if (技能Id == 0) return;

            // 恢复力：从当前职业的技能表查（和"选哪个治疗"用的是同一份数据）
            var 恢复力 = 取恢复力(技能Id);
            if (恢复力 <= 0f) return;      // 不是治疗技 -> 不记

            var m = 取方法();
            if (m == null) return;
            m.Invoke(null, new object?[] { 恢复力 });
        }
        catch { }
    }

    private static float 取恢复力(uint 技能Id)
    {
        try
        {
            var 表 = HealRotationEventHandler.取当前职业技能表();
            if (表 == null) return 0f;

            // ① 先在"治疗候选"里查 —— 那里的恢复力是人工核对过的完整数据
            foreach (var h in 表.治疗候选.全部)
                if (h != null && h.Id == 技能Id) return h.总恢复力;

            // ② 再试通用入口（它内部也会先查治疗候选，然后落回别的槽位）
            return 表.查威力(技能Id, (int)AEAssist.Core.Me.Level);
        }
        catch { return 0f; }
    }

    private static MethodInfo? 取方法()
    {
        if (_找过) return _方法;
        _找过 = true;
        try
        {
            var 类型 = Type.GetType("BlueWhale.AutoHealerACR.战斗指标, BlueWhale");
            if (类型 == null) return null;
            _方法 = 类型.GetMethod("记治疗落点",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(float) }, null);
        }
        catch { }
        return _方法;
    }
}
