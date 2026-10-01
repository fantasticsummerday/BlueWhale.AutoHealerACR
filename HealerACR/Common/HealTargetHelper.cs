using System.Linq;
using System.Numerics;
using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// 奶妈最核心的一件事：决定"这个技能给谁"。
/// 所有目标筛选都集中在这里，resolver 只管调用。
/// </summary>
public static class HealTargetHelper
{
    /// <summary>
    /// 按血量比例升序的、可以被治疗的队友。
    ///
    /// ⚠️ 默认半径 30 米（单体治疗够用），但**群疗判断必须传技能真实半径** ——
    ///    见 <see cref="低于阈值人数(float, float)"/> 的说明。
    /// </summary>
    public static List<IBattleChara> 可治疗队友(float 半径 = 30f)
    {
        // ══════════════════════════════════════════════════════════════
        //  ★ 这是**所有治疗目标的唯一入口**，所以韧性放在这里 ★
        //
        //  [!] 崩溃签名（Windows 事件 1026，进程 ffxiv_dx11.exe 被杀）：
        //        Dalamud...BattleChara.get_StatusList()
        //        AEAssist...MemApiBuff.HasAnyAura(...)
        //        HealerACR...HealTargetHelper.<可治疗队友>b__0_1   <- 就是下面那个 lambda
        //
        //  [!] **为什么原来那个 `catch` 不够** ——
        //      这类崩溃是**访问违例**（`0xc0000005`）级别，会**穿透 C# 的 catch**。
        //      ==> 不能指望"外面包了 try 就安全"。
        //      ==> 真正有效的是**两步都做**：
        //           ① 进 lambda 之前先筛掉无效对象（下面 `对象有效()`）
        //           ② 触到游戏对象的每一步各自包 try（Linq 的延迟求值会把异常
        //              拖到 `ToList()` 那一刻，报错位置和真实位置不一致）
        //
        //  [!] 拿不到就返回**空列表**，方向很重要：
        //      空列表 => 治疗以为"没人需要治" => **什么都不做**，
        //      而不是去读一个可能失效的对象。宁可少治一次，不要崩游戏。
        // ══════════════════════════════════════════════════════════════
        try
        {
            // ══════════════════════════════════════════════════════════════
            //  ★ 第一道：**先整体确认这个队伍可不可信，不可信就直接空手退出 ★**
            //
            //  [!] 这是第 3 次闪退之后加的。前两次失败的原因现在清楚了：
            //      转储里 `异常信息 #0 地址: **12345679**` ——
            //      那是 Dalamud 的"已释放对象"哨兵地址，
            //      而当时的 `对象有效()` 只检查 `Address != 0`
            //      ==> **哨兵地址通过了检查** ==> 随后读 `StatusList` 踩空 ==> 进程被杀。
            //      （`对象有效()` 本身已修好，这里是**第二道**。）
            //
            //  [!] 为什么还要这道：`对象有效()` 修好之后，
            //      "检查通过"到"真正读 buff"之间仍有极短的窗口。
            //      而 `PartyHelper.CastableAlliesWithin30` 在换区时会**整批**
            //      变成失效对象 —— 只要发现**任何一个**失效，
            //      就说明这一帧整个队伍不可信，**这一次调用直接放弃**。
            //
            //  [!] 代价是"换区那一两帧治不了人" —— 本来也治不了（人都不在场景里）。
            // ══════════════════════════════════════════════════════════════
            try
            {
                var 待检 = PartyHelper.CastableAlliesWithin30;
                if (待检 == null) return new List<IBattleChara>();
                foreach (var r in 待检)
                    if (r == null || !r.对象有效()) return new List<IBattleChara>();
            }
            catch { return new List<IBattleChara>(); }

            var 我 = Core.Me.Position;
            var 源 = 半径 >= 30f
                ? PartyHelper.CastableAlliesWithin30
                : PartyHelper.CastableAlliesWithin30.Where(r =>
                    {
                        try { return r.对象有效() && Vector3.Distance(我, r.Position) <= 半径; }
                        catch { return false; }
                    });

            // [!] `可以治()` 现在自带 try（见 `CharacterExt.可以治`），
            //     这里的 `对象有效()` 是**第二道**：先便宜地筛一遍，
            //     避免对明显失效的对象再进 `可以治()`。
            return 源
                .Where(r => r.对象有效() && r.可以治())
                .OrderBy(r =>
                {
                    try { return r.有效血量比例(); }
                    catch { return 999f; }   // 读不到排最后（不参与优先治疗）
                })
                .ToList();
        }
        catch
        {
            return new List<IBattleChara>();
        }
    }

    /// <summary>血量最低、且低于阈值的队友；没有就是 null</summary>
    public static IBattleChara? 最低血量队友(float 阈值, float 半径 = 30f)
    {
        return 可治疗队友(半径).FirstOrDefault(r => r.有效血量比例() <= 阈值);
    }

    /// <summary>
    /// 血量低于阈值的人数（判断值不值得群奶）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 半径参数是**必须的** —— 不传的后果是"离小怪很远还在 AOE" ★
    ///
    ///  参考实现的计数函数是 `CountLowHpCastableAlliesInRange(阈值, 半径)`，
    ///  调用时**按技能的真实半径传**（IL 直证）：
    ///      白魔 医济/医治/愈疗  → **10 米**
    ///      白魔 庇护所          → **20 米**
    ///      学者 不屈/群盾/低语  → **20 米**
    ///      占星 阳星/阳星合相   → **20 米**
    ///  而且它内部 `if (range < 30f) 才查距离` —— 所以传 10/20 是**真的在筛**。
    ///
    ///  ⚠️ 我们原来写死用 `CastableAlliesWithin30`（一律 30 米）：
    ///      结果 **30 米外有人掉血也算"够人数"** → 放群疗，
    ///      而群疗实际只覆盖 10~20 米 → **那个人根本治不到**，
    ///      还白占一个 GCD。这就是"离很远还在 AOE"的根因。
    ///
    ///  ⚠️ 默认值给 30 是为了兼容旧调用；**群疗路径一律要显式传技能半径**。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static int 低于阈值人数(float 阈值, float 半径 = 30f)
    {
        return 可治疗队友(半径).Count(r => r.有效血量比例() <= 阈值);
    }

    /// <summary>队伍里血量最低的人（不看阈值，给大加用）</summary>
    public static IBattleChara? 最危险队友()
    {
        return 可治疗队友().FirstOrDefault();
    }

    /// <summary>
    /// **队伍里的坦克**（不设血量门槛）—— 只要求活着、是坦克。
    ///
    /// ══════════════════════════════════════════════════════════════
    ///  [!] 为什么必须和 `血量最低的坦克` 分开（用户实测报的问题）
    ///
    ///      原来想知道队伍里的坦克是谁的调用点（给 AI 报告 / 采样 / 个人减伤）
    ///      也走 `血量最低的坦克()`，于是吃到了它的默认血量门槛 `1f`。
    ///
    ///      而 `有效血量比例() = 血量比例() + 盾百分比` —— **可以 > 1.0**。
    ///      坦克**满血 + 任何盾**（鼓舞 / 均衡诊断 / 神祝祷 / 天星交错 /
    ///      至黑之夜 / 圣盾阵…）时 `有效血量比例 > 1.0`
    ///      ==> `<= 1f` 为 false ==> **被判成没有坦克**。
    ///
    ///  [!] 现象正是：
    ///      · 「老二打的时候有一些时间段调试窗显示没有识别到 T」
    ///        —— 坦克只在**没盾且不满血**的间隙里存在。
    ///      · 更糟：`坦克压力.每帧更新` 见 T == null 会**清空采样**
    ///        ==> 波动 / 无敌 / 减伤全被重置，数据断断续续。
    ///
    ///  [!] 和 `血量最低的坦克` 的分工：
    ///      · 本方法             -> 「T 是谁」（报告 / 采样 / 个人减伤）
    ///      · `血量最低的坦克(t)` -> 「哪个 T 缺血到该照顾了」（预铺盾 / 心关）
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    // ══════════════════════════════════════════════════════════════
    //  ★★ 主坦（MT）—— 八人本要和副坦（ST）分开 ★★
    //
    //  [!] 用户实测要求（原话）：
    //      「t应该看的是**主仇恨的 mt**，八人本直接就区分 mtst」
    //
    //  [!] 为什么必须要它（不是锦上添花）：
    //      `血量最低的坦克()` 是**跟着谁掉血跑**的。
    //      而有一批技能**必须钉在 MT 身上**，不能跟着掉血跑：
    //        · 贤者**心关**     —— 打敌人顺带治 MT，钉错人整场少一大块治疗
    //        · 学者**妖精契约** —— 持续治疗 MT
    //        · 白魔**神祝祷** / 贤者**混合** —— 单减 + 增疗给 MT
    //        · 战士/骑士这类「对搭档生效」的机制也要求稳定指向 MT
    //      ==> 八人本里 ST 掉血时，这些全会跑到 ST 身上。
    //
    //  [!] 判据是**行为证据，不是猜测**：
    //      Dalamud **不暴露仇恨值**（`输出目标.cs` 注释已写明这条路封死），
    //      但**「怪在看谁」可读** —— `敌人.TargetObjectId`。
    //      ==> **被最多敌人盯着的那个 T = MT**。
    //      副坦没接怪时不会有怪盯着他，而主坦会一直有。
    //
    //  [!] 为什么要**粘滞窗口**：
    //      拉怪分离 / 换 T / 小怪死掉时，计数会抖一下。
    //      不粘滞的话心关会在两个 T 之间来回跳（心关换人要重新贴，很亏）。
    //
    //  [!] 退回顺序（不返回 null，避免调用方突然失去目标）：
    //      ① 怪最多且 > 0 的那个 T
    //      ② **队伍顺序的第一个 T**（八人本里游戏本身就把他排在前面）
    //      ③ 有效血量最低的 T（保持旧行为）
    // ══════════════════════════════════════════════════════════════
    /// <summary>粘滞缓存：上次认定的 MT</summary>
    private static ulong _主坦Id;
    private static long _主坦时刻;
    
    /// <summary>认定了多久之内不再换（防止拉怪/换 T 时抖）</summary>
    private const long 主坦粘滞毫秒 = 1500;
    
    /// <summary>
    /// **主坦（MT）** —— 需要钉在一个固定的人身上的技能用它，
    /// 而不是用跟着掉血跑的 `血量最低的坦克()`。
    /// </summary>
    public static IBattleChara? 主坦()
    {
        try
        {
            var 坦克们 = PartyHelper.CastableTanks?.Where(r => r != null && r.活着()).ToList();
            if (坦克们 == null || 坦克们.Count == 0) return null;
            if (坦克们.Count == 1) return 坦克们[0];      // 四人本：不用挑
    
            var 现在 = TimeHelper.Now();
    
            // ① 粘滞期内直接用上次认定的（前提：他还活着）
            if (_主坦Id != 0 && 现在 - _主坦时刻 < 主坦粘滞毫秒)
            {
                var 还 = 坦克们.FirstOrDefault(t => t.GameObjectId == _主坦Id);
                if (还 != null) return 还;
            }
    
            // ② 数有几个怪在盯着他
            var 计数 = new Dictionary<ulong, int>();
            try
            {
                foreach (var 敌 in Data.AllHostileTargets)
                {
                    if (敌 == null || 敌.CurrentHp <= 0) continue;
                    var 盯 = 0ul;
                    try { 盯 = 敌.TargetObjectId; } catch { }
                    if (盯 == 0)
                    {
                        try { 盯 = 敌.CastTargetObjectId; } catch { }
                    }
                    if (盯 == 0) continue;
                    foreach (var t in 坦克们)
                    {
                        if (t.GameObjectId != 盯) continue;
                        计数[盯] = 计数.TryGetValue(盯, out var n) ? n + 1 : 1;
                    }
                }
            }
            catch { }
    
            // ③ 怪最多的那个；同数时取队伍顺序靠前的（游戏本身把 MT 排前面）
            var 最好 = 坦克们[0];
            var 最好数 = 计数.TryGetValue(最好.GameObjectId, out var n0) ? n0 : 0;
            for (var k = 1; k < 坦克们.Count; k++)
            {
                var t = 坦克们[k];
                var c = 计数.TryGetValue(t.GameObjectId, out var n) ? n : 0;
                if (c > 最好数) { 最好 = t; 最好数 = c; }
            }
    
            // ④ 一个怪都没盯着任何一个 T（还没开怪 / 全在看别人）——
            //    退回队伍顺序的第一个 T，**不要**退回血最少的（那会跟着掉血跑）
            if (最好数 == 0) 最好 = 坦克们[0];
    
            _主坦Id = 最好.GameObjectId;
            _主坦时刻 = 现在;
            return 最好;
        }
        catch
        {
            // 彻底失败时退回旧行为，**不要返回 null**（调用方会失去目标）
            try { return 队伍里的坦克(); } catch { return null; }
        }
    }
    
    /// <summary>重置主坦粘滞（换本 / 战斗重置用）</summary>
    public static void 清主坦记录()
    {
        _主坦Id = 0;
        _主坦时刻 = 0;
    }
    
    public static IBattleChara? 队伍里的坦克()
    {
        try
        {
            return PartyHelper.CastableTanks
                .Where(r => r.活着())
                .OrderBy(r => r.有效血量比例())
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>血量最低的坦克（挂心关 / 预铺盾用）</summary>
    public static IBattleChara? 血量最低的坦克(float 阈值 = 1f)
    {
        return PartyHelper.CastableTanks
            .Where(r => r.活着() && r.有效血量比例() <= 阈值)
            .OrderBy(r => r.有效血量比例())
            .FirstOrDefault();
    }

    /// <summary>需要驱散的队友（麻痹 / 中毒 / 减速…）</summary>
    public static IBattleChara? 需要驱散队友()
    {
        return PartyHelper.CastableAlliesWithin30
            .FirstOrDefault(r => r.活着() && r.HasCanDispel());
    }

    // ==================== 队伍规模 ====================

    private static bool? _是八人本缓存;
    private static uint _缓存地图;

    /// <summary>
    /// 是不是八人本（队伍里有 2 个治疗）。
    ///
    /// **进本时算一次、缓存起来**；地图变了自动重算，所以换本会重新判定。
    ///
    /// 用途：**复活策略分两种**
    ///   · 四人本（单奶）→ 只有我能拉，无条件自己上
    ///   · 八人本（双奶）→ 才有"谁负责"的余地
    /// </summary>
    public static bool 是八人本()
    {
        try
        {
            var 地图 = HealerACR.Timeline.TimelineManager.当前地图Id;
            if (_是八人本缓存 == null || 地图 != _缓存地图)
            {
                _缓存地图 = 地图;
                _是八人本缓存 = 算队伍规模();
            }

            return _是八人本缓存.Value;
        }
        catch
        {
            return false;   // 判断不了就当四人本（自己拉，更安全）
        }
    }

    private static bool 算队伍规模()
    {
        try
        {
            // 八人本必然有 2 个治疗
            var 奶妈们 = PartyHelper.CastableHealers;
            if (奶妈们 != null && 奶妈们.Count >= 2) return true;

            // 兜底：直接数队伍人数
            var 队友 = PartyHelper.CastableAlliesWithin30;
            return 队友 != null && 队友.Count > 5;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>手动刷新队伍规模缓存（进本 / 换本时调）</summary>
    public static void 刷新队伍规模() => _是八人本缓存 = null;

    // ==================== 按队伍规模的策略调整 ====================

    /// <summary>
    /// **群疗能力技 / 群 HoT 的触发人数门槛** —— 直接取设置项本身，只夹范围，**不打折**。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  [!] 为什么要单独一个函数（而不是各 resolver 各写一遍）
    ///
    ///      这个表达式原来在**四个地方**各写了一遍：
    ///        `Res_AllExtra.cs` / `Res_Extra.cs`（两处）/ `Res_Heal.cs` / `ScholarACR.cs`
    ///      而其中一处先改、其余没改 ==>
    ///      **同一个设置项在不同技能上算出不同的人数**，
    ///      而且**改的时候只改一处**（Round 6 改了能力技群奶，
    ///      剩下 6 处一直留着 `- 1`，Round 10 才发现）。
    ///
    ///      ==> 这正是项目开发约定 F③ 要防的事：
    ///          「同一个决策不论从哪条路进来，判断必须一致」。
    ///          办法就是**只留一份实现**，所有调用方都走这里。
    ///
    ///  [!] 为什么是「设置项本身」而不是「设置项 - 1」
    ///      参考实现（两套一致，IL 实证）：
    ///        shiyuvi `count >= AOEHealCount`（默认 2）
    ///        youshu  `count >= Clamp(群奶人数, 0, 8)`（默认 2）
    ///      ==> 都是设置项本身。原来的 `- 1` 让默认值 2 变成"只要 1 人"，
    ///          一个人掉血就交掉一个群疗 CD —— 明显在浪费。
    ///
    ///  [!] `Clamp(_, 1, 8)` 的两端：
    ///        下界 1：用户把设置调到 0 时不该变成「永远不交」
    ///        上界 8：队伍规模（八人本最多也就 8 个人）
    ///
    ///  ⚠️ **注意与 `群奶人数要求()` 的区别**（那个是给 **GCD 群奶**用的）：
    ///      `群奶人数要求()` 会**按队伍规模调整**（四人本 `max(2,基础-1)`、
    ///      八人本 `max(基础,3)`），因为 GCD 群奶更贵。
    ///      两个函数**有意不同**，名字也刻意取得不一样，别混用。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static int 群疗能力技人数要求(int 基础人数)
    {
        try { return Math.Clamp(基础人数, 1, 8); }
        catch { return Math.Max(1, 基础人数); }
    }

    /// <summary>
    /// 群体治疗的人数门槛。
    ///
    /// **为什么两个规模不一样**：
    ///   · 四人本（单奶）：队伍只有 4 人，**2 人掉血就已经是大事**，
    ///     而且没人帮你补 → 门槛要低、反应要快。
    ///   · 八人本（双奶）：8 个人里 2 人掉血很常见，
    ///     为这个交群奶会浪费 → 门槛要高一点。
    ///
    /// [!] **八人本门槛从 4 降到 3**（用户拍板）——
    ///     原来的 `max(基础, 4)` 有个明确缺口：
    ///     **3 人同时掉到 50%（37.5% 的队伍）时不放任何 GCD 群奶**
    ///     （3 &lt; 4 ⇒ `Res_HealAoEGcd` 不触发，只剩门槛 1 人的
    ///     AoE 能力技会响应）。
    ///     而 3/8 人同时掉一半，在多数副本里是该交群奶的
    ///     —— 尤其带 HoT 的 医养 / 阳星合相 / 士气高扬之策。
    ///
    /// [!] 降到 3 之后：2 人仍然不交（保留原来的"别浪费"意图），
    ///     3 人开始交。
    ///
    /// ⚠️ **这是给 GCD 群奶用的**；群疗**能力技**走
    ///    `群疗能力技人数要求()`（不打折 —— 能力技更便宜）。
    /// </summary>
    public static int 群奶人数要求(int 基础人数)
    {
        try
        {
            return 是八人本()
                ? Math.Max(基础人数, 3)          // 八人本：至少 3 人（原为 4，用户拍板降低）
                : Math.Max(2, 基础人数 - 1);     // 四人本：2 人就行
        }
        catch
        {
            return 基础人数;
        }
    }

    /// <summary>
    /// 资源保留数的调整（以太 / 蛇胆这类共用资源）。
    ///
    /// **四人本要留更多** —— 单奶没有第二个人兜底，
    /// 豆子打光了真出事就只能干看着。
    /// 八人本有搭档，可以少留一点多打输出。
    /// </summary>
    public static int 资源保留调整(int 基础保留)
    {
        try
        {
            return 是八人本()
                ? Math.Clamp(基础保留 - 1, 0, 3)   // 八人本：少留一颗
                : Math.Clamp(基础保留 + 1, 0, 3);  // 四人本：多留一颗
        }
        catch
        {
            return 基础保留;
        }
    }

    /// <summary>
    /// 复活时该不该由我负责。
    ///
    /// ⚠️ **修正（现象：90 级本 T 死了没人复活，而且即刻是好的）**
    ///
    ///    原来这里实现的是"两个奶妈时，只让对象 ID 最小的那个拉人"，
    ///    本意是避免重复。但实际会死锁：
    ///      · 如果对方奶妈是别的 ACR（比如其他 ACR），它很可能也有自己的协调逻辑
    ///      · 两边互相谦让 → **两个都不拉**
    ///      · 更糟的是这个判断在"有没有即刻"之前就 return 了，
    ///        所以表现成"明明即刻是好的，就是不拉人"
    ///
    ///    权衡：日随里"两个奶妈同时拉同一个人"只是浪费一个即刻，
    ///          而"没人拉"的代价是灭团。**宁可重复，不能没人。**
    ///
    ///    防重复交给 复活等待 buff（已经有人挂了复活就不重复拉）。
    /// </summary>
    public static bool 该我复活()
    {
        // 四人本（单奶）：只有我能拉，无条件自己上。
        // 八人本（双奶）：也不搞单方面谦让 —— 对方如果是别的 ACR，
        //   它不知道我在等它，结果两个都不拉（这就是 T 死了没人复活的根因）。
        //   防重复交给「复活等待」buff，那个机制本来就够用。
        return true;
    }

    /// <summary>躺在地上、还没被挂复活 buff 的队友（含多奶妈协调）</summary>
    public static IBattleChara? 待复活队友()
    {
        // 多奶妈协调：不是"我负责"就返回空，让另一个奶妈去拉
        if (!该我复活()) return null;

        // ── 优先顺序：奶妈 > 坦克 > 其他人 ──
        //    参考同类 ACR 的 ShouldPrioritizeHealerResurrect：
        //    奶妈躺了 → 全队治疗断档 → 最容易连锁崩盘，所以优先救。
        //
        // ⚠️ 过滤两类，**含义完全不同，别搞混**：
        //    · 复活等待(148)  —— 别人已经在拉了 → 防"两个奶妈抢同一个尸体"
        //    · 限制复活(5 个) —— 这个尸体**根本拉不起来** → 防"白交即刻和 GCD"
        //    对照分析之前只做了前者，后者漏了。
        var 躺着的 = PartyHelper.DeadAllies
            .Where(r => r != null && !r.被禁止复活())
            .ToList();

        if (躺着的.Count == 0) return null;

        // 职业判断统一走 职业表（ID 不再散落在各处）
        var 奶妈 = 躺着的.FirstOrDefault(r => 职业表.是奶妈(r));
        if (奶妈 != null) return 奶妈;

        var 坦克 = 躺着的.FirstOrDefault(r => 职业表.是坦克(r));
        return 坦克 ?? 躺着的[0];
    }

    /// <summary>
    /// 周围敌人数量（AOE 判断用）。
    ///
    /// ⚠️ 必须**以当前目标为中心**数，不能以自己为中心 ——
    /// 奶妈站远程位，以自己为中心 5 米内经常一个敌人都没有，
    /// 结果就是 AOE 永远不触发。
    /// </summary>
    /// <param name="伤害范围">技能命中后的伤害范围（米）</param>
    /// <param name="施法范围">技能施法距离（米）</param>
    public static int 周围敌人数量(float 伤害范围 = 5f, float 施法范围 = 25f)
    {
        // 对照官方文档的 CheckNeedUseAOE：施法范围和伤害范围要**分别传**。
        // 之前我把两个都写死成 5 米，等于把"能不能打到"和"能打几个"混为一谈了。
        var t = 当前目标();
        if (t != null) return TargetHelper.GetNearbyEnemyCount(t, (int)施法范围, (int)伤害范围);
        return TargetHelper.GetNearbyEnemyCount((int)伤害范围);
    }

    /// <summary>
    /// **以玩家自己为中心**数敌人（用来判断"对自身周围"的 AOE 能打到几个）。
    ///
    /// ⚠️ 和 <see cref="周围敌人数量"/> 的关键区别：
    ///    那个是**以当前目标为中心**数（奶妈站远程位时要靠它才数得到），
    ///    这个**以自己为中心** —— 因为「破阵法 / 裂阵法」是
    ///    `CastType=2 射程=0 效果范围=5` 的**自身圆形 AOE**，
    ///    判定就是"我周围 5 米内有几个敌人"。
    ///
    /// ⚠️ 用法上必须按技能的 `EffectRange` 传半径 ——
    ///    传错半径会得出错的命中数，进而选错技能。
    ///
    /// ⚠️ 用 `敌人.HitboxRadius` 放宽：Boss 的目标圈有大有小，
    ///    严格按中心距会在"贴着大 Boss"时少数。
    /// </summary>
    public static int 自身周围敌人数量(float 半径 = 5f)
    {
        var 数 = 0;
        try
        {
            var 我 = Core.Me.Position;

            foreach (var 敌人 in Data.AllHostileTargets)
            {
                if (敌人 == null) continue;
                if (敌人.CurrentHp <= 0) continue;

                try
                {
                    if (!敌人.IsTargetable) continue;
                    // 正在死的不算（0.1% 血线）
                    if (敌人.MaxHp > 0 &&
                        敌人.CurrentHp / (float)敌人.MaxHp <= 0.001f) continue;

                    var 距 = Vector3.Distance(我, 敌人.Position) - 敌人.HitboxRadius;
                    if (距 > 半径) continue;
                }
                catch { continue; }

                数++;
            }
        }
        catch { }

        return 数;
    }
    
    /// <summary>
    /// **诊断版**：返回命中数 + 每个被数进来的敌人的明细。
    ///
    /// [!] 为什么需要它：用户报「50 级究极神兵（单体）还在打破阵法」，
    ///     已确认是**本地**选的（日志有 `CastSpell success: 16539`，
    ///     而 AI 的采纳数是 0），且本地规则看起来是对的
    ///     （字典序：威力优先、5% 内同级、同级比蓝耗）。
    ///     ==> 嫌疑落在 `自身周围敌人数量(5)` 数出了 >1。
    ///     **不该靠怀疑改判据** —— 先让它把实际数字打出来。
    ///
    /// [!] `- HitboxRadius` 是重点怀疑对象：究极神兵体型巨大，
    ///     减掉一个很大的 `HitboxRadius` 之后，远处的敌人也会落进半径。
    /// </summary>
    public static string 自身周围敌人明细(float 半径 = 5f)
    {
        var sb = new System.Text.StringBuilder();
        var 数 = 0;
        try
        {
            var 我 = Core.Me.Position;
            sb.Append($"半径{半径:F1} 我({我.X:F1},{我.Z:F1}) ");
            foreach (var 敌人 in Data.AllHostileTargets)
            {
                if (敌人 == null) continue;
                var 名 = "?";
                try { 名 = 敌人.Name.ToString(); } catch { }
                if (敌人.CurrentHp <= 0) { sb.Append($"[{名}:死] "); continue; }
                try
                {
                    if (!敌人.IsTargetable) { sb.Append($"[{名}:不可选] "); continue; }
                    if (敌人.MaxHp > 0 && 敌人.CurrentHp / (float)敌人.MaxHp <= 0.001f)
                    { sb.Append($"[{名}:濒死] "); continue; }
                    var 直 = Vector3.Distance(我, 敌人.Position);
                    var 距 = 直 - 敌人.HitboxRadius;
                    sb.Append($"[{名} 直{直:F1}-圈{敌人.HitboxRadius:F1}={距:F1}] ");
                    if (距 <= 半径) 数++;
                }
                catch { sb.Append($"[{名}:读失败] "); }
            }
            sb.Append($"=> 命中{数}");
        }
        catch (Exception e) { sb.Append("异常:" + e.Message); }
        return sb.ToString();
    }

    /// <summary>当前选中的敌人（挂 DoT / 打输出用）</summary>
    public static IBattleChara? 当前目标()
    {
        var t = Core.Me.GetCurrTarget();
        if (t == null) return null;
        return t.CurrentHp > 0 ? t : null;
    }

    /// <summary>
    /// **这个敌人被"拉到了"吗**（= 已经进战，在打我们队伍里的某个人）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么要做它 ★
    ///
    ///    现象：
    ///      "出现了自动给**没有仇恨**的目标上毒的问题"
    ///
    ///  ── 根因 ──
    ///    `其他敌人()` 用的是 `Data.AllHostileTargets` ——
    ///    那是**场景里所有敌对目标**，不区分"有没有进战"。
    ///    所以 25 米内**还没被 T 拉到的怪**、旁边不相干的怪，
    ///    全都会出现在 AI 的敌人列表里 → AI 自然会建议给它们补 DoT。
    ///
    ///  ── 判据为什么这么写 ──
    ///    Dalamud **没有公开的 enmity（仇恨值）接口** ——
    ///    我反射查过整个 `IBattleChara` / `ICharacter` / `IGameObject` 链，
    ///    没有 `Enmity` / `Aggro` 之类的属性（`BattleChara` 结构体里有
    ///    原生字段，但 Dalamud 没暴露）。所以只能用**间接信号**：
    ///
    ///      ① **它在看着我们队里的人** → 一定是被拉到了
    ///         （`IGameObject.TargetObjectId`，最可靠的信号）
    ///      ② **它在对我们队里的人读条** → 同上
    ///         （`IBattleChara.CastTargetObjectId`，抓"正在起手"的怪）
    ///
    ///  ⚠️ **失败方向是"当作没拉到"**（保守）——
    ///     宁可少给一个怪上 DoT，也不要给没拉的怪上毒把人引过来。
    ///     这和你报的问题方向一致：误判成"拉了"的代价（ADD）远大于
    ///     误判成"没拉"的代价（少打一个 DoT）。
    ///
    ///  ⚠️ 已知取舍：**怪已经在打我但我方列表读不到**时会判成没拉到。
    ///     这种情况会在"目标是自己"时发生 —— 所以下面额外放行"目标是我自己"。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    // ══════════════════════════════════════════════════════════════
    //  ★ 稳定仇恨：怪「盯住同一个我方成员」够久才算真拉到 ★
    //
    //  [!] 为什么需要（用户实测报的问题）：
    //      「小怪阶段如果 T **脸接怪**，也会判定为拉到仇恨 ——
    //        要确保 T 建立了稳定仇恨再打」。
    //
    //  [!] 根因：`有仇恨()` 原来的判据是「怪的目标是我方某人」——
    //      T 脸接的瞬间，怪的目标**立刻**就是 T，
    //      但 T 还没有仇恨（没打仇恨技）==> 我们一输出就抢走。
    //
    //  [!] 为什么用「盯住同一个人够久」而不是「盯住坦克」：
    //      · 坦克不一定是 `血量最低的坦克`（副坦也可能在拉）
    //      · 8 人本两个 T 都可能各拉一批
    //      · 而**稳定仇恨的特征**是：怪不再挑目标了 ——
    //        T 打上仇恨技后它会一直盯住 T；脸接时它随时会切。
    //
    //  [!] 只影响**首次接怪**的判定 —— 一旦盯够时间就一直是 true，
    //      正常战斗中不会因为这条而停手。
    // ══════════════════════════════════════════════════════════════
    
    /// <summary>敌人 id -> (上次看到它盯的目标, 那个目标从什么时候开始被盯)</summary>
    private static readonly Dictionary<ulong, (ulong 目标, long 起始)> _盯人记录 = new();
    
    /// <summary>
    /// **要求怪盯住同一个我方成员多久**（毫秒）才算稳定仇恨。
    ///
    /// [!] 取值权衡：
    ///      · 太短 -> 脸接的瞬间就放行（等于没修）
    ///      · 太长 -> 坦正常开怪后我们迟迟不肯输出（白站 1~2 秒）
    ///      1200ms ≈ 坦克一到两个 GCD（仇恨技 + 一个 GCD），够建立仇恨又不至于发呆。
    /// </summary>
    private const int 稳仇毫秒 = 1200;
    
    /// <summary>
    /// **它是不是已经被稳定拉住了** —— 在 `有仇恨` 之上再加一层时间判据。
    ///
    /// [!] 和 `有仇恨` 的关系：
    ///      `有仇恨`   = 此刻它在看我方某人（**瞬时**信号）
    ///      本方法      = 它**持续**看着我方同一个人（**稳定**信号）
    ///      输出/Dot 该用本方法；而它是否在攻击我们这类判断仍可用 `有仇恨`。
    /// </summary>
    public static bool 稳定仇恨(IBattleChara 敌人)
    {
        try
        {
            if (敌人 == null) return false;
            if (敌人.CurrentHp <= 0) return false;
    
            // 先过瞬时判据（它在看我方吗）—— 不满足就根本谈不上稳定
            if (!有仇恨(敌人)) return false;
    
            // 取它现在盯的人（读条目标优先，和 `有仇恨` 同源）
            var 盯的人 = 0ul;
            try { 盯的人 = 敌人.TargetObjectId; } catch { }
            if (盯的人 == 0)
            {
                try { 盯的人 = 敌人.CastTargetObjectId; } catch { }
            }
            // [!] 盯着我自己的情况直接放行 —— 它已经在打我了，
            //     这时候再等稳定没有意义（我们已经被打了）。
            try
            {
                if (Core.Me != null && 盯的人 == Core.Me.GameObjectId) return true;
            }
            catch { }
            if (盯的人 == 0) return false;
    
            var 现在 = TimeHelper.Now();
            var id = 敌人.GameObjectId;
    
            if (!_盯人记录.TryGetValue(id, out var 记))
            {
                _盯人记录[id] = (盯的人, 现在);
                return false;      // [!] 数据不足一律 false（宁可多等）
            }
    
            if (记.目标 != 盯的人)
            {
                // 换目标了 -> 重新计时（这正是还没稳定的特征）
                _盯人记录[id] = (盯的人, 现在);
                return false;
            }
    
            return 现在 - 记.起始 >= 稳仇毫秒;
        }
        catch
        {
            // [!] 判不了就**放行** —— 和 `有仇恨` 的保守方向相反：
            //     这里出错多半是读不到数据，卡住输出比偶尔抢一次仇恨更糟。
            return true;
        }
    }
    
    /// <summary>清掉盯人记录（换本 / 换目标时用）。</summary>
    public static void 清盯人记录() => _盯人记录.Clear();
    
    public static bool 有仇恨(IBattleChara 敌人)
    {
        if (敌人 == null) return false;

        try
        {
            if (敌人.CurrentHp <= 0) return false;

            // 我方所有可能的"被攻击对象"：队伍成员 + 我自己 + 陆行鸟/召唤物
            var 我方 = new HashSet<ulong>();
            try
            {
                foreach (var m in PartyHelper.CastableParty)
                    if (m != null) 我方.Add(m.GameObjectId);
            }
            catch { }

            try
            {
                if (Core.Me != null) 我方.Add(Core.Me.GameObjectId);
            }
            catch { }

            if (我方.Count == 0) return false;

            // ① 它在看着谁
            try
            {
                var 目标Id = 敌人.TargetObjectId;
                if (目标Id != 0 && 我方.Contains(目标Id)) return true;
            }
            catch { }

            // ② 它在对谁读条
            try
            {
                var 读条目标 = 敌人.CastTargetObjectId;
                if (读条目标 != 0 && 我方.Contains(读条目标)) return true;
            }
            catch { }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// **被拉到了、但身上没有我的 DoT** 的敌人（按"值得补"排序）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 本地层的缺口 ★
    ///
    ///    "在小怪阶段没有别的技能可打的情况下
    ///     没有给 T 拉到仇恨的其他小怪上 dot"
    ///
    ///  ── 我们的现状 ──
    ///    `Res_Dot` 只认**当前选中**那一个目标。当前目标身上 DoT 还在时，
    ///    它就不补了 —— **哪怕旁边还有 3 个被拉到的怪一个 DoT 都没有**。
    ///    多目标 DoT 是两家参考实现都有的能力，我们一直没有
    ///    （历史待办里排第 2，标注"必须先保证 Check/Build 同源"）。
    ///
    ///  ── 这个方法只负责"找出来"，不负责"怎么打" ──
    ///    真正打的时候要切目标，那是另一件事（涉及 `SetTarget` 的时机）。
    ///    先把它做出来喂给 AI / 诊断，让判断有依据。
    ///
    ///  ⚠️ 只返回**有仇恨**的（用 `有仇恨()`）—— 这是本次修的核心。
    ///  ⚠️ 排除当前目标（那个归 `Res_Dot` 管，避免重复建议）。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static List<IBattleChara> 可补Dot的敌人(IReadOnlyList<uint>? 我的DotBuffs,
                                                 float 半径 = 25f, int 最多几个 = 3)
    {
        var 结果 = new List<IBattleChara>();

        try
        {
            var 我 = Core.Me.Position;
            var 当前 = 当前目标();

            bool 有我的Dot(IBattleChara c)
            {
                if (我的DotBuffs == null || 我的DotBuffs.Count == 0) return false;
                try
                {
                    foreach (var b in 我的DotBuffs)
                        if (b != 0 && c.HasAura(b)) return true;
                }
                catch { }
                return false;
            }

            foreach (var 敌人 in Data.AllHostileTargets)
            {
                if (敌人 == null) continue;

                try
                {
                    if (敌人.CurrentHp <= 0) continue;
                    if (当前 != null && 敌人.GameObjectId == 当前.GameObjectId) continue;
                    if (!有仇恨(敌人)) continue;              // ★ 本次修的核心
                    if (有我的Dot(敌人)) continue;

                    var 距离 = Vector3.Distance(我, 敌人.Position);
                    if (距离 > 半径) continue;

                    结果.Add(敌人);
                }
                catch { }
            }

            // 排序：近的优先（少走位、少切目标的时间）
            结果 = 结果.OrderBy(x =>
            {
                try { return Vector3.Distance(我, x.Position); }
                catch { return 999f; }
            }).Take(最多几个).ToList();
        }
        catch { }

        return 结果;
    }

    /// <summary>
    /// AOE 的最佳落点目标（需求 1）。
    /// 用 AEAssist 现成的 GetMostCanTargetObjects，它会挑"这个技能能打到最多敌人"的那个目标；
    /// 选不到就退回当前目标。
    /// </summary>
    [Obsolete("截至 3.19.33 无生产调用点（只有 `Res_Damage` 注释提过）。" +
              "**真正的落点选择走 `智能选目标` / `TargetHelper.GetMostCanTargetObjects`** ——" +
              "别用这个，它的默认 `期望命中数 = 3` 和 `AOE最少敌人数`(=2) 不一致，" +
              "用了会得到和第二套逻辑不同的答案。")]
    public static IBattleChara? AOE最佳目标(uint 技能Id, int 期望命中数 = 3)
    {
        var 当前 = 当前目标();
        if (技能Id == 0) return 当前;

        try
        {
            var 最佳 = 期望命中数 > 1
                ? TargetHelper.GetMostCanTargetObjects(技能Id, 期望命中数)
                : TargetHelper.GetMostCanTargetObjects(技能Id);

            if (最佳 != null && 最佳.CurrentHp > 0) return 最佳;
        }
        catch
        {
        }

        return 当前;
    }

    /// <summary>
    /// 当前目标是不是"快死的残血小怪"（需求 2）—— 这种目标不要交爆发。
    /// 血量低于阈值，或者 TTK 估算很快，都算。
    /// </summary>
    /// <summary>
    /// **这个怪值不值得上 DoT**（照抄参考实现的 ShouldSkipDotByHp）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 判据（三段，逐条对照参考实现的 IL）★
    ///
    ///    ① `CurrentHp <= 0` 或血量比例 <= 0.1%  → 跳过（正在死）
    ///    ② 血量比例 < 「不挂Dot血线」（默认 3%）→ 跳过
    ///    ③ `MaxHp <= 队伍最大血量 × 倍数`       → 跳过
    ///
    ///  ── 第 ③ 条是这次新增的核心 ──
    ///     DoT 是 **30 秒**的持续伤害。怪 **5 秒就死**的话，
    ///     剩下 25 秒的伤害全浪费，还占了一个本该打直接伤害的 GCD。
    ///     ⇒ **小怪无论血线多少都不该上 DoT。**
    ///
    ///  ⚠️ 分母用「队伍最大血量」而不是「我的」——
    ///     参考实现用的是我的，但我的 MaxHp 随等级变化极大
    ///     （Lv50 学者约 1.5 万 / Lv90 约 6 万），而怪的 MaxHp 也随等级变，
    ///     两边不同步 → 照抄会在低等级把绝大多数怪判成"不值得"（等于关掉 DoT）。
    ///     详见 `HealSettings.Dot血量倍数` 的说明。
    ///
    ///  ⚠️ 倍数为 0（或负）= **关掉第 ③ 条**（回到旧行为）。
    ///     拿不到队伍血量时也**放行** —— 这条是"优化"不是"安全"，
    ///     拿不到数据不该拦着（保守方向是别乱拦）。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    /// <summary>
    /// **这个目标身上是不是已经有「预铺类」效果** —— 盾 / 绿帽 / 护盾幕 都算。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  [!] 为什么必须有它（用户实测「T 身上有绿帽还读单盾，完全是浪费」）：
    ///
    ///      原来 `Res_HealSingleGcd` / `Res_HealShield` 的去重判据只查
    ///      **`_t.单体盾` 一个技能** ==> 绿帽（深谋远虑之策，buff 1220）
    ///      根本不在检查范围里 ==> T 有绿帽时照样铺单盾。
    ///
    ///  [!] 参考实现怎么做的（IL 实证，不是推测）：
    ///      · youshu `ScholarTools::HasScholarShield`（IL 26405-26419）
    ///        查 aura **297 / 1918 / 2607 / 2608 / 2609**（纯存在性，不看剩余时间）
    ///        而 Excog 那条路**额外**查自己的 aura **1220**
    ///      · shiyuvi 走另一条路：判据是 `(CurrentHpPercent + ShieldPercentage) <= 阈值`
    ///        —— **把盾算进有效血量**
    ///      两者的共同点：**已经有预铺就不该再铺**。
    ///
    ///  [!] 为什么列这么多 buff 而不是只查盾：
    ///      四个奶妈各有一套「预铺」—— 绿帽 / 水流幕 / 天星交错 / 均衡系 / 活化 / 混合。
    ///      它们的**收益是同一种**（提前把血/盾垫上），所以**重叠时都是浪费**。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static bool 已有预铺(IBattleChara? 目标)
    {
        if (目标 == null) return false;
        try
        {
            if (!目标.对象有效()) return false;
    
            // ── 学者 ──
            //   鼓舞(297) / 激励(1918) 是盾；深谋远虑之策(1220) 是绿帽
            if (目标.有该技能的Buff(SpellIds.取("鼓舞激励之策"))) return true;
            if (目标.有该技能的Buff(SpellIds.取("深谋远虑之策"))) return true;
    
            // ── 白魔 ── 神祝祷(7432) 同时是个人减伤与单体盾
            if (目标.有该技能的Buff(SpellIds.取("神祝祷"))) return true;
            if (目标.有该技能的Buff(SpellIds.取("水流幕"))) return true;
    
            // ── 占星 ── 天星交错(16556)
            if (目标.有该技能的Buff(SpellIds.取("天星交错"))) return true;
    
            // ── 贤者 ── 均衡系（诊断/预后 在均衡状态下的盾）
            if (目标.有该技能的Buff(SpellIds.取("诊断"))) return true;
            if (目标.有该技能的Buff(SpellIds.取("预后"))) return true;
            if (目标.有该技能的Buff(SpellIds.取("活化"))) return true;
            if (目标.有该技能的Buff(SpellIds.取("混合"))) return true;
    
            // ── 通用：任何护盾类状态（`减伤状态表.护盾` 直查）──
            //   [!] 兜底用表查，防止将来加了新盾技能却漏在这个清单里。
            try
            {
                foreach (var kv in 减伤状态表.护盾)
                    if (目标.HasAura(kv.Key)) return true;
            }
            catch { }
        }
        catch { }
        return false;
    }
    
    public static bool 值得上Dot(IBattleChara? 目标)
    {
        if (目标 == null) return false;

        try
        {
            // ① 已经在死 / 死了
            if (目标.CurrentHp <= 0) return false;
            var 血比 = 目标.MaxHp > 0 ? 目标.CurrentHp / (float)目标.MaxHp : 1f;
            if (血比 <= 0.001f) return false;

            // ② 低于「不挂Dot血线」（沿用已有设置，默认 3%）
            float 血线;
            try { 血线 = HealSettings.Instance.不挂Dot血线; }
            catch { 血线 = 0.03f; }
            if (血比 < 血线) return false;

            // ③ 血量倍数（0 = 关掉这条）
            float 倍数;
            try { 倍数 = HealSettings.Instance.Dot血量倍数; }
            catch { 倍数 = 12f; }
            if (倍数 <= 0f) return true;

            var 基准 = 队伍最大血量();
            if (基准 <= 0f) return true;      // 拿不到 → 放行

            return 目标.MaxHp > 基准 * 倍数;
        }
        catch
        {
            return true;   // 任何异常 → 放行（别因为判断失败就不上 DoT）
        }
    }

    /// <summary>
    /// **队伍里最大的 MaxHp** —— 给 DoT 血量倍数当分母。
    ///
    /// ⚠️ 用队伍最大而不是"我的"：坦克血通常最厚，
    ///     用它当基准更稳定（不会因为切了个脆皮职业就变）。
    ///     拿不到就退回我自己。
    /// </summary>
    private static float 队伍最大血量()
    {
        try
        {
            float 最大 = 0f;
            foreach (var r in PartyHelper.CastableParty)
            {
                if (r == null) continue;
                if (r.MaxHp > 最大) 最大 = r.MaxHp;
            }
            if (最大 > 0f) return 最大;
        }
        catch { }

        try { return Core.Me.MaxHp; } catch { return 0f; }
    }

    public static bool 目标快死了(float 血线 = 0.25f, int ttk秒 = 12)    {
        var t = 当前目标();

        // 没目标就别拦着（让输出逻辑自己处理）
        if (t == null) return false;

        if (t.有效血量比例() <= 血线) return true;

        try
        {
            if (TTKHelper.IsTargetTTK(t, ttk秒, false)) return true;
        }
        catch
        {
            // TTK 拿不到就只按血量判断
        }

        return false;
    }

    /// <summary>值不值得对当前目标交爆发（需求 2 的统一入口）</summary>
    [Obsolete("截至 3.19.33 全仓库零调用点，保留备用。" +
              "如果你要用它，先确认它和 `目标快死了` / `敌人波次要结束` 的关系，" +
              "别在调用点重新写一套判断。")]
    public static bool 值得交爆发(float 血线 = 0.25f, int ttk秒 = 12)
    {
        return !目标快死了(血线, ttk秒) && !敌人波次要结束();
    }

    // ==================== 全部敌人概览（给 AI 看）====================

    /// <summary>一个敌人的概览信息</summary>
    public sealed class 敌人信息
    {
        public string 名字 = "";
        public uint 血量;
        public uint 上限;
        public float 血量比例;
        public float 距离;
        public bool 是Boss;
        public bool 是当前目标;
        /// <summary>身上有没有"我挂的 DoT"</summary>
        public bool 有我的Dot;

        /// <summary>
        /// **这个怪被拉到了吗**（已进战，在打我们队里的人）。
        ///
        /// ⚠️ 这个字段是**确保进来的都是拉到的**（`其他敌人()`
        ///    已经过滤掉没拉到的）—— 保留它是为了让 AI
        ///    看得出这份数据的口径，以后要是放宽过滤也不会混。
        /// </summary>
        public bool 有仇恨;
    }

    /// <summary>
    /// **除了当前目标之外的敌人** —— 按"对治疗决策的有用程度"排序。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么要做这个 ★
    ///
    ///    "非当前目标的敌人状态也得知道这样才能更好判断"
    ///
    ///  原来 AI 只看到**当前选中**的那一个敌人。这有两个实际盲区：
    ///
    ///    ① **DoT 覆盖判断做不了** —— 只看到当前目标有没有 DoT，
    ///       看不到"一共 5 个怪，其中 3 个有我的 DoT"。
    ///       于是没法判断"该不该再补 DoT"（补了可能全在重复）。
    ///
    ///    ② **AOE / 群奶时机判断不完整** —— 只知道"5 米内有几个"，
    ///       不知道它们的血量分布。一堆残血小怪即将清完时，
    ///       交群奶/爆发是浪费（这正是 `敌人波次要结束` 在管的事，
    ///       但 AI 看不到依据）。
    ///
    ///  ── 排序规则（重要的排前面）──
    ///    1. 身上**有我的 DoT** 的（关系到"要不要补"）
    ///    2. **快死的**（关系到"别浪费资源"）
    ///    3. 离我近的（关系到 AOE 能不能打到）
    ///
    ///  ⚠️ 参数收的是 <c>所有DotBuff</c>（**多档 buff 的数组**），不是单个技能 id。
    ///     依据：`JobSpellTable.所有DotBuff` 的注释 ——
    ///     DoT 升级会换 buff id，只查一个的话满级会"永远认为没上 DoT"。
    ///     这个坑在本地层踩过，喂给 AI 的数据不能重复踩。
    ///
    ///  ⚠️ 只返回**附近**的（默认 25 米内）—— 视野外的敌人对决策没意义，
    ///    而且会把提示词撑爆。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static List<敌人信息> 其他敌人(IReadOnlyList<uint>? 我的DotBuffs = null,
                                        float 半径 = 25f, int 最多几个 = 6)
    {
        var 结果 = new List<敌人信息>();

        try
        {
            var 我 = Core.Me.Position;
            var 当前 = 当前目标();

            bool 有我的Dot(IBattleChara c)
            {
                if (我的DotBuffs == null || 我的DotBuffs.Count == 0) return false;

                try
                {
                    foreach (var b in 我的DotBuffs)
                        if (b != 0 && c.HasAura(b)) return true;
                }
                catch { }

                return false;
            }

            var 候选 = new List<敌人信息>();

            foreach (var 敌人 in Data.AllHostileTargets)
            {
                if (敌人 == null) continue;

                try
                {
                    if (敌人.CurrentHp <= 0) continue;

                    // ⚠️ **必须过滤"没被拉到"的** ——
                    //    `Data.AllHostileTargets` 是**场景里所有敌对目标**。
                    //    不过滤的话，25 米内**还没被 T 拉到的怪**也会进列表，
                    //    AI 看到"附近 3 个敌人里 0 个有 DoT"就会建议补 ——
                    //    而其中可能有两个根本没进战，等于**主动 ADD**。
                    //    判据见 `有仇恨()` 的长注释（Dalamud 没暴露 enmity，
                    //    只能用"它在打谁"间接判断）。
                    if (!有仇恨(敌人)) continue;

                    var 距离 = Vector3.Distance(我, 敌人.Position);
                    if (距离 > 半径) continue;

                    var 信息 = new 敌人信息
                    {
                        名字 = 敌人.Name.ToString(),
                        血量 = 敌人.CurrentHp,
                        上限 = Math.Max(1u, 敌人.MaxHp),
                        距离 = 距离,
                        是Boss = 敌人.IsBoss(),
                        是当前目标 = 当前 != null && 敌人.GameObjectId == 当前.GameObjectId,
                        有我的Dot = 有我的Dot(敌人),
                        有仇恨 = true,   // 能进来就说明已经过了 有仇恨() 过滤
                    };

                    信息.血量比例 = 信息.血量 * 1f / 信息.上限;

                    候选.Add(信息);
                }
                catch { }
            }

            // 排序：有 DoT 的优先 → 快死的优先 → 近的优先
            结果 = 候选
                .OrderByDescending(x => x.有我的Dot)
                .ThenBy(x => x.血量比例)
                .ThenBy(x => x.距离)
                .Take(最多几个)
                .ToList();
        }
        catch { }

        return 结果;
    }

    /// <summary>附近活着的敌人总数（含当前目标）</summary>
    public static int 附近敌人总数(float 半径 = 25f)
    {
        try
        {
            var 我 = Core.Me.Position;
            var n = 0;

            foreach (var 敌人 in Data.AllHostileTargets)
            {
                if (敌人 == null) continue;
                try
                {
                    if (敌人.CurrentHp <= 0) continue;

                    // ⚠️ 口径与 `其他敌人()` 保持一致：**只数被拉到的**。
                    //    不过滤的话，旁边一堆没进战的怪会把数量拉高，
                    //    导致"够人数了该放 AOE"—— 而实际上只有 1 个在打我们。
                    if (!有仇恨(敌人)) continue;

                    if (Vector3.Distance(我, 敌人.Position) > 半径) continue;
                    n++;
                }
                catch { }
            }

            return n;
        }
        catch { return 0; }
    }

    // ==================== 整波判断（对照同类 ACR 的 ShouldHoldForDyingTrash）====================


    /// <summary>
    /// **当前这一波小怪是不是快清完了** —— 是的话不该交爆发。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么要单独做"整波"判断 ★
    ///
    ///    <see cref="目标快死了"/> 只看 `当前目标()` 一个怪。
    ///    但日随的主场景不是"打一个残血 Boss"，而是**一波 3~5 只小怪**：
    ///      · 当前目标满血，可整波合计只剩 20% 血
    ///      · 这时候开爆发/吃爆发药 → 爆发打空，纯浪费
    ///
    ///    所以判据要从"这个怪快死了"升级成"**这一波快没了**"。
    ///
    ///  ★ 判据：**两条并联**（对照同类 ACR 的 ShouldHoldForDyingTrash）★
    ///
    ///    取 25 米内的**非 Boss** 敌人：
    ///      ① 数量为 0 → 不算（没有波次概念）
    ///      ② 合计血量 / 合计上限 < 阈值（默认 30%）→ true
    ///      ③ **平均 DeathPrediction < 毫秒阈值（默认 15000）→ true**
    ///
    ///  ★ 关于第 ③ 条（"平均 TTK"）★
    ///
    ///    ⚠️ 这里原来写着"`TargetStat.DeathPrediction` 拿不到，所以不做" ——
    ///       **那个结论是错的**，后来实测推翻了：
    ///         · 元数据：`AEAssist.CombatRoutine.Module.Target.TargetStat`
    ///           是 **public** 类型，`DeathPrediction` 是 **public** 字段；
    ///           `TargetMgr.Instance` / `TargetStats` / `EnemysIn25` 同理
    ///         · 编译验证：直接用它们写一段代码，**0 error**
    ///       当初大概是反射没查全（只看了属性、没看字段）就下了结论。
    ///
    ///    为什么值得补：**它是唯一把"这波还有多久清完"量化的判据**。
    ///    只看合计血量会漏掉一种情况 ——
    ///    一波 5 只、合计还有 40% 血，但每只都只剩几千血、
    ///    预测 5 秒内全死。这时候交爆发就是纯浪费。
    ///
    ///  ⚠️ 是 `||` 不是 `&&` —— 两条任一成立就算"快清完"，
    ///     不会让原本成立的判断失效。
    ///
    ///  ★ 为什么排除 Boss ★
    ///
    ///    Boss 战只有一只怪，它的血量降到 30% 时**正是该爆发的时候**
    ///    （最后阶段通常有伤害加成机制）。把 Boss 算进来会把爆发憋死。
    ///    这一条和同类 ACR 的"非 Boss 战"门控是同一个意思。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    /// <param name="合计血线">整波合计血量低于这个比例 → 判定快清完</param>
    /// <param name="平均TTK毫秒">平均预测死亡时间低于这个毫秒数 → 判定快清完</param>
    /// <param name="搜索半径">只看这个距离内的敌人（避免把下一波算进来）</param>
    public static bool 敌人波次要结束(float 合计血线 = 0.30f,
                                     float 平均TTK毫秒 = 15000f,
                                     float 搜索半径 = 25f)
    {
        try
        {
            var 我 = Core.Me.Position;

            float 合计当前 = 0f;
            float 合计上限 = 0f;
            var 数量 = 0;

            foreach (var 敌人 in Data.AllHostileTargets)
            {
                if (敌人 == null) continue;

                try
                {
                    if (敌人.CurrentHp <= 0) continue;
                    if (敌人.IsBoss()) continue;                       // Boss 不算"波次"
                    if (Vector3.Distance(我, 敌人.Position) > 搜索半径) continue;

                    合计当前 += 敌人.CurrentHp;
                    合计上限 += Math.Max(1u, 敌人.MaxHp);
                    数量++;
                }
                catch { }
            }

            // 没有小怪 → 不适用（可能是纯 Boss 战，或还没接怪）
            if (数量 == 0 || 合计上限 <= 0f) return false;

            // ① 合计血量
            if (合计当前 / 合计上限 < 合计血线) return true;

            // ② 平均预测死亡时间（TTK）
            //
            // ⚠️ 拿不到统计数据时**返回 false（不判定）**，
            //    让 ① 单独生效 —— 不能因为这条拿不到就把整波判成"没结束"。
            try
            {
                var mgr = AEAssist.CombatRoutine.Module.Target.TargetMgr.Instance;
                if (mgr == null) return false;

                var 统计表 = mgr.TargetStats;
                if (统计表 == null) return false;

                double 预测和 = 0;
                var 预测数 = 0;

                foreach (var kv in mgr.EnemysIn25)
                {
                    var 怪 = kv.Value;
                    if (怪 == null) continue;
                    if (怪.CurrentHp <= 0) continue;
                    if (怪.IsBoss()) continue;                     // 和上面同一个口径

                    if (!统计表.TryGetValue(怪.EntityId, out var st)) continue;
                    if (st.DeathPrediction <= 0) continue;         // <=0 = 还预测不出来

                    预测和 += st.DeathPrediction;
                    预测数++;
                }

                if (预测数 > 0 && 预测和 / 预测数 < 平均TTK毫秒) return true;
            }
            catch { }

            return false;
        }
        catch
        {
            return false;   // 判断不了就当"没结束"，别憋着爆发
        }
    }

    /// <summary>
    /// 当前目标是不是训练木桩（需求 5）。
    /// 木桩环境没有生存压力 —— 治疗全部让路，输出按最优策略打满。
    /// </summary>
    public static bool 是木桩()
    {
        try
        {
            var t = Core.Me.GetCurrTarget();
            return t != null && t.IsDummy();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>木桩模式 = 设置里开了 + 当前目标确实是木桩</summary>
    public static bool 木桩模式
    {
        get
        {
            try
            {
                return HealSettings.Instance.木桩优先输出 && 是木桩();
            }
            catch
            {
                return false;
            }
        }
    }

    // 说明：AEAssist 里"自动选敌"各家做法不同。
    // 骨架里不强行走位选怪 —— 没目标就不输出，避免抢 T 的仇恨。
}
