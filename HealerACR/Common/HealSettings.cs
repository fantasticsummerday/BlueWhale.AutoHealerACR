using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AEAssist.CombatRoutine.View.JobView;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// 四个奶妈共用的设置。每个职业各存一份 json
/// （奶妈设置_WhiteMage.json / _Scholar.json / ...），切职业不会互相覆盖阈值。
///
/// 序列化用 System.Text.Json，注意开了 IncludeFields —— 下面这些都是字段。
/// </summary>
public class HealSettings
{

    /// <summary>
    /// **实时调试窗** —— 独立浮窗，显示每帧实际采集到的数据。
    ///
    /// [!] 为什么做成设置项而不是内存变量：
    ///      调试往往要跨几次上线下线（复现一个问题要打几把），
    ///      每次都要重新打开会很烦。
    ///
    /// [!] 它**只读** —— 不改任何战斗状态，可以放心一直开着。
    /// </summary>
    public bool 启用调试窗 = false;
    public static HealSettings Instance { get; private set; } = new();

    private static string _filePath = string.Empty;

    /// <summary>
    /// 设置 json 的完整路径。**给"反推目录"用**（时间轴探测）。
    ///
    /// ⚠️ 它的价值在于：这是 AEAssist **实际传给我们**的路径，
    ///    一定有效 —— 不像 `Assembly.Location` 在 Dalamud 插件里是空的。
    /// </summary>
    public static string 当前文件路径 => _filePath;

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        WriteIndented = true,
        IncludeFields = true,
    };

    /// <summary>由 Entry 的 Build(settingFolder) 调用一次</summary>
    public static void Build(string settingFolder, string jobName)
    {
        _filePath = Path.Combine(settingFolder, $"奶妈设置_{jobName}.json");

        // ★ 读共享的"时间轴目录"设置 ★
        //   ⚠️ 必须在 _filePath 赋值**之后**调 —— 它要靠这个路径反推共享文件位置
        //      （见 时间轴设置路径 的说明）。
        读时间轴目录();

        if (!File.Exists(_filePath))
        {
            Instance = new HealSettings();
            Instance.Save();
            return;
        }

        try
        {
            var text = File.ReadAllText(_filePath);
            Instance = JsonSerializer.Deserialize<HealSettings>(text, JsonOpt) ?? new HealSettings();
        }
        catch (Exception e)
        {
            Instance = new HealSettings();
            LogHelper.Error($"[HealerACR] 设置读取失败，已重置默认值: {e}");
        }
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(_filePath)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(this, JsonOpt));
        }
        catch (Exception e)
        {
            LogHelper.Error($"[HealerACR] 设置保存失败: {e}");
        }
    }

    // ================== 治疗 ==================

    /// <summary>总开关：关了之后只输出不奶人（低压本想自己手动奶时用）</summary>
    public bool 奶人 = true;

    /// <summary>低于这条血线 → 交能力技大加（天赐 / 深谋 / 先天禀赋 / 白牛…）</summary>
    [System.Text.Json.Serialization.JsonPropertyName("紧急单奶阈值")]
    public float 紧急单奶阈值_基础 = 0.30f;   // 更晚才动用紧急资源，平时靠普通治疗

    /// <summary>
    /// 实际生效的紧急单奶阈值（**经过钩子**）。
    ///
    /// [!] 原来它是**裸字段**，AI 调不了 —— 而它是"什么时候动用救命资源"的开关，
    ///     恰恰是最该让 AI 随局面调整的一个（用户目标：「随时调整策略，各类阈值」）。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float 紧急单奶阈值
    {
        get => 阈值钩子.应用(紧急单奶阈值_基础, 可调参数.紧急单奶阈值);
        set => 紧急单奶阈值_基础 = value;
    }

    /// <summary>低于这条血线 → 用 GCD 单体治疗</summary>
    /// <summary>单条治疗阈值的基础值（用户设置的原值，存 json）</summary>
    [System.Text.Json.Serialization.JsonPropertyName("单体治疗阈值")]
    public float 单体治疗阈值_基础 = 0.52f;
    // ⚠️ 默认值参考同类 ACR 的 Scholar_SingleGCDHeal / Scholar_Lustrate：
    //    它的 Check 常量里是 `50` —— **血量 50% 才治**。
    //    我原来是 0.65（掉到 65% 就开始读条），明显保守得多，
    //    在不需要治疗的场合会抢 GCD（偏保守）。
    //    取 0.52 是留 2% 余量，避免卡在 50% 边界反复触发。

    /// <summary>
    /// 实际生效的单体治疗阈值 = 基础值 经过 阈值钩子 调整。
    /// 不挂钩子时与原值完全相同。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float 单体治疗阈值
    {
        get => 阈值钩子.应用(单体治疗阈值_基础, 可调参数.单体治疗阈值);
        set => 单体治疗阈值_基础 = value;
    }

    /// <summary>群体治疗血线</summary>
    /// <summary>群体治疗阈值的基础值（用户设置的原值，存 json）</summary>
    [System.Text.Json.Serialization.JsonPropertyName("群体治疗阈值")]
    public float 群体治疗阈值_基础 = 0.62f;
    // 群奶阈值同步下调：原来 0.70 意味着"平均掉 30% 就交群奶"，
    // 实际战场上这个血线还很安全，交群奶属于浪费。

    /// <summary>实际生效的群体治疗阈值（经过钩子调整）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float 群体治疗阈值
    {
        get => 阈值钩子.应用(群体治疗阈值_基础, 可调参数.群体治疗阈值);
        set => 群体治疗阈值_基础 = value;
    }

    /// <summary>
    /// **长 CD 大招专用的血线** —— 秘策 / 炽天召唤 / 炽天附体 / 神速咏唱这类
    /// **180 秒级**的能力技用它判断"该不该现在交"。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  [!] 为什么要和 `群体治疗阈值` **分开**：
    ///
    ///      那三个大招原来用的是**普通群疗同一个阈值**（`群体治疗阈值`）：
    ///          var 掉血多 = 低于阈值人数(群体治疗阈值) >= 群奶最少人数;
    ///          var 要来了 = 未来有减伤(3.0) || 即将来大伤害();
    ///          if (!掉血多 && !要来了) return -1;      // 满足任一就交
    ///      ==> **只是"3 个人掉到 60%"也会把 180 秒大招交掉** ——
    ///          而那点血量差普通群疗 GCD 就补上了。
    ///
    ///  [!] 为什么做成"独立参数"而不是直接把阈值调高：
    ///      · 直接调高会**连带改变普通群疗**的行为（那是另一个决策，不该一起动）
    ///      · 做成参数之后**默认偏移为 0 = 和原来完全一致**（纯增量，不改变现有行为）
    ///      · 而 AI 可以按局面把它抬高/压低 —— 正是用户要的
    ///        「AI 随时调整策略（各类阈值）…资源最大化利用」
    ///
    ///  [!] 配置值**复用** `群体治疗阈值_基础` ——
    ///      用户界面不需要多一个滑条（"大招比群疗晚多少"由 AI 按局面决定）。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float 大招血线
        => 阈值钩子.应用(群体治疗阈值_基础, 可调参数.大招血线);

    /// <summary>低于群奶血线的人 ≥ 这个数，才值得群奶</summary>
    public int 群奶最少人数 = 2;

    public bool 单体治疗 = true;
    public bool 群体治疗 = true;

    // ================== 保命 ==================

    public bool 复活 = true;

    /// <summary>没即刻时是否也硬读条拉人（日随建议关：战斗里读 8 秒很危险）</summary>
    public bool 允许硬读复活 = false;

    public bool 驱散 = true;

    public bool 醒梦 = true;
    public int 醒梦蓝量阈值 = 6000;

    // ================== 输出 ==================

    public bool 输出 = true;
    public bool AOE = true;
    public bool 挂Dot = true;

    /// <summary>目标血量低于这个比例就不补 DoT 了（小怪快死时省 GCD）</summary>
    public float 不挂Dot血线 = 0.03f;

    /// <summary>
    /// **DoT 血量倍数门槛**：怪的 MaxHp 不到「队伍最大血量 × 这个倍数」
    /// 就不值得上 DoT（0 = 关掉这条判据）。
    ///
    /// ══════════════════════════════════════════════════════════════
    ///  ★ 为什么需要它（与对照实现一致的 ShouldSkipDotByHp）★
    ///
    ///    DoT 是 **30 秒**的持续伤害，交出去的是一个 GCD。
    ///    如果怪 **5 秒就死**，那 25 秒的伤害全浪费 —— 这一发 GCD
    ///    本来可以打一个完整的直接伤害。
    ///
    ///    所以我们原来"给一波小怪挨个上毒"是**纯亏本**：
    ///    小怪无论血线多少都不该上 DoT。
    ///
    ///  ── 参考实现的判据（IL 直证）──
    ///      · 单体 DoT：MaxHp <= 我方 MaxHp × **12** → 跳过
    ///      · 群 DoT  ：MaxHp <= 我方 MaxHp × **3**  → 跳过
    ///    我们只有一个 DoT 位，取**单体那个（12）**——
    ///    群 DoT 的 3 更宽松，用 12 是更保守的一侧。
    ///
    ///  ⚠️ **分母改了**：参考实现用"**我的** MaxHp"，
    ///     我们改成"**队伍最大** MaxHp"。
    ///     原因：我的 MaxHp 随等级变化极大（Lv50 学者 ~1.5 万 / Lv90 ~6 万），
    ///     而怪的 MaxHp 也随等级变化，两边**不同步** ——
    ///     按对照实现会在 Lv50 把 1.8 万血以上的怪全跳过（等于关掉 DoT）。
    ///     用队伍最大血量做分母，**语义不变但和等级脱钩**。
    ///
    ///  ⚠️ 调这个值的方向：
    ///     · 调**小** → 更容易上 DoT（小怪也上，亏 GCD）
    ///     · 调**大** → 更难上 DoT（只有精英/Boss 上）
    ///     · 设成 **0** → 完全关掉这条判据（回到旧行为）
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    public float Dot血量倍数 = 12f;

    /// <summary>DoT 的持续时间（秒）。用来在拿不到 DoT buff ID 时按时间兜底</summary>
    public float Dot持续时间 = 30f;

    /// <summary>
    /// 木桩环境下打最优输出（需求 5）。
    /// 开着的时候，只要当前选中目标是训练木桩，治疗/复活/驱散/减伤全部让路，
    /// 输出技能也不再"攒资源"，按各职业的最优循环打满。
    /// </summary>
    public bool 木桩优先输出 = true;

    // ================== 减伤 ==================

    /// <summary>
    /// 团队/个人减伤能力技是否自动放。
    /// 0.1.0~0.1.2 这里是默认关的 —— 结果就是节制/野战阵/中间学派/坚角清汁
    /// 一个都不会触发。现在默认打开（日随里 CD 都很长，卡 CD 放不会浪费）。
    /// </summary>
    public bool 自动减伤 = true;

    /// <summary>启用技能效果确认（hook ActionEffect，默认关，风险高）</summary>
    public bool 启用效果确认 = false;

    /// <summary>时间轴预报"马上要来大伤害"时，要不要把治疗资源攥住别乱花</summary>
    public bool 时间轴攒资源 = true;

    // ================== 时间轴（cactbot） ==================

    /// <summary>是否启用 Timelines 目录里的 cactbot 时间轴</summary>
    public bool 启用时间轴 = true;

    /// <summary>提前多少秒发出"该减伤了"的信号（时间轴里的技能时间点之前）</summary>
    public float 时间轴提前秒 = 1.5f;

    /// <summary>手工补的"要减伤"技能 ID，十进制，逗号/空格分隔</summary>
    public string 时间轴额外技能Id = "";

    // ================== 职业细化参数（各职业的「职业」页里调） ==================

    /// <summary>白魔：队伍血量低于这个值才动百合，否则攒着等更急的时候</summary>
    public float 百合使用血线 = 0.50f;

    /// <summary>白魔：苦难之心（血百合满了）是否自动打出去</summary>
    public bool 用苦难之心 = true;

    /// <summary>
    /// 学者：妖精契约（连线）的触发血线（坦克低于这个才挂）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 默认值从 0.80 改为 0.60 —— **对齐两套参考实现** ★
    ///
    ///  [!] IL 实证：
    ///        shiyuvi `ScholarSettings::Aetherpact = 0.6f`
    ///        youshu  `ScholarSettingsData::链子阈值 = 55`（整数 = 55%）
    ///      ==> 都是 0.55~0.60，而原来的 0.80 明显偏高
    ///          （0.80 意味着"坦克掉 20% 就挂"，等于几乎常驻）。
    ///
    ///  [!] 而且这个设置**原来根本没人读**（我逐处搜过全仓库）——
    ///      现在 `SCH_Aetherpact.Check()` 读它了，所以默认值才有意义。
    ///
    ///  ⚠️ 想改回去：设置页滑条（0.30~1.00）或 AI 的「妖精契约血线」参数。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("妖精契约血线")]
    public float 妖精契约血线_基础 = 0.60f;

    /// <summary>实际生效的妖精契约血线（经过钩子，AI 可调）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float 妖精契约血线
    {
        get => 阈值钩子.应用(妖精契约血线_基础, 可调参数.妖精契约血线);
        set => 妖精契约血线_基础 = value;
    }

    /// <summary>学者：以太低于这个数就不放"能力技群奶"，退回 GCD 群奶</summary>
    public int 以太保留数 = 1;

    /// <summary>占星：出卡优先给近战（关掉则优先远程）</summary>
    public bool 出卡优先近战 = true;

    /// <summary>占星：地星提前多少秒铺（地星 10 秒后自动炸）</summary>
    public float 地星提前秒 = 10f;

    /// <summary>贤者：蛇胆低于这个数就不放"能力技群奶"</summary>
    public int 蛇胆保留数 = 1;

    /// <summary>贤者：毒刺攒到几个就泄掉（避免溢出浪费）</summary>
    public int 箭毒泄刺阈值 = 2;

    // ================== 时间轴目录（共享，不是每职业一份）==================

    /// <summary>
    /// cactbot 时间轴目录。**留空 = 自动探测**。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么需要手动指定 ★
    ///
    ///    时间轴原来靠"dll 旁边的 Timelines"找，而那条**失效了**：
    ///    Dalamud 从内存加载 ACR，`Assembly.Location` **是空的** ——
    ///    于是最可靠的候选直接落空，只能靠猜目录。
    ///
    ///    而猜的那些也都不对，因为**目录名和程序集名不一致**：
    ///      · ACR 目录叫 `BlueWhale`（这是 dll 名）
    ///      · 但 `AuthorName` 是"小鲸鱼统治世界"（AEAssist 拿它当设置目录名）
    ///    代码按程序集名拼路径，自然找不到。
    ///
    ///  ── 所以给一个**显式指定**的入口 ──
    ///
    ///    这是这个问题的**根治手段**：不再猜，让用户直接指定。
    ///    自动探测保留作为兜底（万一以后 `Assembly.Location` 可用了）。
    ///
    ///  ⚠️ 存**共享文件**（AEAssist 根目录下），不是每职业 json：
    ///     时间轴跟职业无关，四个奶妈应该共用一份设置 ——
    ///     存进职业 json 的话用户得**设置四次**，而且换职业会发现"又没生效"。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static string 时间轴目录 { get; private set; } = "";

    /// <summary>共享设置文件名（放在 AEAssist 根目录，跨职业共用）</summary>
    private const string 时间轴设置文件 = "小鲸鱼_时间轴目录.txt";

    /// <summary>
    /// 共享设置文件的完整路径。
    ///
    /// 优先放 AEAssist 根目录（= 各 ACR 的上一级），
    /// 这样**重装 ACR 也不会丢**；拿不到就退回设置目录。
    /// </summary>
    private static string 时间轴设置路径()
    {
        try
        {
            // _filePath 形如 <根>\Settings\Plugins\<作者>\奶妈设置_WhiteMage.json
            // 往上四层就是 AEAssist 根（Settings\Plugins\<作者> -> Plugins -> Settings -> 根）
            var 作者目录 = Path.GetDirectoryName(_filePath);
            var 根 = 作者目录;
            for (var i = 0; i < 3 && !string.IsNullOrEmpty(根); i++)
                根 = Path.GetDirectoryName(根);

            if (!string.IsNullOrEmpty(根) && Directory.Exists(根))
                return Path.Combine(根, 时间轴设置文件);
        }
        catch { }

        try
        {
            var 同目录 = Path.GetDirectoryName(_filePath) ?? ".";
            return Path.Combine(同目录, 时间轴设置文件);
        }
        catch { return 时间轴设置文件; }
    }

    /// <summary>从磁盘读共享的时间轴目录设置（Build 时调一次）</summary>
    public static void 读时间轴目录()
    {
        try
        {
            var p = 时间轴设置路径();
            if (!File.Exists(p)) { 时间轴目录 = ""; return; }

            var 值 = File.ReadAllText(p, System.Text.Encoding.UTF8).Trim();
            时间轴目录 = 值;

            if (!string.IsNullOrEmpty(值))
                LogHelper.Info($"[HealerACR] 时间轴目录（用户指定）：{值}");
        }
        catch (Exception e)
        {
            LogHelper.Error("[HealerACR] 读时间轴目录设置失败：" + e.Message);
        }
    }

    /// <summary>写共享的时间轴目录设置（留空 = 恢复自动探测）</summary>
    public static void 写时间轴目录(string 值)
    {
        try
        {
            时间轴目录 = (值 ?? "").Trim();

            var p = 时间轴设置路径();

            // ⚠️ **必须自己建目录** ——
            //    这个文件在 `_filePath` 的**同一个目录**里，
            //    而那个目录是框架给的 `settingFolder`。
            //
            //    正常流程下 `Save()` 会先建好它，但这里有两条不保证的路径：
            //      · `Save()` 从没被调用过（新装 + 用户没改过任何设置）
            //      · 用户在设置里**只改了时间轴目录**就关掉
            //    一旦目录不存在，`File.WriteAllText` 直接抛
            //    `DirectoryNotFoundException`，被下面的 catch 吃掉 ——
            //    现象是"填了路径、保存了，但下次读还是空"。
            //
            //    而且这条是**首次运行**最容易踩的：新环境里
            //    `D:\FF14\Settings\Plugins\<作者名>\` 可能还不存在。
            var 目录 = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(目录)) Directory.CreateDirectory(目录);

            File.WriteAllText(p, 时间轴目录, System.Text.Encoding.UTF8);

            LogHelper.Info(string.IsNullOrEmpty(时间轴目录)
                ? "[HealerACR] 时间轴目录已清空（恢复自动探测）"
                : $"[HealerACR] 时间轴目录已设为：{时间轴目录}（写入 {p}）");
        }
        catch (Exception e)
        {
            LogHelper.Error("[HealerACR] 写时间轴目录设置失败：" + e.Message);
        }
    }

    // ================== 占星卡牌 ==================

    /// <summary>
    /// 哪些牌算"近战卡"（给近战 DPS）。
    /// 按 CardType 的运行时名字匹配，不在这份名单里的视为远程卡。
    /// </summary>
    public string 近战卡关键词 = "Balance,Bole,Arrow,太阳,世界树,箭";

    // ================== ID 覆盖（自动解析失败时手工填） ==================

    /// <summary>
    /// 技能 ID 覆盖：键 = 技能英文名（跟各职业 SpellTable 里写的一致），值 = 游戏技能 ID。
    ///
    /// 平时不用填 —— 代码会先用 MemApiSpell.GetId(名字) 去游戏数据里查。
    /// 如果日志里出现"[HealerACR] 技能名解析失败：XXX"，
    /// 就用卫月插件 SeeSpell 查到 ID，在这里补一条。
    /// </summary>
    public Dictionary<string, uint> 技能Id覆盖 = new();

    /// <summary>
    /// 状态 ID 覆盖：键 = AuraIds.cs 里的中文键名（比如 "即刻"、"死斗"、"白魔Dot"），
    /// 值 = 游戏状态 ID。拿不准的 buff 用 SeeBuff 查。
    /// </summary>
    public Dictionary<string, uint> BuffId覆盖 = new();

    // ================== 其他 ==================

    public bool 复活喊话开关 = false;

    public string 复活喊话频道 = "/p ";
    public List<string> 复活喊话 = new() { "制作\"<t>\"成功！" };

    /// <summary>QT 面板的配色/位置保存</summary>
    public JobViewSave 职业视图保存 = new()
    {
        MainColor = new Vector4(40 / 255f, 173 / 255f, 70 / 255f, 0.8f),
    };

    /// <summary>
    /// 给 JobViewWindow 用的保存回调。
    /// ⚠️ 必须 JsonIgnore：它是委托，System.Text.Json 序列化不了，
    ///    不加的话每次 Save() 都会抛 NotSupportedException（0.1.0 就是这么炸的）。
    /// </summary>
    [JsonIgnore]
    public Action 保存回调 => Save;
}

/// <summary>
/// 阈值钩子 —— 外部可以挂一个"调整函数"来改写治疗阈值。
///
/// **设计要点：不挂钩子就是原值。**
/// 所以原版 HealerACR 完全不受影响；
/// BlueWhale 挂上它，AI 的"保守/激进"才有实际效果。
///
/// 用钩子而不是直接改字段，是因为：
///   · 直接改字段会被存进 json（把 AI 的临时判断变成永久设置）
///   · 钩子是每次读的时候现算，AI 一改倾向立刻生效、不落盘
/// </summary>
public static class 阈值钩子
{
    /// <summary>
    /// 治疗阈值调整函数（**通用**，所有阈值共用）。
    /// 输入原值，返回调整后的值。
    /// </summary>
    public static Func<float, float>? 治疗阈值调整;

    /// <summary>
    /// **具名参数调整** —— 让外部可以**分别**调不同阈值。
    ///
    /// ══════════════════════════════════════════════════════════════
    ///  [!] 为什么需要它（用户的目标）：
    ///      「AI 的目的就是**随时调整策略**（各类阈值，风格是激进还是保守等）优化」
    ///
    ///      而原来只有上面那**一个** `治疗阈值调整` ——
    ///      它"对任何传进来的值做同样变换"，**无法分别调**：
    ///        想让「群体阈值更保守、但紧急阈值更激进」做不到。
    ///
    ///  [!] 优先级：**具名 > 通用**。
    ///      具名没挂就退回通用，通用也没挂就是原值（原设计要点：不挂钩子=原值）。
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    private static readonly Dictionary<string, Func<float, float>> _具名 = new();

    /// <summary>挂/换一个具名参数的调整函数（传 null = 摘掉）。</summary>
    public static void 挂具名(string 参数, Func<float, float>? 调整)
    {
        try
        {
            if (调整 == null) _具名.Remove(参数);
            else _具名[参数] = 调整;
        }
        catch { }
    }

    /// <summary>按参数名应用调整。具名没挂 -> 通用 -> 原值。</summary>
    public static float 应用(float 原值, string? 参数 = null)
    {
        try
        {
            if (参数 != null && _具名.TryGetValue(参数, out var 具))
                return float.IsFinite(具(原值)) ? 具(原值) : 原值;
        }
        catch { }

        try { return 治疗阈值调整?.Invoke(原值) ?? 原值; }
        catch { return 原值; }   // 钩子里出任何问题都退回原值
    }

    /// <summary>卸载全部钩子（BlueWhale 退出/换职业时调）</summary>
    public static void 卸载()
    {
        治疗阈值调整 = null;
        try { _具名.Clear(); } catch { }
    }

    /// <summary>当前挂了几个具名参数（给设置界面/诊断看）</summary>
    public static int 具名数量
    {
        get { try { return _具名.Count; } catch { return 0; } }
    }
}

/// <summary>
/// **AI 可以调的策略参数名** —— 集中在这里，避免各处写裸字符串。
///
/// [!] 为什么用常量字符串而不是散落各处的字面量：
///     写错一个字的后果是"钩子静默不生效"（不报错、只是没作用），
///     那是这个项目里已经出现过多次的失效类型。集中定义至少能靠引用查。
/// </summary>
public static class 可调参数
{
    public const string 单体治疗阈值 = "单体治疗阈值";
    public const string 群体治疗阈值 = "群体治疗阈值";
    public const string 紧急单奶阈值 = "紧急单奶阈值";
    public const string 预铺血线 = "预铺血线";
    public const string 妖精契约血线 = "妖精契约血线";

    /// <summary>长 CD 大招的血线（秘策 / 炽天召唤 / 炽天附体）。默认偏移 0 = 与群体阈值一致。</summary>
    public const string 大招血线 = "大招血线";
    public const string 以太保留数 = "以太保留数";
    public const string 蛇胆保留数 = "蛇胆保留数";
}