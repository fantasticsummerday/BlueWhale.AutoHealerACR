using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// **正在读条的机制** —— 从 Boss 读条**实测**确定"接下来打过来的是什么"。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么需要它（用户需求：把数值接进伤害预测）★
///
///    `伤害预测.cs` 原来判断"是哪个机制"是**猜**的：
///        var 机制 = TimelineManager.下一条机制();
///        // 用"时间轴的下一条"当作"刚打过来的那一下"
///    这套猜法有三个问题：
///      ① 时间轴只有 **45%** 的副本有（286/638）
///      ② 时间轴的对齐会漂（尤其灭了重来、或跳阶段）
///      ③ 时间轴的下一条**未必**就是刚打的那一下（同时有两条在跑时）
///
///    ⇒ 猜错的代价很大：把 A 机制的观察值记到 B 的名字上，
///      之后所有关于 B 的预测都是错的，而且**从日志上看不出来**。
///
///  ★ 本类给出的东西 ★
///
///    Boss **开始读条**时就能确定：
///      · 机制身份（ActionId -> `机制数值表`）—— 实测，不是猜
///      · **还有几秒打过来**（用官表的读条时长，比时间轴更准）
///      · 射程 / 是否在时间轴里（三方交叉验证）
///
///  ⇒ 这让预测从"事后补偿"变成"**事中预判**"：
///      Boss 读条 3 秒的 AOE -> 立刻知道"3 秒后有一发"，
///      而不是等打完看到掉血才知道。
///
///  [!] 只能覆盖**读条技能**。瞬发机制没有读条可看，
///      仍然依赖时间轴 + 观察 —— 这点必须说清楚，不能假装全能。
///      官表里 28629/45490 有读条时间，但**真正会打人的机制**
///      里读条占比更高（大伤害 AOE 基本都是读条）。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 机制读条
{
    /// <summary>一条正在读的机制</summary>
    public sealed class 在读
    {
        /// <summary>施法者的 ActionId</summary>
        public uint 技能Id;

        /// <summary>名字（取自 `机制数值表`）</summary>
        public string 名 = "";

        /// <summary>读条总时长（秒）—— 官表值，比时间轴更准</summary>
        public float 总长;

        /// <summary>已经读了多久（秒）</summary>
        public float 已读;

        /// <summary>**还有几秒打过来**（= 总长 - 已读）</summary>
        public float 剩余;

        /// <summary>射程（米，0 = 未知）</summary>
        public float 射程;

        /// <summary>施法者实例 id</summary>
        public ulong 施法者Id;

        public string 施法者名 = "";

        /// <summary>它是不是冲着"我方"读的（目标是我方成员）</summary>
        public bool 打我方;

        /// <summary>
        /// 会不会打到**玩家自己**。
        ///
        /// [!] 这里只能给"可能"：真正的判定要看技能形状（圆形/扇形/直线）
        ///     和落点，而游戏 API 不暴露这些。
        ///     所以：单体技看目标是不是自己；AOE 技保守地当成"可能吃到"。
        ///     宁可多算一点，也别漏算（漏算 = 没预铺 = 死人）。
        /// </summary>
        public bool 可能打我;

        /// <summary>在 cactbot 时间轴里出现过吗（三方交叉验证）</summary>
        public bool 在时间轴;

        /// <summary>时间轴里的出现次数</summary>
        public int 时间轴次数;

        /// <summary>找得到权威数据吗（false = 官表里没有这条）</summary>
        public bool 有权威数据;

        /// <summary>
        /// **这次读条是「死刑」还是「AOE」** —— AI 铺减伤时的**第一个决定**。
        ///
        /// ══════════════════════════════════════════════════════════════════
        ///  [!] 为什么必须区分（审查发现）★
        ///
        ///    本地靠两个不同 API 就能区分（`MitigationHelper.cs`）：
        ///        `targetCastingIsBossAOE`             -> 全体 AOE
        ///        `targetCastingIsDeathSentenceWithTime` -> 死刑（单体，打坦克）
        ///
        ///    而 AI 只看到「Boss 在读条 N 秒」，**没有这个区分** ——
        ///    可这恰恰是它要做的第一个决定：
        ///        · 死刑 -> 给**坦克**单体减伤 / 单盾
        ///        · AOE  -> 给**团队**减伤 / 群盾
        ///    给错了就是白交一个长 CD（几十秒到两分钟）。
        /// ══════════════════════════════════════════════════════════════════
        /// </summary>
        public bool 是死刑;

        /// <summary>是不是会打全队的 AOE 读条</summary>
        public bool 是AOE;
    }

    private static readonly List<在读> _缓存 = new();
    private static long _缓存时刻;
    private const int 缓存毫秒 = 150;

    /// <summary>
    /// **当前正在读条的机制**（可能多条 —— 多个敌人同时读）。
    ///
    /// [!] 150ms 缓存：这个方法会被每帧调多次（预测 / AI 局面 / 界面），
    ///     而遍历敌人 + 查表不便宜。150ms 远小于任何读条时长，不会漏。
    /// </summary>
    public static IReadOnlyList<在读> 当前()
    {
        var 现在 = TimeHelper.Now();
        if (现在 - _缓存时刻 < 缓存毫秒) return _缓存;

        _缓存.Clear();
        _缓存时刻 = 现在;

        try
        {
            var 我 = Core.Me;
            if (我 == null) return _缓存;

            foreach (var 敌 in Data.AllHostileTargets)
            {
                if (敌 == null) continue;

                try
                {
                    if (!敌.IsCasting) continue;

                    var 技能Id = 敌.CastActionId;
                    if (技能Id == 0) continue;

                    var 总 = 敌.TotalCastTime;
                    var 已 = 敌.CurrentCastTime;
                    if (总 <= 0f) continue;

                    var 权威 = 机制数值表.取(技能Id);

                    var 读 = new 在读
                    {
                        技能Id = 技能Id,
                        施法者Id = 敌.GameObjectId,
                        施法者名 = 名(敌),
                        已读 = 已,
                        有权威数据 = 权威 != null,
                    };

                    // ══════════════════════════════════════════════════════════
                    //  ★ 死刑 / AOE 分类 —— AI 铺减伤的**第一个决定** ★
                    //
                    //  [!] 用和本地 `减伤Helper` **完全相同**的两个 API
                    //      （`MitigationHelper.cs`）：
                    //        targetCastingIsBossAOE             -> 全体 AOE
                    //        targetCastingIsDeathSentenceWithTime -> 死刑（单体，打坦克）
                    //
                    //  [!] 为什么要传给 AI：它原来只看到「Boss 在读条 N 秒」，
                    //      于是不知道给**坦克单体减伤**还是**团队减伤/群盾** ——
                    //      给错了就是白交一个几十秒到两分钟的长 CD。
                    //
                    //  [!] 用 2500ms 提前量（和本地预铺窗口一致）——
                    //      不做成两个不同的数，免得"本地在铺、AI 以为还早"。
                    // ══════════════════════════════════════════════════════════
                    try
                    {
                        读.是AOE = AEAssist.Helper.TargetHelper.targetCastingIsBossAOE(敌, 2500);
                    }
                    catch { }
                    
                    try
                    {
                        读.是死刑 = AEAssist.Helper.TargetHelper
                            .targetCastingIsDeathSentenceWithTime(敌, 2500);
                    }
                    catch { }

                    if (权威 != null)
                    {
                        读.名 = 权威.名;
                        读.射程 = 权威.射程;
                        读.在时间轴 = 权威.在时间轴;
                        读.时间轴次数 = 权威.次数;
                    }
                    else
                    {
                        // 官表里没有 -> 用游戏里读到的名字（信息少但比空着好）
                        读.名 = "";
                    }

                    // ⚠️ 剩余时间用**游戏实测的**总长，不用官表值。
                    //    官表是"标准读条时长"，实际会因急速/减速/打断而变。
                    //    实测值永远比表值准 —— 表值只在"还没开始读"时有用。
                    读.总长 = 总;
                    读.剩余 = MathF.Max(0f, 总 - 已);

                    // ── 冲着谁读的 ──
                    try
                    {
                        var 读条目标 = 敌.CastTargetObjectId;
                        var 目标 = 读条目标 == 我.GameObjectId ? 我 : null;

                        if (目标 != null) 读.可能打我 = true;

                        // 目标是我方成员 -> 打我方
                        if (读条目标 != 0)
                        {
                            foreach (var r in 可治疗())
                            {
                                if (r.GameObjectId == 读条目标) { 读.打我方 = true; break; }
                            }
                        }

                        // [!] 没读到目标 或 目标是敌人自己 -> 多半是**以自己为中心的 AOE**。
                        //     对玩家来说这同样危险（范围技照样打到我们），
                        //     所以保守地标成"可能打我"。
                        //     漏算的代价是"没预铺 -> 死人"，多算的代价只是"多看一眼"。
                        if (读条目标 == 0) 读.可能打我 = true;
                    }
                    catch { 读.可能打我 = true; }

                    _缓存.Add(读);
                }
                catch { }
            }

            // 最快打过来的排前面 —— 调用方通常只关心"最近的那一发"
            _缓存.Sort((a, b) => a.剩余.CompareTo(b.剩余));
        }
        catch { }

        return _缓存;
    }

    /// <summary>最近一发（没有返回 null）</summary>
    public static 在读? 最近()
    {
        var 全 = 当前();
        return 全.Count > 0 ? 全[0] : null;
    }

    /// <summary>
    /// **未来 <paramref name="秒"/> 秒内会不会有读条技落地** —— 给预铺用。
    ///
    /// [!] 这是本类最有价值的用法：
    ///     时间轴说"30 秒后有机制"时，你不知道该不该**现在**预铺；
    ///     而读条说"2.4 秒后落地"时，预铺时机就明确了。
    /// </summary>
    public static bool 有落地(float 秒)
    {
        foreach (var r in 当前())
            if (r.剩余 <= 秒) return true;
        return false;
    }

    /// <summary>未来 <paramref name="秒"/> 秒内落地的最强一发（按是否在时间轴/次数排序的粗排）</summary>
    public static 在读? 最强(float 秒)
    {
        try
        {
            在读? 最 = null;
            var 最分 = -1f;
            foreach (var r in 当前())
            {
                if (r.剩余 > 秒) continue;
                // 粗排：在时间轴里的加分；出现次数越多越可能是真机制
                var 分 = (r.在时间轴 ? 100f : 0f) + MathF.Min(50f, r.时间轴次数);
                // 打我的加分
                if (r.可能打我) 分 += 30f;
                if (分 > 最分) { 最分 = 分; 最 = r; }
            }
            return 最;
        }
        catch { return null; }
    }

    /// <summary>给 AI / 日志的一行描述（没有在读的返回空串）</summary>
    public static string 描述()
    {
        try
        {
            var 全 = 当前();
            if (全.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            foreach (var r in 全)
            {
                var 名 = string.IsNullOrEmpty(r.名) ? ("技能" + r.技能Id) : r.名;
                sb.Append("  ").Append(r.施法者名).Append(" 正在读 ").Append(名);
                sb.Append("（").Append(r.剩余.ToString("0.0")).Append("秒后落地");
                if (r.射程 > 0f) sb.Append("，射程").Append((int)r.射程).Append("m");
                if (r.在时间轴) sb.Append("，时间轴确认");
                if (!r.有权威数据) sb.Append("，无权威数据");
                if (r.可能打我) sb.Append("，可能命中我");
                sb.AppendLine("）");
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    /// <summary>诊断：缓存里几条</summary>
    public static int 条数 => 当前().Count;

    // ==================== 小工具 ====================

    private static string 名(IBattleChara 目标)
    {
        try { return 目标.Name.ToString(); } catch { return "?"; }
    }

    private static List<IBattleChara> 可治疗()
    {
        try { return HealTargetHelper.可治疗队友(50f); }
        catch { return new List<IBattleChara>(); }
    }
}
