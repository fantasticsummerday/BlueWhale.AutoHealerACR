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
///  ★ 重写说明（用户实测："50 级神兵不读再生"）★
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
        //  ★ 动态优先级 —— 决定"HoT 还是硬读直疗" ★
        //
        //  用户实测的问题："有时候再生比硬读 GCD 奶更好"。
        //
        //  固定优先级解决不了这件事，因为**两种场景要的结果相反**：
        //
        //    · **有人濒危**（< 35%）→ 必须先直疗把血拉起来，
        //      HoT 是慢的，这时候挂 HoT 等于见死不救。→ HoT 该让路。
        //    · **有人中低血量且稳定**（没到濒危）→ 这正是 HoT 的强项：
        //      一个 GCD 换 30 秒持续回血，比一次次硬读直疗省得多。
        //      → HoT 该**优先**。
        //
        //  所以按局面动态给分值（都会盖过 `Res_HealSingleGcd` 的 10）：
        //    · 有持续伤害（一直在掉血）→ 14，**最高**
        //    · 一般中低血量           → 12，**也高于直疗**
        //    · 有人濒危               → 3，**让路给直疗**
        // ══════════════════════════════════════════════════════════════
        try
        {
            // 有人濒危 → 让路（HoT 太慢，救不了急）
            if (HealTargetHelper.低于阈值人数(0.35f) > 0) return 3;

            // 目标身上有持续伤害 → 最该挂 HoT 的场景
            if (AuraIds.有持续伤害(目标)) return 14;

            return 12;
        }
        catch
        {
            return 5;
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
            // 所以给一个保底值（参考实现也是这么做的：`Math.Max(0.3f, 阈值)`）——
            // 否则用户把阈值调得很低时，HoT 会因为"没人低于阈值"而永不挂。
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
            //   ⚠️ 排除自己：给自己挂 HoT 意义不大（自己在读条，
            //      而且我们的 `可以治()` 已经排掉假死类状态）。
            var 队友 = HealTargetHelper.最低血量队友(阈值);
            if (队友 != null && 队友.GameObjectId != Core.Me.GameObjectId && 适合挂(队友))
                return 队友;

            return null;
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
                if (r == null) continue;
                if (!适合挂(r)) continue;

                var 比例 = r.血量比例();
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

/// <summary>群体 HoT / 场地治疗：白魔 庇护所 / 贤者 自生（自生II 是升级版）。</summary>
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
        var 要求人数 = Math.Max(1, s.群奶最少人数 - 1);
        if (HealTargetHelper.低于阈值人数(s.群体治疗阈值) < 要求人数) return -1;

        return SpellUtil.可用(技能) ? 8 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;

        // 庇护所是放置型（落脚下），自生是自身中心
        if (_t.Job == Jobs.WhiteMage) slot.Add(new Spell(spell.Id, AEAssist.Core.Me.Position));
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
        var 坦克 = HealTargetHelper.血量最低的坦克(要来 ? 1f : 0.8f);
        if (坦克 == null) return -1;
        if (坦克.有该技能的Buff(技能)) return -3;

            // ⚠️ 假死状态不给减伤（参考同类 ACR 的罩子 Check 里的 409/811/810）
            //    坦克开死斗/行尸走肉时那几秒本来就不会死，减伤纯浪费。
            if (坦克.处于假死状态()) return -4;

        return SpellUtil.可用(技能) ? 14 : -1;
    }

    public void Build(Slot slot)
    {
        var 要来 = TimelineManager.未来有减伤(4.0) || 减伤Helper.即将来大伤害();
        var 坦克 = HealTargetHelper.血量最低的坦克(要来 ? 1f : 0.8f);
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
        if (Core.Me.有该技能的Buff(技能)) return -3;

        // 掉血够多 / 伤害要来 → 值得强化
        var s = HealSettings.Instance;
        var 要来了 = TimelineManager.未来有减伤(3.0) || 减伤Helper.即将来大伤害();
        var 掉血多 = HealTargetHelper.低于阈值人数(s.群体治疗阈值) >= s.群奶最少人数;

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

        // 给快死的坦克 + 伤害要来的时候
        var 要来 = TimelineManager.未来有减伤(4.0) || 减伤Helper.即将来大伤害();
        var 目标 = HealTargetHelper.血量最低的坦克(要来 ? 0.9f : 0.5f);
        if (目标 == null) return -1;
        if (目标.有该技能的Buff(技能)) return -3;

        return SpellUtil.可用(技能) ? 16 : -1;
    }

    public void Build(Slot slot)
    {
        var 要来 = TimelineManager.未来有减伤(4.0) || 减伤Helper.即将来大伤害();
        var 目标 = HealTargetHelper.血量最低的坦克(要来 ? 0.9f : 0.5f);
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
            var 人够多 = HealTargetHelper.低于阈值人数(s.群体治疗阈值) >= s.群奶最少人数;
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
        slot.Add(new Spell(spell.Id, Core.Me.Position));
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
        if (HealTargetHelper.低于阈值人数(s.群体治疗阈值) < HealTargetHelper.群奶人数要求(s.群奶最少人数)) return -1;

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

        // 自己身上得有盾 —— 注意用 buff id（鼓舞 = 297），不是技能 id 185。
        // 之前拿 185 去 HasAura，永远查不到，这个扩散盾等于从来没生效过。
        if (AuraIds.鼓舞 != 0 && !Core.Me.HasAura(AuraIds.鼓舞)) return -3;

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
        if (TimeHelper.Now() - 上次开 < 4000) return -7;
        if (Core.Me.有该技能的Buff(技能)) return -3;

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
        if (Core.Me.有该技能的Buff(技能)) return -3;

        var 坦克 = HealTargetHelper.血量最低的坦克(0.8f);
        if (坦克 == null) return -1;

        return SpellUtil.可用(技能) ? 7 : -1;
    }

    public void Build(Slot slot)
    {
        var 坦克 = HealTargetHelper.血量最低的坦克(0.8f);
        if (坦克 == null) return;

        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(new Spell(spell.Id, 坦克));
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
        if (Core.Me.有该技能的Buff(技能)) return -3;

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
