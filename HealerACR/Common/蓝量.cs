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
    public const int 停手阈值 = 1400;
      // ★ 2026-10-04：2000 → 1400
      //   对照实现**没有全局低蓝停手**，只有按技能分的门：
      //   闪灼/再生 400、医治/愈疗 700、医养 800、失衡/群盾/单盾/预后 1000、救疗 1400。
      //   原来 2000 ⇒ 四奶在 MP 1400~2000 之间**完全停输出**，比参考最严的门还早 ✗
      //   这里取参考的最高门 1400 作为全局停手线（保守侧）。

    /// <summary>现在该不该因为低蓝而停手</summary>
    public static bool 低蓝停手()
    {
        try
        {
            return CharacterExt.我的当前蓝量() <= 停手阈值;
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
            try { return CharacterExt.我的当前蓝量(); } catch { return 0; }
        }
    }
}
