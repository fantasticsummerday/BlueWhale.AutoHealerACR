using System.Text;
using Lumina;
using Lumina.Data;

// 导出四奶 + 基础职业的技能，**重点是充能数（MaxCharges）**。
// 有充能的技能如果不在"连按防护"里，就会一口气把多层全交掉（光速就是这么出事的）。
//
// 用法: SpellDump.exe "D:\FF14\最终幻想XIV\game" 输出前缀

Console.OutputEncoding = Encoding.UTF8;

var root = args.Length > 0 ? args[0] : @"D:\FF14\最终幻想XIV\game";
var prefix = args.Length > 1 ? args[1] : "dump";

var candidates = new List<string>();
foreach (var c in root.Split(';', StringSplitOptions.RemoveEmptyEntries))
{
    if (Directory.Exists(c)) candidates.Add(c);
    var sub = Path.Combine(c, "sqpack");
    if (Directory.Exists(sub)) candidates.Add(sub);
}

GameData data = null;
foreach (var path in candidates)
{
    try
    {
        data = new GameData(path, new LuminaOptions { DefaultExcelLanguage = Language.ChineseSimplified });
        Console.WriteLine($"OK {path}");
        break;
    }
    catch (Exception e)
    {
        Console.WriteLine($"   {path}: {e.Message}");
    }
}

if (data == null) return 2;

var actions = data.Excel.GetSheet<Lumina.Excel.Sheets.Action>();
var jobName = new Dictionary<uint, string>
{
    [6] = "幻术", [26] = "秘术", [24] = "白魔", [28] = "学者", [33] = "占星", [40] = "贤者",
};

var lines = new List<string> { "Id\tJob\tLevel\tCharges\tRecastSec\tName" };
var 有充能 = new List<string>();

foreach (var row in actions)
{
    uint job;
    try { job = row.ClassJob.RowId; } catch { continue; }
    if (!jobName.ContainsKey(job)) continue;

    uint level = 0, charges = 0, recast = 0;
    try { level = row.ClassJobLevel; } catch { }
    try { charges = row.MaxCharges; } catch { }
    try { recast = row.Recast100ms; } catch { }

    var name = row.Name.ToString();

        // ★ CastType = 技能形状 ★
        //   1=单体 2=圆形(目标中心) 3=扇形/锥形 4=圆形(自身) 5=直线
        //   这个字段决定 AOE 该用哪种选目标算法
        var castType = 0;
        try { castType = (int)row.CastType; } catch { }

        var range = 0;
        try { range = row.Range; } catch { }

        var effectRange = 0;
        try { effectRange = row.EffectRange; } catch { }

    lines.Add($"{row.RowId}\t{jobName[job]}\t{level}\t{charges}\t{recast / 10.0:0.0}\t{castType}\t{range}\t{effectRange}\t{name}");

    if (charges > 1) 有充能.Add($"{jobName[job]}  {name,-14} id={row.RowId,-6} 充能={charges}  复唱={recast / 10.0:0.0}s  等级={level}");
}

File.WriteAllLines($"{prefix}_actions.tsv", lines, Encoding.UTF8);
Console.WriteLine($"\n导出 {lines.Count - 1} 条 -> {prefix}_actions.tsv");

Console.WriteLine($"\n=== 四奶里**有充能**的技能（{有充能.Count} 个）===");
foreach (var s in 有充能.OrderBy(x => x)) Console.WriteLine("  " + s);

return 0;
