using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// **综合效果指标** —— 用来回答"AI 调参到底有没有让表现变好"。
///
/// ══════════════════════════════════════════════════════════════════════
///  ★ 为什么"先测，不判断" ★
///
///    "什么算好"和"各项占多少权重"是**策略取舍**，不该拍脑袋定：
///      · 过量治疗率高，在**高压**时是必然的（该过量也得过量）
///      · 蓝量均值低，可能是"低蓝停手"**主动**省蓝的结果，不是浪费
///      · 倒地次数太少（一把可能 0 次），单独当判据样本不够
///
///    所以本模块只**如实测量**，不合成分数、不做判断。
///    等拿到几把真实数据之后再决定怎么加权。
///
///  ★ 4 项指标与"资源最大化利用"的关系 ★
///
///    ① 能力技空转率 —— **最直接**：CD 转好了却一直闲着 = 资源没被用掉。
///    ② 过量治疗率   —— GCD 花在不需要的治疗上 = 另一种浪费。
///    ③ 蓝量均值     —— 资源（蓝）用掉了多少。
///    ④ 倒地/濒死    —— 兜底失败的硬证据。
///
///  ★ 为什么不依赖 ActionEffect hook ★
///
///    `效果确认.cs` 的注释写明：ActionEffect 回调**不含技能 ID**，
///    而且那个 hook 默认关（高风险）。所以这里全部用**状态判据**，
///    只读 `Cooldown` / 血量 / MP —— 不挂钩、不碰内存。
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public static class 战斗指标
{
    // ==================== ① 能力技空转 ====================

    /// <summary>技能 ID -> 它"就绪且没人用"已经持续了多久（毫秒）</summary>
    private static readonly Dictionary<uint, long> _空转起 = new();

    /// <summary>已经计入"空转"的技能（同一个 CD 周期只计一次，避免重复累积）</summary>
    private static readonly HashSet<uint> _本周期已计 = new();

    /// <summary>累计"空转秒"（所有大招加起来）</summary>
    private static double _空转秒;

    /// <summary>采样过的总帧数（用来算占比）</summary>
    private static long _帧数;

    /// <summary>
    /// 本周期**第一帧的时刻**（`Environment.TickCount64` 毫秒）。
    ///
    /// ★ 2026-10-15 新增（全代码审查发现）：`空转占比` 原来用 `_帧数 / 60.0` 反推"战斗秒"，
    ///   注释写"一帧约 1/60 秒"——**这个前提不成立**：`_帧数++` 在 `每帧更新()` 里，
    ///   而它的调用点是 resolver 的 `Check()`（按队列调度，**不是每游戏帧一次**，
    ///   一次调度可能连续跑多个 Check、也可能隔几拍才跑）⇒ 反推出的"战斗秒"可偏离数倍，
    ///   于是**空转占比整体失真**（本项目其余四项指标不受影响）。
    ///   ⇒ 改为**记录真实时刻**再算流逝秒（`_开始时刻 == 0` 表示本周期还没有样本）。
    /// </summary>
    private static long _开始时刻;

    /// <summary>判"这条大招被闲置了"的门槛（秒）—— 低于它算"刚转好，正常"</summary>
    private const float 空转门槛秒 = 5f;

    // ==================== ② 过量治疗 ====================

    private static int _治疗次数;
    private static int _过量次数;
    private static double _落点需要累计;
    private static long _落点需要采样;

    // ==================== ③ 蓝量 ====================

    private static double _蓝量累计;
    private static long _蓝量采样数;

    // ==================== ④ 倒地 / 濒死 ====================

    private static int _倒地次数;
    private static int _濒死次数;

    /// <summary>已经记过"倒地"的人（同一个人只记一次，防止每帧累加）</summary>
    private static readonly HashSet<ulong> _已记倒地 = new();

    /// <summary>已经记过"濒死"的人（脱离濒死之后才会再记一次）</summary>
    private static readonly HashSet<ulong> _已记濒死 = new();

    // ==================== 每帧采样 ====================

    /// <summary>
    /// 每帧调用（挂在心跳里）。
    ///
    /// [!] 只在战斗中采样 —— 脱战时的 CD 状态和血量没有意义。
    /// </summary>
    public static void 每帧更新()
    {
        try
        {
            if (!CharacterExt.我在战斗())
            {
                // 脱战：清"就绪计时"，避免把脱战那几分钟算成空转
                if (_空转起.Count > 0) _空转起.Clear();
                if (_本周期已计.Count > 0) _本周期已计.Clear();
                return;
            }

            _帧数++;
            // ★ 2026-10-15：本周期第一帧记下时刻（供 `空转占比` 算真实战斗秒）
            if (_开始时刻 == 0) _开始时刻 = Environment.TickCount64;

            // ③ 蓝量
            try
            {
                var 上限 = Math.Max(1u, CharacterExt.我的最大蓝量());
                _蓝量累计 += CharacterExt.我的当前蓝量() * 1.0 / 上限;
                _蓝量采样数++;
            }
            catch { }

            // ① 能力技空转
            采空转();

            // ④ 倒地 / 濒死
            采伤亡();
        }
        catch { }
    }

    private static void 采空转()
    {
        try
        {
            var 表 = HealerACR.Common.HealRotationEventHandler.取当前职业技能表();
            if (表 == null) return;

            var 现在 = TimeHelper.Now();

            // 只看**长 CD 大招**（短 CD 技能"闲着"是正常的，不该算浪费）
            foreach (var id in 大招清单(表))
            {
                if (id == 0) continue;

                var 剩 = SpellUtil.冷却剩余秒(id);

                if (剩 > 0.5f)
                {
                    // 在转 CD —— 结束上一轮的空转统计
                    if (_空转起.TryGetValue(id, out var 起) && 起 > 0)
                    {
                        var 闲了 = (现在 - 起) / 1000.0;
                        if (闲了 >= 空转门槛秒 && _本周期已计.Add(id))
                            _空转秒 += 闲了 - 空转门槛秒;   // 扣掉门槛那段（那是正常的）
                    }
                    _空转起.Remove(id);
                    continue;
                }

                // 已就绪 —— 开始/继续计时
                if (!_空转起.ContainsKey(id))
                {
                    _空转起[id] = 现在;
                    _本周期已计.Remove(id);      // 新的一轮 CD 周期
                }
            }
        }
        catch { }
    }

    /// <summary>值得统计空转的大招清单（**从技能表取，不写死 ID**）。</summary>
    private static IEnumerable<uint> 大招清单(HealerACR.Common.JobSpellTable 表)
    {
        yield return 表.紧急单奶;
        yield return 表.群体治疗能力技;
        yield return 表.团队减伤;
        yield return 表.预铺单奶能力技;
        yield return 表.单体盾;
        yield return 表.群体盾;
        yield return 表.单体HoT;
    }

    private static void 采伤亡()
    {
        try
        {
            foreach (var r in PartyHelper.CastableParty)
            {
                // ★ 判 对象有效()：换图时成员被释放但仍非 null（哨兵 0x12345679），只判 null 会崩
                if (r == null || !r.对象有效()) continue;
                var id = r.GameObjectId;
                var 比 = r.CurrentHp * 1f / Math.Max(1u, r.MaxHp);

                if (r.CurrentHp <= 0 || r.IsDead)
                {
                    if (_已记倒地.Add(id)) _倒地次数++;
                    continue;
                }

                _已记倒地.Remove(id);      // 被救起来了 -> 下次再倒还能记

                if (比 <= 0.15f)
                {
                    if (_已记濒死.Add(id)) _濒死次数++;
                }
                else if (比 > 0.30f)
                {
                    _已记濒死.Remove(id);   // 脱离濒死 -> 允许再记
                }
            }
        }
        catch { }
    }

    // ==================== ② 过量治疗的记录点 ====================

    /// <summary>
    /// **记一次治疗落点** —— 由执行层在真的放治疗技能时调用。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  [!] 为什么**不带目标**、也不算精确过量：
    ///
    ///      · `Spell` **没有公开的 Target 属性**（AEAssist 源码里找不到）
    ///        ==> `AfterSpell` 里拿不到"这次治的是谁"
    ///      · `效果确认` 的 ActionEffect hook 虽然有 targetId，但它
    ///        **不解析治疗量**，而且**默认关**（高风险）
    ///        ==> 依赖它会让指标在默认配置下完全没数据
    ///
    ///  [!] 所以用**队伍需要度**做代理：
    ///
    ///          需要度 = Σ(MaxHp - CurrentHp) / Σ(MaxHp)
    ///
    ///      记下"这次治疗落下去的时候，队伍到底缺不缺血"。
    ///        · 需要度很低 = 大家都不缺血 ==> 大概率是**过量治疗**（白花一个 GCD）
    ///        · 需要度很高 = 有人真的在掉血 ==> 用得值
    ///
    ///  [!] 诚实标注：这是**代理指标**，不是精确过量量。
    ///      但它在"拿不到治疗量"的前提下是**唯一能测的**，
    ///      而且比编一个看起来精确的数字好。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static void 记治疗落点(float 恢复力)
    {
        try
        {
            if (恢复力 <= 0f) return;      // 0 恢复力 = 不是治疗技

            _治疗次数++;

            // 采样"这一刻队伍有多缺" —— 累积起来最后取均值
            var 需 = 队伍需要度();
            if (需 >= 0f)
            {
                _落点需要累计 += 需;
                _落点需要采样++;
            }

            // 需要度低于 5% 视为"这次治疗基本是过量"
            if (需 >= 0f && 需 < 0.05f) _过量次数++;
        }
        catch { }
    }

    /// <summary>
    /// 队伍需要度（0~1）：所有人缺失血量 / 所有人上限血量之和。
    /// 读不到返回 -1。
    /// </summary>
    private static float 队伍需要度()
    {
        try
        {
            double 缺 = 0, 总 = 0;

            void 计入(IBattleChara? r)
            {
                if (r == null) return;
                var 上限 = r.MaxHp;
                if (上限 == 0) return;
                总 += 上限;
                缺 += Math.Max(0u, 上限 - r.CurrentHp);
            }

            计入(Core.Me);
            // ★ 判 对象有效()：`计入()` 会读游戏对象（血量），释放对象会崩
            foreach (var r in PartyHelper.CastableParty)
                if (r != null && r.对象有效()) 计入(r);

            return 总 > 0 ? (float)(缺 / 总) : -1f;
        }
        catch { return -1f; }
    }

    // ==================== 读数 ====================

    /// <summary>过量治疗比例（0~1）；没样本时返回 -1</summary>
    public static float 过量率 => _治疗次数 > 0 ? _过量次数 * 1f / _治疗次数 : -1f;

    /// <summary>蓝量比例均值；没样本时返回 -1</summary>
    public static float 蓝量均值 => _蓝量采样数 > 0 ? (float)(_蓝量累计 / _蓝量采样数) : -1f;

    /// <summary>治疗落点时的队伍需要度均值（越低越说明"治了不需要治的"）；没样本 -1</summary>
    public static float 落点需要均值 => _落点需要采样 > 0 ? (float)(_落点需要累计 / _落点需要采样) : -1f;

    /// <summary>能力技空转总秒数</summary>
    public static double 空转秒 => _空转秒;

    /// <summary>
    /// 空转占比 —— 空转秒 / 战斗秒。**这是"资源利用率"的反面**。
    /// 没样本时返回 -1。
    /// </summary>
    public static float 空转占比
    {
        get
        {
            // ★ 2026-10-15 修（全代码审查发现）：**不再用帧数反推** ——
            //   原注释写"一帧约 1/60 秒；用帧数反推战斗时长足够"，但 `_帧数++` 挂在
            //   resolver 的 `Check()` 上（按队列调度，**不是每游戏帧一次**）
            //   ⇒ 反推的"战斗秒"会偏离数倍，空转占比随之失真。
            //   ⇒ 用**真实流逝时间**（本周期第一帧至今），与 `_空转秒` 同量纲 ✓
            var 战斗秒 = _开始时刻 == 0
                ? 0.0
                : (Environment.TickCount64 - _开始时刻) / 1000.0;
            if (战斗秒 < 1.0) return -1f;
            return (float)(_空转秒 / 战斗秒);
        }
    }

    public static int 倒地次数 => _倒地次数;
    public static int 濒死次数 => _濒死次数;

    /// <summary>总采样帧数（判断样本够不够）</summary>
    public static long 采样帧数 => _帧数;

    // ==================== 输出 / 重置 ====================

    /// <summary>一行摘要（日志 / 调试窗用）。</summary>
    public static string 摘要()
    {
        try
        {
            var 过 = 过量率;
            var 蓝 = 蓝量均值;
            var 空 = 空转占比;
            return $"空转 {(_空转秒):F0}s({(空 >= 0 ? (空 * 100f).ToString("F0") + "%" : "样本不足")})" +
                   $"｜过量 {(过 >= 0 ? (过 * 100f).ToString("F0") + "%" : "样本不足")}" +
                   $"｜蓝量均值 {(蓝 >= 0 ? (蓝 * 100f).ToString("F0") + "%" : "样本不足")}" +
                   $"｜倒地 {_倒地次数}｜濒死 {_濒死次数}" +
                   // 预取耗时 —— 回答「开怪卡一下是不是 ACR」（用户在问）
                   $"｜最慢预取 {AiDecisionLayer.最慢预取毫秒}ms";
        }
        catch { return "（读不到）"; }
    }

    /// <summary>把本局指标打一条日志（**打完一把时调**）。</summary>
    public static void 打日志()
    {
        try
        {
            if (_帧数 < 60 * 10) return;   // 少于 10 秒不报（样本太少没意义）
            Ai调试.日志($"【本局指标】{摘要()}");
        }
        catch { }
    }

    /// <summary>
    /// **本局生效的 AI 参数快照** —— 和指标一起看才有意义。
    ///
    /// [!] 为什么要它：光看"空转 30s"没法判断是 AI 调坏了还是本来就这样。
    ///     把"这局 AI 调了哪些参数"记在旁边，才能对上因果。
    /// </summary>
    public static string 当前参数快照
    {
        get
        {
            try
            {
                var s = Ai策略参数.状态描述();
                return string.IsNullOrEmpty(s) || s == "（无调整）" ? "" : s;
            }
            catch { return ""; }
        }
    }

    /// <summary>重置（换本 / 战斗重置 / 收尾之后）。</summary>
    public static void 重置()
    {
        try
        {
            _空转起.Clear();
            _本周期已计.Clear();
            _已记倒地.Clear();
            _已记濒死.Clear();
            _空转秒 = 0;
            _帧数 = 0;
            _开始时刻 = 0;   // ★ 2026-10-15：与帧数一起清零（下一帧会重新打点）
            _治疗次数 = 0;
            _过量次数 = 0;
            _落点需要累计 = 0;
            _落点需要采样 = 0;
            _蓝量累计 = 0;
            _蓝量采样数 = 0;
            _倒地次数 = 0;
            _濒死次数 = 0;
        }
        catch { }
    }
}
