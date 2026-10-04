using AEAssist;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// **贤者蛇胆的花费节流**（表 #118 / #119）。
///
/// ══════════════════════════════════════════════════════════════════
///  [!] 要解决的问题：蛇胆上限 3 颗，而**一次血崩会把三颗连着倒出去** ——
///      几帧之内 输血 → 白牛清汁 → 灵橡清汁 → 坚角清汁 全交掉，
///      其中两发常常打在**同一个目标**身上（第二发几乎全过量）。
///
///  [!] 参考实现怎么做的（IL 直读 `自动单奶.SelectAction`）：
///      · 消费顺序固定：**输血 → 白牛清汁 → 灵橡清汁**
///      · 而且带一个「**最近用过就不重复**」的时间窗（**2000ms**）
///      也就是"两次蛇胆消费之间至少隔 2 秒"。
///
///  [!] 为什么是 2000ms（不是更短/更长）：
///      蛇胆 20 秒自然回 1 颗，一次血崩撑死也就花 2 颗；
///      2 秒的间隔足够让**第一发的治疗落地**（服务器 + 动画锁约 1.2 秒），
///      于是第二发的目标血量已经是更新过的 —— 不会打在"刚被奶满的人"身上。
///
///  [!] 本类**只做节流**，不决定用哪一个（那是各 resolver 的活）——
///      和 `OffGcd闸门` 同一个分工（闸门不做选择）。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 蛇胆节流
{
    /// <summary>两次蛇胆消费之间的最小间隔（参考口径 2000ms）</summary>
    public const int 互斥毫秒 = 2000;

    private static long _上次消费;

    /// <summary>现在允许再花一颗蛇胆吗</summary>
    public static bool 可以花()
    {
        try
        {
            if (_上次消费 == 0) return true;
            return TimeHelper.Now() - _上次消费 >= 互斥毫秒;
        }
        catch
        {
            return true;   // 读失败 ⇒ 放行（宁可多花一颗，也别把治疗全哑掉）
        }
    }

    /// <summary>距离上次消费多久（毫秒）；没消费过返回 -1</summary>
    public static long 距上次()
    {
        try
        {
            if (_上次消费 == 0) return -1;
            return TimeHelper.Now() - _上次消费;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>记一次"刚花了蛇胆"（在**选定了要放**之后调，而不是每帧调）</summary>
    public static void 记一次消费()
    {
        try { _上次消费 = TimeHelper.Now(); }
        catch { }
    }

    /// <summary>战斗重置 / 换本时清（有状态就得清 —— 开发约定 F①）</summary>
    public static void 重置()
    {
        _上次消费 = 0;
    }

    /// <summary>诊断用</summary>
    public static string 状态描述()
    {
        var 距 = 距上次();
        return 距 < 0 ? "无记录" : $"{距}ms";
    }
}
