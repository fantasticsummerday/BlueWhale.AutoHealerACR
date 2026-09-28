using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// 轻量 IL 扫描：把每个方法体里的 **常量** 和 **调用的方法名** 扒出来。
// 用途：逆向成品 ACR 的判断逻辑（阈值是多少、调了哪些 API），
//       比只看类名/字段名靠谱得多。
//
// 用法: DllPeek.exe <dll> [类型名关键词,逗号分隔]

var path = args.Length > 0 ? args[0] : @"ref\Shiyuvi\Shiyuvi\Shiyuvi.dll";
var filter = args.Length > 1
    ? args[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
    : Array.Empty<string>();

if (!File.Exists(path)) { Console.WriteLine($"找不到 {path}"); return 2; }

using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();

string 解析调用(int token)
{
    try
    {
        var h = MetadataTokens.EntityHandle(token);
        if (h.IsNil) return null;

        if (h.Kind == HandleKind.MethodDefinition)
        {
            var m = md.GetMethodDefinition((MethodDefinitionHandle)h);
            var t = md.GetTypeDefinition(m.GetDeclaringType());
            return $"{md.GetString(t.Name)}.{md.GetString(m.Name)}";
        }

        if (h.Kind == HandleKind.MemberReference)
        {
            var mr = md.GetMemberReference((MemberReferenceHandle)h);
            var 名 = md.GetString(mr.Name);
            if (mr.Parent.Kind == HandleKind.TypeReference)
            {
                var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                return $"{md.GetString(tr.Name)}.{名}";
            }
            return 名;
        }
    }
    catch { }
    return null;
}

foreach (var th in md.TypeDefinitions)
{
    var t = md.GetTypeDefinition(th);
    var tn = md.GetString(t.Name);
    if (tn.StartsWith("<")) continue;

    var ns = md.GetString(t.Namespace);
    var full = string.IsNullOrEmpty(ns) ? tn : $"{ns}.{tn}";
    if (filter.Length > 0 && !filter.Any(f => full.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;

    var 输出 = new List<string>();

    foreach (var mh in t.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        if (m.RelativeVirtualAddress == 0) continue;

        var mn = md.GetString(m.Name);
        if (mn.StartsWith("get_") || mn.StartsWith("set_") || mn.StartsWith("<")) continue;

        byte[] il;
        try { il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes(); }
        catch { continue; }

        var 常量 = new List<int>();
        var 调用 = new List<string>();

        var i = 0;
        while (i < il.Length)
        {
            var op = il[i];

            // ldc.i4.s <byte>
            if (op == 0x1F && i + 1 < il.Length) { 常量.Add((sbyte)il[i + 1]); i += 2; continue; }
            // ldc.i4 <int32>
            if (op == 0x20 && i + 4 < il.Length) { 常量.Add(BitConverter.ToInt32(il, i + 1)); i += 5; continue; }
            // ldc.i4.m1 / 0..8  (0x15..0x1E)
            if (op >= 0x15 && op <= 0x1E) { 常量.Add(op - 0x16); i++; continue; }
            // ldc.i8
            if (op == 0x21 && i + 8 < il.Length) { i += 9; continue; }
            // ldstr  (0x72 + token) -> UserString 堆
            if (op == 0x72 && i + 4 < il.Length)
            {
                try
                {
                    var tok = BitConverter.ToInt32(il, i + 1);
                    var h2 = MetadataTokens.UserStringHandle(tok & 0x00FFFFFF);
                    var s = md.GetUserString(h2);
                    if (!string.IsNullOrEmpty(s)) 调用.Add("STR:" + (s.Length > 60 ? s.Substring(0, 60) + "…" : s));
                }
                catch { }
                i += 5;
                continue;
            }
            // call / callvirt
            if ((op == 0x28 || op == 0x6F) && i + 4 < il.Length)
            {
                var 名 = 解析调用(BitConverter.ToInt32(il, i + 1));
                if (!string.IsNullOrEmpty(名)) 调用.Add(名);
                i += 5;
                continue;
            }
            i++;
        }

        if (调用.Count == 0 && 常量.Count == 0) continue;

        // 只保留有意思的常量（阈值、优先级），过滤掉明显是 token 的杂音
        var 有用常量 = 常量.Where(c => c >= -1000 && c <= 1000).Distinct().Take(12).ToList();
        var 有用调用 = 调用.Distinct()
            .Where(c => c.StartsWith("STR:") || (!c.Contains("get_") && !c.Contains("set_") && !c.StartsWith("String.")))
            .Take(10).ToList();

        var 行 = $"  {mn}";
        if (有用常量.Count > 0) 行 += $"   常量({string.Join(",", 有用常量)})";
        if (有用调用.Count > 0) 行 += $"   调({string.Join(" ", 有用调用)})";
        输出.Add(行);
    }

    if (输出.Count > 0)
    {
        Console.WriteLine($"\n### {full}");
        foreach (var l in 输出) Console.WriteLine(l);
    }
}

return 0;
