using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// 决策层（阶段 B）—— AI 出**具体技能建议**。
///
/// ══════════════════════════════════════════════════════════════════
///  ⚠️ 核心设计：**预取多步**（不要"现问现用"）
///
///  第一版是"每次都现问下一个 GCD"：
///      GCD 2.5 秒 → 问 AI 要 1.5~3 秒 → 答案回来时 GCD 早过了 → 过期
///      实测统计：命中 681 / 过期 875 —— **一半以上是白问的**
///
///  改成本版的思路：**让 AI 提前备货**
///      · 一次请求让它给出**接下来 3 步**的技能序列（不是一个）
///      · 后台持续维持队列里有 2~3 条待用建议
///      · 主循环从队列头部取 —— 取的时候它通常已经躺在那儿了
///
///  这样一来，"AI 响应慢" 就不致命的 ——
///  因为**它是在为未来备货，而不是为当下救火**。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class AiDecisionLayer
{
    /// <summary>一条 AI 建议</summary>
    public sealed class 建议
    {
        public uint 技能Id;
        public string 理由 = "";
        public long 生成时间;

        /// <summary>
        /// 有效期：**按 GCD 数算**（比按毫秒合理）。
        /// 一个 GCD 2.5 秒，给 2 个 GCD 的容忍度 = 5 秒。
        /// </summary>
        public const long 有效期毫秒 = 5000;

        public bool 还新鲜 => TimeHelper.Now() - 生成时间 <= 有效期毫秒;
    }

    /// <summary>预取队列（先进先出）</summary>
    private static readonly Queue<建议> _队列 = new();

    /// <summary>队列维持的目标长度 —— 低于这个数就去补货</summary>
    private const int 目标长度 = 3;

    /// <summary>一次请求让 AI 给几步</summary>
    private const int 每次请求步数 = 3;

    private static long _上次预取;
    private static bool _预取中;
    private static long _上次局面变化;   // 局面剧变时强制清空队列重来

    public static bool 预取中 => _预取中;
    public static int 队列长度 => _队列.Count;

    /// <summary>队首建议（不消费），给 UI 显示用</summary>
    public static 建议? 当前建议
    {
        get
        {
            foreach (var s in _队列)
            {
                if (s.还新鲜) return s;
            }
            return null;
        }
    }

    /// <summary>命中率统计</summary>
    public static int 命中次数 { get; private set; }
    public static int 过期次数 { get; private set; }

    // ==================== 每帧驱动 ====================

    /// <summary>每帧调用（很轻：只维护队列 + 到点启动一次后台任务）</summary>
    public static void 每帧更新()
    {
        var s = AiSettings.Instance;

        if (!s.启用决策层 || !s.已配置) return;

        // ① 清掉队首的过期建议
        清理过期();

        if (_预取中) return;
        if (DeepSeekClient.熔断中) return;

        // ② 队列够长 → 不用补货（这是"预取"的关键：有货就不问）
        if (_队列.Count >= 目标长度) return;

        // ③ 补货节流
        var 间隔 = Math.Max(300, s.决策预取毫秒);
        if (TimeHelper.Now() - _上次预取 < 间隔) return;

        _上次预取 = TimeHelper.Now();
        _ = 预取();
    }

    private static void 清理过期()
    {
        // 队列是 FIFO，过期的都在前面
        while (_队列.Count > 0)
        {
            var 头 = _队列.Peek();
            if (头.还新鲜) break;

            _队列.Dequeue();
            过期次数++;
        }
    }

    /// <summary>
    /// 局面剧变（换目标 / 血量骤降 / 进战脱战）时清空队列。
    ///
    /// **为什么需要**：预取的本质是"赌局面不变"。
    /// 局面一变，队列里剩下的建议就全是错的 ——
    /// **过期的建议比没有建议更危险**（会做出与当前战场无关的决策）。
    /// </summary>
    public static void 局面剧变()
    {
        try
        {
            if (_队列.Count == 0) return;

            _队列.Clear();
            _上次局面变化 = TimeHelper.Now();
        }
        catch { }
    }

    // ==================== 预取 ====================

    private static async Task 预取()
    {
        _预取中 = true;
        try
        {
            var 局面 = AiSituation.采集();

            // 决策层：给 3 秒 —— 它是在"备货"，不必卡在 GCD 内
            // 超时 8 秒 —— deepseek-flash 是**推理型模型**（日志里能看到"正文来自 reasoning_content"），
            // 它会先思考再回答，通常 3~10 秒。
            // 反正是预取备货，慢一点没关系：备好了等着用，比"快但总超时"强。
            var 回复 = await DeepSeekClient.提问(系统提示, 局面, 8000).ConfigureAwait(false);

            if (回复 == null) return;

            var 条数 = 解析并填充(回复);

            if (条数 > 0)
            {
                Ai调试.日志($"预取 {条数} 步（队列 {_队列.Count}/{目标长度}）");
            }
        }
        catch (Exception e)
        {
            Ai调试.日志("决策预取异常（已忽略）：" + e.Message);
        }
        finally
        {
            _预取中 = false;
        }
    }

    // ==================== 提示词 ====================

    /// <summary>
    /// 系统提示 —— 和策略层同一条铁律：**不许编机制，不许编技能。**
    ///
    /// **改成一口气要 3 步**：这样一次请求就能填满队列，
    /// 不必每个 GCD 都问一次（省 token 又抗延迟）。
    /// </summary>
    private static readonly string 系统提示 = $"""
        你是 FF14 治疗职业的战斗决策助手。
        根据【数据】里的局面，给出**接下来 {每次请求步数} 个 GCD** 最该放的技能，按先后顺序。

        ═══ 铁律 ═══
        1. 你【不了解】这个副本的机制。不要猜测、不要回忆、不要描述机制。
        2. 只能从【可选技能】清单里选技能 ID。清单外的 ID 一律不许输出。
        3. 数据不足就输出：0|数据不足
        4. 理由必须对应【数据】里某一项。
        5. 禁止编造技能名或机制名。
        6. 后面的步骤是**预判**：如果前面几步之后局面可能变化，按你的判断给最合理的后续。
           不确定就重复当前最优解，**不要为了凑数编技能**。

        ═══ 输出格式 ═══
        每行一条，最多 {每次请求步数} 行：
        技能ID|理由

        ═══ 示例 ═══
        16534|低于35%有2人，需要群抬
        16534|血线还没拉回来，继续群抬
        16540|血线稳了，补DoT
        """;

    // ==================== 解析 + 白名单 ====================

    private static int 解析并填充(string 回复)
    {
        var 条数 = 0;

        try
        {
            var 行 = 回复.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var 原始行 in 行)
            {
                if (条数 >= 每次请求步数) break;

                var 段 = 原始行.Trim().Split('|', 2);
                if (段.Length == 0) continue;

                if (!uint.TryParse(段[0].Trim(), out var id)) continue;
                if (id == 0) continue;   // AI 说"数据不足"，跳过这行

                // ★ 白名单校验：防幻觉的最后一道闸 ★
                if (!在可选清单里(id))
                {
                    Ai调试.日志($"建议的技能 {id} 不在可选清单里，已丢弃（疑似幻觉）");
                    continue;
                }

                _队列.Enqueue(new 建议
                {
                    技能Id = id,
                    理由 = 段.Length > 1 ? 段[1].Trim() : "",
                    生成时间 = TimeHelper.Now(),
                });

                条数++;
            }
        }
        catch (Exception e)
        {
            Ai调试.日志("建议解析异常：" + e.Message);
        }

        return 条数;
    }

    // ==================== 消费 ====================

    /// <summary>
    /// 取建议（**出队**）。返回 null 表示"没有可用建议"。
    ///
    /// ⚠️ **不要在 Check() 里调这个** —— 会导致逻辑陷阱：
    ///
    ///    Check 出队 A → 返回高分
    ///      ↓
    ///    如果这一帧被更高优先级的 resolver 抢先，Build 没被调用
    ///      → **A 已经出队，永久丢失** ❌
    ///
    ///    而且 Build 里再读"当前建议"（Peek）时已经不是 A 了
    ///      → **Check 判的是 A，Build 放的可能是 B** ❌
    ///
    ///  正确用法：
    ///    Check → 用 当前建议（只看）
    ///    Build → 用 当前建议 + 放完之后调 消费()
    /// </summary>
    public static 建议? 取建议()
    {
        清理过期();

        if (_队列.Count == 0) return null;

        var s = _队列.Dequeue();
        命中次数++;
        return s;
    }

    /// <summary>
    /// 消费队首建议（**技能真的放出去了才调**）。
    ///
    /// 这是 Check/Build 分离的正确做法：
    ///   Check 只看（Peek）→ 不动队列
    ///   Build 放成功后 → 调这个出队
    /// </summary>
    public static void 消费()
    {
        try
        {
            清理过期();
            if (_队列.Count == 0) return;

            _队列.Dequeue();
            命中次数++;
        }
        catch { }
    }

    public static void 重置()
    {
        _队列.Clear();
        _上次预取 = 0;
        _预取中 = false;
        命中次数 = 0;
        过期次数 = 0;
    }

    /// <summary>
    /// 这个 ID 是不是当前职业真的会用的技能。
    ///
    /// **白名单是防幻觉的硬手段** —— 提示词只是"请求"它别编，
    /// 这里是"验证"它有没有编。两道一起上才稳。
    /// </summary>
    private static bool 在可选清单里(uint id)
    {
        if (id == 0) return false;

        try
        {
            var 表 = HealerACR.Common.HealRotationEventHandler.取当前职业技能表();
            if (表 == null) return true;   // 拿不到表就不拦（宁可放过也别全丢）

            if (id == 表.基础输出) return true;
            if (id == 表.群体输出) return true;
            if (id == 表.Dot技能) return true;
            if (id == 表.移动填充技) return true;
            if (id == 表.单体治疗GCD) return true;
            if (id == 表.群体治疗GCD) return true;
            if (id == 表.紧急单奶) return true;
            if (id == 表.群体治疗能力技) return true;
            if (id == 表.单体盾) return true;
            if (id == 表.群体盾) return true;
            if (id == 表.团队减伤) return true;
            if (id == 表.复活) return true;
            if (id == 表.驱散) return true;
            if (id == 表.醒梦) return true;

            if (表.输出能力技 != null && Array.IndexOf(表.输出能力技, id) >= 0) return true;
            if (表.脱战准备技能 != null && Array.IndexOf(表.脱战准备技能, id) >= 0) return true;
            if (表.所有DotBuff != null && Array.IndexOf(表.所有DotBuff, id) >= 0) return true;

            return false;
        }
        catch
        {
            return true;
        }
    }
}
