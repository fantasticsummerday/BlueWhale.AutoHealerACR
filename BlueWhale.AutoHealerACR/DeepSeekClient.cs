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
        // ⚠️ 这个值必须 **≥** 所有调用点里最长的超时，否则它会**静默砍掉**它们。
        //
        //  ── 实测踩的坑 ──
        //      `HttpClient.Timeout` 是**硬上限**，`CancellationTokenSource`
        //      的超时**盖不过它** —— 谁的秒数小谁说了算。
        //
        //      原来这里是 15 秒，而：
        //          初始化   传 40000ms（40 秒）→ 实际只有 15 秒  ← 被砍
        //          策略层   传 10000ms（10 秒）→ 正常
        //          决策层   传  8000ms（ 8 秒）→ 正常
        //
        //      ⇒ 初始化那 40 秒**形同虚设**。而 `deepseek-flash` 是推理型模型，
        //        初始化的局面报告又是最大的一次请求 —— 15 秒经常不够。
        //        日志实证：一次运行 352 条「请求失败（超时）」，几乎全是它。
        //
        //  ⚠️ 各层**自己的**超时仍然生效（决策层 8 秒、策略层 10 秒）——
        //     这里只是把天花板抬高，让它们各自的设置能真正起作用。
        Timeout = TimeSpan.FromSeconds(60),
    };

    // ══════════════════════════════════════════════════════════════
    //  ★ 通道独立熔断 ★
    //
    //  [!] 为什么改（第三方审阅指出、已核实）：
    //      原来所有调用共用一个 `_连续失败` + 一个 `_熔断到`：
    //          初始化 / 策略层 / 决策层 / 记忆提炼
    //      => **决策层高频失败会把熔断器推上去，连累正常的策略层**。
    //
    //      这个隐患代码注释里其实写出来过（原 L965-966）：
    //        「决策层超时 -> 熔断 -> 策略层也被停掉（两层共用一个熔断器）」
    //      当时的对策是"给超时单独放宽上限" —— 缓解了症状，根因还在。
    //
    //  [!] 语义：
    //      · 每个通道有自己的失败计数 / 熔断时刻 / 退避时刻
    //      · 一个通道熔断，别的通道照常工作
    //      · **强制熔断（人手动的）仍然是全局的** —— 那是用户的意图，不是故障
    // ══════════════════════════════════════════════════════════════

    /// <summary>AI 调用通道 —— 熔断 / 退避按通道独立维护</summary>
    public enum 通道
    {
        初始化,      // 一次性：加载后 / 进本时
        策略,        // 低频：倾向判断
        决策,        // 高频：技能预取（**最容易失败，也最不该拖累别人**）
        记忆,        // 一次性：对局提炼
        其他,        // 兜底
    }

    /// <summary>单个通道的熔断 / 退避状态</summary>
    private sealed class 通道状态
    {
        public int 连续失败;
        public long 熔断到;
        public long 退避到;
        public 错误类别 上次错误 = 错误类别.无;
        public int 同类连续失败;
    }

    private static readonly Dictionary<通道, 通道状态> _通道表 = new()
    {
        [通道.初始化] = new 通道状态(),
        [通道.策略] = new 通道状态(),
        [通道.决策] = new 通道状态(),
        [通道.记忆] = new 通道状态(),
        [通道.其他] = new 通道状态(),
    };

    private static readonly object _通道锁 = new();

    /// <summary>取通道状态（不存在就建一个，容错）</summary>
    private static 通道状态 状态(通道 道)
    {
        lock (_通道锁)
        {
            if (!_通道表.TryGetValue(道, out var s))
            {
                s = new 通道状态();
                _通道表[道] = s;
            }
            return s;
        }
    }

    /// <summary>**这个通道**是否在熔断中</summary>
    public static bool 熔断中(通道 道 = 通道.其他)
        => TimeHelper.Now() < 状态(道).熔断到;

    /// <summary>**这个通道**是否在退避中（`提问()` 用）</summary>
    private static bool 本通道退避中(通道 道)
        => TimeHelper.Now() < 状态(道).退避到;

    /// <summary>**这个通道**退避剩余秒数</summary>
    private static double 本通道退避剩余秒(通道 道)
        => Math.Max(0, (状态(道).退避到 - TimeHelper.Now()) / 1000.0);

    // ══════════════════════════════════════════════════════════════
    //  兼容用：原来的全局属性改成"任一通道熔断"
    //
    //  [!] 为什么保留"任一"语义：
    //      `该走原版逻辑` 在 `AiHeartbeat` 里当**完全不发请求**的闸，
    //      那里宁可保守 —— 有任何一层在熔断就先别发。
    //      真正精细的按通道判断在 `提问()` 里。
    // ══════════════════════════════════════════════════════════════

    /// <summary>任一通道熔断（保守语义，给"完全不发请求"的闸用）</summary>
    public static bool 任一通道熔断
    {
        get
        {
            var 现在 = TimeHelper.Now();
            lock (_通道锁)
                foreach (var kv in _通道表)
                    if (现在 < kv.Value.熔断到) return true;
            return false;
        }
    }

    /// <summary>所有通道的连续失败之和（界面显示用）</summary>
    public static int 连续失败数
    {
        get
        {
            var 和 = 0;
            lock (_通道锁)
                foreach (var kv in _通道表) 和 += kv.Value.连续失败;
            return 和;
        }
    }

    /// <summary>各通道状态的紧凑描述（界面 / 日志用）</summary>
    public static string 通道摘要()
    {
        try
        {
            var 段 = new List<string>();
            var 现在 = TimeHelper.Now();
            lock (_通道锁)
            {
                foreach (var kv in _通道表)
                {
                    if (kv.Value.连续失败 == 0 && 现在 >= kv.Value.熔断到) continue;
                    var 状 = 现在 < kv.Value.熔断到 ? "熔断" : $"{kv.Value.连续失败} 次失败";
                    段.Add($"{kv.Key}={状}");
                }
            }
            return 段.Count == 0 ? "全部正常" : string.Join("｜", 段);
        }
        catch
        {
            return "";
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ★ 错误分类 + 指数退避 ★
    //
    //  ══ 为什么要分类（原来的问题）══
    //    `记失败` 原来只分"超时 / 其他"两类，于是：
    //      · **401/403（Key 错）** 和网络抖动走同一条路 ——
    //        但 Key 错是**配置问题，重试一万次也不会好**，
    //        只会白烧时间和额度，还把熔断器推上去**连累正常的层**。
    //      · **429（限流）** 用固定冷却 → 每 5 秒撞一次 → **请求风暴**。
    //      · 日志里分不出"该改配置"还是"该等一等"。
    //
    //  ══ 五类 ══
    //    鉴权    401/403 —— **长时间停手**，日志直接说"去改 Key"
    //    限流    429     —— 指数退避 + 抖动
    //    服务端  5xx     —— 指数退避（基准短一点）
    //    超时            —— 推理模型的**预期行为**，走原有宽松上限
    //    其他            —— 未知，走原有上限
    //
    //  ⚠️ 退避**只决定"下一次请求要不要等"**，不影响 ACR 战斗逻辑 ——
    //     AI 停手时全部走本地原逻辑（既有设计）。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>错误类别</summary>
    public enum 错误类别 { 无, 鉴权, 限流, 服务端, 超时, 其他 }

    /// <summary>
    /// 当前错误类别（给界面 / 日志用）—— 取"最近出错的那个通道"的类别。
    ///
    /// [!] 按通道之后没有单一的"上次错误"了，这里保留一个全局视图纯为显示。
    ///     真正参与逻辑判断的是每通道的 `上次错误`。
    /// </summary>
    public static 错误类别 上次错误
    {
        get
        {
            lock (_通道锁)
            {
                foreach (var kv in _通道表)
                    if (kv.Value.上次错误 != 错误类别.无) return kv.Value.上次错误;
            }
            return 错误类别.无;
        }
    }

    /// <summary>
    /// **退避中吗**（任一通道）—— 退避期间不发请求。
    ///
    /// [!] 这里保留"任一"语义是**保守**：只要有任何通道在退避，
    ///     说明刚刚出过错，先等一下比立刻重试好。
    ///     按通道的精细判断在 `提问()` 里（它只查自己那个通道）。
    /// </summary>
    public static bool 退避中
    {
        get
        {
            var 现在 = TimeHelper.Now();
            lock (_通道锁)
                foreach (var kv in _通道表)
                    if (现在 < kv.Value.退避到) return true;
            return false;
        }
    }

    /// <summary>还要退避多少秒（0 = 没在退避）—— 取最长的那个通道</summary>
    public static double 退避剩余秒
    {
        get
        {
            var 现在 = TimeHelper.Now();
            var 最 = 0L;
            lock (_通道锁)
                foreach (var kv in _通道表)
                    if (kv.Value.退避到 - 现在 > 最) 最 = kv.Value.退避到 - 现在;
            return 最 > 0 ? 最 / 1000.0 : 0;
        }
    }

    /// <summary>
    /// 按类别算退避时长（毫秒）。
    ///
    /// ⚠️ 指数退避 + **抖动**：
    ///     `2 / 5 / 15 / 30` 秒，再加 0~500ms 随机。
    ///     抖动的作用是**避免多个请求在同一毫秒一起重试**（惊群）。
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    private static long 算退避毫秒(错误类别 类, 通道 道 = 通道.其他)
    {
        // 鉴权错误是**配置问题** —— 退很久，等用户去改 Key。
        //   不给"无限"是因为用户可能改好了想立刻恢复，10 分钟是个折中。
        if (类 == 错误类别.鉴权) return 10 * 60 * 1000L;

        long[] 阶梯 = 类 == 错误类别.限流
            ? new[] { 2000L, 5000L, 15000L, 30000L }
            : new[] { 1000L, 3000L, 8000L, 20000L };   // 服务端：基准短一点

        var i = Math.Clamp(状态(道).同类连续失败 - 1, 0, 阶梯.Length - 1);
        var 基础 = 阶梯[i];

        // 抖动 0~500ms（用时间戳低位当伪随机，避免引 Random 的线程安全问题）
        var 抖动 = (int)(TimeHelper.Now() % 500);
        return 基础 + 抖动;
    }

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
    // ==================== 推理型模型的回复处理 ====================

    /// <summary>
    /// 没显式指定 max_tokens 时用的值。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 这个值原来写死 800，是"正文为空"的**直接原因** ★
    ///
    ///    `deepseek-flash` 是推理型模型：**思考也占用 max_tokens 额度**。
    ///    800 的额度里思考吃掉 700 多，正文就只剩几十个 token ——
    ///    表现就是"思考写完了，正文还没来得及输出就被截断"，
    ///    我们收到一个 content 为空、只有 reasoning_content 的回复。
    ///
    ///    实测证据（用户截图）：初始化第 1 次"正文为空"失败、
    ///    第 2 次成功 —— 典型的**额度不够、偶发截断**。
    ///
    ///  ── 为什么可以放心给大 ──
    ///
    ///    max_tokens 是**上限**不是计费量：
    ///      · 模型写够了就停，不会因为我们给 2000 就写满 2000
    ///      · 计费按实际输出算，所以"给大"本身不花钱
    ///      · 给小的唯一效果就是**截断**
    ///
    ///    所以这里的取舍很明确：**上限给宽，避免截断**。
    ///
    ///  ⚠️ 需要短输出的调用点（决策层只要 3 行、测试连接只要两个字）
    ///     仍然应该显式传小值 —— 那能**限制模型别啰嗦**，
    ///     是"控制输出风格"，和"避免截断"是两件事。
    ///     但要注意：传太小会重新触发"思考把额度吃光"的问题。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private const int 默认最大Token = 4000;

    /// <summary>
    /// "思考吃光额度"时的**自动加大重试**倍数。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么不能只判失败 ★
    ///
    ///  日志实证（初始化**连续失败两次**，整个 AI 层失效走原版逻辑）：
    ///    「正文为空（推理型模型把额度用在思考上、没来得及输出正文）」
    ///    「初始化未拿到回复：请求失败（走原版逻辑）」
    ///
    ///  而 AI 的**思考内容其实是对的**
    ///  （"队伍只有两名治疗、没有坦克，我既是奶也是半个承伤位"）——
    ///  **它答得出来，只是被额度截断了。**
    ///
    ///  ── 直接判失败的代价 ──
    ///    初始化失败 = **整场战斗的 AI 层失效**（走原版逻辑）。
    ///    为了一个"额度给少了"的可恢复原因，把整个功能废掉，不划算。
    ///
    ///  ── 为什么重试一定有用 ──
    ///    `max_tokens` 是**上限不是计费量**：模型写够了就停。
    ///    所以"给大"本身不花钱，而"给小"的唯一效果就是**截断**。
    ///    ⇒ 遇到截断就加大重试，是**零成本**的补救。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private const int 截断重试倍数 = 2;

    /// <summary>最多自动加大重试几次（1 次就够：×2 之后还截断说明是别的问题）</summary>
    private const int 截断重试次数 = 1;

    /// <summary>
    /// 处理"正文为空、只有思考内容"的情况。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 这是对**思考泄漏**的止损，不是根治 ★
    ///
    ///    根治在 <see cref="默认最大Token"/>：别让正文被截断成空。
    ///    这里处理"已经发生了"的情况。
    ///
    ///  ── 处理方式**取决于调用方期望什么格式**（第一版写错了）──
    ///
    ///    第一版不分调用方，一律按 `ID|理由` 去思考里捞。结果：
    ///      初始化 / 策略层的期望是**一段倾向文本**，
    ///      思考里当然没有 `ID|理由` → 捞不到 → 判失败。
    ///      （用户截图里"初始化第 1 次失败"就是这个原因，
    ///        而且那是**我引入的回归** —— 修一个调用点时弄坏了另一个。）
    ///
    ///    所以现在分两种：
    ///
    ///    ① **期望 `ID|理由`**（决策层）→ 严格捞取。
    ///       思考里经常已经写出结论了（"So 25871|..."），捞出来有用。
    ///       ⚠️ 但**绝不能原样返回思考**：那是英文散文，
    ///          上层会当成"AI 一直在乱回"，产生成百上千条解析失败。
    ///
    ///    ② **期望散文**（初始化 / 策略层 / 提炼）→ **返回 null（认失败）**。
    ///       ⚠️ 为什么这次不"捞"了：
    ///          这类调用方要的是**结论**，而思考是**过程**。
    ///          把过程当结论交上去，AI 的"倾向"会变成一个半成品 ——
    ///          而且**没人能看出来那是半成品**（它就是一段通顺的中文）。
    ///          宁可认失败走降级（保持上一次的结论），也不要污染它。
    ///
    ///  ⚠️ 两种情况下**日志都会说清原因**，并指出真正的修法
    ///     （加大 max_tokens / 换非推理模型）。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static string? 处理推理型回复(string? 推理, bool 期望ID理由格式)
    {
        if (string.IsNullOrWhiteSpace(推理)) return null;

        if (!期望ID理由格式)
        {
            // 散文类调用方：**认失败**，不要拿思考冒充结论（见上面②的说明）
            Ai调试.日志(
                "正文为空（推理型模型把额度用在思考上、没来得及输出正文）。" +
                "这类调用方要的是结论文本，**不拿思考过程冒充** —— 本次按失败处理。" +
                "治本：加大 max_tokens 或换非推理模型");
            return null;
        }

        try
        {
            var 捞出来的 = new List<string>();

            foreach (var 行 in 推理.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var 段 = 行.Trim().Split('|', 2);
                if (段.Length < 2) continue;

                // 必须严格是"纯数字|内容"——避免把思考里的普通句子误捞进来
                if (!uint.TryParse(段[0].Trim(), out var id)) continue;
                if (id == 0) continue;

                捞出来的.Add($"{id}|{段[1].Trim()}");

                if (捞出来的.Count >= 每次最多捞几条) break;
            }

            if (捞出来的.Count == 0)
            {
                Ai调试.日志(
                    "正文为空，且思考过程里没有 `ID|理由` 行 —— 本次无可用建议。" +
                    "治本：加大 max_tokens 或换非推理模型");
                return null;
            }

            Ai调试.日志(
                $"正文为空，从 reasoning_content 里捞到 {捞出来的.Count} 条 `ID|理由`" +
                "（推理型模型没输出正文就返回了 —— 建议加大 max_tokens 或换非推理模型）");

            return string.Join('\n', 捞出来的);
        }
        catch
        {
            return null;
        }
    }

    private const int 每次最多捞几条 = 3;

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

    // ==================== 缓存命中统计 ====================

    /// <summary>累计命中的输入 token（便宜的那部分）</summary>
    public static long 缓存命中Token { get; private set; }

    /// <summary>累计未命中的输入 token（贵的那部分）</summary>
    public static long 缓存未命中Token { get; private set; }

    /// <summary>命中率（0~1）。两个计数都为 0 时返回 -1（还没数据）。</summary>
    public static double 缓存命中率
    {
        get
        {
            var 总 = 缓存命中Token + 缓存未命中Token;
            return 总 <= 0 ? -1 : 缓存命中Token * 1.0 / 总;
        }
    }

    /// <summary>一句话描述缓存情况（给面板/日志）</summary>
    public static string 缓存描述()
    {
        var r = 缓存命中率;
        if (r < 0) return "缓存：暂无数据";
        return $"缓存命中 {缓存命中Token:N0} / 未命中 {缓存未命中Token:N0}" +
               $"（命中率 {r * 100:F0}%）";
    }

    private static long _上次缓存日志;

    /// <summary>
    /// 从响应的 `usage` 里累加缓存命中/未命中。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么要盯这个数（官方文档《API 上线硬盘缓存》）★
    ///
    ///    「只有当两个请求的**前缀内容相同**时（从第 0 个 token 开始相同），
    ///      才算重复。**中间开始的重复不能被缓存命中**。」
    ///
    ///    DeepSeek 的硬盘缓存**自动运行、无需改代码**，
    ///    命中的部分 **0.1 元/百万 tokens**，未命中 **1 元/百万** ——
    ///    **差 10 倍**。所以这是最值得盯的一个数。
    ///
    ///    返回的 `usage` 里有：
    ///      · prompt_cache_hit_tokens  —— 命中（便宜）
    ///      · prompt_cache_miss_tokens —— 未命中（贵）
    ///
    ///  ⚠️ 我们的请求结构是 `system`（规则，**固定**）+ `user`（局面，每轮变），
    ///     所以那 7000+ 字符的规则**天然是稳定前缀**，应该命中。
    ///     如果实测命中率低，说明**局面报告/规则的开头不稳定** ——
    ///     那才是要修的地方（**不是**把规则从报告里删掉）。
    ///
    ///  每 60 秒往日志打一次，用户能直接看出缓存有没有生效。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static void 记缓存用量(JsonElement 根)
    {
        try
        {
            if (!根.TryGetProperty("usage", out var usage)) return;

            long 取(string 名)
            {
                if (usage.TryGetProperty(名, out var v) &&
                    v.ValueKind == JsonValueKind.Number &&
                    v.TryGetInt64(out var n)) return n;
                return 0;
            }

            缓存命中Token += 取("prompt_cache_hit_tokens");
            缓存未命中Token += 取("prompt_cache_miss_tokens");

            if (TimeHelper.Now() - _上次缓存日志 > 60_000)
            {
                _上次缓存日志 = TimeHelper.Now();
                LogHelper.Info("[BlueWhale.AI] " + 缓存描述());
            }
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════════
    //  ★ 拉取模型列表 ★
    //
    //  ⚠️ 为什么需要（这是一个**真实发生过**的 bug）：
    //     代码里硬编码了 `deepseek-pro` 作为预设选项，
    //     但官方**没有这个模型名** —— 用户点了它就会收到 400。
    //     而报错文案还写着更老的名字（`deepseek-chat` / `deepseek-reasoner`）。
    //
    //  ⇒ 模型名**会随版本变**，硬编码一定会过期。
    //     所以提供"从 API 拉真实列表"这个按钮，
    //     预设值只作**兜底**（拉不到时才用）。
    //
    //  官方接口：`GET /models`（见 api-docs.deepseek.com/api/list-models）
    // ══════════════════════════════════════════════════════════════

    /// <summary>上一次拉到的模型列表（空 = 还没拉过）</summary>
    public static IReadOnlyList<string> 模型列表 { get; private set; } = Array.Empty<string>();

    /// <summary>拉取结果描述（给界面显示）</summary>
    public static string 模型列表状态 { get; private set; } = "（还没拉取过，用的是内置预设）";

    /// <summary>
    /// **从 API 拉取可用模型列表**（设置界面按钮调）。
    ///
    /// ⚠️ 失败不影响任何东西 —— 拉不到就继续用内置预设。
    /// </summary>
    public static async Task<bool> 拉取模型列表()
    {
        var s = AiSettings.Instance;
        if (!s.已配置)
        {
            模型列表状态 = "拉取失败：还没填 API Key";
            return false;
        }

        try
        {
            var 地址 = s.Endpoint?.TrimEnd('/') ?? "";
            if (!地址.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            {
                // 用户可能填的是 .../v1 或裸域名，都补成 /models
                地址 = 地址.Replace("/chat/completions", "") + "/models";
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, 地址);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);

            using var cts = new CancellationTokenSource(10_000);
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var 码 = (int)resp.StatusCode;
                模型列表状态 = 码 == 401 || 码 == 403
                    ? $"拉取失败：HTTP {码}（API Key 无效或没权限）"
                    : $"拉取失败：HTTP {码}";
                return false;
            }

            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                模型列表状态 = "拉取失败：返回里没有 data 数组（接口变了？）";
                return false;
            }

            var 列表 = new List<string>();
            foreach (var m in data.EnumerateArray())
            {
                if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    var 名 = id.GetString();
                    if (!string.IsNullOrWhiteSpace(名) && !列表.Contains(名)) 列表.Add(名);
                }
            }

            if (列表.Count == 0)
            {
                模型列表状态 = "拉取成功，但列表是空的";
                return false;
            }

            模型列表 = 列表;
            模型列表状态 = $"已拉到 {列表.Count} 个模型：" + string.Join("、", 列表);
            LogHelper.Info("[BlueWhale.AI] " + 模型列表状态);
            return true;
        }
        catch (OperationCanceledException)
        {
            模型列表状态 = "拉取失败：超时";
            return false;
        }
        catch (Exception e)
        {
            模型列表状态 = "拉取失败：" + e.Message;
            return false;
        }
    }

    /// <summary>重置缓存统计（换职业 / 换本时调，便于分段观察）</summary>
    public static void 重置缓存统计()
    {
        缓存命中Token = 0;
        缓存未命中Token = 0;
    }

    // ==================== 请求 ====================

    /// <param name="超时毫秒">0 = 用设置里的值</param>
    /// <param name="最大Token">
    /// 输出上限。0 = 用 <see cref="默认最大Token"/>。
    ///
    /// ⚠️ **输出越长越要给足**。推理型模型的**思考也占 max_tokens**，
    ///    给少了会出现"思考写完了、正文一个字没来得及写"——
    ///    收到 `content` 为空、只有 `reasoning_content` 的回复。
    ///
    ///  ⚠️ 已知踩过的坑（都是"只传了超时、忘了传额度"）：
    ///      · `Ai初始化` —— 输出是一大段策略分析 → 曾用默认额度被截断
    ///      · `AiStrategyLayer` —— 注释写着"给足 10 秒"，10 秒是**超时**
    ///      · `记忆库` 提炼 —— "总结一场完整副本"，会截在句子中间
    ///
    ///  ⚠️ 需要**短输出**的调用点（决策层只要 3 行、测试连接只要两个字）
    ///     仍然应该显式传小值 —— 那是"控制输出风格"，和"避免截断"是两件事。
    ///     但传太小会重新触发"思考吃光额度"的问题。
    ///
    ///  ✅ `max_tokens` 是**上限不是计费量** —— 给大本身不花钱，
    ///     给小的唯一效果就是截断。所以宁可给宽。
    /// </param>
    /// <param name="关思考">
    /// **关闭思考模式** —— 高频调用点（决策层预取）必须传 true。
    ///
    /// [!] 官方默认是**开启思考 + effort = high**（api-docs.deepseek.com/guides/thinking_mode）。
    ///     不显式关掉的话，一个"备货"用的高频请求也在做最强推理 ——
    ///     实测中位耗时 2.6 秒，而 GCD 只有 2.5 秒，答案回来就已经过期。
    ///
    /// [!] 低频调用点（策略层 / 初始化 / 记忆提炼）**不要传** ——
    ///     那边要的是推理质量，多等几秒没关系。
    /// </param>
    public static async Task<string?> 提问(string systemPrompt, string userPrompt,
                                           int 超时毫秒 = 0, int 最大Token = 0,
                                           bool 期望_ID理由格式 = false,
                                           bool 关思考 = false,
                                           通道 道 = 通道.其他)
    {
        var s = AiSettings.Instance;

        if (!s.已配置) return null;

        // ★ 按通道判断熔断 —— 别的通道熔断不该停掉这一层 ★
        //   [!] 这是本次改造的核心：原来所有调用共用一个熔断器，
        //       决策层高频失败会把策略层一起停掉。
        if (熔断中(道))
        {
            Ai调试.调试($"通道【{道}】熔断中，本次不发请求（其他通道不受影响）");
            return null;
        }

        // ══════════════════════════════════════════════════════════════
        //  ★ 退避闸门 —— 限流 / 服务端 / 鉴权后退避期内**不发请求** ★
        //
        //  ⚠️ 和熔断的区别（语义不同，别混）：
        //     熔断 = "AI 整体不可用"，界面显示熔断、`该走原版逻辑` 变 true
        //     退避 = "这一次先别发"，AI 仍算可用，只是要等几秒
        //
        //  ⚠️ 为什么必须有这道闸：
        //     没有它，退避就只是"记了个时间戳" ——
        //     调用方照样每轮发请求，**限流时反而打得更凶**。
        // ══════════════════════════════════════════════════════════════
        if (本通道退避中(道))
        {
            Ai调试.调试(
                $"通道【{道}】退避中（还剩 {本通道退避剩余秒(道):F1} 秒，" +
                $"上次错误 {状态(道).上次错误}），本次不发请求");
            return null;
        }

        var 本次额度 = 最大Token > 0 ? 最大Token : 默认最大Token;

        // ══════════════════════════════════════════════════════════════
        //  ★ 额度不足 → 自动加大重试 ★
        //
        //  ── 为什么 ──
        //    初始化连续失败两次，整个 AI 层失效走原版逻辑：
        //      「正文为空（推理型模型把额度用在思考上、没来得及输出正文）」
        //      「初始化未拿到回复：请求失败（走原版逻辑）」
        //
        //    而 AI 的**思考内容其实是对的**
        //    （"队伍只有两名治疗、没有坦克，我既是奶也是半个承伤位"）——
        //    **它答得出来，只是被额度截断了。**
        //
        //  ── 为什么重试一定有用、且不花钱 ──
        //    `max_tokens` 是**上限不是计费量**：模型写够了就停。
        //    计费按实际输出算，所以"给大"本身不花钱，
        //    而"给小"的唯一效果就是**截断**。
        //    ⇒ 遇到截断就加大重试，是**零成本**的补救。
        //
        //  ⚠️ 只对"正文为空但**有思考内容**"这一种情况重试 ——
        //     那才是"思考吃光额度"的特征。
        //     网络错误 / HTTP 错误 / 真的答不出来，重试也没用（不重试）。
        // ══════════════════════════════════════════════════════════════
        for (var 第几次 = 0; ; 第几次++)
        {
            var (文本, 是截断) = await 发一次(systemPrompt, userPrompt,
                                               超时毫秒, 本次额度,
                                               期望_ID理由格式, 关思考, 道).ConfigureAwait(false);

            if (文本 != null) return 文本;

            if (!是截断 || 第几次 >= 截断重试次数) return null;

            本次额度 *= 截断重试倍数;
            Ai调试.日志($"正文被截断（额度 {本次额度 / 截断重试倍数} 不够思考用）→ " +
                        $"加大到 {本次额度} 重试");
        }
    }

    /// <summary>
    /// 真正发一次请求。
    /// </summary>
    /// <returns>
    /// (文本, 是否因为额度不足被截断)。
    /// 文本为 null 表示这次没拿到可用回复；
    /// <c>是截断</c> 为 true 表示"正文为空但**有思考内容**"——
    /// 那是额度不够的**特征**，调用方可以加大额度重试。
    /// </returns>
    private static async Task<(string? 文本, bool 是截断)> 发一次(
        string systemPrompt, string userPrompt,
        int 超时毫秒, int 最大Token, bool 期望_ID理由格式,
        bool 关思考 = false, 通道 道 = 通道.其他)
    {
        var s = AiSettings.Instance;

        try
        {
            // ══════════════════════════════════════════════════════════════
            //  ★ 思考模式控制（官方文档：api-docs.deepseek.com/guides/thinking_mode）★
            //
            //  [!] 官方默认是**开启思考 + effort = high** ——
            //      所以不显式关掉的话，高频预取也在用最强推理，白等好几秒。
            //      实测中位耗时 2.6 秒，而 GCD 只有 2.5 秒。
            //
            //  [!] 关思考的写法（OpenAI 兼容格式）：
            //        {"thinking": {"type": "disabled"}}
            //      开启是 `enabled`，强度另用 `reasoning_effort`。
            //
            //  [!] `temperature` 在**思考模式下无效**（官方原文：
            //      "设置这些参数不会触发错误，但也不会生效"）。
            //      所以只在**关思考**时才传温度 —— 那时它才真的起作用。
            //      开着思考还传温度只会让人**误以为**已经调了稳定性。
            // ══════════════════════════════════════════════════════════════
            object body = 关思考
                ? new
                {
                    model = s.Model,
                    messages = new[]
                    {
                        new { role = "system", content = 加上语言要求(systemPrompt) },
                        new { role = "user", content = userPrompt },
                    },
                    // 关思考时温度才生效：要稳定决策，不要创意
                    temperature = 0.2,
                    max_tokens = 最大Token,
                    stream = false,
                    thinking = new { type = "disabled" },
                }
                : new
                {
                    model = s.Model,
                    messages = new[]
                    {
                        new { role = "system", content = 加上语言要求(systemPrompt) },
                        new { role = "user", content = userPrompt },
                    },
                    // ⚠️ 思考模式下**不传 temperature** —— 传了也不生效，
                    //    留着只会误导（以为调了稳定性，其实没有）
                    max_tokens = 最大Token,
                    stream = false,
                    thinking = new { type = "enabled" },
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
                // ⚠️ 把状态码**单独传**给 `记失败` —— 不要让它从字符串里认。
                //    字符串匹配（"HTTP 401".Contains("401")）看着能用，
                //    但改一次措辞就静默失效 —— 那是本项目反复踩过的坑。
                记失败($"HTTP {(int)resp.StatusCode}", (int)resp.StatusCode, 道: 道);
                return (null, false);          // HTTP 错误，重试没用
            }

            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);

            // ══════════════════════════════════════════════════════════
            //  ★ 读缓存命中统计 ★
            //
            //  ⚠️ 为什么要读（官方文档《API 上线硬盘缓存》的原文）：
            //
            //    「只有当两个请求的**前缀内容相同**时（从第 0 个 token 开始相同），
            //      才算重复。中间开始的重复不能被缓存命中。」
            //
            //    DeepSeek 的硬盘缓存**自动运行、无需改代码**，
            //    命中的部分按 **0.1 元/百万 tokens**（未命中 1 元/百万）——
            //    命中与否差 **10 倍**。所以这是最值得盯的一个数。
            //
            //    返回的 `usage` 里有：
            //      · prompt_cache_hit_tokens  —— 命中（便宜）
            //      · prompt_cache_miss_tokens —— 未命中（贵）
            //
            //  ⚠️ 命中率的**唯一**前提是"从第 0 个 token 起前缀一致"。
            //     我们的请求结构是 `system`(规则，固定) + `user`(局面，每轮变)，
            //     所以规则的 7000+ 字符**天然是稳定前缀**，应该命中。
            //     如果实测命中率低，就说明**局面报告的开头不稳定** ——
            //     那正是要修的地方（而不是把规则从报告里删掉）。
            // ══════════════════════════════════════════════════════════
            记缓存用量(doc.RootElement);

            var 首选项 = doc.RootElement.GetProperty("choices")[0];
            var 消息 = 首选项.GetProperty("message");

            // ══════════════════════════════════════════════════════════════
            //  ★ 读 API 的 `finish_reason` —— 把"是否截断"从启发式变成有依据 ★
            //
            //  [!] 为什么必需（第三方审阅指出、已核实）：
            //      原来**完全没用**这个字段，靠"正文空 + 有推理内容"来猜截断。
            //      缺口是：`finish_reason == "length"` 但**正文非空**时
            //      （正文写了一半被额度截断）会被当成完整回复交上去。
            //
            //  [!] 为什么"残缺正文"比"空正文"更危险：
            //      空正文 -> `解析并填充` 解析出 0 条 -> 安全（走原版逻辑）
            //      残缺正文 -> 可能解析出**前半截的建议**，
            //                  而它们基于的局面可能已经不成立了
            //                  => 会真的被执行
            //
            //  [!] 取值（OpenAI 兼容接口）：
            //      `stop`   正常结束
            //      `length` 达到 max_tokens 被截断
            //      `content_filter` 被内容过滤
            //      其他 / 缺失 -> 当作未知，退回启发式
            // ══════════════════════════════════════════════════════════════
            var 收尾原因 = "";
            try
            {
                if (首选项.TryGetProperty("finish_reason", out var fr) &&
                    fr.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    收尾原因 = fr.GetString() ?? "";
                }
            }
            catch { }

            var 因额度截断 = string.Equals(收尾原因, "length", StringComparison.OrdinalIgnoreCase);

            // ⚠️ 推理型模型的正文可能在 reasoning_content，content 会是空字符串。
            //    但**不能无条件拿 reasoning_content 当答案** —— 那是"思考过程"，
            //    格式和语言都不对，直接拿去解析会全军覆没。
            //    详见下面 处理推理型回复 的说明。
            string? 文本 = null;

            if (消息.TryGetProperty("content", out var c))
            {
                文本 = c.GetString();
            }

            string? 推理 = null;
            if (消息.TryGetProperty("reasoning_content", out var rc))
            {
                推理 = rc.GetString();
            }

            // ══════════════════════════════════════════════════════════════
            //  ★ 正文非空、但 `finish_reason == "length"` ⇒ **残缺正文，必须丢弃** ★
            //
            //  [!] 这是原实现的真实缺口：只检查"正文是否为空"，
            //      于是"写了一半被截断"的正文会当成完整回复交上去。
            //      对 `ID|理由` 这种多行输出，残缺点可能是**任何地方** ——
            //      最后一行不完整、甚至中间某行被切断。
            //      => 宁可当作没回复（走原版逻辑），也不要用残缺内容做决策。
            // ══════════════════════════════════════════════════════════════
            if (因额度截断 && !string.IsNullOrWhiteSpace(文本))
            {
                Ai调试.日志($"回复被额度截断（finish_reason=length），正文 {文本.Length} 字符" +
                            " **整体丢弃** —— 残缺内容比空内容更危险（可能解析出前半截的建议）");
                记失败("回复被额度截断，正文残缺已丢弃", 道: 道);
                return (null, true);     // 标为截断 ⇒ 调用方会自动加大额度重试一次
            }

            if (string.IsNullOrWhiteSpace(文本))
            {
                // 正文为空、但有思考内容 → 交给 处理推理型回复（它按调用方格式决定捞不捞）
                文本 = 处理推理型回复(推理, 期望_ID理由格式);

                if (string.IsNullOrWhiteSpace(文本))
                {
                    // 捞不出来 → 记失败（**不要**把思考过程当答案塞给上层）
                    //
                    // ★ 区分两种"空"（这决定了调用方要不要重试）★
                    //    · **有思考内容却捞不出来** = "思考吃光额度、正文没写"
                    //      → 收尾原因(reason=length) 或思考很长 ⇒ **是截断**，值得加大额度重试
                    //    · 连思考都没有 = 模型真的没答
                    //      → 重试也没用，不标截断
                    // ★ 优先用 API 的权威字段；没有才退回启发式 ★
                    //   （`因额度截断` 上面已经返回了，走到这里说明 finish_reason 不是 length）
                    var 是截断 = !string.IsNullOrWhiteSpace(推理);

                    if (因额度截断) 是截断 = true;   // 双保险（理论上到不了这里）

                    记失败(是截断
                        ? "正文为空（推理型模型把额度用在思考上、没来得及输出正文）"
                        : "正文为空，未产出可用回复（详见日志）", 道: 道);

                    if (是截断)
                    {
                        Ai调试.日志("  这类调用方要的是结论文本，不接受思考过程冒充。" +
                                    "治本：加大 max_tokens（会自动重试一次）");
                    }

                    return (null, 是截断);
                }
            }

            // 成功 → 清空**本通道**的失败计数和退避状态
            //   [!] 只清本通道 —— 别的通道的失败不该被"顺带治好"，
            //       否则一个通道反复失败会被另一个通道的成功不断清零，
            //       永远触发不了熔断。
            var 本次通道 = 状态(道);
            if (本次通道.连续失败 > 0)
            {
                LogHelper.Info($"[BlueWhale.AI] 通道【{道}】请求成功，" +
                               $"清零该通道失败计数（原 {本次通道.连续失败} 次）");
            }
            本次通道.连续失败 = 0;
            本次通道.上次错误 = 错误类别.无;
            本次通道.同类连续失败 = 0;
            本次通道.退避到 = 0;
            上次失败原因 = "";

            if (s.记录原始回复)
            {
                Ai调试.日志("原始回复：" + 文本.Trim());
            }

            return (文本.Trim(), false);
        }
        catch (OperationCanceledException)
        {
            记失败("超时", 道: 道);
            return (null, false);          // 超时，重试没用（会又超时）
        }
        catch (Exception e)
        {
            记失败(e.GetType().Name + ": " + e.Message, 道: 道);
            return (null, false);
        }
    }

    /// <summary>
    /// **把一次失败归类**。
    ///
    /// ⚠️ 状态码优先 —— 它最准；只有在没有状态码（超时 / 异常）时才看原因文本。
    ///    本项目踩过"靠字符串匹配判类型"的坑（改一次措辞就静默失效）。
    /// </summary>
    private static 错误类别 分类错误(int 状态码, string 原因)
    {
        // ── 有状态码：按 HTTP 语义分 ──
        if (状态码 > 0)
        {
            if (状态码 == 401 || 状态码 == 403) return 错误类别.鉴权;
            if (状态码 == 429) return 错误类别.限流;
            if (状态码 >= 500) return 错误类别.服务端;
            return 错误类别.其他;         // 其他 4xx（请求格式等）—— 重试无用但也不退避
        }

        // ── 没状态码：看原因文本 ──
        if (原因.Contains("超时") || 原因.Contains("Timeout")
            || 原因.Contains("TaskCanceled") || 原因.Contains("OperationCanceled"))
            return 错误类别.超时;

        return 错误类别.其他;
    }

    private static void 记失败(string 原因, int http状态码 = 0, 通道 道 = 通道.其他)
    {
        var 本通道 = 状态(道);
        本通道.连续失败++;

        // ══════════════════════════════════════════════════════════
        //  ★ 分类 ★
        //
        //  ⚠️ 状态码优先（它最准）；没状态码时才退回看原因文本。
        // ══════════════════════════════════════════════════════════
        var 类 = 分类错误(http状态码, 原因);

        if (类 == 本通道.上次错误) 本通道.同类连续失败++;
        else { 本通道.上次错误 = 类; 本通道.同类连续失败 = 1; }

        // ══ 鉴权错误：**确定性失败，重试无用** ══
        //    401/403 = Key 错 / 无权限 —— 这是**配置问题**。
        //    不进"连续失败"熔断器（那会连累正常的层），只设退避。
        if (类 == 错误类别.鉴权)
        {
            本通道.退避到 = TimeHelper.Now() + 算退避毫秒(类, 道);
            本通道.连续失败--;    // 撤销上面那次 ++：它不该计入熔断
            LogHelper.Error(
                "[BlueWhale.AI] **鉴权失败（401/403）** —— 这是配置问题，重试没用。" +
                "请检查设置里的 API Key（sk- 开头、没有多余空格）和账号权限。" +
                "已暂停 AI 请求 10 分钟。");
            return;
        }

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
        //  ★ 限流 / 服务端：**指数退避**（不进熔断器）★
        //
        //  ⚠️ 为什么不进熔断器：
        //     熔断的语义是"AI 整体不可用"，会改变界面显示和 `该走原版逻辑`。
        //     而 429 / 5xx 是**暂时性**的 —— 退避几秒就好，
        //     没必要把整个 AI 判定成不可用。
        //
        //  ⚠️ 为什么不能固定冷却：
        //     固定 5 秒在限流时会形成**请求风暴** ——
        //     每 5 秒撞一次、每次都失败、每次都推高计数。
        //     指数退避（2/5/15/30 秒 + 抖动）才能真的错开。
        // ══════════════════════════════════════════════════════════
        if (类 == 错误类别.限流 || 类 == 错误类别.服务端)
        {
            var 退避 = 算退避毫秒(类, 道);
            本通道.退避到 = TimeHelper.Now() + 退避;

            Ai调试.日志(
                $"请求失败（{原因}，{(类 == 错误类别.限流 ? "限流" : "服务端")}），" +
                $"第 {本通道.同类连续失败} 次同类失败 → 退避 {退避 / 1000.0:F1} 秒" +
                $"（通道 {道}，其他通道不受影响）");

            return;
        }

        // ══════════════════════════════════════════════════════════
        var 是超时 = 原因.Contains("超时")
                  || 原因.Contains("Timeout")
                  || 原因.Contains("TaskCanceled")
                  || 原因.Contains("OperationCanceled");

        var 上限 = 是超时 ? Math.Max(s.连续失败上限 * 4, 12) : s.连续失败上限;

        if (本通道.连续失败 >= 上限)
        {
            本通道.熔断到 = TimeHelper.Now() + s.失败冷却秒 * 1000L;
            LogHelper.Error(
                $"[BlueWhale.AI] 通道【{道}】连续失败 {本通道.连续失败} 次（{原因}），" +
                $"熔断 {s.失败冷却秒} 秒 —— **该通道**期间走原有逻辑；" +
                "其他通道不受影响（通道独立熔断）。");
        }
        else
        {
            Ai调试.日志($"请求失败（{原因}），通道【{道}】第 {本通道.连续失败} 次，走降级。" +
                (是超时 ? $"（超时上限 {上限} 次，属预期行为）" : ""));
        }
    }

    /// <summary>
    /// **清零失败计数，但不解除熔断**（初始化成功时调）。
    ///
    /// ══════════════════════════════════════════════════════════════
    ///  ★ 为什么需要它 ★
    ///
    ///    超时**也计入** `_连续失败`（上限 `max(连续失败上限*4, 12)` = 12）。
    ///
    ///    而 `deepseek-flash` 是推理模型，初始化那次请求**很容易超时** ——
    ///    日志实证：连续 5 次超时，计数直接到 5。
    ///    之后决策层只要再失败几次，**熔断器就被推上去了**，
    ///    现象是"AI 明明初始化成功了，打一会儿却自己熔断了"。
    ///
    ///    初始化成功本身就是"链路是通的"的最强证据 ——
    ///    这时候不清零，那些超时就成了**已经还清的债还挂在账上**。
    ///
    ///  ⚠️ 和 `解除熔断()` 的区别：
    ///     `解除熔断()` 会**同时清掉熔断状态**（不只是计数）。
    ///     这里只清计数 —— 熔断状态该由熔断器自己管，不越权。
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    public static void 清零失败计数()
    {
        try
        {
            var 原 = 连续失败数;
            if (原 > 0)
                LogHelper.Info($"[BlueWhale.AI] 初始化成功，清零全部通道的失败计数（原共 {原} 次）");

            // [!] 清**全部通道** —— 初始化成功是"整条链路验证通过"的信号，
            //     这时所有通道的旧失败都该视为已还清（超时是预期行为，
            //     不清的话它们会一直挂在通道计数上）。
            清空所有通道计数();
        }
        catch { }
    }

    /// <summary>
    /// 手动解除熔断（设置界面用）。
    ///
    /// ⚠️ **也会取消强制熔断** —— 用户按这个按钮时想的是"让 AI 恢复"，
    ///     如果只清自动熔断、强制熔断还开着，现象就是"按了没反应"。
    ///     （QT 那边下一帧会把开关也同步回来，见 `HealQt.每帧更新`。）
    /// </summary>
    public static void 解除熔断()
    {
        清空所有通道计数();
        强制熔断 = false;
    }

    /// <summary>清掉所有通道的失败计数 / 熔断 / 退避（手动解除时用）</summary>
    private static void 清空所有通道计数()
    {
        lock (_通道锁)
        {
            foreach (var kv in _通道表)
            {
                kv.Value.连续失败 = 0;
                kv.Value.熔断到 = 0;
                kv.Value.退避到 = 0;
                kv.Value.上次错误 = 错误类别.无;
                kv.Value.同类连续失败 = 0;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  ★ 强制熔断（QT 开关）★
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// **强制熔断中吗** —— 由 `强制熔断` 这个 QT 开关控制。
    ///
    /// ⚠️ 和自动熔断（`熔断中`）**是两回事，要分开记**：
    ///     · 自动熔断靠 `_熔断到` 这个时间戳，到期自己解除
    ///     · 强制熔断是**开关**，只要开着就一直熔断，不会自己解除
    ///
    ///     如果强行用 `_熔断到 = long.MaxValue` 实现"永久"，
    ///     会带来两个麻烦：
    ///       ① `解除熔断()` 把 `_熔断到` 清零就解开了，
    ///          但开关还开着 → 下一帧又熔断（用户会觉得"解了没用"）
    ///       ② 时间戳是 `long`，加太大溢出会变成负数 → **反而立刻不熔断**
    ///     ⇒ 所以单独用一个 bool 记，语义清楚也不会溢出。
    /// </summary>
    public static bool 强制熔断 { get; private set; }

    /// <summary>综合判断：自动熔断 **或** 强制熔断</summary>
    /// <summary>
    /// 该走原版逻辑吗（任一通道熔断 / 强制熔断）。
    ///
    /// [!] 这里用**任一通道**的保守语义 —— 它在 `AiHeartbeat` 里当
    ///     "完全不发请求"的闸，宁可保守。
    ///     真正按通道的精细判断在 `提问()` 里（只查自己那个通道）。
    /// </summary>
    public static bool 该走原版逻辑 => 任一通道熔断 || 强制熔断;

    /// <summary>
    /// **这个通道**现在该不该停发。
    ///
    /// ══════════════════════════════════════════════════════════════
    ///  [!] 为什么需要它（第二轮审阅 P1-11）：
    ///      上一批只在 `提问()` 里做了按通道熔断，**上层这道闸还是全局的**：
    ///          `该走原版逻辑 => 任一通道熔断 || 强制熔断`
    ///      而它在 4 个地方当"完全不发请求"的闸 ——
    ///      ⇒ **决策层高频失败照样会把策略层和初始化停掉**，
    ///        也就是这一批想修的那个 bug，只是位置换到了上层。
    ///
    ///  [!] 语义：
    ///      · 该通道自己熔断   -> 停发（别的通道不受影响）
    ///      · `强制熔断`（人手动的）-> **仍然全局停发** ——
    ///        那是用户的显式意图，不是故障，不该被"按通道"削弱。
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    public static bool 该停发(通道 道) => 强制熔断 || 熔断中(道);

    /// <summary>
    /// **熔断状态描述**（给界面用的）。
    ///
    /// ⚠️ 分三种说清楚，别笼统写"熔断中"——
    ///     强制熔断是用户自己开的，和"连续失败导致的自动熔断"**原因完全不同**，
    ///     混成一句会让人以为出故障了，然后去乱按"解除熔断"。
    /// </summary>
    public static string 状态描述()
    {
        if (强制熔断) return "强制熔断（手动开关）";
        if (任一通道熔断) return $"自动熔断（{通道摘要()}）";
        return "正常";
    }

    /// <summary>设置强制熔断（QT 开关回调）</summary>
    public static void 设强制熔断(bool 开)
    {
        try
        {
            if (强制熔断 == 开) return;

            强制熔断 = 开;

            if (开)
            {
                清空所有通道计数();   // 免得解除后立刻又触发自动熔断
                LogHelper.Info("[BlueWhale.AI] 已**强制熔断** —— " +
                               "期间不走任何 AI 请求，全部用原有逻辑，不影响战斗。");
            }
            else
            {
                LogHelper.Info("[BlueWhale.AI] 已取消强制熔断 —— 恢复正常请求。");
            }
        }
        catch { }
    }
}
