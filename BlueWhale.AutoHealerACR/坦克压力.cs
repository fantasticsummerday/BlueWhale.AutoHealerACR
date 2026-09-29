using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// 坦克压力评估 —— 给 AI 提供"现在 T 扛得住吗"的判断依据。
///
/// ══════════════════════════════════════════════════════════════════
///  用户需求：AI 决策要能看出
///    ① 接战时 T 的血量波动是否很大
///    ② 减伤是否充足
///    ③ 怪是否很多
///
///  ── 关于"减伤是否充足"，我换了判据 ──
///
///    **没有**去读坦克身上的减伤 buff 列表。
///    理由：坦克减伤 buff 有二十多种（铁壁/复仇/预警/血乱/暗影之心…），
///    ID 我**没有逐个验证过** —— 凭印象写一张表就是第 ⑫ 类 bug
///    （未验证的 API 语义当判断依据）。
///
///    改用**血量波动**来间接判断 —— 它的好处是：
///      · 数据完全确定（自己采样，不依赖任何没验证的 ID）
///      · 直接反映"减伤到底有没有起作用"（结果导向，比看 buff 更本质）
///      · T 有减伤 → 血量平稳；没减伤 → 血量剧烈波动
///
///    唯一的例外是**无敌**：那个有 AEAssist 现成的 `Data.InvincibleStatus`，
///    已验证过，所以直接用。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 坦克压力
{
    /// <summary>采样间隔（毫秒）</summary>
    private const int 采样间隔 = 250;

    /// <summary>保留多久的采样（毫秒）</summary>
    private const int 窗口毫秒 = 5000;

    private class 采样点
    {
        public long 时间;
        public float 血量比例;
    }

    private static readonly List<采样点> _采样 = new();
    private static long _上次采样;

    /// <summary>
    /// 血量波动幅度（0~1）。
    ///
    /// 含义：最近 5 秒内 T 血量的**最大跌幅**。
    ///   · < 0.10 → 平稳（减伤充足，或者根本没在挨打）
    ///   · 0.10 ~ 0.30 → 有压力
    ///   · > 0.30 → 剧烈波动（减伤不足，或者正在吃大招）
    /// </summary>
    public static float 波动幅度 { get; private set; }

    /// <summary>最近 5 秒最低血量</summary>
    public static float 最低血量 { get; private set; } = 1f;

    /// <summary>最近 5 秒最高血量</summary>
    public static float 最高血量 { get; private set; } = 1f;

    /// <summary>T 现在是不是无敌（AEAssist 已验证的 API）</summary>
    public static bool 无敌中 { get; private set; }

    /// <summary>T 身上有没有减伤</summary>
    public static bool 有减伤 { get; private set; }

    /// <summary>命中的减伤 buff 名（诊断用）</summary>
    public static string 命中的减伤 { get; private set; } = "";

    // ══════════════════════════════════════════════════════════════════
    //  坦克减伤 buff 表
    //
    //  ⚠️ 这些 ID **不是凭印象写的** —— 是用 tools/SpellDump 从游戏数据里
    //     导出来的（dump_tankbuffs.tsv）：**从坦克职业的减伤技能反查
    //     它们给自己挂的 buff**，再用技能名人工核对一遍。
    //
    //  来源技能（数据里的 ClassJob）：
    //    骑士(19)：壁垒22 / 圣光幕帘3540 / 干预7382 / 武装戍卫7385 /
    //              圣盾阵25746 / 极致防御36920 / 神圣领域30(无敌)
    //    战士(21)：原初之魂49 / 原初的直觉3551 / 原初的解放7389 / 摆脱7388 /
    //              原初的勇猛16464 / 原初的血气25751 / 泰然自若3552 / 戮罪36923
    //    暗骑(32)：暗黑布道16471 / 血乱7390 / 献奉25754 / 暗影卫36927
    //    枪刃(37)：星云16148 / 极光16151 / 光之心16160 /
    //              刚玉之心25758 / 大星云36935
    //
    //  ⚠️ 同一个技能名会对应**多个 buff ID**（主效果 + 副效果，或者
    //     不同等级版本），所以每个技能都把导出的 ID 全列上 ——
    //     命中任意一个就算有减伤。
    //
    //  ⚠️ 已排除的：
    //     · 血壤(5051)      —— 枪刃的弹药资源，不是减伤
    //     · 命运之印(4294)  —— 用途不明，宁可不判
    //     · 金刚极意 / 疾风极意 —— **武僧技能**（我一开始把武僧 20
    //       当成了坦克，这个错误是靠数据对照发现的）
    //
    //  维护提示：游戏版本更新后，用 tools/SpellDump 重新导一次
    //           dump_tankbuffs.tsv，对照本表增删。
    // ══════════════════════════════════════════════════════════════════
    private static readonly uint[] 坦克减伤Buff =
    {
        // ── 骑士 ──
        77,                              // 壁垒
        726, 727, 1362, 2168, 2169,      // 圣光幕帘
        1174, 2020,                      // 干预
        1175,                            // 武装戍卫
        2674, 3026,                      // 圣盾阵
        3829,                            // 极致防御
        82, 1302,                        // 神圣领域（无敌，也当减伤算）

        // ── 战士 ──
        411, 1398,                       // 原初之魂
        735,                             // 原初的直觉
        1177, 1303,                      // 原初的解放
        1457, 1993,                      // 摆脱
        1857, 2061, 2227,                // 原初的勇猛
        2678, 3030,                      // 原初的血气
        2681,                            // 泰然自若（自疗，等于有效血量）
        3832,                            // 戮罪

        // ── 暗骑 ──
        1894, 2171,                      // 暗黑布道
        1972, 1996, 3836,                // 血乱
        2682,                            // 献奉
        3835,                            // 暗影卫

        // ── 枪刃 ──
        1834, 3051,                      // 星云
        1835, 2065,                      // 极光
        1839, 2000,                      // 光之心
        2683, 4295,                      // 刚玉之心
        3838,                            // 大星云
    };

    /// <summary>每帧更新采样</summary>
    public static void 每帧更新()
    {
        try
        {
            var 现在 = TimeHelper.Now();
            if (现在 - _上次采样 < 采样间隔) return;
            _上次采样 = 现在;

            var T = HealTargetHelper.血量最低的坦克();
            if (T == null)
            {
                // 没坦克（比如四人本 T 死了）→ 清空数据，别拿旧数据骗 AI
                if (_采样.Count > 0) 清空();
                return;
            }

            _采样.Add(new 采样点 { 时间 = 现在, 血量比例 = T.血量比例() });

            // 丢掉超窗口的
            while (_采样.Count > 0 && 现在 - _采样[0].时间 > 窗口毫秒)
            {
                _采样.RemoveAt(0);
            }

            // 算波动
            if (_采样.Count >= 2)
            {
                最高血量 = _采样[0].血量比例;
                最低血量 = _采样[0].血量比例;

                foreach (var p in _采样)
                {
                    if (p.血量比例 > 最高血量) 最高血量 = p.血量比例;
                    if (p.血量比例 < 最低血量) 最低血量 = p.血量比例;
                }

                波动幅度 = 最高血量 - 最低血量;
            }

            // 无敌检测
            // ⚠️ Data.InvincibleStatus 是 HashSet<uint>（一整组无敌 buff ID），
            //    不是单个 ID —— 要逐个查，命中任意一个就算无敌。
            无敌中 = false;
            try
            {
                foreach (var id in Data.InvincibleStatus)
                {
                    if (T.HasAura(id)) { 无敌中 = true; break; }
                }
            }
            catch { 无敌中 = false; }

            // ── 减伤检测（用游戏数据导出的准确 ID）──
            有减伤 = false;
            命中的减伤 = "";
            try
            {
                foreach (var id in 坦克减伤Buff)
                {
                    if (!T.HasAura(id)) continue;
                    有减伤 = true;
                    命中的减伤 = SpellIds.反查(id);
                    if (string.IsNullOrEmpty(命中的减伤)) 命中的减伤 = "buff " + id;
                    break;
                }
            }
            catch { 有减伤 = false; }
        }
        catch { }
    }

    /// <summary>波动是不是很大（AI 该考虑给减伤/重点看护）</summary>
    public static bool 波动很大() => 波动幅度 >= 0.30f;

    /// <summary>波动是不是中等（该留意）</summary>
    public static bool 波动中等() => 波动幅度 >= 0.10f;

    /// <summary>
    /// 给 AI 看的一段描述。
    /// **只陈述事实，不做建议** —— 建议让 AI 自己给。
    /// </summary>
    public static string 状态描述()
    {
        try
        {
            var T = HealTargetHelper.血量最低的坦克();
            if (T == null) return "队伍里没有存活的坦克";

            var sb = new System.Text.StringBuilder();

            sb.AppendLine($"坦克：{T.Name}  当前血量 {T.血量比例() * 100f:F0}%");

            if (无敌中)
            {
                sb.AppendLine("  坦克当前处于【无敌】状态 —— 期间掉血是设计如此，不要浪费减伤和治疗");
                return sb.ToString();
            }

            if (_采样.Count < 2)
            {
                sb.AppendLine("  血量波动：数据不足（刚开始采样）");
                return sb.ToString();
            }

            // 减伤状态（用导出的准确 ID 判的）
            if (有减伤)
            {
                sb.AppendLine($"  当前减伤：有（{命中的减伤}）");
            }
            else
            {
                sb.AppendLine("  当前减伤：**没有**（T 身上看不到任何坦克减伤 buff）");
            }

            sb.AppendLine($"  近 5 秒血量波动幅度：{波动幅度 * 100f:F0}%"
                          + $"（区间 {最低血量 * 100f:F0}% ~ {最高血量 * 100f:F0}%）");

            if (波动很大())
            {
                sb.AppendLine(有减伤
                    ? "  -> 波动【很大】：有减伤还这么掉，说明伤害超出减伤覆盖（大伤害机制），重点看护"
                    : "  -> 波动【很大】且**没有减伤**：T 在裸扛，该考虑补减伤了");
            }
            else if (波动中等())
            {
                sb.AppendLine(有减伤
                    ? "  -> 波动【中等】：有减伤，压力可控"
                    : "  -> 波动【中等】且没有减伤：留意 T 的减伤是否断了");
            }
            else
            {
                sb.AppendLine(有减伤
                    ? "  -> 波动【平稳】：减伤覆盖良好"
                    : "  -> 波动【平稳】：虽然没减伤但也没在挨打（可能没接怪）");
            }

            return sb.ToString();
        }
        catch (Exception e)
        {
            return "坦克压力读取失败：" + e.Message;
        }
    }

    /// <summary>敌人数量描述（"怪是否很多"）</summary>
    public static string 敌人描述()
    {
        try
        {
            var 近 = HealTargetHelper.周围敌人数量(5f);
            var 中 = HealTargetHelper.周围敌人数量(10f);
            var 远 = HealTargetHelper.周围敌人数量(25f);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"敌人数量：5 米内 {近} 个 / 10 米内 {中} 个 / 25 米内 {远} 个");

            if (远 >= 6)
            {
                sb.AppendLine("  -> 怪【很多】：小怪阶段，群体减伤和 AOE 治疗价值高，但别把资源全花光");
            }
            else if (远 >= 3)
            {
                sb.AppendLine("  -> 怪【中等】：可以适当用 AOE");
            }
            else if (远 >= 1)
            {
                sb.AppendLine("  -> 怪【很少】：偏单体环境");
            }
            else
            {
                sb.AppendLine("  -> 视野内没有敌人");
            }

            return sb.ToString();
        }
        catch (Exception e)
        {
            return "敌人数量读取失败：" + e.Message;
        }
    }

    private static void 清空()
    {
        _采样.Clear();
        波动幅度 = 0f;
        最低血量 = 1f;
        最高血量 = 1f;
        无敌中 = false;
        有减伤 = false;
        命中的减伤 = "";
    }

    /// <summary>战斗重置 / 换本</summary>
    public static void 重置() => 清空();
}
