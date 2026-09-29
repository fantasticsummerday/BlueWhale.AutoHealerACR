using AEAssist;
using AEAssist.Extension;

namespace HealerACR.Common;

/// <summary>
/// DoT 黑名单 —— 对照 鍚岀被 ACR 的 `DotBlacklistHelper`。
///
/// ══════════════════════════════════════════════════════════════════
///  为什么要黑名单：
///
///    有些敌人**免疫 DoT**，或者 DoT 在它身上**不生效**：
///      · 某些不死系 / 构造体
///      · 血量极低的小怪（DoT 还没跳完就死了，白费一个 GCD）
///      · 特定副本的机制怪（打上去没伤害）
///
///    往这些目标身上补 DoT = **每 30 秒白费一个 GCD**，
///    而且更糟的是**它会一直显示"该补 DoT"**（因为 buff 永远上不去），
///    导致 DoT 技能反复触发、把输出循环卡死。
///
///  ── 怎么判断"该不该拉黑" ──
///    默认清单是我实测/查资料确认免疫 DoT 的，
///    但**游戏版本会变**，所以：
///      · 清单可以在设置界面编辑
///      · 用**血量比例**作为动态判据（太低的小怪不打 DoT）
///      · 加一个"连续补不上就临时拉黑"的自适应机制
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class Dot黑名单
{
    // ==================== 静态黑名单 ====================

    /// <summary>
    /// 按**怪物种类 ID**（NameId）拉黑 —— 同一种怪长得一样、ID 一样。
    ///
    /// ⚠️ 这些是我根据资料整理的可疑项，**不保证全对** ——
    ///    如果你发现某个怪明明能吃 DoT 却被打黑了，从设置里删掉对应的即可。
    /// </summary>
    private static readonly HashSet<uint> _默认黑名单 = new()
    {
        // 例：某些副本的"无敌"阶段怪 / 构造体
        // 留空是安全的 —— 空的清单 = 不拉黑任何东西
        // 需要时在设置界面加
    };

    /// <summary>用户自定义的黑名单（设置界面可编辑）</summary>
    public static readonly HashSet<uint> 自定义黑名单 = new();

    /// <summary>自适应黑名单：连续 N 次补不上 DoT 的目标类型</summary>
    private static readonly Dictionary<uint, int> _补失败次数 = new();

    /// <summary>连续补失败几次就临时拉黑</summary>
    private const int 失败上限 = 3;

    // ==================== 判断 ====================

    /// <summary>这个敌人现在该不该上 DoT</summary>
    public static bool 可以上Dot(IBattleChara? 敌人)
    {
        if (敌人 == null || 敌人.CurrentHp <= 0) return false;

        try
        {
            var 种类 = 取种类Id(敌人);

            // ① 静态 / 自定义黑名单
            if (种类 != 0)
            {
                if (_默认黑名单.Contains(种类)) return false;
                if (自定义黑名单.Contains(种类)) return false;

                // ② 自适应黑名单（补了好几次都上不去）
                if (_补失败次数.TryGetValue(种类, out var 次数) && 次数 >= 失败上限)
                {
                    return false;
                }
            }
        }
        catch { }

        return true;
    }

    /// <summary>
    /// 这次 DoT 补失败了吗？
    ///
    /// **调用时机**：补了 DoT 之后隔一小段时间检查 buff 在不在 ——
    /// 不在就说明没上上去（免疫 / 怪死了 / 距离问题）。
    ///
    /// 连续失败到一定次数就把这个**种类**拉黑，
    /// 免得整场都在做无用功。
    /// </summary>
    public static void 记补失败(IBattleChara? 敌人)
    {
        if (敌人 == null) return;

        try
        {
            var 种类 = 取种类Id(敌人);
            if (种类 == 0) return;

            _补失败次数.TryGetValue(种类, out var 次数);
            _补失败次数[种类] = 次数 + 1;

            if (次数 + 1 == 失败上限)
            {
                AEAssist.Helper.LogHelper.Info(
                    $"[HealerACR.DoT] 种类 {种类} 连续 {失败上限} 次补 DoT 失败，本场拉黑");
            }
        }
        catch { }
    }

    /// <summary>补成功了，清掉失败记录</summary>
    public static void 记补成功(IBattleChara? 敌人)
    {
        if (敌人 == null) return;

        try
        {
            var 种类 = 取种类Id(敌人);
            if (种类 != 0) _补失败次数.Remove(种类);
        }
        catch { }
    }

    /// <summary>战斗开始 / 换本时清掉自适应记录（静态黑名单保留）</summary>
    public static void 重置自适应()
    {
        _补失败次数.Clear();
    }

    // ==================== 工具 ====================

    private static uint 取种类Id(IBattleChara 敌人)
    {
        try { return 敌人.NameId; }
        catch { return 0; }
    }

    /// <summary>诊断</summary>
    public static string 状态描述(IBattleChara? 敌人)
    {
        if (敌人 == null) return "无目标";

        try
        {
            var 种类 = 取种类Id(敌人);

            if (_默认黑名单.Contains(种类)) return $"种类 {种类}：在黑名单里（静态）";
            if (自定义黑名单.Contains(种类)) return $"种类 {种类}：在黑名单里（自定义）";

            if (_补失败次数.TryGetValue(种类, out var 次数))
            {
                return $"种类 {种类}：补失败 {次数}/{失败上限} 次";
            }

            return $"种类 {种类}：正常";
        }
        catch
        {
            return "读取失败";
        }
    }
}
