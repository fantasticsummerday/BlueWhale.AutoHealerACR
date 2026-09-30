using AEAssist;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// **空中检测** —— 判断玩家"跳起来了、还没落地"。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么需要它（用户反馈）★
///
///    "原地跳跃应该也是移动状态。"
///
///    核实到的原因（IL 直证）：框架的 `MoveHelper.IsMoving()` 是
///        MemApiMove.IsMoving:
///            call  AgentMap.Instance
///            ldfld AgentMap.IsPlayerMoving      <- **只看这一个游戏标志**
///            ret
///    而**跳跃不改变 `IsPlayerMoving`**（那个标志跟"移动输入"走）。
///
///    另查 Dalamud 的 `StatusFlags` 也没有跳跃位：
///        None / Hostile / InCombat / WeaponOut / OffhandOut /
///        PartyMember / AllianceMember / Friend / IsCasting
///    => 框架层拿不到"在空中"，只能自己从坐标判。
///
///  ★ 为什么这不只是"分类"问题 ★
///
///    跳跃中**无法开始读条**（会失败或立刻被打断）。
///    现状是：跳跃时 `在移动()` 返回 false ->
///    `移动状态描述()` 说"站定 —— 可以建议读条技能" ->
///    AI 建议读条技能 -> **放不出来**。
///    => 要修的不是"把它叫做移动"，而是**别在跳起来的时候建议读条**。
///
///  [!] 所以它**不进 `在移动()` 的语义**（那会污染给 AI 的措辞：
///      "移动中"会让人以为在走位），而是**单独并进"能不能读条"的判断**。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 空中检测
{
    /// <summary>上升多少算"跳起了"（米）—— 跳跃高度约 1 米，取保守值</summary>
    private const float 起跳阈值 = 0.30f;

    /// <summary>落回起点多少以内算"落地了"（米）</summary>
    private const float 落地容差 = 0.15f;

    /// <summary>落地后还要宽限多久（毫秒）—— 覆盖下落收尾</summary>
    private const long 落地宽限毫秒 = 250;

    private static float _起点Y = float.NaN;
    private static bool _在空中;
    private static long _落地时刻;

    /// <summary>**现在在空中吗**（跳起来了、还没落地）</summary>
    public static bool 在空中
    {
        get
        {
            try
            {
                // 落地后的宽限期 —— 一帧内可能还在下落收尾
                if (!_在空中 && _落地时刻 != 0 &&
                    TimeHelper.Now() - _落地时刻 < 落地宽限毫秒)
                    return true;
                return _在空中;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// **每帧采一次**（挂在 `HealerEntryBase` 的每帧回调里）。
    ///
    /// [!] 必须在**游戏线程**上跑 —— 它读 `Core.Me.Position`。
    /// ══════════════════════════════════════════════════════════════
    /// 判据（刻意保守，避免误判把读条技能全废掉）：
    ///   · **只认"上升过"** —— 地形抬升/下落不触发（那两种是持续单向变化）
    ///   · 落回**起点附近**才算落地（不是"到了某个绝对高度"）
    ///   · `NaN` 起点 = 首次采样，只记起点不判定
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    public static void 每帧更新()
    {
        try
        {
            var 我 = Core.Me;
            if (我 == null) return;

            var y = 我.Position.Y;

            if (float.IsNaN(_起点Y))
            {
                _起点Y = y;
                return;
            }

            if (!_在空中)
            {
                // 抬升超过阈值 -> 认为跳起来了
                if (y - _起点Y >= 起跳阈值)
                {
                    _在空中 = true;
                }
                else
                {
                    // 没跳：把起点跟着地面走（这样走上台阶不会攒出假阈值）
                    _起点Y = y;
                }
            }
            else
            {
                // 在空中：落回起点附近 -> 落地
                if (MathF.Abs(y - _起点Y) <= 落地容差)
                {
                    _在空中 = false;
                    _落地时刻 = TimeHelper.Now();
                    _起点Y = y;
                }
            }
        }
        catch { }
    }

    /// <summary>重置（换本 / 切职业时）</summary>
    public static void 重置()
    {
        _起点Y = float.NaN;
        _在空中 = false;
        _落地时刻 = 0;
    }

    /// <summary>给 AI / 日志的一句话（没在空中返回空串）</summary>
    public static string 描述()
        => 在空中 ? "空中（跳跃中，读条放不出来）" : "";
}
