using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// 屏幕提示 —— 走 AEAssist 的 <see cref="LogHelper.Print(string, string)"/>。
///
/// 就是游戏画面上那条蓝色横幅（"未保存过自定义QT..."那种）。
///
/// **为什么不用 LogHelper.Info**：
///   Info 只写日志文件，游戏里看不到。
///   有些事必须"当场告诉用户" —— 比如 AI 初始化完成、熔断触发 ——
///   让用户去翻日志是不合理的。
///
/// **为什么加节流**：
///   这些东西可能在每帧被触发（比如熔断状态），
///   不节流的话屏幕会被刷满。
/// </summary>
public static class 屏幕提示
{
    /// <summary>同一个 key 的提示最短间隔（毫秒）</summary>
    private const int 节流毫秒 = 5000;

    private static readonly Dictionary<string, long> _上次提示 = new();

    /// <summary>普通提示（蓝色横幅）</summary>
    public static void 提示(string 标题, string 内容, string key = "")
    {
        发(标题, 内容, key);
    }

    /// <summary>成功类提示</summary>
    public static void 成功(string 内容, string key = "")
    {
        发("✅ 小鲸鱼", 内容, key);
    }

    /// <summary>警告类提示</summary>
    public static void 警告(string 内容, string key = "")
    {
        发("⚠️ 小鲸鱼", 内容, key);
    }

    /// <summary>错误类提示</summary>
    public static void 错误(string 内容, string key = "")
    {
        try
        {
            if (!可以发(key)) return;
            LogHelper.PrintError("❌ 小鲸鱼", 内容);
            LogHelper.Error($"[提示] {内容}");
        }
        catch { }
    }

    private static void 发(string 标题, string 内容, string key)
    {
        try
        {
            if (!可以发(key)) return;

            LogHelper.Print(标题, 内容);
            LogHelper.Info($"[提示] {内容}");
        }
        catch { }
    }

    /// <summary>节流判断：同一个 key 短时间内只发一次</summary>
    private static bool 可以发(string key)
    {
        if (string.IsNullOrEmpty(key)) return true;

        try
        {
            var 现在 = TimeHelper.Now();

            if (_上次提示.TryGetValue(key, out var 上次) && 现在 - 上次 < 节流毫秒)
            {
                return false;
            }

            _上次提示[key] = 现在;
            return true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>清掉节流记录（换本 / 重新初始化时用）</summary>
    public static void 重置() => _上次提示.Clear();
}
