using AEAssist.Helper;
using HealerACR.Common;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// AI 调试输出 —— 把 AI 的日志直接打到游戏里。
///
/// ══════════════════════════════════════════════════════════════════
///  为什么需要它：
///
///    AI 层的关键信息（请求了什么、AI 回了什么、为什么采纳/丢弃）
///    平时只在日志文件里。要看就得开日志、翻文件、找时间点 ——
///    调提示词的时候这个流程太慢了。
///
///    开启调试模式后，这些信息直接走屏幕横幅（和"AI 初始化中"同一个通道），
///    在游戏里就能看到 AI 每一步在想什么。
///
///  ── 注意 ──
///
///    所有日志仍然会写进日志文件（Debug 只是**额外**打到屏幕上）。
///    屏幕横幅有节流，而且文本太长会被截断 ——
///    所以太长的内容（比如完整 JSON）只写文件、不上屏。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class Ai调试
{
    /// <summary>屏幕上最多显示多少字（太长会占满屏幕）</summary>
    private const int 屏幕最大长度 = 120;

    /// <summary>调试模式是否启用（跟设置联动）</summary>
    public static bool 启用
    {
        get
        {
            try { return AiSettings.Instance.调试模式; }
            catch { return false; }
        }
    }

    /// <summary>写一条 AI 日志（始终写文件；调试模式下额外上屏）</summary>
    public static void 日志(string 内容)
    {
        try
        {
            LogHelper.Info("[BlueWhale.AI] " + 内容);

            if (!启用) return;

            // 太长的不上屏 —— 会占满屏幕反而看不见别的
            var 显示 = 内容;
            if (显示.Length > 屏幕最大长度)
            {
                显示 = 显示.Substring(0, 屏幕最大长度) + "…（详见日志）";
            }

            // 横幅标题固定用"小鲸鱼"（【】里显示小鲸鱼，不要显示 AI）
            屏幕提示.提示("小鲸鱼", 显示);
        }
        catch { }
    }

    /// <summary>带标签的日志</summary>
    public static void 日志(string 标签, string 内容)
        => 日志($"{标签}｜{内容}");

    /// <summary>只在调试模式下输出的日志（高频内容用这个，避免刷日志文件）</summary>
    public static void 调试(string 内容)
    {
        if (!启用) return;
        日志(内容);
    }

    /// <summary>错误（永远上屏，不管调试模式）</summary>
    public static void 错误(string 内容)
    {
        try
        {
            LogHelper.Error("[BlueWhale.AI] " + 内容);

            var 显示 = 内容;
            if (显示.Length > 屏幕最大长度)
            {
                显示 = 显示.Substring(0, 屏幕最大长度) + "…（详见日志）";
            }

            屏幕提示.警告(显示, "ai-err-" + 内容.GetHashCode());
        }
        catch { }
    }
}
