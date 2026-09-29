using AEAssist;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Common;

/// <summary>
/// 技能熔断器 —— 解决"技能拿不到却一直被按"。
///
/// 场景：
///   没做职业任务时，技能其实**根本拿不到**，但 <c>IsUnlock()</c> 仍可能返回 true。
///   于是 ACR 每帧都选中它、每帧都被游戏拒绝，看起来就是"技能没解锁还一直按"。
///
/// 判断办法：
///   用 <c>RecentlyUsed(id, 窗口)</c> 反推 —— AEAssist 只会在技能**真的放出去**之后
///   才记录使用历史。所以如果某个技能被反复选中、却在很长一段时间里从没出现在使用历史里，
///   那它多半是"拿不到"的技能。
///
/// 注意：长 CD 技能（比如 120 秒）本来就很久不用一次，所以窗口开得比较宽（默认 20 秒），
///   而且**只告警 + 拉黑，不永久禁用**（拉黑会在战斗重置时清掉）。
/// </summary>
public static class 技能熔断
{
    /// <summary>从第一次被选中起，多久还没成功放出去就认为拿不到</summary>
    private const long 熔断窗口毫秒 = 20000;

    private static readonly Dictionary<uint, long> 首次选中 = new();
    private static readonly HashSet<uint> 已拉黑 = new();
    private static readonly HashSet<uint> 已告警 = new();

    /// <summary>这个技能是不是被熔断了（拿不到）</summary>
    public static bool 已熔断(uint id) => 已拉黑.Contains(id);

    /// <summary>
    /// 报告一次"选中了这个技能"。返回 true 表示可以继续尝试，false 表示该跳过。
    /// </summary>
    public static bool 可以尝试(uint id)
    {
        if (id == 0) return false;
        if (已拉黑.Contains(id)) return false;

        var 现在 = TimeHelper.Now();

        // 用过 → 说明拿得到，清掉记录
        try
        {
            if (SpellExtension.RecentlyUsed(id, (int)熔断窗口毫秒))
            {
                首次选中.Remove(id);
                return true;
            }
        }
        catch
        {
            return true;   // 判断不了就别拦
        }

        if (!首次选中.TryGetValue(id, out var 起))
        {
            首次选中[id] = 现在;
            return true;
        }

        if (现在 - 起 < 熔断窗口毫秒) return true;

        // 超时还没放出去 → 拉黑
        已拉黑.Add(id);

        if (已告警.Add(id))
        {
            LogHelper.Error(
                $"[HealerACR] 技能 {id} 在 {熔断窗口毫秒 / 1000} 秒内一次都没放出去，已暂时停用。" +
                "常见原因：没做职业任务 / 等级不够 / ID 不对。");
        }

        return false;
    }

    /// <summary>战斗重置时清空（跨战斗重新给机会）</summary>
    public static void 重置()
    {
        首次选中.Clear();
        已拉黑.Clear();
        已告警.Clear();
    }

    /// <summary>诊断用：现在有几个技能被熔断</summary>
    public static int 熔断数 => 已拉黑.Count;
}
