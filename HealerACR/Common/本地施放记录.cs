using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// 本地施放记录 —— 对付**服务器状态滞后**。
///
/// ══════════════════════════════════════════════════════════════════
///  ⚠️ 官方文档（ACR开发 L129-133）明确指出的坑：
///
///    "所有依赖于服务器的状态都是滞后的。
///     A 技能使用后给自己添加 buff，在内存里是 A 使用成功的
///     0.x 秒后才会获得这个 buff，如果恰好网络卡，这个时间会更长。"
///
///  ── 这会引发什么 ──
///
///    · **刚放完盾** → buff 还没下发 → 代码判断"目标没盾" → **又放一次**
///    · **刚补完 DoT** → buff 没到 → 判断"该补" → **又补一次**
///    · **刚用掉能力技** → 资源还没扣 → 判断"还有资源" → **重复用**
///
///    表现出来就是"一个技能连着放两次"、"DoT 反复补"。
///
///  ── 怎么解决 ──
///
///    在**本地**记一笔"我刚放过这个技能"，
///    判断的时候先问本地记录，不问服务器状态。
///
///    本地记录是**立即生效**的（不依赖服务器），
///    所以能盖住那段"内存还没更新"的空窗期。
///
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 本地施放记录
{
    /// <summary>
    /// 默认的"视为刚放过"时长（毫秒）。
    ///
    /// 取 1200ms 的理由：
    ///   · 服务器延迟通常 50~200ms，网络差时到 500ms
    ///   · 加上动画锁（0.6~0.7 秒）
    ///   · 1200ms 能覆盖绝大多数情况，又不会长到影响正常节奏
    /// </summary>
    private const int 默认时长 = 1200;

    /// <summary>技能 ID → 最后一次施放时间</summary>
    private static readonly Dictionary<uint, long> _最近施放 = new();

    /// <summary>记录一次施放（在 AfterSpell / 实际放出去时调用）</summary>
    public static void 记(uint 技能Id)
    {
        if (技能Id == 0) return;

        try { _最近施放[技能Id] = TimeHelper.Now(); }
        catch { }
    }

    /// <summary>
    /// 这个技能是不是"我刚放过"。
    ///
    /// **判断顺序应该是：先问这个，再看服务器 buff** ——
    /// 因为服务器状态可能还没更新。
    /// </summary>
    public static bool 刚放过(uint 技能Id, int 毫秒 = 默认时长)
    {
        if (技能Id == 0) return false;

        try
        {
            if (!_最近施放.TryGetValue(技能Id, out var 时间)) return false;
            return TimeHelper.Now() - 时间 < 毫秒;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>距离上次放这个技能过了多久（毫秒）；没放过返回 -1</summary>
    public static long 距上次(uint 技能Id)
    {
        try
        {
            if (!_最近施放.TryGetValue(技能Id, out var 时间)) return -1;
            return TimeHelper.Now() - 时间;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// 综合判断：**服务器说没有 buff，但我可能刚放过**。
    ///
    /// 用法：
    /// <code>
    /// // 不要这样写：
    /// if (目标.有盾) return -3;          // ← 服务器滞后时会漏判
    ///
    /// // 要这样写：
    /// if (本地施放记录.刚放过(盾技能) || 目标.有盾) return -3;
    /// </code>
    /// </summary>
    public static bool 视为已生效(uint 技能Id, Func<bool> 服务器状态, int 毫秒 = 默认时长)
    {
        // 本地优先 —— 它不滞后
        if (刚放过(技能Id, 毫秒)) return true;

        try { return 服务器状态(); }
        catch { return false; }
    }

    /// <summary>清理过期记录（每帧调，很轻）</summary>
    public static void 每帧更新()
    {
        try
        {
            if (_最近施放.Count == 0 && _按目标.Count == 0) return;

            var 现在 = TimeHelper.Now();
            var 要删 = new List<uint>();
            var 要删目标 = new List<(uint, uint)>();

            foreach (var kv in _最近施放)
            {
                // 超过 10 秒的记录没用了
                if (现在 - kv.Value > 10000) 要删.Add(kv.Key);
            }

            foreach (var kv in _按目标)
            {
                if (现在 - kv.Value > 10000) 要删目标.Add(kv.Key);
            }

            foreach (var id in 要删) _最近施放.Remove(id);
            foreach (var k in 要删目标) _按目标.Remove(k);
        }
        catch { }
    }

    /// <summary>战斗重置</summary>
    public static void 重置()
    {
        _最近施放.Clear();
        _按目标.Clear();
    }

    // ══════════════════════════════════════════════════════════════════
    //  ★ **按目标去重** ★
    //
    //  [!] 为什么需要（表 #63）：神名 / 天赐祝福 / 神祝祷 这类**单体能力技**
    //      原来只有"技能级"的记录 —— 而这类技能的 CD 与"打谁"无关：
    //      换个人放是**正确**的（两个人都掉血就该各给一发），
    //      对**同一个人**连放两发才是浪费（第二发几乎全过量）。
    //
    //  [!] 所以去重键必须是 **(技能, 目标)** 而不是只按技能 ——
    //      只按技能会把"另一个人的那一发"也一起挡掉（那是真 bug）。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>(技能Id, 目标Id) → 最后一次施放时间</summary>
    private static readonly Dictionary<(uint 技能, uint 目标), long> _按目标 = new();

    /// <summary>默认的"同一目标不重复"时长</summary>
    public const int 同目标默认时长 = 1500;

    /// <summary>记录一次"对某目标"的施放</summary>
    public static void 记目标(uint 技能Id, uint 目标Id)
    {
        if (技能Id == 0 || 目标Id == 0) return;

        try { _按目标[(技能Id, 目标Id)] = TimeHelper.Now(); }
        catch { }
    }

    /// <summary>这个技能**刚刚**对这个人放过吗</summary>
    public static bool 刚放过目标(uint 技能Id, uint 目标Id, int 毫秒 = 同目标默认时长)
    {
        if (技能Id == 0 || 目标Id == 0) return false;

        try
        {
            if (!_按目标.TryGetValue((技能Id, 目标Id), out var 时间)) return false;
            return TimeHelper.Now() - 时间 < 毫秒;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>诊断</summary>
    public static string 状态描述()
    {
        try
        {
            if (_最近施放.Count == 0) return "无记录";

            var 部分 = new List<string>();
            foreach (var kv in _最近施放)
            {
                部分.Add($"{kv.Key}:{(TimeHelper.Now() - kv.Value)}ms");
            }
            return string.Join(" ", 部分);
        }
        catch
        {
            return "读取失败";
        }
    }
}
