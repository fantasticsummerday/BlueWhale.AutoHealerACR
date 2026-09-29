using AEAssist;
using AEAssist.Helper;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// 把 AI 的「倾向」翻译成具体的**阈值调整**。
///
/// 这是阶段 A 真正落地的地方 —— AI 说什么，这里就改什么。
/// 所有调整都是**相对量**，原值来自 HealerACR 的设置，AI 只是在上面加减。
///
/// **为什么用相对量而不是绝对值**：
///   AI 不知道你的装备/副本/习惯，让它直接定"治疗阈值 = 0.82"很容易离谱。
///   但让它说"该保守一点"，然后我在你的基础上 +0.08 —— 这个幅度是可控的。
/// </summary>
public static class AiThresholdAdapter
{
    /// <summary>单次调整的最大幅度（防止 AI 把阈值推到极端）</summary>
    private const float 最大阈值偏移 = 0.10f;
    private const int 最大保留偏移 = 1;

    /// <summary>调整后的单体治疗阈值</summary>
    public static float 单体治疗阈值(float 原值)
    {
        return Math.Clamp(原值 + 偏移(), 0.30f, 0.95f);
    }

    /// <summary>调整后的群体治疗阈值</summary>
    public static float 群体治疗阈值(float 原值)
    {
        return Math.Clamp(原值 + 偏移(), 0.30f, 0.95f);
    }

    /// <summary>
    /// 调整后的以太保留数。
    /// 保守 → 多留（+1）；激进 → 少留（-1，最低 0）。
    /// </summary>
    public static int 以太保留数(int 原值)
    {
        // 用**同一个平滑偏移** —— 这样资源保留也跟着渐变，
        // 不会因为倾向抖一下就从"留 2 颗"跳到"留 1 颗"。
        var 归一 = 偏移() / 最大阈值偏移;                  // 归一化到 [-1, 1]
        var 偏移整 = (int)MathF.Round(归一 * 最大保留偏移);

        return Math.Clamp(原值 + 偏移整, 0, 3);
    }

    /// <summary>调整后的蛇胆保留数（贤者）</summary>
    public static int 蛇胆保留数(int 原值)
    {
        // 用**同一个平滑偏移** —— 这样资源保留也跟着渐变，
        // 不会因为倾向抖一下就从"留 2 颗"跳到"留 1 颗"。
        var 归一 = 偏移() / 最大阈值偏移;                  // 归一化到 [-1, 1]
        var 偏移整 = (int)MathF.Round(归一 * 最大保留偏移);

        return Math.Clamp(原值 + 偏移整, 0, 3);
    }

    /// <summary>
    /// 是否应该"卸资源换输出"。
    /// 只有激进倾向才允许（保守时哪怕豆子满了也留给救急）。
    /// </summary>
    public static bool 允许卸资源 => AiStrategyLayer.当前倾向 == AiStrategyLayer.倾向.激进;

    // ==================== 平滑过渡（"取中间值"）====================

    /// <summary>实际生效的偏移 —— 会平滑地逼近目标，不是一下子就跳过去</summary>
    private static float _当前偏移;

    /// <summary>本次平滑的时间戳</summary>
    private static long _上次平滑;

    /// <summary>平滑系数：每秒把剩余差距消掉 70%</summary>
    private const float 平滑速率 = 0.7f;

    /// <summary>
    /// AI 给的倾向对应的**目标**偏移。
    /// 这只是"最终要去的地方"，不会立刻生效。
    /// </summary>
    private static float 目标偏移()
    {
        return AiStrategyLayer.当前倾向 switch
        {
            AiStrategyLayer.倾向.保守 => 最大阈值偏移,
            AiStrategyLayer.倾向.激进 => -最大阈值偏移,
            _ => 0f,
        };
    }

    /// <summary>
    /// 阈值偏移量 —— **平滑逼近目标**。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ⚠️ 为什么要平滑（"取一个中间值"）
    ///
    ///  原来是直接返回目标值：AI 说"激进"→ 阈值立刻 -0.10，
    ///  10 秒后说"保守"→ 又立刻 +0.10。
    ///  而 AI 的判断本来就在小幅波动（日志里能看到 10 秒内 激进→均衡→保守），
    ///  结果阈值跟着来回横跳，治疗节奏也跟着抖。
    ///
    ///  改成按时间指数逼近之后：
    ///      · 倾向切换 → 阈值**渐进**移动，中间经过所有中间值
    ///      · 短暂波动 → 只走一小段就回头，**几乎看不出来**
    ///      · 倾向稳定 → 几秒内就到位，不会迟钝
    ///
    ///  本质上是给 AI 的决策加了个**低通滤波器** ——
    ///  保留趋势，滤掉抖动。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static float 偏移()
    {
        try
        {
            var 目标 = 目标偏移();
            var 现在 = TimeHelper.Now();

            // 第一次调用：直接对齐（否则会从 0 慢慢爬）
            if (_上次平滑 == 0)
            {
                _当前偏移 = 目标;
                _上次平滑 = 现在;
                return _当前偏移;
            }

            var 间隔秒 = (现在 - _上次平滑) / 1000f;
            if (间隔秒 <= 0) return _当前偏移;

            _上次平滑 = 现在;

            // 指数逼近：间隔越长，走得越多
            //   系数 = 1 - 0.7^间隔
            //     0.1 秒 → 约 3.5%
            //     1   秒 → 30%
            //     3   秒 → 66%
            var 系数 = 1f - MathF.Pow(平滑速率, 间隔秒);
            _当前偏移 += (目标 - _当前偏移) * 系数;

            // 靠得足够近就直接吸附，避免无限逼近
            if (MathF.Abs(目标 - _当前偏移) < 0.002f) _当前偏移 = 目标;

            return _当前偏移;
        }
        catch
        {
            return 目标偏移();
        }
    }

    /// <summary>倾向切换时重置平滑（换本 / 战斗重置用）</summary>
    public static void 重置平滑()
    {
        _上次平滑 = 0;
        _当前偏移 = 0;
    }

    /// <summary>给面板显示用的一句话</summary>
    public static string 状态描述()
    {
        var s = AiSettings.Instance;

        // ---- 逐层检查为什么"不生效"，而不是笼统说"未启用" ----
        // ⚠️ 这些字符串会显示在游戏内的 AI 设置页上，
        //    所以**用纯文本编号**（1) 2) …），不要用 ①②③ 这类符号 ——
        //    FF14 的界面字体对它们的支持没有保证。
        if (!s.已配置)
        {
            return "1) 未配置 API Key —— 走原版阈值（这是正常的默认状态）";
        }

        if (!s.启用策略层)
        {
            return "2) Key 已配置，但【阶段 A：策略层】没勾 —— 勾上才会开始请求";
        }

        if (DeepSeekClient.该走原版逻辑)
        {
            return $"3) {DeepSeekClient.状态描述()}（冷却 {s.失败冷却秒} 秒）—— 期间走原版阈值";
        }

        var 当前 = AiStrategyLayer.当前倾向;

        if (当前 == AiStrategyLayer.倾向.未知)
        {
            if (AiStrategyLayer.刷新中)
            {
                return "4) 已启用，首次请求进行中…（最多等 " + s.超时毫秒 + " 毫秒）";
            }

            if (AiStrategyLayer.上次成功时间 == 0)
            {
                var 距今 = AiStrategyLayer.成功次数 == 0 ? "还没成功过" : "已成功过";
                return "5) 已启用，但还没有有效回复（" + 距今 + "）—— 上次结果：" + AiStrategyLayer.上次结果;
            }
        }

        var 量 = 偏移();
        var 符号 = 量 > 0 ? "+" : "";

        return $"生效中｜倾向 {当前}｜阈值 {符号}{量:F2}｜{AiStrategyLayer.说明}" +
               $"｜累计成功 {AiStrategyLayer.成功次数} 次";
    }
}

// ============================================================================
//  钩子挂载 —— 让阶段 A 真正生效
// ============================================================================

/// <summary>
/// 把 AI 的阈值调整挂到 HealerACR 的钩子上。
///
/// **只有挂上之后 AI 才有实际影响**；不挂 = 完全等同原版。
/// </summary>
public static class AiHookInstaller
{
    private static bool _已挂;

    /// <summary>挂上钩子（入口 Build 时调用）</summary>
    public static void 挂载()
    {
        if (_已挂) return;

        try
        {
            HealerACR.Common.阈值钩子.治疗阈值调整 = AiThresholdAdapter.单体治疗阈值;
            _已挂 = true;
            Ai调试.日志("阈值钩子已挂载 —— AI 的保守/激进会影响实际治疗阈值");
        }
        catch (Exception e)
        {
            LogHelper.Error("[BlueWhale.AI] 阈值钩子挂载失败（AI 将只显示不生效）：" + e.Message);
        }
    }

    /// <summary>卸载（退出/换职业时调用，避免影响其他 ACR）</summary>
    public static void 卸载()
    {
        if (!_已挂) return;

        try
        {
            HealerACR.Common.阈值钩子.卸载();
            _已挂 = false;
        }
        catch { }
    }

    public static bool 已挂载 => _已挂;
}
