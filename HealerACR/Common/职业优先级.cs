using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.Extension;
using Dalamud.Bindings.ImGui;

namespace HealerACR.Common;

/// <summary>
/// 职业优先级 —— 决定"血线差不多时先救谁"。
///
/// ══════════════════════════════════════════════════════════════════
///  为什么需要它：
///
///    奶妈经常面对这种情况：**两个人同时掉到 60%**，
///    只能先给一个治疗 —— 先给谁？
///
///    纯按"血量最低"排会有问题：
///      · 坦克血线波动大是常态，它 60% 其实很安全
///      · 奶妈躺了全队断治疗，但它的血线往往不是最低的
///      · DPS 60% 可能下一秒就吃机制死了
///
///    所以需要一个**可配置的优先级**，而不是纯看血量。
///
///  ── 怎么用 ──
///    在 QT 面板的「优先级」页里拖排序，数字越小越优先。
///    治疗选目标时：**先按优先级分层，层内再比血量**。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 职业优先级
{
    /// <summary>
    /// 优先级权重（越小越优先）。
    ///
    /// 默认顺序的考虑：
    ///   1. 自己 —— 我死了全队没治疗
    ///   2. 奶妈 —— 搭档死了治疗量直接减半
    ///   3. 坦克 —— 它死了怪就打别人了
    ///   4. 近战 —— 站在怪堆里，最容易吃 AOE
    ///   5. 远程/法系 —— 站得远，相对安全
    /// </summary>
    public static int 自己 = 1;
    public static int 奶妈 = 2;
    public static int 坦克 = 3;
    public static int 近战 = 4;
    public static int 远程 = 5;
    public static int 法系 = 6;
    public static int 其他 = 7;

    /// <summary>是否启用优先级（关掉就纯按血量选）</summary>
    public static bool 启用 = true;

    /// <summary>取某个队友的优先级权重</summary>
    public static int 取权重(IBattleChara? c)
    {
        // ★ 入口判有效性：参数是游戏对象，读它的属性会因【已释放对象】而
        //   触发原生访问违例（穿 catch / 无转储 / 进程直接没）。
        //   本项目 12 次崩溃全部是这一类 —— 不假设调用方判过。
        if (c == null || !c.对象有效()) return 0;
        if (c == null) return 其他;
        if (!启用) return 0;   // 不启用 → 全部同权，等于只比血量

        try
        {
            // 自己最优先
            if (c.GameObjectId == CharacterExt.我的ObjectId()) return 自己;

            if (职业表.是奶妈(c)) return 奶妈;
            if (职业表.是坦克(c)) return 坦克;
            if (职业表.是近战(c)) return 近战;
            if (职业表.是远程(c)) return 远程;
            if (职业表.是法系(c)) return 法系;
        }
        catch { }

        return 其他;
    }

    /// <summary>
    /// 综合评分：**越小越该先救**。
    ///
    /// 算法：优先级 × 100 + 血量 × 100
    ///   → 优先级差 1 级 = 需要血量差 100% 才能抵消
    ///
    /// 也就是说 **优先级是硬分层** —— 只要优先级高一级，血量再高也先救它。
    /// 这符合直觉："奶妈 90% 血 + DPS 20% 血" 时，
    /// 你其实还是想先看 DPS —— 所以这个算法可能太硬。
    ///
    /// 折中：**只在血量差不超过 15% 时才让优先级起作用**。
    /// </summary>
    public static float 评分(IBattleChara? c)
    {
        if (c == null) return float.MaxValue;

        try
        {
            var 血量 = c.CurrentHpPercent();
            var 权重 = 取权重(c);

            // 血量低到一定程度 → 优先级让位（快死的人最优先）
            if (血量 < 0.35f) return 血量;

            // 血量差在 15% 以内 → 按优先级
            // 实现方式：把优先级折算成"最多 0.15 的血量偏移"
            return 血量 + 权重 * 0.02f;
        }
        catch
        {
            return float.MaxValue;
        }
    }

    /// <summary>画优先级设置页（挂在 QT 面板的「优先级」页签）</summary>
    public static void 画()
    {
        ImGui.TextDisabled("决定血线接近时先救谁。数字越小越优先。");
        ImGui.Separator();

        if (ImGui.Checkbox("启用职业优先级", ref 启用))
        {
            HealSettings.Instance.Save();
        }

        if (!启用)
        {
            ImGui.TextDisabled("  （关掉后纯按血量选目标）");
            return;
        }

        ImGui.NewLine();
        ImGui.TextDisabled("优先级（1 = 最优先）");

        var 变了 = false;

        var 项 = new (string 名, Func<int> 取, Action<int> 设)[]
        {
            ("自己",   () => 自己, v => 自己 = v),
            ("奶妈",   () => 奶妈, v => 奶妈 = v),
            ("坦克",   () => 坦克, v => 坦克 = v),
            ("近战",   () => 近战, v => 近战 = v),
            ("远程物理", () => 远程, v => 远程 = v),
            ("法系",   () => 法系, v => 法系 = v),
            ("其他",   () => 其他, v => 其他 = v),
        };

        foreach (var (名, 取, 设) in 项)
        {
            var 值 = 取();
            ImGui.SetNextItemWidth(120);

            if (ImGui.SliderInt(名, ref 值, 1, 9))
            {
                设(值);
                变了 = true;
            }
        }

        if (变了) HealSettings.Instance.Save();

        ImGui.NewLine();
        ImGui.Separator();
        ImGui.TextDisabled("规则说明：");
        ImGui.TextDisabled("  · 血量低于 35% -> 无视优先级，谁快死了救谁");
        ImGui.TextDisabled("  · 血量差在 15% 以内 -> 按上面的优先级");
        ImGui.TextDisabled("  · 血量差超过 15% -> 救血量更低的");
    }

    /// <summary>恢复默认</summary>
    public static void 恢复默认()
    {
        自己 = 1;
        奶妈 = 2;
        坦克 = 3;
        近战 = 4;
        远程 = 5;
        法系 = 6;
        其他 = 7;
    }
}
