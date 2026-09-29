using System.Text;
using HealerACR.Timeline;

// ══════════════════════════════════════════════════════════════════
//  TimelineProbe —— 用**真实时间轴文件**验证解析器和"机制预告"的取数逻辑
//
//  为什么需要它：
//    「机制预告」是给 AI 看的新功能，而 AI 拿到的是**文字**。
//    如果取数逻辑错了（比如把同步行也当成机制、
//    或者时间算成绝对值），AI 会收到一堆噪声，
//    表现就是"AI 开始胡言乱语"——而且很难从日志看出根因。
//    所以要在本地用真实文件把输出**打印出来看**。
//
//  它**不依赖 AEAssist**（解析器本身零依赖），所以能独立跑。
//  `TimelineRunner` 依赖 AEAssist，所以这里**复刻**它的那段取数逻辑，
//  目的只是确认"文件解析 + 过滤规则"这条路是通的、输出是可读的。
//
//  用法：
//      dotnet run --project tools/TimelineProbe -- <时间轴目录或文件> [时间点秒]
// ══════════════════════════════════════════════════════════════════

var 目标 = args.Length > 0 ? args[0] : @"cactbot_timelines";
var 时间点 = args.Length > 1 && double.TryParse(args[1], out var t) ? t : 10.0;

var 文件列表 = new List<string>();
if (File.Exists(目标))
{
    文件列表.Add(目标);
}
else if (Directory.Exists(目标))
{
    文件列表.AddRange(Directory.EnumerateFiles(目标, "*.txt", SearchOption.AllDirectories));
}
else
{
    Console.WriteLine($"找不到: {目标}");
    return 2;
}

Console.WriteLine($"扫描 {文件列表.Count} 个文件（目录: {目标}）");
Console.WriteLine();

// ── 统计 ──
var 有地图 = 0;
var 总条目 = 0;
var 失败 = new List<string>();
var 地图分布 = new Dictionary<uint, int>();

foreach (var f in 文件列表)
{
    try
    {
        var 文本 = File.ReadAllText(f, Encoding.UTF8);
        var id = CactbotTimelineParser.解析地图Id(文本);
        if (id.HasValue)
        {
            有地图++;
            地图分布[id.Value] = 地图分布.GetValueOrDefault(id.Value) + 1;
        }
        else
        {
            失败.Add(Path.GetFileName(f) + " (没有 ZoneId 头)");
            continue;
        }

        var 数据 = CactbotTimelineParser.解析(文本, Path.GetFileName(f));
        总条目 += 数据.条目.Count;
    }
    catch (Exception e)
    {
        失败.Add(Path.GetFileName(f) + " (" + e.GetType().Name + ": " + e.Message + ")");
    }
}

Console.WriteLine("=== 解析结果 ===");
Console.WriteLine($"  能认出地图 ID 的: {有地图} / {文件列表.Count}");
Console.WriteLine($"  覆盖不同地图:    {地图分布.Count} 个");
Console.WriteLine($"  解析出条目总数:  {总条目}");
if (失败.Count > 0)
{
    Console.WriteLine($"  [!] 失败 {失败.Count} 个：");
    foreach (var x in 失败.Take(10)) Console.WriteLine("      " + x);
}
Console.WriteLine();

// ══════════════════════════════════════════════════════════════════
//  重点：验证「机制预告」的过滤规则（复刻 TimelineRunner.是有效机制）
// ══════════════════════════════════════════════════════════════════

static bool 是有效机制(时间轴条目 e)
{
    if (e == null || e.是标签) return false;
    if (!e.是技能行) return false;
    if (string.IsNullOrWhiteSpace(e.名称)) return false;

    var 名 = e.名称.Trim();
    if (名.StartsWith("--")) return false;
    if (名.Equals("--sync--", StringComparison.OrdinalIgnoreCase)) return false;
    if (名.All(c => !char.IsLetter(c))) return false;

    return true;
}

// 挑一个条目多、有代表性的文件做详细展示
var 样本 = 文件列表
    .Select(f => (路径: f, 文本: File.ReadAllText(f, Encoding.UTF8)))
    .Select(x => (x.路径, 数据: CactbotTimelineParser.解析(x.文本, Path.GetFileName(x.路径))))
    .Where(x => x.数据.条目.Count > 20)
    .OrderByDescending(x => x.数据.条目.Count)
    .FirstOrDefault();

if (样本.数据 == null)
{
    Console.WriteLine("没有足够大的样本文件，跳过详细展示。");
    return 0;
}

Console.WriteLine("=== 样本文件 ===");
Console.WriteLine($"  {样本.数据.文件名}  ({样本.数据.条目.Count} 条)");
Console.WriteLine($"  地图 ID = {CactbotTimelineParser.解析地图Id(File.ReadAllText(样本.路径, Encoding.UTF8))}");
Console.WriteLine();

var 机制 = 样本.数据.条目.Where(是有效机制).ToList();
Console.WriteLine($"  其中「有效机制」(会告诉 AI 的): {机制.Count} 条");
Console.WriteLine($"  被过滤掉的: {样本.数据.条目.Count - 机制.Count} 条（同步行 / 标签 / 占位）");
Console.WriteLine();

Console.WriteLine($"=== 模拟：战斗第 {时间点:F0} 秒时，AI 会看到什么 ===");
Console.WriteLine();
Console.WriteLine("【副本与机制时间轴】");

// 正在发生（±1.5 秒）
var 当前 = 机制.Where(e => Math.Abs(e.时间 - 时间点) <= 1.5).Take(4).ToList();
if (当前.Count > 0)
    Console.WriteLine("  正在发生：" + string.Join("、", 当前.Select(e => e.名称)));

// 后续 20 秒
var 预告 = 机制.Where(e => e.时间 >= 时间点 - 3 && e.时间 <= 时间点 + 20).Take(12).ToList();
if (预告.Count > 0)
{
    Console.WriteLine("  后续机制（还有几秒 → 名字）：");
    foreach (var e in 预告)
    {
        var 差 = e.时间 - 时间点;
        var 时点 = 差 <= 0.6 ? "刚刚" : $"{差:F1}s 后";
        Console.WriteLine($"    {时点,-10} {e.名称}");
    }
}
else
{
    Console.WriteLine("  后续 20 秒内没有记录到的机制。");
}

// 再往后
var 更远 = 机制.FirstOrDefault(e => e.时间 > 时间点 + 20);
if (更远 != null)
    Console.WriteLine($"  再往后：{更远.时间 - 时间点:F0}s 后 —— {更远.名称}");

Console.WriteLine();
Console.WriteLine("=== 过滤规则自检 ===");
var 可疑 = 机制.Where(e => e.名称.StartsWith("--") || e.名称.StartsWith("#")).ToList();
Console.WriteLine($"  过滤后仍以 -- / # 开头的: {可疑.Count}  {(可疑.Count == 0 ? "[OK]" : "[X] 过滤有漏)")}");

var 空名 = 机制.Count(e => string.IsNullOrWhiteSpace(e.名称));
Console.WriteLine($"  空名字的: {空名}  {(空名 == 0 ? "[OK]" : "[X]")}");

return 0;
