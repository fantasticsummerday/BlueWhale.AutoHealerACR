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
///
/// ⚠️ **标题一律用纯文本，不要加图标符号**：
///   FF14 的界面字体**不支持 emoji 和大部分装饰符号**
///   （✅ ⚠️ ❌ 🐋 ★ → 这类），会显示成**方块或问号**。
///   所以这里只用中文 + 全角括号，例如「小鲸鱼：」。
///   加新的上屏文案时请遵守这条 —— 代码注释里用什么都无所谓，
///   **只要会被玩家看到，就必须是纯文本**。
/// </summary>
public static class 屏幕提示
{
    /// <summary>同一个 key 的提示最短间隔（毫秒）</summary>
    private const int 节流毫秒 = 5000;

    private static readonly Dictionary<string, long> _上次提示 = new();

    /// <summary>
    /// 横幅署名 —— **纯文本**（FF14 字体不认图标）
    /// </summary>
    private const string 署名 = "小鲸鱼";

    /// <summary>普通提示（蓝色横幅）</summary>
    public static void 提示(string 标题, string 内容, string key = "")
    {
        发(标题, 内容, key);
    }

    /// <summary>成功类提示</summary>
    public static void 成功(string 内容, string key = "")
    {
        发($"{署名}（完成）", 内容, key);
    }

    /// <summary>警告类提示</summary>
    public static void 警告(string 内容, string key = "")
    {
        发($"{署名}（注意）", 内容, key);
    }

    /// <summary>错误类提示</summary>
    public static void 错误(string 内容, string key = "")
    {
        try
        {
            if (!可以发(key)) return;
            LogHelper.PrintError($"{署名}（错误）", 内容);
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

    // ══════════════════════════════════════════════════════════════
    //  ★ 彩蛋：小鲸鱼偶尔会哼一段 ★
    // ══════════════════════════════════════════════════════════════

    /// <summary>触发概率（0~1）。**必须低** —— 它是彩蛋，不是功能</summary>
    private const double 唱歌概率 = 0.035;

    /// <summary>
    /// **唱歌** —— 判定成功时返回一段"歌词"，否则返回 null。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么是"追加"而不是"替换" ★
    ///
    ///    只在**本来就有话要说**的时候才哼 ——
    ///    初始化成功 / 解除熔断本来就会打一条横幅。
    ///
    ///    如果单独打一条，两个问题：
    ///      ① 第二条会**把第一条顶掉**（同一位置，用户来不及看）
    ///      ② 万一在别的地方触发，就变成"莫名其妙冒出一句歌词"，
    ///         而真正该看的提示被挤掉了
    ///
    ///    ⇒ 追加在后面：功能信息永远完整，彩蛋只是尾巴。
    ///
    ///  ⚠️ **纯英文，故意不翻译** ——
    ///     翻译过来就不好笑了（原文的荒诞感来自那种一本正经的腔调）。
    ///     而且这几句全是大写字母和常见标点，FF14 字体一定认。
    ///
    ///  ⚠️ **单行 + " / " 分隔**（见 <see cref="歌词分隔符"/> 的说明）：
    ///     聊天框单条消息**不保证渲染换行**，
    ///     真发多行的话可能被压成一行、或者只显示第一行。
    ///     用分隔符就与换行支持与否无关。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static string? 唱歌()
    {
        try
        {
            if (Random.Shared.NextDouble() >= 唱歌概率) return null;

            var 行 = 歌词;
            return string.Join(歌词分隔符, 行);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 歌词分隔符。
    ///
    /// ⚠️ 为什么不用 `\n`：聊天框单条消息**不保证支持换行** ——
    ///     `LogHelper.Print` 只是把整串交出去，
    ///     换行是渲染成真换行、还是被压成空格、还是直接截断，**无法确认**。
    ///     " / " 是纯文本，任何渲染方式下都读得通。
    ///
    ///     要是以后确认了支持换行，把它改成 "\n" 就行（只改这一处）。
    /// </summary>
    private const string 歌词分隔符 = " / ";

    /// <summary>
    /// 彩蛋台词 —— **逐字照抄，不要改**。
    ///
    /// 改了就不好笑了：这几句的味道全在"短句 + 句号 + 英文"那种
    /// 一本正经的腔调上。
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
