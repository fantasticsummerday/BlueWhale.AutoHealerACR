using Dalamud.Game.ClientState.JobGauge.Enums;

namespace HealerACR.Common;

/// <summary>
/// 占星「六张卡各自发谁」。
///
/// ══════════════════════════════════════════════════════════════════
///  [!] 原来只有一句话："近战卡给近战，其余给远程，都在各自那类里挑血最少的"✗
///      六张卡的收益差得很远，发错人等于白送一张卡：
///
///        太阳神之衡（近战增伤）→ 按**职业优先级表**挑输出
///        战争神之枪（远程增伤）→ 同上，但顺序相反（远程优先）
///        放浪神之箭（暴击）    → 优先**开了秘策的学者**（秘策让下一发必暴）
///        世界树之干（减伤）    → 坦克死刑预判 > 血最少的坦克 > 血最少的队友（≤90%）
///        建筑神之塔（减伤）    → 坦克，且**已经有盾就换人/不发**
///        河流神之瓶（回蓝）    → 缺血的治疗优先，其次缺血的其他队友
///
///  [!] 所有卡都排斥「黑头」：给一个正在吃伤害降低的人发增伤卡是纯浪费。
///
///  [!] 自己当兜底有**时间窗**：实在没人可发时先等 3 秒
///      （队伍在换位/复活途中），等不到才发给自己 ——
///      否则一进本就先把卡贴自己脸上。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 占星卡目标
{
    // ==================================================================
    //  职业优先级表
    //
    //  ⚠️ 为什么用**职业**而不是"近战/远程"两大类：
    //     给谁增伤的收益取决于那个职业**这段时间能打出多少**，
    //     而不是他站得离怪多近。所以按职业排，近战/远程只是**分表**。
    //
    //  ⚠️ 两张表分**起手**与**2 分钟+** 两段 ——
    //     开场爆发期和后续循环期，各职业的收益顺序不一样。
    //     战斗 30 秒内 = 起手，30~120 秒按下表，120 秒后继续用 2 分钟表。
    // ==================================================================

    private static readonly uint[] 近战起手 = { 34, 30, 22, 32, 20, 39, 41 };   // 武士 忍者 龙骑 暗骑 武僧 镰刀 蝰蛇
    private static readonly uint[] 近战二分钟 = { 34, 30, 41, 22, 20, 32, 39 };
    private static readonly uint[] 远程起手 = { 42, 27, 31, 38, 35, 25, 23 };   // 绘灵 召唤 机工 舞者 赤魔 黑魔 诗人
    private static readonly uint[] 远程二分钟 = { 42, 27, 31, 23, 35, 38, 25 };

    /// <summary>
    /// 战斗时间（毫秒）→ 用哪张优先级表。
    ///
    /// [!] **起手表一直用到 120 秒**（表外审计 P1-8 修正；原来是 30 秒）。
    ///
    /// 参考 IL（`发卡设置.更新战斗阶段`，L298-318）逐字是：
    /// <code>
    ///   var t = AI.Instance.BattleData.CurrBattleTimeInMs;
    ///   int next;
    ///   if (t &lt;= 30000)      next = 2;              // IL_0054 ble.s  → ldc.i4.2
    ///   else if (t &lt; 120000) next = 当前战斗阶段;    // IL_005d bge.s 失败 → br IL_0069
    ///   else                 next = 3;              // IL_0065        → ldc.i4.3
    /// </code>
    /// ⇒ **30 秒 ~ 120 秒这一段"阶段不变"**，也就是说起手那一档（阶段 2）
    ///   会**一直用到 120 秒**，不是 30 秒就换。
    ///
    /// [!] 参考的字典键只有两个：`"起手"` 与 `"2分钟+"`（`.cctor` 里
    ///     `ldstr "起手"` / `ldstr "2分钟+"` 各出现两次，分别给近战和远程）
    ///     —— 没有第三张表，所以"不切换"就等于"继续用起手表" ✓
    ///
    /// [!] 我们原来在 30 秒就切到二分钟表 ⇒ 30~120 秒这一段发卡顺序与参考不同
    ///     （这一段是开场爆发的后半段，顺序差异会有实际影响）。
    /// </summary>
    private static bool 用起手表()
    {
        try
        {
            var 毫秒 = AI.Instance?.BattleData?.CurrBattleTimeInMs ?? 0;
            return 毫秒 <= 120_000;
        }
        catch
        {
            return true;
        }
    }

    private static uint[] 近战表 => 用起手表() ? 近战起手 : 近战二分钟;

    private static uint[] 远程表 => 用起手表() ? 远程起手 : 远程二分钟;

    // ==================================================================
    //  公共工具
    // ==================================================================

    /// <summary>
    /// 自己（拿不到返回 null）。
    ///
    /// [!] 一律走 `Core.Me?.…` 这种写法 —— `Core.Me` 在换图/退出时会是 null，
    ///     直接拆属性会抛；而在原生的那些成员上，真机里会直接崩进程。
    /// </summary>
    private static IBattleChara? 我 => Core.Me;

    /// <summary>自己的实体 id（拿不到返回 0，于是不会等于任何成员）</summary>
    private static uint 我的Id => Core.Me?.EntityId ?? 0;

    /// <summary>能发卡吗：有效、活着、能接受治疗（假死/无敌/不可选中都不发）</summary>
    private static bool 可发(IBattleChara? c)
    {
        try
        {
            return c != null && c.对象有效() && c.活着() && c.可以治();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 这个人的职业 id（拿不到返回 0）。
    ///
    /// ★ 入口先判 `对象有效()`：`ClassJob` 是**原生的行引用**，
    ///   换图时成员被释放但仍非 null（哨兵 0x12345679），直接读会触发原生访问违例。
    /// </summary>
    public static uint 职业Id(IBattleChara c)
    {
        if (c == null || !c.对象有效()) return 0;

        try
        {
            return c.ClassJob.IsValid ? (uint)c.ClassJob.RowId : 0u;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 按优先级表挑第一个人。
    ///
    /// [!] `排除黑头` 为真时，优先在"没有黑头"的人里挑；
    ///     全队都黑头才退回按原表挑（否则会出现"明明有人却一张卡都不发"）。
    /// </summary>
    private static IBattleChara? 按职业挑(uint[] 表, CardType 卡, bool 排除黑头)
    {
        try
        {
            var 候选 = new List<IBattleChara>();

            foreach (var 成员 in PartyHelper.Party)
            {
                // ★ 判 对象有效()：换图时成员被释放但仍非 null（哨兵地址），只判 null 会崩
                if (成员 == null || !成员.对象有效()) continue;
                if (!可发(成员)) continue;
                if (成员.EntityId == 我的Id) continue;   // 自己另走兜底
                if (职业Id(成员) == 0) continue;
                if (Array.IndexOf(表, 职业Id(成员)) < 0) continue;
                if (已有这张卡(成员, 卡)) continue;                 // 同一张卡不叠发
                if (候选.Any(x => x.EntityId == 成员.EntityId)) continue;
                候选.Add(成员);
            }

            if (候选.Count == 0) return null;

            // 先按"没有黑头"过滤一遍
            if (排除黑头)
            {
                var 干净的 = 候选.Where(x => !AuraIds.是黑头(x)).ToList();
                if (干净的.Count > 0) 候选 = 干净的;
            }

            // 表里靠前的优先；同职业取血最少的
            return 候选
                .OrderBy(x => Array.IndexOf(表, 职业Id(x)))
                .ThenBy(x => x.血量比例())
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>血最少的队友，可选"只挑坦克"</summary>
    private static IBattleChara? 血最少(bool 只要坦克, float 阈值, CardType 卡)
    {
        try
        {
            var 来源 = 只要坦克
                ? PartyHelper.CastableTanks
                : PartyHelper.CastableAlliesWithin30;

            return 来源
                .Where(可发)
                .Where(x => x.血量比例() < 阈值)
                .Where(x => !AuraIds.是黑头(x))
                .Where(x => !已有这张卡(x, 卡))
                .OrderBy(x => x.血量比例())
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    // ==================================================================
    //  自己当兜底（3 秒等待窗）
    // ==================================================================

    private static long _自己兜底开始;
    private static uint _自己兜底卡面;

    /// <summary>
    /// 没人可发时，要不要发给自己。
    ///
    /// [!] **不是立刻**给自己 —— 先记下"从这一刻开始等"，
    ///     3 秒内一直返回 false（队伍可能在换位/复活途中，马上就有合适的人）；
    ///     满 3 秒还没人，才真的发给自己。
    ///     换了一张卡就重新计时（`卡面` 不同就重置）。
    /// </summary>
    private static bool 可以发给自己(CardType 卡)
    {
        // 自己身上已经有这张卡 ⇒ 发给自己也是白送
        try
        {
            if (已有这张卡(我, 卡)) return false;
        }
        catch { }

        var 现在 = Environment.TickCount64;
        var 卡面 = (uint)卡;

        if (_自己兜底卡面 != 卡面 || _自己兜底开始 == 0)
        {
            _自己兜底卡面 = 卡面;
            _自己兜底开始 = 现在;
            return false;
        }

        return 现在 - _自己兜底开始 >= 3000;
    }

    /// <summary>战斗重置 / 换本时清（有状态就得清）</summary>
    public static void 重置()
    {
        _自己兜底开始 = 0;
        _自己兜底卡面 = 0;
    }

    // ==================================================================
    //  六张卡各自的规则
    // ==================================================================

    /// <summary>
    /// 挑这一张卡该发给谁。没有合适的人返回 null（调用方据此不发）。
    /// </summary>
    /// <param name="卡">手上的卡面</param>
    /// <param name="去重">
    /// 要不要排除"身上已经有同一张卡"的人。
    /// 开着时：能换人就换人，全都挂着就挑不出人（返回 null ⇒ 这张卡留着下次发）。
    /// </param>
    public static IBattleChara? 选目标(CardType 卡, bool 去重 = true)
    {
        _去重 = 去重;

        try
        {
            return 卡 switch
            {
                CardType.Balance => 选增伤卡(用近战: true, 卡),
                CardType.Spear   => 选增伤卡(用近战: false, 卡),
                CardType.Arrow   => 选放浪神(卡),
                CardType.Bole    => 选世界树(卡),
                CardType.Spire   => 选建筑神(卡),
                CardType.Ewer    => 选河流神(卡),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            _去重 = true;
        }
    }

    /// <summary>这一次挑选要不要排除"已经有同一张卡"的人（由 <see cref="选目标"/> 设置）</summary>
    private static bool _去重 = true;

    /// <summary>
    /// 这个人身上已经有手上这张卡了吗（`卡` 为 None 时恒 false）。
    /// </summary>
    private static bool 已有这张卡(IBattleChara c, CardType 卡)
    {
        if (!_去重) return false;
        if (卡 == CardType.None) return false;
        if (c == null || !c.对象有效()) return false;

        try
        {
            var buff = AuraIds.卡面Buff((uint)卡);
            return buff != 0 && c.HasAura(buff);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 太阳神之衡（近战卡）/ 战争神之枪（远程卡）。
    ///
    /// [!] 先在自己那一档里挑；那一档一个人都没有（比如全法系队拿到近战卡），
    ///     再跨到另一档去挑 —— 总比把卡捏在手里好。
    /// </summary>
    private static IBattleChara? 选增伤卡(bool 用近战, CardType 卡)
    {
        var 主表 = 用近战 ? 近战表 : 远程表;
        var 副表 = 用近战 ? 远程表 : 近战表;

        var 目标 = 按职业挑(主表, 卡, 排除黑头: true)
                   ?? 按职业挑(副表, 卡, 排除黑头: true);

        if (目标 != null) return 目标;

        // 全队都不是输出（比如双奶双坦的四人本）⇒ 发给自己，但要等满 3 秒
        if (可以发给自己(卡)) return 我;

        return null;
    }

    /// <summary>
    /// 放浪神之箭（暴击卡）。
    ///
    /// [!] **优先给开了「秘策」的学者**（1896）——
    ///     秘策让学者的下一发治疗必定暴击，这时候再叠一层暴击率提升收益最高。
    ///     没有这样的学者时，退回"缺血最少的那个队友"。
    /// </summary>
    private static IBattleChara? 选放浪神(CardType 卡)
    {
        try
        {
            foreach (var 成员 in PartyHelper.CastableAlliesWithin30)
            {
                if (成员 == null || !成员.对象有效()) continue;
                if (!可发(成员)) continue;
                if (成员.EntityId == 我的Id) continue;
                if (职业Id(成员) != 28) continue;                 // 28 = 学者
                if (!成员.HasAura(秘策)) continue;
                return 成员;
            }
        }
        catch { }

        // 没有"开秘策的学者"⇒ 发血最少的队友（阈值用紧急单奶那一档）
        var 阈值 = HealSettings.Instance.紧急单奶阈值;
        var 目标 = 血最少(只要坦克: false, 阈值, 卡) ?? 血最少(只要坦克: false, 0.90f, 卡);
        if (目标 != null) return 目标;

        if (可以发给自己(卡)) return 我;
        return null;
    }

    /// <summary>学者的「秘策」= 1896（让下一发治疗必定暴击）</summary>
    private static uint 秘策 => AuraIds.秘策;

    /// <summary>
    /// 世界树之干（减伤卡）。
    ///
    /// [!] 两条路：
    ///       ① **坦克死刑预判**：10 秒内有坦克死刑 → 给血最少的坦克
    ///          （阈值抬高 0.10，死刑前坦克血不满也该有减伤）
    ///       ② 没有死刑预告：给血最少的坦克；坦克都满血就给**血最少的队友**
    ///          （阈值 0.90 —— 世界树是减伤，血线不用卡得太低）
    ///
    /// [!] **排除已经有卡的人**：同一张卡叠不上，发第二张纯浪费。
    /// </summary>
    private static IBattleChara? 选世界树(CardType 卡)
    {
        var 阈值 = HealSettings.Instance.单体治疗阈值;

        if (有坦克死刑(10000))
        {
            var 死刑坦克 = 血最少(只要坦克: true, 阈值 + 0.10f, 卡);
            if (死刑坦克 != null) return 死刑坦克;
        }

        var 坦克 = 血最少(只要坦克: true, 阈值 + 0.10f, 卡);
        if (坦克 != null) return 坦克;

        var 队友 = 血最少(只要坦克: false, 0.90f, 卡);
        if (队友 != null) return 队友;

        if (可以发给自己(卡)) return 我;
        return null;
    }

    /// <summary>
    /// 建筑神之塔（减伤卡）。
    ///
    /// [!] 和世界树不同：这张**只给坦克**，而且**已经有盾就换人** ——
    ///     塔的减伤和护盾是同一层，叠着发等于只拿到一份。
    ///     坦克位找不到人时（四人本双奶、或坦克刚好不可治）才考虑自己。
    ///
    /// [!] 有坦克死刑预告时**放宽血线**（阈值 + 0.10）：
    ///     死刑要来了，"现在满血"不代表不需要预铺减伤。
    /// </summary>
    private static IBattleChara? 选建筑神(CardType 卡)
    {
        var 阈值 = HealSettings.Instance.单体治疗阈值;
        var 有死刑 = 有坦克死刑(10000);

        var 坦克 = 血最少(只要坦克: true, 阈值 + (有死刑 ? 0.10f : 0.0f), 卡);
        if (坦克 != null && !有盾(坦克)) return 坦克;

        // 坦克已经有盾 ⇒ 不发（减伤叠不上去，留着下次）
        if (坦克 != null) return null;

        // 没有坦克：自己血低且没盾才给自己
        try
        {
            var 自己 = 我;
            if (自己 != null && 可发(自己) && 自己.血量比例() < 0.50f && !有盾(自己)
                && 可以发给自己(卡))
            {
                return 自己;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// 河流神之瓶（回蓝卡）。
    ///
    /// [!] **治疗优先**：治疗没蓝 = 全队断疗，比输出少几个 GCD 严重得多。
    ///     所以先在所有"缺血的治疗"里挑血最少的，没有才轮到其他队友。
    /// </summary>
    private static IBattleChara? 选河流神(CardType 卡)
    {
        var 阈值 = HealSettings.Instance.紧急单奶阈值;

        try
        {
            var 治疗 = PartyHelper.CastableAlliesWithin30
                .Where(可发)
                .Where(x => x.EntityId != 我的Id)
                .Where(x => x.IsHealer())
                .Where(x => x.血量比例() < 阈值)
                .Where(x => !AuraIds.是黑头(x))
                .Where(x => !已有这张卡(x, 卡))
                .OrderBy(x => x.血量比例())
                .FirstOrDefault();

            if (治疗 != null) return 治疗;

            var 其他 = 血最少(只要坦克: false, 阈值, 卡);
            if (其他 != null) return 其他;
        }
        catch { }

        if (可以发给自己(卡)) return 我;
        return null;
    }

    // ==================================================================
    //  共用判据
    // ==================================================================

    /// <summary>
    /// 场上有没有自己的地星（「巨星主宰」1248 或未长大的「地星主宰」1224）。
    ///
    /// [!] 贵妇和地星**互斥**：地星已经铺下去时再交贵妇，两份群体治疗撞在同一拍，
    ///     地星那一发就白铺了。所以"地星在场 ⇒ 贵妇不发"。
    /// </summary>
    public static bool 地星在场()
    {
        try
        {
            // ⚠️ 表外审计 P0：**读 `Core.Me` 的光环前必须先判 `对象有效()`**
            //
            // [!] 项目铁律（`HealTargetHelper.cs:102-104`）：已释放的游戏对象是
            //     哨兵 `0x12345679` 而**不是 null**，在它身上读原生字段会抛
            //     **访问违例 `0xc0000005`，而且穿透 `catch` 直接杀进程**。
            //     所以 `我 != null` **不够**，必须再判一次 `对象有效()`。
            //
            // [!] 参考侧也没判（`小奥秘卡.txt:233-244` 直接 `Core.Me` + `HasAura`），
            //     但那是参考的疏漏 —— 我们自己的铁律要求判，而且这条
            //     **每帧都会被走到**（`AST_EarthlyStar.Check` 与贵妇判定都调它）。
            //
            // [!] 读不到时返回 `false`（= "没铺地星"）：
            //     代价是"可能多铺一颗地星"（游戏会拒绝重复铺，无害），
            //     比"读到哨兵直接崩游戏"好得多 ✓
            var 自己 = Self();
            if (自己 == null || !自己.对象有效()) return false;

            if (AuraIds.地星主宰 != 0 && 自己.HasAura(AuraIds.地星主宰)) return true;
            if (AuraIds.巨星主宰 != 0 && 自己.HasAura(AuraIds.巨星主宰)) return true;
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 自己（判过 `对象有效()` 的版本）。
    ///
    /// [!] 表外审计新增：`我` 那个属性直接返回 `Core.Me`（**没判有效性**），
    ///     凡是读它的**属性**（`HasAura` 等）都必须先过这一层。
    ///     纯做 `== null` 比较的地方可以继续用 `我`。
    /// </summary>
    private static IBattleChara? Self()
    {
        try
        {
            var me = AEAssist.Core.Me;
            return me != null && me.对象有效() ? me : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>20 米内的敌人数量（王冠卡判断"这一发值不值得交"用）</summary>
    public static int 近处敌人数量()
    {
        try
        {
            return TargetHelper.GetNearbyEnemyCount(20);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 有没有"血量危险到需要贵妇"的坦克（20 米内、可治、血低于阈值）。
    ///
    /// [!] 贵妇是**单体治疗**卡，连一个血线告急的坦克都没有时交它 = 浪费。
    /// </summary>
    public static bool 有血量危险的坦克(float 阈值)
    {
        try
        {
            foreach (var 坦克 in PartyHelper.CastableTanks)
            {
                if (!可发(坦克)) continue;
                if (坦克.Distance(我!) > 20f) continue;
                if (坦克.血量比例() < 阈值) return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>身上有没有盾（学者鼓舞/激励、贤者诊断盾）—— 塔不发给已经有盾的人</summary>
    private static bool 有盾(IBattleChara c)
    {
        // ★ 入口先判有效性：读游戏对象属性的原生访问违例会穿 catch（本项目 12 次崩溃都是这类）
        if (c == null || !c.对象有效()) return false;

        try
        {
            if (AuraIds.鼓舞 != 0 && c.HasAura(AuraIds.鼓舞)) return true;
            if (AuraIds.激励 != 0 && c.HasAura(AuraIds.激励)) return true;
            if (AuraIds.均衡诊断 != 0 && c.HasAura(AuraIds.均衡诊断)) return true;
            if (AuraIds.均衡预后 != 0 && c.HasAura(AuraIds.均衡预后)) return true;
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 这么多毫秒内有没有坦克死刑在读条。
    ///
    /// [!] 用 AEAssist 自己的死刑判据（它认 Boss 的读条表），
    ///     不是"读到任何大伤害读条"—— AOE 读条不该触发"给坦克减伤"这条分支。
    /// </summary>
    public static bool 有坦克死刑(int 毫秒)
    {
        try
        {
            foreach (var 敌人 in Data.AllHostileTargets)
            {
                if (敌人 == null || !敌人.对象有效()) continue;
                if (敌人.CurrentHp <= 0) continue;

                if (TargetHelper.targetCastingIsDeathSentenceWithTime(敌人, 毫秒)) return true;
            }
        }
        catch { }

        return false;
    }
}
