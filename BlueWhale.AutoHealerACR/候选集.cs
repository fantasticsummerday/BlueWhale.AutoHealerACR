using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// **候选动作集** —— 本地把"当前合法且值得考虑的动作"算成一组候选。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么需要它（第三方审阅第 9/10/24 条）★
///
///    现状：`AI -> SkillId`。
///    白名单只能证明"**这个技能本职业有**"，不能证明
///    "**这个局面下该用它**"。于是 `AiSuggestionResolver` 里不断补条件，
///    实际上是把"合法性判断"重新实现了一遍。
///
///    目标：本地先把游戏世界压缩成一组**真实、合法、可执行**的候选，
///    AI 只负责"在候选之间取舍"。
///
///  ★ 与 `治疗决策.选最优` 的关系 ★
///
///    `选最优` = "本地直接选一个最优"（返回技能，不含目标）
///    本类     = "把若干可能的选择列出来交给 AI"（**带目标**）
///    两者互补：AI 不可用 -> 走 `选最优`；AI 可用 -> 给它候选集。
///
///  [!] 候选**必须带目标**：AI 现在的输出只有 `技能ID|理由`，
///      目标由本地现算 —— "AI 想治 A、本地选了 B" 时会错位。
///
///  [!] 数量限量：提示词预算很紧（实测最坏 102%），
///      所以按本地算分排序取前 N，既省字符又保证列出来的都值得考虑。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 候选集
{
    /// <summary>一个候选动作</summary>
    public sealed class 候选
    {
        /// <summary>短编号（给 AI 引用，形如 C1）</summary>
        public string 编号 = "";

        public uint 技能Id;
        public string 技能名 = "";

        /// <summary>目标（0 = 自己 / 无目标）</summary>
        public ulong 目标Id;
        public string 目标名 = "";

        public bool 群体;
        public bool 能力技;
        public bool 瞬发;

        /// <summary>治疗恢复力 / 输出威力</summary>
        public int 量;

        /// <summary>目标当前缺口（% 最大血，0~1）</summary>
        public float 缺口;

        /// <summary>预计过量（0~1）</summary>
        public float 过量;

        public int 耗蓝;
        public float 冷却;

        /// <summary>资源消耗（以太 / 蛇胆之类，0 = 不耗）</summary>
        public int 资源;

        /// <summary>本地算分（**只用于排序，不给 AI 看** —— 免得它被本地分数带跑）</summary>
        public float 本地分;
    }

    private const int 最多 = 8;
    private const int 缓存毫秒 = 300;

    private static readonly List<候选> _本帧 = new();
    private static long _本帧时刻;

    /// <summary>生成当前候选集（按本地算分排序、限量、重新编号）</summary>
    public static IReadOnlyList<候选> 生成()
    {
        var 现在 = TimeHelper.Now();
        if (_本帧.Count > 0 && 现在 - _本帧时刻 < 缓存毫秒) return _本帧;

        _本帧.Clear();
        _本帧时刻 = 现在;

        try { 加治疗候选(); }
        catch (Exception e) { Ai调试.调试("候选集(治疗)异常：" + e.Message); }

        try { 加输出候选(); }
        catch (Exception e) { Ai调试.调试("候选集(输出)异常：" + e.Message); }

        _本帧.Sort((a, b) => b.本地分.CompareTo(a.本地分));
        if (_本帧.Count > 最多) _本帧.RemoveRange(最多, _本帧.Count - 最多);
        for (var i = 0; i < _本帧.Count; i++) _本帧[i].编号 = "C" + (i + 1);

        return _本帧;
    }

    /// <summary>按编号取候选（执行层用）</summary>
    public static 候选? 按编号(string? 编号)
    {
        if (string.IsNullOrWhiteSpace(编号)) return null;
        var 要 = 编号.Trim();
        foreach (var c in _本帧)
            if (string.Equals(c.编号, 要, StringComparison.OrdinalIgnoreCase))
                return c;
        return null;
    }

    /// <summary>拼成给 AI 看的一段（**替代提示词里那份散文技能清单**）</summary>
    public static string 描述()
    {
        try
        {
            var 表 = 生成();
            if (表.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== 可选动作（本地已算好，**从里面挑**，用编号回答）===");
            sb.AppendLine("  这些是当前时刻**已确认合法**的动作。不要自己编技能 ID。");
            sb.AppendLine("  格式含义：能力技=不占 GCD / 瞬发=移动中也能放 / 缺口=目标离满血差多少 / 过量=预计浪费多少");

            foreach (var c in 表)
            {
                sb.Append("  ").Append(c.编号).Append('=').Append(c.技能名);

                if (c.目标Id != 0 && !string.IsNullOrEmpty(c.目标名))
                    sb.Append("->").Append(c.目标名);

                sb.Append(c.群体 ? " 群体" : " 单体");
                sb.Append(c.能力技 ? " 能力技" : " GCD");
                if (c.瞬发) sb.Append(" 瞬发");

                if (c.量 > 0) sb.Append(" 量").Append(c.量);
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

    // ==================== 候选来源 ====================

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
                // 群体：把有缺口的人聚成一个候选（目标取缺口最大的那个，执行时按范围算）
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

                var 候选 = 造(技, 最缺, 最缺量, 技.总恢复力);
                if (候选 != null)
                {
                    候选.群体 = true;
                    // 群体治多个人 -> 分高一点（覆盖人数也算价值）
                    候选.本地分 += Math.Min(1.0f, 有人 * 0.15f);
                    _本帧.Add(候选);
                }
            }
            else
            {
                foreach (var r in 队)
                {
                    var 缺口 = 治疗决策.缺口量(r);
                    if (缺口 <= 0.01f) continue;

                    var 候选 = 造(技, r, 缺口, 技.总恢复力);
                    if (候选 != null) _本帧.Add(候选);
                }
            }
        }
    }

    /// <summary>把一条治疗技能 + 目标变成候选</summary>
    private static 候选? 造(治疗技能 技, IBattleChara 目标, float 缺口, float 恢复力)
    {
        try
        {
            var 目标Id = 目标.GameObjectId;

            // 同一个"技能+目标"只留一条
            foreach (var x in _本帧)
                if (x.技能Id == 技.Id && x.目标Id == 目标Id) return null;

            var 上限 = 最大血(目标);
            var 单次 = MathF.Max(1f, 恢复力);
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
                量 = (int)恢复力,
                缺口 = Math.Clamp(缺口, 0f, 1f),
                过量 = Math.Clamp(过量, 0f, 1f),
                耗蓝 = 技.MP,
                冷却 = 技.冷却,
                资源 = 技.资源消耗,
                // 本地算分：覆盖够 + 过量少 + 能力技加分（不占 GCD）
                本地分 = 覆盖 * 2f - 过量 * 1.5f + (技.冷却 > 0f ? 0.3f : 0f),
            };
        }
        catch
        {
            return null;
        }
    }

    private static void 加输出候选()
    {
        var 技能表 = 职业表();
        if (技能表 == null) return;

        var 目标 = 输出目标.选();
        if (目标 == null || !目标.活着()) return;

        var 等级 = (int)Core.Me.Level;

        // 基础输出
        加输出(技能表.基础输出, 技能表.查威力(技能表.基础输出, 等级), 目标, false);

        // 群体输出（敌人够多才列）
        try
        {
            var 敌数 = HealTargetHelper.周围敌人数量(技能表.AOE伤害范围, 25f);
            if (敌数 >= 技能表.AOE最少敌人数 && 技能表.群体输出 != 0)
                加输出(技能表.群体输出, 技能表.查威力(技能表.群体输出, 等级) * 敌数, 目标, true);
        }
        catch { }

        // DoT（值得上才列）
        try
        {
            if (技能表.Dot技能 != 0 && HealTargetHelper.值得上Dot(目标))
                加输出(技能表.Dot技能, 技能表.查威力(技能表.Dot技能, 等级), 目标, false);
        }
        catch { }
    }

    private static void 加输出(uint 技能Id, int 威力, IBattleChara 目标, bool 群体)
    {
        if (技能Id == 0) return;
        try
        {
            if (!SpellUtil.已解锁(技能Id)) return;
            if (!SpellUtil.可用(技能Id)) return;
        }
        catch { return; }

        foreach (var c in _本帧)
            if (c.技能Id == 技能Id) return;

        bool 瞬发;
        try { 瞬发 = SpellUtil.移动中可用(技能Id); } catch { 瞬发 = false; }

        _本帧.Add(new 候选
        {
            技能Id = 技能Id,
            技能名 = 取技能名(技能Id),
            目标Id = 目标.GameObjectId,
            目标名 = 名(目标),
            群体 = 群体,
            能力技 = false,
            瞬发 = 瞬发,
            量 = 威力,
            本地分 = 威力 / 100f,   // 输出候选的"分"就是威力（AOE 已乘敌数）
        });
    }

    // ==================== 小工具 ====================

    private static JobSpellTable? 职业表()
    {
        try { return HealRotationEventHandler.当前技能表; }
        catch { return null; }
    }

    /// <summary>
    /// 取技能名。
    ///
    /// [!] 只从**治疗候选表**里找 —— 那是唯一带名字的地方（`治疗技能.名`）。
    ///     输出技能没现成的名字表，退回显示 ID：
    ///     反正 AI 只需要看得懂"这是哪个候选"，编号才是它引用的键。
    /// </summary>
    private static string 取技能名(uint 技能Id)
    {
        try
        {
            var 表 = 职业表();
            if (表 != null)
            {
                foreach (var 技 in 表.治疗候选.全部)
                    if (技.Id == 技能Id && !string.IsNullOrEmpty(技.名)) return 技.名;
            }
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
