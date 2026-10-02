using AEAssist.CombatRoutine;
using AEAssist.Helper;
using HealerACR.Common;
using HealerACR.Timeline;

namespace HealerACR.Resolvers;

// ============================================================================
//  0.3.7 一次性补全剩余技能。
//
//  设计：按"机制"归并，一个 resolver 覆盖多个职业 ——
//  比如「单体HoT」这一个类同时管白魔的再生和占星的吉星相位。
//  技能 ID 用 switch 按职业分发，这样不用给四个职业表各加一堆字段。
//
//  每个类的 Check 顺序都遵守 开发约定.md：
//    1. 木桩分类（纯治疗拦、资源循环/输出放）
//    2. QT 开关
//    3. 已有 buff → 不重复上
//    4. 充能技 → 限流
//    5. 目标/血线条件
// ============================================================================

/// <summary>按职业取技能 ID 的小工具</summary>
internal static class 技能选取
{
    public static uint 取(Jobs job, uint 白魔 = 0, uint 学者 = 0, uint 占星 = 0, uint 贤者 = 0)
    {
        return job switch
        {
            Jobs.WhiteMage => 白魔,
            Jobs.Scholar => 学者,
            Jobs.Astrologian => 占星,
            Jobs.Sage => 贤者,
            _ => 0,
        };
    }
}

// ============================================================================
//  一、HoT 类
// ============================================================================

/// <summary>
/// 单体 HoT：白魔 再生 / 占星 吉星相位。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 重写说明（现象："50 级神兵不读再生"）★
///
///  旧实现**只给坦克挂**：
///      var 坦克 = HealTargetHelper.血量最低的坦克(0.95f);
///      if (坦克 == null) return -1;        // ← 没坦克就整个不挂
///  后果：**单人 / 没坦克的场景，再生永远不会被放**。
///  而 HoT 恰恰是最省资源的治疗手段（一个 GCD 换 30 秒持续回血），
///  比硬读 GCD 直疗划算得多 —— 这正是用户说的
///  "有时候再生比硬读 GCD 奶更好"。
///
///  ── 参考同类 ACR 的三层目标选择（它们的 HoT 逻辑是全库最完整的）──
///    ① 止血优先：<85% 且**有持续伤害**且**没有可驱散状态**
///    ② 坦克：低于阈值
///    ③ 非坦克：低于阈值      ← **我们旧实现完全没有这一层**
///  它们的优先级：坦克 20 / **非坦克 30**（非坦克反而更高，
///  因为坦克通常有自回，非坦克掉血更依赖治疗）。
///
///  ── 我们比它们强的地方（既然有就用上）──
///    · **续 HoT 按剩余时间**：它们只看"有没有"（`timeleft > 0` 就算有），
///      结果 HoT 掉光前不会续、掉光后才发现 —— 中间有一段空窗。
///      我们用 `撑不过N个Gcd` 提前续，覆盖不断档。
///    · **不给自己挂**（`可以治()` 里排了假死类状态）
///    · 数值全部走共享工具，不写死
///
///  ⚠️ 为什么"有可驱散状态就不挂"：
///     那个状态很可能马上被驱散掉，此时挂 HoT 是**打在将要消失的问题上**。
///     等驱散完再看血线更合理。
///     （这条是从参考实现学来的，它们的理由注释没写，但这个解释站得住。）
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class Res_SingleHoT : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_SingleHoT(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job,
        白魔: SpellIds.取("再生"),
        占星: SpellIds.取("吉星相位"));

    /// <summary>止血优先的阈值 —— 参考实现用的是 0.85</summary>
    private const float 止血阈值 = 0.85f;

    /// <summary>低于这个血量的一律优先挂 HoT（保底，避免阈值设置过严导致不挂）</summary>
    private const float 保底阈值 = 0.30f;

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("HoT", true)) return -101;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        var 目标 = 选目标();
        if (目标 == null) return -1;

        // ⚠️ 移动守卫：再生是**瞬发**，所以这条对再生永远放行；
        //    但占星的吉星相位是读条的，移动中确实放不出 —— 由它兜住。
        if (!SpellUtil.移动中可用(技能)) return -7;

        if (!SpellUtil.可用(技能)) return -1;

        // ══════════════════════════════════════════════════════════════
        //  ★ 有人濒危 → **直接退出**（不是"降分让路"）★
        //
        //  ⚠️⚠️ 这里踩过一个我自己造的坑，务必看清：⚠️⚠️
        //
        //    第一版我写的是 `return 3`，注释说"让路给直疗(10)"。
        //    那个理由是错的 —— 它建立在"Check() 的**分值**决定谁先执行"之上。
        //
        //    反汇编 AEAssist 实测（`PVE_RunSlotHelper+<CheckNext>d__4::MoveNext`）：
        //        IL_0013  callvirt ISlotResolver::Check
        //        IL_0018  stloc.2
        //        IL_0066  ldc.i4.0
        //        IL_0067  blt IL_016e        ← **只看 < 0，没有任何比大小的指令**
        //        IL_0054  AppendFormatted<int>  ← 分值只进了日志字符串
        //    而 `<RunSlotResolvers>d__2` 是 **foreach 顺序试，第一个 >= 0 的就 leave 退出**。
        //
        //    ⇒ **真正决定顺序的是「在队列里的行号」，分值完全不参与仲裁。**
        //      （官方开发指南写的 "higher priority wins" 是误导性的，
        //        本项目 交接文档.md 早就写着"从上到下遍历，第一个 Check()>=0 的赢"。）
        //
        //    后果：`return 3` 仍然是 >= 0，而 Res_SingleHoT 在队列里排在
        //    Res_HealSingleGcd **之前** → 有人 <35% 时照样抢下这个 GCD 去挂 HoT
        //    → 正是注释声称要避免的"见死不救"。
        //
        //    ⇒ 正确写法只有 `return -1`：**放弃这个 GCD**，让后面的直疗 resolver 接。
        //      （不能靠挪队列位置 —— 那会破坏"中低血量时 HoT 优先"这个正确设计。）
        // ══════════════════════════════════════════════════════════════
        try
        {
            // ══════════════════════════════════════════════════════════
            //  ★ 有人濒危 → 让路给直疗；**但移动中不让** ★
            //
            //  ── 现象 ──
            //    "最后移动中掉血的情况完全可以上再生，而不是等不动了硬读 gcd 奶"
            //
            //  ── 为什么会"等站定" ──
            //    移动中有人掉到 30% 时：
            //      ① `Res_SingleHoT`（本 resolver，队列第 148 行）
            //         看到"有人 <35%" → `return -1` 让路
            //      ② `Res_HealSingleGcd`（第 152 行）接住 ——
            //         但它是**读条**的，移动中被自己的移动守卫挡掉
            //    ⇒ **两个都不放，一直等到站定**。
            //
            //    而再生（137）是**瞬发**的，移动中本来就能放 ——
            //    这是移动中唯一能给出的治疗，让掉它等于把这段 GCD 全废掉。
            //
            //  ── 修法 ──
            //    只有**站定**时才让路（那时直疗确实更好：HoT 太慢，救不了急）。
            //    移动中**不让** —— 因为能让的只有瞬发技能，
            //    而再生就是那个唯一的选择。
            //
            //  ⚠️ 注意这不违背上面那段长注释的本意：
            //     它防的是"有人濒危时抢 GCD 去挂 HoT 而见死不救"——
            //     而移动中**根本没有"救"这个选项**（读条放不出），
            //     所以挂 HoT 不是抢，是唯一能做的事。
            // ══════════════════════════════════════════════════════════
            // ⚠️ 用 `不能读条()`（= 移动中 **或** 空中）而不是 `在移动()` ——
            //    [!] 上面整段推理的**前提**就是"读条放不出"，而**跳跃同样读不出条**
            //        （框架的 `IsPlayerMoving` 不因跳跃改变，见 `空中检测.cs`）。
            //    [!] 用 `在移动()` 的后果：跳起来时它返回 false ->
            //        **会错误地让路**给一个根本放不出来的读条直疗 ->
            //        "跳跃中既不挂 HoT、也不放直疗"，白等落地。
            //    [!] 这是第 4 处同一个判据缺口（另外三处在 `治疗决策` 与
            //        `Res_Heal` 的 `移动中` 参数上），统一到 `不能读条()`。
            if (!SpellUtil.不能读条() && HealTargetHelper.低于阈值人数(0.35f) > 0) return -1;

            return 12;
        }
        catch
        {
            return -1;
        }
    }

    public void Build(Slot slot)
    {
        // ⚠️ **必须和 Check 同源**（开发约定 F③）：
        //    Check 判的是"谁"，Build 就得给同一个人。
        //    这里重新选一次是安全的 —— 选择逻辑是纯查询、无副作用；
        //    真正要避免的是**判 A 放 B**，而这里两次调用是同一个函数。
        var 目标 = 选目标();
        if (目标 == null) return;

        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(new Spell(spell.Id, 目标));
    }

    /// <summary>
    /// 选 HoT 目标 —— **三层，按优先级**（见类注释的说明）。
    ///
    /// 返回 null = 现在不该挂。
    /// </summary>
    private IBattleChara? 选目标()
    {
        try
        {
            var s = HealSettings.Instance;

            // 用户设的单体治疗阈值；但 HoT 比直疗"便宜"，
            // 所以给一个保底值 —— 否则用户把阈值调得很低时，
            // HoT 会因为"没人低于阈值"而永不挂。
            //
            // 📌 归因更正：参考实现的 IL 里 `Math.Max(0.3f, 阈值)`
            //    **只加在坦克层**，非坦克层传的是原阈值。
            //    我们给两层都加了 0.3（比它宽松，算我们的取舍），
            //    但别再把"两家都这么写"当成依据 —— 按 开发约定.md ⑧，
            //    错误归因的注释比没有注释更危险。
            var 阈值 = Math.Max(保底阈值, s.单体治疗阈值);

            // ── ① 止血优先：有持续伤害 + 血线掉了 + 没有被驱散的问题 ──
            //
            //   ⚠️ 这一层用**独立的 0.85 阈值**，不看用户设置 ——
            //      因为"正在持续掉血"本身就是明确的治疗理由，
            //      不该被"血线还没到阈值"挡住。
            var 止血 = 找持续伤害目标(止血阈值);
            if (止血 != null) return 止血;

            // ── ② 坦克：低于阈值 ──
            var 坦克 = HealTargetHelper.血量最低的坦克(阈值);
            if (坦克 != null && 适合挂(坦克)) return 坦克;

            // ── ③ 其他人：低于阈值（**旧实现缺这一层，导致没坦克就永不挂**）──
            //
            //  ⚠️⚠️ 这里**不能排除自己** —— 我第一版加了
            //      `队友.GameObjectId != Core.Me.GameObjectId`，
            //      结果**单人场景又变回"永远不挂再生"**：
            //      单人时队友表里只有自己，③ 层被排除 → 返回 null → 整个技能不触发。
            //      那正是这个类要修的那个 bug，等于换个形式复发了一遍。
            //
            //      参考实现的非坦克层也不排除自己（它只判
            //      `!IsTank && CanReceiveHeal && !HasRegen && 血 < 阈值`）。
            //      而且给自己挂 HoT 本来就不亏 —— 少读一次直疗。
            //
            //  ⚠️ 但也不能无条件给自己：**全队满血时不该给自己挂**
            //      （那会成为"没人需要治疗时也在花 GCD"）。
            //      所以下面的 `最适合挂的其他人()` 会先看有没有**别人**需要，
            //      没有才考虑自己 —— 顺序天然解决了这件事。
            var 队友 = 最适合挂的其他人(阈值);
            if (队友 != null) return 队友;

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// ③ 层：所有低于阈值的人里最该挂 HoT 的那个 —— **包含自己**。
    ///
    /// 为什么要单独写一个而不是用 `最低血量队友()`：
    ///   那个helper的语义是"队友"，我第一版拿它 + 排除自己，
    ///   直接把单人场景排空了。这里明确把"自己"也当候选，
    ///   但**只有在自己确实掉了血的时候**才会被选中（阈值已经过滤过）。
    /// </summary>
    private IBattleChara? 最适合挂的其他人(float 阈值)
    {
        try
        {
            IBattleChara? 最优 = null;
            var 最低 = 1f;

            foreach (var r in PartyHelper.CastableAlliesWithin30)
            {
                // ★ 判 对象有效()：换图时成员被释放但仍非 null（哨兵 0x12345679），只判 null 会崩
                if (r == null || !r.对象有效()) continue;
                // ★ 用**有效血量**：血低但盾厚的人不该抢 HoT 的位置 ★
                if (r.有效血量比例() > 阈值) continue;
                if (!适合挂(r)) continue;

                if (r.有效血量比例() < 最低) { 最低 = r.有效血量比例(); 最优 = r; }
            }

            // 队伍列表里没有自己（某些场景 API 不含自己）→ 单独判一次自己
            //
            //   这样"单人 / 自己掉了血"的场景也能挂上再生。
            if (最优 == null)
            {
                var 我 = Core.Me;
                if (我 != null && 我.有效血量比例() <= 阈值 && 适合挂(我)) return 我;
            }

            return 最优;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>这一层：有持续伤害、血线低于阈值、且没有可驱散状态</summary>
    private IBattleChara? 找持续伤害目标(float 阈值)
    {
        try
        {
            IBattleChara? 最优 = null;
            var 最低 = 1f;

            foreach (var r in PartyHelper.CastableAlliesWithin30)
            {
                // ★ 判 对象有效()：换图时成员被释放但仍非 null（哨兵 0x12345679），只判 null 会崩
                if (r == null || !r.对象有效()) continue;
                if (!适合挂(r)) continue;

                var 比例 = r.有效血量比例();   // ★ 有效血量（含盾）
                if (比例 >= 阈值) continue;

                if (!AuraIds.有持续伤害(r)) continue;

                if (比例 < 最低) { 最低 = 比例; 最优 = r; }
            }

            return 最优;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 这个人现在适合挂 HoT 吗。
    ///
    /// 判据（缺一不可）：
    ///   · 能治（`可以治()` 排掉了假死 / 已被禁止复活那类）
    ///   · 身上**没有可驱散状态**（有的话先驱散更合理 —— 见类注释）
    ///   · 身上**没有我这个 HoT**，或者**快断了**（提前续，别等掉光）
    /// </summary>
    private bool 适合挂(IBattleChara 目标)
    {
        try
        {
            if (!目标.可以治()) return false;

            // 有可驱散状态 → 先等驱散，别把 HoT 打在将要消失的问题上
            try { if (目标.HasCanDispel()) return false; } catch { }

            var buff = AuraIds.技能转Buff(技能);
            if (buff == 0) return true;   // 查不到 buff id 就别挡（宁可多挂）

            // 身上没有 → 该挂
            // ★ 读 buff 前判有效性：HasAura 走 StatusList，原生违例会穿透 catch
            if (目标 == null || !目标.对象有效()) return false;
            if (!目标.HasAura(buff)) return true;

            // 身上有 → **提前续**：撑不过 3 个 GCD 就该补了
            //
            //   ⚠️ 这里和参考实现**不一样**（它们只看"有没有"）。
            //      它们那样写会有一段"HoT 已掉光但还没补"的空窗，
            //      我们用"剩余 GCD 数"提前接上，覆盖不断档。
            return 目标.撑不过N个Gcd(buff, 3, 1.5f);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 群体 HoT / 场地治疗：白魔 庇护所 / 贤者 自生（自生II 是升级版）。
///
/// ══════════════════════════════════════════════════════════════════
///  [!] **为什么只有两个职业注册它**（横向一致性检查问过这个）
///
///      因为这个技能**只有这两个职业有**：
///        白魔 庇护所(3569) —— 地面 HoT
///        贤者 自生(24288/24302) —— 自身中心 HoT
///        学者：没有群体 HoT（它的持续治疗靠**小仙女**，走 `SCH_*` 那几个）
///        占星：没有群体 HoT（它的持续治疗是**单体**的 吉星相位）
///      ==> 学者/占星的注册表里没有它，**是"没有这个技能"，不是"漏了"**。
///      （同一个道理：`Res_SingleHoT` 只给白魔/占星，另两个职业没有单体 HoT。）
///
///  [!] 为什么技能是**硬编码在这个类里**（`技能选取.取`）而不是走 `JobSpellTable` 槽位：
///      这是**结构上的不一致** —— 其它同类（单盾 / 群盾 / 群疗…）都走表里的槽位。
///      但**不值得为此重构**：
///        · 只有 2 个职业用它，且技能恒定
///        · 改动会牵动 `JobSpellTable` 的公开成员（AI 层在读那些槽位）
///      ⚠️ 所以：**以后要把"贤者的群体 HoT 换成别的技能"，改这里，不是改表**。
///         （`Res_SingleHoT` / `Res_HealLink` / `Res_HealAmp` / `Res_KardiaBoost`
///           也都是这种写法 —— 横向检查时别再当成"漏了槽位"。）
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class Res_GroupHoT : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_GroupHoT(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job,
        白魔: SpellIds.取("庇护所"),
        贤者: SpellUtil.取已解锁(SpellIds.取("自生II"), SpellIds.取("自生")));

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("HoT", true)) return -101;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        var s = HealSettings.Instance;

        // 时间轴预报到伤害 → 提前铺（庇护所是场地、自生是自身中心）
        if (TimelineManager.未来有减伤(4.0) || 减伤Helper.即将来大伤害())
        {
            return SpellUtil.可用(技能) ? 8 : -1;
        }

        // 兜底：多人掉血
        var 要求人数 = HealTargetHelper.群疗能力技人数要求(s.群奶最少人数);
        // ⚠️ 血线走 `治疗阈值表`（带每技能值 + AI 的三种偏移）——
        //    原来硬编码 `s.群体治疗阈值`，**而表里给这两个技能登记的值根本读不到**
        //    （`庇护所` / `自生` 登记了、也归了类，但没有任何 resolver 读 ===> 死登记）。
        //    [!] 后果：AI 按"群疗类"调阔/收紧时，**这个技能纹丝不动** ——
        //        它只吃大类阈值，不吃 AI 偏移（和 Round 7 修掉的那批是同一个问题）。
        //    [!] 判据与 `Res_SingleHoT` / `Res_HealAmp` / 学者那几个能力技**同源**
        //        （开发约定 F③：同一个决策不论从哪条路进来，判断必须一致）。
        var 本技血线 = 治疗阈值表.取(技能, s.群体治疗阈值);
        if (HealTargetHelper.低于阈值人数(本技血线, 20f) < 要求人数) return -1;

        return SpellUtil.可用(技能) ? 8 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;

        // 庇护所是放置型（落脚下），自生是自身中心
        if (_t.Job == Jobs.WhiteMage) slot.Add(new Spell(spell.Id, CharacterExt.我的位置()));
        else slot.Add(spell);
    }
}

// ============================================================================
//  二、减伤 / 护盾类
// ============================================================================

/// <summary>单体减伤 / 增血：白魔 水流幕 / 占星 天星交错 / 学者 生命回生法。</summary>
public class Res_SingleMitigation : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_SingleMitigation(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job,
        白魔: SpellIds.取("水流幕"),
        学者: SpellIds.取("生命回生法"),
        占星: SpellIds.取("天星交错"));

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (HealQt.GetQt("减伤", true) == false) return -100;
        if (!HealSettings.Instance.自动减伤) return -101;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 给坦克；时间轴预报到伤害时满血也给
        var 要来 = TimelineManager.未来有减伤(4.0) || 减伤Helper.即将来大伤害();
        var 坦克 = HealTargetHelper.主坦();
        if (坦克 == null) return -1;
        if (坦克.有该技能的Buff(技能)) return -3;

        // ⚠️ 假死状态不给减伤（参考同类 ACR 的罩子 Check 里的 409/811/810）
        //    坦克开死斗/行尸走肉时那几秒本来就不会死，减伤纯浪费。
        if (坦克.处于假死状态()) return -4;

        // ══════════════════════════════════════════════════════════════
        //  ★ 血线判据（原来**没有**）—— 参考实现里这是它唯一的触发条件 ★
        //
        //  [!] 修的是一个真偏差：`要来` 在上面算出来了，但**没有用在返回值里**，
        //      于是这个技能只在"时间轴说未来有减伤 / 有危险读条"时才会被评估，
        //      **坦克掉到 35% 而当前没有读条时它不会交**。
        //
        //  [!] 参考实现怎么做的（IL 实证）：
        //        youshu  `生命回生法`(25867) 的条件是 `ProtractionTarget(...) == null -> -200`，
        //               选区走 `回生法阈值`（默认 **35**）——
        //               **没有任何读条判据**。
        //        ==> 它是一个**按血线交的治疗技**，
        //            不是"只在大伤害前来一下"的减伤技。
        //
        //  [!] 所以改成"两个条件之一成立就值得考虑"：
        //        ① 有伤害要来（原来的路：时间轴 / 读条）
        //        ② **坦克血线已经到该交的程度**（参考实现的路）
        //      两条都不成立才放弃。
        //
        //  [!] 为什么用 `有效血量比例()` 而不是 `血量比例()`：
        //      参考实现的单奶判据是 `CurrentHpPercent + ShieldPercentage/100 <= 阈值`，
        //      同一个口径（我们的 `有效血量比例()` 就是这个和）。
        //
        //  [!] 阈值走 `治疗阈值表`（按技能查），查不到回落 `单体治疗阈值` ——
        //      这样表为空/没登记时行为退回原样。
        // ══════════════════════════════════════════════════════════════
        var 血线该交 = false;
        try
        {
            var 阈值 = 治疗阈值表.取(技能, HealSettings.Instance.单体治疗阈值);
            if (阈值 > 0f && 坦克.有效血量比例() <= 阈值) 血线该交 = true;
        }
        catch { }

        if (!要来 && !血线该交) return -5;

        return SpellUtil.可用(技能) ? 14 : -1;
    }

    public void Build(Slot slot)
    {
        // ⚠️ 这里原来有一行 `var 要来 = ...`，**赋值后从未被读** —— 纯遗留死代码，已删。
        //    （`Check` 里那一份已经在 Round 3 修好并真的用上了；`Build` 只负责放技能。）
        var 坦克 = HealTargetHelper.主坦();
        if (坦克 == null) return;

        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(new Spell(spell.Id, 坦克));
    }
}

/// <summary>群体减伤：占星 命运之轮 / 学者 疾风怒涛之计 / 贤者 坚角清汁（已有，这里只补前两个）。</summary>
public class Res_GroupMitigationExtra : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_GroupMitigationExtra(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job,
        学者: SpellIds.取("疾风怒涛之计"),
        占星: SpellUtil.取已解锁(SpellIds.取("太阳星座"), SpellIds.取("命运之轮")));

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (HealQt.GetQt("减伤", true) == false) return -100;
        if (!HealSettings.Instance.自动减伤) return -101;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        if (!TimelineManager.未来有减伤(4.0) && !减伤Helper.即将来大伤害()) return -1;

        return SpellUtil.可用(技能) ? 13 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;
        // 能力技不等回包
        slot.Add(CharacterExt.能力技(spell.Id));
    }
}

/// <summary>群体护盾能力技：贤者 泛输血。</summary>
public class Res_GroupShieldAbility : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_GroupShieldAbility(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job, 贤者: SpellIds.取("泛输血"));

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("群盾", false)) return -101;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        if (!TimelineManager.未来有减伤(4.0) && !减伤Helper.即将来大伤害()) return -1;

        return SpellUtil.可用(技能) ? 12 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(spell);
    }
}

// ============================================================================
//  三、强化 / 增疗类
// ============================================================================

/// <summary>强化下一次治疗：学者 秘策 / 贤者 活化。掉血人数够多时先开，再交群奶。</summary>
public class Res_HealBooster : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_HealBooster(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job,
        学者: SpellIds.取("秘策"),
        贤者: SpellIds.取("活化"));

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 已经开着就不重复
        if (CharacterExt.我有该技能的Buff(技能)) return -3;

        // 掉血够多 / 伤害要来 → 值得强化
        var s = HealSettings.Instance;
        var 要来了 = TimelineManager.未来有减伤(3.0) || 减伤Helper.即将来大伤害();
        var 掉血多 = HealTargetHelper.低于阈值人数(s.大招血线) >= s.群奶最少人数;

        if (!要来了 && !掉血多) return -1;

        return SpellUtil.可用(技能) ? 19 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>增加治疗量：贤者 混合（给目标增疗）。</summary>
public class Res_HealAmp : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_HealAmp(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job, 贤者: SpellIds.取("混合"));

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        // ══════════════════════════════════════════════════════════════
        //  ★ 血线判据（原来 `要来` 算了**完全没用**）★
        //
        //  [!] 修的是和前几轮同一个模式：
        //        ① 算出一个判据（`要来`）
        //        ② **那条路没用它**
        //        ③ 结果 = 只要坦克身上没这个 buff，就无条件给
        //      ==> 坦克满血也会被挂「混合」（贤者的增疗 buff，有自己的 CD）。
        //
        //  [!] 有 `要来` 时仍然无条件给（强判据：伤害马上落地，增疗/减伤要提前）。
        //  [!] 没 `要来` 时要求坦克**确实掉血了**才给 ——
        //      用 `治疗阈值表` 查「混合」，查不到回落 `单体治疗阈值`。
        // ══════════════════════════════════════════════════════════════
        var 要来 = TimelineManager.未来有减伤(4.0) || 减伤Helper.即将来大伤害();
        var 目标 = HealTargetHelper.主坦();
        if (目标 == null) return -1;
        if (目标.有该技能的Buff(技能)) return -3;

        if (!要来)
        {
            var 血线该交 = false;
            try
            {
                var 阈值 = 治疗阈值表.取(技能, HealSettings.Instance.单体治疗阈值);
                if (阈值 > 0f && 目标.有效血量比例() <= 阈值) 血线该交 = true;
            }
            catch { }
            if (!血线该交) return -5;
        }

        return SpellUtil.可用(技能) ? 16 : -1;
    }

    public void Build(Slot slot)
    {
        // ⚠️ 这里原来有一行 `var 要来 = ...`，**赋值后从未被读** —— 纯遗留死代码，已删。
        //    `Build` 的职责是"把 Check 选中的东西放进 slot"，不做判断
        //    （开发和约定 F③：判断只在 Check，Build 只负责放）。
        var 目标 = HealTargetHelper.主坦();
        if (目标 == null) return;

        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(new Spell(spell.Id, 目标));
    }
}

// ============================================================================
//  四、大群奶 / 应急类
// ============================================================================

/// <summary>
/// 大群奶能力技：白魔 全大赦 / 占星 大宇宙 / 贤者 整体论、魂灵风息。
/// 只在"掉血人多"或"大伤害要来"时交，平时留着。
/// </summary>
public class Res_BigAoEHeal : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_BigAoEHeal(JobSpellTable t) => _t = t;

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("群奶")) return -101;

        foreach (var id in 候选())
        {
            if (id == 0 || !SpellUtil.已解锁(id) || !SpellUtil.可用(id)) continue;

            var s = HealSettings.Instance;
            // ⚠️ 血线走 `治疗阈值表`（每技能值 + AI 三种偏移）——
            //    原来硬编码 `s.大招血线`，而表里给
            //    `大宇宙 0.55` / `全大赦 0.35` / `魂灵风息 0.60` 登记的值**读不到**。
            //    [!] 数值不会突变：那些登记值的来源注释写着
            //        `// shiyuvi GCD群奶治疗阈值 0.5 / youshu GCD群奶 55` 这类，
            //        说明它们**本来就是从统一值抄过来的** —— 接上以后只是变成可被 AI 逐个调。
            //    [!] 判据与 `Res_GroupHoT` / `Res_HealAoEAbility` 同源（开发约定 F③）。
            var 本技血线 = 治疗阈值表.取(id, s.大招血线);
            var 人够多 = HealTargetHelper.低于阈值人数(本技血线, 20f) >= s.群奶最少人数;   // 20 米：大宇宙 25874
            var 要来了 = TimelineManager.未来有减伤(3.0) || 减伤Helper.即将来大伤害();

            if (!人够多 && !要来了) return -1;

            return 17;
        }

        return -1;
    }

    public void Build(Slot slot)
    {
        foreach (var id in 候选())
        {
            if (id == 0 || !SpellUtil.已解锁(id) || !SpellUtil.可用(id)) continue;

            var spell = SpellUtil.当前形态(id);
            if (spell != null) slot.Add(spell);
            return;
        }
    }

    private uint[] 候选() => _t.Job switch
    {
        // 大宇宙 → 之后要用小宇宙结算，两个都排上
        Jobs.Astrologian => new[] { SpellIds.取("大宇宙"), SpellIds.取("小宇宙") },
        Jobs.WhiteMage => new[] { SpellIds.取("全大赦") },
        Jobs.Sage => new[]
        {
            SpellUtil.取已解锁(SpellIds.取("魂灵风息"), SpellIds.取("整体论")),
            SpellIds.取("魂灵风息"),
            SpellIds.取("整体论"),
        },
        _ => Array.Empty<uint>(),
    };
}

/// <summary>放置式大治疗：白魔 礼仪之铃。</summary>
public class Res_PlacedHeal : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_PlacedHeal(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job, 白魔: SpellIds.取("礼仪之铃"));

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        if (!TimelineManager.未来有减伤(5.0) && !减伤Helper.即将来大伤害()) return -1;

        return SpellUtil.可用(技能) ? 11 : -1;
    }

    public void Build(Slot slot)
    { 
            // 地面技能选位（参考同类 ACR 的敌人移动检测）
            try
            {
                var 落点 = 敌人移动检测.地面技能位置();
                var sp = SpellUtil.当前形态(技能);
                if (sp != null) { slot.Add(new Spell(sp.Id, 落点)); return; }
            }
            catch { }

        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;
        slot.Add(new Spell(spell.Id, CharacterExt.我的位置()));
    }
}

/// <summary>应急：学者 应急战术（盾转治疗）/ 展开战术（扩散盾）/ 贤者 寄生清汁（消耗盾换治疗）。</summary>
public class Res_Emergency : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_Emergency(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job,
        学者: SpellIds.取("应急战术"),
        贤者: SpellIds.取("寄生清汁"));

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("应急", true)) return -101;   // 兜底改成 true —— 之前是 false 导致紧急治疗从未生效
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        var s = HealSettings.Instance;
        if (HealTargetHelper.低于阈值人数(s.群体治疗阈值, 20f) < HealTargetHelper.群奶人数要求(s.群奶最少人数)) return -1;

        return SpellUtil.可用(技能) ? 10 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>扩散盾：学者 展开战术（把自己身上的盾扩散给全队）。</summary>
public class Res_SpreadShield : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_SpreadShield(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job, 学者: SpellIds.取("展开战术"));

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("群盾", false)) return -101;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 自己身上得有盾。
        //
        // ⚠️ 用 `有该技能的Buff(185)` —— 它内部覆盖**四档**：
        //      297 鼓舞 / 1918 激励（暴击盾）/ 3087 / 3088
        //
        //    原来只查 `AuraIds.鼓舞`(297) 一个 —— 而**暴击时挂的是 1918 激励**，
        //    于是"自己身上是暴击盾"时这里判断成"没盾" → `return -3` → 扩散不触发。
        //    （和"技能 ID 当 buff ID 查"是同一类坑的另一面：
        //      这次是"只查了普通那一档、漏了暴击那一档"。）
        if (!CharacterExt.我有该技能的Buff(185)) return -3;

        if (!TimelineManager.未来有减伤(4.0) && !减伤Helper.即将来大伤害()) return -1;

        return SpellUtil.可用(技能) ? 9 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(spell);
    }
}

// ============================================================================
//  五、其他
// ============================================================================

/// <summary>免蓝：白魔 无中生有（下一次治疗不耗蓝）。2 层充能，卡 CD 开。</summary>
public class Res_FreeCast : ISlotResolver
{
    private readonly JobSpellTable _t;

    private static long 上次开;

    public Res_FreeCast(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job, 白魔: SpellIds.取("无中生有"));

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 2 层充能：限流，别一口气全交
        if (Environment.TickCount64 - 上次开 < 4000) return -7;
        if (CharacterExt.我有该技能的Buff(技能)) return -3;

        // 有人需要治疗时才开（不然白开）
        if (HealTargetHelper.低于阈值人数(HealSettings.Instance.单体治疗阈值) == 0) return -1;

        return SpellUtil.可用(技能) ? 6 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;
        slot.Add(spell);
        上次开 = TimeHelper.Now();
    }
}

/// <summary>
/// **预铺类 / 瞬发类单体治疗能力技** —— "优先用不读条的"。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 解决什么 ★
///
///  现象："治疗应该优先能力技（或者说不用读条的技能），
///             比如学者可以给 T 上绿帽而不是狂读单盾"
///
///  日志实证（他那一场学者）：
///      鼓舞激励之策（单盾，**读条**）  61 次
///      生命活性法（能力技）             6 次
///      **深谋远虑之策（绿帽）           0 次**
///
///  ── 绿帽为什么是 0 ──
///    它原来被塞在 `紧急单奶` 栏里，急救判据是血 < 30%（`紧急单奶阈值`）。
///    日志里坦克一直在 80% → **永远不触发**。
///    而绿帽是 45 秒 CD 的**预铺**技（挂上后目标掉到阈值自动触发治疗），
///    设计用途就是"提前给"。**分类错了 → 等于这个技能不存在。**
///
///  ── 参考实现怎么做的（IL 直证）──
///    学者的 resolver 列表顺序：
///        13 不屈不挠之策（能力技）
///        14 鼓舞激励之策（GCD 读条）   ← 单盾
///        15 医术（GCD 读条）
///        16 **深谋远虑之策（绿帽，能力技）**   ← 有独立 QT + 独立阈值
///        18 生命活性法（能力技）
///    即 **能力技有独立的 resolver 和独立的阈值**，而不是被塞进急救、
///    共用"血 < 30%"这一条判据。
///
///  ── 这里的两层 ──
///    ① `预铺单奶能力技`（绿帽 7434 / 水流幕 25861 / 天星交错 16556）
///       → 打给**坦克**，坦克血线掉了就给。不占 GCD、不读条。
///    ② `瞬发单奶能力技`（生命活性法 189 / 神名 3570 …）
///       → 给血最低的人，**移动中尤其重要**（读条那个会被移动守卫挡掉）。
///
///  ⚠️ 两层必须分开：① 的价值在"**提前**"（伤害来之前给），
///    ② 的价值在"**不读条**"（掉血了才给）。判据不同，混在一起会两头不讨好。
///
///  ⚠️ 这个 resolver 在决策队列里的位置**必须在 Res_HealSingleGcd 之前**
///     —— 那才是"优先能力技"的实现方式（返回值不参与仲裁，位置才算）。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class Res_InstantHealAbility : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_InstantHealAbility(JobSpellTable t) => _t = t;

    /// <summary>
    /// 坦克低于这个血线就值得给预铺技。
    ///
    /// ⚠️ **0.60 不是随手定的**（第一版我写了 0.85，太高 —— 已改）。
    ///
    ///  参考实现的学者阈值分层（IL 直读默认值）：
    ///      绿帽（深谋远虑之策）**60%**   ← 能力技，45 秒 CD
    ///      单盾（鼓舞激励之策）**45%**   ← GCD 读条
    ///      GCD 群奶            50%
    ///      生命活性法          45%
    ///
    ///  那 **15 个百分点就是"能力技优先"的实际实现** ——
    ///  不是靠开关，是靠**阈值分层**：便宜的（能力技）在 60% 就交，
    ///  贵的（占 GCD 的读条）等到 45% 才交。
    ///
    ///  ⚠️ 0.85 的问题：坦克被平 A 蹭一下就挂绿帽，45 秒 CD 全浪费在
    ///     不痛不痒的掉血上；真该用的时候反而在 CD。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private const float 预铺血线 = 0.60f;

    /// <summary>选预铺目标：坦克优先，没坦克就退到血量最低的人</summary>
    private IBattleChara? 预铺目标(uint 技能)
    {
        try
        {
            var 坦克 = HealTargetHelper.主坦();
            if (坦克 != null && !坦克.有该技能的Buff(技能) && !坦克.处于假死状态())
                return 坦克;

            // 没坦克（或坦克满血）时别浪费 —— 但不给满血的人铺
            // ★ 审计 P1-11（2026-10-03）：不要再吃本文件里的硬编码 0.60 ——
            //   原来这里用的是 `private const float 预铺血线 = 0.60f`（第二份硬编码 ✗），
            //   导致 AI 调「预铺血线」时这个能力技完全不受影响。
            //   ⇒ 统一走参数入口 `治疗阈值表.取(技能, 统一阈值)`：
            //      它按【单技能偏移 + 类别(预铺)偏移 + 全局 AI 倾向】合成 ✓
            //      那个常量只留作"表里没有这个技能时的基准值" ✓
            var 目标 = HealTargetHelper.最低血量队友(
                HealerACR.Common.治疗阈值表.取(技能, 预铺血线));
            if (目标 != null && !目标.有该技能的Buff(技能) && !目标.处于假死状态())
                return 目标;

            return null;
        }
        catch { return null; }
    }

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("单奶")) return -101;

        // ⚠️ **能力技队列深度闸门**（对照分析发现的缺口）——
        //    参考实现里瞬发单奶用 `CanUseOffGcd(1)`。
        //    ⚠️ 加在 ①② 之前，两条路都管住（加在后面只管得住后一条）。
        if (!OffGcd闸门.可以排(1)) return -1;

        // ── ① 预铺类 ──
        var 预铺 = _t.预铺单奶能力技;
        if (预铺 != 0 && SpellUtil.已解锁(预铺) && SpellUtil.可用(预铺) && 预铺目标(预铺) != null)
        {
            // ⚠️ 有人濒危时让路给急救（`Res_HealEmergency` 排在 OffGcd 趟，
            //    这里只保证不跟"必须奶满/急救"抢目标）
            if (必须奶满.找目标() == null && HealTargetHelper.低于阈值人数(0.30f) == 0)
                return 26;
        }

        // ── ② 瞬发类 ──
        //
        //   ⚠️ 血线用**技能表自己的 `瞬发单奶血线`**（默认 0.75），
        //      不是 `单体治疗阈值`（默认 0.52）。
        //
        //      参考实现的白魔就是这么分的（IL 直读）：
        //          神名（60 秒 CD、有充能）→ **75%**  ← 常规补血
        //          天赐（180 秒 CD）        → **20%**  ← 救命
        //          GCD 单奶                 → **40%**
        //      便宜的先交、贵的后交 —— 这才是"优先用不读条的"。
        //      如果这里用 0.52，神名会等到比 GCD 单奶还晚，等于白配。
        var 瞬发 = _t.瞬发单奶能力技;
        if (瞬发 != 0 && SpellUtil.已解锁(瞬发) && SpellUtil.可用(瞬发))
        {
            // ★ 血线改成**按技能查**（`治疗阈值表`），查不到才回落到职业表的 `瞬发单奶血线` ★
            //   [!] 参考实现的阈值是**每技能一个**（IL 实证）：
            //         shiyuvi Lustrate 0.45 / Adloquium 0.4 / FeyBlessing 0.6 …
            //         youshu  活性法 45 / 单盾 45 / 绿帽 60 / 不屈 70 / 祥光 70 …
            //       ==> 统一一个 `瞬发单奶血线` 表达不了这种分层。
            //   [!] 但**这只是第一步**：真正的结构差异是「参考按技能选区，我们先选区再选技能」，
            //       那个改动更大，留到单独一轮（见 `复刻边界-参考实现与AI层.md`）。
            var 单奶血线 = 治疗阈值表.取(瞬发, _t.瞬发单奶血线);
            var 目标 = HealTargetHelper.最低血量队友(单奶血线);
            if (目标 != null && !目标.处于假死状态() && 必须奶满.找目标() == null)
                return 24;
        }

        return -1;
    }

    public void Build(Slot slot)
    {
        // ⚠️ 必须和 Check 同源（开发约定 F③）：判谁就放谁，顺序也必须一致。

        var 预铺 = _t.预铺单奶能力技;
        if (预铺 != 0 && SpellUtil.已解锁(预铺) && SpellUtil.可用(预铺))
        {
            if (必须奶满.找目标() == null && HealTargetHelper.低于阈值人数(0.30f) == 0)
            {
                var 目标 = 预铺目标(预铺);
                if (目标 != null)
                {
                    var s = SpellUtil.当前形态(预铺);
                    if (s != null) { slot.Add(new Spell(s.Id, 目标)); return; }
                }
            }
        }

        var 瞬发 = _t.瞬发单奶能力技;
        if (瞬发 != 0 && SpellUtil.已解锁(瞬发) && SpellUtil.可用(瞬发))
        {
            if (必须奶满.找目标() == null)
            {
                // ⚠️ 和 Check 用**同一个血线**（`瞬发单奶血线`），否则 Check 过了 Build 找不到目标
                // ★ 血线改成**按技能查**（`治疗阈值表`），查不到才回落到职业表的 `瞬发单奶血线` ★
                //   [!] 参考实现的阈值是**每技能一个**（IL 实证）：
                //         shiyuvi Lustrate 0.45 / Adloquium 0.4 / FeyBlessing 0.6 …
                //         youshu  活性法 45 / 单盾 45 / 绿帽 60 / 不屈 70 / 祥光 70 …
                //       ==> 统一一个 `瞬发单奶血线` 表达不了这种分层。
                //   [!] 但**这只是第一步**：真正的结构差异是「参考按技能选区，我们先选区再选技能」，
                //       那个改动更大，留到单独一轮（见 `复刻边界-参考实现与AI层.md`）。
                var 单奶血线 = 治疗阈值表.取(瞬发, _t.瞬发单奶血线);
                var 目标 = HealTargetHelper.最低血量队友(单奶血线);
                if (目标 != null && !目标.处于假死状态())
                {
                    var s = SpellUtil.当前形态(瞬发);
                    if (s != null) slot.Add(new Spell(s.Id, 目标));
                }
            }
        }
    }
}

/// <summary>治疗转移：占星 星位合图（把对坦克的治疗复制一份给队友）。</summary>
public class Res_HealLink : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_HealLink(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job, 占星: SpellIds.取("星位合图"));

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;
        if (CharacterExt.我有该技能的Buff(技能)) return -3;

        // ══════════════════════════════════════════════════════════════
        //  ★ 血线判据（原来**没有** —— 坦克无条件吃）★
        //
        //  [!] 修的是和 `Res_SingleMitigation` / `Res_HealAmp` **同一个模式**：
        //        选了一个纯选择器（`主坦()` —— 它只回答"谁是坦克"，
        //        **不带任何血线**），然后 `if (坦克 != null) return ...;`
        //      ==> **门槛没人做** ==> 坦克满血也会被挂星位合图。
        //
        //  [!] 参考实现怎么做的（youshu IL 实证）：
        //        `星位合图` 的 `SelectTarget`：
        //          坦克阈值 = `Clamp((星位合图阈值 - 5) / 100)` = **25%**
        //          非坦克   = `星位合图阈值 / 100`             = **30%**
        //          条件还含 `CanReceiveHeal && !有845 && CanSingleHealTarget`
        //        ==> 两套都是**按血线选人**，没有"坦克无条件给"这回事。
        //        （`星位合图阈值` 默认 30 -> 我们的 `治疗阈值表` 里登记的也是 0.30）
        //
        //  [!] 所以：坦克**过了血线**才给它；没过就往下看有没有别人需要。
        //      （学者那边修 `Res_SingleMitigation` 时用的就是这个形状，
        //        F③ 要求同一个决策在各条路上一致。）
        //
        //  [!] 用 `有效血量比例()`（含盾）而不是 `血量比例()` ——
        //      与参考的 `hp + shield/100` 同口径，也与本项目其它地方一致。
        // ══════════════════════════════════════════════════════════════
        var 血线 = 治疗阈值表.取(技能, HealSettings.Instance.单体治疗阈值);

        var 坦克 = HealTargetHelper.主坦();
        if (坦克 != null && 坦克.有效血量比例() <= 血线) return SpellUtil.可用(技能) ? 7 : -1;

        // 坦克没过线 -> 看有没有别人到线了（没有坦克的场景也走这里）
        var 队友 = HealTargetHelper.最低血量队友(血线);
        if (队友 != null && 队友.可以治()) return SpellUtil.可用(技能) ? 7 : -1;

        return -1;
    }

    public void Build(Slot slot)
    {
        // ⚠️ 必须和 Check 同源（F③）：判谁就放谁。
        //    这里重跑同一个选择逻辑（纯查询、无副作用），而不是缓存 —— 
        //    和 `Res_SingleHoT.Build` 同一套做法。
        var 血线 = 治疗阈值表.取(技能, HealSettings.Instance.单体治疗阈值);

        var 坦克 = HealTargetHelper.主坦();
        var 目标 = 坦克 != null && 坦克.有效血量比例() <= 血线
            ? 坦克
            : HealTargetHelper.最低血量队友(血线);
        if (目标 == null) return;

        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(new Spell(spell.Id, 目标));
    }
}

/// <summary>心关强化：贤者 拯救（短时间内心关治疗量提升）。</summary>
public class Res_KardiaBoost : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_KardiaBoost(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job, 贤者: SpellIds.取("拯救"));

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;
        if (CharacterExt.我有该技能的Buff(技能)) return -3;

        // 有人明显掉血的时候开
        if (HealTargetHelper.低于阈值人数(HealSettings.Instance.单体治疗阈值) == 0) return -1;

        return SpellUtil.可用(技能) ? 7 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>转化：学者 转化（牺牲小仙女换 3 颗以太）—— 以太见底且小仙女在场上时用。</summary>
public class SCH_Dissipation : ISlotResolver
{
    private static uint 技能 => SpellIds.取("转化");

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("以太超流", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 以太空了、小仙女还在 → 转化换以太
        if (JobApiHelper.以太 >= 1) return -3;
        if (!JobApiHelper.有小仙女) return -4;
        if (JobApiHelper.炽天使剩余 > 0) return -5;

        return SpellUtil.可用(技能) ? 4 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(spell);
    }
}
