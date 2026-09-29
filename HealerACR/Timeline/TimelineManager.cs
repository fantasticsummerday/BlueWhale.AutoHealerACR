using AEAssist;
using AEAssist.Helper;
using AEAssist.MemoryApi;
using HealerACR.Common;

namespace HealerACR.Timeline;

/// <summary>
/// 时间轴管理器：负责"找对文件"，跑起来的事交给 <see cref="TimelineRunner"/>。
///
///   1. 启动时扫描 ACR 目录下的 <c>Timelines\*.txt</c>，读文件头的 <c># ZoneId: N</c> 建索引
///   2. 战斗中按当前副本 ID 挑出对应那份，装进 Runner
///   3. Runner 每帧推进，命中技能行就提前 <c>时间轴提前秒</c> 发出信号
///
/// 没有时间轴的副本会自动退回读条判断（Res_TeamMitigation 里做了兜底），日随照样跑。
/// </summary>
public static class TimelineManager
{
    /// <summary>地图 ID → 时间轴文件路径</summary>
    private static readonly Dictionary<uint, string> 索引 = new();

    private static readonly TimelineRunner Runner = new();
    private static uint 当前地图;

    /// <summary>给 QT 面板显示的状态</summary>
    public static string 状态 => Runner.状态;

    /// <summary>当前地图 ID（给 AI 描述"在哪个副本"用）</summary>
    public static uint 当前地图Id => 当前地图;

    public static int 条目数 => Runner.有数据 ? 1 : 0;

    /// <summary>
    /// **实际扫描到的**时间轴目录。
    ///
    /// ⚠️ 和 <see cref="时间轴目录"/> 的区别：那个是"候选里第一个存在的"，
    ///    即使**一个都不存在**也会返回第一个候选（看起来像找到了）。
    ///    这个是"真的存在、真的扫了"—— **没有就返回空串**。
    ///
    ///    设置界面显示它，用户才能一眼看出"到底扫没扫到"。
    /// </summary>
    public static string 实际使用的目录 { get; private set; } = "";

    /// <summary>
    /// 时间轴目录：**优先用用户指定的**，否则依次兜底探测。
    ///
    /// ⚠️ 不能只用 Assembly.Location —— Dalamud 是**从内存加载** ACR 的，
    ///    这种情况下 Location 是**空串**，退化成当前目录后就永远找不到时间轴
    ///    （日志里"没有 Timelines 目录"就是这么来的）。
    ///
    ///    而且**目录名和程序集名不一致**：
    ///      ACR 目录叫 `BlueWhale`，但 AuthorName 是"小鲸鱼统治世界" ——
    ///      代码按程序集名拼路径，自然对不上。
    ///
    ///    所以最终的可靠手段是**用户显式指定**
    ///    （设置界面里的「Timelines 目录」输入框）。
    /// </summary>
    public static string 时间轴目录
    {
        get
        {
            foreach (var 候选 in 候选目录())
            {
                try { if (Directory.Exists(候选)) return 候选; } catch { }
            }

            var 全部 = 候选目录().ToList();
            return 全部.Count > 0 ? 全部[0] : "Timelines";
        }
    }

    /// <summary>候选目录，按优先级排列</summary>
    private static IEnumerable<string> 候选目录()
    {
        // ══════════════════════════════════════════════════════════════
        //  ★ 0) 用户**显式指定**的目录 —— 优先级最高 ★
        //
        //    这一条是**根治手段**：下面那些自动探测全都不可靠
        //    （`Assembly.Location` 在 Dalamud 插件里是空的，
        //      目录名又和程序集名不一致 —— 详见 HealSettings.时间轴目录 的说明）。
        //
        //    用户指定了就**先用它**，不再猜。
        // ══════════════════════════════════════════════════════════════
        var 指定 = HealSettings.时间轴目录;
        if (!string.IsNullOrWhiteSpace(指定))
        {
            // 允许用户直接填到 Timelines 本身，或者填它的上一级
            yield return 指定;
            yield return Path.Combine(指定, "Timelines");
        }

        var 名字 = "HealerACR";
        try { 名字 = typeof(TimelineManager).Assembly.GetName().Name ?? 名字; } catch { }

        // 1) dll 旁边（正常情况下就是这个）
        //
        //    ⚠️ **这条在 Dalamud 插件里是失效的**：
        //       ACR 从内存加载，`Assembly.Location` 返回空字符串，
        //       于是拿不到目录 —— 这正是"找不到 Timelines"的根因。
        //       保留它是因为**万一**以后版本能拿到，就能自动生效。
        string dll目录 = null;
        try
        {
            var dll = typeof(TimelineManager).Assembly.Location;
            if (!string.IsNullOrEmpty(dll)) dll目录 = Path.GetDirectoryName(dll);
        }
        catch { }

        if (!string.IsNullOrEmpty(dll目录)) yield return Path.Combine(dll目录, "Timelines");

        // 2) 从 AEAssist.dll 的位置反推 —— ACR 一般就在它附近
        //
        //    ⚠️ 同样依赖 `Assembly.Location`，同样可能拿不到。
        foreach (var ae in AppDomain.CurrentDomain.GetAssemblies())
        {
            string ae目录 = null;
            try
            {
                if (ae.GetName().Name != "AEAssist") continue;
                var loc = ae.Location;
                if (!string.IsNullOrEmpty(loc)) ae目录 = Path.GetDirectoryName(loc);
            }
            catch { }

            if (string.IsNullOrEmpty(ae目录)) continue;

            yield return Path.Combine(ae目录, "ACR", 名字, "Timelines");
            yield return Path.Combine(ae目录, "..", "ACR", 名字, "Timelines");
            yield return Path.Combine(ae目录, "..", "..", "ACR", 名字, "Timelines");
        }

        // 3) 已知的常见布局
        //
        //    ⚠️ 这里是"猜"，而且**猜错过**：目录名和程序集名不一致
        //       （ACR 目录叫 BlueWhale，AuthorName 是"小鲸鱼统治世界"），
        //       所以下面这些组合大概率都不存在。
        //       留着是为了覆盖"作者名恰好等于文件夹名"的情况。
        foreach (var 根 in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrEmpty(根)) continue;
            yield return Path.Combine(根, "ACR", 名字, "Timelines");
            yield return Path.Combine(根, "Output", "ACR", 名字, "Timelines");
            yield return Path.Combine(根, "..", "ACR", 名字, "Timelines");
        }

        // 4) 从设置目录反推 —— 这条**不依赖 Assembly.Location**，
        //    是自动探测里最可能命中的一条：
        //      设置路径 <根>\Settings\Plugins\<作者>\xxx.json
        //      ACR 一般在  <根>\ACR\<名字>\Timelines
        foreach (var 候选 in 从设置目录反推())
            yield return 候选;

        // 5) 当前目录下
        yield return Path.Combine(".", "Timelines");
    }

    /// <summary>
    /// 从"设置文件所在目录"往上推 ACR 目录。
    ///
    /// ⚠️ 这条的价值在于：它**不依赖 `Assembly.Location`** ——
    ///    而后者在 Dalamud 插件里是空的，导致前几条候选全部失效。
    ///    设置目录是 AEAssist 实际传给我们的，一定拿得到。
    /// </summary>
    private static IEnumerable<string> 从设置目录反推()
    {
        string 作者目录 = null;
        try { 作者目录 = Path.GetDirectoryName(HealSettings.当前文件路径); } catch { }

        if (string.IsNullOrEmpty(作者目录)) yield break;

        var 名字 = "HealerACR";
        try { 名字 = typeof(TimelineManager).Assembly.GetName().Name ?? 名字; } catch { }

        // 往上找若干层，每层都试 ACR\<名字>\Timelines 和 <名字>\Timelines
        var 当前 = 作者目录;
        for (var i = 0; i < 4 && !string.IsNullOrEmpty(当前); i++)
        {
            yield return Path.Combine(当前, "ACR", 名字, "Timelines");
            yield return Path.Combine(当前, 名字, "Timelines");

            当前 = Path.GetDirectoryName(当前);
        }
    }

    /// <summary>启动 / 重载时调用一次</summary>
    public static void 初始化()
    {
        索引.Clear();
        当前地图 = 0;
        Runner.卸载();

        // 找不到时把**所有尝试过的路径**打出来（带存在与否），
// 这样一眼就能看出该把 Timelines 放哪
        var 尝试 = new List<string>();
        foreach (var d in 候选目录())
        {
            bool 在;
            try { 在 = Directory.Exists(d); } catch { 在 = false; }
            尝试.Add((在 ? "[有] " : "[无] ") + d);
        }
        var 提示 = "没有 Timelines 目录。已找过：" + string.Join(" | ", 尝试);

        try
        {
            if (Directory.Exists(时间轴目录))
            {
                实际使用的目录 = 时间轴目录;

                foreach (var 文件 in Directory.GetFiles(时间轴目录, "*.txt", SearchOption.AllDirectories))
                {
                    try
                    {
                        var id = CactbotTimelineParser.解析地图Id(File.ReadAllText(文件));
                        if (id is { } v && v != 0) 索引[v] = 文件;
                    }
                    catch (Exception e)
                    {
                        LogHelper.Error($"[HealerACR] 时间轴索引失败 {Path.GetFileName(文件)}: {e.Message}");
                    }
                }

                提示 = $"已索引 {索引.Count} 份时间轴（{实际使用的目录}）";
            }
            else
            {
                实际使用的目录 = "";
            }
        }
        catch (Exception e)
        {
            提示 = "扫描失败: " + e.Message;
            LogHelper.Error($"[HealerACR] 时间轴扫描失败: {e}");
        }

        Runner.设置空闲状态(提示);
        LogHelper.Info($"[HealerACR] {提示}");
    }

    /// <summary>每帧调用（挂在 IRotationEventHandler.OnBattleUpdate 上）</summary>
    public static void 更新(int 战斗毫秒)
    {
        if (!HealSettings.Instance.启用时间轴 || !HealQt.GetQt("时间轴", true))
        {
            Runner.清本帧();
            return;
        }

        if (战斗毫秒 <= 0)
        {
            Runner.清本帧();
            return;
        }

        确保当前时间轴();
        Runner.更新(战斗毫秒 / 1000.0, HealSettings.Instance.时间轴提前秒);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ★ 或门（OR gate）：cactbot 时间轴 或 AEAssist Trigger ★
    //
    //  两个触发源**同时保留**，任一为真就算有需求：
    //    · cactbot 时间轴 —— 本地 txt 文件，精确到时间点
    //    · AEAssist Trigger —— 在它的时间轴编辑器里挂 TriggerAction
    //
    //  **为什么保留两个而不是二选一**：
    //    · cactbot 时间轴是"数据驱动"，改 txt 就能调，不用重新编译
    //    · Trigger 是"图形化编辑"，不写文件也能挂，适合临时调整
    //    · 两者覆盖的场景不同，而且**谁先谁后用不着争** —— 或门天然解决
    //
    //  改在这里的好处：**所有调用点自动获得或门能力**，
    //  不用去 30 多个 resolver 里逐处加判断。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>有没有要求"现在铺减伤"（cactbot 时间轴 或 Trigger，任一即可）</summary>
    public static bool 该铺减伤()
    {
        // 来源①：cactbot 时间轴
        if (Runner.本帧要减伤) return true;

        // 来源②：AEAssist Trigger（外部通过触发器请求）
        if (Common.减伤信号.该减伤()) return true;

        return false;
    }

    /// <summary>
    /// 未来 N 秒附近有没有减伤需求（地星预铺 / 攒资源用）。
    ///
    /// **Trigger 的请求算作"立刻有需求"** —— 因为它没有"提前量"的概念，
    /// 触发就是现在要做。
    /// </summary>
    public static bool 未来有减伤(double 秒后, double 容差 = 1.5)
    {
        // 来源②：Trigger 刚触发 → 相当于"现在就有需求"
        if (Common.减伤信号.该减伤()) return true;

        // 来源①：cactbot 时间轴
        if (!HealSettings.Instance.启用时间轴) return false;
        return Runner.未来有减伤(秒后, 容差);
    }

    /// <summary>再过多少秒会有下一次减伤需求，没有返回 -1</summary>
    public static double 距下次减伤()
    {
        // Trigger 已触发 → 就是现在（0 秒后）
        if (Common.减伤信号.该减伤()) return 0;

        if (!HealSettings.Instance.启用时间轴) return -1;
        return Runner.距下次减伤();
    }

    /// <summary>战斗结束 / 重置</summary>
    public static void 战斗重置()
    {
        Runner.重置();

        // Trigger 信号也要清 —— 否则上一场留下的请求会带到下一场
        Common.减伤信号.重置();
    }

    private static void 确保当前时间轴()
    {
        uint 副本;
        try
        {
            副本 = Core.Resolve<MemApiZoneInfo>().GetCurrTerrId();
        }
        catch
        {
            return;
        }

        if (副本 == 当前地图) return;

        当前地图 = 副本;

        if (!索引.TryGetValue(副本, out var 文件))
        {
            Runner.卸载();
            Runner.设置空闲状态($"副本 {副本} 没有时间轴（走读条判断）");
            return;
        }

        try
        {
            var 文本 = File.ReadAllText(文件);
            Runner.装载(CactbotTimelineParser.解析(文本, Path.GetFileName(文件)));
            LogHelper.Info($"[HealerACR] 载入时间轴：{状态}");
        }
        catch (Exception e)
        {
            Runner.卸载();
            LogHelper.Error($"[HealerACR] 时间轴解析失败: {e}");
        }
    }
}
