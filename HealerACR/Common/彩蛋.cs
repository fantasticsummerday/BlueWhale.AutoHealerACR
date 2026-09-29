using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// **彩蛋：小鲸鱼偶尔会哼一段**（一句一句慢慢发）。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么用"每帧推进的队列"而不是协程 / Task.Delay ★
///
///    `async` + `Task.Delay` 在游戏里有两个坑：
///      ① **切职业 / 重载 ACR 会把 ACR 卸载**，
///         而 `Task` 还活着 → 回调进一个已经卸载的对象（甚至崩）
///      ② 没法在换本、停手时**取消** —— 回来时突然冒出半句话
///
///    队列方案：状态就是一个 `List` + 一个时间戳，
///    换本/重载时 `重置()` 一清就没了，**天然安全**。
///
///  ── 为什么每帧推进也要"等 1~2 秒" ──
///    六句话**挤在一起发**会连成一片、而且聊天框会滚掉；
///    分开念才像"在哼歌"。间隔**随机** 1~2 秒 ——
///    固定间隔听起来像机器播报，随机才有语气。
///
///  ⚠️ 概率**比单行版低**（0.02）：现在已经够显眼了，
///     而且六句话 vs 一句话，后者打扰小得多。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 彩蛋
{
    /// <summary>触发概率（0~1）</summary>
    private const double 概率 = 0.02;

    /// <summary>句子之间的间隔（毫秒），随机取这个区间</summary>
    private const int 最小间隔毫秒 = 1000;
    private const int 最大间隔毫秒 = 2000;

    /// <summary>
    /// 待唱的句子队列。空 = 没在唱。
    /// ⚠️ 只在主线程（每帧更新）里动它，不用加锁。
    /// </summary>
    private static readonly List<string> _待唱 = new();

    /// <summary>下一句该发的时刻</summary>
    private static long _下一句时间;

    /// <summary>正在唱吗</summary>
    public static bool 在唱 => _待唱.Count > 0;

    /// <summary>
    /// **判定要不要唱**，要唱就把歌词排进队列。
    ///
    /// <returns>true = 这次触发了彩蛋（调用方可以把横幅文案换个说法）</returns>
    ///
    /// ⚠️ 它**只排队，不发** —— 第一句由 <see cref="每帧更新"/> 按时间发，
    ///     这样第一句也会和后面几句保持同样的间隔节奏。
    /// </summary>
    public static bool 试试()
    {
        try
        {
            if (在唱) return false;                          // 上一段还没唱完
            if (Random.Shared.NextDouble() >= 概率) return false;

            _待唱.Clear();
            _待唱.AddRange(歌词);

            // 第一句稍等一下再发 —— 让原来的横幅（"AI 就绪"）先被看到
            _下一句时间 = TimeHelper.Now() + 间隔();

            LogHelper.Info("[彩蛋] 触发，共 " + _待唱.Count + " 句");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// **每帧推进**（挂在本地层的每帧里）。
    ///
    /// ⚠️ 必须由**本地层**驱动，不能放 AI 层：
    ///     记录模式下 `AiHeartbeat` 会被移出队列，
    ///     那样歌唱到一半就永远卡住了。
    /// </summary>
    public static void 每帧更新()
    {
        try
        {
            if (_待唱.Count == 0) return;

            var 现在 = TimeHelper.Now();
            if (现在 < _下一句时间) return;

            var 句 = _待唱[0];
            _待唱.RemoveAt(0);

            // ⚠️ 走"绕过节流"的出口 ——
            //    正常出口有 5 秒节流，会把第二句起全吃掉。
            屏幕提示.直接发(屏幕提示.彩蛋署名, 句);

            if (_待唱.Count > 0) _下一句时间 = 现在 + 间隔();
        }
        catch
        {
            // 出任何问题就**别继续唱了** —— 半句话卡在队列里比不唱更糟
            _待唱.Clear();
        }
    }

    /// <summary>随机间隔（1~2 秒）</summary>
    private static int 间隔()
    {
        try
        {
            return Random.Shared.Next(最小间隔毫秒, 最大间隔毫秒 + 1);
        }
        catch
        {
            return 最小间隔毫秒;
        }
    }

    /// <summary>
    /// **立刻停唱并清队列**（换本 / 切职业 / 重载时调）。
    ///
    /// ⚠️ 不清的话：换本后突然冒出上一段没唱完的句子。
    /// </summary>
    public static void 重置()
    {
        try
        {
            _待唱.Clear();
            _下一句时间 = 0;
        }
        catch { }
    }

    /// <summary>
    /// 彩蛋台词 —— **逐字照抄，不要改**。
    ///
    /// 改了就不好笑了：这几句的味道全在"短句 + 句号 + 英文"那种
    /// 一本正经的腔调上。翻译过来就没了。
    ///
    /// ⚠️ 全是 ASCII —— FF14 的界面字体对非 ASCII 符号支持很差，
    ///     这里不用担心中文/emoji 变方块。
    /// </summary>
    private static readonly string[] 歌词 =
    {
        "Okay.",
        "Let me go.",
        "I'm making the calls.",
        "Let me write the JSON.",
        "I'll call the tool.",
        "Let me do it.",
    };
}
