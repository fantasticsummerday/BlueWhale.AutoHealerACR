using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;
using AEAssist.MemoryApi;

namespace HealerACR.Common;

// 从 SpellUtil.cs 拆出来的（那个文件里有两个类，
// 导致我三次把方法加错类 —— 拆开就不会再混了）。

public static class CharacterExt
{
    public static float 血量比例(this IBattleChara c)
        => c.MaxHp == 0 ? 0f : c.CurrentHp / (float)c.MaxHp;

    public static float 蓝量比例(this IBattleChara c)
        => c.MaxMp == 0 ? 0f : c.CurrentMp / (float)c.MaxMp;

    public static bool 活着(this IBattleChara c) => c.CurrentHp > 0;

    /// <summary>
    /// 身上有没有某个护盾。
    /// 注：这个 Dalamud 版本没有 CurrentShield，读不到精确盾量，
    /// 所以按"盾 buff 在不在"判断（多数护盾技能的 buff id 就等于技能 id）。
    /// </summary>
    /// <summary>
    /// 我上的某个 buff，在这个目标上是不是"快没了"（剩余 &lt; 秒）。
    ///
    /// 参考同类 ACR 的 GameObjectExtension.HasMyAuraWithTimeleft ——
    /// 比只看"有没有"精确得多：DoT 还剩 20 秒时不该补，剩 3 秒才该补。
    /// 拿不到时间信息时返回 true（当成该补，宁可多补一次）。
    /// </summary>
    /// <summary>
    /// 造一个"不等服务器回包"的能力技 —— 官方文档推荐：
    ///   new Spell 时把 WaitServerAcq 设成 False，能力技用完不等待回包，
    ///   能力技之间不会互相卡顿（手感更顺）。
    ///
    /// 注意：缓存的 SpellUtil.Get() 拿到的对象改不了这个属性，必须 new。
    /// </summary>
    public static Spell 能力技(uint id)
    {
        return new Spell(id, SpellTargetType.Self) { WaitServerAcq = false };
    }

    /// <summary>同上，指定目标</summary>
    public static Spell 能力技(uint id, IBattleChara 目标)
    {
        if (目标 == null) return 能力技(id);   // 退回"以自己为目标"，不把 null 传下去
        return new Spell(id, 目标) { WaitServerAcq = false };
    }

    /// <summary>
    /// 现在是不是插入能力技的时机。
    ///
    /// 参考同类 ACR 用的 GCDHelper.CanUseGCD ——
    /// 比我之前硬编码的 "GCD 剩余 &lt; 600ms" 准：不同技速下 GCD 长度不一样，
    /// 固定 600ms 在高速时偏早、低速时偏晚。读不到就退回旧阈值。
    /// </summary>
    /// <summary>
    /// 某个 buff 在目标身上的层数。参考同类 ACR 用的 GameObjectExtension.GetAuraStack。
    /// 读不到返回 0。
    /// </summary>
    public static int Buff层数(this IBattleChara c, uint buffId)
    {
        if (c == null || buffId == 0) return 0;
        try { return c.GetAuraStack(buffId); } catch { return 0; }
    }

    /// <summary>
    /// 当前 GCD 的总时长（毫秒）。参考同类 ACR 用的 GCDHelper.GetGCDDuration。
    /// 用途：把"GCD 剩余"换算成"进度百分比"，比拿固定阈值判断准。
    /// </summary>
    public static int GCD时长
    {
        get
        {
            try { return GCDHelper.GetGCDDuration(); } catch { return 2500; }
        }
    }

    /// <summary>现在离 GCD 转好还差多少（占 GCD 总时长的比例，0~1）</summary>
    public static float GCD进度
    {
        get
        {
            var 总 = GCD时长;
            if (总 <= 0) return 1f;
            try { return Math.Clamp(1f - GCDHelper.GetGCDCooldown() / (float)总, 0f, 1f); }
            catch { return 1f; }
        }
    }

    public static bool 可以插能力技()
    {
        // ⚠️ 回到 GCDHelper.GetGCDCooldown() < 600 —— 验证过能用的版本。
        //
        // 0.6.2 我把它换成了 GCDHelper.CanUseGCD()，**又一次没验证语义**，
        // 结果所有能力技（连环计 / 能量吸收 / 以太超流 / 法令 / 光速…）全都不放。
        // 表现就是"学者的爆发完全没反应"，而 GCD 治疗输出看着正常 ——
        // 因为问题出在 OffGcd 的准入条件上。
        //
        // 这是第三次栽在"看到个 API 就换上去"：
        //   Spell.Cooldown            → 所有技能不可用
        //   Spell.IsUnlock()          → 召唤/爆发不放
        //   GCDHelper.CanUseGCD()     → 所有能力技不放
        //
        // **结论：这个项目里，凡是语义没吃透的 API，一律不用。**
        return GCDHelper.GetGCDCooldown() < 600;
    }
    /// <summary>
    /// 技能当前充能层数。读不到返回 -1（调用方据此退回旧逻辑）。
    ///
    /// 参考同类 ACR 的 MemApiSpell.GetCharges —— 比"用时间限流猜充能"准得多：
    /// 之前是"2 秒内不放第二次"，属于盲猜；现在能直接读"还剩几层"。
    /// </summary>
    public static int 充能数(uint id)
    {
        if (id == 0) return -1;
        try { return (int)Core.Resolve<AEAssist.MemoryApi.MemApiSpell>().GetCharges(id); }
        catch { return -1; }
    }

    // ==================== 技能运行时状态（给 AI 描述用）====================
    //
    // ⚠️ 这一组是**只读描述**，不参与任何战斗判断 ——
    //    目的是让 AI 知道"这个技能现在转好没有 / 还剩几层"，
    //    而不是让它拿这些数字去自己算该放什么。
    //    真正的判断仍在本地层（见 开发约定.md G 节：AI 是增强层）。

    /// <summary>
    /// 技能**剩余冷却**秒数。就绪返回 0，拿不到返回 -1。
    /// </summary>
    public static float 冷却剩余秒(uint id)
    {
        if (id == 0) return -1f;

        try
        {
            var s = SpellUtil.Get(id);
            if (s == null) return -1f;

            var 剩余 = s.Cooldown;
            return 剩余.TotalSeconds <= 0 ? 0f : (float)剩余.TotalSeconds;
        }
        catch
        {
            return -1f;
        }
    }

    /// <summary>
    /// 技能当前充能层数（读不到返回 -1）与上限。
    ///
    /// 用 <see cref="充能数"/> 走 MemApiSpell.GetCharges ——
    /// 那是更可靠的来源；Spell.Charges 作为兜底。
    /// </summary>
    public static int 最大充能数(uint id)
    {
        if (id == 0) return 0;
        try
        {
            var s = SpellUtil.Get(id);
            return s?.MaxCharges ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>施法时间（秒）；瞬发返回 0，拿不到返回 -1</summary>
    public static float 施法时间秒(uint id)
    {
        if (id == 0) return -1f;
        try
        {
            var s = SpellUtil.Get(id);
            if (s == null) return -1f;
            return (float)s.CastTime.TotalSeconds;
        }
        catch
        {
            return -1f;
        }
    }

    /// <summary>蓝量消耗；不耗蓝返回 0，拿不到返回 -1</summary>
    public static long 蓝耗(uint id)
    {
        if (id == 0) return -1;
        try
        {
            var s = SpellUtil.Get(id);
            return s == null ? -1 : (long)s.MPNeed;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>技能射程（米）；拿不到返回 -1</summary>
    public static float 射程(uint id)
    {
        if (id == 0) return -1f;
        try
        {
            var s = SpellUtil.Get(id);
            return s == null ? -1f : s.ActionRange;
        }
        catch
        {
            return -1f;
        }
    }

    /// <summary>
    /// 目标身上**所有** aura 的「名字 + 剩余秒数」。
    ///
    /// 给 AI 用来判断"身上还挂着什么、还能撑多久"——
    /// 尤其是盾 / HoT / 自身增益这类**时间敏感**的东西。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ⚠️ 这里**必须用 fromMe = false**（踩过的坑，我自己写错过一次）：
    ///
    ///    <see cref="我的Buff剩余秒"/> 走的是 `GetAuraTimeleft(..., true)`，
    ///    它**只认"我挂的"** buff。而状态列表里还有大量**不是我挂的**：
    ///      · 食物、药、部队特效
    ///      · 队友给的增益（舞伴、诗歌、护盾…）
    ///      · 敌人身上**别人**挂的 DoT
    ///    对这些它返回 0 → 全被下面的 `>= 1f` 过滤掉 →
    ///    AI 看到的"身上啥也没有"，判断直接跑偏。
    ///
    ///    正确做法是 `fromMe: false`（任意来源都算）。
    ///    而对"我挂的 DoT"这种**需要区分归属**的场景，
    ///    仍然走 `我的Buff剩余秒`（fromMe = true）——
    ///    两者用途不同，不要互相替代。
    /// ══════════════════════════════════════════════════════════════════
    ///
    /// ⚠️ 顺序不稳定（沿用游戏给的状态表顺序），调用方如需排序请自己排。
    ///    拿不到返回空列表（不是 null）。
    /// </summary>
    public static List<(string 名, float 剩余秒)> 所有状态剩余(IBattleChara? 目标)
    {
        var 结果 = new List<(string, float)>();
        if (目标 == null) return 结果;

        try
        {
            var 列表 = 目标.StatusList;
            if (列表 == null) return 结果;

            var api = Core.Resolve<AEAssist.MemoryApi.MemApiBuff>();

            foreach (var s in 列表)
            {
                if (s == null) continue;

                string 名;
                try { 名 = s.GameData.Value.Name.ToString(); }
                catch { continue; }

                if (string.IsNullOrWhiteSpace(名)) continue;

                // ★ fromMe: false —— 任意来源的 aura 都要能读到（见上面的说明）
                float 剩余;
                try { 剩余 = api.GetAuraTimeleft(目标, s.StatusId, false) / 1000f; }
                catch { continue; }

                // 读不到或已过期的不列
                if (剩余 < 0f) continue;

                结果.Add((名, 剩余));
            }
        }
        catch { }

        return 结果;
    }

    /// <summary>
    /// 我挂在这个目标身上的 buff 还剩多久（**单位：毫秒**；没有这个 buff 返回 -1）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ⚠️⚠️ **单位是毫秒，不是秒** —— 这个函数原来叫 `我的Buff剩余秒`，
    ///       名字骗了所有调用方，是"总是打 DoT"这个 bug 的直接根因。
    ///
    ///  `MemApiBuff.GetAuraTimeleft(target, id, 是否只看我挂的)` 返回 **int 毫秒**：
    ///    证据一：AEAssist 自己的 `HasMyAuraWithTimeleft(target, id, timeLeft)`
    ///            实现就是 `GetAuraTimeleft(...) < timeLeft`，直接比、无换算；
    ///    证据二：同类 ACR 调用它时传的常量是 `ldc.i4 3000` / `4000` / `5000`
    ///            并把返回值直接和 3000 比 —— 只有毫秒说得通。
    ///
    ///  曾经造成的后果（两处，方向相反，都很难查）：
    ///    · `撑不过N个Gcd`：`毫秒 < 秒` 恒为 false（除非真的只剩几毫秒）
    ///      → DoT **只有彻底掉光才补**，从不提前续。
    ///    · 若哪天数值反过来，同一行代码会变成"每 2.5 秒补一次"。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static float 我的Buff剩余毫秒(this IBattleChara c, uint buffId)
    {
        if (c == null || buffId == 0) return -1f;

        try
        {
            return Core.Resolve<MemApiBuff>().GetAuraTimeleft(c, buffId, true);
        }
        catch
        {
            return -1f;
        }
    }

    /// <summary>同 <see cref="我的Buff剩余毫秒"/>，换算成秒（拿不到返回 -1）</summary>
    public static float 我的Buff剩余秒(this IBattleChara c, uint buffId)
    {
        var 毫秒 = c.我的Buff剩余毫秒(buffId);
        return 毫秒 < 0f ? -1f : 毫秒 / 1000f;
    }

    /// <summary>
    /// 这个 buff **还剩得比 N 秒多**吗（用于"还够用，不用补"）。
    ///
    /// ⚠️ 名字直说语义 —— 原来叫 `我的Buff快没了`，但实现是
    ///    `HasMyAuraWithTimeleft(id, 秒)` = **"还剩超过 N 秒"**，
    ///    和"快没了"正好相反。所有调用方都得配一个 `!` 才能读对，
    ///    后来果然就有调用方漏了那个 `!`（见 SageACR）。
    ///    **名字和语义相反的辅助函数，迟早会被用错。**
    ///
    /// 注意：目标身上**根本没有**这个 buff 时返回 false（= 不算"还剩得多"）。
    /// </summary>
    public static bool 我的Buff还剩超过N秒(this IBattleChara c, uint buffId, float 秒 = 3f)
    {
        if (c == null || buffId == 0) return false;   // 没有 buff → 不算"还剩得多"
        try
        {
            return c.HasMyAuraWithTimeleft(buffId, (int)(秒 * 1000));
        }
        catch
        {
            return false;   // 判断不了 → 当作"没有剩余"，让调用方去补
        }
    }

    /// <summary>
    /// 这个 buff 是不是"撑不过接下来 N 个 GCD"（用于 DoT 补判）。
    ///
    /// **比固定秒数聪明**：GCD 时长会变（急速、光速、dot 快照），
    /// 按"还剩几个 GCD"算才不会在极速装备下补得太晚。
    ///
    /// ⚠️ **单位必须是秒** —— 这里曾经拿 `我的Buff剩余秒`（实际返回毫秒）
    ///    去和"秒"比，导致 `毫秒 < 秒` 恒为 false，
    ///    **DoT 只有彻底掉光才补**（"总是打 DoT / 循环不对"的根因之一）。
    ///    现在统一走 `我的Buff剩余秒`（已修正为真的秒）。
    /// </summary>
    public static bool 撑不过N个Gcd(this IBattleChara c, uint buffId, int gcd数 = 2, float 缓冲秒 = 1.5f)
    {
        var 剩余秒 = c.我的Buff剩余秒(buffId);   // 拿不到返回 -1

        // 拿不到精确值 → 退回"还剩超过 N 秒"判断
        //   ⚠️ 这里返回的语义要和函数名一致：**撑不过 = 该补**。
        //      所以"还剩得够多" → false（不用补）；"没有/快没了" → true（要补）。
        if (剩余秒 < 0f)
        {
            return !c.我的Buff还剩超过N秒(buffId, gcd数 * 2.5f);
        }

        try
        {
            var gcd = GCDHelper.GetGCDDuration() / 1000f;   // 毫秒 → 秒
            if (gcd <= 0) gcd = 2.5f;

            return 剩余秒 < gcd * gcd数 + 缓冲秒;
        }
        catch
        {
            return 剩余秒 < gcd数 * 2.5f + 缓冲秒;
        }
    }

    /// <summary>
    /// ⚠️ **已废弃，不要再用** —— 名字与语义相反（它判断的是"还剩得比 N 秒多"，
    ///    不是"快没了"），调用方必须配 `!` 才读得对，历史上真有人漏了。
    ///    请改用语义直白的 <see cref="我的Buff还剩超过N秒"/>。
    ///
    /// **故意标成 error 而不是 warning**：warning 会被忽略，
    /// 而这个名字用错一次就是一个"循环不对"的 bug。让编译器直接挡住。
    /// </summary>
    [Obsolete("名字与语义相反（它=还剩超过N秒），请改用 我的Buff还剩超过N秒", true)]
    public static bool 我的Buff快没了(this IBattleChara c, uint buffId, int 秒 = 3)
        => c.我的Buff还剩超过N秒(buffId, 秒);

    public static bool 有盾(this IBattleChara c, uint 盾BuffId)
    {
        return 有任意盾(c, 盾BuffId);
    }
    /// <summary>
    /// 按**技能 ID** 判断目标身上有没有那个技能的护盾。
    ///
    /// ⚠️ 有些技能的护盾 buff id 和技能 id **不一样**：
    ///    学者「鼓舞激励之策」技能 185，护盾 buff「鼓舞」= 297。
    ///    之前直接拿技能 id 当 buff id 查，永远查不到 —— 等于护盾检查形同虚设。
    /// </summary>
    public static bool 有该技能的盾(this IBattleChara c, uint 技能Id)
    {
        if (c == null || 技能Id == 0) return false;

        // 技能 id -> buff id（只列两者不一致的）
        var buffId = 技能Id == 185 ? AuraIds.鼓舞 : 技能Id;
        return 有任意盾(c, buffId);
    }


    /// <summary>
    /// 一次查多个可能的护盾 buff。
    /// 参考同类 ACR 的 Scholar_TankSingleShield.HasShield()——它内部用的是 HasAnyAura，
    /// 而不是只认一个 id：护盾类 buff 在不同等级/形态下 id 会变，只认一个容易漏判。
    /// </summary>
    /// <summary>
    /// 按**技能 ID** 判断目标身上有没有它带来的 buff。
    /// 自动处理"技能 ID ≠ buff ID"的情况（见 AuraIds.技能转Buff 的映射表）。
    ///
    /// ⚠️ **同一个技能可能挂出多个 buff ID** —— 这里对盾类做了多 ID 覆盖：
    ///    学者「鼓舞激励之策」普通挂 297（鼓舞）、**暴击挂 1918（激励）**。
    ///    只查 297 的话，目标只有暴击盾时会被判成"没盾" → 重复读条。
    ///    （参考同类 ACR 的 `HasScholarShield`：297 + 1918 两个都查。）
    ///
    ///    其它技能目前都是"一个技能一个 buff"，映射表里取到谁就查谁。
    /// </summary>
    public static bool 有该技能的Buff(this IBattleChara c, uint 技能Id)
    {
        if (c == null || 技能Id == 0) return false;

        try
        {
            // 学者护盾技能（鼓舞 185 / 鼓舞激励之策）→ 鼓舞 + 激励 任一中即算有盾
            if (技能Id == 185 || 技能Id == AuraIds.鼓舞)
            {
                if (AuraIds.鼓舞 != 0 && c.HasAura(AuraIds.鼓舞)) return true;
                if (AuraIds.激励 != 0 && c.HasAura(AuraIds.激励)) return true;
            }

            return c.HasAura(AuraIds.技能转Buff(技能Id));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 目标是不是处于"假死"类无敌状态（死斗 / 行尸走肉 / 死而不僵）。
    ///
    /// **为什么要判这个**（参考同类 ACR 的 Scholar_SacredSoil，
    /// 它的 Check 常量里有 `409, 811, 810` —— 正是这三个）：
    ///
    ///   坦克开假死时，那几秒**它本来就不会死**，
    ///   这时候给它减伤 / 大治疗是纯浪费 —— CD 和白魔百合都白交。
    ///   等它快出假死状态、血量还是 1 的时候再给，才是正确时机。
    /// </summary>
    public static bool 处于假死状态(this IBattleChara c)
    {
        if (c == null) return false;

        try
        {
            foreach (var id in AuraIds.假死不治)
            {
                if (id != 0 && c.HasAura(id)) return true;
            }
        }
        catch { }

        return false;
    }

    public static bool 有任意盾(this IBattleChara c, params uint[] 候选)
    {
        if (c == null || 候选 == null) return false;

        foreach (var id in 候选)
        {
            if (id == 0) continue;
            try { if (c.HasAura(id)) return true; } catch { }
        }

        return false;
    }

    /// <summary>
    /// 这个尸体**现在不该去拉** —— 包含两类含义完全不同的原因：
    ///
    ///   ① **别人已经在拉了**（`AuraIds.复活等待` = 148）
    ///      → 防"两个奶妈抢同一个尸体"，浪费一个即刻。
    ///   ② **这个尸体根本拉不起来**（`AuraIds.限制复活` = 1755/2449/3380/4262/4263）
    ///      → 防"对着拉不起来的尸体交即刻和 GCD"。
    ///        某些副本机制 / 多变迷宫 / PvP 会给这类状态，硬拉是纯浪费。
    ///
    /// ⚠️ 两类**不能合并成一句注释** —— 一个是怕重复，一个是怕白做，
    ///    方向相反（这一点在驱散那边也一样：见 开发约定 里"复活不谦让、驱散要谦让"）。
    ///
    /// 无法读取状态时返回 false（= 允许尝试拉）——
    /// "误判为禁止"会让该拉的人没人拉，比"偶尔白拉一次"严重得多。
    /// </summary>
    public static bool 被禁止复活(this IBattleChara c)
    {
        if (c == null) return false;

        try
        {
            if (AuraIds.复活等待 != 0 && c.HasAura(AuraIds.复活等待)) return true;

            foreach (var id in AuraIds.限制复活)
            {
                if (c.HasAura(id)) return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public static bool 可以治(this IBattleChara c)
        => c.活着() && !c.死了()
        // 假死 / 无敌类统一走列表（含 810/811/2303/3255/409/1836），
        // 不再单独查某两个 —— 之前单独查的两个里，`出死入生` 还挂错了 id(811)，
        // 现在 id 已按 status 表对齐，集中在这里判断一处就够。
        && !c.HasAnyAura(AuraIds.假死不治, 3000);

    /// <summary>Dalamud 的 IsDead 属性（这里是包一层，方便将来换实现）</summary>
    public static bool 死了(this IBattleChara c) => c.IsDead;
}
