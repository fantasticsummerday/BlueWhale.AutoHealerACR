using System.Text;
using System.Text.Json;
using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// 对局记录器 —— 记录模式下把**玩家手动**放的每个技能连同当时的局面记下来。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 它和「战斗记忆」的区别（两个都要，各管一件事）★
///
///    · `战斗记忆`：**长期**采集，判断"谁放的"（人类/ACR/AI），带 3s/6s 结果回填。
///      它是通用素材库，一直在跑。
///    · `对局记录`（本类）：**只在记录模式**跑，一次副本一份记录文件，
///      专门为"打完提炼成记忆库"服务。
///
///    分开的原因：提炼需要的是**一场完整对局**的边界（副本开始 → 副本结束），
///    而 战斗记忆 是按天追加的流水账，没有"一局"的概念。
///
///  ★ 一条记录里记什么（为什么记这些）★
///
///    核心是**时机**，所以要能回答："他在什么局面下放了这个技能"
///      · 队伍血量分布（排序后）→ 最低血多少 = 他是**反应式**还是**预判式**治疗
///      · 技能是 GCD 还是能力技 → 判断"占不占输出节奏"
///      · 距上场战斗开始多久 → 起手习惯
///      · 敌人数量 → 单体还是 AOE 环境
///      · 蓝量 → 资源管理习惯
///
///  ⚠️ 技能归属**天然干净**：记录模式下 ACR 完全停手（决策队列为空），
///     所以记录到的每一条都必然是玩家手动按的 —— 不需要任何启发式去猜。
///     这是"完全停手"换来的最大好处，别为了省事破坏它。
/// ══════════════════════════════════════════════════════════════════
public static class 对局记录
{
    // ==================== 数据结构 ====================

    private sealed class 一条
    {
        public string 时间 = "";
        public int 距开战秒;

        // ── 技能 ──
        public uint 技能;
        public string 技能名 = "";

        // ── 局面快照（放技能那一刻）──
        public float 蓝量;
        public float[] 队伍血量 = Array.Empty<float>();
        public float 最低血量;
        public int 受伤人数;        // < 70%
        public int 濒危人数;        // < 35%
        public float 自己血量;
        public int 敌人数量;
        public float 目标血量;      // 当前目标（可能没目标 → -1）
        public bool 战斗中;
    }

    // ==================== 状态 ====================

    private static readonly List<一条> _本局 = new();

    /// <summary>本局开始时刻（第一次记录时设）</summary>
    private static long _本局开始;

    /// <summary>本局的地图 / 副本类型 —— 提炼时按「职业+等级+副本类型」归档要用</summary>
    private static uint _地图;
    private static string _副本类型 = "";
    private static int _等级;

    /// <summary>当前是否有正在记录的对局</summary>
    public static bool 有记录 => _本局.Count > 0;

    /// <summary>本局已记录条数</summary>
    public static int 本局条数 => _本局.Count;

    /// <summary>
    /// **上一局**落盘时的条数。
    ///
    /// 落盘会把 `_本局` 清空，所以"刚才那局有多少条"必须单独存下来 ——
    /// 收尾逻辑要靠它决定"样本够不够提炼"。
    /// </summary>
    public static int 上次条数 { get; private set; }

    /// <summary>最后一次落盘的文件</summary>
    public static string 最后文件 { get; private set; } = "";

    // ==================== 采集 ====================

    /// <summary>挂到 记录模式.记录一次 上</summary>
    public static void 记一次(uint 技能, string 技能名)
    {
        if (!记录模式.开启) return;
        if (技能 == 0) return;

        try
        {
            var 现在 = TimeHelper.Now();

            // 本局第一条 → 记开始时刻，并抓本局的环境信息
            if (_本局.Count == 0)
            {
                _本局开始 = 现在;
                抓本局环境();
            }

            var 血量 = 采集队伍血量();

            _本局.Add(new 一条
            {
                时间 = DateTime.Now.ToString("HH:mm:ss.fff"),
                距开战秒 = (int)((现在 - _本局开始) / 1000),
                技能 = 技能,
                技能名 = 技能名,
                蓝量 = 采蓝量比例(),
                队伍血量 = 血量,
                最低血量 = 血量.Length > 0 ? 血量[0] : 1f,   // 采集时已排序，[0] 最低
                受伤人数 = 血量.Count(h => h < 0.7f),
                濒危人数 = 血量.Count(h => h < 0.35f),
                自己血量 = 自身血量(),
                敌人数量 = 周围敌人数(),
                目标血量 = 当前目标血量(),
                战斗中 = 在战斗中(),
            });

            // 安全阀：一局最多记这么多条，防止异常情况下无限增长
            if (_本局.Count > 3000)
            {
                LogHelper.Info("[对局记录] 单局条数超过 3000，自动收尾落盘");
                落盘("条数超限");
            }
        }
        catch
        {
            // 记录绝不能影响战斗
        }
    }

    /// <summary>抓本局的环境信息（地图 / 副本类型 / 等级）—— 归档维度</summary>
    private static void 抓本局环境()
    {
        try { _地图 = (uint)Data.CurrentContentFinderConditionId; } catch { _地图 = 0; }
        try { _等级 = Data.PlayerCurrentLevel; } catch { _等级 = 0; }

        // 副本类型：四人本 / 八人本 —— 用户选的归档维度之一
        try
        {
            _副本类型 = HealTargetHelper.是八人本() ? "八人" : "四人";
        }
        catch { _副本类型 = "未知"; }
    }

    // ==================== 采集辅助 ====================

    /// <summary>队伍血量百分比，**排序（升序）** —— [0] 是最低的，方便判断紧迫度</summary>
    private static float[] 采集队伍血量()
    {
        try
        {
            var 列表 = new List<float>();
            if (PartyHelper.CastableAlliesWithin30 != null)
            {
                foreach (var 人 in PartyHelper.CastableAlliesWithin30)
                {
                    // ★ 判 对象有效()：换图时成员被释放但仍非 null（哨兵 0x12345679），只判 null 会崩
                    if (人 == null || !人.对象有效()) continue;
                    列表.Add(人.CurrentHpPercent());
                }
            }
            列表.Sort();
            return 列表.ToArray();
        }
        catch
        {
            return Array.Empty<float>();
        }
    }

    private static float 采蓝量比例()
    {
        try
        {
            var me = Core.Me;
            return me.MaxMp > 0 ? me.CurrentMp * 1f / me.MaxMp : 0f;
        }
        catch { return 0f; }
    }

    private static float 自身血量()
    {
        // ★ 先判 null（`CurrentHpPercent()` 是原生 getter）
        try { return Core.Me == null ? 1f : Core.Me.CurrentHpPercent(); }
        catch { return 0f; }
    }

    private static int 周围敌人数()
    {
        try { return HealTargetHelper.周围敌人数量(); }
        catch { return 0; }
    }

    private static float 当前目标血量()
    {
        try
        {
            var t = HealTargetHelper.当前目标();
            return t == null ? -1f : t.CurrentHpPercent();
        }
        catch { return -1f; }
    }

    private static bool 在战斗中()
    {
        try { return CharacterExt.我在战斗(); }
        catch { return false; }
    }

    // ==================== 落盘 ====================

    /// <summary>
    /// 结束本局并落盘。返回落盘的文件路径（没有内容返回空串）。
    ///
    /// ⚠️ **必须异步写**（官方错题集第 5 条：主线程做同步 IO 会卡顿）。
    /// </summary>
    public static string 落盘(string 原因 = "")
    {
        if (_本局.Count == 0) return "";

        try
        {
            var 数据 = _本局.ToList();
            var 地图 = _地图;
            var 副本类型 = _副本类型;
            var 等级 = _等级;

            _本局.Clear();
            _本局开始 = 0;
            上次条数 = 数据.Count;   // 清空前先存下来（收尾逻辑要用）

            var 目录 = 记忆路径();
            var 职业 = 当前职业名();
            var 文件 = Path.Combine(目录,
                $"记录_{DateTime.Now:yyyyMMdd_HHmmss}_{职业}_{副本类型}.jsonl");

            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(目录);

                    var sb = new StringBuilder();

                    // 第一行写"对局元信息" —— 提炼时靠它归档（职业+等级+副本类型）
                    var 头 = JsonSerializer.Serialize(new
                    {
                        类型 = "对局头",
                        职业,
                        等级,
                        地图,
                        副本类型,
                        条数 = 数据.Count,
                        时间 = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        结束原因 = 原因,
                    }, JsonOpts);
                    sb.AppendLine(头);

                    foreach (var r in 数据)
                        sb.AppendLine(JsonSerializer.Serialize(r, JsonOpts));

                    File.WriteAllText(文件, sb.ToString(), Encoding.UTF8);
                    最后文件 = 文件;

                    LogHelper.Info(
                        $"[对局记录] 落盘 {数据.Count} 条 -> {文件}" +
                        (string.IsNullOrEmpty(原因) ? "" : $"（{原因}）"));
                }
                catch (Exception e)
                {
                    LogHelper.Error($"[对局记录] 落盘失败：{e.Message}");
                }
            });

            return 文件;
        }
        catch (Exception e)
        {
            LogHelper.Error($"[对局记录] 落盘准备失败：{e.Message}");
            return "";
        }
    }

    /// <summary>丢掉本局（不落盘）—— 用于"记录模式被关掉，这局不算"</summary>
    public static void 丢弃()
    {
        _本局.Clear();
        _本局开始 = 0;
    }

    /// <summary>当前职业名</summary>
    public static string 当前职业名()
    {
        // ★ 走带守卫的唯一入口（原来是裸读 Core.Me.ClassJob.Value.Name）
        try { return AiSituation.职业名(); }
        catch { return "未知"; }
    }

    /// <summary>当前副本类型（四人/八人）</summary>
    public static string 当前副本类型() => string.IsNullOrEmpty(_副本类型) ? "未知" : _副本类型;

    /// <summary>当前等级</summary>
    public static int 当前等级() => _等级 > 0 ? _等级 : 1;

    /// <summary>
    /// 记忆根目录 —— **统一走 AiSettings.记忆根目录()**。
    ///
    /// ⚠️ 不要在这里自己拼路径：记忆/记录/记忆库三个模块必须读同一个来源，
    ///    否则用户改了保存位置之后，会出现"记录写到新位置、
    ///    但记忆库还在读老位置"这种半生效的怪状态。
    /// </summary>
    public static string 记忆路径() => AiSettings.记忆根目录();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
