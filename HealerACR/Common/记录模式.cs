using AEAssist;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// 记录模式 —— **只观察，不动手**。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 这是什么 ★
///
///    用户要的一个模式：开了之后 **ACR 完全不参与输出和治疗**，
///    只把**玩家自己手动**放的每一个技能连同当时的局面记录下来。
///    副本结束后由 AI 提炼成"记忆库"，下次初始化时喂回给 AI。
///
///    目的是拿到**人类高手打法的参考答案** ——
///    这是记忆库唯一的可信来源（见下面"为什么必须停手"）。
///
///  ★ 为什么必须"完全停手"（用户选择的最严格档）★
///
///    记忆库最大的风险是**回音壁**：
///      ACR 做决策 → 存库 → 检索到 → 照做 → 又存库
///    越用越确信自己的习惯是对的，**错误被固化**。
///
///    所以记录模式里必须让 ACR 一个技能都不放 ——
///    这样"记录到的每一条"都必然是玩家手动按的，**归属天然干净**，
///    不需要任何启发式去猜"这条是谁放的"。
///
///    ⚠️ 代价：这期间 **ACR 不会替你奶**，全程得自己操作。
///       这是有意的取舍 —— 数据纯度优先。
///
///  ★ 为什么放在 HealerACR 这边（而不是 BlueWhale）★
///
///    "停手"是**战斗行为的开关**，必须由本地层执行 ——
///    否则就违反了 开发约定.md G 节（AI 是增强层，不能成为必需品）。
///    存盘和提炼那一半需要 AI，放在 BlueWhale 用钩子接
///    （和 记忆钩子 / 状态重置钩子 同一个套路）。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 记录模式
{
    // ==================== 开关 ====================

    /// <summary>
    /// 是否处于记录模式。
    ///
    /// ⚠️ 这个标志**同时被两处读取**，改它必须两处都生效：
    ///   · `HealerEntryBase.构建决策队列()` —— 返回空队列 = ACR 停手
    ///   · <see cref="每帧更新"/> —— 只有开启时才观察玩家操作
    /// </summary>
    public static bool 开启 { get; private set; }

    /// <summary>
    /// 记录模式下是否**保留保命兜底**。
    ///
    /// 用户选了"只记录，完全不动" → 默认 **false**（最纯净）。
    /// 留这个开关是因为日随里全程手动奶确实累，
    /// 万一以后想放松一点，改这里就行。
    ///
    /// ⚠️ 一旦打开，记录里就会出现 ACR 自己的操作 ——
    ///    那些条目的归属就不干净了。启用时会在日志里**明确警告**。
    /// </summary>
    public static bool 保留保命兜底 = false;

    /// <summary>切换记录模式（带日志提示）</summary>
    public static void 设置(bool 开)
    {
        if (开启 == 开) return;
        开启 = 开;

        try
        {
            if (开)
            {
                LogHelper.Info("[记录模式] 已开启 —— ACR 停止输出与治疗，只记录你的手动操作");
                LogHelper.Info(保留保命兜底
                    ? "[记录模式] 保命兜底开启：记录里会混入 ACR 自己的操作，归属不再纯净"
                    : "[记录模式] 保命兜底关闭：全程需要你手动操作（记录最纯净）");
            }
            else
            {
                LogHelper.Info("[记录模式] 已关闭 —— ACR 恢复正常运行");
            }
        }
        catch { }

        // 通知外面（BlueWhale 用它决定要不要采集/提炼）
        try { 进入记录模式?.Invoke(开); } catch { }
    }

    /// <summary>切换通知（参数 = 是否开启）。HealerACR 不挂 → 什么也不发生。</summary>
    public static Action<bool>? 进入记录模式;

    // ==================== 手动施法观察 ====================

    /// <summary>
    /// 玩家放了一个技能 —— 参数：(技能ID, 技能名)。
    /// 由 BlueWhale 挂上；没挂就什么也不发生（原版 HealerACR 不带记录功能）。
    /// </summary>
    public static Action<uint, string>? 记录一次;

    /// <summary>
    /// 上一次观察到的施法（用来判断"新的一次"）。
    ///
    /// ══════════════════════════════════════════════════════════════
    ///  ★ 双通道观察（能力技漏记的问题已解决）★
    ///
    ///   **通道① 施法成功事件**（主力，能抓瞬发）
    ///     `MemApiSpellCastSuccess.OnCastSucces` —— AEAssist hook 了
    ///     动作包发送函数，瞬发能力技也会经过它。
    ///     详见 <see cref="订阅施法事件"/>（含归属验证与已知漏洞）。
    ///
    ///   **通道② 读条轮询**（兜底）
    ///     `IBattleChara.IsCasting` + `CastActionId` 的上升沿。
    ///     ⚠️ 属性名是 `CastActionId`，不是 `CurrentAction`
    ///        （后者在 IBattleChara 上不存在，编译期就会报错）。
    ///
    ///   两者按 <see cref="去重窗口毫秒"/> 去重（同一次施放可能都被看到）。
    ///
    ///  ── 现在的覆盖情况 ──
    ///     ✅ 读条技能（大部分治疗/输出/复活）—— 两条通道都能抓
    ///     ✅ **瞬发能力技（减伤、天赐祝福、神速咏唱…）—— 通道①**
    ///     ⚠️ 通道① 的**归属**需要验证（见 订阅施法事件 的说明），
    ///        验不准的会标为「疑似」，提炼时能看到比例
    ///
    ///  ⚠️ 提炼提示词里的「已知局限」仍然保留 ——
    ///     因为「疑似」这一类**可能混入队友的同职业技能**，
    ///     不能当成 100% 确定的事实。
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    private static uint _上次技能Id;

    /// <summary>上一次是否在读条 —— 用来抓"开始读条"这个**上升沿**</summary>
    private static bool _上次在读条;

    /// <summary>
    /// 每帧调用（挂在心跳里）。**非记录模式下直接返回**，零开销。
    ///
    /// ── 双通道观察 ──
    ///   ① <see cref="订阅施法事件"/>：瞬发技能（能力技）—— 主力
    ///   ② 本方法的轮询：带读条的技能 —— 兜底
    ///   两者按时间窗去重（同一次施放可能被两条通道都看到）。
    /// </summary>
    public static void 每帧更新()
    {
        if (!开启) return;

        try
        {
            var me = Core.Me;
            if (me == null) return;

            var 在读条 = me.IsCasting;

            // 只记**上升沿**（开始读条那一刻），否则一个 2.5 秒读条会被记上百次
            var 刚开始 = 在读条 && !_上次在读条;
            _上次在读条 = 在读条;

            if (!刚开始) return;

            var 技能 = me.CastActionId;
            if (技能 == 0) return;

            落一条(技能, "轮询");
        }
        catch
        {
            // 观察绝不能影响战斗 —— 任何异常都吞掉
        }
    }

    /// <summary>
    /// 每帧维护 —— **不受战斗状态限制**（由 HealQt 的每帧钩子调用）。
    ///
    /// ⚠️ 为什么订阅/退订不能放在 <see cref="每帧更新"/> 里：
    ///    那个方法只在**战斗中**被调用（OnBattleUpdate 由战斗循环驱动）。
    ///    而记录模式完全可能在**脱战**时被打开 ——
    ///    那时还没订阅，就要等进战斗才开始记录，**会漏掉开场**。
    ///    所以订阅状态必须由一个不受战斗状态限制的地方维护。
    /// </summary>
    public static void 每帧维护()
    {
        try
        {
            if (开启 && !_已订阅) 订阅施法事件();
            else if (!开启 && _已订阅) 退订施法事件();
        }
        catch { }
    }

    // ==================== 通道①：施法成功事件（能抓瞬发）====================

    /// <summary>
    /// 订阅 AEAssist 的全局"施法成功"事件。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 它能抓到瞬发能力技 —— 这正是轮询抓不到的那一块 ★
    ///
    ///    事件由 AEAssist hook 的**动作包发送函数**触发
    ///    （`MemApiSpellCastSuccess` 里的 `Hook<..> WriteActionPacketData`），
    ///    所以瞬发技能也会经过它。
    ///
    ///  ⚠️ **但它不带施法者信息**（委托签名只有 `(SpellType, spellID)`）——
    ///     也就是说**无法从事件本身判断这一发是不是你放的**。
    ///     这是个真实风险：如果它把队友的动作也报出来，
    ///     记忆库里就会混入别人的操作。
    ///
    ///  ── 我的处理：订阅 + 归属验证 ──
    ///
    ///    验证手段是 `MemApiSpell.RecastIsActive(id)`：
    ///    **冷却状态只反映本地玩家自己** —— 队友放技能不会影响你的冷却。
    ///    所以"事件说放了 X + X 的冷却确实刚转过" ⇒ 这一发是你放的。
    ///
    ///  ⚠️ 这不完美（下面 <see cref="归属判定"/> 里逐条说明了漏洞），
    ///     所以**我加了诊断日志**：每条验证结果都会记下来，
    ///     实测一场后看 `[记录模式][诊断]` 就知道到底准不准。
    ///
    ///    取舍：**宁可记进来再验证，也不要不记** ——
    ///    漏掉的能力技是**永久丢失**的信息，而误记的可以在提炼时靠
    ///    "显著不属于本职业"筛掉（或人工看诊断日志调整）。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static bool _已订阅;

    /// <summary>本职业的技能 ID 白名单（惰性构建）—— 用来过滤"明显不是我的技能"</summary>
    private static HashSet<uint>? _本职业技能;
    private static uint _白名单职业;

    /// <summary>最近一次记录（去重用）</summary>
    private static uint _最后记录技能;
    private static long _最后记录时间;

    /// <summary>同一次施放的时间窗（毫秒）—— 两条通道都看到时只记一次</summary>
    private const int 去重窗口毫秒 = 400;

    private static void 订阅施法事件()
    {
        try
        {
            var api = Core.Resolve<AEAssist.MemoryApi.MemApiSpellCastSuccess>();
            if (api == null) return;

            api.OnCastSucces += 收到施法成功;
            _已订阅 = true;

            LogHelper.Info("[记录模式] 已订阅施法成功事件（可抓瞬发能力技）");
        }
        catch (Exception e)
        {
            // 订阅失败要**明确告知** —— 否则会以为能力技在记录，其实没有
            LogHelper.Error("[记录模式] 订阅施法事件失败，瞬发能力技将无法记录：" + e.Message);
            Ai调试_失败提示();
        }
    }

    private static void 退订施法事件()
    {
        try
        {
            var api = Core.Resolve<AEAssist.MemoryApi.MemApiSpellCastSuccess>();
            if (api != null) api.OnCastSucces -= 收到施法成功;
        }
        catch { }

        _已订阅 = false;
    }

    /// <summary>事件回调 —— 只做归属判定 + 转发</summary>
    private static void 收到施法成功(AEAssist.CombatRoutine.SpellType 类型, uint 技能)
    {
        try
        {
            if (!开启 || 技能 == 0) return;

            var 判定 = 归属判定(技能);

            if (判定 == 归属.不是我的)
            {
                // 别人的技能 / 不属于本职业 → 丢掉（不记诊断，免得刷屏）
                return;
            }

            诊断(技能, 类型, 判定);

            落一条(技能, 判定 == 归属.已确认 ? "事件" : "事件?");
        }
        catch { }
    }

    private enum 归属
    {
        已确认,     // 冷却刚转过 → 基本确定是我放的
        疑似,       // 冷却没动静，但技能属于本职业 → 可能是 GCD 或验证失败
        不是我的,   // 不属于本职业 → 丢掉
    }

    /// <summary>
    /// 归属判定 —— 见 <see cref="订阅施法事件"/> 的说明。
    ///
    /// ⚠️ **已知漏洞（如实记录，别再自己骗自己）**：
    ///   ① **GCD 技能验证不了** —— GCD 共用全局冷却，不体现为单体 recast，
    ///      所以 `RecastIsActive` 对它们是 false。这类只能靠"属于本职业"放行
    ///      （判为「疑似」）。好在这类本来就被轮询通道抓到了，重复率高。
    ///   ② **队友用同职业技能时可能误判为「疑似」** ——
    ///      八人本里另一个奶妈放白魔技能，白名单挡不住。
    ///      但它判为「疑似」而不是「已确认」，**提炼时可以看到比例**。
    ///   ③ 完全瞬发且不进 recast 的特殊情况（极少数）同样落「疑似」。
    ///
    ///    所以：**「已确认」可信度高，「疑似」需要在提炼时留个心眼。**
    /// </summary>
    private static 归属 归属判定(uint 技能)
    {
        // ① 不属于本职业 → 直接丢（挡掉绝大多数队友的技能）
        if (!是本职业技能(技能)) return 归属.不是我的;

        // ② 冷却正在转 → 基本确定是我刚放的
        try
        {
            var spellApi = Core.Resolve<AEAssist.MemoryApi.MemApiSpell>();
            if (spellApi != null && spellApi.RecastIsActive(技能)) return 归属.已确认;
        }
        catch { }

        // ③ 属于本职业但验证不了（GCD / 充能技 / 极瞬发）→ 疑似
        return 归属.疑似;
    }

    /// <summary>这个技能属于当前职业吗（惰性构建白名单，切职业会重建）</summary>
    private static bool 是本职业技能(uint 技能)
    {
        try
        {
            var 职业 = (uint)(Jobs)CharacterExt.我的当前职业();
            if (_本职业技能 == null || _白名单职业 != 职业)
            {
                _本职业技能 = 收集本职业技能();
                _白名单职业 = 职业;
            }

            return _本职业技能 == null || _本职业技能.Count == 0
                   || _本职业技能.Contains(技能);
        }
        catch
        {
            // 判不了就放行 —— 宁可多记（可在提炼时筛），不要漏记
            return true;
        }
    }

    /// <summary>把技能表里的技能收成一个 ID 集合</summary>
    private static HashSet<uint> 收集本职业技能()
    {
        var 集 = new HashSet<uint>();

        try
        {
            var 表 = HealRotationEventHandler.取当前职业技能表();
            if (表 == null) return 集;

            void 加(uint id) { if (id != 0) 集.Add(id); }
            void 加数组(uint[] ids) { if (ids != null) foreach (var i in ids) 加(i); }

            加(表.基础输出); 加(表.群体输出); 加(表.Dot技能); 加(表.移动填充技);
            加(表.单体治疗GCD); 加(表.群体治疗GCD); 加(表.紧急单奶);
            加(表.群体治疗能力技); 加(表.单体盾); 加(表.群体盾);
            加(表.团队减伤); 加(表.复活); 加(表.驱散); 加(表.醒梦);
            加数组(表.输出能力技); 加数组(表.脱战准备技能); 加数组(表.所有DotBuff);
        }
        catch { }

        return 集;
    }

    // ==================== 落一条（两条通道共用）====================

    private static void 落一条(uint 技能, string 通道)
    {
        try
        {
            var 现在 = TimeHelper.Now();

            // 去重：同一次施放可能被两条通道都看到
            if (技能 == _最后记录技能 && 现在 - _最后记录时间 < 去重窗口毫秒) return;

            _最后记录技能 = 技能;
            _最后记录时间 = 现在;

            _上次技能Id = 技能;

            try { 记录一次?.Invoke(技能, SpellIds.反查(技能)); } catch { }
        }
        catch { }
    }

    // ==================== 诊断（一次性，看完就能删）====================

    private static readonly Dictionary<string, int> _诊断计数 = new();
    private static long _上次诊断汇总;

    /// <summary>
    /// 记录"事件通道看到了什么、验证结果如何"。
    ///
    /// **目的**：`OnCastSucces` 是否只报玩家、`RecastIsActive` 验证准不准，
    /// 这两件事静态分析**无法确定**，必须实测。
    /// 所以每条都记，但**只在日志里**（不上屏），并按 30 秒汇总一次数量。
    ///
    /// 实测一场后看 `[记录模式][诊断]` 与 `[记录模式][诊断汇总]`：
    ///   · 若「已确认」占绝大多数 → 验证有效，能力技记录可信
    ///   · 若大量「疑似」且明显是队友的技能 → 需要收紧白名单
    ///   · 若事件**完全没触发**（连汇总都没有）→ 说明订阅没生效
    /// </summary>
    private static void 诊断(uint 技能, AEAssist.CombatRoutine.SpellType 类型, 归属 判定)
    {
        try
        {
            var 标签 = 判定 == 归属.已确认 ? "已确认" : "疑似";
            var 名 = SpellIds.反查(技能);

            LogHelper.Info($"[记录模式][诊断] 技能={技能} {名} 类型={类型} 归属={标签}");

            _诊断计数[标签] = _诊断计数.TryGetValue(标签, out var c) ? c + 1 : 1;

            var 现在 = TimeHelper.Now();
            if (_上次诊断汇总 == 0) _上次诊断汇总 = 现在;
            if (现在 - _上次诊断汇总 < 30_000) return;

            _上次诊断汇总 = 现在;
            var 概要 = string.Join("、", _诊断计数.Select(kv => $"{kv.Key}={kv.Value}"));
            LogHelper.Info($"[记录模式][诊断汇总] {概要}（近 30 秒）");
        }
        catch { }
    }

    /// <summary>订阅失败时的屏幕提示（走 HealerACR 自己的提示通道）</summary>
    private static void Ai调试_失败提示()
    {
        try
        {
            屏幕提示.警告("记录模式：施法事件订阅失败，瞬发能力技可能记录不到（详见日志）", "rec-sub-fail");
        }
        catch { }
    }

    /// <summary>战斗重置 / 换本 / 切职业时清（避免跨场次串味）</summary>
    public static void 重置()
    {
        _上次技能Id = 0;
        _上次在读条 = false;
        _最后记录技能 = 0;
        _最后记录时间 = 0;
        _本职业技能 = null;    // 换职业要重建白名单
        _白名单职业 = 0;
    }

    /// <summary>
    /// ACR 卸载时调用 —— **必须退订事件**。
    ///
    /// ⚠️ 不退订的后果：事件持有我们的回调，
    ///    即使 ACR 被换掉/停用，只要游戏还开着，回调还会被触发；
    ///    而那时 记录一次 钩子已经被摘掉，等于白跑 + 潜在空引用。
    /// </summary>
    public static void 卸载()
    {
        退订施法事件();
        _诊断计数.Clear();
        _本职业技能 = null;
    }
}
