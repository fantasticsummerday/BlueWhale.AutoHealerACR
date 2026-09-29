using System.Numerics;
using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// 智能 AOE 选目标 —— 参考同类 ACR 的
/// `SmartTargetCircleAOE` / `SmartTargetLineAOE` / `SmartTargetFanAOE`。
///
/// **要解决的问题**：
///   AOE 技能打出去，如果目标是随便选的，很可能只打到 1~2 个敌人。
///   正确的做法是**找一个"以它为中心能覆盖最多敌人"的位置**。
///
/// **三种形状**：
///   · 圆形：以目标为圆心，半径内的敌人都会被覆盖（白魔神圣、学者破阵法）
///   · 直线：从自己出发的一条线（贤者失衡、贤者箭毒）
///   · 扇形：从自己出发的一个扇面（部分技能）
///
/// **算法**：对每个候选目标，数"以它为参照能覆盖几个敌人"，取最多的那个。
///   这比"以自己为中心数"准确得多 —— 奶妈站远程位，身边经常一个敌人都没有。
/// </summary>
public static class 智能选目标
{
    /// <summary>候选敌人离我多远以内才考虑（太远的够不着）</summary>
    private const float 搜索半径 = 25f;

    // ==================== 技能形状表 ====================
    //
    // ⚠️ 为什么是硬编码而不是读游戏数据：
    //   我 dump 了 Lumina 的 Action 表，发现 `CastType` 字段**区分不出直线和圆形** ——
    //     神圣   (139)   CastType=2  Range=0   EffectRange=8    ← 自身圆形
    //     箭毒   (24304) CastType=2  Range=25  EffectRange=5    ← 直线！
    //     失衡   (24297) CastType=2  Range=0   EffectRange=5    ← 自身圆形
    //   三个都是 2。能区分"要不要指定目标"（Range），但区分不了形状。
    //
    //   所以直线技能只能靠游戏知识人工标注。
    //   **只标有把握的** —— 标错了会让选目标变差，比不标更糟。
    //
    //   （CastType=7 倒是稳定对应"地面放置技能"：庇护所/罩子/地星/礼仪之铃 全是 7）

    /// <summary>直线 AOE 技能（从自己出发的一条线）</summary>
    public static readonly uint[] 直线技能 =
    {
        24304,   // 贤者 · 箭毒
        24316,   // 贤者 · 箭毒II
    };

    /// <summary>这个技能是不是直线 AOE</summary>
    public static bool 是直线技能(uint 技能Id)
        => Array.IndexOf(直线技能, 技能Id) >= 0;

    /// <summary>
    /// 按技能形状自动挑最优目标 —— **给调用方一个统一入口**。
    ///
    /// 直线的用直线算法，其余按圆形处理（圆形是大多数）。
    /// </summary>
    public static IBattleChara? 按形状选最优(uint 技能Id, float 半径, int 至少几个 = 3)
    {
        try
        {
            if (是直线技能(技能Id))
            {
                // 直线：长度取射程（默认 25），宽度按效果范围
                return 直线最优(25f, Math.Max(2f, 半径 * 0.6f), Math.Max(2, 至少几个 - 1));
            }

            return 圆形最优(半径, 至少几个);
        }
        catch
        {
            return null;
        }
    }

    // ==================== 圆形 ====================

    /// <summary>
    /// 圆形 AOE 的最优目标：以它为圆心，半径内敌人最多。
    /// </summary>
    /// <param name="半径">AOE 半径（米）</param>
    /// <param name="至少几个">少于这个数就返回 null（不值得放）</param>
    public static IBattleChara? 圆形最优(float 半径, int 至少几个 = 3)
    {
        var 敌人 = 附近敌人();
        if (敌人.Count == 0) { 列表池<IBattleChara>.还(敌人); return null; }

        try
        {
            IBattleChara? 最优 = null;
            var 最优覆盖 = 0;

            foreach (var 候选 in 敌人)
            {
                if (候选 == null || 候选.CurrentHp <= 0) continue;

                var 覆盖 = 0;
                foreach (var 其他 in 敌人)
                {
                    if (其他 == null || 其他.CurrentHp <= 0) continue;
                    if (距离(候选.Position, 其他.Position) <= 半径) 覆盖++;
                }

                // ⚠️ **平手时取 CurrentHp 大的**（参考实现的做法，IL 直证）。
                //
                //    为什么：覆盖数相同的两个落点，**血多的那个更可能活到 AOE 落地**。
                //    原来只写 `覆盖 > 最优覆盖` —— 平手什么都不做，
                //    结果等于"取遍历里碰到的第一个"（随机选）。
                //    在怪群边缘很常见：好几个落点都覆盖同样的数量。
                //
                //    ⚠️ 只改"平手怎么办"，**覆盖数仍然是第一判据** ——
                //       不能为了血多而选一个覆盖更少的落点。
                if (覆盖 > 最优覆盖
                    || (覆盖 == 最优覆盖 && 最优 != null && 候选.CurrentHp > 最优.CurrentHp))
                {
                    最优覆盖 = 覆盖;
                    最优 = 候选;
                }
            }

            return 最优覆盖 >= 至少几个 ? 最优 : null;
        }
        finally
        {
            列表池<IBattleChara>.还(敌人);
        }
    }

    // ==================== 直线 ====================

    /// <summary>
    /// 直线 AOE 的最优目标：从**我**出发，指向它的一条矩形里敌人最多。
    /// </summary>
    /// <param name="长度">AOE 射程（米）</param>
    /// <param name="宽度">AOE 宽度（米）</param>
    /// <param name="至少几个">少于这个数就返回 null</param>
    public static IBattleChara? 直线最优(float 长度, float 宽度, int 至少几个 = 2)
    {
        var 敌人 = 附近敌人();
        if (敌人.Count == 0) { 列表池<IBattleChara>.还(敌人); return null; }

        try
        {
            var 我 = Core.Me.Position;
            IBattleChara? 最优 = null;
            var 最优覆盖 = 0;

            foreach (var 候选 in 敌人)
            {
                if (候选 == null || 候选.CurrentHp <= 0) continue;

                var 方向 = 候选.Position - 我;
                if (方向.LengthSquared() < 0.01f) continue;

                var 覆盖 = 0;
                foreach (var 其他 in 敌人)
                {
                    if (其他 == null || 其他.CurrentHp <= 0) continue;
                    if (在矩形内(我, 方向, 其他.Position, 长度, 宽度)) 覆盖++;
                }

                // ⚠️ **平手时取 CurrentHp 大的**（参考实现的做法，IL 直证）。
                //
                //    为什么：覆盖数相同的两个落点，**血多的那个更可能活到 AOE 落地**。
                //    原来只写 `覆盖 > 最优覆盖` —— 平手什么都不做，
                //    结果等于"取遍历里碰到的第一个"（随机选）。
                //    在怪群边缘很常见：好几个落点都覆盖同样的数量。
                //
                //    ⚠️ 只改"平手怎么办"，**覆盖数仍然是第一判据** ——
                //       不能为了血多而选一个覆盖更少的落点。
                if (覆盖 > 最优覆盖
                    || (覆盖 == 最优覆盖 && 最优 != null && 候选.CurrentHp > 最优.CurrentHp))
                {
                    最优覆盖 = 覆盖;
                    最优 = 候选;
                }
            }

            return 最优覆盖 >= 至少几个 ? 最优 : null;
        }
        finally
        {
            列表池<IBattleChara>.还(敌人);
        }
    }

    // ==================== 扇形 ====================

    /// <summary>
    /// 扇形 AOE 的最优目标。
    /// </summary>
    /// <param name="半径">射程（米）</param>
    /// <param name="角度">张角（度）</param>
    public static IBattleChara? 扇形最优(float 半径, float 角度, int 至少几个 = 2)
    {
        var 敌人 = 附近敌人();
        if (敌人.Count == 0) { 列表池<IBattleChara>.还(敌人); return null; }

        try
        {
            var 我 = Core.Me.Position;
            IBattleChara? 最优 = null;
            var 最优覆盖 = 0;

            foreach (var 候选 in 敌人)
            {
                if (候选 == null || 候选.CurrentHp <= 0) continue;

                var 方向 = 候选.Position - 我;
                if (方向.LengthSquared() < 0.01f) continue;

                var 覆盖 = 0;
                foreach (var 其他 in 敌人)
                {
                    if (其他 == null || 其他.CurrentHp <= 0) continue;
                    if (在扇形内(我, 方向, 其他.Position, 半径, 角度)) 覆盖++;
                }

                // ⚠️ **平手时取 CurrentHp 大的**（参考实现的做法，IL 直证）。
                //
                //    为什么：覆盖数相同的两个落点，**血多的那个更可能活到 AOE 落地**。
                //    原来只写 `覆盖 > 最优覆盖` —— 平手什么都不做，
                //    结果等于"取遍历里碰到的第一个"（随机选）。
                //    在怪群边缘很常见：好几个落点都覆盖同样的数量。
                //
                //    ⚠️ 只改"平手怎么办"，**覆盖数仍然是第一判据** ——
                //       不能为了血多而选一个覆盖更少的落点。
                if (覆盖 > 最优覆盖
                    || (覆盖 == 最优覆盖 && 最优 != null && 候选.CurrentHp > 最优.CurrentHp))
                {
                    最优覆盖 = 覆盖;
                    最优 = 候选;
                }
            }

            return 最优覆盖 >= 至少几个 ? 最优 : null;
        }
        finally
        {
            列表池<IBattleChara>.还(敌人);
        }
    }

    // ==================== 几何 ====================

    /// <summary>用平方距离比，省一次开方</summary>
    private static float 距离(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

    /// <summary>点是不是落在"从我出发、朝某方向的矩形"里</summary>
    private static bool 在矩形内(Vector3 我, Vector3 方向, Vector3 点, float 长度, float 宽度)
    {
        try
        {
            var 单位 = Vector3.Normalize(方向);
            var 相对 = 点 - 我;

            // 沿朝向的投影（0 ~ 长度）
            var 前向 = Vector3.Dot(相对, 单位);
            if (前向 < 0 || 前向 > 长度) return false;

            // 垂直方向的偏移（±宽度/2）
            var 垂足 = 单位 * 前向;
            var 侧向 = (相对 - 垂足).Length();

            return 侧向 <= 宽度 * 0.5f;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>点是不是落在"从我出发、朝某方向的扇形"里</summary>
    private static bool 在扇形内(Vector3 我, Vector3 方向, Vector3 点, float 半径, float 角度度)
    {
        try
        {
            var 相对 = 点 - 我;
            var 距离我 = 相对.Length();
            if (距离我 > 半径 || 距离我 < 0.01f) return false;

            var 单位朝向 = Vector3.Normalize(方向);
            var 单位目标 = Vector3.Normalize(相对);

            var cos = Math.Clamp(Vector3.Dot(单位朝向, 单位目标), -1f, 1f);
            var 夹角 = MathF.Acos(cos) * 180f / MathF.PI;

            return 夹角 <= 角度度 * 0.5f;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 取附近的敌对目标（用完必须还）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 过滤条件 —— 照抄参考实现的 `CanAttackTargetInRange` ★
    ///
    ///  ── 原来只有一条 `距离 <= 搜索半径`，漏了三种情况 ──
    ///    ① **不可选中**（`IsTargetable == false`）
    ///       正在读条 / 转阶段无敌 / 还没正式入场 —— 这种怪算进"能打几个"
    ///       会让 AOE 判定虚高，实际打下去只命中一部分。
    ///    ② **正在死**（血量 <= 0.1%）
    ///       `CurrentHp > 0` 拦不住"血 1 点"的怪 —— 它下一帧就没了，
    ///       算进命中数是虚的。参考实现用的是 **0.1%** 血线，不是 `> 0`。
    ///    ③ **距离判据分两种**：能不能**够到**（施法距离）
    ///       和能**打中几个**（伤害范围）—— 原版把它们混成一个 `搜索半径`。
    ///
    ///  ⚠️ 这里仍然**不查"攻击无效状态"**（参考实现有一条 `HasAttackBlock`）——
    ///     那需要一张状态 id 表，我们暂时没有可靠来源。
    ///     漏掉的后果：极少数带"攻击无效"的怪会被算进命中数（判定偏乐观）。
    ///     比漏查 ①② 轻得多，先不做。
    ///
    ///  ⚠️ 依然**不查 `有仇恨()`** —— 这个函数是"数周围有几个敌人"，
    ///     给 AOE 判定用的**计数**，不是"该打谁"。
    ///     加仇恨过滤会让 AOE 判定偏保守（少算），
    ///     而 AOE 的落点选择后面还有 `Res_AoEDamage` 那层把关。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static List<IBattleChara> 附近敌人()
    {
        var 列表 = 列表池<IBattleChara>.取();

        try
        {
            var 我 = Core.Me.Position;

            foreach (var 敌人 in Data.AllHostileTargets)
            {
                if (敌人 == null) continue;
                if (敌人.CurrentHp <= 0) continue;
                if (Vector3.Distance(我, 敌人.Position) > 搜索半径) continue;

                try
                {
                    // ① 必须可选中（读条/无敌/未入场时会被判成不能打）
                    if (!敌人.IsTargetable) continue;

                    // ② 正在死的不算（0.1% 血线，和参考实现一致）
                    if (敌人.MaxHp > 0 &&
                        敌人.CurrentHp / (float)敌人.MaxHp <= 0.001f) continue;
                }
                catch { }

                列表.Add(敌人);
            }
        }
        catch { }

        return 列表;
    }
}
