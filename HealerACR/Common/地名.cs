using System.Text.Json;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// **地名解析** —— 把游戏给的 TerritoryType ID 变成人话。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么必须单独做 ★
///
///  ── 现象 ──
///    站在**雪都房区**（伊修加德穹顶皓天）时，横幅显示：
///        `学者 Lv100 · 副本#979`
///    他当场指出："这个不是阿罗阿罗岛，我在雪都房区。"
///
///  ── 根因：**两套编号空间被混用了** ──
///    游戏里 `MemApiZoneInfo.GetCurrTerrId()` 返回的是
///    **TerritoryType 的 RowId**（979 = 伊修加德城区/穹顶皓天）。
///
///    而原来那张 `DutyNames.json` 的键是
///    **ContentFinderCondition 的行号**（1 = 托托·拉克千狱、8 = 布雷福洛克斯）。
///
///    两套编号**完全不相干**：
///        布雷福洛克斯：CFC = 8，  TerritoryType = 206
///        伊修加德房区：TerritoryType = 979，**CFC 里根本没有这一条**
///
///    ⇒ 拿 TerritoryType 去查 CFC 表，**能不能命中全靠巧合**。
///      之前"布雷福洛克斯显示对了"纯属 206 恰好没撞上别的名字。
///      （更糟的是 979 在 CFC 表里确实有个同号的"异闻阿罗阿罗岛"，
///        于是显示出了一个**完全错误但看起来合理**的副本名 ——
///        这种错最难发现。）
///
///  ── 修法 ──
///    用 `ContentFinderCondition.TerritoryType` 这个**引用字段**把两套编号连起来，
///    由 `tools/TerritoryDump` 导出两份**以 TerritoryType 为键**的表：
///        · `TerritoryNames.json`  —— 638 条，真·副本名
///        · `TerritoryPlaces.json` —— 1128 条，地点名（房区 / 野外 / 主城）
///
///  ── 解析优先级（顺序很重要）──
///    ① 副本表命中        → 副本名（"休养胜地布雷福洛克斯野营地"）
///    ② 地点表命中        → 地点名（"穹顶皓天"）
///    ③ 都没有            → `区域#<id>`（照实说，不编）
///
///  ⚠️ ①优先于②的原因：副本的 TerritoryType 在两张表里都有，
///     但需要知道的是"我在打什么本"，不是"这块地的名字"。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 地名
{
    private static Dictionary<string, string>? _副本表;
    private static Dictionary<string, string>? _地点表;
    private static long _加载时间;
    private static string? _目录;

    private const long 缓存毫秒 = 600_000;   // 10 分钟（文件可能被工具更新）

    /// <summary>
    /// 找到放表文件的目录 —— 多候选，逐个试。
    ///
    /// ⚠️ 不能只用 `AppContext.BaseDirectory`：
    ///    实测它在这个宿主下**不指向插件目录**，
    ///    于是 `File.Exists` 一直 false、表永远是 null、
    ///    最后所有副本都退化成 `副本#<id>`（而且**一声不响**）。
    ///    `Assembly.Location` 才是 DLL 的物理路径，最可靠。
    /// </summary>
    private static string? 找目录()
    {
        // ⚠️ 两个分支的失败原因都要打出来 ——
        //    原来两个 catch 都是空的，于是"找不到目录"这件事在日志里
        //    **完全不可见**：既没有"已加载"也没有"加载失败"，只有一片沉默。
        //    实测就是这样：跑了几十场，`[地名]` 一次都没出现过，
        //    而 `是副本()` 对**所有**地图都返回 false
        //    （连 1048「究极神兵破坏作战」都判成"不是副本"）。
        string 程序集 = "";
        string 基准 = "";
        string 比1 = "?", 比2 = "?";

        try
        {
            程序集 = System.Reflection.Assembly.GetExecutingAssembly().Location ?? "";
        }
        catch (Exception e) { 程序集 = "（读取异常：" + e.Message + "）"; }

        try
        {
            基准 = AppContext.BaseDirectory ?? "";
        }
        catch { }

        try
        {
            if (!string.IsNullOrWhiteSpace(程序集))
            {
                var d = Path.GetDirectoryName(程序集);
                if (!string.IsNullOrWhiteSpace(d))
                {
                    比1 = Path.Combine(d, "TerritoryNames.json");
                    if (File.Exists(比1))
                    {
                        LogHelper.Info("[地名] 数据目录（取自程序集位置）：" + d);
                        return d;
                    }
                }
            }
        }
        catch (Exception e) { 比1 = "（异常：" + e.Message + "）"; }

        try
        {
            if (!string.IsNullOrWhiteSpace(基准))
            {
                比2 = Path.Combine(基准, "TerritoryNames.json");
                if (File.Exists(比2))
                {
                    LogHelper.Info("[地名] 数据目录（取自 BaseDirectory）：" + 基准);
                    return 基准;
                }
            }
        }
        catch (Exception e) { 比2 = "（异常：" + e.Message + "）"; }

        // ══════════════════════════════════════════════════════════
        //  ③ **从设置目录反推** —— 这条**不依赖 `Assembly.Location`**
        //
        //  ⚠️ 为什么必须有它（实测）：
        //     前两条在 Dalamud 里**都是空的** ——
        //     日志原文：`程序集= ｜ BaseDirectory= ｜ 试过：? ／ ?`
        //     ACR 是从内存加载的，`Assembly.Location` 返回空字符串。
        //
        //     而时间轴那边**早就有**这条推导（`TimelineManager.从设置目录反推()`），
        //     所以时间轴一直能找到目录、地名一直找不到 ——
        //     同一个插件里两个模块，一个能工作一个不能，差别就在这里。
        //
        //  推导链：
        //     设置文件 <根>\Settings\Plugins\<作者>\奶妈设置_xxx.json
        //     → 往上找若干层，每层试 ACR\BlueWhale\ 和 BlueWhale\
        // ══════════════════════════════════════════════════════════
        try
        {
            string? 作者目录 = null;
            try { 作者目录 = Path.GetDirectoryName(HealSettings.当前文件路径); } catch { }

            if (!string.IsNullOrWhiteSpace(作者目录))
            {
                // ⚠️ 用**程序集名**（BlueWhale）而不是硬编码 "HealerACR" ——
                //    部署目录叫 BlueWhale，硬编码会一直猜错。
                var 名字 = "BlueWhale";
                try { 名字 = typeof(地名).Assembly.GetName().Name ?? 名字; } catch { }

                var 当前 = 作者目录;
                for (var i = 0; i < 5 && !string.IsNullOrWhiteSpace(当前); i++)
                {
                    foreach (var 候选 in new[]
                             {
                                 Path.Combine(当前, "ACR", 名字),
                                 Path.Combine(当前, 名字),
                                 Path.Combine(当前, "ACR", "BlueWhale"),
                             })
                    {
                        try
                        {
                            if (File.Exists(Path.Combine(候选, "TerritoryNames.json")))
                            {
                                LogHelper.Info("[地名] 数据目录（从设置目录反推）：" + 候选);
                                return 候选;
                            }
                        }
                        catch { }
                    }

                    当前 = Path.GetDirectoryName(当前);
                }
            }
        }
        catch (Exception e)
        {
            LogHelper.Info("[地名] 从设置目录反推失败：" + e.Message);
        }

        // ★ 三条都失败 → 把**实际试过的路径**打出来 ★
        LogHelper.Info($"[地名] 找不到数据目录 ｜ 程序集={程序集} ｜ BaseDirectory={基准} " +
                       $"｜ 试过：{比1} ／ {比2} ｜ 设置目录反推也失败" +
                       "（本来还有内嵌兜底表，见 常见副本表）");
        return null;
    }

    private static void 确保加载()
    {
        if (_副本表 != null && TimeHelper.Now() - _加载时间 < 缓存毫秒) return;

        _目录 ??= 找目录();
        if (_目录 == null) return;

        try
        {
            var 选项 = new JsonSerializerOptions { WriteIndented = false };

            var p1 = Path.Combine(_目录, "TerritoryNames.json");
            if (File.Exists(p1))
                _副本表 = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p1));

            var p2 = Path.Combine(_目录, "TerritoryPlaces.json");
            if (File.Exists(p2))
                _地点表 = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p2));

            _加载时间 = TimeHelper.Now();

            LogHelper.Info($"[地名] 已加载：副本 {_副本表?.Count ?? 0} 条 / 地点 {_地点表?.Count ?? 0} 条" +
                           $"（目录 {_目录}）");
        }
        catch (Exception e)
        {
            LogHelper.Info("[地名] 加载失败：" + e.Message);
        }
    }

    /// <summary>
    /// **TerritoryType ID → 人话名字**。
    ///
    /// <paramref name="是副本"/> 为 true 时优先查副本表（进本时用），
    /// 否则优先查地点表（在城里/野外时用）。
    /// 两张表都查不到就返回 `区域#&lt;id&gt;`。
    /// </summary>
    public static string 解析(uint territoryId, bool 是副本 = false)
    {
        if (territoryId == 0) return "未知区域";

        try
        {
            确保加载();

            var key = territoryId.ToString();

            string? 副本名 = null;
            string? 地点名 = null;

            try { _副本表?.TryGetValue(key, out 副本名); } catch { }
            try { _地点表?.TryGetValue(key, out 地点名); } catch { }

            // 按调用场景决定优先看哪张表（见类注释里"①优先于②"的说明）
            if (是副本)
            {
                if (!string.IsNullOrWhiteSpace(副本名)) return 副本名!;
                if (!string.IsNullOrWhiteSpace(地点名)) return 地点名!;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(地点名)) return 地点名!;
                if (!string.IsNullOrWhiteSpace(副本名)) return 副本名!;
            }
        }
        catch { }

        // ══════════════════════════════════════════════════════════
        //  ★ 兜底①：**内嵌的常见副本表** ★
        //
        //  ⚠️ 顺序很重要：**先查内嵌表，再退化到 `区域#<id>`**。
        //     外部文件读不到时（实测 `Assembly.Location` 是空串 → 文件找不到），
        //     内嵌表就是唯一能给出**真名**的来源。
        //
        //     不给真名的后果不是"少个装饰" —— AI 收到的会是 `区域#1048`，
        //     而那是个它无法理解的编号（这正是"AI 不知道在打什么本"的根因）。
        // ══════════════════════════════════════════════════════════
        try
        {
            var 兜底 = 常见副本表.查(territoryId);
            if (!string.IsNullOrWhiteSpace(兜底)) return 兜底!;
        }
        catch { }

        // ⚠️ 最后兜底照实说 —— 不要把 ID 伪装成名字
        return $"区域#{territoryId}";
    }

    /// <summary>这个 TerritoryType 是不是一个**副本**（在副本表里）</summary>
    public static bool 是副本(uint territoryId)
    {
        if (territoryId == 0) return false;

        try
        {
            确保加载();
            // ⚠️ 外部表说"是"就算 —— 但外部表**读不到时**要认内嵌表，
            //    否则 `是副本()` 对所有地图返回 false（实测踩过这个坑）
            if (_副本表?.ContainsKey(territoryId.ToString()) == true) return true;

            return 常见副本表.查(territoryId) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>换本 / 重载时清缓存（表文件可能被工具更新过）</summary>
    public static void 重置()
    {
        _副本表 = null;
        _地点表 = null;
        _加载时间 = 0;
        _目录 = null;
    }
}
