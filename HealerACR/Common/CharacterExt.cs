using AEAssist;
using System.Numerics;
using AEAssist.Extension;
using AEAssist.Helper;
using AEAssist.MemoryApi;

namespace HealerACR.Common;

// 从 SpellUtil.cs 拆出来的（那个文件里有两个类，
// 导致我三次把方法加错类 —— 拆开就不会再混了）。

public static class CharacterExt
{
    public static float 血量比例(this IBattleChara c)
    {
        // ★ 读之前判有效性 —— 这是本工程**最热**的读取点之一，
        //   而它原来**完全裸读**（连 null 都没判）。
        //   游戏对象在换图/切区时会被**释放但非 null**（哨兵 0x12345679），
        //   读它的血量 = **原生访问违例**（穿 catch / 无转储 / 进程直接没）。
        //   在这里判一次，覆盖全部调用者（本项目 12 次崩溃都是这一类）。
        // ⚠️ 返回 1f（满血）而不是 0f：读不到的对象**不该被当成需要治疗的人**；
        //    返回 0 会诱发过量治疗和乱交技能 —— 方向反了更危险。
        if (c == null || !c.对象有效()) return 1f;
        try { return c.MaxHp == 0 ? 0f : c.CurrentHp / (float)c.MaxHp; }
        catch { return 1f; }
    }

    /// <summary>
    /// **有效血量比例** = 血量比例 + 护盾百分比。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 两轮对照分析都把它列为「最大缺口」，一直没做 ★
    ///
    ///  ── 问题在哪 ──
    ///    我们所有治疗/盾判断都只看 `血量比例()`，**不看盾**。
    ///    于是"45% 血 + 一层厚盾"的人会被反复投治疗 ——
    ///    那一口治疗实际是**过量**，而真正危险的人（比如 60% 血无盾）
    ///    反而排在后面。盾越厚，这个偏差越大。
    ///
    ///  ── 参考实现怎么做（IL 直证）──
    ///    两家统一写法：`血量% + ShieldPercentage / 100`
    ///    （A 12 处、B 24 处，用的是 `Dalamud...ICharacter::get_ShieldPercentage`）。
    ///
    ///  ── ⚠️ 一个纠正：我们曾经以为拿不到 ──
    ///    `CharacterExt` 原来有条注释写"这个 Dalamud 版本没有 `CurrentShield`"。
    ///    那句话本身没错（`CurrentShield` 确实不存在），**但结论错了** ——
    ///    精确盾量在 **`ICharacter.ShieldPercentage`** 上，类型是 `Byte`，
    ///    值域 0~100（**不是 0~1**）。
    ///    ⇒ 所以要 `/100f`。写成 `+ ShieldPercentage` 会让盾的影响放大 100 倍，
    ///      表现成"有盾的人永远不被治疗"。
    ///
    ///  ⚠️ 失败方向：读不到盾就当 0（退回原来的纯血量判断）——
    ///    绝不因为读不到盾就判成"这个人满血"。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static float 有效血量比例(this IBattleChara c)
    {
        // ★ 这里**必须自己判** —— 本函数除了转发 `血量比例()`，
        //   还要读 `c.ShieldPercentage`（**另一个裸读点**）。
        //   ⚠️ 只靠 `血量比例()` 里的守卫不够：那个已经 return 之后，
        //      下面这行 `c.ShieldPercentage` 照样会读已释放的对象。
        //      （我一开始把守卫只放在 `血量比例()` 里，就是这个漏。）
        if (c == null || !c.对象有效()) return 1f;
        var 血 = c.血量比例();

        try
        {
            return 血 + c.ShieldPercentage / 100f;
        }
        catch
        {
            return 血;   // 读不到盾 → 退回纯血量
        }
    }

    public static float 蓝量比例(this IBattleChara c)
    {
        // ★ 读之前判有效性 —— 这是本工程**最热**的读取点之一，
        //   而它原来**完全裸读**（连 null 都没判）。
        //   游戏对象在换图/切区时会被**释放但非 null**（哨兵 0x12345679），
        //   读它的血量 = **原生访问违例**（穿 catch / 无转储 / 进程直接没）。
        //   在这里判一次，覆盖全部调用者（本项目 12 次崩溃都是这一类）。
        if (c == null || !c.对象有效()) return 1f;
        try { return c.MaxMp == 0 ? 0f : c.CurrentMp / (float)c.MaxMp; }
        catch { return 1f; }
    }

    public static bool 活着(this IBattleChara c)
    {
        // ★ 读之前判有效性 —— 这是本工程**最热**的读取点之一，
        //   而它原来**完全裸读**（连 null 都没判）。
        //   游戏对象在换图/切区时会被**释放但非 null**（哨兵 0x12345679），
        //   读它的血量 = **原生访问违例**（穿 catch / 无转储 / 进程直接没）。
        //   在这里判一次，覆盖全部调用者（本项目 12 次崩溃都是这一类）。
        // ⚠️ 读不到 = 当它**不在场**（false），不参与决策。
        if (c == null || !c.对象有效()) return false;
        try { return c.CurrentHp > 0; }
        catch { return false; }
    }

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
    /// <summary>
    /// 目标身上**所有**状态（含剩余时间）。
    ///
    /// ⚠️ `Id` 是**后加的**（原来是 `(名, 剩余秒)` 两元组）——
    ///    加它是为了让调用方能**按 buff id 精确找**某一条的剩余时间，
    ///    而不是靠名字匹配（名字会因语言/版本变，id 不会）。
    ///    两元组的调用点用的是**具名**访问（`.名` / `.剩余秒`），所以不受影响。
    /// </summary>
    public static List<(uint Id, string 名, float 剩余秒)> 所有状态剩余(IBattleChara? 目标)
    {
        var 结果 = new List<(uint, string, float)>();
        if (目标 == null) return 结果;
        
        // [!] **必须先确认对象还有效** —— 失效对象读 StatusList 会抛异常，
        //     而本方法可能被 UI 回调每帧调用 ==> 异常逃逸 = 游戏进程被杀（见 `对象有效`）。
        if (!目标.对象有效()) return 结果;

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

                结果.Add((s.StatusId, 名, 剩余));
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
        // ★ 入口判有效性：本函数要读 buff（走 StatusList）——
        //   原生访问违例会**穿透下面的 catch**，所以必须读之前判。
        if (c == null || !c.对象有效() || 技能Id == 0) return false;

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
        // ★ 入口判有效性：本函数要读 buff（走 StatusList）——
        //   原生访问违例会**穿透下面的 catch**，所以必须读之前判。
        if (c == null || !c.对象有效() || 技能Id == 0) return false;

        try
        {
            foreach (var b in 该技能对应的所有Buff(技能Id))
                if (b != 0 && c.HasAura(b)) return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 一个技能可能挂出的**全部** buff —— 多档位的情况。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ⚠️ 为什么需要"一张表"而不是单个 ID
    ///
    ///  官方表里同一个技能名往往有**好几个状态 ID**（日/夜版本、特性升级版、
    ///  暴击版…）。只登记一个的后果是**"已有就不重复给"的判断在某些档位下失效**
    ///  → 技能被反复放。
    ///
    ///  已经踩过/审计出来的多档：
    ///    · 鼓舞 297 / 3087     激励 1918 / 3088      （学者盾，暴击版）
    ///    · 天星交错 1888(日) / 1889 / 4040
    ///    · 均衡诊断 2607 / 2865 / 3109               （贤者盾，等级档）
    ///    · 再生 158 / 1330
    ///    · 吉星相位 835 / 3099 / 3100                （日/夜版本）
    ///
    ///  ⚠️ 这里**只列审计确认过存在的档位**，不猜。
    ///     多列一个不存在的 ID 是无害的（HasAura 恒 false），
    ///     但少列一个就会静默失效 —— 所以宁可多列已核实的。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static IEnumerable<uint> 该技能对应的所有Buff(uint 技能Id)
    {
        // 学者盾：鼓舞 185 / 鼓舞激励之策 + 暴击版激励
        if (技能Id == 185 || 技能Id == AuraIds.鼓舞 || 技能Id == 297)
        {
            yield return AuraIds.鼓舞;       // 297
            yield return AuraIds.激励;       // 1918
            yield return 3087;               // 鼓舞（另一档）
            yield return 3088;               // 激励（另一档）
            yield break;
        }

        // 贤者盾：诊断 24284 / 均衡诊断
        if (技能Id == 24284 || 技能Id == AuraIds.均衡诊断)
        {
            yield return AuraIds.均衡诊断;   // 2607
            yield return 2865;
            yield return 3109;
            yield break;
        }

        // 贤者群盾：均衡预后 24286
        if (技能Id == 24286 || 技能Id == AuraIds.均衡预后)
        {
            yield return AuraIds.均衡预后;   // 2609
            yield break;
        }

        // 再生 137 / 158：两个档位都算
        if (技能Id == 137 || 技能Id == AuraIds.再生)
        {
            yield return AuraIds.再生;       // 158
            yield return AuraIds.再生2;      // 1330
            yield break;
        }

        // 吉星相位 3595 / 835：日/夜版本
        if (技能Id == 3595 || 技能Id == AuraIds.吉星相位)
        {
            yield return AuraIds.吉星相位;   // 835
            yield return 3099;
            yield return 3100;
            yield break;
        }

        // 天星交错 16556 / 1889：另有 1888(日) / 4040
        if (技能Id == 16556 || 技能Id == AuraIds.天星交错)
        {
            yield return AuraIds.天星交错;   // 1889
            yield return 1888;
            yield return 4040;
            yield break;
        }

        // 其余技能：走映射表，取到谁查谁
        yield return AuraIds.技能转Buff(技能Id);
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
        if (c == null || !c.对象有效()) return false;

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
        // ★ 入口判有效性（同上：要读 buff）
        if (c == null || !c.对象有效() || 候选 == null) return false;

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
        if (c == null || !c.对象有效()) return false;

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

    /// <summary>
    /// **这个游戏对象现在还安全可读吗** —— 读任何字段之前都该先过这一关。
    ///
    /// ══════════════════════════════════════════════════════════════
    ///  [!] 这条是**闪退的直接修复**（Windows 事件 1026，进程 ffxiv_dx11.exe）：
    ///
    ///      崩溃堆栈：
    ///        Dalamud...BattleChara.get_StatusList()
    ///        HealerACR.Common.HealTargetHelper.<可治疗队友>b__0_1
    ///        -> 伤害预测.状态描述() -> 调试窗.画预测() -> OnDrawSetting()
    ///        -> Dalamud.Interface.UiBuilder.OnDraw()
    ///
    ///  [!] 触发时机：**出副本那一刻**。
    ///      此时队伍成员的 `IBattleChara` 已经失效（换区 / 对象回收），
    ///      读 `StatusList` 抛异常；而这条路径是**调试窗每帧在 UI 回调里**跑的
    ///      ==> 异常逃逸到 Dalamud 的 `OnDraw` ==> **整个游戏进程被杀**。
    ///
    ///  [!] 为什么原来的 `catch { }` 没兜住：
    ///      异常是在 UI 回调那层逃逸的，不在我们控制得住的 try 里；
    ///      Dalamud 的绘制回调遇到未处理异常 = 直接终止进程。
    ///      （所以**光加 try 不够，必须在读之前就判断对象有效性**。）
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    /// <summary>
    /// **`IGameObject` 版本的对象有效性判定**。
    ///
    /// [!] **为什么需要它**：有些地方拿到的是 `IGameObject`（不是 `IBattleChara`）——
    ///     例如 `技能数据.视线被挡(IGameObject? 目标)`。那里要调 `ToStruct()`
    ///     （**读对象内存拿原生指针**）再交给 AEAssist 的 `LineOfSightChecker.IsBlocked`，
    ///     **不判就会在外部库内部崩**（钩子兜不住，见下）。
    ///
    /// [!] **判据只此一处**：`IBattleChara` 版**转发**到这里，不各写一份 ——
    ///     两份拷贝 = 将来改一处忘一处（开发约定 F③）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  [!] 三道判据分别挡什么（都是**实测崩溃**换来的）
    ///
    ///   ① `v < 0x10000` —— 空指针区。访问它会直接触发 `0xC0000005`。
    ///
    ///   ② **哨兵 `0x12345679`** —— 这是 Dalamud 的**已释放对象**标记。
    ///      ⚠️ **它不等于 0**！这就是为什么"只判 null"从来不够：
    ///         对象被释放后**仍然不是 null**，读它的任何属性都会踩坏内存。
    ///      两种形态都要挡：完整值相等、以及**低 32 位相等**
    ///      （某些路径上高位是脏的）。
    ///
    ///   ③ `v > 0x7FFFFFFF_FFFF` —— x64 用户态上界之外的**内核区**地址。
    ///
    ///   最后再 `IsValid()` 兜一次 —— 它同时挡"对象已从表里移除"这一类。
    ///
    ///  [!] **为什么不能只靠 `try/catch`**：这类崩溃是**原生访问违例**，
    ///      会**穿透 C# 的 `catch`**，进程直接被终止（无转储、无全局异常事件）。
    ///      ==> 唯一有效的防线是**读之前就判定"不能读"**。
    ///
    ///  [!] **为什么钩子也抓不到**：如果这个未验证的对象被交给**外部库**
    ///      （AEAssist / Dalamud），崩溃发生在**它们的函数体内**，
    ///      我们的 `try/catch` 和全局异常钩子**全都在外面** ——
    ///      这就是连续 12 轮排查里钩子一次都没触发的原因。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    // ══════════════════════════════════════════════════════════════════
    //  ★★ **`Core.Me` 的安全访问入口** —— 全工程都该走这里 ★★
    //
    //  [!] 为什么必须（连续 17 次崩溃方向里的最后一环）：
    //      `Core.Me` 在**换图 / 登录 / 过场**时**可以是 null**。
    //      而它的属性（`Position` / `CurrentHp` / `Level` / `MaxHp` ...）是
    //      **Dalamud 的原生 getter** —— 在 null 上调用就是**原生访问违例**：
    //        · 崩在 Dalamud 内部（我们的 try/catch 在它外面）
    //        · 无转储、无 .NET Runtime 事件
    //        · 全局异常钩子（含 FirstChance）**一次都不触发**
    //
    //  [!] 实测崩点（`Log-2722288.log`，主线程已锁定之后）：
    //        路标停在 `HealTargetHelper.可治疗队友()` 的
    //            `var 我 = Core.Me.Position;`
    //      ==> 队友都判了 `对象有效()`，**偏偏自己的 `Core.Me` 没判**。
    //
    //  [!] 方向（很重要）：拿不到就给**中性值** ——
    //        · 位置 -> `Vector3.Zero`（距离判定会得出"很远"，不会误放技能）
    //        · 血量 -> 1（满血，不会诱发治疗）
    //        · 等级 -> 0（查表得 0，不会误判技能可用）
    //      **绝不能因为读不到就去猜**，也绝不能为了读它把游戏打崩。
    // ══════════════════════════════════════════════════════════════════
    /// <summary>自己这个对象存不存在（`Core.Me` 在换图/登录时会是 null）。</summary>
    public static bool 我有效()
    {
        try { return AEAssist.Core.Me != null; } catch { return false; }
    }

    /// <summary>我的坐标 —— 拿不到给 `Vector3.Zero`（距离会算成很远，安全方向）。</summary>
    public static Vector3 我的位置()
    {
        try
        {
            var 我 = AEAssist.Core.Me;
            if (我 == null) return Vector3.Zero;
            return 我.Position;
        }
        catch { return Vector3.Zero; }
    }

    /// <summary>我的当前血量 —— 拿不到给 1（不会诱发治疗）。</summary>
    public static uint 我的当前血量()
    {
        try { var 我 = AEAssist.Core.Me; return 我 == null ? 1u : 我.CurrentHp; }
        catch { return 1u; }
    }

    /// <summary>我的最大血量 —— 拿不到给 1（避免除零）。</summary>
    public static uint 我的最大血量()
    {
        try { var 我 = AEAssist.Core.Me; return 我 == null ? 1u : 我.MaxHp; }
        catch { return 1u; }
    }

    /// <summary>我的当前蓝量 —— 拿不到给 0（不会误判"蓝够"）。</summary>
    public static uint 我的当前蓝量()
    {
        try { var 我 = AEAssist.Core.Me; return 我 == null ? 0u : 我.CurrentMp; }
        catch { return 0u; }
    }

    /// <summary>我的最大蓝量 —— 拿不到给 1（避免除零）。</summary>
    public static uint 我的最大蓝量()
    {
        try { var 我 = AEAssist.Core.Me; return 我 == null ? 1u : 我.MaxMp; }
        catch { return 1u; }
    }

    /// <summary>我的等级 —— 拿不到给 0（查表得 0，不会误判技能可用）。</summary>
    public static int 我的等级()
    {
        try { var 我 = AEAssist.Core.Me; return 我 == null ? 0 : (int)我.Level; }
        catch { return 0; }
    }

    /// <summary>我在不在战斗 —— 拿不到给 false。</summary>
    public static bool 我在战斗()
    {
        try { var 我 = AEAssist.Core.Me; return 我 != null && 我.InCombat(); }
        catch { return false; }
    }

    /// <summary>我身上有没有某个 buff —— `Core.Me` 为 null 时给 false（不误判"有"）。</summary>
    public static bool 我有光环(uint buffId)
    {
        try
        {
            if (buffId == 0) return false;
            var 我 = AEAssist.Core.Me;
            if (我 == null) return false;
            return 我.HasAura(buffId);
        }
        catch { return false; }
    }

    /// <summary>我身上某个 buff 的层数 —— 拿不到给 0。</summary>
    public static int 我的光环层数(uint buffId)
    {
        try
        {
            if (buffId == 0) return 0;
            var 我 = AEAssist.Core.Me;
            if (我 == null) return 0;
            return 我.GetAuraStack(buffId);
        }
        catch { return 0; }
    }

    /// <summary>我当前选中的目标 —— `Core.Me` 为 null 时给 null。</summary>
    public static IBattleChara? 我的目标()
    {
        try
        {
            var 我 = AEAssist.Core.Me;
            if (我 == null) return null;
            return 我.GetCurrTarget();
        }
        catch { return null; }
    }

    /// <summary>我自己的 ObjectId —— 拿不到给 0（不会和任何真人撞上）。</summary>
    public static ulong 我的ObjectId()
    {
        try
        {
            var 我 = AEAssist.Core.Me;
            if (我 == null) return 0;
            return 我.GameObjectId;
        }
        catch { return 0; }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ★ **不经过"阈值钩子"的阈值读取** —— 专供 AI 采集流程用 ★
    //
    //  [!] 为什么需要（路标实证）：
    //      崩点被夹在 `3341`（打印）和 `3342`（没打印）之间，
    //      而那两行之间**只有一行**：
    //          var 阈值 = HealSettings.Instance.单体治疗阈值;
    //
    //      `单体治疗阈值` 是**属性**，getter 是：
    //          get => 阈值钩子.应用(单体治疗阈值_基础, 可调参数.单体治疗阈值);
    //      ==> 它会在**采集流程内部**回调 **AI 层挂的钩子**。
    //          钩子用静态 `Dictionary` + 静态 `Func` 实现（都无锁），
    //          而 AI 层此刻**正在同一个线程上跑采集**
    //          ==> **回调进 AI 代码 = 重入**（半初始化状态下重入自己的代码）。
    //
    //  [!] 修法：**采集流程只读 `_基础` 纯字段** ——
    //      无钩子、无字典、无委托调用，不可能重入。
    //
    //  [!] 功能没少：真正**做决策**的地方（本地 resolver）继续走属性、
    //      继续受 AI 钩子影响。AI 拿阈值只是用来**叙述**当前阈值大概多少。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>单体治疗阈值（**原始值，不经过 AI 钩子**）—— 供采集/叙述用。</summary>
    public static float 单体治疗阈值_原始()
    {
        try { return HealSettings.Instance.单体治疗阈值_基础; }
        catch { return 0.52f; }
    }

    /// <summary>群体治疗阈值（**原始值，不经过 AI 钩子**）。</summary>
    public static float 群体治疗阈值_原始()
    {
        try { return HealSettings.Instance.群体治疗阈值_基础; }
        catch { return 0.62f; }
    }

    /// <summary>我在不在读条 —— `Core.Me` 为 null 时给 false（原属性是原生 getter）。</summary>
    public static bool 我在读条()
    {
        try
        {
            var 我 = AEAssist.Core.Me;
            if (我 == null) return false;
            return 我.IsCasting;
        }
        catch { return false; }
    }

    /// <summary>我还活着吗（自己的当前血量 &gt; 0）—— 拿不到给 false。</summary>
    public static bool 我还活着() => 我的当前血量() > 0;

    /// <summary>我身上的、由某个技能产生的 buff —— `Core.Me` 为 null 时给 false。</summary>
    public static bool 我有该技能的Buff(uint 技能Id)
    {
        try
        {
            var 我 = AEAssist.Core.Me;
            if (我 == null) return false;
            return 我.有该技能的Buff(技能Id);
        }
        catch { return false; }
    }

    /// <summary>我某个 buff 的剩余毫秒 —— 拿不到给 0（视为"没了"）。</summary>
    public static float 我的Buff剩余毫秒安全(uint buffId)
    {
        try
        {
            var 我 = AEAssist.Core.Me;
            if (我 == null) return 0f;
            return 我.我的Buff剩余毫秒(buffId);
        }
        catch { return 0f; }
    }

    /// <summary>我的职业 RowId —— 拿不到给 0。</summary>
    public static uint 我的职业Id()
    {
        try
        {
            var 我 = AEAssist.Core.Me;
            if (我 == null) return 0;
            return 我.ClassJob.RowId;
        }
        catch { return 0; }
    }

    /// <summary>我的当前职业（Job 枚举）—— 拿不到给 0。</summary>
    public static uint 我的当前职业()
    {
        try
        {
            var 我 = AEAssist.Core.Me;
            if (我 == null) return 0;
            return (uint)我.CurrentJob();
        }
        catch { return 0; }
    }






    public static bool 对象有效(this IGameObject? o)
    {
        if (o == null) return false;

        // ══════════════════════════════════════════════════════════════════
        //  ★★★ **判据顺序 —— 这是本项目最严重的崩溃点之一** ★★★
        //
        //  [!] 现场证据（用户截图，2026-10-02 20:14）：
        //      栈自下往上：
        //          Dalamud…DxgiSwapChainPresentDetour          ← 渲染线程
        //            Dalamud.Interface.Internal…Draw
        //              HealerACR.Common.独立设置窗.每帧画()
        //                HealerACR.Common.HealerEntryBase.画调试窗()
        //                  BlueWhale.AutoHealerACR.调试窗.绘制()
        //                    HealerACR.Common.CharacterExt.对象有效(IGameObject)
        //                      Dalamud…Types.GameObject.get_GameObjectId()  ← ★ 崩在这
        //      异常：`System.AccessViolationException`（0xc0000005）
        //
        //  [!] 原来的实现把 `o.GameObjectId` 放在**第一句**：
        //          try {
        //              _ = o.GameObjectId;      // ← 第一句就是它
        //              _ = o.IsValid();
        //              var 地址 = o.Address;    // ← 地址检查在【访问之后】才做
        //              ...
        //          } catch { return false; }
        //
        //      ==> 两个错：
        //        ① **地址判据来晚了** —— 先碰了内存，再问"这地址可信吗"
        //        ② ★★ **`try/catch` 根本兜不住它** ★★
        //           `GameObjectId` 读的是**原生内存**；对象已销毁时它抛的是
        //           `AccessViolationException` —— 这是 **corrupted state exception**，
        //           .NET 默认**不允许 catch** ⇒ **catch 块永远不执行** ⇒ 进程直接死。
        //           ==> 也就是说：这个"保护函数"**本身没有任何保护作用**。
        //
        //  [!] 所以现在的顺序：**先做纯托管判据，最后才碰原生属性**。
        //      `Address` 只是读一个字长指针的**值** —— 不 deref、不解引用，
        //      所以"这个值看着像不像合法指针"在**任何时刻**都问得安全。
        //      ==> 地址不像指针 ⇒ **一次原生属性访问都不做** ⇒ 不可能 AV。
        //      ==> 地址像指针   ⇒ 对象是活的，后续访问安全。
        //
        //  [!] 为什么不再单独调 `IsValid()`：
        //      它内部【也读原生内存】（会碰 `gameObject->EntityId`），
        //      在"对象正在销毁"的窗口里它自己就能 AV —— 而那个 AV 同样兜不住。
        //      ==> 多一句判据在**这个函数里**是纯风险，不是保险。
        //          （最后那句 `return o.IsValid()` 是**地址已验证之后**才走的。）
        // ══════════════════════════════════════════════════════════════════
        try
        {
            // ── ① 地址判据（纯托管，先做）──
            var 地址 = o.Address;
            if (地址 == IntPtr.Zero) return false;

            var v = 地址.ToInt64();
            if (v < 0x10000L) return false;                          // ① 空指针区
            if (v == 0x12345679L) return false;                      // ② 哨兵（完整）
            if ((v & 0xFFFFFFFFL) == 0x12345679L) return false;       // ② 哨兵（截断）
            if (v > 0x7FFFFFFF_FFFFL) return false;                  // ③ 内核区

            // ── ② 地址可信了，才碰原生属性 ──
            return o.IsValid();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 角色对象有效性 —— **纯转发**给 `对象有效(this IGameObject?)`。
    /// 保留这个重载只是为了调用点少写强转（`r.对象有效()` 而不是 `((IGameObject)r).对象有效()`）。
    /// </summary>
    public static bool 对象有效(this IBattleChara? c)
        => ((IGameObject?)c).对象有效();

    
    /// <summary>
    /// 这个人现在**可以被治疗**吗（对象有效 · 活着 · 不在假死/无敌类状态）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  [!] **buff 读取单独包 try** —— 这是闪退的根因（用户实测「进本就闪退」）。
    ///
    ///      `对象有效()` 只保证**检查那一刻**对象可用；
    ///      进本/出本瞬间 `IsValid()` 可能还是 true，而底层角色对象已被释放
    ///      ==> 随后的 `HasAnyAura` -> `StatusList` 踩空。
    ///
    ///  [!] 而这类崩溃是**访问违例**（`0xc0000005`）级别 ——
    ///      **`try/catch` 在多数情况下根本拦不住**（它穿透到 UI 回调，进程被杀）。
    ///      ==> 所以本函数**不能**被当作安全边界：
    ///          真正的防线是**调用方在读之前判定"整个队伍可不可信"**
    ///          （见 `调试窗.绘制()` 的第③条守卫）。
    ///      这里包 try 只是"能拦就拦一层"，不代替调用方的守卫。
    ///
    ///  [!] 拿不到状态时返回 **false**（当作"不能治"）——
    ///      方向很重要：返回 true 会让治疗去读一个可能失效的对象。
    ///      少治一个 vs 崩游戏，选前者。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static bool 可以治(this IBattleChara c)
    {
        // 先判对象有效性 —— 失效对象读 buff 列表会抛异常（闪退根因，见 `对象有效`）
        if (c == null || !c.对象有效() || !c.活着() || c.死了()) return false;

        // 假死 / 无敌类统一走列表（含 810/811/2303/3255/409/1836），
        // 不再单独查某两个 —— 之前单独查的两个里，`出死入生` 还挂错了 id(811)，
        // 现在 id 已按 status 表对齐，集中在这里判断一处就够。
        //
        // ══════════════════════════════════════════════════════════════════
        //  ★★ **这里就是崩溃点，而且 `catch` 兜不住** ★★
        //
        //  [!] 崩溃转储（`dalamud_appcrash_*_154349_357_12772.log`）：
        //        地址: **12345679**                     <- Dalamud 的"已释放对象"哨兵
        //        BattleChara.get_StatusList()           <- 踩在坏内存上
        //        MemApiBuff.HasAnyAura(...)
        //        HealerACR.<可治疗队友>b__0_1           <- 调用方
        //        伤害预测.预计掉血人数 -> 调试窗.画预测
        //
        //  [!] 原来这里的注释写的是"失效对象读 buff 列表**会抛异常**" ——
        //      **那个归因是错的**：它是**原生访问违例**（`0xc0000005`），
        //      **直接穿透 C# 的 `catch`**。所以下面那个 `catch { return false; }`
        //      在这种情况下**一点作用都没有** —— 进程当场被杀。
        //
        //  [!] 而上面第 758 行已经判过 `对象有效()` 了，为什么还崩？
        //      因为"判完"到"读 buff"之间有**极窄的窗口**：
        //      换区 / 队友掉线 / 对象池回收，都可能让这个对象在这一瞬间被释放。
        //
        //  [!] 所以这里做**两层**：
        //        ① 读之前**再判一次**（把窗口压到最小）
        //        ② 读本身**不加 try** —— 加了也没用，反而让人以为安全了；
        //           真要防，只能在"读之前"防（这也是全项目的既定结论）
        // ══════════════════════════════════════════════════════════════════
        if (!c.对象有效()) return false;

        // ⚠️ `catch` 保留（它能挡住 `HasAnyAura` 内部的**托管**异常，比如字典查不到），
        //    但**不要以为它能挡住崩溃** —— 见上面的说明。
        try { return !c.HasAnyAura(AuraIds.假死不治, 3000); }
        catch { return false; }
    }

    /// <summary>Dalamud 的 IsDead 属性（这里是包一层，方便将来换实现）</summary>
    public static bool 死了(this IBattleChara c)
    {
        // ★ 读之前判有效性 —— 这是本工程**最热**的读取点之一，
        //   而它原来**完全裸读**（连 null 都没判）。
        //   游戏对象在换图/切区时会被**释放但非 null**（哨兵 0x12345679），
        //   读它的血量 = **原生访问违例**（穿 catch / 无转储 / 进程直接没）。
        //   在这里判一次，覆盖全部调用者（本项目 12 次崩溃都是这一类）。
        // ⚠️ 读不到 = 当它**不在场**（死了 = true，即「别去治它」）。
        if (c == null || !c.对象有效()) return true;
        try { return c.IsDead; }
        catch { return true; }
    }
}
