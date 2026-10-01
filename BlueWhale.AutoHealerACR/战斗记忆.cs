using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// 战斗记忆采集 —— **阶段 1：纯采集。**
///
/// ══════════════════════════════════════════════════════════════════
///  设计原则（很重要，别破坏它）：
///
///   ① **只写不读** —— 这个类不参与任何战斗决策。
///      它挂了、卡了、写失败了，战斗逻辑一点都不受影响。
///
///   ② **数据来源必须标记** —— 这是整个方案的安全阀。
///      如果分不清"这条是人类打的还是 ACR 自己打的"，
///      将来检索时就会变成**回音壁**：
///          ACR 做决策 → 存库 → 检索到 → 照做 → 又存库
///      越用越确信自己的习惯是对的，错误被固化。
///
///   ③ **结果延迟回填** —— 决策当下不知道好不好，
///      要等 +3s / +6s 的血量才能判断。
///
///  ── 数据格式：JSONL（每行一个 JSON）──
///     便于追加、便于用任何工具分析、坏一行不影响其他行。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 战斗记忆
{
    // ==================== 开关 ====================

    /// <summary>采集总开关。默认开 —— 采集本身不花钱也几乎不耗性能。</summary>
    public static bool 启用采集 = true;

    /// <summary>采样间隔（毫秒）。1 秒一次够了，太密是浪费。</summary>
    private const int 采样间隔 = 1000;

    /// <summary>回填延迟（毫秒）—— 决策后多久看结果</summary>
    private static readonly int[] 回填延迟 = { 3000, 6000 };

    // ==================== 数据结构 ====================

    /// <summary>决策来源 —— **这是安全阀，不能省**</summary>
    public enum 来源类型
    {
        未知 = 0,
        人类 = 1,       // 玩家手动按的 —— 这才是"参考答案"
        ACR = 2,        // 原版 HealerACR 逻辑
        AI = 3,         // BlueWhale 的 AI 决策
        其他ACR = 4,    // 同类的外部 ACR
    }

    /// <summary>一条决策记录（含延迟回填的结果）</summary>
    private sealed class 记录
    {
        public string 时间 = "";
        public uint 地图;
        public string 职业 = "";
        public int 等级;

        // ── 决策 ──
        public uint 技能;
        public string 技能名 = "";
        public string 来源 = "";
        public string 理由 = "";       // AI 决策时的理由

        // ── 决策瞬间的局面快照 ──
        public float 蓝量;
        public float[] 队伍血量 = Array.Empty<float>();
        public int 濒危人数;           // < 35%
        public int 受伤人数;           // < 70%
        public int 敌人数量;
        public float 最高敌人血量;
        public bool 敌人有读条;
        public int 以太;
        public bool 战斗中的;

        // ── 结果（延迟回填）──
        public float[] 结果3秒 = Array.Empty<float>();
        public float[] 结果6秒 = Array.Empty<float>();
        public bool 已回填3秒;
        public bool 已回填6秒;

        /// <summary>
        /// 决策之后、回填之前，队伍里**有人倒下了**。
        ///
        /// ⚠️ 这个字段以前**从来没有被赋值过**（编译器 CS0649 警告指出来了）——
        ///    也就是说它恒为 false，AI 提炼记忆时看到的"没人死过"是假的。
        ///    现在由 `回填()` 用 `死亡追踪.最近有人死亡()` 填上。
        /// </summary>
        public bool 当场死亡;

        /// <summary>记录创建时刻（TimeHelper.Now()）—— 回填时算"过了多久"用</summary>
        public long 出生时间;
    }

    // ==================== 状态 ====================

    private static readonly List<记录> _待回填 = new();
    private static readonly List<记录> _已采集 = new();

    private static long _上次采样;
    private static bool _上次在战斗;

    /// <summary>本次战斗的统计</summary>
    public static int 本次采集条数 => _已采集.Count;

    /// <summary>累计采集条数（跨战斗）</summary>
    public static int 累计条数 { get; private set; }

    /// <summary>最后一次落盘的文件路径</summary>
    public static string 最后文件 { get; private set; } = "";

    // ==================== 每帧驱动 ====================

    /// <summary>
    /// 每帧调用。**很轻** —— 只在到采样点时做一次快照，其余都是空转。
    /// </summary>
    public static void 每帧更新()
    {
        if (!启用采集) return;

        try
        {
            var 在战斗 = 战斗中();

            // ① 战斗刚结束 → 落盘
            if (_上次在战斗 && !在战斗)
            {
                落盘();
            }
            _上次在战斗 = 在战斗;

            if (!在战斗) return;

            // ② 回填到期的结果（每帧都查，但成本极低）
            回填();

            // ③ 按间隔做一次快照采样
            var 现在 = TimeHelper.Now();
            if (现在 - _上次采样 < 采样间隔) return;
            _上次采样 = 现在;

            采样();
        }
        catch
        {
            // 采集绝不能影响战斗 —— 任何异常都吞掉
        }
    }

    private static bool 战斗中()
    {
        try { return Core.Me.InCombat(); }
        catch { return false; }
    }

    // ==================== 采样 ====================

    /// <summary>
    /// 采一条"局面快照"。注意：**这一刻还没发生决策**，
    /// 所以技能字段是空的 —— 决策由 <see cref="记决策"/> 回填进来。
    /// </summary>
    private static void 采样()
    {
        var 快照 = 做快照();

        // 占位记录：技能等决策来填。如果这一秒没有决策，
        // 这条就不会跑到 _已采集 里（只有决策才产生有效记忆）。
        _当前快照 = 快照;
    }

    /// <summary>最近一次快照（决策发生时把它带上）</summary>
    private static 记录? _当前快照;

    private static 记录 做快照()
    {
        var 记录 = new 记录
        {
            时间 = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            地图 = HealerACR.Timeline.TimelineManager.当前地图Id,
            职业 = "未知",
            等级 = 减伤Helper.玩家等级(),
            战斗中的 = true,
        };

        try
        {
            var 我 = Core.Me;

            // 蓝量
            try { 记录.蓝量 = 我.CurrentMp / (float)Math.Max(1, 我.MaxMp); } catch { }

            // 队伍血量（排序后存 —— 顺序无关，只关心分布）
            try
            {
                var 血量 = new List<float>();
                if (PartyHelper.CastableAlliesWithin30 != null)
                {
                    foreach (var 人 in PartyHelper.CastableAlliesWithin30)
                    {
                        if (人 == null) continue;
                        血量.Add(人.CurrentHpPercent());
                    }
                }
                血量.Sort();
                记录.队伍血量 = 血量.ToArray();
                记录.濒危人数 = 血量.Count(h => h < 0.35f);
                记录.受伤人数 = 血量.Count(h => h < 0.70f);
            }
            catch { }

            // 敌人
            try
            {
                var 数量 = 0;
                var 最高血 = 0f;
                var 有读条 = false;

                foreach (var 敌人 in Data.AllHostileTargets)
                {
                    if (敌人 == null || 敌人.CurrentHp <= 0) continue;
                    数量++;

                    var 比例 = 敌人.CurrentHpPercent();
                    if (比例 > 最高血) 最高血 = 比例;

                    // IsCasting 这个 API 不存在 —— 用我自己的危险读条判断
                    try { if (减伤Helper.是危险读条(敌人)) 有读条 = true; } catch { }
                }

                记录.敌人数量 = 数量;
                记录.最高敌人血量 = 最高血;
                记录.敌人有读条 = 有读条;
            }
            catch { }

            // 以太 / 职业
            try
            {
                // ⚠️ `ClassJob` 是 `RowRef<ClassJob>`（**struct，不是可空引用**），
                //    所以不能用 `?.`（编译器报 CS0023）。
                //    读 `.Value.Name` 才拿得到名字 —— 和 `对局记录` / `AiSituation`
                //    用的是同一种读法，保持一致。
                // ★ 读之前先判有效性 —— 失效时给占位符，不要为了一个名字把游戏打崩
                    记录.职业 = (我.对象有效()) ? 我.ClassJob.Value.Name.ToString() : "未知";
            }
            catch { }

            try
            {
                记录.以太 = 我.GetAuraStack(304);   // 以太超流 buff
            }
            catch { }
        }
        catch { }

        return 记录;
    }

    // ==================== 记录决策 ====================

    /// <summary>
    /// 记一条决策。
    ///
    /// **必须在技能真的放出去的时候调**（不是"打算放"的时候）——
    /// 否则库里存的是"想做但没做"的计划，不是事实。
    /// </summary>
    public static void 记决策(uint 技能, string 技能名, 来源类型 来源, string 理由 = "")
    {
        if (!启用采集 || 技能 == 0) return;

        try
        {
            // 拿最近一次快照，把决策填进去
            var 记录 = _当前快照 ?? 做快照();

            记录.技能 = 技能;
            记录.技能名 = 技能名;
            记录.来源 = 来源.ToString();
            记录.理由 = 理由;

            记录.出生时间 = TimeHelper.Now();

            _待回填.Add(记录);
            _已采集.Add(记录);

            // 快照用掉了，下一个决策要配新的快照
            _当前快照 = null;
        }
        catch { }
    }

    /// <summary>
    /// 判断这次施放的来源。
    ///
    /// **这是安全阀** —— 记忆库最大的风险是"回音壁"：
    ///   ACR 做决策 → 存库 → 检索到 → 照做 → 又存库
    /// 分不清来源，就会把自己的习惯当成正确答案，错误被固化。
    ///
    /// 现在能区分「AI」和「ACR」两类；
    /// 「人类」需要外部手动标记（ACR 看不到玩家的手动按键）。
    /// </summary>
    public static 来源类型 判来源(uint 技能Id)
    {
        try
        {
            // AI 这一帧采纳的技能 → 标 AI
            if (AiSuggestionResolver.本帧技能 == 技能Id && 技能Id != 0)
                return 来源类型.AI;
        }
        catch { }

        return 来源类型.ACR;
    }

    // ==================== 结果回填 ====================

    /// <summary>
    /// 给到期的决策补上"后来怎么样了"。
    ///
    /// **这是整个记忆库最有价值的部分** —— 没有结果，
    /// 检索出来的只是"当时做了什么"，无法判断"该不该这么做"。
    /// </summary>
    private static void 回填()
    {
        if (_待回填.Count == 0) return;

        try
        {
            var 现在 = TimeHelper.Now();
            var 当前血量 = 采集当前血量();

            for (var i = _待回填.Count - 1; i >= 0; i--)
            {
                var 记录 = _待回填[i];

                // 用"时间"字段反推不方便，这里用列表顺序 + 一个简单的时长近似：
                // 由于采样间隔是 1 秒，_已采集 的索引差 ≈ 秒数
                // 更稳的做法是给记录存一个 TimeHelper.Now()，下面补上
                var 已过毫秒 = 现在 - 记录.出生时间;

                if (!记录.已回填3秒 && 已过毫秒 >= 3000)
                {
                    记录.结果3秒 = 当前血量;
                    记录.已回填3秒 = true;
                }

                if (!记录.已回填6秒 && 已过毫秒 >= 6000)
                {
                    记录.结果6秒 = 当前血量;
                    记录.已回填6秒 = true;

                    // ★ 填上"当场死亡"（原来这个字段从来没被赋值过）★
                    //
                    //   覆盖窗口 = 从决策到 6 秒回填这一整段 ——
                    //   那正是"这个决策有没有救到人"的观察期。
                    //
                    //   ⚠️ 为什么用 `死亡追踪.最近有人死亡()` 而不是逐个查血量：
                    //      那个人可能**已经被复活**、也可能**已经脱离**
                    //      `CastableAlliesWithin30` —— 逐人查会漏。
                    //      `死亡追踪` 用的是 `PartyHelper.DeadAllies`，更可靠。
                    记录.当场死亡 = 死亡追踪.最近有人死亡((int)已过毫秒 + 500);

                    // 两个时间点都填完了 → 从待回填队列移除
                    _待回填.RemoveAt(i);
                }
            }
        }
        catch { }
    }

    private static float[] 采集当前血量()
    {
        try
        {
            var 血量 = new List<float>();
            if (PartyHelper.CastableAlliesWithin30 != null)
            {
                foreach (var 人 in PartyHelper.CastableAlliesWithin30)
                {
                    if (人 == null) continue;
                    血量.Add(人.CurrentHpPercent());
                }
            }
            血量.Sort();
            return 血量.ToArray();
        }
        catch
        {
            return Array.Empty<float>();
        }
    }

    // ==================== 落盘 ====================

    /// <summary>
    /// 把采集到的数据写盘。
    ///
    /// ⚠️ **必须异步**（官方错题集第 5 条：Update/UI 里做同步 IO 会卡顿）。
    ///
    ///   `File.AppendAllText` 是同步阻塞的 —— 数据多的时候能卡住几百毫秒，
    ///   在战斗里就是明显的掉帧甚至技能延迟。
    ///
    ///   做法：先把数据搬到局部变量、清空列表（主线程只做内存操作），
    ///   然后丢到线程池去写文件。
    /// </summary>
    private static void 落盘()
    {
        if (_已采集.Count == 0) return;

        try
        {
            // ── 主线程只做这些：取数据 + 清列表 ──
            var 数据 = _已采集.ToList();
            _已采集.Clear();
            _待回填.Clear();

            // ⚠️ 统一走 AiSettings.记忆根目录() —— 别在这里自己拼路径
            //    （记录 / 记忆库 / 战斗记忆 三处必须读同一个来源，
            //      否则用户改了保存位置会出现"半生效"）
            var 目录 = AiSettings.记忆根目录();

            // ── 写文件丢到后台 ──
            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(目录);

                    var 文件 = Path.Combine(目录, $"战斗记忆_{DateTime.Now:yyyyMMdd}.jsonl");

                    var sb = new StringBuilder();
                    foreach (var r in 数据)
                    {
                        sb.AppendLine(JsonSerializer.Serialize(r, JsonOpts));
                    }

                    File.AppendAllText(文件, sb.ToString(), Encoding.UTF8);

                    累计条数 += 数据.Count;
                    最后文件 = 文件;

                    LogHelper.Info($"[BlueWhale.记忆] 落盘 {数据.Count} 条 -> {文件}（累计 {累计条数}）");
                }
                catch (Exception e)
                {
                    LogHelper.Error($"[BlueWhale.记忆] 落盘失败：{e.Message}");
                }
            });
        }
        catch (Exception e)
        {
            LogHelper.Error($"[BlueWhale.记忆] 落盘准备失败：{e.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,   // JSONL 每行一条，不缩进
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        IncludeFields = true,    // ★ 别忘了这个 —— 字段不序列化的坑踩过一次了
    };

    /// <summary>手动清空（调试用）</summary>
    public static void 清空()
    {
        _已采集.Clear();
        _待回填.Clear();
        _当前快照 = null;
    }
}
