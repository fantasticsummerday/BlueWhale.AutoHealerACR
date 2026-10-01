using System.Collections.Generic;

namespace HealerACR.Common;

/// <summary>
/// **治疗阈值表** —— 参考实现的阈值是**每个技能一个**，而我们原来只有"一个大阈值"。
///
/// ══════════════════════════════════════════════════════════════════════
///  [!] 为什么需要它（复刻的核心差异，IL 实证）
///
///      我们原来的结构：
///        `HealSettings.单体治疗阈值`（0.52）—— 所有单奶共用一个
///        `HealSettings.群体治疗阈值`（0.62）—— 所有群奶共用一个
///        `JobSpellTable.瞬发单奶血线`  —— 职业级一个裸 const
///
///      参考实现（两套都是）：
///        **每个技能一个独立阈值**。以学者为例（IL 实证）：
///          shiyuvi：Adloquium 0.4 / Lustrate 0.45 / Indomitability 0.7 /
///                   WhisperingDawn 0.7 / FeyBlessing 0.6 / Succor 0.65 /
///                   SummonSeraph 0.55 / Seraphism 0.4 / EmergencyTactics 0.35 /
///                   GCDShield 0.8 / GCDAOEHeal 0.5 / Aetherpact 0.6
///          youshu ：单盾 45 / 群盾 55 / 绿帽 60 / 活性法 45 / 回生法 35 /
///                   低语 70 / 不屈 70 / 祥光 70 / 慰藉 55 / 变身 40 /
///                   野战治疗阵 35 / 应急战术 35 / GCD群奶 50 / GCD单奶 45
///
///  [!] 为什么不能继续"一个大阈值 + 例外"
///      参考实现的差异是**结构性的**，不是"调一下数"：
///        · 同一个技能在两套实现里差 5~15 个点（不屈 70 vs 65）
///        · 组内差异很大（单盾 0.4 而罩子 0.35、GCD群盾 0.8）
///        · 一个大阈值下**没法表达"这个技能宁可早点交、那个宁可晚点"**
///      ==> 复刻这一层，就必须有"每技能一个数"的落点。
///
///  [!] 不硬编码在 resolver 里，而是集中成表 —— 三个理由：
///      ① 参考实现的这些数**在设置里可调**（有 UI 滑条），我们也该可调
///      ② AI 的"阈值调参"要能逐个改（用户明确要的特色）
///      ③ 一处能看全 → 对照参考实现时不用满仓库找
///
///  [!] 缺省行为：**表里没有该技能 → 回落到原来的统一阈值**。
///      这样逐职业铺开时，没改到的技能行为不变（可以一个一个来）。
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public static class 治疗阈值表
{
    /// <summary>
    /// 每个技能自己的血线。key = 技能 id。
    ///
    /// ⚠️ 数值来源**逐个标注**在该技能的注释里（IL 行号或"两套实现的取值"）。
    ///    没标注来源的数**不许进来** —— 这是本项目的铁律（数值必须有依据）。
    /// </summary>
    private static readonly Dictionary<uint, float> _表 = new();

    /// <summary>
    /// 静态构造 —— **保证表一定被载入**。
    /// （不依赖任何调用方记得先调 `载入默认()`；"必须记得初始化"的设计在本项目踩过坑。）
    /// </summary>
    static 治疗阈值表() { try { 载入默认(); } catch { } }

    /// <summary>表里的条目数（诊断用）</summary>
    public static int 条目数 { get { try { return _表.Count; } catch { return 0; } } }

    /// <summary>
    /// 查这个技能自己的血线。
    /// **查不到返回 -1**（调用方据此回落到统一阈值）——
    /// 不用 0 当"没有"，因为 0 是合法血线（"永不因血线触发"）。
    /// </summary>
    public static float 查(uint 技能Id)
    {
        if (技能Id == 0) return -1f;
        try { return _表.TryGetValue(技能Id, out var v) ? v : -1f; }
        catch { return -1f; }
    }

    /// <summary>
    /// 取"这个技能该用的血线"：表里有就用表里的，没有就用传入的统一阈值。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ **AI 的偏移在这里生效**（这是"保留 AI 调参特色"的关键接缝）★
    ///
    ///  [!] 为什么必须有这一段：
    ///      AI 原来只能调 `单体治疗阈值` / `群体治疗阈值` 这**几个大类**。
    ///      而参考实现的阈值是**每技能一个**，所以本表登记了 44 条。
    ///      ==>
    ///      一旦某个技能在本表里有值，**调用方就不再读那些大类阈值了**
    ///      ==> `AiThresholdAdapter` 的偏移**对它失效** ——
    ///          AI 说"这轮保守一点"，这个技能却纹丝不动。
    ///
    ///  [!] 所以：**表里的值也要过一遍 AI 的偏移**。
    ///      传 `AI偏移` 进来（由调用方从 `AiThresholdAdapter` 拿），
    ///      没传就按 0 算（单装 HealerACR 时 AI 层不存在，行为不变）。
    ///
    ///  [!] 为什么偏移**不缓存**：
    ///      `AiThresholdAdapter` 自己做了平滑（`平滑速率 0.7`），
    ///      每次调用拿到的是当前值 —— 缓存反而会让平滑失效。
    ///
    ///  [!] 上下界：夹在 `[0.05, 0.99]` ——
    ///        下界 0.05：不能因为偏移把阈值压到 0（那等于"永不触发"）
    ///        上界 0.99：不能压过 1.0（那等于"永远触发"）
    /// ══════════════════════════════════════════════════════════════════
    ///
    /// 调用方写法：
    ///     var 血线 = 治疗阈值表.取(技能id, HealSettings.Instance.单体治疗阈值);
    /// </summary>
    public static float 取(uint 技能Id, float 统一阈值)
    {
        var v = 查(技能Id);
        var 基 = v >= 0f ? v : 统一阈值;
        return 夹(基 + AI偏移);
    }

    /// <summary>
    /// **AI 的阈值偏移** —— 由 `BlueWhale` 侧每帧写入（本地不反向依赖 AI）。
    ///
    /// ⚠️ 单装 `HealerACR`（没有 BlueWhale）时它恒为 0，一切按表里的原值走。
    ///    这是开发约定 G（AI 是增强层，本地逻辑不该依赖 AI 存活）的落点。
    /// </summary>
    public static float AI偏移 { get; set; }

    /// <summary>把血线夹在合法区间 `[0.05, 0.99]`。</summary>
    private static float 夹(float v)
    {
        try { return Math.Clamp(v, 0.05f, 0.99f); }
        catch { return v; }
    }

    /// <summary>
    /// 显式登记一条（给将来的 UI / 配置读写用）。
    ///
    /// ⚠️ `技能Id == 0` 时**跳过**（0 是"查不到"的返回值，登记进去会污染整张表）——
    ///    但**照样计入 `_登记次数`**，这样自检能发现"有一条没登记进去"。
    /// </summary>
    public static void 设(uint 技能Id, float 血线)
    {
        _登记次数++;
        if (技能Id == 0) return;
        try { _表[技能Id] = 血线; } catch { }
    }

    /// <summary>
    /// 载入所有职业的默认值（幂等；重复调只会覆盖成同一批数）。
    ///
    /// ⚠️ **不写 `自检(预期条数)` 的硬编码数字** —— 那样每次加减一条都要改两处，
    ///    而"忘记改第二处"正是本项目反复出问题的方式。
    ///    改成**自己数一遍**：`设()` 每次被调都计数，载入完拿计数和实际条目数对。
    /// </summary>
    public static void 载入默认()
    {
        try
        {
            _表.Clear();
            _登记次数 = 0;
            载入学者();
            载入白魔();
            载入占星();
            载入贤者();
            自检(_登记次数);
        }
        catch { }
    }

    /// <summary>本次载入里 `设()` 被调用的次数（含被 0 跳过的）—— 给自检用。</summary>
    private static int _登记次数;

    /// <summary>
    /// 学者 —— 阈值来源：两套参考实现的 IL，**逐个标注**。
    ///
    /// shiyuvi = `.il/shiyuvi.il.txt` `ScholarSettings::.ctor`（L73875-73993）
    /// youshu  = `.il/youshu.il.txt` `ScholarSettingsData::.ctor`（L46734，整数=百分数）
    ///
    /// ⚠️ **取值原则**：两套不一致时取**两者之间**（都不偏），
    ///    并在注释里写清两个原值 —— 将来想改成某一边，改一个数就行。
    /// </summary>
    private static void 载入学者()
    {
        // ── 单体 ──
        // 单盾（鼓舞）: shiyuvi Adloquium 0.4 ｜ youshu 单盾 45
        设(SpellIds.取("鼓舞激励之策"), 0.42f);
        // 生命活性法: shiyuvi Lustrate 0.45 ｜ youshu 活性法 45  → 完全一致
        设(SpellIds.取("生命活性法"), 0.45f);
        // 绿帽（深谋远虑之策）: shiyuvi 无独立值 ｜ youshu 绿帽 60
        设(SpellIds.取("深谋远虑之策"), 0.60f);
        // 生命回生法: shiyuvi 无 ｜ youshu 回生法 35
        设(SpellIds.取("生命回生法"), 0.35f);
        // 以太契约（连线）: shiyuvi Aetherpact 0.6 ｜ youshu 链子 55 → 用设置项（已有滑条）
        //   这里不登记：它由 `HealSettings.妖精契约血线` 提供，见 SCH_Aetherpact。

        // ── 群体 ──
        // 不屈不挠之策: shiyuvi 0.7（预设按钮 0.65）｜ youshu 不屈 70
        设(SpellIds.取("不屈不挠之策"), 0.70f);
        // 仙光的低语: shiyuvi WhisperingDawn 0.7 ｜ youshu 低语 70  → 一致
        设(SpellIds.取("仙光的低语"), 0.70f);
        // 异想的祥光: shiyuvi FeyBlessing 0.6 ｜ youshu 祥光 70  → 分歧大，取中间
        设(SpellIds.取("异想的祥光"), 0.65f);
        // 慰藉: shiyuvi 无 ｜ youshu 慰藉 55
        设(SpellIds.取("慰藉"), 0.55f);
        // 炽天附体: shiyuvi Seraphism 0.4 ｜ youshu 变身 40  → 一致
        设(SpellIds.取("炽天附体"), 0.40f);
        // 炽天召唤: shiyuvi SummonSeraph 0.55 ｜ youshu 无
        设(SpellIds.取("炽天召唤"), 0.55f);
        // 野战治疗阵（罩子）: shiyuvi 无 ｜ youshu 野战治疗阵 35
        设(SpellIds.取("野战治疗阵"), 0.35f);
        // 应急战术: shiyuvi EmergencyTactics 0.35 ｜ youshu 应急战术 35 → 一致
        设(SpellIds.取("应急战术"), 0.35f);
        // GCD 群盾（士气高扬之策）: shiyuvi GCDShield 0.8 ｜ youshu 群盾 55
        //   ⚠️ 分歧最大的一处（0.8 vs 0.55）—— shiyuvi 的 0.8 是"应急群盾"档，
        //      youshu 的 55 是常规群盾档。取中间偏保守。
        设(SpellIds.取("士气高扬之策"), 0.65f);
        // GCD 群奶（士气高扬之策的纯奶形态）/ 预后 等由各职业表处理。
    }

    /// <summary>
    /// 白魔 —— 来源：**只有 youshu 有 PvE 白魔**
    /// （shiyuvi 的 `ACR3._0.WHMPVP` 是 PvP，技能 id 不同，不能对标）。
    ///
    /// youshu = `WhiteMageSettingsData::.ctor`，整数=百分数。
    /// </summary>
    private static void 载入白魔()
    {
        设(SpellIds.取("天赐祝福"), 0.20f);   // youshu 天赐阈值 20
        设(SpellIds.取("神名"),     0.75f);   // youshu 神名阈值 75
        设(SpellIds.取("神祝祷"),   0.70f);   // youshu 神祝祷阈值 70
        设(SpellIds.取("水流幕"),   0.70f);   // youshu 水流幕阈值 70（坦克另有 0.60 档）
        设(SpellIds.取("庇护所"),   0.80f);   // youshu 庇护所阈值 80
        设(SpellIds.取("全大赦"),   0.35f);   // youshu 全大赦阈值 35
        设(SpellIds.取("法令"),     0.70f);   // youshu 法令阈值 70
        设(SpellIds.取("安慰之心"), 0.40f);   // youshu 安慰之心阈值 40
        设(SpellIds.取("狂喜之心"), 0.75f);   // youshu 狂喜之心阈值 75
        设(SpellIds.取("医养"),     0.65f);   // youshu 医养阈值 65（注：该 resolver 实际施放 133）
        设(SpellIds.取("医治"),     0.45f);   // youshu 医治阈值 45
        设(SpellIds.取("愈疗"),     0.40f);   // youshu 愈疗阈值 40
        设(SpellIds.取("再生"),     0.40f);   // youshu GCD单奶阈值 40（再生用同一档）
    }

    /// <summary>
    /// 占星 —— 来源：youshu（`AstrologianSettingsData::.ctor`，整数=百分数）
    /// + shiyuvi（浮点，仅少数几项）。
    /// </summary>
    private static void 载入占星()
    {
        设(SpellIds.取("先天禀赋"), 0.30f);   // youshu 先天阈值 30
        设(SpellIds.取("擢升"),     0.75f);   // youshu 擢升阈值 75
        设(SpellIds.取("天星交错"), 0.70f);   // youshu 天星交错阈值 70
        设(SpellIds.取("星位合图"), 0.30f);   // youshu 星位合图阈值 30
        设(SpellIds.取("天宫图"),   0.75f);   // youshu 天宫图阈值 75（shiyuvi 0.5，分歧大）
        设(SpellIds.取("天星冲日"), 0.80f);   // youshu 天星冲日阈值 80
        设(SpellIds.取("中间学派"), 0.65f);   // youshu 中间学派阈值 65
        // ⚠️ 小奥秘卡的"贵妇"：参考实现 IL 用 **7445**，而我们 `SpellIds` 里是
        //    `王冠之贵妇 = 41504` / `王冠之领主 = 41505`。
        //    两者对不上 —— **可能是版本差异**（7444/7445 是旧 id）。
        //    ==> 登记用**我们自己的 id**（`SpellIds.取("王冠之贵妇")`），
        //        并在注释里留下这个分歧，供将来核对。
        设(SpellIds.取("王冠之贵妇"), 0.70f);  // youshu 贵妇卡阈值 70
        设(SpellIds.取("地星"),     0.30f);   // youshu 地星阈值 30
        // 星体爆轰：参考 IL 用 **8324**，而 `SpellIds` 里**没有**这个 id。
        //   ==> 不登记（登记会写进 key=0，等于没写）。
        //       贤者/占星重建时**必须先把它加进 SpellIds 并核对语义**。
        设(SpellIds.取("大宇宙"),   0.55f);   // shiyuvi GCD群奶治疗阈值 0.5 / youshu GCD群奶 55
    }

    /// <summary>
    /// 贤者 —— 来源：youshu（`SageSettingsData::.ctor`，整数=百分数）。
    /// shiyuvi 的 `SageSettings` **没有任何玩法阈值**（全硬编码在技能类里），所以贤者只有一套可对标。
    ///
    /// ⚠️ 两处**参考实现里是硬编码、不是设置项**的，我按原值登记并标注：
    ///    · `Ability.拯救`（24294）：目标血线 **70.0f**（硬字面量，不是 `输血阈值`）
    ///    · `GCD.单盾` 的流血档：`HP < 0.85`
    /// </summary>
    private static void 载入贤者()
    {
        设(SpellIds.取("自生"),     0.80f);   // youshu 自生阈值 80
        设(SpellIds.取("寄生清汁"), 0.75f);   // youshu 寄生阈值 75
        设(SpellIds.取("智慧之爱"), 0.70f);   // youshu 智慧之爱阈值 70
        设(SpellIds.取("输血"),     0.70f);   // youshu 输血阈值 70
        设(SpellIds.取("白牛清汁"), 0.65f);   // youshu 白牛阈值 65
        设(SpellIds.取("灵橡清汁"), 0.50f);   // youshu 灵橡阈值 50
        设(SpellIds.取("魂灵风息"), 0.60f);   // youshu 魂灵风息阈值 60
        设(SpellIds.取("拯救"),     0.70f);   // youshu `Ability.拯救` 硬编码 70.0f
    }

    /// <summary>
    /// **漏登记自检**（诊断用）。
    ///
    /// ⚠️ 为什么要它：`设(SpellIds.取("xxx"), v)` 里如果 `"xxx"` 不在 `SpellIds`，
    ///    `取` 返回 0，而 `设` 对 0 直接跳过 ==>
    ///    **那一条会静默不登记** —— 看起来"填了"其实没有。
    ///    （本表第一版就踩过：`贵妇卡` / `星体爆轰` 两个名字不在 `SpellIds` 里。）
    ///
    /// ==> 载入完自检一遍，条目数对不上就写日志。
    ///     不影响运行，只是让"漏了"这件事**看得见**。
    /// </summary>
    private static void 自检(int 预期条数)
    {
        try
        {
            if (_表.Count != 预期条数)
                LogHelper.Info($"[治疗阈值表] 条目数 {_表.Count} != 预期 {预期条数}" +
                               "（多半是某个技能名不在 SpellIds 里，被 `设` 当 0 跳过了）");
        }
        catch { }
    }
}
