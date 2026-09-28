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
    /// 对照 Shiyuvi 的 GameObjectExtension.HasMyAuraWithTimeleft ——
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
    /// 对照 Shiyuvi 用的 GCDHelper.CanUseGCD ——
    /// 比我之前硬编码的 "GCD 剩余 &lt; 600ms" 准：不同技速下 GCD 长度不一样，
    /// 固定 600ms 在高速时偏早、低速时偏晚。读不到就退回旧阈值。
    /// </summary>
    /// <summary>
    /// 某个 buff 在目标身上的层数。对照 Shiyuvi 用的 GameObjectExtension.GetAuraStack。
    /// 读不到返回 0。
    /// </summary>
    public static int Buff层数(this IBattleChara c, uint buffId)
    {
        if (c == null || buffId == 0) return 0;
        try { return c.GetAuraStack(buffId); } catch { return 0; }
    }

    /// <summary>
    /// 当前 GCD 的总时长（毫秒）。对照 Shiyuvi 用的 GCDHelper.GetGCDDuration。
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
    /// 对照 Shiyuvi 的 MemApiSpell.GetCharges —— 比"用时间限流猜充能"准得多：
    /// 之前是"2 秒内不放第二次"，属于盲猜；现在能直接读"还剩几层"。
    /// </summary>
    public static int 充能数(uint id)
    {
        if (id == 0) return -1;
        try { return (int)Core.Resolve<AEAssist.MemoryApi.MemApiSpell>().GetCharges(id); }
        catch { return -1; }
    }

    /// <summary>
    /// 我挂在这个目标身上的 buff 还剩多少秒（没有则返回 -1）。
    ///
    /// **对照 Shiyuvi 的 `ShiyuviBuffHelper.目标身上buff时间`** ——
    /// 它直接调 `MemApiBuff.GetAuraTimeleft` 拿精确值，
    /// 而不是只问"是不是小于 N 秒"。
    ///
    /// 拿到精确值的意义：DoT 补判可以按"还能放几个 GCD"来算，
    /// 而不是硬写一个固定秒数。
    /// </summary>
    public static float 我的Buff剩余秒(this IBattleChara c, uint buffId)
    {
        if (c == null || buffId == 0) return -1f;

        try
        {
            var 剩余 = Core.Resolve<MemApiBuff>().GetAuraTimeleft(c, buffId, true);
            return 剩余;
        }
        catch
        {
            return -1f;
        }
    }

    /// <summary>
    /// 这个 buff 是不是"撑不过接下来 N 个 GCD"（用于 DoT 补判）。
    ///
    /// **比固定秒数聪明**：GCD 时长会变（急速、光速、dot 快照），
    /// 按"还剩几个 GCD"算才不会在极速装备下补得太晚。
    /// </summary>
    public static bool 撑不过N个Gcd(this IBattleChara c, uint buffId, int gcd数 = 2, float 缓冲秒 = 1.5f)
    {
        var 剩余 = c.我的Buff剩余秒(buffId);

        // 拿不到精确值 → 退回旧的秒数判断（宁可保守）
        if (剩余 < 0)
        {
            return c.我的Buff快没了(buffId, gcd数 * 3);
        }

        try
        {
            var gcd = GCDHelper.GetGCDDuration() / 1000f;   // 毫秒 → 秒
            if (gcd <= 0) gcd = 2.5f;

            return 剩余 < gcd * gcd数 + 缓冲秒;
        }
        catch
        {
            return 剩余 < gcd数 * 2.5f + 缓冲秒;
        }
    }

    public static bool 我的Buff快没了(this IBattleChara c, uint buffId, int 秒 = 3)
    {
        if (c == null || buffId == 0) return true;
        try { return c.HasMyAuraWithTimeleft(buffId, 秒); } catch { return true; }
    }

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
    /// 对照 Shiyuvi 的 Scholar_TankSingleShield.HasShield()——它内部用的是 HasAnyAura，
    /// 而不是只认一个 id：护盾类 buff 在不同等级/形态下 id 会变，只认一个容易漏判。
    /// </summary>
    /// <summary>
    /// 按**技能 ID** 判断目标身上有没有它带来的 buff。
    /// 自动处理"技能 ID ≠ buff ID"的情况（见 AuraIds.技能转Buff 的映射表）。
    /// </summary>
    public static bool 有该技能的Buff(this IBattleChara c, uint 技能Id)
    {
        if (c == null || 技能Id == 0) return false;
        try { return c.HasAura(AuraIds.技能转Buff(技能Id)); } catch { return false; }
    }

    /// <summary>
    /// 目标是不是处于"假死"类无敌状态（死斗 / 行尸走肉 / 死而不僵）。
    ///
    /// **为什么要判这个**（对照 Shiyuvi 的 Scholar_SacredSoil，
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

    public static bool 可以治(this IBattleChara c)
        => c.活着() && !c.死了()
        && !c.HasAura(AuraIds.出死入生)
        && !c.HasAura(AuraIds.行尸走肉)
        && !c.HasAnyAura(AuraIds.假死不治, 3000);

    /// <summary>Dalamud 的 IsDead 属性（这里是包一层，方便将来换实现）</summary>
    public static bool 死了(this IBattleChara c) => c.IsDead;
}
