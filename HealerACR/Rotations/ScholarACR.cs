using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;
using HealerACR.Resolvers;
using HealerACR.Timeline;

namespace HealerACR.Rotations;

/// <summary>学者技能表。技能 ID 走 SpellIds 内置表（中文名）。</summary>
public class SCHSpellTable : JobSpellTable
{
    public override Jobs Job => Jobs.Scholar;
    public override string 职业名 => "学者";

    /// <summary>
    /// 基础输出链 —— ⚠️ **必须按等级从高到低排**。
    ///
    /// `取已解锁` 是"从前往后挑第一个已解锁的" ——
    /// **顺序就是优先级**，等级高的必须排在前面。
    ///
    /// ⚠️ 这里踩过坑（审计发现）：原来是
    ///    `极炎法(82) / 魔炎法(64) / 死炎法(72) / 气炎法(54) / 毁坏(38) / 毁灭(1)`
    ///    —— **魔炎法(64) 排在了 死炎法(72) 前面**，
    ///    于是 **72~81 级取到的是魔炎法**（死炎法是死代码），
    ///    整段等级都在打低一级的填充技。
    ///
    /// 等级（官方 Action 表）：极炎法 82 / 死炎法 72 / 魔炎法 64 /
    ///                        气炎法 54 / 毁坏 38 / 毁灭 1
    /// </summary>
    public override uint 基础输出 => SpellUtil.取已解锁(
        SpellIds.取("极炎法"), SpellIds.取("死炎法"), SpellIds.取("魔炎法"),
        SpellIds.取("气炎法"), SpellIds.取("毁坏"), SpellIds.取("毁灭"));

    /// <summary>药尾声补刀用的瞬发填充技 —— 学者：毁坏（17870）—— 本来就是瞬发，但即刻下能用高一级的极炎法。</summary>
    public override uint 药尾声填充技 => SpellIds.取("毁坏");
    public override uint 群体输出 => SpellUtil.取已解锁(
        SpellIds.取("裂阵法"), SpellIds.取("破阵法"));
    // 注意：埋伏之毒**不在这里** —— 它需要"埋伏之毒预备"buff，是独立技能，
    // 由 SCH_BanefulImpaction 处理。当成普通 DoT 塞进链条会永远打不出来。
    public override uint Dot技能 => SpellUtil.取已解锁(
        SpellIds.取("蛊毒法"), SpellIds.取("猛毒菌"), SpellIds.取("毒菌"));
    public override uint DotBuff => AuraIds.学者Dot;
    // 蛊毒法有三档 id（1895 / 2039 / 3089）
    public override uint[] 所有DotBuff => new[]
    {
        AuraIds.学者Dot, AuraIds.学者DotAlt, AuraIds.学者DotAlt2, AuraIds.学者Dot2, AuraIds.学者Dot1
    };

    public override uint 单体治疗GCD => SpellUtil.取已解锁(SpellIds.取("鼓舞激励之策"), SpellIds.取("医术"));
    public override uint 群体治疗GCD => SpellIds.取("士气高扬之策");

    // ⚠️ **绿帽不在这里** —— 它原先是 `取已解锁(深谋远虑之策, 生命活性法)`，
    //    被当成"紧急单奶"，而急救判据是血 < 30%。
    //    日志实证：坦克一直在 80%，于是**绿帽一次都没放过**
    //    （那一场 61 次单盾 / 0 次绿帽）。
    //    绿帽是 45 秒 CD 的**预铺**技能（挂上后目标掉到阈值自动触发治疗），
    //    所以归到 `预铺单奶能力技`。详见 JobSpellTable 那一栏的说明。
    public override uint 紧急单奶 => SpellIds.取("生命活性法");

    /// <summary>绿帽：45 秒 CD 的预铺 —— 给坦克挂上，掉血时自动触发治疗。</summary>
    public override uint 预铺单奶能力技 => SpellIds.取("深谋远虑之策");

    /// <summary>生命活性法：不读条、能移动中用的单体治疗。</summary>
    public override uint 瞬发单奶能力技 => SpellIds.取("生命活性法");

    /// <summary>
    /// 活性法阈值 **45%**（参考实现 IL 直读的 `活性法阈值 = 45`）。
    ///
    /// ⚠️ 比绿帽的 60% **低** —— 这个分层就是"能力技优先"的实现：
    ///      绿帽   60%  ← 45 秒 CD，预铺
    ///      活性法 45%  ← 应急瞬发
    ///      单盾   45%  ← GCD 读条（等量时能力技排前面，所以它先出）
    /// </summary>
    public override float 瞬发单奶血线 => 0.45f;

    public override uint 群体治疗能力技 => SpellIds.取("不屈不挠之策");
    public override uint 单体盾 => SpellIds.取("鼓舞激励之策");
    public override uint 团队减伤 => SpellIds.取("野战治疗阵");

    public override uint 复活 => SpellIds.取("复生");
    public override uint 驱散 => SpellsDefine.Esuna;
    public override uint 醒梦 => SpellsDefine.LucidDreaming;

    // 需求 3：以太就是学者的"群奶能力技资源"，没以太就退回 GCD 群奶
    public override bool 治疗资源充足 => JobApiHelper.以太 >= HealSettings.Instance.以太保留数;

    // 需求 4：脱战把以太补上
    public override uint[] 脱战准备技能 => new[] { SpellIds.取("以太超流") };

    // 移动填充：毁坏（Ruin II）是瞬发的，边走边打不掉输出
    public override uint 移动填充技 => SpellIds.取("毁坏");

    /// <summary>
    /// **贴身时用破阵法代替毁坏** ——
    ///   破阵法 600 威力 / 瞬发 / 以自己为中心 5 米
    ///   毁坏   500 威力 / 瞬发
    /// ⇒ 站在近战位时破阵法**高 100 威力（+20%）**。
    ///
    /// ⚠️ 82 级后是 `裂阵法`（25866，威力 700）——
    ///    和 `群体输出` 一样走 `SpellUtil.当前形态()` 自动升级，
    ///    所以这里填**低级那个**即可。
    /// </summary>
    public override uint 近战填充技 => SpellIds.取("破阵法");

    /// <summary>
    /// 贴身距离门槛 = **5 米**。
    ///
    /// ⚠️ 表里的默认值是 3，那个**偏小** —— 查官方技能表
    /// （`dump_actions.tsv`，逐字）：
    ///     16539 破阵法  学者 46  CastType=2  Range=0  **EffectRange=5**
    ///     25866 裂阵法  学者 82  CastType=2  Range=0  **EffectRange=5**
    ///     CastType=2 = **以自己为中心**的范围技
    /// ⇒ 半径就是 5 米。设成 3 的话，**3~5 米这一段会被判"够不着"**，
    ///   白白打了低一级的填充技（毁坏 500 威力 vs 破阵法 600）。
    ///
    /// 参考实现在同一处用的也是 5（IL 里的 `Ldc_r4 5`）。
    /// </summary>
    public override float 近战填充距离 => 5f;

    // 能量吸收是"以太换输出"，卡 CD 打（跟法令一个道理）
    // 能量吸收是拿以太换输出。
    // ⚠️ 关键：以太**读不到**时不能判死 —— JobApiHelper 读失败会返回 0，
    //    如果直接写 "以太 >= 1"，读不到就等于"永远没以太"，永远不打能量吸收。
    /// <summary>
    /// 输出能力技（能量吸收）—— **默认留着，只在两种时机卸**。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 机制（替换原来的"有豆子就卸"）★
    ///
    ///    **默认留**，只有满足下面任一条才打能量吸收：
    ///
    ///    ① **开爆发** —— 爆发窗口里豆子就该换输出，不受保留数限制
    ///    ② **以太超流快转好了**，且**无治疗压力** ——
    ///       在它转好之前把豆子清空
    ///
    ///  ── 为什么②是对的（这段是理解的关键）──
    ///
    ///    以太超流的机制是**补满到 3 颗**，**不是"加 3 颗"**。
    ///    所以如果它转好时你手里还攥着豆子，**那些豆子直接白溢出**。
    ///
    ///    ⇒ 在它转好前把手里清空 = **白赚输出**，没有任何代价。
    ///    ⇒ 反过来，转好还早的时候卸豆就是纯亏 ——
    ///       因为豆子留在手里既能应急、又能在它补满前一直存着。
    ///
    ///    这就是"默认留"的全部道理：**留到快溢出为止**。
    ///
    ///  ── 旧逻辑错在哪（留个记录，别再改回去）──
    ///
    ///    原来是"队伍血量健康 + 以太 > 保留数 → 卸"，
    ///    再加上"小怪阶段卸到剩 1 颗"和"满 3 颗必卸"。
    ///    问题：**转好还早就把豆子打光了**，
    ///    等到以太超流转好时手里没豆子，那 3 颗就只是"补回来"，
    ///    没有产生任何额外输出 —— 等于把资源浪费在"提前打掉"上。
    ///
    ///  ⚠️ 读不到以太时**不卸**（保守）：
    ///     旧代码是"读不到就卸"（`if (!读得到) return new[]{能量吸收}`）——
    ///     那在读取接口抽风时会把豆子无脑打光。现在改成读不到就留着。
    ///
    ///  ⚠️ 「以太保留数」设置有冲突：
    ///     新机制下"留着"是默认行为，这个设置已经**没有意义**了
    ///     （它想表达的"留几颗"就是现在的默认）。
    ///     保留它只是为了不破坏旧存档，界面上仍可调但不影响本逻辑。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public override uint[] 输出能力技
    {
        get
        {
            var 能量吸收 = SpellIds.取("能量吸收");
            if (能量吸收 == 0) return Array.Empty<uint>();

            // ⚠️ 读不到以太 → **留着**（不要卸）。
            //    方向很重要：读不到就卸的话，接口一抽风豆子就没了。
            if (!JobApiHelper.读得到("以太")) return Array.Empty<uint>();

            // 总开关（用户可能想完全禁掉卸豆）
            if (!HealQt.GetQt("能量吸收", true)) return Array.Empty<uint>();

            // 蓝量见底时停手 —— 蓝要留给治疗
            if (蓝量.低蓝停手()) return Array.Empty<uint>();

            var 以太 = JobApiHelper.以太;
            if (以太 <= 0) return Array.Empty<uint>();   // 没豆子，等以太超流补

            // ── 条件①：爆发窗口 → 拿去换输出 ──
            if (以太管理.在爆发窗口()) return new[] { 能量吸收 };

            // ── 条件②：以太超流快转好 + 无治疗压力 → 清空免得溢出 ──
            if (以太管理.以太超流快转好())
            {
                // "无治疗压力"的判据：没有人低于群体治疗阈值。
                //   这一条**必须有** —— 没有它就会在团队大掉血的瞬间
                //   把救命的豆子全换成输出。
                if (HealTargetHelper.低于阈值人数(HealSettings.Instance.群体治疗阈值) == 0)
                    return new[] { 能量吸收 };
            }

            // ── 其他情况：一律留着 ──
            return Array.Empty<uint>();
        }
    }
}

public class SCHRotationEntry : HealerEntryBase
{
    private readonly SCHSpellTable _spells = new();

    public override Jobs TargetJob => Jobs.Scholar;
    public override string OverlayTitle => "小鲸鱼 · 学者（日随）";
    public override string Description =>
        "日随用学者 ACR。以太优先，小仙女 / 炽天使自动放，时间轴预报到伤害时拉炽天使。\n" +
        "1-100 级通用；脱战自动补以太，木桩模式打最优输出。";
    public override JobSpellTable Spells => _spells;

    protected override List<SlotResolverData> 构建决策队列_职业专属()
    {
        return new List<SlotResolverData>
        {
            new SlotResolverData(new Res_Sprint(), SlotMode.Always),              // 脱战自动疾跑
            new SlotResolverData(new SCH_AutoDissipation(), SlotMode.Always),   // 转化换豆（默认关）
            new SlotResolverData(new SCH_ForceAetherflow(), SlotMode.Always),   // 手动强制补（默认关）
            new SlotResolverData(new SCH_Aetherflow(), SlotMode.Always),   // 按错题集：能力技进 Always，不受 GCD 就绪的影响
            new SlotResolverData(new SCH_ChainStratagem(), SlotMode.Always),   // 按错题集：能力技进 Always，不受 GCD 就绪的影响
            new SlotResolverData(new Res_OffensiveAbility(_spells), SlotMode.Always),   // 按错题集：能力技进 Always，不受 GCD 就绪的影响
            new SlotResolverData(new Res_PrepareResources(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_Raise(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_Esuna(_spells), SlotMode.Gcd),
            // ★ 「必须奶满」机制 —— 插在最前面，理由是行号才是优先级。
            //   这类 debuff 是“奶到 100% 才解除”，晚一个 GCD 人就没了；
            //   而 Check() 的返回值**不参与仲裁**（反汇编已证），只有行号算数。
            //   原来它排在所有治疗 GCD 之后 —— 场上有人被再生/医济抢走 GCD 时，
            //   就永远轮不到它。详见 Res_MustFullHeal 的类注释。
            new SlotResolverData(new Res_MustFullHeal(_spells), SlotMode.Gcd),
            // ★ 预铺 / 瞬发治疗能力技 —— **必须在所有治疗 GCD 之前**。
            //   需求是“治疗应该优先能力技 / 不读条的技能”，
            //   而 Check() 的返回值**不参与仲裁** —— 排在哪一行才算数。
            //   参考实现的 resolver 列表也是能力技在前（IL 直证）。
            //   详见 Res_InstantHealAbility 的类注释。
            new SlotResolverData(new Res_HealEmergency(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_InstantHealAbility(_spells), SlotMode.OffGcd),
            new SlotResolverData(new SCH_Accession(), SlotMode.Gcd),                 // 降临之章（炽天附体期间）
            new SlotResolverData(new Res_HealAoEGcd(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealSingleGcd(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealShield(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_SpreadShield(_spells), SlotMode.Gcd),  // 展开战术   // 展开战术：要有盾可扩散
            // AOE 优先：3 个以上敌人时不该先给单只怪挂 DoT
            new SlotResolverData(new Res_AoEDamage(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_Dot(_spells), SlotMode.Gcd),
            new SlotResolverData(new SCH_BanefulImpaction(), SlotMode.Gcd),   // 埋伏之毒（需预备 buff）
            // ⚠️ 位置很重要：必须在 `Res_MoveGcd`（移动填充）**之前** ——
            //   药尾声补刀最该生效的场景就是**移动中**（读条放不出来），
            //   排在移动填充后面等于**永远选不到**。
            new SlotResolverData(new Res_PotionTailDamage(_spells), SlotMode.Gcd),
            // ★ 多目标 DoT：主目标 DoT 还在时，把 DoT 扩散到**其他被拉到的怪** ★
            //   ⚠️ 必须在 Res_MoveGcd 之前（否则永远抢不到这个 GCD）
            //   ⚠️ 必须在 Res_Dot 之后（主目标的 DoT 优先级更高）
            new SlotResolverData(new Res_MultiDot(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_MoveGcd(_spells), SlotMode.Gcd),            new SlotResolverData(new Res_BaseDamage(_spells), SlotMode.Gcd),

            new SlotResolverData(new SCH_Consolation(), SlotMode.OffGcd),
            new SlotResolverData(new SCH_WhisperingDawn(), SlotMode.OffGcd),
            new SlotResolverData(new SCH_FeyBlessing(), SlotMode.OffGcd),
            new SlotResolverData(new SCH_FeyIllumination(), SlotMode.OffGcd),
            new SlotResolverData(new Res_HealAoEAbility(_spells), SlotMode.OffGcd),
            new SlotResolverData(new SCH_Aetherpact(), SlotMode.OffGcd),
            new SlotResolverData(new SCH_Seraph(), SlotMode.OffGcd),
            new SlotResolverData(new SCH_SummonFairy(), SlotMode.OffGcd),
            new SlotResolverData(new Res_SelfMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_TeamMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_LucidDreaming(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_HealBooster(_spells), SlotMode.OffGcd),         // 秘策
            new SlotResolverData(new Res_Emergency(_spells), SlotMode.OffGcd),           // 应急战术
            new SlotResolverData(new Res_GroupMitigationExtra(_spells), SlotMode.OffGcd), // 疾风怒涛之计
            new SlotResolverData(new Res_SingleMitigation(_spells), SlotMode.OffGcd),    // 生命回生法
            new SlotResolverData(new SCH_Dissipation(), SlotMode.OffGcd),                // 转化
            new SlotResolverData(new SCH_Seraphism(), SlotMode.OffGcd),                  // 炽天附体(100)
            new SlotResolverData(new SCH_AdloquiumUpgrade(), SlotMode.OffGcd),           // 意气轩昂之策(96)
            new SlotResolverData(new Res_LimitBreak(), SlotMode.OffGcd),                  // 极限技
        };
    }

    protected override void 构建QT()
    {
        base.构建QT();
        加职业开关("以太超流", true);
        加职业开关("链式策略", true);
        加职业开关("小仙女", true);
        加职业开关("炽天使", true);
    }

    /// <summary>爆发轴（勾上「一键爆发」才生效）</summary>
    protected override AEAssist.CombatRoutine.Module.ISlotSequence[] 构建爆发轴()
        => new AEAssist.CombatRoutine.Module.ISlotSequence[] { new 学者爆发轴(Spells) };
}

// ============================================================================
//  学者专属
// ============================================================================

/// <summary>以太超流：脱战也能补（脱战准备那条会先用掉一次）。</summary>
public class SCH_Aetherflow : ISlotResolver
{
    private static uint 技能 => SpellIds.取("以太超流");

    public int Check()
    {
        // 木桩也要补以太 —— 能量吸收要靠它换输出，属于资源循环
        if (!HealQt.GetQt("以太超流", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;
            // ⚠️ 修正（2026-09-28）：以太超流是**补满到 3 颗**，不是"覆盖"。
            //    有 1 颗时开新的 → 变成 3 颗，**一点都不浪费**。
            //
            //    我原来写的是"必须一颗都不剩才补"，注释还说"新的会覆盖旧的、直接浪费" ——
            //    **那个理解是错的**。后果是一个死锁：
            //      有 1 颗 → 我不开 → 以太永远到不了 3 颗 → "满 3 才卸"永远不成立
            //      → 能量吸收永远不打（现象就是这个）
            //
            //    正确规则：只有**已经满了**（3 颗）才不开，否则 CD 好了就补。
            if (JobApiHelper.读得到("以太") && JobApiHelper.以太 >= 3) return -3;

        // ★ 低蓝时不补豆（对照同类 ACR：它的补豆 Check 里也有 IsLowMpStopActive）★
        //   理由：补豆是为了卸豆换输出，低蓝时该做的是省蓝治疗。
        if (蓝量.低蓝停手()) return -4;

        if (!CharacterExt.可以插能力技()) return -6;

        if (HealSettings.Instance.时间轴攒资源 && TimelineManager.未来有减伤(4.0)) return -5;

        return SpellUtil.可用(技能) ? 2 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>异想的祥光：小仙女给的免费群体治疗。</summary>
public class SCH_FeyBlessing : ISlotResolver
{
    private static uint 技能 => SpellIds.取("异想的祥光");

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("群奶")) return -101;
        if (!HealQt.GetQt("小仙女", true)) return -103;
        if (!SpellUtil.已解锁(技能)) return -2;

        var s = HealSettings.Instance;
        // 小仙女技能是免费的，门槛比 GCD 群奶低一个人
        var 要求人数 = Math.Max(1, s.群奶最少人数 - 1);
        if (HealTargetHelper.低于阈值人数(s.群体治疗阈值, 20f) < 要求人数) return -1;

        return SpellUtil.可用(技能) ? 18 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>以太契约：给小仙女指定目标持续回血，需要 20 点妖精能量。</summary>
public class SCH_Aetherpact : ISlotResolver
{
    private static uint 技能 => SpellIds.取("以太契约");

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("小仙女", true)) return -103;
        if (!SpellUtil.已解锁(技能)) return -2;
        if (JobApiHelper.妖精能量 < 20) return -3;

        var tank = HealTargetHelper.血量最低的坦克(HealSettings.Instance.妖精契约血线);
        if (tank == null) return -1;
        if (技能 != 0 && tank.有该技能的Buff(技能)) return -4;

        return SpellUtil.可用(技能) ? 5 : -1;
    }

    public void Build(Slot slot)
    {
        // ⚠️ 必须和 Check 用**同一个血线**（审计发现）——
        //    原来 Check 用设置值、Build 写死 0.8f。
        //    用户把「妖精契约血线」调到 >0.8 时：
        //      Check 找到坦克（如 0.9 血）返回 5
        //      → Build 再查 0.8f 得到 null → **slot 是空的**
        //    → 框架继续扫下一个 resolver → 这个技能**静默不放**。
        //    （F③ 那类"判 A 放不出"：Check 和 Build 必须同源）
        var tank = HealTargetHelper.血量最低的坦克(HealSettings.Instance.妖精契约血线);
        if (tank == null) return;
        slot.Add(new Spell(技能, tank));
    }
}

/// <summary>炽天召唤：把小仙女升级成炽天使（解锁慰藉群盾）。</summary>
public class SCH_Seraph : ISlotResolver
{
    private static uint 技能 => SpellIds.取("炽天召唤");

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("小仙女", true)) return -103;
        if (!HealQt.GetQt("炽天使", true)) return -104;
        if (!SpellUtil.已解锁(技能)) return -2;
        if (JobApiHelper.炽天使剩余 > 0) return -3;

        var 团队掉血 = HealTargetHelper.低于阈值人数(HealSettings.Instance.群体治疗阈值)
                       >= HealSettings.Instance.群奶最少人数;
        var 要来伤害 = TimelineManager.未来有减伤(3.0) || 减伤Helper.即将来大伤害();

        if (!团队掉血 && !要来伤害) return -1;

        return SpellUtil.可用(技能) ? 7 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>慰藉：炽天使的群体护盾，能放两次。</summary>
public class SCH_Consolation : ISlotResolver
{
    private static uint 技能 => SpellIds.取("慰藉");

    /// <summary>上次放慰藉的时间。慰藉有 **2 层充能**，不加限流的话
    /// 条件一成立就会瞬间把两层全交掉（应该分两次用，比如 AOE 前后各一次）。</summary>
    private static long 上次慰藉;

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("炽天使", true)) return -104;
        if (!SpellUtil.已解锁(技能)) return -2;
        if (JobApiHelper.炽天使剩余 <= 0) return -3;

        // 慰藉 2 层充能：3 秒内只放一次，避免一口气全交
        // 慰藉 2 层充能（参考同类 ACR 的 GetCharges）：
        //   满 2 层时尽快交掉防溢出；只剩 1 层时保持较长限流，避免一口气全交。
        var 充能 = CharacterExt.充能数(技能);
        var 限流 = 充能 >= 2 ? 800 : 3000;
        if (TimeHelper.Now() - 上次慰藉 < 限流) return -7;

        var s = HealSettings.Instance;
        var 团队掉血 = HealTargetHelper.低于阈值人数(s.群体治疗阈值, 20f) >= s.群奶最少人数;   // 慰藉 20 米
        var 要来伤害 = TimelineManager.未来有减伤(2.0) || 减伤Helper.即将来大伤害();

        if (!团队掉血 && !要来伤害) return -1;

        return SpellUtil.可用(技能) ? 19 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell == null) return;
        slot.Add(spell);
        上次慰藉 = TimeHelper.Now();
    }
}

/// <summary>小仙女不在场就召唤。</summary>
public class SCH_SummonFairy : ISlotResolver
{
    private static uint 技能 => SpellIds.取("朝日召唤");

    public int Check()
    {
        // 木桩模式**不拦** —— 小仙女是常驻宠物，掉了就该补
        if (!HealQt.GetQt("小仙女", true)) return -103;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 791 = 转化状态：转化期间小仙女被牺牲，召唤无效
        if (AuraIds.转化中 != 0 && Core.Me.HasAura(AuraIds.转化中)) return -3;

        // 已经在场就不用召唤（HasPet 读得到，面板显示也证明它是准的）
        if (JobApiHelper.有小仙女) return -3;
        if (JobApiHelper.炽天使剩余 > 0) return -3;

        // 移动中不召唤（读不到就放行）
        try { if (MoveHelper.IsMoving()) return -3; } catch { }

        // ⚠️ 这里刻意**不检查 SpellUtil.可用()**。
        //    逆向同类 ACR 的 Scholar_GetPet 发现：它的 Check 里根本没有可用性检查，
        //    判断完条件就直接把技能塞进 slot 交给游戏。
        //    而我加的 IsReadyWithCanCast() 对召唤类技能返回 false，
        //    导致永远不召唤（日志：已解锁=True 能召唤=False）。
        return 1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>连环计：团辅。木桩模式卡 CD 放，不攒。</summary>
public class SCH_ChainStratagem : ISlotResolver
{
    private static uint 技能 => SpellIds.取("连环计");

    public int Check()
    {
        if (!HealQt.GetQt("链式策略", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;
        if (HealTargetHelper.当前目标() == null) return -1;
        if (!CharacterExt.可以插能力技()) return -6;

        if (!HealTargetHelper.木桩模式)
        {
            // 需求 2：残血小怪不给团辅
            if (HealTargetHelper.目标快死了()) return -4;
            if (HealSettings.Instance.时间轴攒资源 && TimelineManager.未来有减伤(8.0)) return -5;
        }

        return SpellUtil.可用(技能) ? 2 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}
