using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// 死亡追踪 —— 参考同类 ACR 的 `IsTargetDeadLongEnough` / `UpdateDeathStatus`。
///
/// ══════════════════════════════════════════════════════════════════
///  它解决的问题：**刚躺下的人不该马上拉。**
///
///  为什么：
///    · 队友刚死时，可能马上会有战复（召唤/学者的即刻复活、
///      或者副本机制自带的复活）
///    · 也可能下一秒就被 AOE 打死（读条 8 秒 = 白读）
///    · 所以**等 2~3 秒再拉**，能省下即刻和 GCD
///
///  代价：**脱战场景下这 2 秒是纯浪费** —— 因为没人会战复，
///  所以你如果站在安全的地方，应该立刻拉。
///
///  折中方案：**战斗中要求躺够 2.5 秒，脱战立刻拉**。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 死亡追踪
{
    /// <summary>战斗中要躺够多久才拉（秒）</summary>
    private const float 战斗中等待秒 = 2.5f;

    /// <summary>记录多久没出现就清掉（防止字典无限增长）</summary>
    private const int 记录过期秒 = 60;

    /// <summary>尸体 ID → 第一次被发现躺着的时间</summary>
    private static readonly Dictionary<ulong, long> _死亡时间 = new();

    /// <summary>
    /// 每帧调用：更新"谁躺下了、躺了多久"。
    /// </summary>
    /// <summary>最近一次有人倒下的时刻（毫秒）；没有过则为 0</summary>
    private static long _最近死亡时刻;

    /// <summary>
    /// **这段时间内有人倒下吗**。
    ///
    /// 用途：战斗记忆回填时判断
    /// "决策之后队伍里有人死了吗" ——
    /// 那是评估决策好坏的重要信号。
    ///
    /// ⚠️ 为什么用"最近一次死亡时刻"而不是逐人查：
    ///    回填时只知道"当初的队伍快照"，
    ///    而那个人可能已经被复活、也可能已经脱离了
    ///    `CastableAlliesWithin30` —— 逐人查会漏。
    ///    "最近有人死"这个信号对那些情况都成立。
    /// </summary>
    public static bool 最近有人死亡(int 毫秒)
    {
        try
        {
            if (_最近死亡时刻 == 0) return false;
            return TimeHelper.Now() - _最近死亡时刻 <= 毫秒;
        }
        catch { return false; }
    }

    public static void 每帧更新()
    {
        try
        {
            var 现在 = TimeHelper.Now();
            var 躺着的 = new HashSet<ulong>();

            foreach (var 队友 in PartyHelper.DeadAllies)
            {
                if (队友 == null) continue;

                var id = 队友.GameObjectId;
                躺着的.Add(id);

                // 第一次发现它躺着 → 记时间
                if (!_死亡时间.ContainsKey(id))
                {
                    _死亡时间[id] = 现在;

                    // ★ 同时记下"最近一次有人倒下"的时刻 ★
                    //
                    //  用途：战斗记忆回填时判断
                    //  "决策之后队伍里有人死了吗"。
                    //  那个判断原本写了字段但**从来没赋值**（CS0649）。
                    _最近死亡时刻 = 现在;
                }
            }

            // 清掉已经起来的 / 过期的
            var 要删 = new List<ulong>();
            foreach (var kv in _死亡时间)
            {
                if (!躺着的.Contains(kv.Key))
                {
                    要删.Add(kv.Key);                    // 已经起来了
                }
                else if (现在 - kv.Value > 记录过期秒 * 1000)
                {
                    要删.Add(kv.Key);                    // 躺太久了，记录没意义
                }
            }

            foreach (var id in 要删) _死亡时间.Remove(id);
        }
        catch { }
    }

    /// <summary>这个人躺了多久（秒）；不知道就返回 0</summary>
    public static float 躺了多久(IBattleChara? c)
    {
        if (c == null) return 0;

        try
        {
            if (!_死亡时间.TryGetValue(c.GameObjectId, out var 起始)) return 0;
            return (TimeHelper.Now() - 起始) / 1000f;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 这个人是否"躺够久了，值得拉"。
    ///
    /// **脱战立刻返回 true** —— 脱战没人会战复，也不会有 AOE 打断读条，
    /// 等着纯属浪费。
    /// </summary>
    public static bool 躺够久了(IBattleChara? c)
    {
        if (c == null) return false;

        try
        {
            // 脱战：立刻拉
            if (!Core.Me.InCombat()) return true;

            // 战斗中：等一会儿
            var 躺了 = 躺了多久(c);

            // 数据不足（刚进本 / 刚重置）→ 放行，宁可拉错也别不拉
            if (躺了 <= 0) return true;

            return 躺了 >= 战斗中等待秒;
        }
        catch
        {
            return true;   // 判断不了就放行
        }
    }

    /// <summary>战斗重置时清空</summary>
    public static void 重置()
    {
        _死亡时间.Clear();
        _最近死亡时刻 = 0;
    }

    /// <summary>诊断信息</summary>
    public static string 状态描述()
    {
        try
        {
            if (_死亡时间.Count == 0) return "无人在躺";

            var 部分 = new List<string>();
            foreach (var kv in _死亡时间)
            {
                部分.Add($"{(TimeHelper.Now() - kv.Value) / 1000f:F1}s");
            }
            return "躺着: " + string.Join(" / ", 部分);
        }
        catch
        {
            return "读取失败";
        }
    }
}
