using System.Linq;
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
    /// <summary>按血量比例升序的、可以被治疗的队友</summary>
    public static List<IBattleChara> 可治疗队友()
    {
        return PartyHelper.CastableAlliesWithin30
            .Where(r => r.可以治())
            .OrderBy(r => r.血量比例())
            .ToList();
    }

    /// <summary>血量最低、且低于阈值的队友；没有就是 null</summary>
    public static IBattleChara? 最低血量队友(float 阈值)
    {
        return 可治疗队友().FirstOrDefault(r => r.血量比例() <= 阈值);
    }

    /// <summary>血量低于阈值的人数（判断值不值得群奶）</summary>
    public static int 低于阈值人数(float 阈值)
    {
        return 可治疗队友().Count(r => r.血量比例() <= 阈值);
    }

    /// <summary>队伍里血量最低的人（不看阈值，给大加用）</summary>
    public static IBattleChara? 最危险队友()
    {
        return 可治疗队友().FirstOrDefault();
    }

    /// <summary>血量最低的坦克（挂心关 / 预铺盾用）</summary>
    public static IBattleChara? 血量最低的坦克(float 阈值 = 1f)
    {
        return PartyHelper.CastableTanks
            .Where(r => r.活着() && r.血量比例() <= 阈值)
            .OrderBy(r => r.血量比例())
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
    /// 群体治疗的人数门槛。
    ///
    /// **为什么两个规模不一样**：
    ///   · 四人本（单奶）：队伍只有 4 人，**2 人掉血就已经是大事**，
    ///     而且没人帮你补 → 门槛要低、反应要快。
    ///   · 八人本（双奶）：8 个人里 2 人掉血很常见，
    ///     为这个交群奶会浪费 → 门槛要高一点。
    /// </summary>
    public static int 群奶人数要求(int 基础人数)
    {
        try
        {
            return 是八人本()
                ? Math.Max(基础人数, 4)          // 八人本：至少 4 人
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
    /// ⚠️ **修正（用户实测：90 级本 T 死了没人复活，而且即刻是好的）**
    ///
    ///    原来这里实现的是"两个奶妈时，只让对象 ID 最小的那个拉人"，
    ///    本意是避免重复。但实际会死锁：
    ///      · 如果对方奶妈是别的 ACR（比如 鍚岀被 ACR），它很可能也有自己的协调逻辑
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
        //    对照 鍚岀被 ACR 的 ShouldPrioritizeHealerResurrect：
        //    奶妈躺了 → 全队治疗断档 → 最容易连锁崩盘，所以优先救。
        var 躺着的 = PartyHelper.DeadAllies
            .Where(r => r != null && !r.HasAura(AuraIds.复活等待))
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

    /// <summary>当前选中的敌人（挂 DoT / 打输出用）</summary>
    public static IBattleChara? 当前目标()
    {
        var t = Core.Me.GetCurrTarget();
        if (t == null) return null;
        return t.CurrentHp > 0 ? t : null;
    }

    /// <summary>
    /// AOE 的最佳落点目标（需求 1）。
    /// 用 AEAssist 现成的 GetMostCanTargetObjects，它会挑"这个技能能打到最多敌人"的那个目标；
    /// 选不到就退回当前目标。
    /// </summary>
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
    public static bool 目标快死了(float 血线 = 0.25f, int ttk秒 = 12)
    {
        var t = 当前目标();

        // 没目标就别拦着（让输出逻辑自己处理）
        if (t == null) return false;

        if (t.血量比例() <= 血线) return true;

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
    public static bool 值得交爆发(float 血线 = 0.25f, int ttk秒 = 12)
    {
        return !目标快死了(血线, ttk秒);
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
