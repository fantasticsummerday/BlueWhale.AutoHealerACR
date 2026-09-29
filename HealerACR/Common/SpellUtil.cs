using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.Extension;
using AEAssist.MemoryApi;

namespace HealerACR.Common;

/// <summary>
/// 技能表要用的几个工具。
///
/// 单独放一层的原因：AEAssist 不同版本里"取技能实体""自动换成当前等级的技能"
/// 这两件事的 API 名字变过，集中在这里，换版本只改这个文件。
/// </summary>
public static class SpellUtil
{
    /// <summary>
    /// 取技能实体。所有 xxx.GetSpell() 最终都走这里。
    /// </summary>
    public static Spell? Get(uint id)
    {
        if (id == 0) return null;
        try
        {
            return id.GetSpell();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>这个技能当前等级学会了吗</summary>
    public static bool 已解锁(uint id)
    {
        if (id == 0) return false;
        try
        {
            return id.GetSpell().IsUnlock();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>现在能用吗（含施法条件检查）</summary>
    public static bool 可用(uint id)
    {
        if (id == 0) return false;

        var s = Get(id);
        if (s == null) return false;

        // ==================== 这一段改动前请先读完 ====================
        //
        // 官方文档原文（完整版，注意【】里的限定词）：
        //
        //     技能是否可用 IsReady
        //
        //     注意，考虑到 Buff 延迟，【部分有 Buff 才亮的技能】，可能需要你自行
        //     比对本地有没有释放获取 Buff 的技能。【此时】你可以通过 IsUnlock
        //     检查是否学习/等级够不够，接着使用 xxx.GetSpell().Cooldown 来检测
        //     技能 CD，再检测本地有没有释放过获取对应 Buff 的技能
        //
        // → **主判断始终是 IsReady**。「IsUnlock + Cooldown」是【只在 Buff 延迟
        //    导致技能不亮时】才用的**补充手段**，不是通用替代方案。
        //
        // 我在这一点上翻车两次，别再犯：
        //   0.6.7  换成 s.Cooldown > TimeSpan.Zero —— 语义没验证，恒真
        //          → 所有技能不可用
        //   0.7.3  加 !s.IsUnlock()               —— 对召唤技/爆发技返回 false
        //          → 小仙女不召唤、爆发不打
        //
        // 实测证据：0.5.5 的实机日志里有 "CastSpell success: 17215 朝日召唤"，
        // 那一版用的就是这个判断。**实测优先于文档的一般性建议。**
        //
        // 真要做"Buff 延迟"的补充判断，应该**针对具体技能**在它自己的 resolver 里做，
        // 而不是在这个通用入口上一刀切。
        // =============================================================

        // ⚠️ **这里故意不做移动判定** —— 移动守卫由各 resolver 显式调用
        //    `移动中能放()` 完成（错误码也更具体，方便调试）。
        //
        //    为什么不在这个公共出口一刀切：
        //      ① 有些调用点判的是**能力技**（紧急单奶 / 群体治疗能力技），
        //         它们本来就是瞬发，在这里判是多余的；
        //      ② 更要紧的是**可预测性** —— 在这里静默否决，
        //         会让"为什么这个技能突然不放了"变得很难查
        //         （我已经在"通用入口一刀切"上翻车过两次，见上面那段注释）。
        return s.IsReadyWithCanCast();
    }

    /// <summary>
    /// 移动守卫的便捷包装：**移动中不要放读条技能**（瞬发和正在读条的除外）。
    ///
    /// 各 resolver 在"确定要用哪个技能之后、返回之前"调用它，
    /// 详见 <see cref="移动中能放"/> 的说明。
    /// </summary>
    public static bool 移动中可用(uint id) => 移动中能放(id);

    // ==================== 移动状态描述（给 AI 看）====================

    /// <summary>
    /// **我现在在移动吗**。读不到返回 false（保守：当作没在移动）。
    /// </summary>
    public static bool 在移动()
    {
        try { return MoveHelper.IsMoving(); }
        catch { return false; }
    }

    /// <summary>
    /// 当前读条进度 —— (是否在读条, 已读秒, 总读条秒)。没读条返回 (false, 0, 0)。
    /// </summary>
    public static (bool 在读条, float 已读秒, float 总秒) 读条进度()
    {
        try
        {
            var me = Core.Me;
            if (me == null || !me.IsCasting) return (false, 0f, 0f);

            var 总 = me.TotalCastTime;
            var 已 = me.CurrentCastTime;

            // TotalCastTime <= 0 说明不是真读条（瞬发也会让 IsCasting 短暂为真）
            if (总 <= 0f) return (false, 0f, 0f);

            return (true, 已, 总);
        }
        catch { return (false, 0f, 0f); }
    }

    /// <summary>
    /// 一句话描述移动 / 读条状态 —— **给 AI 判断"能不能建议读条技能"用**。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么要给 AI 这个 ★
    ///
    ///    "本地逻辑已经会在移动时挡掉读条技能，但 **AI 还在建议读条技能**"
    ///
    ///    根因：**AI 根本不知道玩家在移动** ——
    ///    `移动中能放()` 是本地层的事，AI 拿到的局面里没有这个信息。
    ///    于是它按"站着不动"的前提给建议，那些建议全被本地拦掉，
    ///    表现为"AI 一直在说用闪灼，但一个都没打出去"。
    ///
    ///    所以必须把移动状态**作为事实喂给它**，让它自己改成建议瞬发技能。
    ///
    ///  ── 输出形态（举例）──
    ///      `移动中（读条会中断）—— 阶段B只能建议瞬发技能`
    ///      `站定 —— 可以建议读条技能`
    ///      `正在读条（已 1.2s / 共 2.5s）—— 读条期间不能移动`
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static string 移动状态描述()
    {
        var (在读条, 已读, 总) = 读条进度();

        if (在读条)
            return $"正在读条（已 {已读:F1}s / 共 {总:F1}s）—— 读条期间不能移动";

        if (在移动())
            return "移动中（读条会被中断）—— 阶段B只能建议瞬发技能";

        return "站定 —— 可以建议读条技能";
    }

    /// <summary>
    /// 这个技能**能不能在移动中放**（给 AI 看的一句话）。
    /// 用于告诉 AI"你建议的这个技能现在放不放得出来"。
    /// </summary>
    public static string 技能移动适用性(uint id)
    {
        if (id == 0) return "未知";

        try
        {
            var s = Get(id);
            if (s == null) return "未知";

            var 读条 = s.CastTime.TotalSeconds;
            if (读条 <= 0.05) return "瞬发（移动中也能放）";

            return $"读条 {读条:F1}s（移动中放不出）";
        }
        catch { return "未知"; }
    }

    /// <summary>
    /// 等级变换：把"最初形态"的技能换成当前等级该用的那个。
    /// 白魔 Stone(1) → StoneII → Glare → GlareIII，学者 Ruin → Broil IV …
    ///
    /// ⚠️ **返回值可能是 null，调用方必须判空**（现有调用方都判了）。
    ///
    /// ⚠️ 这里做了**双重保险**，因为"闪飒放不出来"这类 bug 的嫌疑点就在这：
    ///    `CheckActionChange(id)` 对**没有形态变化**的技能会返回 0
    ///    （而不是原样返回 id）—— 这时 `0.GetSpell()` 拿到的是 null，
    ///    技能就**静默不放**了。
    ///    所以：结果 id 为 0 → 退回原 id；换出来的 Spell 也是 null → 再退一次。
    ///    这样"没有形态变化"的技能永远走 `Get(id)`，不会因为多包一层而失效。
    /// </summary>
    public static Spell? 当前形态(uint id)
    {
        if (id == 0) return null;

        uint 形态Id = 0;
        try
        {
            形态Id = Core.Resolve<MemApiSpell>().CheckActionChange(id);
        }
        catch
        {
            // 这个 AEAssist 版本没有 CheckActionChange → 直接用原 id
            return Get(id);
        }

        // 没有形态变化（返回 0）→ 按原 id 处理，别让它变成"放不出来"
        if (形态Id == 0) return Get(id);

        return Get(形态Id) ?? Get(id);
    }

    /// <summary>从高到低挑第一个已解锁的技能（等级降级用），都没解锁返回 0</summary>
    public static uint 取已解锁(params uint[] ids)
    {
        foreach (var id in ids)
        {
            if (已解锁(id)) return id;
        }

        return 0;
    }

    // ==================== 移动判定 ====================

    /// <summary>
    /// **现在正忙着施法**吗（有读条且读条已经开始）。
    ///
    /// 用途见 <see cref="移动中能放"/>。
    /// </summary>
    private static bool 正在读条()
    {
        try
        {
            var me = Core.Me;
            return me != null && me.IsCasting && me.TotalCastTime > 0f && me.CurrentCastTime > 0f;
        }
        catch { return false; }
    }

    /// <summary>
    /// **移动中能不能放这个技能**。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么需要这个（两个实际现象）★
    ///
    ///    ① "移动时不应该反复尝试读条" ——
    ///       原来全 ACR 只有 `Res_MoveGcd`（移动填充）自己检查了
    ///       `MoveHelper.IsMoving()`，**其他 resolver 一个都没查**。
    ///       于是移动时会照样把读条 GCD 塞进 slot，读条被移动打断，
    ///       下一帧再塞、再断 —— 表现就是"反复尝试读条"，
    ///       既放不出技能，又把 GCD 白白空转掉。
    ///
    ///    ② "移动时应该尝试用无读条技能" ——
    ///       把读条技能挡住之后，瞬发技能（安慰之心 / 闪飒 /
    ///       学者的毁坏…）自然就轮到前面去了，这就是想要的降级。
    ///
    ///  ── 判定逻辑（三条，顺序有意义）──
    ///
    ///    ① **没在移动 → 放行**（绝大多数情况，零开销）
    ///    ② **正忙着读条 → 放行**：
    ///       这条**必须有**，否则会"自己把自己打断"——
    ///       已经读了一半的技能也是"移动中"（读条时确实不能走），
    ///       若因此判定不能放，会把正在进行的读条判成非法。
    ///       （FF14 里读条期间本来就动不了，所以 IsMoving 在读条中
    ///         通常为 false；但万一插件读到的状态有偏差，这条兜底。）
    ///    ③ 读条时间为 0（瞬发）→ 放行
    ///
    ///  ⚠️ 读条时间**必须在运行时读**（`Spell.CastTime`）——
    ///     CN 客户端的 `Action.csv` 里 `Cast<100ms>` 列**恒为 0**，
    ///     拿它判读条会把所有技能都当成瞬发（我实测确认过）。
    ///
    ///  ⚠️ 读不到读条时间时**放行**（兜底偏向"不要因为读不到就什么都不放"）。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    /// <param name="技能Id">要放的技能（会自动取当前形态）</param>
    /// <param name="原因">给日志/调试用的拒绝原因（可为空）</param>
    public static bool 移动中能放(uint 技能Id)
    {
        try
        {
            // ① 没在移动 → 放行
            if (!MoveHelper.IsMoving()) return true;

            // ② 正在读条 → 放行（见上面说明：不能把自己打断）
            if (正在读条()) return true;

            // ══════════════════════════════════════════════════════════
            //  ★ ③ 有"瞬发"类 buff → 放行（不加这条会误挡）★
            //
            //  ── 为什么必须加（我们原来漏了）──
            //    下面这几个 buff 会让**读条技能变成瞬发**：
            //      167 即刻咏唱 / 1249 连续咏唱 / 841 光速 / 3884 炽天附体
            //    这时候移动中**明明放得出来**，但 ④ 只看"技能本身有没有读条时间"
            //    → 会被挡掉。
            //
            //    表现："开了即刻咏唱想在移动中拉人/补血，却一直放不出来"。
            //
            //  ── 参考实现就是这么做的（IL 直证）──
            //    `IsMovementBlockingCast(actionId)` 的第一件事就是查这 4 个 buff，
            //    命中任一 → 直接放行，根本不看读条时间。
            //
            //  ⚠️ 顺序：这一条**必须在 ④ 之前** ——
            //     放后面就永远轮不到它（④ 已经把读条技能判成不能放了）。
            // ══════════════════════════════════════════════════════════
            try
            {
                var 我 = Core.Me;
                if (我 != null)
                {
                    foreach (var b in AuraIds.瞬发豁免)
                    {
                        if (我.HasAura(b)) return true;
                    }
                }
            }
            catch { }

            if (技能Id == 0) return true;

            // ④ 读运行时读条时间
            var spell = Get(技能Id);
            if (spell == null) return true;   // 读不到 → 放行

            return spell.CastTime.TotalSeconds <= 0.05;
        }
        catch
        {
            return true;   // 任何异常 → 放行（绝不因为判断失败让 ACR 停摆）
        }
    }

    /// <summary>
    /// **这个技能本身是不是瞬发**（读条时间 ≈ 0）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ⚠️ 为什么要单独提出来 —— 别拿 `移动中能放()` 当"是否瞬发"用
    ///
    ///    `移动中能放()` 的**第一句**就是：
    ///        `if (!MoveHelper.IsMoving()) return true;`
    ///    ⇒ **站着不动时它对任何技能都返回 true**。
    ///    拿它判"瞬发"会在站桩场景里把读条技能全判成瞬发
    ///    （我写 `Res_PotionTailDamage` 第一版就踩了这个坑，
    ///      后果是**该放即刻的时候不放**，补刀变成硬读条）。
    ///
    ///    判"是否瞬发"必须**只看读条时间**，和移动状态无关 —— 就是这个方法。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    /// <remarks>
    /// ⚠️ 失败方向是 **true（当作瞬发）**：读不到就当瞬发 → 调用方会跳过即刻。
    ///    这是有意的 —— "多放一个即刻" 比 "因为读不到读条时间就不补刀" 更糟，
    ///    前者浪费一个 60 秒 CD，后者只是少打一发。
    /// </remarks>
    public static bool 是瞬发(uint 技能Id)
    {
        if (技能Id == 0) return false;

        try
        {
            var spell = Get(技能Id);
            if (spell == null) return true;
            return spell.CastTime.TotalSeconds <= 0.05;
        }
        catch
        {
            return true;
        }
    }
}

/// <summary>
/// 血量 / 蓝量的统一读法。
/// 直接用 Dalamud 原生属性算，不依赖任何 AEAssist 版本特有的扩展方法。
/// </summary>
