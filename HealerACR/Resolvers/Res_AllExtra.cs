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
        // oGCD 队列深度闸门（参考口径 CanUseOffGcd(2)）
        if (!OffGcd闸门.可以排(2)) return -4;
        if (!SpellUtil.已解锁(技能)) return -2;

        // ★ **我自己不能接受治疗时，别把地面技铺在自己脚下**（表 #30）
        //   [!] 参考里白魔 庇护所 在 Check 里判它（返回 -7）——
        //       白魔的庇护所是"脚下场地"、贤者的自生是"自身中心"，
        //       两者落点都必然包含我自己 ⇒ 我治不了自己时那一份是纯损失。
        if (AuraIds.我无法接受治疗()) return -7;

        // ★ 2026-10-15：**白魔·已有团减就不叠**（复刻 youshu `庇护所.txt` / `节制.txt`：
        //   自身 `HasAura(1911 庇护所)` 或 `HasAura(1873 节制)` → -5）★
        //   [!] 两条 aura 都用官方 `Status.csv` 核验过（1911=庇护所 / 1873=节制），
        //       已登记为 `AuraIds.庇护所` / `AuraIds.节制`。
        //   [!] 为什么需要：庇护所与节制是白魔的两张**群减**牌（52 级 / 80 级），
        //       参考在两者各自的门里都查对方的状态 ⇒ 同一次群减窗口只烧一张。
        //       我们原来只靠 `团减快照`（`Res_TeamMitigation` 那条路），
        //       而本 resolver 是**另一条路**（OffGcd 的群体 HoT）⇒ 会"节制刚交完、
        //       庇护所又铺一层"地重复覆盖 ✗
        //   [!] **只对白魔判** —— 本 resolver 贤者也用（`自生`），
        //       那两个 id 是白魔的 aura，对贤者判没有意义（还白读两次 aura）。
        if (_t.Job == Jobs.WhiteMage)
        {
            try
            {
                var 我 = AEAssist.Core.Me;
                if (我 != null && 我.对象有效()
                    && (我.HasAura(AuraIds.庇护所) || 我.HasAura(AuraIds.节制)))
                    return -5;
            }
            catch { }
        }

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

        // ★ 2026-10-04：**刚有人消耗过豆子 ⇒ 这一拍别再消耗** ✓
        //   本项目采用「一次消耗后全局抑制」的口径（以太层数下降后 7 秒）——
        //   避免同一拍把豆子连打光、也避免两个能力技互相抢（两套对照实现都有等价物）
        if (以太管理.抑制中) return -3;
        if (HealTargetHelper.木桩模式) return -300;
        if (HealQt.GetQt("减伤", true) == false) return -100;
        if (!HealSettings.Instance.自动减伤) return -101;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        // ══════════════════════════════════════════════════════════════
        //  ★ 选区：**坦克优先，其次非坦**（参考实现的 `AquaVeilTarget`）★
        //
        //  [!] 原来只认 `主坦()` 一个 ✗ —— 后果是
        //      "主坦满血、而某个 DPS 掉到 30%" 时，这个减伤**谁也不给** ✗
        //
        //  [!] 参考的判据（IL 直读 `AquaVeilTarget`）：
        //      在**所有可治队友**里找血量最低的那个，但**阈值分两档** ——
        //        坦克   → 阈值 - 10
        //        非坦克 → 阈值
        //      也就是"坦克门更宽"：坦克没掉那么多也值得给，
        //      而给非坦要掉得更狠才值得（把这一发留给坦克的意图）。
        //
        //  [!] 用 `有效血量比例()`（血量 + 盾）而不是裸血量 ——
        //      和参考的 `CurrentHpPercent + ShieldPercentage/100` 同口径。
        // ══════════════════════════════════════════════════════════════
        var 基准阈值 = 治疗阈值表.取(技能, HealSettings.Instance.单体治疗阈值);
        var 坦克阈值 = Math.Clamp(基准阈值 - 0.10f, 0f, 1f);
        var 非坦阈值 = Math.Clamp(基准阈值, 0f, 1f);

        // 伤害要来吗（时间轴 / 危险读条）—— 上面选区之前就要算好
        var 要来 = TimelineManager.未来有减伤(4.0) || 减伤Helper.即将来大伤害();

        var 该给谁 = 选单体减伤目标(技能, 坦克阈值, 非坦阈值);
        if (该给谁 == null) return -1;

        // ⚠️ 假死状态不给减伤（参考同类 ACR 的罩子 Check 里的 409/811/810）
        //    坦克开死斗/行尸走肉时那几秒本来就不会死，减伤纯浪费。
        if (该给谁.处于假死状态()) return -4;

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
        //        ② **目标血线已经到该交的程度**（参考实现的路）
        //      两条都不成立才放弃。
        //
        //  [!] 阈值走 `治疗阈值表`（按技能查），查不到回落 `单体治疗阈值` ——
        //      这样表为空/没登记时行为退回原样。
        // ══════════════════════════════════════════════════════════════
        var 血线该交 = false;
        try
        {
            var 该目标阈值 = 该给谁.IsTank() ? 坦克阈值 : 非坦阈值;
            if (该目标阈值 > 0f && 该给谁.有效血量比例() <= 该目标阈值) 血线该交 = true;
        }
        catch { }

        if (!要来 && !血线该交) return -5;

        return SpellUtil.可用(技能) ? 14 : -1;
    }

    /// <summary>
    /// 挑这一发单体减伤给谁：**坦克可以更早给**（阈值 -0.10），
    /// 在所有可治队友里取"相对自己那档阈值掉得最多"的那个。
    ///
    /// [!] 不分两轮（先找坦克、再找非坦）—— 参考实现是**一趟遍历选最低血**，
    ///     只有阈值不同。分两轮会让"坦克 95%、DPS 20%"这种局面仍然给坦克。
    /// </summary>
    private static IBattleChara? 选单体减伤目标(uint 技能, float 坦克阈值, float 非坦阈值)
    {
        IBattleChara? 最优 = null;
        var 最优剩余 = float.MaxValue;   // "离自己的阈值还差多少"，越小越该给

        try
        {
            foreach (var 成员 in PartyHelper.CastableAlliesWithin30)
            {
                if (成员 == null || !成员.对象有效()) continue;
                if (!成员.活着() || !成员.可以治()) continue;

                // 已经有这个 buff ⇒ 不重复给
                if (成员.有该技能的Buff(技能)) continue;

                // ⚠️ 天星交错还要**排除 2717**（IL `CanCastCelestialIntersection`：
                //    `!HasAura(1889, 0) && HasAura(2717, 0) == 0`）——
                //    与 `天星交错档位()` / `天星可施()` 同一判据（开发约定 F③）。
                //    只对占星这一发生效，其余职业行为不变。
                if (技能 == SpellIds.取("天星交错") && 成员.HasAura(2717)) continue;

                var 是坦克 = 成员.IsTank();
                var 阈值 = 是坦克 ? 坦克阈值 : 非坦阈值;
                if (阈值 <= 0f) continue;

                var 有效 = 成员.有效血量比例();
                var 剩余 = 有效 - 阈值;       // 正数 = 还没到该给的程度
                if (剩余 > 0f) continue;      // 没到阈值，跳过

                if (剩余 < 最优剩余)
                {
                    最优剩余 = 剩余;
                    最优 = 成员;
                }
            }
        }
        catch { }

        return 最优;
    }

    public void Build(Slot slot)
    {
        // ⚠️ 必须和 Check 同源（开发约定 F③）：同一个选区方法 + 同一组阈值。
        var 基准阈值 = 治疗阈值表.取(技能, HealSettings.Instance.单体治疗阈值);
        var 坦克阈值 = Math.Clamp(基准阈值 - 0.10f, 0f, 1f);
        var 非坦阈值 = Math.Clamp(基准阈值, 0f, 1f);

        var 目标 = 选单体减伤目标(技能, 坦克阈值, 非坦阈值);
        if (目标 == null) return;
        if (目标.处于假死状态()) return;

        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(new Spell(spell.Id, 目标));
    }
}

/// <summary>群体减伤：占星 命运之轮 / 学者 疾风怒涛之计 / 贤者 坚角清汁（已有，这里只补前两个）。</summary>
public class Res_GroupMitigationExtra : ISlotResolver
{

    /// <summary>
    /// 占星团减选哪一发。
    ///
    /// ★ 2026-10-04 修：原来用「取已解锁(太阳星座, 命运之轮)」。
    ///   太阳星座(37031) 必须在中间学派期间（身上有「太阳星座预备」3895）才能放，
    ///   无条件比较可用性 ⇒ 满级恒选太阳星座 ⇒ 中间学派没开时选出一个按不出去的技能。
    ///   现在：有 3895 才用太阳星座，否则用命运之轮（对照实现里命运之轮才是主团减）。
    /// </summary>
    private static uint 占星团减()
    {
        try
        {
            var 太阳 = SpellIds.取("太阳星座");
            var 命运 = SpellIds.取("命运之轮");

            // ══════════════════════════════════════════════════════════
            //  ★ 2026-10-15：**命运之轮就绪 ⇒ 让给它** ★
            //    （复刻 youshu `太阳星座.txt` 的 `IsReady(3613) ⇒ -7`）
            //
            //  [!] 参考里太阳星座在**命运之轮手上**时主动让路（`-7`）。
            //      但本项目是"**一条 resolver 只认一个技能**"（`技能` 属性先选出那一发），
            //      所以**不能只加 `-7`** —— 那会变成"太阳星座让路、命运之轮又没人排"
            //      的**整体卡死** ✗（差距审计的代理已经预见到这一点，此处按它的提醒落地）
            //      ⇒ 正确做法是在**选技层同向**：命运之轮就绪就选命运之轮。
            //
            //  [!] 这正是"让路 = `return -1`、提前 = 把它在队列里往前挪"那条语义
            //      在**选技层**的等价实现（返回值不参与仲裁，只有队列行号算优先级）。
            // ══════════════════════════════════════════════════════════
            if (命运 != 0 && SpellUtil.可用(命运)) return 命运;

            // 太阳星座必须在中间学派期间（身上有「太阳星座预备」3895）才能放；
            // 否则选中一个按不出去的技能。
            if (太阳 != 0 && CharacterExt.我有光环(3895)) return 太阳;

            if (命运 != 0) return 命运;
            return 太阳;
        }
        catch { return 0; }
    }

    private readonly JobSpellTable _t;

    public Res_GroupMitigationExtra(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job,
        学者: SpellIds.取("疾风怒涛之计"),
        占星: 占星团减());

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (HealQt.GetQt("减伤", true) == false) return -100;
        if (!HealSettings.Instance.自动减伤) return -101;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        // ══════════════════════════════════════════════════════════════════
        //  ★ 2026-10-15：**学者把「疾风怒涛之计」让给组合链**（防同帧双排）★
        //
        //  [!] 背景：学者的团减**组合链**（`Res_TeamMitigation.学者团减编排()`，
        //      按 youshu `自动减伤.GetAoeMitigationSpells` 的 IL 第 201~634 行逐条复刻）
        //      现在也会排 **25868 疾风怒涛之计** —— 组合②「罩子+跑快快」、
        //      组合④「跑快快+慰藉」、组合⑥「单发跑快快」。
        //      而本 resolver 也会**独立**排同一个 25868 ⇒ **同一帧双排**：
        //      第二发按不出去（CD 已进）或白占一个能力技窗口 ——
        //      等于把 90 秒 CD 烧成一张废牌 ✗
        //
        //  [!] 谁更权威：组合链是**逐条照 IL** 复刻的（含组合优先级、
        //      `pendingMitigation` 与"炽天使在场与否"的分支），所以学者这一路让给它。
        //      ⚠️ 只影响**学者**：占星的「命运之轮 / 太阳星座」仍走本 resolver ✓
        // ══════════════════════════════════════════════════════════════════
        if (_t.Job == Jobs.Scholar) return -1;

        // ══════════════════════════════════════════════════════════════════
        //  ★ 占星 命运之轮 / 太阳星座：**Boss 读条门 + `!849` 排除**（照抄 youshu IL）★
        //
        //  [!] 为什么要有这道门（缺口清单 ④）：
        //      原来这一路只看"时间轴 / `即将来大伤害()`（默认提前量 2.5 秒）"，
        //      ⇒ **本轮根本没有 AOE 读条**时也会把 60 / 120 秒的大招交掉 ✗
        //
        //  [!] IL 原文（`.il\cls_youshu\ACR.Astrologian.Resolvers.{命运之轮,太阳星座}.txt`）：
        //      · `命运之轮.txt` `IL_0065~IL_0073`：
        //          `HasBossCastingAoeWithin(6000)` 为**假** ⇒ `ldc.i4.s -2`（不放）
        //      · `太阳星座.txt` `IL_008b~IL_0099`：
        //          `HasBossCastingAoeWithin(10000)` 为**假** ⇒ `ldc.i4.s -5`（不放）
        //      · `太阳星座.txt` `IL_009a~IL_00ae`：
        //          自身 `Me.HasAura(849)` 为**真**（命运之轮在身）⇒ `ldc.i4.s -6`（不放）
        //          （`849` = 命运之轮，官方名见 `Common/减伤状态表.cs:37,278`）
        //      ⇒ 负数 = **不放（让路）**，正数才放行 —— 与本文件其余 return 同语义。
        //
        //  [!] 复用项：参考的 `HasBossCastingAoeWithin(毫秒)` 在我们这边是
        //      `减伤Helper.即将来大伤害(毫秒)`（`Common/MitigationHelper.cs:21`）；
        //      窗口常量 **6000 / 10000 照 IL 原值**，不做缩放。
        //  ⚠️ 只对**占星**生效（学者的 `疾风怒涛之计` 已在上一行让给组合链）✓
        // ══════════════════════════════════════════════════════════════════
        if (_t.Job == Jobs.Astrologian)
        {
            if (技能 == SpellIds.取("命运之轮"))
            {
                // IL_0065：`HasBossCastingAoeWithin(6000)` 为假 ⇒ -2
                if (!减伤Helper.即将来大伤害(6000)) return -2;
            }
            else if (技能 == SpellIds.取("太阳星座"))
            {
                // IL_008b：`HasBossCastingAoeWithin(10000)` 为假 ⇒ -5
                if (!减伤Helper.即将来大伤害(10000)) return -5;

                // IL_009a：自身 `849`（命运之轮）在身 ⇒ -6
                if (CharacterExt.我有光环(849)) return -6;
            }
        }

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

        // ★ 2026-10-04：已有减伤就不再叠（对照实现的 CurrentMitigation 快照）
        if (团减快照.已有减伤()) return -1;
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("群盾", true)) return -101;
        if (技能 == 0) return -102;
        // oGCD 队列深度闸门（参考口径 CanUseOffGcd(2)）
        if (!OffGcd闸门.可以排(2)) return -4;
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
        // oGCD 队列深度闸门（参考口径 CanUseOffGcd(2)）
        if (!OffGcd闸门.可以排(2)) return -4;
        if (!SpellUtil.已解锁(技能)) return -2;

        // ★ 2026-10-15：**每技能 QT**（学者「秘策」在 `ScholarACR.构建QT` 注册了独立开关）★
        //   [!] 关掉「秘策」⇒ 本地这条也一起失效（AI 候选集查的是同一个 `每技能通过`，
        //       两条路同时失效，不会出现"本地不奶、AI 还在建议"的失联）。
        //   [!] 未注册的技能（贤者「活化」）`每技能通过` 返回 true ⇒ 行为不变。
        if (!HealQt.每技能通过(技能)) return -4;

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
///
/// ⚠️⚠️ 2026-10-15 标注（全代码审查发现，**这是本项目 45 个 resolver 里唯一没有 `new` 的一个**）⚠️⚠️
///   [!] 事实：`new Res_HealAmp(...)` **全仓 0 处** ⇒ 本类**从未被注册进任何队列**
///       ⇒ `Check()/Build()` 永远不会被调用 ⇒ **这是一段死实现**。
///   [!] 连带后果（比"死代码"更值得注意）：它要放的 **`混合`（官方 24317 = 贤者的增疗
///       "所受的体力恢复效果提高 20%"）** —— 全仓**找不到任何施放点**
///       （`混合`/`24317` 只出现在 `SpellIds` / `AuraIds` / 减伤状态表 / 治疗阈值表 这些**表**里）
///       ⇒ **这个增疗技很可能从来没被放出去过**。
///   [!] 处理：**本次只标注，不擅自接线** —— 接上会让贤者所有治疗量多 20%（行为改动很大），
///       而且"该不该自动放 Zoe、什么时机放"必须**先有参考 IL / 官方依据**才能定
///       （本项目铁律：不猜）。**这条要由你决定**：接上（泽被式增疗）还是删掉这个空壳。
/// </summary>
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
        // oGCD 队列深度闸门（参考口径 CanUseOffGcd(2)）
        if (!OffGcd闸门.可以排(2)) return -4;
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

    /// <summary>
    /// Check 里选好的落点目标，给 Build 用（避免判 A 放 B）。
    ///
    /// [!] 只有贤者的魂灵风息用得上（它是**直线 AOE**，落点决定打几个）；
    ///     其余职业的候选是自身中心 / 地面放置技，这个字段保持 null。
    /// </summary>
    private static IBattleChara? 本帧落点;

    /// <summary>这一发是不是"直线落点分支"（决定 Build 要不要用 `本帧落点`）</summary>
    private static bool 本帧用落点;

    /// <summary>魂灵风息的直线尺寸（参考口径：矩形长 25、宽 3）</summary>
    private const float 直线长 = 25f;
    private const float 直线宽 = 3f;

    /// <summary>
    /// 占星 **大宇宙该不该等占卜**（120 秒团辅对齐）—— 逐条照抄 youshu
    /// `.il\cls_youshu\ACR.Astrologian.Resolvers.GCD.大宇宙.txt` 的
    /// `:: static bool ShouldWaitDivination()`。
    ///
    /// ── IL 原文（每条都标了 IL 偏移）──
    ///   · `IL_0000~IL_0010`：`Gcd(16552).Cooldown` → V0（16552 = 占卜）
    ///   · `IL_0011~IL_0021`：`V0.TotalMilliseconds` vs `ldc.r8 2000` + `bgt.un.s IL_0037`
    ///       ⇒ **大于 2000ms 直接 false**（占卜还早，不用等）
    ///   · `IL_0023~IL_0036`：`Me.HasAura(1878, 0)` 再 `ldc.i4.0 / ceq`
    ///       ⇒ **自身没有 1878**（占卜的团辅态）时为 **true**
    ///   ⇒ 判据 = `占卜 CD 剩余 <= 2000ms` **且** `!我有光环(1878)`
    ///
    /// [!] 返回值语义：IL 的两个调用点（`IL_00e6~IL_00ef`、`IL_0115~IL_011e`）
    ///     为真时都是 `ldc.i4.s -8` —— **负数 = 不放（让路）**，见下面调用处。
    ///
    /// [!] 为什么没有直接复用 `AstrologianACR.cs` 里的 `占卜临近()`：
    ///     那个是 `private static`（同类之外不可见），而且窗口是 **8000ms**
    ///     （攒爆发口径，见 `AstrologianACR.cs:1385`）——
    ///     与这里 IL 的 **2000ms** 不是同一个判据，
    ///     故按 IL 原值在本类内重建，避免把两个口径混成一个。
    /// </summary>
    private static bool ShouldWaitDivination()
    {
        try
        {
            var 占卜 = SpellIds.取("占卜");
            if (占卜 == 0) return false;

            var s = SpellUtil.Get(占卜);
            if (s == null) return false;

            // IL_0021 `bgt.un.s IL_0037`（> 2000 ⇒ false）
            if (s.Cooldown.TotalMilliseconds > 2000) return false;

            // IL_0023~IL_0036（`HasAura(1878, 0) == 0` ⇒ true）；1878 = 占卜团辅态
            return !CharacterExt.我有光环(AuraIds.占卜);
        }
        catch
        {
            return false;   // 读失败 → 不等（不拦）
        }
    }

    public int Check()
    {
        本帧落点 = null;
        本帧用落点 = false;

        // ★ 2026-10-04：已有减伤就不再叠（对照实现的 CurrentMitigation 快照）
        if (团减快照.已有减伤()) return -1;
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("群奶")) return -101;

        // ★ **我自己不能接受治疗时，别交自身中心的大群奶**（表 #30）
        //   [!] 参考里占星 大宇宙 在 Check 里判它（返回 -71）。
        //   [!] 这一族（大宇宙 / 整体论 / 全大赦 / 魂灵风息）的落点
        //       **必然包含我自己** ⇒ 我治不了自己时那一份是纯损失。
        if (AuraIds.我无法接受治疗()) return -71;

        foreach (var id in 候选())
        {
            if (id == 0 || !SpellUtil.已解锁(id) || !SpellUtil.可用(id)) continue;

            // ★ 职业级 QT（表 #66）—— 参考给大宇宙单开了一个开关
            if (id == SpellIds.取("大宇宙") && !HealQt.GetQt("大宇宙", true)) continue;

            // ★ 表外审计 W3：全大赦 与 节制 共享 5.5 秒互斥（参考 `全大赦.txt:40` 的 CanUseGroupMitigation）
            if (id == SpellIds.取("全大赦") && Res_TeamMitigation.白魔群减互斥中()) continue;

            // ══════════════════════════════════════════════════════════
            //  ★ 贤者 魂灵风息：**直线输出用法**（表 #107）★
            //
            //  [!] 它是**直线 AOE**（矩形 25×3），落点直接决定打几个 ——
            //      原来固定 `slot.Add(spell)`（自身中心 / 当前目标），
            //      在怪横排时经常只打到 1 个 ✗
            //
            //  [!] 参考实现（`贤炮.txt`）的完整分支：
            //      ① 落点找得到且覆盖 ≥ 2 个敌人 ⇒ **返回 2**（低优先，先治病）
            //      ② 高难模式外、落点覆盖 **≥ 6 个** ⇒ **返回 3**（爆发式群攻）
            //         —— 6 个是"这一发打满"的门槛，参考里它比"治病"低一档
            //      ③ "没人需要治"（`CannotReceiveHeal` / 奶人 QT 关掉）时，
            //         上面两条一旦成立就直接交（返回 2）
            //      ④ 都不成立 ⇒ 才看掉血人数（`魂灵风息阈值` 数人 ≥ 群奶人数）
            //
            //  [!] 我们只做**输出那一半**（①②④）：`CannotReceiveHeal` 那类
            //      "全队都治不了"的特殊语义我们没建模，硬套会改变治疗行为。
            // ══════════════════════════════════════════════════════════
            if (id == SpellIds.取("魂灵风息"))
            {
                var 落点 = 智能选目标.直线最优(直线长, 直线宽, 至少几个: 2);

                if (落点 != null && 落点.对象有效())
                {
                    var 命中 = 直线命中数(落点);
                    var s1 = HealSettings.Instance;

                    // ② 覆盖 ≥ 6：爆发式群攻，优先于普通治疗
                    if (命中 >= 6)
                    {
                        本帧落点 = 落点;
                        本帧用落点 = true;
                        return 3;
                    }

                    // ④ 掉血人数够 ⇒ 用这个落点交（既是群疗也是群攻）
                    var 血线1 = 治疗阈值表.取(id, s1.大招血线);
                    if (HealTargetHelper.低于阈值人数(血线1, 20f) >= s1.群奶最少人数)
                    {
                        本帧落点 = 落点;
                        本帧用落点 = true;
                        return 17;
                    }

                    // ① 覆盖 ≥ 2、但没人需要治 ⇒ 只在"要来了"时才交
                    var 要来了1 = TimelineManager.未来有减伤(3.0) || 减伤Helper.即将来大伤害();
                    if (要来了1)
                    {
                        本帧落点 = 落点;
                        本帧用落点 = true;
                        return 2;
                    }

                    continue;   // 这个候选这一拍不交，看下一个
                }
                // 落点找不到（没有 ≥2 个敌人的直线）⇒ 退回普通治疗判据
            }

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

            // ══════════════════════════════════════════════════════════
            //  ★ ③ 占星 大宇宙：**等占卜**（`ShouldWaitDivination`）⇒ `-8` 让路 ★
            //
            //  [!] IL 出处：`.il\cls_youshu\ACR.Astrologian.Resolvers.GCD.大宇宙.txt`
            //      · `IL_00e2~IL_00f2`（20 米敌人 ≥ 6）：`ShouldWaitDivination()` 真 ⇒ `-8`，假 ⇒ 60
            //      · `IL_0111~IL_0121`（≥ 3 且非高难、奶人 QT 关 / 不能接受治疗）：
            //          真 ⇒ `-8`，假 ⇒ 30
            //      ⇒ 我们只落地"**让路**"这一半（`-8`）——
            //        60 / 30 那两个正数在参考里是"关掉奶人后当 AOE 输出交"的分支，
            //        与我们的治疗槽位语义不同，不在本次范围（见缺口清单 ③）。
            //
            //  [!] 为什么加 `!人够多`：IL 的 `-8` 只出现在"群伤 / 输出"分支；
            //      真有人掉到阈值以下时（`人够多`）参考走的是补血分支（-1 / 44），
            //      不在这里等占卜 —— 我们保持"**急救不被团辅对齐拖住**"。
            //  [!] 负数 = 不放（让路），正数 = 放行 ⇒ 必须 `return -8` 而不是别的正数。
            //  ⚠️ 候选里只有占星有 `大宇宙`（`候选()` 按职业给），所以**只影响占星** ✓
            // ══════════════════════════════════════════════════════════
            if (!人够多 && 要来了 && id == SpellIds.取("大宇宙") && ShouldWaitDivination()) return -8;

            return 17;
        }

        return -1;
    }

    /// <summary>以某个落点为目标时，我→它那条直线能覆盖几个敌人</summary>
    private static int 直线命中数(IBattleChara 落点)
    {
        try
        {
            var 我 = CharacterExt.我的位置();
            var 方向 = 落点.Position - 我;
            if (方向.LengthSquared() < 0.01f) return 0;

            var 数 = 0;
            foreach (var 敌人 in Data.AllHostileTargets)
            {
                if (敌人 == null || !敌人.对象有效()) continue;
                if (敌人.CurrentHp <= 0) continue;
                if (敌人状态.攻击无效(敌人)) continue;
                if (智能选目标.在矩形内(我, 方向, 敌人.Position, 直线长, 直线宽)) 数++;
            }
            return 数;
        }
        catch
        {
            return 0;
        }
    }

    public void Build(Slot slot)
    {
        foreach (var id in 候选())
        {
            if (id == 0 || !SpellUtil.已解锁(id) || !SpellUtil.可用(id)) continue;

            var spell = SpellUtil.当前形态(id);
            if (spell == null) continue;

            // ★ 表外审计 W3：全大赦交出去就记互斥锁（参考 `全大赦.txt:87` 的 MarkGroupMitigation）
            if (id == SpellIds.取("全大赦")) Res_TeamMitigation.记白魔群减互斥();

            // ★ 直线落点分支（魂灵风息）：把 Check 选中的那个落点交给它
            if (本帧用落点 && 本帧落点 != null && 本帧落点.对象有效())
            {
                slot.Add(new Spell(spell.Id, 本帧落点));
                return;
            }

            slot.Add(spell);
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

        // ★ **我自己不能接受治疗时，别把地面技铺在自己脚下**（表 #30）
        //   [!] 参考里白魔 庇护所 / 铃铛 都在同一位置判它（返回 -7）。
        //   [!] 理由：这类技能的落点**必然包含我自己**，而我治不了自己的时候
        //       那一份效果是**纯损失**（不是少治一点，是零）。
        if (AuraIds.我无法接受治疗()) return -7;

        // ★ 2026-10-15：**1000ms 内不重放**（复刻 youshu `礼仪之铃.txt`：
        //   `RecentlyUsed(25862, 1000) → -200`）—— 它是 **90 秒 CD 的地面技**，
        //   连放等于白烧一个 CD（白魔差距审计优先项④）。
        //   [!] 本 resolver 只被白魔注册（`WhiteMageACR.cs:624`）⇒ 这条门不会误伤别职业。
        try
        {
            if (AEAssist.Helper.SpellExtension.RecentlyUsed(技能, 1000)) return -200;
        }
        catch { }

        // ★ 2026-10-04：补**血线触发** —— 原来只判"伤害要来" ✗
        //   对照实现的判据是「20 米内低于群奶阈值的人数 >= 群奶人数」✓
        var 铃铛血线 = 治疗阈值表.取(技能, HealSettings.Instance.群体治疗阈值);
        var 人够多 = HealTargetHelper.低于阈值人数(铃铛血线, 20f)
                     >= HealTargetHelper.群奶人数要求(HealSettings.Instance.群奶最少人数);

        if (!TimelineManager.未来有减伤(5.0) && !减伤Helper.即将来大伤害() && !人够多) return -1;

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
        // oGCD 队列深度闸门（参考口径 CanUseOffGcd(2)）
        if (!OffGcd闸门.可以排(2)) return -4;
        if (!SpellUtil.已解锁(技能)) return -2;

        var s = HealSettings.Instance;
        // ★ 2026-10-04：改读本技能阈值（表里 0.35），原来读大类 0.62 ✗
        if (HealTargetHelper.低于阈值人数(治疗阈值表.取(技能, s.群体治疗阈值), 20f)
            < HealTargetHelper.群奶人数要求(s.群奶最少人数)) return -1;

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
        if (!HealQt.GetQt("群盾", true)) return -101;
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

        // 有人需要治疗时才开（不然白开）；或蓝不够但有队友躺了（下一发复活免蓝）也开
        var 复活等蓝 = CharacterExt.我的当前蓝量() < 2400 && HealTargetHelper.待复活队友() != null;
        if (HealTargetHelper.低于阈值人数(HealSettings.Instance.单体治疗阈值) == 0 && !复活等蓝) return -1;

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

    // ══════════════════════════════════════════════════════════════════
    //  ★ **蛇胆消费的节流**（表 #118 / #119）★
    //
    //  [!] 贤者的 输血 / 白牛清汁 / 灵橡清汁 都吃**同一池蛇胆**（上限 3 颗）。
    //      没有互斥时，血崩那一瞬间三条路会在**几帧之内**连着交出去：
    //        输血 → 白牛清汁 → 灵橡清汁
    //      后两发常常打在**刚被第一发奶满的人**身上（第二发几乎全过量）✗
    //
    //  [!] 参考实现的做法（IL 直读 `自动单奶.SelectAction`）：
    //      消费顺序固定 **输血 → 白牛清汁 → 灵橡清汁**，
    //      并且带一个「**最近用过就不重复**」的窗口 —— **2000ms**。
    //
    //  [!] 本 resolver 覆盖了 `预铺单奶能力技`（白牛）与 `瞬发单奶能力技`（输血/白牛/灵橡）
    //      两个槽位，也就是**除灵橡溢出通道之外的全部蛇胆消费点**。
    //      溢出通道（`SGE_CholeOverflow`）单独判同一条节流。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// **这个技能吃不吃蛇胆**，以及"现在能不能吃"。
    ///
    /// [!] 吃蛇胆的只有贤者那三个：输血 24305 / 白牛清汁 24303 / 灵橡清汁 24296。
    ///     其余职业的同名槽位（学者 活性法 / 白魔 神名…）**不吃蛇胆**，恒放行。
    ///
    /// [!] 两条判据：
    ///      ① 蛇胆 &gt; 0（没豆子就放不出去 —— 挡的是"空按"）
    ///      ② `蛇胆节流.可以花()`（两次消费至少隔 2000ms —— 参考的时间窗）
    /// </summary>
    private static bool 蛇胆类可用(uint 技能Id)
    {
        try
        {
            if (!是蛇胆消费技(技能Id)) return true;   // 不吃蛇胆 ⇒ 不受这两条约束
            if (JobApiHelper.蛇胆 <= 0) return false;
            return 蛇胆节流.可以花();
        }
        catch
        {
            return true;
        }
    }

    /// <summary>这个技能是不是"花一颗蛇胆"的（输血 / 白牛清汁 / 灵橡清汁）</summary>
    private static bool 是蛇胆消费技(uint 技能Id)
    {
        if (技能Id == 0) return false;

        foreach (var id in new[]
                 {
                     SpellIds.取("输血"),
                     SpellIds.取("白牛清汁"),
                     SpellIds.取("灵橡清汁"),
                     // ★ 2026-10-15 补：**坚角清汁也是吃蛇胆的**（原来漏了它 ⇒ 既不记账也不过节流闸 ✗）
                     //   [!] 证据：`Res_Mitigation.cs:142` 自己的注释就写着
                     //       "前置：有蛇胆才有坚角清汁（它是蛇胆技，Lv50+）"；
                     //       而 `蛇胆节流.cs:11` 的注释把
                     //       `输血 → 白牛清汁 → 灵橡清汁 → **坚角清汁**` 列为**同一条节流链**。
                     //   [!] 后果（补之前）：坚角的消耗**不被 `记一次消费()` 记账**，
                     //       而 `蛇胆类可用()` 对不在白名单的技能**恒返回 true**
                     //       ⇒ 坚角**完全绕过节流** ⇒ 可与白牛清汁在几帧内**连花两颗蛇胆**
                     //       （节流存在的唯一意义就是防这件事）。
                     //   [!] 补进白名单后**两件事一起修好**（开发约定 F③：一条判据只有一处）：
                     //       ① `记蛇胆消费(坚角)` 开始记账；② `蛇胆类可用(坚角)` 自动开始过节流闸。
                     //       `Res_Mitigation` 那条编排路（坚角自己那边）另需补一次 `可以花()` 检查。
                     SpellIds.取("坚角清汁"),
                 })
        {
            if (id != 0 && id == 技能Id) return true;
        }

        return false;
    }

    /// <summary>花掉之后记账（`Build` 里调）</summary>
    private static void 记蛇胆消费(uint 技能Id)
    {
        if (是蛇胆消费技(技能Id)) 蛇胆节流.记一次消费();
    }

    /// <summary>
    /// 本帧是不是"**绿帽交不出去 → 改放秘策**"（复刻 youshu `深谋远虑之策.Check` 第 11 步）。
    ///
    /// [!] Check 里选定、Build 里照放（开发约定 F③：判 A 放 A）。
    ///     为真时 Build 放 **16542（秘策）且不带目标**（参考的 Build 就是 `Ability(16542)` 无参）。
    /// </summary>
    private static bool _秘策兜底;

    public Res_InstantHealAbility(JobSpellTable t) => _t = t;

    /// <summary>
    /// 解析本帧要用的瞬发单奶能力技。
    ///
    /// [!] **贤者三档**（表外审计 S4）：`输血 → 白牛清汁 → 灵橡清汁` 按「就绪」选第一个。
    ///     原来 `瞬发单奶能力技 => 取已解锁(输血,白牛,灵橡)` 恒取**输血**，
    ///     而输血是 120 秒大 CD —— 一转 CD，`可用()` 就恒假，整条 ② 直接跳过，
    ///     **白牛/灵橡 永远上不了台**（它们的 0.65 / 0.50 阈值也彻底读不到）✗
    ///
    /// [!] 参考 `自动单奶.txt:182-218` `SelectAction` 正是按「就绪」挑第一个，
    ///     每个技能各配各的阈值 —— 输血 70 / 白牛 65 / 灵橡 50（`治疗阈值表` 已登记）。
    ///
    /// [!] 其余职业的 `瞬发单奶能力技` 是单一技能位（神名/先天禀赋/活性法…），
    ///     直接返回，不受这条影响。
    /// </summary>
    private uint 解析瞬发技能(JobSpellTable t)
    {
        try
        {
            if (t.Job != Jobs.Sage) return t.瞬发单奶能力技;

            var 候选 = new[]
            {
                SpellIds.取("输血"),
                SpellIds.取("白牛清汁"),
                SpellIds.取("灵橡清汁"),
            };
            foreach (var id in 候选)
            {
                if (id != 0 && SpellUtil.已解锁(id) && SpellUtil.可用(id)) return id;
            }
            return 0;
        }
        catch { return 0; }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ★ 占星 天星交错（16556）—— **按 IL 顺序的五档** ★
    //
    //  出处：`.scratch/参考对比/youshu-占星减伤能力.md`「天星交错（技能 id 16556，
    //        能力技）」小节（逐条对应参考 IL，权威）；常量逐个照抄，不自造。
    //
    //  IL 的档位顺序（本项目只搬"选区 / 档位"这一段，前后门控沿用本项目既有约定）：
    //    ⑨ 出血档  : 30 码内可施法队友 **≤2** 且 `SelectBleedTarget()` 非空        → **60**
    //    ⑩ 自身低血: `Me.CurrentHpPercent < threshold` 且可对自己施放                 → **50**
    //    ⑪ 死刑档  : `HasBossCastingTankbusterWithin(10000)` 且 `SelectTankbusterTarget()` 非空 → **40**
    //    ⑫ 常规坦  : `SelectRegularTank(threshold)` 非空                             → **30**
    //    ⑬ 常规非坦: `SelectRegularNonTank(threshold)` 非空                          → **20**
    //    ⑭ 全落空  : **-1**（负数=不放；返回值不参与仲裁，队列行号才是优先级）
    //
    //  常量（逐个 IL 直读）：
    //    · 死刑窗口       = **10000**（ms，`HasBossCastingTankbusterWithin` 的实参）
    //    · 出血档血线     = **0.85f**（`CurrentHpPercent < 0.85f`）
    //    · 出血档人数上限 = **2**（`CastableAlliesWithin30.Count <= 2`）
    //    · threshold      = `Clamp(设置阈值/100f, 0f, 1f)`（`conv.r4` 后钳制）。
    //      本项目对应 `治疗阈值表.取(技能, 单体治疗阈值)` —— 表里登记的就是
    //      youshu 的 **70**（`治疗阈值表.cs:690`「youshu 天星交错阈值 70」）。
    //
    //  `CanCastCelestialIntersection(target)`（IL 直读，**含排除 2717**）：
    //    `CanReceiveHeal && IsInHealRange(target, 30f) && !HasAura(1889, 0) && HasAura(2717, 0) == 0`
    //  `CountMitigations(target)`（IL 直读）：`HasAura(2717) + HasAura(3890) + HasAura(3892)`，
    //    **< 2** 才入选（死刑档的目标筛选里用它）。3890 / 3892 的**名字 IL 无法确定** ⇒ 照裸 id 用。
    //
    //  ⚠️ **IL 无法确定 / 本项目无对应物 ⇒ 未实现**（不猜，逐条列出）：
    //    · `SelectBleedTarget` 里还有 `!HasDispelStatus` 与
    //      `CountAspectedBeneficBleedShields < 2` 两条子判据：后者这个"计数"由哪些 buff
    //      构成 **IL 无法确定** ⇒ 两条都不加，出血档只按「出血 + <0.85 + 可施」判。
    //    · `SelectRegularTank` 的 `NeedsLivingDeadHeal` 分支：IL 内部实现 **无法确定**
    //      ⇒ 未实现，常规坦档只按「血量 < threshold 且最低」判。
    //    · 两处"最低血"初值 IL 是 `2f`（不做血线门）—— 那个常量照抄。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>本帧五档选出的天星交错目标（Check 里选、Build 里放 —— 开发约定 F③）。</summary>
    private static IBattleChara? _天星本次目标;

    private const int 天星死刑窗口毫秒 = 10000;   // IL: HasBossCastingTankbusterWithin(10000)
    private const float 天星出血血线 = 0.85f;      // IL: CurrentHpPercent < 0.85f
    private const int 天星出血人数上限 = 2;        // IL: CastableAlliesWithin30.Count <= 2

    /// <summary>
    /// `CanCastCelestialIntersection(target)`（IL 直读）：
    /// 可治 + 30 码内（候选来自 `可治疗队友(30f)`，等价 `IsInHealRange(..., 30f)`）
    /// + 无 1889（天星交错）+ **无 2717（擢升）**。
    /// </summary>
    private static bool 天星可施(IBattleChara? 目标)
    {
        // ⚠️ 读游戏对象属性前必须判 `对象有效()`（全量防护审计红线）
        if (目标 == null || !目标.对象有效()) return false;
        try
        {
            if (!目标.活着() || !目标.可以治()) return false;
            if (目标.HasAura(AuraIds.天星交错)) return false;   // 1889：已经有这个盾
            if (目标.HasAura(2717)) return false;               // 2717：擢升 ⇒ 不叠（IL 直读）
            return true;
        }
        catch { return false; }
    }

    /// <summary>`CountMitigations(target)` = 2717 + 3890 + 3892 计数（IL 直读；3890/3892 名字 IL 无法确定）。</summary>
    private static int 天星减伤计数(IBattleChara? 目标)
    {
        if (目标 == null || !目标.对象有效()) return 0;
        try
        {
            var 数 = 0;
            if (目标.HasAura(2717)) 数++;   // 2717 = 擢升（`减伤状态表.cs` 有登记）
            if (目标.HasAura(3890)) 数++;   // 3890：名字 IL 无法确定
            if (目标.HasAura(3892)) 数++;   // 3892：名字 IL 无法确定
            return 数;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 天星交错的**五档**判定（IL 顺序，见上面那段说明）。
    /// 返回 60 / 50 / 40 / 30 / 20；全落空返回 **-1**（负数=不放）。
    /// 命中时把目标写进 `_天星本次目标`，供 `Build` 用（判 A 放 A）。
    /// </summary>
    private static int 天星交错档位(uint 技能)
    {
        _天星本次目标 = null;
        try
        {
            // threshold = Clamp(阈值/100f, 0f, 1f)（IL）；本项目从阈值表取（表里 = 70）
            var 阈值 = Math.Clamp(治疗阈值表.取(技能, HealSettings.Instance.单体治疗阈值), 0f, 1f);

            // 30 码内队友**物化一次**（高频路径：不反复枚举同一集合）
            var 队友 = HealTargetHelper.可治疗队友(30f);

            // ⑨ 出血档 → 60
            //    IL: `CastableAlliesWithin30.Count <= 2` 且 `SelectBleedTarget()` 非空
            if (队友.Count <= 天星出血人数上限)
            {
                foreach (var 成员 in 队友)
                {
                    if (!天星可施(成员)) continue;                 // 含 30 码 / 无 1889 / 无 2717
                    if (成员.血量比例() >= 天星出血血线) continue;   // IL: CurrentHpPercent < 0.85f
                    if (!AuraIds.有出血(成员)) continue;            // IL: HasBleed
                    _天星本次目标 = 成员;                          // IL: 取**第一个**满足者
                    return 60;
                }
            }

            // ⑩ 自身低血档 → 50
            //    IL: `Me.CurrentHpPercent < threshold` 且 `CanCastCelestialIntersection(Me)`
            var 我 = AEAssist.Core.Me;
            if (我 != null && 我.对象有效() && 我.血量比例() < 阈值 && 天星可施(我))
            {
                _天星本次目标 = 我;
                return 50;
            }

            // ⑪ 死刑档 → 40
            //    IL: `HasBossCastingTankbusterWithin(10000)` 且 `SelectTankbusterTarget()` 非空
            if (减伤Helper.boss要打坦克死刑(天星死刑窗口毫秒))
            {
                IBattleChara? 死刑目标 = null;
                var 死刑最低 = 2f;                            // IL: 最低血初值 2f（⇒ 不做血线门）
                foreach (var 成员 in 队友)
                {
                    if (!天星可施(成员)) continue;
                    if (!成员.IsTank()) continue;
                    if (天星减伤计数(成员) >= 2) continue;      // IL: CountMitigations < 2
                    var 血 = 成员.血量比例();
                    if (血 < 死刑最低) { 死刑最低 = 血; 死刑目标 = 成员; }
                }
                if (死刑目标 != null) { _天星本次目标 = 死刑目标; return 40; }
            }

            // ⑫ 常规坦 → 30 ；⑬ 常规非坦 → 20
            //    IL: `SelectRegularTank/NonTank(threshold)` = 「血量 < threshold」里最低者
            IBattleChara? 常规坦 = null; var 坦最低 = 2f;
            IBattleChara? 常规非坦 = null; var 非坦最低 = 2f;
            foreach (var 成员 in 队友)
            {
                if (!天星可施(成员)) continue;
                var 血 = 成员.血量比例();
                if (血 >= 阈值) continue;                      // IL: CurrentHpPercent < threshold
                if (成员.IsTank())
                {
                    if (血 < 坦最低) { 坦最低 = 血; 常规坦 = 成员; }
                }
                else
                {
                    if (血 < 非坦最低) { 非坦最低 = 血; 常规非坦 = 成员; }
                }
            }

            if (常规坦 != null) { _天星本次目标 = 常规坦; return 30; }
            if (常规非坦 != null) { _天星本次目标 = 常规非坦; return 20; }
        }
        catch { }

        _天星本次目标 = null;
        return -1;   // ⑭ IL: 全落空 → -1（负数=不放；返回值不参与仲裁）
    }

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
            // ★ 2026-10-15：**贤者「输血」用参考的固定选人链**（「当前目标的当前目标」）
            //   [!] 判据收在 `HealTargetHelper.输血目标()` 里**一处** ——
            //       本地这条路与 **AI 候选侧**（`候选集`）**同源**（开发约定 F③）。
            //   [!] 取不到（返回 null）时**回落到下面的既有选人**（坦克优先/最低血），
            //       不能就此不放 —— 否则输血会因"读不到目标"而静默失效。
            if (技能 == SpellIds.取("输血"))
            {
                var 输血靶 = HealTargetHelper.输血目标();
                if (输血靶 != null && !输血靶.有该技能的Buff(技能)) return 输血靶;
            }

            var 坦克 = HealTargetHelper.主坦();
            if (坦克 != null && !坦克.有该技能的Buff(技能) && !坦克.处于假死状态())
            {
                // ★ 2026-10-04：坦克也要过血线 —— 原来只要没这个 buff 就无条件返回
                //   ⇒ 坦克满血也吃（白费一次 60 秒 CD）
                var 坦克线 = 治疗阈值表.取(技能, 0.60f);   // 0.60 = 本文件原来那份预铺血线的基准值（保留原语义，同时接上本技能偏移）

                // ══════════════════════════════════════════════════════
                //  ★ **坦克死刑预判 ⇒ 满血也要预铺**（表 #38）★
                //
                //  [!] 血线判据在**死刑**这个场景下是错的：
                //      坦克满血吃死刑照样会掉一大截（甚至直接躺），
                //      而预铺技（绿帽 60% / 水流幕 / 天星交错 / 输血）的价值
                //      恰恰在"**伤害落地之前**把它挂上" —— 等它掉血再挂就晚了 ✗
                //
                //  [!] 参考实现就是这么分的：
                //      `HasBossCastingTankbusterWithin(...)` 是**独立于大伤害**的
                //      一个判据，它成立时预铺技**不看血线**。
                //
                //  [!] 范围：**只对死刑放宽**，不对普通掉血放宽 ——
                //      所以不是"坦克无条件吃"，45 秒 CD 还是保住了。
                // ══════════════════════════════════════════════════════
                if (减伤Helper.boss要打坦克死刑()) return 坦克;

                if (坦克.有效血量比例() <= 坦克线) return 坦克;
            }

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

        // ★ 2026-10-15：每帧先把"秘策兜底"标记清掉（有状态就得每帧重算，
        //   否则上一次为真会泄漏到下一次 Check —— 开发约定 F① 同源）
        _秘策兜底 = false;

        // ★ 占星 天星交错：同样每帧清（`天星交错档位()` 命中时才会重新写入）——
        //   否则上一帧的目标会泄漏给 Build（F①）
        _天星本次目标 = null;

        // ★ 2026-10-04：**刚有人消耗过豆子 ⇒ 这一拍别再消耗** ✓
        //   本项目采用「一次消耗后全局抑制」的口径（以太层数下降后 7 秒）——
        //   避免同一拍把豆子连打光、也避免两个能力技互相抢（两套对照实现都有等价物）
        if (以太管理.抑制中) return -3;
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("单奶")) return -101;

        // ⚠️ **能力技队列深度闸门**（对照分析发现的缺口）——
        //    ⚠️ 加在 ①② 之前，两条路都管住（加在后面只管得住后一条）。
        //    [!] 上限**按职业走**（`OffGcd闸门.默认上限()`）：
        //        贤者是 2，其余职业是 1 —— 见 `OffGcd闸门` 的说明。
        if (!OffGcd闸门.可以排(OffGcd闸门.默认上限())) return -1;

        // ── ① 预铺类 ──
        var 预铺 = _t.预铺单奶能力技;

        // ══════════════════════════════════════════════════════════════
        //  ★ 占星 天星交错（16556）：**先走 IL 的五档**（60/50/40/30/20）★
        //
        //  [!] 原来这一发只有 `预铺目标()` 的**单档**（坦克/其余都 0.60）——
        //      出血档（<0.85）与死刑档（10000ms 内 Boss 读条）**根本进不来** ✗
        //      ⇒ 五档全部由 `天星交错档位()` 判，命中哪一档就 return 那一档。
        //
        //  [!] 五档全落空时**不在这里 return**（IL 是 -1 让路）：继续往下走 ——
        //      本项目把「预铺 + 瞬发」合并在同一个 resolver（② 里的先天禀赋
        //      **没有别的 resolver** 负责），这里直接 return -1 会把先天禀赋一起挡掉 ✗
        //      ⇒ 落到下面的通用预铺路（它只在 `预铺目标()` 非空时才成立）。
        //      两条路都是"放 16556"，**同一帧内选区同源**（开发约定 F③）。
        // ══════════════════════════════════════════════════════════════
        var 是占星交叉 = _t.Job == Jobs.Astrologian && 预铺 != 0 && 预铺 == SpellIds.取("天星交错");
        if (是占星交叉 && HealQt.每技能通过(预铺) && SpellUtil.已解锁(预铺) && SpellUtil.可用(预铺)
            && 必须奶满.找目标() == null && HealTargetHelper.低于阈值人数(0.30f) == 0)
        {
            var 档 = 天星交错档位(预铺);
            if (档 > 0) return 档;
            // 五档全落空：交给下面的通用预铺路 / ② 瞬发路（见上面的说明）
        }

        if (预铺 != 0 && HealQt.每技能通过(预铺) && SpellUtil.已解锁(预铺) && SpellUtil.可用(预铺) && 预铺目标(预铺) != null)
        {
            // ⚠️ 有人濒危时让路给急救（`Res_HealEmergency` 排在 OffGcd 趟，
            //    这里只保证不跟"必须奶满/急救"抢目标）
            if (必须奶满.找目标() == null && HealTargetHelper.低于阈值人数(0.30f) == 0)
            {
                if (!蛇胆类可用(预铺)) return -3;

                // ══════════════════════════════════════════════════════════
                //  ★ 2026-10-15：**白魔「水流幕」的两条可确证硬门** ★
                //    （复刻 youshu `ACR.WhiteMage.Resolvers.Ability.水流幕.txt`）
                //
                //  [!] IL 里可确证的两条：
                //        · QT「**减伤**」为假 → **-100**
                //        · `RecentlyUsed(140 天赐祝福, 3000)` → **-1**
                //          （刚交过天赐这种"一次到满"的救命技，这一拍不必再补水流幕）
                //
                //  [!] ⚠️ **它的选人 Helper `AquaVeilTarget()` 内部实现 IL 无法确定** ⇒
                //      **目标选择不复制**，沿用本 resolver 既有的预铺选人（坦克优先 / 最低血）。
                //      能证实的照抄、证不了的标注 —— 不是省事。
                //
                //  [!] 只对**白魔的水流幕**生效 —— 同一预铺槽上的学者绿帽(7434) / 贤者输血
                //      **不受影响**（它们的判据各自照 IL 走）。
                //
                //  [!] 顺带更正一条措辞：`水流幕` 与 `神祝祷` **不是同一个槽位** ——
                //      前者在 `预铺单奶能力技`（`WhiteMageACR.cs:391`），后者在 `单体盾`（`:446`）+
                //      `个人减伤`（`:432`）；两槽的分工见 `WhiteMageACR.cs:386-389` 的说明。
                // ══════════════════════════════════════════════════════════
                if (预铺 == SpellIds.取("水流幕"))
                {
                    if (!HealQt.GetQt("减伤", true)) return -100;

                    try
                    {
                        if (AEAssist.Helper.SpellExtension.RecentlyUsed(SpellIds.取("天赐祝福"), 3000))
                            return -1;
                    }
                    catch { }
                }
                // ★ 学者绿帽（深谋远虑之策）吃以太：没豆子 = 放不出去（参考 Scholar_Lustrate 的 Aetherflow 闸）
                if (以太管理.以太治疗技没豆子(预铺))
                {
                    // ══════════════════════════════════════════════════════
                    //  ★ 2026-10-15：**秘策联动**（复刻 youshu `深谋远虑之策.Check`）★
                    //
                    //  [!] youshu IL 的分支次序（逐条直读 `深谋远虑之策.txt`）：
                    //        ① `HasAetherflow && CanSpendAetherflow`：
                    //             `IsReady(7434,1)` 真 → chosen=7434（20）；假 → **-200**（放弃，不退秘策）
                    //        ② 自身带 `1896`（秘策 buff = 下一发治疗必暴击）且 `IsReady(7434,1)`
                    //            → chosen=7434（20）  ← **免费那一发**
                    //        ③ QT「秘策」且 `IsReady(16542,1)` → chosen=**16542**（5）
                    //        ④ 否则 → -2
                    //      `Build`：chosen==16542 → `Ability(16542)`（**不带目标**，打自己）；
                    //                否则 → `Ability(7434, target)`
                    //
                    //  [!] 我们原来的缺口：`没豆子 → return -3` 就结束了，
                    //      秘策完全由独立 resolver（`Res_HealBooster`，按掉血人数/大伤害触发）负责
                    //      ⇒ **与绿帽脱钩**：这里明明有预铺目标，却什么都不会发生 ✗
                    //
                    //  [!] 本项目的结构差异（已知、有意保留）：
                    //      参考是"一个技能一条 resolver"，可以从容 `return -200`；
                    //      我们是"预铺 + 瞬发"合并成一条 resolver（`Res_InstantHealAbility`），
                    //      所以**"有豆但绿帽 CD 中"这一种情况我们不 return**，
                    //      而是落到下面的瞬发分支（同一拍还有活性法可用）——
                    //      这比参考的 `-200` 更宽松，是本项目 resolver 合并的必要差异。
                    // ══════════════════════════════════════════════════════
                    var 秘策 = SpellIds.取("秘策");

                    // ② 身上已有「秘策」(1896) ⇒ 这一发绿帽免费，照放
                    //    （绿帽可用已由外层 if 条件保证：外层要求 `SpellUtil.可用(预铺)`）
                    if (秘策 != 0 && CharacterExt.我有光环(AuraIds.秘策)) return 26;

                    // ③ QT「秘策」开 + 16542 就绪 ⇒ 补一发秘策（不带目标），留给后面的治疗
                    //    ⚠️ 参考靠 `IsReady(16542,1)` 隐式排除"已经在身上"的情况。
                    //  ★ 2026-10-15 修（**删掉一个恒真的 no-op**，全代码审查发现）：
                    //    这里原来还有 `&& !CharacterExt.我有该技能的Buff(秘策)`，但**它恒为真** ——
                    //    因为"秘策已在身"的情况**上面 `:1896` 已经 `return 26`** 了
                    //    （复核确认 `我有该技能的Buff(16542)` 与 `我有光环(AuraIds.秘策=1896)` 同源）。
                    //    ⇒ no-op 不但没用，还会让人以为"这里另有一层防重复挂 buff 的保护"。
                    //    ⇒ 判据归一到 `:1896` 一处（开发约定 F③：一事一判据）。
                    //      行为**完全不变**（原条件不可达为假）。
                    if (秘策 != 0 && HealQt.每技能通过(秘策) && SpellUtil.可用(秘策))
                    {
                        _秘策兜底 = true;
                        return 25;
                    }

                    return -3;
                }
                return 26;
            }
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
        var 瞬发 = 解析瞬发技能(_t);
        if (!HealQt.每技能通过(瞬发)) return -4;   // 每技能 QT（活性法被关）
        if (瞬发 != 0)
        {
            // ★ **蛇胆互斥**（表 #118 / #119）：两次蛇胆消费至少隔 2000ms ——
            //   不带这条时，血崩瞬间 输血 → 白牛 → 灵橡 会在几帧内连着倒出去，
            //   后两发常常打在**刚被第一发奶满的人**身上（几乎全过量）。
            if (!蛇胆类可用(瞬发)) return -3;
            // ★ 学者活性法吃以太：没豆子 = 放不出去（参考 Scholar_Lustrate 的 Aetherflow 闸）
            if (以太管理.以太治疗技没豆子(瞬发)) return -3;

            // ★ 血线改成**按技能查**（`治疗阈值表`），查不到才回落到职业表的 `瞬发单奶血线` ★
            //   [!] 参考实现的阈值是**每技能一个**（IL 实证）：
            //         shiyuvi Lustrate 0.45 / Adloquium 0.4 / FeyBlessing 0.6 …
            //         youshu  活性法 45 / 单盾 45 / 绿帽 60 / 不屈 70 / 祥光 70 …
            //       ==> 统一一个 `瞬发单奶血线` 表达不了这种分层。
            //   [!] 但**这只是第一步**：真正的结构差异是「参考按技能选区，我们先选区再选技能」，
            //       那个改动更大，留到单独一轮（见 `复刻边界-参考实现与AI层.md`）。
            var 单奶血线 = 治疗阈值表.取(瞬发, _t.瞬发单奶血线);

            // ══════════════════════════════════════════════════════════
            //  ★ 充能保留 / 溢出放宽 / 同目标去重（占星「先天禀赋」）★
            //
            //  [!] 三条都是**只有占星能对上号**的（它登记成 `瞬发单奶能力技`），
            //      所以按 id 判定，白魔/学者的同名槽位不受影响。
            //
            //   ① **保留充能**：先天禀赋 40 秒 CD、**2 层**。
            //      只有 1 层就交掉 ⇒ 真到死刑 / 血崩时手里一张牌都没有。
            //      默认保留 1 层。
            //   ② **充能 > 2 时阈值 +0.10**：层数溢出就是浪费，这时候放宽血线早花掉。
            //   ③ **同一目标 1000ms 内不重复交**：第二发几乎全过量。
            // ══════════════════════════════════════════════════════════
            if (是先天禀赋(瞬发))
            {
                // ★ 职业级 QT（表外审计）：参考给先天禀赋单开了两个开关
                //   [!] `自动先天` 关掉 = **完全不自动交**（这一条整条让路）
                //   [!] `保留先天` 关掉 = **不考虑保留数**（有几颗交几颗，
                //       只看血线）—— 对应参考 `CanSpendAddersgallStatic` 里
                //       `if (!GetQt("保留蓝豆")) return true;` 那条短路
                if (!HealQt.GetQt("自动先天", true)) return -5;

                if (HealQt.GetQt("保留先天", true))
                {
                    if (!充能够花(瞬发)) return -5;
                }

                if (充能过剩(瞬发)) 单奶血线 += 0.10f;
            }

            // ══════════════════════════════════════════════════════════════
            //  ★ 2026-10-15：**天赐祝福 ↔ 神名 3000ms 互斥**（复刻 youshu `神名.txt`）★
            //
            //  [!] youshu IL：`RecentlyUsed(140, 3000) → -1`
            //      **140 = 天赐祝福**（官方 `Action.csv` 核验）—— 也就是
            //      "3 秒内刚交过天赐 ⇒ 神名这一发不要再交"。
            //      参考还用同一条打**同目标**内置 CD（`神名_ + 目标Id`），那是第二层。
            //
            //  [!] 为什么需要：天赐(180 秒 CD) 与 神名(60 秒 CD、2 层充能) 都是"大单奶"，
            //      3 秒内连交等于把两张牌压在**同一次掉血**上 ——
            //      第二发几乎必然过量，而天赐的 CD 是最贵的那一档。
            //
            //  [!] 我们原来完全没有这条（审计 A 项 1 确认）：全库只有
            //      `Res_InstantHealAbility` 里 1500ms 的**按目标去重**
            //      与白魔 5500ms 的**团减**互斥（节制↔全大赦），
            //      **没有**这两发单奶之间的互斥。
            //
            //  [!] 用参考同款的 `SpellExtension.RecentlyUsed`（读游戏自己的施法历史）——
            //      不用 `本地施放记录.刚放过`：那个只认 `记()` 写入的**技能级**字典，
            //      而本 resolver 的 Build 只调了 `记目标()`（另一个字典），
            //      拿它判会恒为 false（又一个静默失效）。
            // ══════════════════════════════════════════════════════════════
            if (瞬发 == SpellIds.取("神名"))
            {
                try
                {
                    if (AEAssist.Helper.SpellExtension.RecentlyUsed(SpellIds.取("天赐祝福"), 3000))
                        return -1;
                }
                catch { }
            }

            // ══════════════════════════════════════════════════════════════
            //  ★ 神名（Tetra）**候补人数上限**（表外审计 W4）★
            //
            //  [!] 参考 `神名.txt:43-44` 用的是 `SingleAbilityHealTarget(神名阈值, "单奶限制", 2)`：
            //      比 `天赐` 的 `AbilityHealTarget(阈值, 开关)` 多一个 `2` ——
            //      语义是「低于阈值的候选人数 > 2 就**别交**」。
            //      道理：单发瞬发奶**奶不起一片**，五六个人都掉血时该让群体治疗上。
            //
            //  [!] 我们原来 `最低血量队友(单奶血线)` 只挑最危险的那一个，
            //      **从不管下面还压着几个人** ⇒ 神名在团崩时反而抢着交 ✗
            // ══════════════════════════════════════════════════════════════
            if (瞬发 == SpellIds.取("神名"))
            {
                try
                {
                    if (HealTargetHelper.低于阈值人数(单奶血线, 30f) > 2) return -1;
                }
                catch { }
            }

            var 目标 = HealTargetHelper.最低血量队友(单奶血线);
            if (目标 != null && !目标.处于假死状态() && 必须奶满.找目标() == null)
            {
                if (是先天禀赋(瞬发) && 同目标冷却中(目标)) return -200;

                // ★ 通用"按目标去重"（表 #63）：
                //   同一发单体能力技对**同一个人**连放两发 ⇒ 第二发几乎全过量。
                //   ⚠️ 键是 **(技能, 目标)** 而不是只按技能 ——
                //      只按技能会把"另一个人的那一发"也挡掉（那是真 bug）。
                if (本地施放记录.刚放过目标(瞬发, (uint)目标.GameObjectId)) return -201;

                return 24;
            }
        }

        return -1;
    }

    /// <summary>
    /// 这个"瞬发单奶能力技"是不是占星的**先天禀赋**（只有它吃充能那套规则）。
    /// </summary>
    private static bool 是先天禀赋(uint id) => id != 0 && id == SpellIds.取("先天禀赋");

    /// <summary>
    /// 充能够花吗 —— 花掉一发之后还剩 `保留先天数量` 层以上。
    ///
    /// [!] 读不到充能（返回 -1）时**放行**：宁可偶尔多花一层，
    ///     也不要因为接口抽风让救命技能整个失效（保守方向取"放行"）。
    /// </summary>
    private static bool 充能够花(uint id)
    {
        try
        {
            var 现有 = CharacterExt.充能数(id);
            if (现有 < 0) return true;

            var 保留 = Math.Clamp(HealSettings.Instance.保留先天数量, 0, 3);
            return 现有 > 保留;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>充能超过 2 层 ⇒ 再不用就要溢出，阈值放宽 0.10</summary>
    private static bool 充能过剩(uint id)
    {
        try { return CharacterExt.充能数(id) > 2; }
        catch { return false; }
    }

    /// <summary>
    /// 同一目标 1000 毫秒内刚交过先天禀赋吗。
    ///
    /// [!] **必须按目标记**（不能只记"最近一次"）——
    ///     两个人都掉血时，只记全局会把另一个人的那一发也一起挡掉。
    /// </summary>
    private static bool 同目标冷却中(IBattleChara 目标)
    {
        // ★ 入口判有效性：读游戏对象属性会因【已释放对象】触发原生访问违例（穿 catch）
        if (目标 == null || !目标.对象有效()) return false;

        try
        {
            return _先天上次目标 == (uint)目标.GameObjectId
                   && TimeHelper.Now() - _先天上次时刻 < 1000;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>先天禀赋的"上次交给谁、什么时候"</summary>
    private static uint _先天上次目标;
    private static long _先天上次时刻;

    private static void 记先天(uint id)
    {
        _先天上次目标 = id;
        _先天上次时刻 = TimeHelper.Now();
    }

    /// <summary>战斗重置 / 换本时清（有状态就得清）</summary>
    public static void 重置先天记录()
    {
        _先天上次目标 = 0;
        _先天上次时刻 = 0;
        _天星本次目标 = null;   // ★ 占星 天星交错的目标也是状态 ⇒ 一起清（开发约定 F①）
    }

    public void Build(Slot slot)
    {
        // ⚠️ 必须和 Check 同源（开发约定 F③）：判谁就放谁，顺序也必须一致。

        // ★ 2026-10-15：**秘策兜底**（Check 里选定）—— 放 16542，**不带目标** ★
        //   [!] 参考 `深谋远虑之策.Build`：`chosenActionId == 16542` ⇒ `Ability(16542)`（无参）
        //       ⇒ 目标为自身/默认，**不是**那个预铺目标（秘策是给自己挂的 buff）。
        if (_秘策兜底)
        {
            _秘策兜底 = false;
            var 秘策 = SpellIds.取("秘策");
            if (秘策 != 0)
            {
                var sp = SpellUtil.Get(秘策);
                if (sp != null) { slot.Add(sp); return; }
            }
        }

        var 预铺 = _t.预铺单奶能力技;
        if (预铺 != 0 && HealQt.每技能通过(预铺) && SpellUtil.已解锁(预铺) && SpellUtil.可用(预铺))
        {
            // ★ 2026-10-15 修：**Build 也要过 Check 那两道「水流幕」硬门**（判 A 放 A / F③）★
            //   [!] 问题：Check 在 `预铺 == 水流幕` 时判了 QT「减伤」（-100）与
            //       `RecentlyUsed(140 天赐祝福, 3000)`（-1，见 Check 里 1823 起那段注释），
            //       而 **Build 这里是自己重新选 `预铺`** 的，只判了 每技能通过 / 已解锁 / 可用
            //       ⇒ **可以放出 Check 从未批准的水流幕**（判 A 放 B），
            //         并且**绕过用户关掉的「减伤」开关** ✗
            //   [!] 判据只此一份：这里用与 Check **逐字相同**的两条表达式，不另写一套。
            if (预铺 == SpellIds.取("水流幕"))
            {
                if (!HealQt.GetQt("减伤", true)) return;
                try
                {
                    if (AEAssist.Helper.SpellExtension.RecentlyUsed(SpellIds.取("天赐祝福"), 3000)) return;
                }
                catch { }
            }

            if (必须奶满.找目标() == null && HealTargetHelper.低于阈值人数(0.30f) == 0)
            {
                // ★ 占星 天星交错：**优先用 Check 里五档选定的目标**（同一来源，F③）；
                //   若 Check 走的是通用预铺路（五档未命中），`_天星本次目标` 为空 ⇒
                //   回落到同一个 `预铺目标()`（两处判据同源）。
                var 目标 = (_t.Job == Jobs.Astrologian && 预铺 == SpellIds.取("天星交错") && _天星本次目标 != null)
                    ? _天星本次目标
                    : 预铺目标(预铺);
                _天星本次目标 = null;   // ★ 有状态就得清（开发约定 F①）
                if (目标 != null)
                {
                    var s = SpellUtil.当前形态(预铺);
                    if (s != null) { slot.Add(new Spell(s.Id, 目标)); 记蛇胆消费(预铺); return; }
                }
            }
        }

        var 瞬发 = 解析瞬发技能(_t);
        if (瞬发 != 0 && HealQt.每技能通过(瞬发))
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

                // ⚠️ **必须和 Check 用同一条血线**（含充能过剩的 +0.10 与保留判定），
                //    否则 Check 过了 Build 找不到目标 —— 就是"判 A 放 B"。
                if (是先天禀赋(瞬发))
                {
                    if (!HealQt.GetQt("自动先天", true)) return;
                    if (HealQt.GetQt("保留先天", true) && !充能够花(瞬发)) return;
                    if (充能过剩(瞬发)) 单奶血线 += 0.10f;
                }

                var 目标 = HealTargetHelper.最低血量队友(单奶血线);
                if (目标 != null && !目标.处于假死状态())
                {
                    if (是先天禀赋(瞬发) && 同目标冷却中(目标)) return;
                    if (本地施放记录.刚放过目标(瞬发, (uint)目标.GameObjectId)) return;

                    // ══════════════════════════════════════════════════════
                    //  ★ **混合（Krasis）要和这一发单奶同目标**（表 #128）★
                    //
                    //  [!] 参考（`自动单奶.txt` IL 第 120~145 行 + Build 第 8~33 行）：
                    //       `useKrasis` 的判据是**针对这一发选出来的 target**：
                    //         · 混合(24317) 对该目标 `IsReady`
                    //         · 该目标身上**没有** 2622（混合的 buff）
                    //         · 该目标的血量 **≤ `GCD单奶阈值`**
                    //       成立 ⇒ `Build` 里**先塞混合、再塞那一发单奶**，
                    //       两个动作**同一个 target** ✓
                    //
                    //  [!] 我们原来把混合做成了一条**独立的 resolver**（`Res_HealAmp`），
                    //       目标写死 `主坦()` ⇒ 两个问题：
                    //         ① 混合加在坦克身上，而这一发单奶可能是治疗别人
                    //            ⇒ 增疗加成了个**没人被治的人** ✗
                    //         ② 坦克满血时它靠"要来"这个弱判据照样交 ✗
                    //
                    //  [!] 现在按参考：**先选单奶目标，再决定要不要给它配混合** ——
                    //       目标同源，条件同源（F③）。
                    // ══════════════════════════════════════════════════════
                    var 混合 = SpellIds.取("混合");
                    var 该配混合 = false;
                    try
                    {
                        该配混合 = 混合 != 0
                                   && SpellUtil.已解锁(混合)
                                   && SpellUtil.可用(混合)
                                   && !目标.HasAura(2622)
                                   && 目标.有效血量比例() <= 混合血线();
                    }
                    catch { }

                    var s = SpellUtil.当前形态(瞬发);
                    if (s != null)
                    {
                        if (该配混合)
                        {
                            var 增疗 = SpellUtil.Get(混合);
                            if (增疗 != null) slot.Add(new Spell(增疗.Id, 目标));
                        }

                        slot.Add(new Spell(s.Id, 目标));
                        if (是先天禀赋(瞬发)) 记先天((uint)目标.GameObjectId);
                        记蛇胆消费(瞬发);

                        // ★ 按目标记账（和 Check 同源）
                        try { 本地施放记录.记目标(瞬发, (uint)目标.GameObjectId); } catch { }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 配混合的血线（参考的 `GCD单奶阈值`）。
    ///
    /// [!] 参考拿它和"这一发要治的那个人的血量"比；
    ///     我们表里 `混合` 登记的是 `单体治疗阈值` 那一档，查不到就回落。
    /// </summary>
    private static float 混合血线()
    {
        try
        {
            var v = 治疗阈值表.取(SpellIds.取("混合"), HealSettings.Instance.单体治疗阈值);
            return v > 0f ? v : HealSettings.Instance.单体治疗阈值;
        }
        catch
        {
            return HealSettings.Instance.单体治疗阈值;
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

      /// <summary>
      /// 拯救的目标（按参考的 `ResolvePepsisTarget` 逐条对齐）：
      ///   ① 30 米内、**能接受治疗**、**能做单体治疗目标**
      ///   ② 身上有**心关**（2605「关心」）—— 拯救强化的是心关的回血，没心关就无收益
      ///   ③ 身上**没有**拯救 buff(2610)
      ///   ④ 有效血量比例 **≤ 0.70**（本技能登记阈值）
      ///   ⑤ 取其中**最低**的那个
      ///
      ///  [!] 修的是表 **#114**："心关在自己身上 ⇒ 拯救永不交"。
      ///      `心关` 挂在自己身上时，自己身上是 **2604**（不是队友的 2605）；
      ///      而原来的判据只查 2605 ⇒ 自己永远不合格 ⇒ `拯救目标 == null` ⇒
      ///      **拯救一次都交不出去** ✗
      ///
      ///  [!] 所以这里**把"我自己"也算进候选**（当 2604 在身时）——
      ///      与参考的 `CastableAlliesWithin30` 行为一致（它只排除敌人，不排除自己）。
      ///
      ///  [!] 另一个 bug：原来把"我有这个 buff"写在 `Check()` 里（`-3`）——
      ///      那是否决**整条** resolver，而不是"换个人"。现在改成**按目标过滤**：
      ///      已经带着拯救的那个人跳过，别人照样能吃到。
      /// </summary>
      private IBattleChara? 拯救目标
      {
          get
          {
              try
              {
                  var 阈值 = 治疗阈值表.取(技能, 0.70f);
                  IBattleChara? 最好 = null; var 最低 = float.MaxValue;

                  // ① 自己：只有"心关在自己身上"（2604）时才参与
                  if (AuraIds.我有心关())
                  {
                      var 我 = Core.Me;
                      if (我 != null && 我.对象有效() && 我.活着() && 我.可以治()
                          && !(我.HasAura(2610)))
                      {
                          var 比0 = 我.有效血量比例();
                          if (比0 <= 阈值) { 最低 = 比0; 最好 = 我; }
                      }
                  }

                  // ①②③④ 队友
                  foreach (var a in HealTargetHelper.可治疗队友(30f))
                  {
                      if (a == null || !a.对象有效() || !a.活着()) continue;
                      if (!a.可以治()) continue;
                      if (!AuraIds.有心关(a)) continue;      // 2605：只认我挂的那一份
                      if (a.HasAura(2610)) continue;         // 已有拯救 ⇒ 换别人

                      var 比 = a.有效血量比例();
                      if (比 > 阈值) continue;
                      if (比 < 最低) { 最低 = 比; 最好 = a; }
                  }

                  return 最好;
              }
              catch { return null; }
          }
      }
    private readonly JobSpellTable _t;

    public Res_KardiaBoost(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job, 贤者: SpellIds.取("拯救"));

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("单奶")) return -101;   // ★ 参考查「奶人 + 单奶」两个 QT
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        // ★ 能力技队列深度闸门（参考用 `CanUseOffGcd(2)`）
        if (!OffGcd闸门.可以排(2)) return -4;

        // 有人明显掉血的时候开
        // ★ 2026-10-04：**必须选区** —— 拯救的收益全在「关心真的在回血」上 ✗
        //   原来无目标施放 + 只看「全局有人低于阈值」⇒ 关心在自己身上时完全无收益 ✗
        //   ⚠️ 原来这里还有一条 `我有该技能的Buff → -3`，那是**否决整条** ✗
        //      已挪进 `拯救目标` 的按目标过滤（见那里的注释）。
        if (拯救目标 == null) return -1;

        return SpellUtil.可用(技能) ? 7 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (拯救目标 != null) slot.Add(new Spell(spell.Id, 拯救目标));
    }
}

/// <summary>转化：学者 转化（牺牲小仙女换 3 颗以太）—— 以太见底且小仙女在场上时用。</summary>
public class SCH_Dissipation : ISlotResolver
{
    private static uint 技能 => SpellIds.取("转化");

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        // ★ 2026-10-15 修（**读错开关名**）：原来是 `GetQt("以太超流", true)` ✗
        //   [!] 「以太超流」那个开关（`ScholarACR.cs:993` 注册，默认 true）**管的是另一个技能**
        //       —— 以太超流(Aetherflow) 自己的 oGCD。
        //       而本类管的是 **转化**（`SCH_Dissipation`：牺牲小仙女换 3 颗以太），
        //       它的开关是 **「自动转化」**（`HealerEntryBase.cs:368` 注册，**默认 false**）。
        //   [!] 后果（修前）：
        //       ① 用户关掉「自动转化」**对这条路完全无效**（假开关）✗
        //       ② 用户为了停掉"以太超流"而关掉那个开关时，会**连带静默禁掉转化** ✗
        //          （两个不相干的技能被同一个开关控制。）
        //   [!] 修法：改读**它自己的**开关，判据与同概念的另一实现**同源** ——
        //       `ScholarDissipation.cs:33` 就是 `GetQt("自动转化", false)`（开发约定 F③）。
        //       默认值 `false` 与**注册默认值一致**（注册也是 false）⇒ 行为不变、开关变真。
        if (!HealQt.GetQt("自动转化", false)) return -101;
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
