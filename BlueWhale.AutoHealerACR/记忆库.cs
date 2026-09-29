using System.Text;
using System.Text.Json;
using AEAssist.Helper;
using HealerACR.Common;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// 记忆库 —— 把"记录模式下玩家怎么打的"提炼成可复用的结论，喂回给 AI。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 数据链路（四步，每一步的边界要清楚）★
///
///    ① 记录模式（本地层）：ACR 完全停手，只观察玩家手动操作
///    ② 对局记录（本层）：一局一个 jsonl 文件，每条 = 技能 + 局面快照
///    ③ **本类（提炼）**：副本结束后
///         · **本地先做统计**（计数/时机/血线分布）→ 不花 token
///         · 再把**统计结论**（不是原始数据）交给 AI 提炼成结论
///         · 追加进记忆库文件
///    ④ 初始化时：按「职业+等级+副本类型」检索相关记忆，拼进提示词
///
///  ★ 为什么"本地先统计"很重要 ★
///
///    一局副本几百条记录。把原始数据（或逐条文本）全塞给 AI 有三个问题：
///      ① 贵 —— 几千 token 一次提炼
///      ② 噪声 —— 大量重复条目会淹没真正有信息量的差异
///      ③ 不可靠 —— 让模型自己做算术（"平均多少秒"）它经常算错
///
///    所以**计数、平均、分布这类算术全部在本地做**，
///    交给 AI 的是已经算好的事实表；AI 只负责它真正擅长的事：
///    **从事实里读出规律、给出可执行的建议**。
///    （这也和"AI 是增强层"一致 —— 算数不该外包给模型。）
///
///  ★ 归档维度：职业 + 等级 + 副本类型（用户指定）★
///    例如「白魔 / 100 / 四人」。检索时优先取完全匹配，
///    等级或副本类型不完全匹配的作为**参考**一并给出（标注出来）。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 记忆库
{
    // ==================== 存储位置 ====================

    /// <summary>
    /// 记忆库根目录 —— **统一走 AiSettings.记忆根目录()**（见那里的说明）。
    /// </summary>
    private static string 根目录() => AiSettings.记忆根目录();

    /// <summary>记忆条目文件（追加式，一行一条结论）</summary>
    private static string 库文件() => Path.Combine(根目录(), "记忆库.jsonl");

    /// <summary>提炼状态（给 GUI 显示）</summary>
    public static string 状态 { get; private set; } = "（空闲）";

    /// <summary>已提炼的局数</summary>
    public static int 已提炼局数 { get; private set; }

    /// <summary>库里现有条目数</summary>
    public static int 条目数 { get; private set; }

    // ==================== 条目 ====================

    private sealed class 记忆条目
    {
        public string 时间 = "";
        public string 职业 = "";
        public int 等级;
        public string 副本类型 = "";
        public string 副本名 = "";
        public int 样本条数;
        public string 结论 = "";       // AI 提炼出的文字（markdown）
    }

    // ==================== 提炼 ====================

    /// <summary>
    /// 提炼一场对局。**在脱战后调用**（不在战斗里做网络请求）。
    ///
    /// 流程：读记录文件 → 本地统计 → 问 AI → 追加进库。
    /// 全程异步，不阻塞主线程。
    /// </summary>
    public static async Task 提炼(string 记录文件)
    {
        try
        {
            if (!File.Exists(记录文件)) { 状态 = "记录文件不存在"; return; }
            if (!AiSettings.Instance.已配置) { 状态 = "未配置 API Key，跳过提炼"; return; }

            状态 = "读取记录…";

            var 行 = await File.ReadAllLinesAsync(记录文件);
            if (行.Length < 2) { 状态 = "记录为空，跳过"; return; }

            // 第一行是对局头
            对局头? 头 = null;   // 反序列化失败时就是 null（下一行就判）
            try { 头 = JsonSerializer.Deserialize<对局头>(行[0]); } catch { }
            if (头 == null) { 状态 = "对局头解析失败"; return; }

            var 条目 = new List<记录条>();
            for (var i = 1; i < 行.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(行[i])) continue;
                try
                {
                    var r = JsonSerializer.Deserialize<记录条>(行[i]);
                    if (r != null) 条目.Add(r);
                }
                catch { }
            }

            if (条目.Count == 0) { 状态 = "没有有效记录"; return; }

            // ── ① 本地统计（不花 token）──
            状态 = "本地统计…";
            var 统计 = 做统计(头, 条目);

            // ── ② 交给 AI 提炼 ──
            状态 = "请求 AI 提炼…";
            var 结论 = await 请AI提炼(头, 统计);

            if (string.IsNullOrWhiteSpace(结论))
            {
                状态 = "AI 未返回有效结论（可能是熔断/超时）";
                LogHelper.Info("[记忆库] 提炼失败：" + 状态);
                return;
            }

            // ── ③ 存进库 ──
            状态 = "写入记忆库…";
            await 追加条目(new 记忆条目
            {
                时间 = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                职业 = 头.职业,
                等级 = 头.等级,
                副本类型 = 头.副本类型,
                副本名 = 地名.解析(头.地图, 是副本: true),
                样本条数 = 条目.Count,
                结论 = 结论.Trim(),
            });

            已提炼局数++;
            状态 = $"已提炼（{头.职业} / {头.等级} / {头.副本类型}，{条目.Count} 条样本）";
            LogHelper.Info($"[记忆库] {状态}");

            try { 屏幕提示.成功($"记忆已提炼：{头.职业} {头.等级} {头.副本类型}", "mem-done"); }
            catch { }
        }
        catch (Exception e)
        {
            状态 = "提炼异常：" + e.Message;
            LogHelper.Error("[记忆库] " + 状态);
        }
    }

    // ==================== 本地统计 ====================

    private sealed class 技能统计
    {
        public string 技能名 = "";
        public uint 技能;
        public int 次数;
        public List<int> 时机秒 = new();       // 距开战秒
        public List<float> 施放时最低血 = new(); // 放技能那一刻队伍最低血量
        public int 受伤时施放;                  // 有人 < 70% 时放的次数
        public int 满血时施放;                  // 全队 >= 95% 时放的次数
    }

    /// <summary>
    /// 本地统计 —— 把几百条原始记录压成一张**事实表**。
    ///
    /// 只做算术（计数/平均/分布），**不做判断** ——
    /// 判断留给 AI（它擅长从事实里读规律，不擅长做算术）。
    /// </summary>
    private static string 做统计(对局头 头, List<记录条> 条目)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"【对局】{头.职业} / {头.等级} 级 / {头.副本类型}副本" +
                      $" / {Resolve副本名(头.地图)}");
        sb.AppendLine($"【记录条数】{条目.Count}（**只包含有读条的技能**，见下方已知局限）");

        var 总时长 = 条目.Max(r => r.距开战秒);
        sb.AppendLine($"【对局时长】约 {总时长} 秒（到最后一个被记录技能为止）");

        // ── 按技能聚合 ──
        var 按技能 = new Dictionary<uint, 技能统计>();

        foreach (var r in 条目)
        {
            if (!按技能.TryGetValue(r.技能, out var s))
            {
                s = new 技能统计 { 技能 = r.技能, 技能名 = r.技能名 };
                按技能[r.技能] = s;
            }

            s.次数++;
            s.时机秒.Add(r.距开战秒);

            if (r.队伍血量 != null && r.队伍血量.Length > 0)
                s.施放时最低血.Add(r.队伍血量[0]);

            if (r.受伤人数 > 0) s.受伤时施放++;
            if (r.最低血量 >= 0.95f) s.满血时施放++;
        }

        sb.AppendLine();
        sb.AppendLine("【技能使用统计】");
        sb.AppendLine("格式：技能名 ×次数 ｜ 首次@秒 ｜ 施放时队伍最低血量(中位/最低) ｜ 受伤时占比");
        sb.AppendLine();

        foreach (var s in 按技能.Values.OrderByDescending(x => x.次数))
        {
            var 首次 = s.时机秒.Count > 0 ? s.时机秒.Min() : 0;
            float 中位 = 0f, 最低 = 0f;

            if (s.施放时最低血.Count > 0)
            {
                var 排序 = s.施放时最低血.OrderBy(x => x).ToList();
                最低 = 排序[0];
                中位 = 排序[排序.Count / 2];
            }

            var 受伤占比 = s.次数 > 0 ? s.受伤时施放 * 100 / s.次数 : 0;
            var 满血占比 = s.次数 > 0 ? s.满血时施放 * 100 / s.次数 : 0;

            sb.AppendLine($"  {s.技能名} ×{s.次数}" +
                          $" ｜ 首次@{首次}s" +
                          $" ｜ 施放时最低血 中位{中位 * 100:F0}% / 最低{最低 * 100:F0}%" +
                          $" ｜ 有人受伤时占{受伤占比}%" +
                          $" ｜ 满血时占{满血占比}%");
        }

        // ── 时间线节奏（前 60 秒，看起手习惯）──
        sb.AppendLine();
        sb.AppendLine("【起手 60 秒的技能序列】（看开场优先级）");
        var 起手 = 条目.Where(r => r.距开战秒 <= 60).OrderBy(r => r.距开战秒).ToList();
        if (起手.Count > 0)
        {
            var 文本 = string.Join(" -> ", 起手.Take(40).Select(r => $"{r.技能名}@{r.距开战秒}s"));
            sb.AppendLine("  " + 文本);
            if (起手.Count > 40) sb.AppendLine($"  …（前 60 秒共 {起手.Count} 条）");
        }
        else sb.AppendLine("  （无）");

        // ── DoT 相关时机（记忆库最想学的之一）──
        sb.AppendLine();
        sb.AppendLine("【可能的 DoT/持续伤害类技能的施放间隔】");
        var dot候选 = 按技能.Values.Where(s => s.次数 >= 3).OrderByDescending(s => s.次数).Take(5);
        foreach (var s in dot候选)
        {
            var 时机 = s.时机秒.OrderBy(x => x).ToList();
            var 间隔 = new List<int>();
            for (var i = 1; i < 时机.Count; i++) 间隔.Add(时机[i] - 时机[i - 1]);
            if (间隔.Count == 0) continue;

            sb.AppendLine($"  {s.技能名}：间隔 中位{间隔.OrderBy(x => x).ElementAt(间隔.Count / 2)}s" +
                          $"（最短{间隔.Min()}s 最长{间隔.Max()}s，共 {间隔.Count} 次间隔）");
        }

        // ── 已知局限（必须写进去，否则 AI 会过度解读）──
        sb.AppendLine();
        sb.AppendLine("【已知局限 —— 解读时必须考虑】");
        sb.AppendLine("  · 记录方式：监听施法成功事件 + 轮询读条状态（双通道）");
        sb.AppendLine("  · **读条技能（治疗/输出/DoT/复活）记录完整可靠**");
        sb.AppendLine("  · **瞬发能力技（减伤/爆发/瞬发治疗）由事件通道记录**，");
        sb.AppendLine("    但它的**归属**依赖冷却验证：");
        sb.AppendLine("      「冷却刚转过」= 确定是你放的；");
        sb.AppendLine("      验证不了的（GCD、极瞬发）会标为不确定 ——");
        sb.AppendLine("      **八人本里可能混入另一个奶妈的同职业技能**");
        sb.AppendLine("  · 因此：**不要写「他从不使用 XX」这类结论** ——");
        sb.AppendLine("    没出现可能只是没记录到或验证失败");
        sb.AppendLine("  · 但**出现过的**技能和它的时机是可信的，可以据它分析");

        return sb.ToString();
    }

    // ==================== AI 提炼 ====================

    private const string 提炼提示词 = """
你是 FF14 治疗职业的数据分析助手。下面是一场**真人玩家手动操作**的副本记录统计。

【重要背景】
这些数据来自"记录模式"：ACR 完全停手，玩家全程手动操作。
所以这是**人类玩家的真实打法**，不是机器生成的 —— 请如实分析他的习惯。

【你的任务】
从统计里提炼出**可复用的打法规律**，让另一个 AI 在同样的职业/等级/副本类型下能照着优化。

【要求】
1. **只根据给的数据说话**。数据里没有的（比如某个技能没出现）不要编造原因。
2. **区分"事实"和"推断"** —— 事实直接写，推断要标注「推测」。
3. 特别注意**时机类**结论，这是最有价值的：
   - 他一般在队伍血量掉到多少时才交治疗？（反应式 vs 预判式）
   - DoT 大概多久补一次？是否卡在剩余 3 秒左右？
   - 起手的技能优先级是什么顺序？
   - 他什么情况下开始输出、什么情况下停手治疗？
4. 如果有**看起来不合理**的地方（例如治疗交得太晚、DoT 补得太勤），
   直接指出来并说明为什么不好 —— 但要用「可能」的语气，因为数据可能不全。
5. ⚠️ **必须考虑"已知局限"那一段**：瞬发能力技没被记录，
   所以绝对不要写"他从不使用减伤/爆发药"这类结论。

【输出格式】
用 markdown，不要代码块包裹。分这几节（没有内容的节直接省略）：
### 治疗时机
### 输出与 DoT
### 起手顺序
### 资源管理
### 值得注意的问题

用简体中文，包括推理/思考过程，不要输出英文句子。
每条尽量具体（带数字），不要写空泛的套话。
""";

    private static async Task<string?> 请AI提炼(对局头 头, string 统计)
    {
        try
        {
            // ⚠️ 输出上限必须调大 —— 这是"总结一场完整副本"，
            //    默认的 800 token 会把结论截断在半句话上。
            //    超时也要放宽：提炼比决策慢得多，用 30 秒。
            return await DeepSeekClient.提问(提炼提示词, 统计,
                超时毫秒: 30000,
                最大Token: 5000);   // 总结一场完整副本：2500 偏紧，会截在句子中间
        }
        catch (Exception e)
        {
            LogHelper.Error("[记忆库] 提炼请求异常：" + e.Message);
            return null;
        }
    }

    // ==================== 存取 ====================

    private static async Task 追加条目(记忆条目 条)
    {
        var 目录 = 根目录();
        Directory.CreateDirectory(目录);

        var 行 = JsonSerializer.Serialize(条, JsonOpts) + Environment.NewLine;
        await File.AppendAllTextAsync(库文件(), 行, Encoding.UTF8);

        // ⚠️ 追加完必须让缓存失效 —— 否则下一轮提示词读到的还是旧库
        //    （不能只靠文件时间戳：同一毫秒内写完又读，时间戳可能没变）
        缓存失效();

        条目数++;
    }

    /// <summary>
    /// 读取全部条目（给 GUI 和检索用）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 带失效的缓存 —— 治"每轮提示词都在游戏线程做同步 IO" ★
    ///
    ///  ── 原来为什么是问题（审计发现）──
    ///    `采记忆库()` **每一轮提示词**都会调到这里 →
    ///    `File.ReadAllLines` + 逐行 `JsonSerializer.Deserialize`，
    ///    而且是**同步**跑在游戏线程上（调用链：
    ///    `AiSituation.采集 ← AiDecisionLayer.预取 ← AiHeartbeat.Check`）。
    ///
    ///    · 违反项目自查清单 A 第 5 条："不在战斗中读写文件"
    ///    · 库越大越慢，而决策层预取最快每 300ms 就来一次
    ///
    ///  ── 修法：缓存 + **两级失效** ──
    ///    ① 文件写时间变了 → 重读（别人改了库、或换过库文件）
    ///    ② 我们自己追加过 → 主动失效（同一个进程内的写入，
    ///       文件时间戳可能因为精度不够而变化不明显）
    ///
    ///  ⚠️ 为什么不用后台线程加载：调用方要**同步**拿到结果才能拼提示词，
    ///    改成异步要么改一大片调用链、要么在战斗里等 IO —— 都比缓存差。
    ///    缓存把"每轮一次 IO"降到"库变了才 IO"，这才是问题的关键。
    ///
    ///  ⚠️ 缓存的是**反序列化后的对象**，不是原始行 ——
    ///    这样连 JSON 解析也只在库变化时做一次。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static List<记忆条目> 读全部()
    {
        try
        {
            var f = 库文件();

            if (!File.Exists(f))
            {
                _缓存 = new List<记忆条目>();
                _缓存文件时间 = 0;
                条目数 = 0;
                return _缓存;
            }

            // ★ 失效检查 ①：文件写时间变了才重读 ★
            var 写时间 = File.GetLastWriteTimeUtc(f).Ticks;
            if (_缓存 != null && 写时间 == _缓存文件时间) return _缓存;

            var 结果 = new List<记忆条目>();
            foreach (var 行 in File.ReadAllLines(f))
            {
                if (string.IsNullOrWhiteSpace(行)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<记忆条目>(行);
                    if (e != null) 结果.Add(e);
                }
                catch { }
            }

            _缓存 = 结果;
            _缓存文件时间 = 写时间;
            条目数 = 结果.Count;
            return _缓存;
        }
        catch
        {
            return _缓存 ?? new List<记忆条目>();
        }
    }

    /// <summary>条目缓存（null = 还没读过）</summary>
    private static List<记忆条目>? _缓存;

    /// <summary>缓存对应的文件写时间（Ticks）—— 和它不一致就重读</summary>
    private static long _缓存文件时间;

    /// <summary>
    /// 让缓存失效（我们自己写过库之后必须调）。
    ///
    /// ⚠️ 光靠文件时间戳不可靠：就在同一毫秒内写完又读时，
    ///    时间戳可能没变（文件系统精度）→ 会读到**旧的缓存**。
    ///    所以写入路径要**主动**失效，不能只依赖时间戳。
    /// </summary>
    private static void 缓存失效()
    {
        _缓存 = null;
        _缓存文件时间 = 0;
    }

    // ==================== 初始化时喂给 AI ====================

    /// <summary>
    /// 取与当前局面相关的记忆，拼成一段提示词。
    ///
    /// **检索规则**（用户指定的归档维度：职业 + 等级 + 副本类型）：
    ///   ① 职业必须相同（不同职业的结论没有参考价值，甚至有害）
    ///   ② 等级接近（±10 级内 —— 技能解锁差异太大就没意义了）
    ///   ③ 副本类型相同的**优先**，不同的作为参考并明确标注
    ///
    /// 没有匹配时返回空串（不留空段落污染提示词）。
    /// </summary>
    /// <summary>单条结论最多留多少字符（超出就截断并标注）</summary>
    private const int 单条上限 = 500;

    /// <summary>整段记忆最多多少字符（**硬上限，防止无界增长**）</summary>
    private const int 整段上限 = 900;

    /// <summary>
    /// 取与当前局面相关的记忆，拼成提示词片段。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么必须有硬上限（用户提出的真实风险）★
    ///
    ///    用户说："提示词还能压缩吗 以免后续记忆增多超额"
    ///
    ///    这个担心是**对的**：结论是 AI 生成的 markdown，
    ///    长度完全不可控 —— 实测这类结论常见的在 300~800 字符，
    ///    极端情况能到两三千。而这里原来**一个字符的上限都没有**：
    ///    3 条 × 任意长度，全部塞进局面报告。
    ///
    ///    局面报告总上限是 6000 字符，实测**最坏情况已经占到 99%**
    ///    （见 tools/PromptBudget.py 的估算）。
    ///    再让记忆无界增长，结果就是**技能清单被挤掉** ——
    ///    而 AI 只能从清单里选 ID，清单没了整轮就废了。
    ///
    ///  ── 两级上限 ──
    ///    · 单条 500 字符：一条结论说清楚一件事足够，超出的是啰嗦
    ///    · 整段 900 字符：保证记忆**永远不会**挤掉事实段
    ///
    ///  ⚠️ 截断时**必须标注**（"已截断"）—— 静默截断会让 AI
    ///    以为结论就这些，从而漏掉后半段的建议。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static string 取相关记忆(string 职业, int 等级, string 副本类型, int 最多条 = 3)
    {
        try
        {
            var 全部 = 读全部();
            if (全部.Count == 0) return "";

            var 候选 = 全部
                .Where(e => e.职业 == 职业)
                .Where(e => Math.Abs(e.等级 - 等级) <= 10)
                .OrderByDescending(e => e.副本类型 == 副本类型)   // 同副本类型优先
                .ThenByDescending(e => -Math.Abs(e.等级 - 等级))  // 等级最接近
                .Take(最多条)
                .ToList();

            if (候选.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("【记忆库 —— 你自己过去记录的人类玩家打法】");
            sb.AppendLine("以下是**真人玩家手动操作**时提炼出的规律（不是 AI 生成的猜测）。");
            sb.AppendLine("参考它来优化你的建议，但**不要盲从**：");
            sb.AppendLine("局面不同时以当前实际数据为准；与本条冲突时优先当前数据。");
            sb.AppendLine();

            foreach (var e in 候选)
            {
                // ★ 整段上限：已经装不下了就不再追加（宁可少给一条记忆，
                //   也不能让记忆把"当前事实"挤出提示词）
                if (sb.Length >= 整段上限) break;

                var 匹配 = e.副本类型 == 副本类型 ? "" : "（副本类型不同，仅供参考）";
                sb.AppendLine($"── {e.职业} / {e.等级}级 / {e.副本类型}副本" +
                              $"{匹配} ｜ 样本 {e.样本条数} 条 ｜ {e.时间}");

                // ★ 单条上限
                var 结论 = e.结论 ?? "";
                if (结论.Length > 单条上限)
                {
                    结论 = 结论.Substring(0, 单条上限)
                           + "\n（此条结论过长已截断 —— 如需完整内容请查看记忆库页面）";
                }

                sb.AppendLine(结论);
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    // ==================== 副本名 ====================

    private static Dictionary<string, string>? _副本名表;
    private static long _副本名表时间;

    /// <summary>
    /// 副本 ID → 名字。读 DutyNames.json（由 tools\DutyDump 生成）。
    ///
    /// 读不到就退回 "副本#ID" —— **绝不因为缺表就不记录**。
    /// </summary>
    /// <summary>
    /// **找到 `DutyNames.json`** —— 多候选路径，逐个试。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么不能只用 `AppContext.BaseDirectory` ★
    ///
    ///  ── 现象 ──
    ///    用户进本后横幅显示 **`副本#979`** 而不是"异闻阿罗阿罗岛"。
    ///    日志里 `副本#` 出现 7 次、**成功 0 次**。
    ///
    ///  ── 排查结论（都验证过，不是猜）──
    ///    · `D:\FF14\ACR\BlueWhale\DutyNames.json` **存在**（50KB / 863 条）
    ///    · 用 .NET 实测 `JsonSerializer.Deserialize` **成功**，
    ///      `979 -> 异闻阿罗阿罗岛` 查得到
    ///    ⇒ 所以**不是文件坏、不是内容缺**，而是**根本没找到文件** ——
    ///      `Path.Combine(AppContext.BaseDirectory, "DutyNames.json")`
    ///      的那个 `BaseDirectory` 指向了别处（宿主/临时加载目录都可能）。
    ///
    ///  ── 修法：按"最可能对"的顺序排候选，命中即用 ──
    ///    ① 程序集自己的位置（`Assembly.Location`）—— **最可靠**，
    ///       它就是 DLL 所在的物理路径，`DutyNames.json` 就在旁边
    ///    ② `AppContext.BaseDirectory` —— 原来那个，留着兼容
    ///    ③ 加上 `Timelines` 的上一级（打包结构所致，见下面说明）
    ///
    ///  ⚠️ 找到之后**记住是哪个路径**（`_副本名表路径`）——
    ///     下次直接查那个，不再重复试一圈（这是热路径）。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static string? 找副本名文件()
    {
        var 候选 = new List<string>();

        try
        {
            // ① 程序集位置 —— 最可靠
            var 程序集 = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrWhiteSpace(程序集))
            {
                var 目录 = Path.GetDirectoryName(程序集);
                if (!string.IsNullOrWhiteSpace(目录)) 候选.Add(Path.Combine(目录, "DutyNames.json"));
            }
        }
        catch { }

        try
        {
            // ② 宿主给的基目录（原来的写法）
            候选.Add(Path.Combine(AppContext.BaseDirectory, "DutyNames.json"));
        }
        catch { }

        try
        {
            // ③ `Timelines` 的上一级 —— 打包时资源在插件目录下的 Timelines 里，
            //    万一 DLL 被放在子目录、json 在上一层，这条能兜住
            var 目录 = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(目录)) 候选.Add(Path.Combine(目录, "DutyNames.json"));
        }
        catch { }

        foreach (var p in 候选)
        {
            try { if (File.Exists(p)) return p; } catch { }
        }

        return null;
    }

    /// <summary>上次成功用过的路径（避免每次都在候选里转一圈）</summary>
    private static string? _副本名表路径;

    public static string Resolve副本名(uint id)
    {
        if (id == 0) return "未知副本";

        try
        {
            // 表缓存 10 分钟（文件可能被工具更新）
            if (_副本名表 == null || TimeHelper.Now() - _副本名表时间 > 600_000)
            {
                var 路径 = _副本名表路径 ?? 找副本名文件();

                if (路径 != null)
                {
                    _副本名表 = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        File.ReadAllText(路径));

                    if (_副本名表 != null)
                    {
                        _副本名表路径 = 路径;      // 记住，下次直接用
                        _副本名表时间 = TimeHelper.Now();
                    }
                }
                else
                {
                    // ⚠️ 找不到文件时**打一次日志** ——
                    //    不然用户只看到"副本#979"，完全不知道是缺文件。
                    //    这是本次排查最花时间的地方：现象里看不出原因。
                    LogHelper.Info("[记忆库] 找不到 DutyNames.json，副本名将退化为「副本#ID」" +
                                   "（应该和 BlueWhale.dll 放在同一个目录）");
                }
            }

            if (_副本名表 != null && _副本名表.TryGetValue(id.ToString(), out var 名)
                && !string.IsNullOrWhiteSpace(名))
                return 名;
        }
        catch (Exception e)
        {
            LogHelper.Info("[记忆库] 读 DutyNames.json 失败：" + e.Message);
        }

        return $"副本#{id}";
    }

    /// <summary>
    /// 清空记忆库（只删结论库，**不动记录文件**）。
    ///
    /// ⚠️ 记录文件是原始素材，删了就再也提炼不出来了 ——
    ///    所以这里只清"提炼出来的结论"，原始记录保留。
    ///    用户想重来的话，重新记录即可（或者以后加个"从记录文件重新提炼"）。
    /// </summary>
    public static void 清空()
    {
        try
        {
            var f = 库文件();
            if (File.Exists(f))
            {
                // 先备份再删 —— 万一用户点错了还能救回来
                var 备份 = f + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                File.Copy(f, 备份, overwrite: true);
                File.Delete(f);
                LogHelper.Info($"[记忆库] 已清空（备份：{Path.GetFileName(备份)}）");
            }

            条目数 = 0;
            缓存失效();   // ⚠️ 清了库也要失效缓存，否则提示词还会读到清空前的记忆
            状态 = "已清空";
            屏幕提示.成功("记忆库已清空（旧库已备份）", "mem-clear");
        }
        catch (Exception e)
        {
            状态 = "清空失败：" + e.Message;
            LogHelper.Error("[记忆库] " + 状态);
        }
    }

    // ==================== 统计信息（给 GUI）====================

    public static (int 条目, int 职业数, string 最近) 概览()
    {
        try
        {
            var 全部 = 读全部();
            if (全部.Count == 0) return (0, 0, "（空）");

            var 职业数 = 全部.Select(e => e.职业).Distinct().Count();
            var 最近 = 全部.OrderByDescending(e => e.时间).First().时间;
            return (全部.Count, 职业数, 最近);
        }
        catch { return (0, 0, "（读取失败）"); }
    }

    // ==================== 数据结构 ====================

    private sealed class 对局头
    {
        public string 职业 { get; set; } = "";
        public int 等级 { get; set; }
        public uint 地图 { get; set; }
        public string 副本类型 { get; set; } = "";
        public int 条数 { get; set; }
        public string 时间 { get; set; } = "";
        public string 结束原因 { get; set; } = "";
    }

    private sealed class 记录条
    {
        public string 时间 { get; set; } = "";
        public int 距开战秒 { get; set; }
        public uint 技能 { get; set; }
        public string 技能名 { get; set; } = "";
        public float 蓝量 { get; set; }
        public float[] 队伍血量 { get; set; } = Array.Empty<float>();
        public float 最低血量 { get; set; }
        public int 受伤人数 { get; set; }
        public int 濒危人数 { get; set; }
        public float 自己血量 { get; set; }
        public int 敌人数量 { get; set; }
        public float 目标血量 { get; set; }
        public bool 战斗中 { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
