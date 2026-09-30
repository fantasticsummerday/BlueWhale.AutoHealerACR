using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// **候选动作集** —— 本地把"当前合法且值得考虑的动作"算成一组**不可变快照**。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么需要它（第一轮审阅第 9/10/24 条）★
///
///    现状：`AI -> SkillId`。白名单只能证明"这个技能本职业有"，
///    不能证明"这个局面下该用它"。
///    ⇒ 本类把游戏世界压缩成一组**真实、合法、可执行**的候选，AI 只做取舍。
///
///  ══════════════════════════════════════════════════════════════════
///  ★★ 第二轮审阅指出的两个 P0（本类已按它重构）★★
///
///  【P0-1】`C1/C2` 原来只是**排序后的位置** —— 不是身份
///      `_本帧.Sort(...)` 之后按位置重编号，缓存 300ms 一过就重算。
///      于是 AI 请求发出时选的是"C1 = 治坦克"，回来时 C1 已经变成"DoT Boss"
///      ⇒ **AI 的选择被执行成了另一个动作**。
///
///      ⇒ 修法：`候选Id` 由 **快照号 + 序号** 生成（形如 `S18231-3`），
///        **永不重编号**；解析时先认 `候选Id`，再兼容显示编号。
///        显示用的 `C1/C2` 仍然给 AI 看（好写好读）。
///
///  【P0-2】`_本帧` 是 `static List`，游戏线程 Clear/Add/Sort，
///      后台线程（AI 回复的续体）按编号查 ⇒ **完全没有同步**（lock 0 处）
///      —— 和本项目已经栽过两次的 bug 一模一样
///      （`AiDecisionLayer._队列`、`AiStrategyLayer.历史`）。
///
///      ⇒ 修法：对外只发**不可变快照**；后台线程只读它自己那份快照，
///         永远不碰正在被改的列表。
///
///  ★ 快照的生命周期 ★
///      每个快照按 `快照Id` 存进有界表（保留最近若干），
///      AI 回复回来时按**请求时记下的快照Id**去取 ——
///      取不到（太久远）就整个丢弃，**不做"用当前候选凑合"的猜测**。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 候选集
{
    /// <summary>一个候选动作（构建后不再改动）</summary>
    public sealed class 候选
    {
        /// <summary>
        /// **稳定身份** —— `S{快照Id}-{序号}`，形如 `S18231-3`。
        /// [!] 这是**唯一**用于解析的身份；位置编号（C1/C2）只是给人看。
        /// </summary>
        public string 候选Id = "";

        /// <summary>显示用短编号（C1/C2…）—— **不参与解析身份**</summary>
        public string 编号 = "";

        public uint 技能Id;
        public string 技能名 = "";

        /// <summary>目标（0 = 自己 / 无目标）</summary>
        public ulong 目标Id;
        public string 目标名 = "";

        public bool 群体;
        public bool 能力技;
        public bool 瞬发;

        /// <summary>恢复力 / 威力（AOE 时是**总量** = 单体 × 命中数）</summary>
        public int 量;

        /// <summary>命中数（>1 表示 AOE 总量）</summary>
        public int 命中数 = 1;

        /// <summary>单体威力（AOE 时给出，避免把总量误当单体值）</summary>
        public int 单体威力;

        /// <summary>目标当前缺口（% 最大血，0~1）</summary>
        public float 缺口;

        /// <summary>预计过量（0~1）</summary>
        public float 过量;

        /// <summary>**即时**治疗量（不含 HoT）—— 救命看它，不是看总量</summary>
        public int 即时治疗;

        public int 耗蓝;
        public float 冷却;
        public int 资源;

        /// <summary>本地算分（只用于排序，**不给 AI 看**）</summary>
        public float 本地分;
    }

    /// <summary>
    /// **候选快照** —— 一次生成的结果，构造后不可变。
    /// [!] AI 请求与响应之间传递的就是它。
    /// </summary>
    public sealed class 快照
    {
        public long 快照Id;
        public long 生成时刻;
        public IReadOnlyList<候选> 候选表 = Array.Empty<候选>();

        /// <summary>按**稳定身份**取候选</summary>
        public 候选? 按Id(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            var 要 = id.Trim();
            foreach (var c in 候选表)
                if (string.Equals(c.候选Id, 要, StringComparison.OrdinalIgnoreCase))
                    return c;
            return null;
        }

        /// <summary>按**显示编号**取候选（C1/C2）—— 兼容 AI 直接回编号</summary>
        public 候选? 按编号(string? 编号)
        {
            if (string.IsNullOrWhiteSpace(编号)) return null;
            var 要 = 编号.Trim();
            foreach (var c in 候选表)
                if (string.Equals(c.编号, 要, StringComparison.OrdinalIgnoreCase))
                    return c;
            return null;
        }

        /// <summary>先按稳定 Id、再按显示编号（两种都接受）</summary>
        public 候选? 解析(string? 引用) => 按Id(引用) ?? 按编号(引用);
    }

    private const int 最多 = 8;
    private const int 缓存毫秒 = 300;
    private const int 快照保留 = 12;

    // ══════════════════════════════════════════════════════════════
    //  ★ 线程安全：只在这把锁里改 `_当前表` / `_快照表` ★
    //
    //  [!] 为什么必须（第二轮审阅 P0-2）：
    //      写点 = 游戏线程（`生成()` 里 Clear/Add/Sort）；
    //      读点 = **后台线程**（AI 回复的续体里解析候选）。
    //      原来这里是裸 `static List`，lock 0 处 ——
    //      和本项目栽过的 `AiDecisionLayer._队列` / `AiStrategyLayer.历史` 同一个 bug。
    //
    //  [!] 但对外**只发快照**：调用方拿到的是不可变对象，
    //      遍历它不需要持锁 —— 从根上消除"遍历期被改"。
    // ══════════════════════════════════════════════════════════════
    private static readonly object _锁 = new();

    private static readonly List<候选> _当前表 = new();
    private static long _本帧时刻;

    private static readonly Dictionary<long, 快照> _快照表 = new();
    private static readonly Queue<long> _快照序 = new();

    private static long _下一个快照Id;
    private static 快照 _当前 = new();

    // ==================== 对外 ====================

    /// <summary>生成（或 300ms 内复用）当前候选快照。返回的是**不可变快照**。</summary>
    public static 快照 生成()
    {
        lock (_锁)
        {
            var 现在 = TimeHelper.Now();
            if (_当前表.Count > 0 && 现在 - _本帧时刻 < 缓存毫秒) return _当前;

            _当前表.Clear();
            _本帧时刻 = 现在;

            try { 加治疗候选(); }
            catch (Exception e) { Ai调试.调试("候选集(治疗)异常：" + e.Message); }

            try { 加输出候选(); }
            catch (Exception e) { Ai调试.调试("候选集(输出)异常：" + e.Message); }

            _当前表.Sort((a, b) => b.本地分.CompareTo(a.本地分));
            if (_当前表.Count > 最多) _当前表.RemoveRange(最多, _当前表.Count - 最多);

            // [!] `候选Id` = 快照号 + 序号 ⇒ **永不重编号**；
            //     位置编号 `C1/C2` 每次重排都会变，**不能**当身份。
            var id = ++_下一个快照Id;
            for (var i = 0; i < _当前表.Count; i++)
            {
                _当前表[i].编号 = "C" + (i + 1);
                _当前表[i].候选Id = "S" + id + "-" + (i + 1);
            }

            快照 新 = new()
            {
                快照Id = id,
                生成时刻 = 现在,
                候选表 = _当前表.ToArray(),   // ★ 副本 —— 之后 _当前表 再改也不影响它
            };

            _当前 = 新;
            存快照(新);
            return 新;
        }
    }

    /// <summary>
    /// **按快照Id 取回当时那份候选** —— AI 响应回来时用。
    /// [!] 取不到返回 null（太久远/已淘汰）—— **不要用当前候选凑合**，
    ///     那正是"AI 选 A、执行 B"的来源。
    /// </summary>
    public static 快照? 取快照(long 快照Id)
    {
        if (快照Id <= 0) return null;
        lock (_锁)
            return _快照表.TryGetValue(快照Id, out var s) ? s : null;
    }

    /// <summary>当前快照的编号（给提示词 / 日志）</summary>
    public static long 当前快照Id
    {
        get { lock (_锁) return _当前.快照Id; }
    }

    /// <summary>拼成给 AI 看的一段</summary>
    public static string 描述()
    {
        try
        {
            var 快 = 生成();
            if (快.候选表.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== 可选动作（快照 #{快.快照Id}，本地已算好，**从里面挑**，用编号回答）===");
            sb.AppendLine("  这些是当前时刻**已确认合法**的动作。不要自己编技能 ID。");
            sb.AppendLine("  能力技=不占 GCD / 瞬发=移动中也能放 / 缺口=离满血差多少 / 过量=预计浪费多少");

            foreach (var c in 快.候选表)
            {
                sb.Append("  ").Append(c.编号).Append('=').Append(c.技能名);

                if (c.目标Id != 0 && !string.IsNullOrEmpty(c.目标名))
                    sb.Append("->").Append(c.目标名);

                sb.Append(c.群体 ? " 群体" : " 单体");
                sb.Append(c.能力技 ? " 能力技" : " GCD");
                if (c.瞬发) sb.Append(" 瞬发");

                // [!] AOE 分开给"单体威力 x 命中数 = 总量" ——
                //     只给"量=900"会让人（和 AI）误以为是单体 900
                if (c.命中数 > 1 && c.单体威力 > 0)
                    sb.Append(" 单体").Append(c.单体威力).Append('x').Append(c.命中数)
                      .Append('=').Append(c.量);
                else if (c.量 > 0)
                    sb.Append(" 量").Append(c.量);

                // [!] 即时治疗单独给 —— HoT 的"总量"和"立刻回多少"不是一回事
                if (c.即时治疗 > 0 && c.即时治疗 != c.量)
                    sb.Append(" 即时").Append(c.即时治疗);

                if (c.缺口 > 0.01f) sb.Append(" 缺口").Append((int)(c.缺口 * 100f)).Append('%');
                if (c.过量 > 0.04f) sb.Append(" 过量").Append((int)(c.过量 * 100f)).Append('%');
                if (c.耗蓝 > 0) sb.Append(" MP").Append(c.耗蓝);
                if (c.冷却 > 0.5f) sb.Append(" CD").Append((int)c.冷却).Append('s');
                if (c.资源 > 0) sb.Append(" 资源").Append(c.资源);

                sb.AppendLine();
            }

            sb.AppendLine("  回答：`C1|一句话理由`，一行一个，按先后顺序。");
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    // ==================== 内部（调用方必须已持锁）====================

    private static void 存快照(快照 s)
    {
        _快照表[s.快照Id] = s;
        _快照序.Enqueue(s.快照Id);
        while (_快照序.Count > 快照保留)
        {
            var 旧 = _快照序.Dequeue();
            if (旧 != s.快照Id) _快照表.Remove(旧);
        }
    }

    private static void 加治疗候选()
    {
        var 技能表 = 职业表();
        if (技能表 == null) return;

        var 队 = HealTargetHelper.可治疗队友(30f);
        if (队.Count == 0) return;

        foreach (var 技 in 技能表.治疗候选.已解锁())
        {
            if (技.Id == 0) continue;
            try
            {
                if (!SpellUtil.已解锁(技.Id)) continue;
                if (!SpellUtil.可用(技.Id)) continue;
            }
            catch { continue; }

            if (技.群体)
            {
                IBattleChara? 最缺 = null;
                var 最缺量 = 0f;
                var 有人 = 0;
                foreach (var r in 队)
                {
                    var g = 治疗决策.缺口量(r);
                    if (g <= 0.01f) continue;
                    有人++;
                    if (g > 最缺量) { 最缺量 = g; 最缺 = r; }
                }
                if (有人 == 0 || 最缺 == null) continue;

                var 候选 = 造(技, 最缺, 最缺量);
                if (候选 != null)
                {
                    候选.群体 = true;
                    候选.本地分 += Math.Min(1.0f, 有人 * 0.15f);
                    _当前表.Add(候选);
                }
            }
            else
            {
                foreach (var r in 队)
                {
                    var 缺口 = 治疗决策.缺口量(r);
                    if (缺口 <= 0.01f) continue;
                    var 候选 = 造(技, r, 缺口);
                    if (候选 != null) _当前表.Add(候选);
                }
            }
        }
    }

    private static 候选? 造(治疗技能 技, IBattleChara 目标, float 缺口)
    {
        try
        {
            var 目标Id = 目标.GameObjectId;
            foreach (var x in _当前表)
                if (x.技能Id == 技.Id && x.目标Id == 目标Id) return null;

            var 上限 = 最大血(目标);
            var 单次 = MathF.Max(1f, 技.总恢复力);
            var 缺口绝对值 = 缺口 * 上限;
            var 过量 = 单次 > 0f ? MathF.Max(0f, (单次 - 缺口绝对值) / 单次) : 0f;
            var 覆盖 = MathF.Min(1f, 单次 / MathF.Max(1f, 缺口绝对值));

            return new 候选
            {
                技能Id = 技.Id,
                技能名 = string.IsNullOrEmpty(技.名) ? ("技能" + 技.Id) : 技.名,
                目标Id = 目标Id,
                目标名 = 名(目标),
                群体 = 技.群体,
                能力技 = 技.冷却 > 0f,
                瞬发 = 技.瞬发,
                量 = (int)技.总恢复力,
                即时治疗 = (int)技.即时恢复力,
                命中数 = 1,
                缺口 = Math.Clamp(缺口, 0f, 1f),
                过量 = Math.Clamp(过量, 0f, 1f),
                耗蓝 = 技.MP,
                冷却 = 技.冷却,
                资源 = 技.资源消耗,
                本地分 = 覆盖 * 2f - 过量 * 1.5f + (技.冷却 > 0f ? 0.3f : 0f),
            };
        }
        catch { return null; }
    }

    private static void 加输出候选()
    {
        var 技能表 = 职业表();
        if (技能表 == null) return;

        var 目标 = 输出目标.选();
        if (目标 == null || !目标.活着()) return;

        var 等级 = (int)Core.Me.Level;

        加输出(技能表.基础输出, 技能表.查威力(技能表.基础输出, 等级), 1, 目标, false);

        try
        {
            var 敌数 = HealTargetHelper.周围敌人数量(技能表.AOE伤害范围, 25f);
            if (敌数 >= 技能表.AOE最少敌人数 && 技能表.群体输出 != 0)
            {
                var 单体 = 技能表.查威力(技能表.群体输出, 等级);
                加输出(技能表.群体输出, 单体 * 敌数, 敌数, 目标, true, 单体);
            }
        }
        catch { }

        try
        {
            if (技能表.Dot技能 != 0 && HealTargetHelper.值得上Dot(目标))
                加输出(技能表.Dot技能, 技能表.查威力(技能表.Dot技能, 等级), 1, 目标, false);
        }
        catch { }
    }

    private static void 加输出(uint 技能Id, int 总威力, int 命中数,
                              IBattleChara 目标, bool 群体, int 单体威力 = 0)
    {
        if (技能Id == 0) return;
        try
        {
            if (!SpellUtil.已解锁(技能Id)) return;
            if (!SpellUtil.可用(技能Id)) return;
        }
        catch { return; }

        foreach (var c in _当前表)
            if (c.技能Id == 技能Id) return;

        bool 瞬发;
        try { 瞬发 = SpellUtil.移动中可用(技能Id); } catch { 瞬发 = false; }

        _当前表.Add(new 候选
        {
            技能Id = 技能Id,
            技能名 = 取技能名(技能Id),
            目标Id = 目标.GameObjectId,
            目标名 = 名(目标),
            群体 = 群体,
            能力技 = false,
            瞬发 = 瞬发,
            量 = 总威力,
            命中数 = 命中数,
            单体威力 = 单体威力,
            本地分 = 总威力 / 100f,
        });
    }

    // ==================== 小工具 ====================

    private static JobSpellTable? 职业表()
    {
        try { return HealRotationEventHandler.当前技能表; }
        catch { return null; }
    }

    private static string 取技能名(uint 技能Id)
    {
        try
        {
            var 表 = 职业表();
            if (表 != null)
                foreach (var 技 in 表.治疗候选.全部)
                    if (技.Id == 技能Id && !string.IsNullOrEmpty(技.名)) return 技.名;
        }
        catch { }
        return "技能" + 技能Id;
    }

    private static float 最大血(IBattleChara 目标)
    {
        try { return MathF.Max(1f, 目标.MaxHp); } catch { return 1f; }
    }

    private static string 名(IBattleChara 目标)
    {
        try { return 目标.Name.ToString(); } catch { return "?"; }
    }
}
