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

/// <summary>单体 HoT：白魔 再生 / 占星 吉星相位。</summary>
public class Res_SingleHoT : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_SingleHoT(JobSpellTable t) => _t = t;

    private uint 技能 => 技能选取.取(_t.Job,
        白魔: SpellIds.取("再生"),
        占星: SpellIds.取("吉星相位"));

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("HoT", true)) return -101;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 优先给坦克挂（HoT 收益最高），坦克满血也值得提前铺
        var 坦克 = HealTargetHelper.血量最低的坦克(0.95f);
        if (坦克 == null) return -1;
        if (坦克.有该技能的Buff(技能)) return -3;

        return SpellUtil.可用(技能) ? 5 : -1;
    }

    public void Build(Slot slot)
    {
        var 坦克 = HealTargetHelper.血量最低的坦克(0.95f);
        if (坦克 == null) return;

        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(new Spell(spell.Id, 坦克));
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

            // ⚠️ 假死状态不给减伤（对照 鍚岀被 ACR 罩子 Check 里的 409/811/810）
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
            // 地面技能选位（对照 鍚岀被 ACR 的敌人移动检测）
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
        if (!HealQt.GetQt("应急", false)) return -101;
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
