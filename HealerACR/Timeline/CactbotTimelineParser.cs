using System.Globalization;
using System.Text.RegularExpressions;

namespace HealerACR.Timeline;

/// <summary>
/// cactbot 时间轴里的一条。
/// 格式：<c>&lt;时间&gt; "&lt;名字&gt;" &lt;条件类型&gt; { 参数 } [window a,b] [duration n] [jump "label"]</c>
/// </summary>
public class 时间轴条目
{
    /// <summary>相对战斗开始的秒数（cactbot 的 0.0 = InCombat）</summary>
    public double 时间;

    /// <summary>人类可读的名字，比如 "Disaster Zone"（同步行是 "--sync--"）</summary>
    public string 名称 = string.Empty;

    /// <summary>Ability / StartsUsing / InCombat / label / …</summary>
    public string 类型 = string.Empty;

    /// <summary>技能 ID（cactbot 里是十六进制，这里已转成游戏用的 uint）</summary>
    public List<uint> 技能Id = new();

    /// <summary>施法者名字</summary>
    public string 来源 = string.Empty;

    /// <summary>window a,b 里的 a</summary>
    public double? 窗口起;

    /// <summary>duration n</summary>
    public double? 持续;

    /// <summary>jump / forcejump 的目标（label 名或绝对时间）</summary>
    public string? 跳转;

    /// <summary>这一行是不是 label 定义</summary>
    public bool 是标签;

    /// <summary>运行时预处理填的：这条值不值得交减伤</summary>
    public bool 需减伤;

    public bool 是技能行 => !是标签 && 技能Id.Count > 0
                            && (类型 == "Ability" || 类型 == "StartsUsing");

    public override string ToString() => $"{时间,8:F1}s  {类型,-12} {名称}";
}

/// <summary>一份解析好的时间轴</summary>
public class 时间轴数据
{
    /// <summary>按文件时间升序的条目</summary>
    public List<时间轴条目> 条目 = new();

    /// <summary>label 名 → 文件时间</summary>
    public Dictionary<string, double> 标签 = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>有没有用到 jump（用来提示"这个本有循环/分支"）</summary>
    public bool 有跳转;

    public string 文件名 = string.Empty;
}

/// <summary>
/// cactbot 时间轴（.txt）解析器。
///
/// 支持：
///   ✔ 带时间的普通行（Ability / StartsUsing / …）
///   ✔ { id: "8D3F" } 和 { id: ["8D3D","8D3F"], source: "..." }
///   ✔ window / duration / jump / forcejump / label / hideall / 注释
///   ✔ 多阶段（jump + label）—— 解析出来交给 TimelineRunner 跑
/// </summary>
public static class CactbotTimelineParser
{
    private static readonly Regex 地图Id正则 = new(
        @"^\s*#\s*ZoneId\s*:\s*(\d+)", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex 标签正则 = new(
        @"^(?<t>[0-9]+(?:\.[0-9]+)?)\s+label\s+""(?<name>[^""]*)""", RegexOptions.Compiled);

    private static readonly Regex 主行正则 = new(
        @"^(?<t>[0-9]+(?:\.[0-9]+)?)\s+""(?<name>[^""]*)""\s+(?<type>[A-Za-z]+)\s*(?:\{(?<args>[^}]*)\})?\s*(?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex Id正则 = new(
        @"id\s*:\s*(?:""(?<one>[0-9A-Fa-f]+)""|\[(?<many>[^\]]*)\])", RegexOptions.Compiled);

    private static readonly Regex 来源正则 = new(
        @"source\s*:\s*""(?<s>[^""]*)""", RegexOptions.Compiled);

    private static readonly Regex 窗口正则 = new(
        @"window\s+(?<a>[0-9]+(?:\.[0-9]+)?)\s*,\s*(?<b>[0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);

    private static readonly Regex 持续正则 = new(
        @"duration\s+(?<d>[0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);

    private static readonly Regex 跳转正则 = new(
        @"(?:force)?jump\s+""?(?<j>[^""\s]+)""?", RegexOptions.Compiled);

    /// <summary>读出文件头声明的地图 ID（`# ZoneId: N`）</summary>
    public static uint? 解析地图Id(string 文本)
    {
        var m = 地图Id正则.Match(文本);
        if (!m.Success) return null;
        return uint.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>把整份时间轴文本解析成数据结构</summary>
    public static 时间轴数据 解析(string 文本, string 文件名 = "")
    {
        var 结果 = new 时间轴数据 { 文件名 = 文件名 };

        foreach (var 原行 in 文本.Split('\n'))
        {
            var 行 = 原行.Trim().TrimEnd('\r');
            if (行.Length == 0) continue;
            if (行.StartsWith("#", StringComparison.Ordinal)) continue;
            if (行.StartsWith("hideall", StringComparison.OrdinalIgnoreCase)) continue;
            if (行.StartsWith("window", StringComparison.OrdinalIgnoreCase)) continue;

            var 标签匹配 = 标签正则.Match(行);
            if (标签匹配.Success)
            {
                var 时间 = 转数字(标签匹配.Groups["t"].Value);
                var 名 = 标签匹配.Groups["name"].Value;
                结果.标签[名] = 时间;
                结果.条目.Add(new 时间轴条目
                {
                    时间 = 时间,
                    名称 = 名,
                    类型 = "label",
                    是标签 = true,
                });
                continue;
            }

            var m = 主行正则.Match(行);
            if (!m.Success) continue;

            var 条目 = new 时间轴条目
            {
                时间 = 转数字(m.Groups["t"].Value),
                名称 = m.Groups["name"].Value,
                类型 = m.Groups["type"].Value,
            };

            var 参数 = m.Groups["args"].Success ? m.Groups["args"].Value : string.Empty;
            if (参数.Length > 0)
            {
                var id匹配 = Id正则.Match(参数);
                if (id匹配.Success)
                {
                    if (id匹配.Groups["one"].Success)
                    {
                        var id = 转技能Id(id匹配.Groups["one"].Value);
                        if (id != 0) 条目.技能Id.Add(id);
                    }
                    else if (id匹配.Groups["many"].Success)
                    {
                        foreach (var piece in id匹配.Groups["many"].Value.Split(','))
                        {
                            var id = 转技能Id(piece.Trim().Trim('"'));
                            if (id != 0) 条目.技能Id.Add(id);
                        }
                    }
                }

                var 源匹配 = 来源正则.Match(参数);
                if (源匹配.Success) 条目.来源 = 源匹配.Groups["s"].Value;
            }

            var 尾巴 = m.Groups["rest"].Success ? m.Groups["rest"].Value : string.Empty;
            if (尾巴.Length > 0)
            {
                var w = 窗口正则.Match(尾巴);
                if (w.Success) 条目.窗口起 = 转数字(w.Groups["a"].Value);

                var d = 持续正则.Match(尾巴);
                if (d.Success) 条目.持续 = 转数字(d.Groups["d"].Value);

                var j = 跳转正则.Match(尾巴);
                if (j.Success)
                {
                    条目.跳转 = j.Groups["j"].Value;
                    结果.有跳转 = true;
                }
            }

            结果.条目.Add(条目);
        }

        结果.条目.Sort((a, b) => a.时间.CompareTo(b.时间));
        return 结果;
    }

    private static double 转数字(string s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>cactbot 里的技能 ID 是十六进制字符串（"8D3F"）</summary>
    private static uint 转技能Id(string s)
    {
        s = s.Trim().Trim('"');
        if (s.Length == 0) return 0;
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
