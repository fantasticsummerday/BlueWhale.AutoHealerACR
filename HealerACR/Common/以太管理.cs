using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// 以太管理 —— 对照 Shiyuvi 的 `AetherflowManager.SetNoAetherflowTrue`。
///
/// ══════════════════════════════════════════════════════════════════
///  ⚠️ 用户实测指出的问题：**「以太保留数」的用途被我搞窄了。**
///
///  我原来的实现是"静态保留"：
///      以太 > 保留数 → 打豆子（能量吸收）
///  这只能保证"永远留着 N 颗"，但**分不清现在是不是有压力** ——
///  平静期也一直在留，白白浪费输出。
///
///  Shiyuvi 的做法是"**动态抑制**"：
///      · 每用掉一颗豆子（不屈/活性法/罩子/契约）→ 记一个时间戳
///      · **7 秒内不再打豆子**
///      · 7 秒后没再动用 → 认为压力过去了，可以打
///
///  **核心洞察**：豆子要留是因为**要应急奶**。
///  而"刚用过豆子"这件事本身就是**有压力的最强信号** ——
///  比任何血量阈值都直接（血线可能已经从低谷涨回来了，
///  但治疗刚花掉的资源还没补上）。
///
///  所以正确做法是**两者结合**：
///      保留数  → 保底（永远留 N 颗）
///      动态抑制 → 自适应（刚用过就多留一会儿）
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 以太管理
{
    /// <summary>
    /// 用掉豆子后的抑制时长（毫秒）。
    /// Shiyuvi 用的是 7 秒 —— 约 2~3 个 GCD，
    /// 正好覆盖"刚用完可能还要再用"的那个窗口。
    /// </summary>
    private const int 抑制时长 = 7000;

    /// <summary>上次观察到以太数量变化的时间</summary>
    private static long _上次用豆子;

    /// <summary>上次看到的以太数量（用来检测"用掉了"）</summary>
    private static int _上次以太 = -1;

    /// <summary>以太超流的 buff id</summary>
    private const uint 以太Buff = 304;

    /// <summary>
    /// 现在是不是处于"刚用过豆子"的抑制期。
    /// **true = 短期内不要再打豆子**（留着应急）。
    /// </summary>
    public static bool 抑制中
    {
        get
        {
            try { return TimeHelper.Now() - _上次用豆子 < 抑制时长; }
            catch { return false; }
        }
    }

    /// <summary>还剩多少毫秒解除抑制（调试显示用）</summary>
    public static int 剩余抑制毫秒
    {
        get
        {
            try
            {
                var 剩 = 抑制时长 - (int)(TimeHelper.Now() - _上次用豆子);
                return Math.Max(0, 剩);
            }
            catch { return 0; }
        }
    }

    /// <summary>当前以太数量</summary>
    public static int 当前以太
    {
        get
        {
            try { return Core.Me.GetAuraStack(以太Buff); }
            catch { return -1; }
        }
    }

    /// <summary>
    /// 每帧调用：**通过观察以太数量变化来自动检测"用掉了豆子"**。
    ///
    /// **为什么用观察而不是让每个技能显式上报**：
    ///   需要上报的地方有 4 个以上（不屈/活性法/罩子/契约/转化…），
    ///   每加一个技能都要记得改，很容易漏。
    ///   而"以太从 3 变成 2"这件事本身**就等价于用掉了一颗**，
    ///   观察它比让所有人上报更可靠。
    /// </summary>
    public static void 每帧更新()
    {
        try
        {
            var 现在 = 当前以太;
            if (现在 < 0) return;

            // 第一次记录，不做判断
            if (_上次以太 < 0)
            {
                _上次以太 = 现在;
                return;
            }

            // 数量变少 → 用掉了豆子
            if (现在 < _上次以太)
            {
                _上次用豆子 = TimeHelper.Now();
            }

            _上次以太 = 现在;
        }
        catch { }
    }

    /// <summary>
    /// 综合判断：**现在该不该打豆子（能量吸收）**。
    ///
    /// 三个条件全部满足才打：
    ///   ① 不处于"刚用过豆子"的抑制期
    ///   ② 以太数量超过保底值
    ///   ③ 当前不是治疗压力场景（由调用方判断）
    /// </summary>
    /// <param name="以太数量">当前以太</param>
    /// <param name="保底">用户设的保留数</param>
    public static bool 可以打豆子(int 以太数量, int 保底)
    {
        if (以太数量 <= 0) return false;

        // ① 动态抑制：刚用过就不打
        if (抑制中) return false;

        // ② 静态保底：留够用户设的数量
        return 以太数量 > Math.Clamp(保底, 0, 3);
    }

    /// <summary>战斗重置</summary>
    public static void 重置()
    {
        _上次用豆子 = 0;
        _上次以太 = -1;
    }
}
