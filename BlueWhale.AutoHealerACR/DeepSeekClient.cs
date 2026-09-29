using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AEAssist.Helper;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// DeepSeek API 客户端。
///
/// **设计原则：绝不让 AI 的失败影响到 ACR 本身。**
///   · 所有方法都返回 null 表示失败，不抛异常给调用方
///   · 有超时（GCD 只有 2.5 秒，不能等）
///   · 连续失败会自动熔断一段时间
/// </summary>
public static class DeepSeekClient
{
    private static readonly HttpClient _http = new()
    {
        // 单个请求的超时由 CancellationToken 控制，这里给个大一点的兜底
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static int _连续失败;
    private static long _熔断到;             // 熔断解除的时间戳（TimeHelper.Now()）

    /// <summary>现在是不是被熔断了（连续失败太多次）</summary>
    public static bool 熔断中 => TimeHelper.Now() < _熔断到;

    public static int 连续失败数 => _连续失败;

    /// <summary>
    /// 最近一次失败的具体原因（成功时清空）。
    ///
    /// **为什么单独暴露**：初始化失败时要告诉用户"为什么失败"，
    /// 只说"失败了"没用 —— 得让他知道是 Key 错、网络不通、还是超时。
    /// </summary>
    public static string 上次失败原因 { get; private set; } = "";

    /// <summary>
    /// 问一次 AI。失败返回 null —— **调用方必须处理 null（降级）**。
    /// </summary>
    /// <param name="systemPrompt">系统提示：告诉它扮演什么角色、输出格式</param>
    /// <param name="userPrompt">当前局面描述</param>
    /// <summary>
    /// 问一次 DeepSeek。
    ///
    /// **超时按调用场景分开给**（重要）：
    ///   · 策略层（低频，10 秒一次）→ 提示词很长（防幻觉铁律 + 六段情境数据），
    ///     AI 要读上千 token 才能回答，**给 10 秒**
    ///   · 决策层（高频，1.5 秒预取）→ 必须在 GCD 内出结果，**给 1.2 秒**
    ///     （注定经常超时，这是这个方案固有的延迟问题）
    ///
    /// ⚠️ 原来两层共用一个 3000ms，结果策略层每次都超时：
    ///     测试连接 0.5 秒能通，策略层 3 秒不够 —— 日志里全是"请求失败（超时）"。
    /// </summary>
    // ==================== 语言限定 ====================

    /// <summary>
    /// 语言要求 —— **所有请求统一追加**，调用方不用各自操心。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么放在这里，而不是写进每个提示词 ★
    ///
    ///    提问（）一共有 5 个调用点（初始化 / 策略层 / 决策层 /
    ///    测试连接 / 记忆库提炼）。**逐处加必然漏** ——
    ///    以后再加一个调用点，就又多一个会回英文的地方。
    ///
    ///    放在这个统一出口，就等于"进 API 之前必经的一道闸"，
    ///    新增调用点自动受约束。
    ///
    ///  ★ 为什么必须强调「包括思考过程」★
    ///
    ///    实测日志里见过这种回复：
    ///      `We need answer in Chinese, format skillID|reason, max 3 lines...`
    ///    —— 它**知道**要中文，但那句是**思考内容泄漏出来了**。
    ///    reasoning 型模型有时会把思考过程混进正文，
    ///    而思考过程默认跟提示词的语言走。
    ///    所以光说"用中文回复"不够，得把思考过程也一起限定。
    ///
    ///  ⚠️ 允许保留的例外：技能 ID、`ID|理由` 这种格式串、
    ///    代码片段 —— 这些是**数据**不是叙述，翻译了反而不能用。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private const string 语言要求 =
        "\n\n【语言要求 · 必须遵守】" +
        "全部输出必须使用**简体中文**，包括推理/思考过程。" +
        "不要输出英文句子。" +
        "（例外：技能 ID 是数字，保持原样；`ID|理由` 这类格式串保持原样；" +
        "不要用代码块包裹中文叙述。）";

    /// <summary>
    /// 在系统提示词末尾追加语言要求。
    ///
    /// ⚠️ 跳过条件必须同时包含「简体中文」+「思考」——
    ///    只写"用简体中文"是不够的（实测泄漏的正是**思考过程**），
    ///    所以那种提示词**仍然要追加**。
    /// </summary>
    private static string 加上语言要求(string systemPrompt)
    {
        if (string.IsNullOrEmpty(systemPrompt)) return 语言要求.TrimStart();

        // 调用方已经把"中文 + 思考过程"都写清楚了 → 不重复
        //   （省 token，也避免提示词变啰嗦）
        if (systemPrompt.Contains("简体中文") && systemPrompt.Contains("思考"))
            return systemPrompt;

        return systemPrompt + 语言要求;
    }

    // ==================== 请求 ====================

    /// <param name="超时毫秒">0 = 用设置里的值</param>
    /// <param name="最大Token">
    /// 输出上限。0 = 用默认的 800（决策类够用）。
    ///
    /// ⚠️ **提炼记忆库必须调大** —— 那是"总结一场完整副本"，
    ///    输出是多条结构化结论，800 token 会**被截断**，
    ///    而且截断点可能正好在句子中间，导致存进记忆库的是半句话。
    /// </param>
    public static async Task<string?> 提问(string systemPrompt, string userPrompt,
                                           int 超时毫秒 = 0, int 最大Token = 0)
    {
        var s = AiSettings.Instance;

        if (!s.已配置) return null;
        if (熔断中) return null;

        try
        {
            var body = new
            {
                model = s.Model,
                messages = new[]
                {
                    new { role = "system", content = 加上语言要求(systemPrompt) },
                    new { role = "user", content = userPrompt },
                },
                // 低温度：我们要的是稳定决策，不是创意
                temperature = 0.2,
                max_tokens = 最大Token > 0 ? 最大Token : 800,
                stream = false,
            };

            // Endpoint 允许只填域名（https://api.deepseek.com），这里补上路径
            var 地址 = s.Endpoint?.TrimEnd('/') ?? "";
            if (!地址.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                地址 += "/chat/completions";
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, 地址);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);
            req.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            // 调用方指定了就用它的，否则用设置里的
            var 本次超时 = 超时毫秒 > 0 ? 超时毫秒 : s.超时毫秒;
            using var cts = new CancellationTokenSource(Math.Max(500, 本次超时));

            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                记失败($"HTTP {(int)resp.StatusCode}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var 消息 = doc.RootElement.GetProperty("choices")[0].GetProperty("message");

            // ⚠️ 推理型模型的正文可能在 reasoning_content，
            //    content 会是空字符串 —— 只读 content 就会误判成"回复为空"。
            //    （日志里那一串"连续失败（回复为空）"就是这么来的。）
            string? 文本 = null;

            if (消息.TryGetProperty("content", out var c))
            {
                文本 = c.GetString();
            }

            if (string.IsNullOrWhiteSpace(文本) && 消息.TryGetProperty("reasoning_content", out var rc))
            {
                文本 = rc.GetString();
                if (!string.IsNullOrWhiteSpace(文本))
                {
                    Ai调试.日志("注意：正文来自 reasoning_content（推理型模型）");
                }
            }

            if (string.IsNullOrWhiteSpace(文本))
            {
                记失败($"回复为空（HTTP 200，但 content 和 reasoning_content 都空。原始：{json.Substring(0, Math.Min(200, json.Length))}）");
                return null;
            }

            // 成功 → 清空失败计数
            _连续失败 = 0;
            上次失败原因 = "";

            if (s.记录原始回复)
            {
                Ai调试.日志("原始回复：" + 文本.Trim());
            }

            return 文本.Trim();
        }
        catch (OperationCanceledException)
        {
            记失败("超时");
            return null;
        }
        catch (Exception e)
        {
            记失败(e.GetType().Name + ": " + e.Message);
            return null;
        }
    }

    private static void 记失败(string 原因)
    {
        _连续失败++;

        // ★ 存下原因 ★ —— 初始化失败时要靠它告诉用户"具体为什么"
        //   （只说"失败了"没法排查：是 Key 错、网络不通、还是超时？）
        上次失败原因 = 原因;

        var s = AiSettings.Instance;

        // ══════════════════════════════════════════════════════════
        //  ⚠️ 超时不能和"链路故障"同等对待。
        //
        //  deepseek-flash 是**推理型模型**（日志里能看到"正文来自 reasoning_content"），
        //  它会先思考再回答，响应 3~10 秒很常见。
        //  决策层为了不阻塞战斗，给 8 秒 —— 仍然可能不够 → **超时是预期行为**。
        //
        //  如果超时也按 3 次熔断，结果是：
        //      决策层超时 → 熔断 → **策略层也被停掉**（两层共用一个熔断器）
        //      → 明明 Key 和网络都好，AI 却完全不工作了。
        //
        //  所以超时给一个宽松得多的上限。
        // ══════════════════════════════════════════════════════════
        var 是超时 = 原因.Contains("超时")
                  || 原因.Contains("Timeout")
                  || 原因.Contains("TaskCanceled")
                  || 原因.Contains("OperationCanceled");

        var 上限 = 是超时 ? Math.Max(s.连续失败上限 * 4, 12) : s.连续失败上限;

        if (_连续失败 >= 上限)
        {
            _熔断到 = TimeHelper.Now() + s.失败冷却秒 * 1000L;
            LogHelper.Error(
                $"[BlueWhale.AI] 连续失败 {_连续失败} 次（{原因}），" +
                $"熔断 {s.失败冷却秒} 秒 —— 期间全部走原有逻辑，不影响战斗。");
        }
        else
        {
            Ai调试.日志($"请求失败（{原因}），第 {_连续失败} 次，走降级。" +
                (是超时 ? $"（超时上限 {上限} 次，属预期行为）" : ""));
        }
    }

    /// <summary>手动解除熔断（设置界面用）</summary>
    public static void 解除熔断()
    {
        _连续失败 = 0;
        _熔断到 = 0;
    }
}
