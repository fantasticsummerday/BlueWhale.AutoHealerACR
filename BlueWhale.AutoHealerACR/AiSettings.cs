using System.Text.Json;
using AEAssist.Helper;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// AI 接管相关的设置。
///
/// **API Key 只存本地 json，不进代码、不进发布包。**
/// </summary>
public class AiSettings
{
    // ==================== 连接 ====================

    /// <summary>DeepSeek API Key（sk-开头）。留空则 AI 功能整体不启用。</summary>
    public string ApiKey = "";

    /// <summary>接口地址。换别的兼容 OpenAI 格式的服务也能用。</summary>
    public string Endpoint = "https://api.deepseek.com";

    /// <summary>模型名。deepseek-flash（快、便宜）/ deepseek-pro（慢、贵、更强）。模型名会随版本变，所以界面允许自定义。</summary>
    public string Model = "deepseek-flash";

    /// <summary>单次请求超时（毫秒）。超过就当失败，走降级。</summary>
    public int 超时毫秒 = 10000;
    // ⚠️ 默认从 3000 提到 10000。
    //    3000 对"测试连接"够（0.5 秒返回），但策略层的提示词带完整情境数据，
    //    AI 读上千 token 要 3 秒以上 —— 结果每次都是"请求失败（超时）"。
    //    这个字段现在是**兜底值**，实际调用时两层各自传自己的超时。

    // ==================== 阶段 A：策略层 ====================

    /// <summary>
    /// 启用策略层 —— AI 低频决策：建议治疗阈值、资源该攒还是该卸。
    ///
    /// 刷新间隔宽松（默认 10 秒一次），延迟不敏感，是**先跑通链路**的那一步。
    /// </summary>
    public bool 启用策略层 = false;

    /// <summary>策略层刷新间隔（秒）。太短会烧钱，太长跟不上战场变化。</summary>
    public int 策略刷新秒 = 10;

    // ==================== 阶段 B：决策层 ====================

    /// <summary>
    /// 启用决策层 —— AI 高频出技能建议（提前算好下一步）。
    ///
    /// ⚠️ 对延迟敏感：GCD 只有 2.5 秒，AI 结果必须在窗口内回来。
    /// 所以它是**异步预取**的：后台算，算完了给下一轮用；没算完就用上一轮的。
    /// </summary>
    public bool 启用决策层 = false;

    /// <summary>决策层预取间隔（毫秒）。</summary>
    public int 决策预取毫秒 = 1500;

    // ==================== 安全阀 ====================

    /// <summary>
    /// API 连续失败多少次后放弃 AI，退回原有逻辑。
    ///
    /// **这个必须有** —— 网络挂了不能让 ACR 停摆。
    /// </summary>
    public int 连续失败上限 = 3;

    /// <summary>失败后冷却多少秒再重试</summary>
    public int 失败冷却秒 = 60;

    /// <summary>把 AI 的原始回复打进日志（调试用，平时关掉免得刷屏）</summary>
    public bool 记录原始回复 = false;

    // ==================== 存取 ====================

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // ★★★ 这一行是关键 ★★★
        // System.Text.Json **默认只序列化属性，不序列化字段**！
        // 而本类为了避免 `ref` 冲突，设置项全都用字段（public string ApiKey = ""）——
        // 结果保存出来只有两个只读属性，配置全丢：
        //     { "已配置": true, "需要AI": false }
        // 加上这个开关，字段才会被正常读写。
        IncludeFields = true,
    };

    private static AiSettings? _实例;

    public static AiSettings Instance
    {
        get
        {
            if (_实例 != null) return _实例;

            try
            {
                var 路径 = 设置路径();
                if (File.Exists(路径))
                {
                    var json = File.ReadAllText(路径);
                    _实例 = JsonSerializer.Deserialize<AiSettings>(json, JsonOpts) ?? new AiSettings();

                    if (_实例.ApiKey?.Length > 0)
                    {
                        LogHelper.Info($"[BlueWhale.AI] 已读取设置，Key 长度 {_实例.ApiKey.Length}");
                    }
                }
                else
                {
                    _实例 = new AiSettings();
                }
            }
            catch
            {
                // 读不出来就用默认值，绝不因为设置文件坏了让 ACR 起不来
                _实例 = new AiSettings();
            }

            return _实例;
        }
    }

    /// <summary>战斗中攒下的保存请求，脱战后补写</summary>
    private static bool _待保存;

    /// <summary>
    /// 每帧调用（从心跳里驱动）：脱战后把攒下的保存补上。
    ///
    /// ⚠️ 官方错题集第 5 条：**Update/UI 里做同步 IO 会导致卡顿**。
    ///    用户在战斗中打开设置改一下就会触发写盘 ——
    ///    所以战斗中的保存请求先攒着，脱战后统一写。
    /// </summary>
    public static void 每帧更新()
    {
        if (!_待保存) return;

        try
        {
            if (AEAssist.Core.Me.InCombat()) return;

            _待保存 = false;
            真正保存();
        }
        catch { }
    }

    public static void 保存()
    {
        // 战斗中不写盘，先记着（错题集第 5 条）
        try
        {
            if (AEAssist.Core.Me.InCombat())
            {
                _待保存 = true;
                return;
            }
        }
        catch { }

        真正保存();
    }

    private static void 真正保存()
    {
        try
        {
            var 路径 = 设置路径();
            var 目录 = Path.GetDirectoryName(路径);
            if (!string.IsNullOrEmpty(目录)) Directory.CreateDirectory(目录);

            File.WriteAllText(路径, JsonSerializer.Serialize(Instance, JsonOpts));
        }
        catch (Exception e)
        {
            // ⚠️ 不再静默吞掉 —— 之前就是因为不报错，
            //    用户以为"设置没保存"，实际是写文件失败却没人知道。
            LogHelper.Error($"[BlueWhale.AI] 设置保存失败：{e.GetType().Name} {e.Message}");
        }
    }

    /// <summary>
    /// 设置文件路径。
    /// ⚠️ 和 HealerACR 一样放在 dll 旁边 —— Assembly.Location 可能为空，
    ///    所以用 AppContext.BaseDirectory 兜底。
    /// </summary>
    /// <summary>设置文件的实际路径（界面上显示给用户看）</summary>
    public static string 当前路径() => 设置路径();

    /// <summary>AEAssist 传进来的设置目录（入口 Build 时赋值）</summary>
    private static string? _设置目录;

    /// <summary>
    /// 初始化设置目录。**必须在入口的 Build 里调用**。
    ///
    /// ⚠️ 不要自己猜路径 —— HealerACR 用的是 AEAssist 传进来的 settingFolder
    ///    （实际落在 `D:\FF14\Settings\Plugins\&lt;作者&gt;\`），
    ///    我原来用 AppContext.BaseDirectory，结果写到了游戏根目录，
    ///    和别的 ACR 的配置散在两处。
    /// </summary>
    public static void 初始化(string settingFolder)
    {
        // ⚠️ 只认第一次。
        //    四个职业入口各会调一次 Build → 各调一次这里，
        //    如果每次都覆盖，最后那个（贤者）的目录会赢，
        //    结果"在学者页填的 Key 存到了贤者目录"——用户永远读不到。
        if (!string.IsNullOrEmpty(_设置目录)) return;

        // 路径规范化，去掉 `..\..` 这种未解析片段
        try
        {
            _设置目录 = Path.GetFullPath(settingFolder);
        }
        catch
        {
            _设置目录 = settingFolder;
        }

        // 把设置放在作者目录下，不跟着职业分（和 HealerACR 一致）
        try
        {
            var 父 = Directory.GetParent(_设置目录 ?? "");
            if (父 != null) _设置目录 = 父.FullName;
        }
        catch { }

        try
        {
            var 路径 = 设置路径();
            LogHelper.Info($"[BlueWhale.AI] 设置目录（规范化后）：{_设置目录}");
            LogHelper.Info($"[BlueWhale.AI] 设置文件：{路径}（{(File.Exists(路径) ? "已存在" : "还没生成")}）");
        }
        catch { }
    }

    private static string 设置路径()
    {
        var 目录 = _设置目录;

        if (string.IsNullOrEmpty(目录))
        {
            // 兜底：dll 旁边 → 再兜底基目录
            try
            {
                var dll = typeof(AiSettings).Assembly.Location;
                目录 = string.IsNullOrEmpty(dll)
                    ? AppContext.BaseDirectory
                    : Path.GetDirectoryName(dll) ?? AppContext.BaseDirectory;
            }
            catch
            {
                目录 = AppContext.BaseDirectory;
            }
        }

        return Path.Combine(目录, "BlueWhale.settings.json");
    }

    /// <summary>AI 功能是否整体可用（有 Key + 至少启用了一层）</summary>
    public bool 已配置 => !string.IsNullOrWhiteSpace(ApiKey);

    public bool 需要AI => 启用策略层 || 启用决策层;
}
