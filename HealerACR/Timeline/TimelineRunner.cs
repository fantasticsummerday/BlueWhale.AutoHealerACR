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
                    LogHelper.Info($"[HealerACR] 时间轴 → 准备减伤：{e.名称}（{e.时间:F1}s）");
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
    private bool 处理跳转(时间轴条目 e, double 实际秒)
    {
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

        LogHelper.Info($"[HealerACR] 时间轴跳转 → {j.跳转}（偏移 {offset:F1}s）");
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
