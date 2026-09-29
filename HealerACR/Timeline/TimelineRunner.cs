using AEAssist;
using AEAssist.Helper;
using AEAssist.MemoryApi;
using HealerACR.Common;

namespace HealerACR.Timeline;

/// <summary>
/// 时间轴运行时：把一份静态时间轴"跑"起来。
///
/// 关键机制是 <b>时间偏移</b>：
///   cactbot 用 jump / label 表达多阶段（同一个副本的不同分支）。
///   线性排的话，一旦实际走了另一条分支，后面所有条目都会差几百秒。
///   所以这里维护一个 offset，满足：
///
///       文件时间 = 实际时间 + offset
///
///   发生 `jump "L"`（在文件时间 T、目标是 label 时间 L）时：
///       offset += L - T
///
///   拿真实文件验证过：valigarmanda 在 108.4s 跳到 label 508.4，
///   offset 变成 400，之后 519.8 的 "Skyruin (storm phase)"
///   实际触发时间就是 519.8 - 400 = 119.8s —— 正好对上文件里那条
///   `119.8 "Skyruin (storm/ice phase?)"`。
///
/// 分支怎么选：
///   同一个时刻有多条带不同技能 ID 的 jump（storm / ice），
///   运行时盯着 <c>BattleData.EnemyCastSpellHistory</c>，
///   看到哪个技能出现就走哪条；等太久（8 秒）就退回第一条。
/// </summary>
public class TimelineRunner
{
    private 时间轴数据? 数据;
    private int 游标;
    private double offset;
    private double 等待分支起始 = -1;
    private double 当前实际秒;

    private readonly HashSet<uint> 已见技能 = new();
    private int 上次历史条数 = -1;

    /// <summary>本帧要不要铺减伤</summary>
    public bool 本帧要减伤 { get; private set; }

    /// <summary>给面板显示用</summary>
    public string 状态 { get; private set; } = "无时间轴";

    public bool 有数据 => 数据 != null && 数据.条目.Count > 0;

    public void 装载(时间轴数据 d)
    {
        数据 = d;
        重置();
        预处理();
        状态 = $"{d.文件名} · {d.条目.Count} 条" + (d.有跳转 ? "（含分支）" : "");
    }

    public void 卸载()
    {
        数据 = null;
        重置();
        状态 = "无时间轴";
    }

    public void 重置()
    {
        游标 = 0;
        offset = 0;
        等待分支起始 = -1;
        当前实际秒 = 0;
        本帧要减伤 = false;
        已见技能.Clear();
        上次历史条数 = -1;
    }

    /// <summary>时间轴被关掉 / 没有战斗时间时，别让上一帧的信号残留</summary>
    public void 清本帧() => 本帧要减伤 = false;

    /// <summary>没有时间轴在跑时，给面板显示个说明</summary>
    public void 设置空闲状态(string 文本) => 状态 = 文本;

    /// <summary>每帧调用一次</summary>
    public void 更新(double 实际秒, float 提前秒)
    {
        本帧要减伤 = false;
        当前实际秒 = 实际秒;

        if (数据 == null) return;

        收集已见技能();

        var 保护 = 0;
        while (游标 < 数据.条目.Count && 保护++ < 2000)
        {
            var 文件秒 = 实际秒 + offset;
            var e = 数据.条目[游标];

            // 还没到（留出提前量）
            if (e.时间 > 文件秒 + 提前秒) break;

            // ---- 跳转行 ----
            if (!string.IsNullOrEmpty(e.跳转))
            {
                if (!处理跳转(e, 实际秒)) return; // 在等分支判定，本帧先停在这
                continue;
            }

            // ---- 技能行 ----
            if (e.是技能行)
            {
                // 没完全错过才触发（中途进本 / 卡顿会跳过一堆）
                if (e.时间 >= 文件秒 - 1.0 && e.需减伤)
                {
                    本帧要减伤 = true;
                    LogHelper.Info($"[HealerACR] 时间轴 -> 准备减伤：{e.名称}（{e.时间:F1}s）");
                }
            }

            游标++;
        }
    }

    /// <summary>
    /// 未来 <paramref name="秒后"/> 秒附近有没有减伤需求。
    /// 地星（10 秒后爆）和"临近大伤害就别花资源"都靠它。
    /// </summary>
    public bool 未来有减伤(double 秒后, double 容差 = 1.5)
    {
        if (数据 == null) return false;

        var 目标文件秒 = 当前实际秒 + offset + 秒后;

        // 只看游标之后的（已经处理过的不用再管）
        for (var i = 游标; i < 数据.条目.Count; i++)
        {
            var e = 数据.条目[i];
            if (e.时间 > 目标文件秒 + 容差) break;
            if (!e.是技能行 || !e.需减伤) continue;
            if (Math.Abs(e.时间 - 目标文件秒) <= 容差) return true;
        }

        return false;
    }

    /// <summary>再过多久会有下一次减伤需求（秒），没有就返回 -1</summary>
    public double 距下次减伤()
    {
        if (数据 == null) return -1;
        for (var i = 游标; i < 数据.条目.Count; i++)
        {
            var e = 数据.条目[i];
            if (!e.是技能行 || !e.需减伤) continue;
            var 差 = e.时间 - (当前实际秒 + offset);
            return 差 < 0 ? 0 : 差;
        }

        return -1;
    }

    // ══════════════════════════════════════════════════════════════════
    //  ★ 给 AI 看的"机制预告" API ★
    //
    //  语义："要让 ai 知道进的什么副本、会有什么机制，
    //            出现机制时 / 时间轴内告知后续有机制时也要告诉 ai"
    //
    //  ── 为什么这件事重要 ──
    //    AI 之前只拿到 3 行时间轴信息（该不该铺减伤 / 未来5秒 / 距下次）。
    //    它**不知道接下来会发生什么**，只能对"现在"做反应式治疗 ——
    //    而治疗的很多决策本质是**预判**：
    //      · 大伤害还有 8 秒 → 现在可以把 HoT 铺上、把能力技留一留
    //      · 马上要来连续伤害 → 别把蓝花/以太现在花光
    //      · 刚过一个机制 → 可以放心打一轮输出
    //    这些判断没有"后续机制"就是做不到的。
    //
    //  ── 设计取舍 ──
    //    ① 只给**名字 + 还有几秒 + 要不要减伤**，不给全部原始字段 ——
    //       时间轴条目动辄几百条，全塞进去会挤爆 prompt（有 6000 字符上限）。
    //    ② **过滤掉同步行**（`--sync--` / `--Reset--` 之类）：
    //       它们对人类是"这里该对表了"，对 AI 是纯噪声。
    //    ③ 时间用**相对秒数**（还有几秒），不是绝对时间戳 ——
    //       AI 不需要知道"战斗第 137 秒"，它需要知道"还有 8 秒"。
    //    ④ 上限保护：条数太多时只取最近的 N 条。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>这一条值不值得告诉 AI（滤掉同步行/占位行）</summary>
    private static bool 是有效机制(时间轴条目 e)
    {
        if (e == null || e.是标签) return false;
        if (!e.是技能行) return false;
        if (string.IsNullOrWhiteSpace(e.名称)) return false;

        var 名 = e.名称.Trim();

        // cactbot 的同步/占位行 —— 对人类是"该对表了"，对 AI 是噪声
        if (名.StartsWith("--")) return false;
        if (名.Equals("--sync--", StringComparison.OrdinalIgnoreCase)) return false;

        // 纯数字/纯符号的名字也滤掉
        if (名.All(c => !char.IsLetter(c))) return false;

        return true;
    }

    /// <summary>
    /// 未来 <paramref name="秒内"/> 秒内会出现的机制（从游标往后找）。
    ///
    /// 返回 `(还有几秒, 名字, 要不要减伤)` 三元组，按时间升序。
    /// 现在正在发生的那一条**也包含在内**（此时"还有几秒"是 0 或负数）。
    /// </summary>
    public List<(double 还有几秒, string 名称, bool 需减伤)> 未来机制(double 秒内, int 最多几条 = 12)
    {
        var 结果 = new List<(double, string, bool)>();
        if (数据 == null) return 结果;

        try
        {
            var 现在 = 当前实际秒 + offset;

            for (var i = 游标; i < 数据.条目.Count; i++)
            {
                var e = 数据.条目[i];
                if (!是有效机制(e)) continue;

                var 差 = e.时间 - 现在;
                if (差 > 秒内) break;                 // 已按时间升序，后面的更远

                // ⚠️ 太老的（已经过去很久的）不再报，否则每帧都在念历史
                if (差 < -3.0) continue;

                结果.Add((差, e.名称.Trim(), e.需减伤));

                if (结果.Count >= 最多几条) break;
            }
        }
        catch { }

        return 结果;
    }

    /// <summary>
    /// **下一条**机制 —— 不管还有多远都返回。
    /// 用于"当前这个本接下来还有什么"这种长视野判断。
    /// </summary>
    public (double 还有几秒, string 名称, bool 需减伤)? 下一条机制()
    {
        if (数据 == null) return null;

        try
        {
            var 现在 = 当前实际秒 + offset;

            for (var i = 游标; i < 数据.条目.Count; i++)
            {
                var e = 数据.条目[i];
                if (!是有效机制(e)) continue;

                return (e.时间 - 现在, e.名称.Trim(), e.需减伤);
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// **正在发生**的机制（时间上刚好落在这一刻附近的那几条）。
    ///
    /// "附近"的窗口取 ±1.5 秒 —— 时间轴写的是技能**判定**时刻，
    /// 而实际伤害结算 / 玩家感受会有一点偏移，窗口太窄会漏报。
    /// </summary>
    public List<(string 名称, bool 需减伤)> 当前机制(double 窗口 = 1.5, int 最多几条 = 4)
    {
        var 结果 = new List<(string, bool)>();
        if (数据 == null) return 结果;

        try
        {
            var 现在 = 当前实际秒 + offset;

            for (var i = 游标; i < 数据.条目.Count; i++)
            {
                var e = 数据.条目[i];
                if (!是有效机制(e)) continue;

                var 差 = e.时间 - 现在;
                if (差 > 窗口) break;
                if (差 < -窗口) continue;

                结果.Add((e.名称.Trim(), e.需减伤));
                if (结果.Count >= 最多几条) break;
            }
        }
        catch { }

        return 结果;
    }

    /// <summary>时间轴文件里总共记录了多少条机制（给 AI 一个"这个本多长"的体感）</summary>
    public int 机制总数()
    {
        if (数据 == null) return 0;
        try { return 数据.条目.Count(是有效机制); } catch { return 0; }
    }

    /// <summary>当前装载的时间轴文件名（就是副本名 —— cactbot 按副本命名文件）</summary>
    public string 文件名 => 数据?.文件名 ?? string.Empty;

    // ==================== 内部 ====================

    /// <summary>装载时把"要减伤"的条目先筛出来，免得每帧去问 IsBossAoe</summary>
    private void 预处理()
    {
        if (数据 == null) return;

        foreach (var e in 数据.条目)
        {
            if (!e.是技能行) continue;
            e.需减伤 = 判定需减伤(e);
        }
    }

    private bool 判定需减伤(时间轴条目 e)
    {
        var 手工 = HealSettings.Instance.时间轴额外技能Id;
        if (!string.IsNullOrWhiteSpace(手工))
        {
            foreach (var piece in 手工.Split(',', '，', ' ', ';'))
            {
                if (uint.TryParse(piece.Trim(), out var id) && e.技能Id.Contains(id)) return true;
            }
        }

        foreach (var id in e.技能Id)
        {
            try
            {
                if (MemApiSpell.IsBossAoe(id)) return true;
            }
            catch
            {
            }
        }

        return false;
    }

    /// <summary>把 EnemyCastSpellHistory 里出现过的技能记下来（分支判定用）</summary>
    private void 收集已见技能()
    {
        try
        {
            var 历史 = AI.Instance.BattleData.EnemyCastSpellHistory;
            if (历史 == null) return;
            if (历史.Count == 上次历史条数) return;
            上次历史条数 = 历史.Count;

            foreach (var kv in 历史)
            {
                已见技能.Add(kv.Key.Item2);
            }
        }
        catch
        {
        }
    }

    /// <summary>处理一条 jump；返回 true 表示可以继续推进，false 表示本帧先停住等分支</summary>
    private bool 处理跳转(时间轴条目 e, double 实际秒)    {
        if (数据 == null) return true;

        var 组 = 收集同组跳转(e.时间);
        if (组.Count == 0) return true;

        // 1) 哪个分支的判定技能已经出现了
        foreach (var j in 组)
        {
            if (j.技能Id.Count == 0) continue;
            if (j.技能Id.Any(已见技能.Contains))
            {
                return 执行跳转(j);
            }
        }

        // 2) 只有一条、又不需要判定的，直接跳
        if (组.Count == 1 && 组[0].技能Id.Count == 0)
        {
            return 执行跳转(组[0]);
        }

        // 3) 等分支：最多等 8 秒，等不到就退回第一条
        if (等待分支起始 < 0) 等待分支起始 = 实际秒;
        if (实际秒 - 等待分支起始 > 8.0)
        {
            LogHelper.Info($"[HealerACR] 时间轴分支判定超时，退回第一条：{组[0].跳转}");
            return 执行跳转(组[0]);
        }

        return false;
    }

    /// <summary>同一时刻（±0.05s）的所有 jump 条目</summary>
    private List<时间轴条目> 收集同组跳转(double 时间)
    {
        var 结果 = new List<时间轴条目>();
        if (数据 == null) return 结果;

        for (var i = 游标; i < 数据.条目.Count; i++)
        {
            var e = 数据.条目[i];
            if (e.时间 > 时间 + 0.05) break;
            if (Math.Abs(e.时间 - 时间) <= 0.05 && !string.IsNullOrEmpty(e.跳转)) 结果.Add(e);
        }

        return 结果;
    }

    private bool 执行跳转(时间轴条目 j)
    {
        if (数据 == null || string.IsNullOrEmpty(j.跳转)) return true;

        double 目标时间;
        if (double.TryParse(j.跳转, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var 数字))
        {
            目标时间 = 数字;
        }
        else if (数据.标签.TryGetValue(j.跳转, out var 标签时间))
        {
            目标时间 = 标签时间;
        }
        else
        {
            // 找不到目标，跳过这行别卡死
            游标++;
            等待分支起始 = -1;
            return true;
        }

        // 核心：调整偏移，让"文件时间 = 实际时间 + offset"继续成立
        offset += 目标时间 - j.时间;

        // 游标移到目标时间之后的第一条
        游标 = 找游标(目标时间);
        等待分支起始 = -1;

        LogHelper.Info($"[HealerACR] 时间轴跳转 -> {j.跳转}（偏移 {offset:F1}s）");
        return true;
    }

    private int 找游标(double 文件时间)
    {
        if (数据 == null) return 0;
        for (var i = 0; i < 数据.条目.Count; i++)
        {
            if (数据.条目[i].时间 >= 文件时间 - 0.001) return i;
        }

        return 数据.条目.Count;
    }
}
