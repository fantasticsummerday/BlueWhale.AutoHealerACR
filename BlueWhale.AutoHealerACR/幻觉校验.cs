using System.Text;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// **AI 回复的幻觉校验** —— 检查它提到的技能在当前等级是否真的可用。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么需要（用户实测）★
///
///    AI 在 **50 级副本**里建议了 `25865 极炎法` —— 那是 **82 级技能**。
///    而系统提示的铁律 18 明确写着"不许建议清单外的技能"。
///
///    问题在于：**提示词约束是不可验证的。** AI 的输出是自由文本，
///    解析层只认"技能ID|理由"或"编号"，对它在推理里提到的其他技能
///    一无所知 —— 于是"它又编了"这件事**没有任何记录**。
///
///    ⇒ 加这一层：把回复里出现的技能引用抽出来，跟"当前等级真正可用的
///      清单"对一遍。这样"AI 幻觉率"变成一个**可量化的数字**。
///
///  ★ 判据（刻意保守，宁可漏报不可误报）★
///
///    ① **技能 ID**：能在 `SpellIds` 里**反查到名字**的才算技能 ID。
///       反查不到的多半是血量/时间/威力数字 —— 一律不记。
///       是技能但 `SpellUtil.已解锁` 为假 => **越级建议**（记）。
///
///    ② **技能名**：只有出现在 `SpellIds` 表里的中文名才算候选。
///       其余中文词（"治疗""坦克""输出"）一律不记 ——
///       否则每次回复都会误报一堆。
///
///    [!] 误报会让这个数字失去意义 —— 所以宁可漏报。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
internal static class 幻觉校验
{
    /// <summary>累计抓到的越级/不存在的技能次数</summary>
    public static int 越级次数 { get; private set; }

    /// <summary>累计检查过的回复条数</summary>
    public static int 检查次数 { get; private set; }

    /// <summary>最近一次抓到的问题（给调试窗/日志）</summary>
    public static string 最近问题 { get; private set; } = "";

    /// <summary>越级建议的明细（最近 8 条）</summary>
    private static readonly List<string> _明细 = new();

    public static string 明细摘要()
    {
        lock (_锁)
        {
            return _明细.Count == 0 ? "（无）" : string.Join("、", _明细);
        }
    }

    private static readonly object _锁 = new();

    /// <summary>清空（换本 / 切职业时）</summary>
    public static void 重置()
    {
        lock (_锁)
        {
            越级次数 = 0;
            检查次数 = 0;
            最近问题 = "";
            _明细.Clear();
        }
    }

    /// <summary>
    /// **检查一段 AI 回复**（解析后调用）。
    ///
    /// [!] 只在**解析成功**时调 —— 解析失败的回复没有分析价值
    ///     （而且格式噪声会被误判成技能名）。
    /// </summary>
    public static void 检查(string? 回复)
    {
        if (string.IsNullOrWhiteSpace(回复)) return;

        try
        {
            lock (_锁)
            {
                检查次数++;

                var 问题 = new List<string>();

                // ── ① 技能 ID ──
                //     只认"能在 SpellIds 里反查到名字"的数字 ——
                //     反查不到的是血量/时间/威力，不是技能。
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(回复, @"\b(\d{3,6})\b"))
                {
                    if (!uint.TryParse(m.Groups[1].Value, out var id)) continue;

                    string 名;
                    try { 名 = HealerACR.Common.SpellIds.反查(id); }
                    catch { continue; }

                    // 反查不到（返回空/等于 ID 本身）=> 不是技能 ID
                    if (string.IsNullOrWhiteSpace(名) || 名 == id.ToString()) continue;

                    // 是技能 —— 当前等级解锁了吗？
                    bool 解锁;
                    try { 解锁 = HealerACR.Common.SpellUtil.已解锁(id); }
                    catch { continue; }

                    if (!解锁) 问题.Add($"{id}={名}(未解锁)");
                }

                // ── ② 技能名（中文）──
                //     [!] 只查"表里真的有这个名字"的 —— 其余中文词一律跳过。
                //        否则"治疗""坦克""移动"全会被当成技能名刷屏。
                var 全部名 = new List<string>();
                try { 全部名.AddRange(HealerACR.Common.SpellIds.全部技能名()); }
                catch { }

                if (全部名.Count > 0)
                {
                    // 长名字优先匹配（避免"医术"命中"医术之心"这类子串问题）
                    全部名.Sort((a, b) => b.Length.CompareTo(a.Length));

                    foreach (var 名 in 全部名)
                    {
                        if (名.Length < 2) continue;
                        if (!回复.Contains(名)) continue;

                        // 已经按 ID 记过的不重复记
                        var id = 0u;
                        try { id = HealerACR.Common.SpellIds.取(名); } catch { }
                        if (id != 0 && 问题.Any(x => x.StartsWith(id + "="))) continue;

                        if (id == 0) continue;   // 表里有名字但拿不到 ID —— 跳过

                        bool 解锁;
                        try { 解锁 = HealerACR.Common.SpellUtil.已解锁(id); }
                        catch { continue; }

                        if (!解锁) 问题.Add($"{名}(未解锁)");
                    }
                }

                if (问题.Count == 0) return;

                越级次数 += 问题.Count;
                最近问题 = string.Join("、", 问题.Distinct().Take(6));

                foreach (var x in 问题.Distinct())
                {
                    if (_明细.Count < 8 && !_明细.Contains(x)) _明细.Add(x);
                }

                // [!] 只打日志，**不做任何拦截** ——
                //     这是观测手段，不是执行路径。
                //     拦截交给原有的"技能不可用"处理（那是可靠的）。
                try
                {
                    BlueWhale.AutoHealerACR.Ai调试.日志(
                        $"[幻觉校验] AI 提到了当前等级用不了的技能：{最近问题}");
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>给调试窗/统计面板的一行</summary>
    public static string 状态描述()
    {
        lock (_锁)
        {
            if (检查次数 == 0) return "还没检查过";
            var 率 = 检查次数 == 0 ? 0f : 越级次数 * 100f / 检查次数;
            return $"越级建议 {越级次数} 处 / 检查 {检查次数} 条（{率:F0}%）" +
                   (string.IsNullOrEmpty(最近问题) ? "" : $"｜最近：{最近问题}");
        }
    }
}
