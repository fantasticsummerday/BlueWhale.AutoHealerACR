using System.Globalization;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// **AI 的策略参数微调** —— 让 AI 能"随时调整各类阈值"（用户的目标）。
///
/// ══════════════════════════════════════════════════════════════════════
///  ★ 和 `AiThresholdAdapter` 的分工 ★
///
///    · `AiThresholdAdapter`：**倾向**（保守/均衡/激进）-> 一个统一偏移。
///      粗粒度，已验证过（指数平滑 + AI 不在就归零）。
///    · 本类：**逐参数微调** —— AI 可以只把「紧急单奶阈值」调低、
///      而把「群体治疗阈值」调高。粗粒度做不到这件事。
///
///    两者**相加**，各自独立夹范围。
///
///  ★ 为什么必须有三层安全网 ★
///
///    AI 会给极端值（它没有"这个数合不合理"的概念），
///    而这些参数直接决定"什么时候交救命技能"——
///    给错一次可能让队伍在关键时刻没有治疗。所以：
///
///      ① 解析失败 -> **整条丢弃**（宁可不动，不要动错）
///      ② 每个参数**独立夹范围**（给 0.99 也只到该参数的上限）
///      ③ **变化速率限制** —— 单次调整不超过 `单次上限`，
///         累计不超过 `总上限`；且用平滑逼近，不会一帧跳到位
///
///  ★ 为什么"AI 不在就归零"和 P4 一样要判 ★
///
///    参数是**永久 static**，装上后不会自己消失。
///    如果 AI 被关掉/熔断之后参数还留着，
///    那就又变成"关掉 AI ≠ 回到本地基线"了（P4 那个 bug 的同一形状）。
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public static class Ai策略参数
{
    // ==================== 参数清单 ====================

    /// <summary>
    /// **单个技能的调整上限**（比大类更紧）—— 见 `接受` 里 `技能:` 那一段的说明。
    ///
    /// 口径和大类阈值一致（单次 0.06 / 总量 0.12）：
    /// 单个技能调太狠会破坏"技能之间的层次关系"
    /// （比如把低语调到比不屈还晚，就失去了"低语先交"的设计）。
    /// </summary>
    private const float 单技能单次上限 = 0.06f;
    private const float 单技能总上限 = 0.12f;

    /// <summary>一个可调参数的元信息（名字 / 范围 / 幅度上限）。</summary>
    public sealed record 定义(string 名, float 最小, float 最大, float 单次上限, float 总上限);

    /// <summary>
    /// **AI 可以调的参数表** —— 键和 `HealerACR.Common.可调参数` **必须逐字一致**
    /// （那边是钩子用的参数名；写错一个字的结果是"钩子静默不生效"）。
    /// </summary>
    private static readonly 定义[] _定义表 =
    {
        //                                       最小   最大   单次   总
        new("单体治疗阈值", 0.30f, 0.95f, 0.06f, 0.12f),
        new("群体治疗阈值", 0.30f, 0.95f, 0.06f, 0.12f),
        new("紧急单奶阈值", 0.10f, 0.80f, 0.08f, 0.15f),
        new("预铺血线",     0.30f, 0.98f, 0.06f, 0.12f),
        new("妖精契约血线", 0.30f, 0.98f, 0.06f, 0.12f),
        // 大招血线：幅度上限比别的宽 —— "留大招到真正的团血危机"是合理策略
        new("大招血线",     0.30f, 0.98f, 0.10f, 0.20f),
        // 保留数是整数型的量，这里用"颗"为单位，钩子那边自己取整
        new("以太保留数",  -3f,   3f,    1f,    2f),
        new("蛇胆保留数",  -3f,   3f,    1f,    2f),
    };

    /// <summary>参数名 -> 定义（查表用）。</summary>
    private static readonly Dictionary<string, 定义> _定义 =
        _定义表.ToDictionary(d => d.名, d => d);

    /// <summary>给 AI 看的参数清单（提示词里用，**从 _定义表 生成**，不手写）。</summary>
    public static string 参数说明()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in _定义表)
                sb.Append("  ").Append(d.名)
                  .Append("（").Append(d.最小.ToString("F2", CultureInfo.InvariantCulture))
                  .Append('~').Append(d.最大.ToString("F2", CultureInfo.InvariantCulture))
                  .Append("，单次最多 ±").Append(d.单次上限.ToString("F2", CultureInfo.InvariantCulture))
                  .Append("）\n");

            // ══════════════════════════════════════════════════════════════
            //  ★ **还能按技能名单独调**（`技能:低语`）★
            //
            //  [!] 为什么要有这一层：
            //      上面那些是"**类别**"（单体/群体/大招/预铺），
            //      而本地每个技能有**自己的血线**。只给类别的话，
            //      "把低语调早一点"这种判断 AI **说不出来** ——
            //      它只能"把整个群疗类调早"，会连带影响不屈/祥光/慰藉。
            //
            //  [!] 为什么把技能名**列出来**：
            //      不列的话 AI 不知道哪些名字是有效的，只能瞎猜 ——
            //      而猜错的名字会被 `接受()` **静默丢弃**
            //      （看起来"我调了"，实际没调，和本项目栽过的"静默不生效"同型）。
            //
            //  [!] 开销：44 个名字约 **162 字符**，加说明约 252。
            //      提示词走**独立的 system 通道**（没有 6000 那个限制，
            //      那个只管局面报告），所以加它不会触发截断。
            // ══════════════════════════════════════════════════════════════
            try
            {
                var 名s = HealerACR.Common.治疗阈值表.全部名字();
                if (名s.Count > 0)
                {
                    sb.Append("  **技能:<技能名>**（同上限，逐个技能微调）：")
                      .Append(string.Join("、", 名s))
                      .Append('\n');
                }
            }
            catch { }

            return sb.ToString();
        }
        catch { return ""; }
    }

    // ==================== 状态 ====================

    private static readonly Dictionary<string, float> _目标 = new();      // AI 要的偏移
    private static readonly Dictionary<string, float> _当前 = new();      // 平滑后的偏移
    private static readonly object _锁 = new();

    private static long _上次平滑;
    private const float 平滑速率 = 0.7f;      // 和 AiThresholdAdapter 同一个系数

    /// <summary>最近一次被接受的调整（给调试窗看）。</summary>
    public static string 最近调整 { get; private set; } = "（还没有过）";

    // ==================== 写入（AI 输出 -> 目标值）====================

    /// <summary>
    /// **接受一条 AI 给的调整**。返回是否接受。
    ///
    /// [!] 单次上限在这里生效：`增量` 超过 `单次上限` 会被夹住，
    ///     而不是整条丢弃 —— 因为 AI 的**方向**通常是对的，
    ///     只是幅度夸张；夹住比丢掉更有用。
    /// </summary>
    public static bool 接受(string 参数, float 增量)
    {
        try
        {
            if (string.IsNullOrEmpty(参数)) return false;

            // ══════════════════════════════════════════════════════════════
            //  ★ **按技能名调**（`技能:低语`）—— 走这条单独的路 ★
            //
            //  [!] 为什么需要它：
            //      AI 原来只能调 4 个**类别**（单体/群体/大招/预铺），
            //      而本地实际用的是**每技能一个阈值**（44 条）。
            //      ==> "把低语调早一点"这种话，AI **说不出来**
            //          （它只能"把整个群疗类调早"，会连带影响不屈/祥光/慰藉）。
            //
            //  [!] 为什么用前缀 `技能:` 而不是直接用技能名：
            //      ① 技能名和大类名可能重名（"再生"这类），有前缀就不会误判
            //      ② 提示词里一眼能看出"这是逐个技能的调整"
            //
            //  [!] 上限（比大类更紧）：
            //      单次 ±0.06 / 总量 ±0.12 —— 和大类阈值一样的口径。
            //      理由：单个技能调太狠会破坏"技能之间的关系"
            //      （比如把低语调到比不屈还晚，就失去了"低语先交"的层次）。
            // ══════════════════════════════════════════════════════════════
            if (参数.StartsWith("技能:", System.StringComparison.Ordinal))
            {
                var 技能名 = 参数.Substring(3).Trim();
                if (技能名.Length == 0) return false;
                if (float.IsNaN(增量) || float.IsInfinity(增量)) return false;
                if (MathF.Abs(增量) < 0.001f) return false;

                var id = HealerACR.Common.治疗阈值表.按名字找(技能名);
                if (id == 0) return false;   // 没这个名字 / 重名 -> 丢弃

                var 夹后技能 = Math.Clamp(增量, -单技能单次上限, 单技能单次上限);
                var 旧 = HealerACR.Common.治疗阈值表.取单技能偏移(id);
                var 新 = Math.Clamp(旧 + 夹后技能, -单技能总上限, 单技能总上限);
                HealerACR.Common.治疗阈值表.设单技能偏移(id, 新);

                最近调整 = $"技能:{技能名} {夹后技能:+0.00;-0.00}（累计 {新:+0.00;-0.00}）";
                Ai调试.日志($"AI 技能阈值：{技能名} -> {夹后技能:+0.00;-0.00}（累计 {新:+0.00;-0.00}）");
                return true;
            }

            if (!_定义.TryGetValue(参数, out var d)) return false;      // 不认识 -> 丢弃
            if (float.IsNaN(增量) || float.IsInfinity(增量)) return false;
            if (MathF.Abs(增量) < 0.001f) return false;                 // 约等于没调，忽略

            var 夹后 = Math.Clamp(增量, -d.单次上限, d.单次上限);

            lock (_锁)
            {
                // 累计上限：新的目标值不能离 0 太远
                var 新目标 = Math.Clamp(夹后, -d.总上限, d.总上限);
                _目标[参数] = 新目标;
            }

            最近调整 = $"{参数} {夹后:+0.00;-0.00}";
            Ai调试.日志($"AI 策略参数：{参数} -> {夹后:+0.00;-0.00}（单次上限 {d.单次上限:F2}，总上限 {d.总上限:F2}）");
            return true;
        }
        catch { return false; }
    }

    /// <summary>清空全部调整（AI 关掉 / 换本时调）。</summary>
    public static void 重置()
    {
        try
        {
            lock (_锁)
            {
                _目标.Clear();
                _当前.Clear();
            }
            _上次平滑 = 0;
            最近调整 = "（已清空）";
        }
        catch { }
    }

    // ==================== 读取（钩子用）====================

    /// <summary>
    /// 某个参数的**当前偏移**（平滑后）。
    /// `AiThresholdAdapter` 把它和倾向偏移**相加**。
    /// </summary>
    public static float 取(string 参数)
    {
        try
        {
            if (!AI还在()) return 0f;        // ★ 和 P4 同一条：AI 不在就归零

            lock (_锁)
            {
                if (!_当前.TryGetValue(参数, out var 值)) return 0f;
                return 值;
            }
        }
        catch { return 0f; }
    }

    /// <summary>
    /// **每帧推进平滑**（挂在心跳里）。
    ///
    /// [!] 为什么要平滑而不是直接用目标值：
    ///     AI 的判断会小幅波动，直接跳会让阈值来回横跳。
    ///     这里和 `AiThresholdAdapter` 用**同一个**指数逼近系数，
    ///     所以两层的节奏一致。
    /// </summary>
    public static void 每帧更新()
    {
        try
        {
            if (!AI还在())
            {
                lock (_锁)
                {
                    if (_当前.Count > 0) { _当前.Clear(); _目标.Clear(); }
                }
                _上次平滑 = 0;
                return;
            }

            var 现在 = TimeHelper.Now();
            var 间隔秒 = _上次平滑 == 0 ? 1f : Math.Max(0f, (现在 - _上次平滑) / 1000f);
            _上次平滑 = 现在;
            if (间隔秒 <= 0f) return;

            var 系数 = 1f - MathF.Pow(平滑速率, 间隔秒);

            lock (_锁)
            {
                foreach (var kv in _目标)
                {
                    _当前.TryGetValue(kv.Key, out var 现值);
                    var 新值 = 现值 + (kv.Value - 现值) * 系数;
                    // 靠得足够近就吸附，避免无限逼近
                    if (MathF.Abs(kv.Value - 新值) < 0.002f) 新值 = kv.Value;
                    _当前[kv.Key] = 新值;
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// **AI 还在吗** —— 和 `AiThresholdAdapter` 的判据必须一致
    /// （开关 / 初始化 / 熔断）。
    ///
    /// [!] 重复一遍这段判断而不是共用，是因为两个类的**归零时机不同**：
    ///     倾向那个还要考虑"倾向是刻意保留的"，
    ///     而参数这边**没有任何理由保留**（AI 不在，参数就该回本地基线）。
    /// </summary>
    private static bool AI还在()
    {
        try
        {
            var 设 = AiSettings.Instance;
            if (设 == null || !设.启用决策层 || !设.启用策略层) return false;
            if (!Ai初始化.已完成) return false;
            if (DeepSeekClient.该停发(DeepSeekClient.通道.策略)) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>给调试窗/日志看的状态。</summary>
    public static string 状态描述()
    {
        try
        {
            lock (_锁)
            {
                if (_当前.Count == 0) return "（无调整）";
                var sb = new System.Text.StringBuilder();
                foreach (var kv in _当前)
                {
                    if (MathF.Abs(kv.Value) < 0.005f) continue;
                    sb.Append(kv.Key).Append(' ').Append(kv.Value.ToString("+0.00;-0.00")).Append("  ");
                }
                return sb.Length == 0 ? "（无调整）" : sb.ToString().TrimEnd();
            }
        }
        catch { return "（读不到）"; }
    }
}
