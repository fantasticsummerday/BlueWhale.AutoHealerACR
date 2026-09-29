using System.Text;
using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// 策略层（阶段 A）—— AI 低频决策。
///
/// **它不改具体技能，只给"倾向性建议"。** 比如：
///   · 治疗阈值该调高还是调低
///   · 豆子/百合该攒着还是该卸
///   · 当前该保守（保命优先）还是该激进（输出优先）
///
/// **关键在于异步**：后台问 AI，答案存下来给主循环读 ——
/// 主循环永远不阻塞，AI 慢几秒也没关系（这正是策略层适合先做的原因）。
///
/// 参考同类 ACR 的 `IsFairyGaugeReduced`：它也用 `AsyncVoidMethodBuilder`
/// 做异步状态检测，思路一致 —— **别在战斗线程里等网络**。
/// </summary>
public static class AiStrategyLayer
{
    /// <summary>AI 给出的整体倾向</summary>
    public enum 倾向
    {
        未知 = 0,
        保守,     // 保命优先：阈值调高、资源留着
        均衡,     // 按默认走
        激进,     // 输出优先：阈值调低、资源卸掉
    }

    private static 倾向 _当前倾向 = 倾向.未知;
    private static string _说明 = "";
    private static long _上次刷新;
    private static bool _刷新中;

    public static 倾向 当前倾向 => _当前倾向;

    /// <summary>上次成功刷新的时间戳（0 = 从没成功过）</summary>
    public static long 上次成功时间 { get; private set; }

    /// <summary>上次请求的结果描述（成功/失败原因）</summary>
    public static string 上次结果 { get; private set; } = "（还没请求过）";

    /// <summary>累计成功次数</summary>
    public static int 成功次数 { get; private set; }

    /// <summary>最近的几次请求结果（面板上展示，最多留 8 条）</summary>
    public static readonly List<string> 历史 = new();
    public static string 说明 => _说明;
    public static bool 刷新中 => _刷新中;

    /// <summary>
    /// 每帧调用（很轻 —— 只在到点时启动一次后台任务）。
    /// </summary>
    public static void 每帧更新()
    {
        var s = AiSettings.Instance;

        if (!s.启用策略层 || !s.已配置) return;
        if (_刷新中) return;                       // 上一次还没回来
        if (DeepSeekClient.熔断中) return;          // 熔断期，别浪费请求

        var 间隔 = Math.Max(3, s.策略刷新秒) * 1000L;
        if (TimeHelper.Now() - _上次刷新 < 间隔) return;

        _上次刷新 = TimeHelper.Now();
        _ = 刷新();
    }

    private static void 记历史(string 内容)
    {
        try
        {
            var 时间 = DateTime.Now.ToString("HH:mm:ss");
            历史.Add($"[{时间}] {内容}");

            // 只留最近 8 条
            while (历史.Count > 8) 历史.RemoveAt(0);
        }
        catch { }
    }

    private static async Task 刷新()
    {
        _刷新中 = true;
        try
        {
            var 局面 = 收集局面();
            var 回复 = await DeepSeekClient.提问(系统提示, 局面, 10000).ConfigureAwait(false);   // 策略层：提示词长，给足 10 秒

            if (回复 == null)
            {
                // 失败：保持上一次的结论不变（比清空更安全）
                上次结果 = "请求失败（见上方日志）";
                记历史("❌ 请求失败");
                return;
            }

            if (解析(回复, out var 倾向, out var 说明))
            {
                if (倾向 != _当前倾向)
                {
                    Ai调试.日志($"策略切换：{_当前倾向} → {倾向}（{说明}）");
                }

                _当前倾向 = 倾向;
                _说明 = 说明;
                上次成功时间 = TimeHelper.Now();
                成功次数++;
                上次结果 = $"成功：{倾向}（{说明}）";
                记历史($"✅ {倾向}｜{说明}");
            }
        }
        catch (Exception e)
        {
            Ai调试.日志("策略刷新异常（已忽略）：" + e.Message);
        }
        finally
        {
            _刷新中 = false;
        }
    }

    // ==================== 提示词 ====================

    /// <summary>
    /// 系统提示 —— **防幻觉是这里最重要的设计目标**。
    ///
    /// 核心认知：**AI 不可能"知道"FF14 副本机制。**
    ///   它是通用模型，具体副本的机制不在它的可靠知识里。
    ///   让它"回忆"机制 = 必然编造（幻觉）。
    ///
    /// 所以这里的策略是：
    ///   1. **只给事实** —— 血量/资源/时间轴，全是游戏里读出来的真实数据
    ///   2. **明确告知它的知识边界** —— "你不了解这个副本"
    ///   3. **禁止推测机制** —— 数据里没写的不许说
    ///   4. **允许说"不知道"** —— 给低置信度时一个安全出口
    /// </summary>
    private const string 系统提示 = """
        你是 FF14 治疗职业的战斗策略助手。
        根据【数据】里的局面，判断治疗应该采取的整体倾向。

        ═══ 铁律（违反即视为错误输出）═══
        1. 你【不了解】任何 FF14 副本的机制。绝对不要描述、猜测、回忆
           任何副本机制、Boss 技能、时间轴节点。
        2. 只根据【数据】里明确写出的内容判断。
           【数据】里没写的信息，一律视为"不知道"。
        3. 禁止编造技能名、机制名、Boss 名。禁止使用"可能""也许会"
           来引入数据里没有的内容。
        4. 如果【数据】不足以判断，直接输出：均衡|数据不足
        5. 理由必须能对应到【数据】里某一项。对应不上就别写。

        ═══ 输出格式 ═══
        倾向|一句话理由

        倾向只能是：保守 / 均衡 / 激进

        ═══ 示例 ═══
        保守|队伍血线普遍偏低（低于70%有3人），优先保命
        激进|全队满血且时间轴显示未来5秒无减伤需求，可以多输出
        均衡|数据不足
        """;

    private static string 收集局面()
    {
        // 统一走 AiSituation —— 那里把"游戏里实际发生了什么"尽量采全
        return AiSituation.采集();
    }

    // ==================== 解析 ====================

    private static bool 解析(string 回复, out 倾向 结果, out string 说明)
    {
        结果 = 倾向.未知;
        说明 = "";

        try
        {
            var 行 = 回复.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (行.Length == 0) return false;

            var 首行 = 行[0].Trim();
            var 段 = 首行.Split('|', 2);

            var 词 = 段[0].Trim();

            结果 = 词 switch
            {
                "保守" => 倾向.保守,
                "均衡" => 倾向.均衡,
                "激进" => 倾向.激进,
                _ => 倾向.未知,
            };

            if (结果 == 倾向.未知) return false;

            说明 = 段.Length > 1 ? 段[1].Trim() : "";
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>重置（换职业 / 重载时调用）</summary>
    public static void 重置()
    {
        _当前倾向 = 倾向.未知;
        _说明 = "";
        _上次刷新 = 0;
        _刷新中 = false;
        上次成功时间 = 0;
        上次结果 = "（还没请求过）";
        成功次数 = 0;
        历史.Clear();
    }
}
