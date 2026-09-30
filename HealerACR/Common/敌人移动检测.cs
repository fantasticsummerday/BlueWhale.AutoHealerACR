using System.Numerics;
using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// 敌人移动检测 —— 参考同类 ACR 的 `ScholarEnemyCheck.HasEnemyMovedLessThan`。
///
/// **要解决的问题**：地面放置技能（罩子 / 地星 / 庇护所 / 礼仪之铃）
/// 放下去之后敌人一走，技能就废了。所以选位置要挑"站位稳定"的敌人。
///
/// ══════════════════════════════════════════════════════════════════
///  ⚠️ 关键修正：「没动」不等于「站得稳」
///
///  第一版逻辑是"最近没移动 -> 判定站得稳"。
///  但**小怪刚出现时往往站着不动**（还在待机 / 没被拉 / 在远处），
///  这时候会被误判成"稳定"，罩子扔过去，它马上冲进来 —— 白放。
///
///  所以真正的判据不是"现在没动"，而是 **"已经稳定了足够久"**：
///    ① 必须**观察满最短时间**（默认 3 秒）才给判断，之前一律 false
///    ② 采样点要够多（至少 4 个），避免"刚好两帧没动"
///    ③ **还在持续靠近我的敌人直接排除**（它在移动，只是采样间隔没抓到）
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 敌人移动检测
{
    /// <summary>采样间隔（毫秒）—— 太短会被抖动干扰，太长反应慢</summary>
    private const int 采样间隔 = 400;

    /// <summary>
    /// 最短观察时间。**这是防误判的核心** ——
    /// 小怪刚出现的头几秒一律不给"稳定"判定。
    /// </summary>
    private const int 最短观察 = 3000;

    /// <summary>最少要采到几个点（配合最短观察，双重保险）</summary>
    private const int 最少采样数 = 4;

    /// <summary>记录多久没出现就清掉</summary>
    private const int 记录过期 = 15000;

    /// <summary>判定阈值：平均移动速度超过这个值（米/秒）就不算稳定</summary>
    private const float 默认阈值 = 2.5f;

    private sealed class 采样
    {
        public Vector3 位置;
        public long 时间;
        public float 累计移动;
        public int 采样数;
        public long 首次观察;
    }

    private static readonly Dictionary<ulong, 采样> _记录 = new();

    /// <summary>
    /// 先记账再说 —— 不管最后判不判得了，采样都要继续。
    /// 返回"当前这次是否需要继续累加"。
    /// </summary>
    private static void 累加采样(采样 s, Vector3 当前位置, long 现在)
    {
        if (现在 - s.时间 < 采样间隔) return;

        s.累计移动 += Vector3.Distance(当前位置, s.位置);
        s.位置 = 当前位置;
        s.时间 = 现在;
        s.采样数++;
    }

    // ══════════════════════════════════════════════════════════════
    //  ★ 复刻参考实现的 `IsTargetMoving`（逐帧位移法）★
    // ══════════════════════════════════════════════════════════════

    /// <summary>上一次位置（逐帧位移用）—— key 用 EntityId，和参考实现一致</summary>
    private static readonly Dictionary<ulong, Vector3> _上次位置 = new();

    /// <summary>
    /// **这个目标"这一帧"动了没有** —— 复刻参考实现的 `IsTargetMoving`。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 和上面 `移动很少()` 的区别（两套判据，用途不同）★
    ///
    ///    | | 移动很少()（我们的） | 本方法（参考实现的） |
    ///    |---|---|---|
    ///    | 判据 | 3 秒内**平均速度** < 2.5 米/秒 | **逐帧位移** > 0.0001 |
    ///    | 观察期 | 要 3000ms + 4 个采样点 | **没有** |
    ///    | 数据不足 | 一律 false | **首次返回 false，之后立刻生效** |
    ///    | 用途 | "这个怪稳不稳"（长时间判断）| "它现在动没动"（当下判断）|
    ///
    ///  ⚠️ **名字反过来看会晕**：`移动很少` 名字像"判当下"，
    ///     其实是**长时间平均**；本方法才是真正的"当下有没有动"。
    ///
    ///  ⚠️ 首次见到这个目标返回 **false**（"没在动"）——
    ///     参考实现就是这么写的（`IL_0035-0043`：建档后直接 `Ldc_i4_0; Ret`）。
    ///     不这么写的话，第一帧必然返回 true，会白白挡住一次施放。
    ///
    ///  ⚠️ key 用 `EntityId`（参考实现也是）——
    ///     `GameObjectId` 和它在我们这个版本里是同一个值的两种暴露，
    ///     但用 `EntityId` 更明确（对象身份而非"游戏对象 id"）。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    [Obsolete("截至 3.19.33 全仓库零调用点。" +
              "它判的是「逐帧位移 > 1 厘米」，和本文件真正在用的 `移动很少()`" +
              "（累计移动/观察秒 < 2.5 米每秒）**不是一回事** —— 别混用。")]
    public static bool 正在移动(IBattleChara? 目标)
    {
        if (目标 == null) return false;

        try
        {
            var id = 目标.EntityId;
            if (id == 0) return false;

            var 位置 = 目标.Position;

            if (!_上次位置.TryGetValue(id, out var 上次))
            {
                _上次位置[id] = 位置;
                return false;              // 首次 → 当作"没动"（照抄参考实现）
            }

            _上次位置[id] = 位置;

            // ⚠️ 0.0001 是参考实现的阈值（`Ldc_r4 0.0001` + `Cgt`）——
            //    这是**平方距离**的比较，0.0001 = 0.01 米 = 1 厘米。
            //    非常灵敏：站着不动的目标偶尔也会有 1 厘米抖动，
            //    但那种抖动是**单帧**的，不会持续，所以不会误判成"一直在动"。
            var dx = 位置.X - 上次.X;
            var dy = 位置.Y - 上次.Y;
            var dz = 位置.Z - 上次.Z;
            return dx * dx + dy * dy + dz * dz > 0.0001f;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>清掉逐帧位移记录（换本时用）</summary>
    public static void 清位移记录() => _上次位置.Clear();


    /// <summary>
    /// 这个敌人是不是**已经稳定了足够久**（可以放心往它脚下放地面技能）。
    ///
    /// **数据不足时一律返回 false** —— 宁可退回"放自己脚下"，
    /// 也不要因为误判把技能扔到马上要跑的目标身上。
    /// </summary>
    public static bool 移动很少(IBattleChara? 敌人, float 阈值 = 默认阈值)
    {
        if (敌人 == null) return false;

        try
        {
            var id = 敌人.GameObjectId;
            var 现在 = TimeHelper.Now();
            var 当前位置 = 敌人.Position;

            // 第一次见 → 建档，什么都不判
            if (!_记录.TryGetValue(id, out var s))
            {
                _记录[id] = new 采样
                {
                    位置 = 当前位置,
                    时间 = 现在,
                    累计移动 = 0,
                    采样数 = 0,
                    首次观察 = 现在,
                };
                return false;
            }

            var 观察毫秒 = 现在 - s.首次观察;

            // ── ③ 还在持续靠近我的敌人直接排除 ──
            //     （先判这个：它明确在移动，不用等观察期）
            try
            {
                if (s.位置 != Vector3.Zero)
                {
                    var 上次距我 = Vector3.Distance(s.位置, Core.Me.Position);
                    var 现在距我 = Vector3.Distance(当前位置, Core.Me.Position);
                    if (现在距我 < 上次距我 - 0.5f)
                    {
                        累加采样(s, 当前位置, 现在);
                        return false;
                    }
                }
            }
            catch { }

            // 继续采样
            累加采样(s, 当前位置, 现在);

            // ── ① 观察时间不够 → 不判 ──
            if (观察毫秒 < 最短观察) return false;

            // ── ② 采样点不够 → 不判 ──
            if (s.采样数 < 最少采样数) return false;

            // ── ④ 平均移动速度够小 → 判定稳定 ──
            var 观察秒 = Math.Max(1f, 观察毫秒 / 1000f);
            return s.累计移动 / 观察秒 < 阈值;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 给**地面放置技能**挑一个落点（罩子 / 地星 / 庇护所 / 礼仪之铃）。
    ///
    /// **选位优先级**：
    ///   ① 优选敌人（一般是 Boss）**已经稳定足够久** → 放它脚下（覆盖最大）
    ///   ② 它在动 / 数据不足 / 没敌人 → **放自己脚下**（最保险）
    ///
    /// 因为 ① 有"观察满 3 秒"的门槛，所以**小怪刚出现那几秒会自动退回自己脚下**，
    /// 不会出现"扔在待机小怪脚下、它马上冲进来"的浪费。
    /// </summary>
    public static Vector3 地面技能位置(IBattleChara? 优选敌人 = null)
    {
        try
        {
            var 敌人 = 优选敌人;

            if (敌人 == null) 敌人 = HealTargetHelper.当前目标();
            if (敌人 == null) 敌人 = HealTargetHelper.当前目标()?.GetCurrTarget();

            if (敌人 != null && 敌人.CurrentHp > 0 && 移动很少(敌人))
            {
                return 敌人.Position;
            }
        }
        catch { }

        return Core.Me.Position;
    }

    /// <summary>
    /// **任意目标（含玩家）是不是站得稳** —— 和 `移动很少()` 同一套判据，
    /// 但**不排除"靠近我的人"**。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  [!] 为什么需要单独一个方法
    ///
    ///    `移动很少()` 里有一条「还在持续靠近我的敌人直接排除」——
    ///    那是为**敌人**写的（怪冲过来时采样间隔可能抓不到，得提前排除）。
    ///    但**队友**（尤其坦克）在道中就是会朝我走过来 ——
    ///    用那条会把"T 正常往前拉怪"误判成不稳定，反而永远铺不出罩子。
    ///
    ///  [!] 用于什么问题
    ///    用户实测：「小怪阶段 T 在路上停了一下 -> 误判放了罩子」。
    ///    判据不能只看"我自己在不在移动"，必须**T 也停了**才算站定。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static bool 玩家站得稳(IBattleChara? 目标, float 阈值 = 默认阈值)
    {
        if (目标 == null) return false;

        try
        {
            var id = 目标.GameObjectId;
            var 现在 = TimeHelper.Now();
            var 当前位置 = 目标.Position;

            if (!_记录.TryGetValue(id, out var s))
            {
                _记录[id] = new 采样
                {
                    位置 = 当前位置,
                    时间 = 现在,
                    累计移动 = 0,
                    采样数 = 0,
                    首次观察 = 现在,
                };
                return false;      // [!] 数据不足一律 false —— 宁可多等，不要误铺
            }

            累加采样(s, 当前位置, 现在);

            var 观察毫秒 = 现在 - s.首次观察;
            if (观察毫秒 < 最短观察) return false;
            if (s.采样数 < 最少采样数) return false;

            var 观察秒 = Math.Max(1f, 观察毫秒 / 1000f);
            return s.累计移动 / 观察秒 < 阈值;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>这个敌人现在的采样状态（调试面板用）</summary>
    public static string 状态描述(IBattleChara? 敌人)
    {
        if (敌人 == null) return "无目标";

        try
        {
            if (!_记录.TryGetValue(敌人.GameObjectId, out var s)) return "无数据";

            var 观察秒 = (TimeHelper.Now() - s.首次观察) / 1000f;
            var 就绪 = 观察秒 * 1000 >= 最短观察 && s.采样数 >= 最少采样数;

            return $"{观察秒:F1}s / 采样{s.采样数} / 移动{s.累计移动:F1}m / {(就绪 ? "已就绪" : "观察中")}";
        }
        catch
        {
            return "读取失败";
        }
    }

    /// <summary>清理过期记录（每帧调一下，很轻）</summary>
    public static void 清理()
    {
        try
        {
            // 位移记录也要清过期 —— 不清会随敌人数无限增长
            if (_上次位置.Count > 64) _上次位置.Clear();

            if (_记录.Count == 0) return;

            var 现在 = TimeHelper.Now();
            var 要删 = new List<ulong>();

            foreach (var kv in _记录)
            {
                if (现在 - kv.Value.时间 > 记录过期) 要删.Add(kv.Key);
            }

            foreach (var id in 要删) _记录.Remove(id);
        }
        catch { }
    }

    /// <summary>换本 / 战斗重置时清空</summary>
    public static void 重置()
    {
        _记录.Clear();
        _上次位置.Clear();
    }
}
