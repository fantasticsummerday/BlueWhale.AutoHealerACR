using System.Text.Json;
using AEAssist.Helper;
using HealerACR.Common;

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

    /// <summary>模型名。deepseek-flash（快、便宜）/ deepseek-v4-pro（慢、贵、更强）。模型名会随版本变，所以界面允许自定义。</summary>
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

    // ==================== 记忆库位置 ====================

    /// <summary>
    /// 战斗记忆 / 记录 / 记忆库的保存目录。**留空 = 用默认位置**。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么要独立出来 ★
    ///
    ///    原来它跟着"ACR 设置目录"走，实际落在：
    ///      `D:\FF14\Settings\Plugins\记忆`
    ///
    ///    三个问题：
    ///      ① **难找** —— 埋在 Settings\Plugins 里，
    ///         那个目录下还塞着各种 ACR 的文件夹，一眼看不出来是我们的
    ///      ② **容易被误删** —— 重装 ACR、清理插件设置时会被顺手清掉，
    ///         而记忆库是**长期积累**的东西，攒几十场才有价值
    ///      ③ **换 ACR 目录就丢** —— 路径跟着设置目录走，设置目录一变，
    ///         老记忆就找不到了（用户会以为"全没了"）
    ///
    ///  ── 默认位置 ──
    ///    `我的文档\BlueWhale记忆库`
    ///    · 在"我的文档"下 → **好找**（资源管理器左栏就有）
    ///    · **不在游戏目录里** → 重装游戏/ACR 都不会动它
    ///    · 和 AEAssist 的目录结构解耦 → 换目录也不丢
    ///
    ///  ⚠️ 留空 ≠ 不保存：留空用的是**默认位置**，
    ///     而不是"退回旧位置" —— 否则新旧行为不一致会更混乱。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public string 记忆目录 = "";

    /// <summary>
    /// 默认记忆目录：`我的文档\BlueWhale记忆库`
    /// </summary>
    public static string 默认记忆目录()
    {
        try
        {
            var 文档 = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(文档))
                return Path.Combine(文档, "BlueWhale记忆库");
        }
        catch { }

        // 兜底：dll 旁边的"记忆"
        try { return Path.Combine(AppContext.BaseDirectory, "记忆"); }
        catch { return "记忆"; }
    }

    /// <summary>
    /// 实际使用的记忆目录 —— **所有记忆/记录都走这里**。
    ///
    /// 这是唯一的权威入口：对局记录 / 记忆库 / 战斗记忆 三处都调它，
    /// 不要在别处自己拼路径（否则改了设置有的地方生效有的不生效）。
    /// </summary>
    public static string 记忆根目录()
    {
        using var _深度 = HealerACR.Common.调用深度.进("AiSettings.记忆根目录");
        try
        {
            var 设置值 = Instance?.记忆目录;
            if (!string.IsNullOrWhiteSpace(设置值))
                return 设置值.Trim();
        }
        catch { }

        var 目录 = 默认记忆目录();
        尝试迁移旧数据(目录);
        return 目录;
    }

    /// <summary>
    /// 把旧位置（ACR 设置目录下的"记忆"）里的数据搬到新位置。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么需要迁移 ★
    ///
    ///    记忆原来存在 `Settings\Plugins\记忆`。改成"我的文档"之后，
    ///    **老用户的记忆会"凭空消失"** —— 文件还在，但程序不看那里了，
    ///    用户会觉得"升级把数据弄丢了"。
    ///
    ///    ⚠️ 用**复制**而不是移动：
    ///       万一新逻辑有问题，旧位置的原件还在，可以人工找回来。
    ///       搬完不删原件 = 多花一点磁盘，换一次可回退的机会，值。
    ///
    ///  ⚠️ 只在**新位置还没有数据时**才搬 ——
    ///     否则每次启动都覆盖用户在新位置积累的东西。
    ///
    ///  ⚠️ 全程 try/catch：迁移失败绝不能影响 ACR 启动。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static void 尝试迁移旧数据(string 新目录)
    {
        using var _深度 = HealerACR.Common.调用深度.进("AiSettings.尝试迁移旧数据");
        if (_已尝试迁移) return;
        _已尝试迁移 = true;   // 一个进程只试一次，别每帧都查磁盘

        try
        {
            var 旧目录 = Path.Combine(_设置目录 ?? "", "记忆");
            if (string.IsNullOrWhiteSpace(_设置目录) || !Directory.Exists(旧目录)) return;

            // 新位置已经有东西 → 不搬（保住用户在新位置的积累）
            if (Directory.Exists(新目录) &&
                Directory.EnumerateFileSystemEntries(新目录).Any())
            {
                LogHelper.Info($"[BlueWhale.AI] 记忆目录：{新目录}（新位置已有数据，不迁移）");
                return;
            }

            Directory.CreateDirectory(新目录);

            var 搬了几份 = 0;
            foreach (var 文件 in Directory.GetFiles(旧目录))
            {
                try
                {
                    var 目标 = Path.Combine(新目录, Path.GetFileName(文件));
                    // 不覆盖已有文件
                    if (!File.Exists(目标)) { File.Copy(文件, 目标); 搬了几份++; }
                }
                catch { }
            }

            if (搬了几份 > 0)
            {
                LogHelper.Info(
                    $"[BlueWhale.AI] 已从旧位置迁移 {搬了几份} 个记忆文件：" +
                    $"{旧目录} -> {新目录}（旧文件保留，可随时回退）");
                屏幕提示.成功($"记忆已迁移到：{新目录}", "mem-moved");
            }
        }
        catch { }
    }

    private static bool _已尝试迁移;

    /// <summary>
    /// 调试模式 —— 启用后 AI 的关键日志**直接显示在游戏里**
    /// （走屏幕横幅，和"AI 初始化中"同一个通道）。
    ///
    /// 用途：调提示词的时候不用开日志文件翻来翻去。
    /// 关掉它日志照样写文件，只是不上屏。
    /// </summary>
    public bool 调试模式 = false;

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

    /// <summary>
    /// **"正在读设置"的标记** —— 防自我递归（见 `Instance` 的说明）。
    ///
    /// [!] 为什么必须有它（**崩溃转储实证**）：
    ///     `AppData\Local\CrashDumps\ffxiv_dx11.exe.*.dmp` 的托管栈：
    ///         BlueWhale.dll!BlueWhale.AutoHealerACR.AiSettings.get_Instance() + 46
    ///         （下面三个地址在**无限循环**，每轮 0x190 字节，一路压到栈耗尽）
    ///     Windows 事件日志：**异常代码 `0xc00000fd`** = **STACK_OVERFLOW**
    ///     （**不是**以前那种读已释放对象的访问违例 `0xc0000005`）。
    ///     现象正是用户报的「开战闪退」，而更早几次是 AppHang（卡死）——
    ///     与"递归越来越深、先卡后崩"完全吻合。
    /// </summary>
    private static bool _正在读;

    public static AiSettings Instance
    {
        get
        {
            if (_实例 != null) return _实例;

            // ══════════════════════════════════════════════════════════════
            //  ★★★ **重入保护** ★★★
            //
            //  [!] 为什么（转储实证：栈溢出，栈顶就是本方法）：
            //      本方法内部要读设置文件、反序列化、打日志。
            //      只要这些步骤里**任何一步**间接触发"再读一次 Instance"，
            //      就会**无限递归** ⇒ 栈耗尽 ⇒ `0xc00000fd` ⇒ 进程直接没。
            //      （**这类崩溃 `try/catch` 也兜不住** —— 栈溢出不可捕获。）
            //
            //  [!] 所以：**"正在读"期间再进来，直接返回默认值**，不递归。
            //      降级成"这一次读到的是默认设置" —— 比整个游戏崩掉好得多。
            //
            //  [!] ⚠️ **不要**在 `读一次()` 里加任何会读 `Instance` 的东西
            //      —— 那正是递归的来源。这条约束比"能少写几行"重要。
            // ══════════════════════════════════════════════════════════════
            if (_正在读) return new AiSettings();

            try
            {
                _正在读 = true;
                return 读一次();
            }
            finally
            {
                // ★ **必须复位** —— 否则一次异常之后就永远读不到真实设置了
                _正在读 = false;
            }
        }
    }

    /// <summary>
    /// **真正读一次设置**（`Instance` 的实现体）。
    ///
    /// [!] 为什么单独抽成一个方法：`try/finally` **不能直接写在属性 getter 里**
    ///     （`get { }` 里只能有 `try/catch`）。
    ///     而复位 `_正在读` **必须**走 `finally`（异常路径也要复位）。
    ///
    /// [!] ⚠️ 本方法内**不许**读 `Instance`（会递归，见上面）。
    /// </summary>
    private static AiSettings 读一次()
    {
        using var _深度 = HealerACR.Common.调用深度.进("AiSettings.读一次");
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

        return _实例!;
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
            if (CharacterExt.我在战斗()) return;

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
            if (CharacterExt.我在战斗())
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
            //    现象是"设置没保存"，实际是写文件失败却没人知道。
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
        using var _深度 = HealerACR.Common.调用深度.进("AiSettings.初始化");
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
        using var _深度 = HealerACR.Common.调用深度.进("AiSettings.设置路径");
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
