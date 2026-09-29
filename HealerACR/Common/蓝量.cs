using AEAssist;

namespace HealerACR.Common;

/// <summary>
/// 蓝量相关的判断。参考同类 ACR 的 <c>ScholarSpellHelper.IsLowMpStopActive()</c>。
///
/// 为什么需要：奶妈的输出 GCD 也耗蓝（每次约 400），蓝打空之后就治不了人了。
/// 同类 ACR 的做法是"低蓝停手" —— 蓝量低于阈值时停止一切输出，把蓝留给治疗。
/// </summary>
public static class 蓝量
{
    /// <summary>低蓝停手的阈值。低于它就停止输出。</summary>
    public const int 停手阈值 = 2000;

    /// <summary>现在该不该因为低蓝而停手</summary>
    public static bool 低蓝停手()
    {
        try
        {
            return Core.Me.CurrentMp <= 停手阈值;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前蓝量（给诊断用）</summary>
    public static uint 当前蓝量
    {
        get
        {
            try { return Core.Me.CurrentMp; } catch { return 0; }
        }
    }
}
