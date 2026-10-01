using AEAssist;
using AEAssist.CombatRoutine.Module;
using HealerACR.Common;
using HealerACR.Resolvers;

namespace HealerACR.Rotations;

/// <summary>
/// 幻术师（Conjurer, CNJ）—— 白魔法师的**前置职业**，等级上限 50。
///
/// ══════════════════════════════════════════════════════════════════
///  ⚠️ 幻术师**不是**白魔的低等级形态 —— 它是一套**独立的技能 ID**：
///
///      幻术师              白魔法师
///      119 飞石            131 愈疗
///      120 治疗            136 神速咏唱
///      121 疾风            137 再生
///      124 医治            139 神圣
///      125 复活            140 天赐祝福
///      127 坚石            3568 垒石
///      132 烈风            ...
///      133 医济
///      135 救疗
///
///  所以不能直接复用白魔的技能表 —— 得按幻术师自己的 ID 配。
///
///  好消息是**技能构成是白魔的子集**：
///      有：单体输出（飞石→坚石）、单体治疗（治疗→救疗）、
///          群体治疗（医治→医济）、DoT（疾风→烈风）、复活
///      没有：群体输出（神圣）、团队减伤（节制）、紧急单奶（天赐/神名）、
///            群体治疗能力技（法令）、个人减伤（神祝祷）
///
///  所以实现上**继承白魔表、把没有的覆写成 0** —— 这样将来白魔表改进
///  （比如新的 DoT 档位处理）会自动同步过来，不用维护两份。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class 幻术师SpellTable : WHMSpellTable
{
    public override Jobs Job => Jobs.Conjurer;
    public override string 职业名 => "幻术师";

    /// <summary>幻术师没有 AOE 输出 —— 神圣是白魔 45 级才学的</summary>
    public override uint 群体输出 => 0;

    /// <summary>幻术师没有天赐祝福和神名，紧急单奶只能靠普通治疗</summary>
    public override uint 紧急单奶 => 0;

    /// <summary>法令是白魔 56 级技能</summary>
    public override uint 群体治疗能力技 => 0;

    /// <summary>节制是白魔技能，幻术师没有团队减伤</summary>
    public override uint 团队减伤 => 0;

    /// <summary>神祝祷是白魔技能</summary>
    public override uint 个人减伤 => 0;

    /// <summary>没有神速咏唱 → 也就没有闪飒形态</summary>
    public override bool 有特殊输出形态 => false;
}

/// <summary>
/// 幻术师入口。
///
/// **架构上直接复用基类** —— 队列、resolver、事件处理全在
/// <see cref="HealerEntryBase"/> 里，幻术师只是换了一张技能表。
///
/// 这也意味着：幻术师会**自动获得**所有对 HealerACR 的改进
/// （DoT 窗口、盾判断、减伤触发、复活优先级、移动检测……），
/// 不需要单独维护。
/// </summary>
// ══════════════════════════════════════════════════════════════════════
//  ★ 条件编译：这份源码会被编译进【两个】程序集 ★
//
//  [!] 背景：`BlueWhale.AutoHealerACR.csproj` 用
//        &lt;Compile Include="..\HealerACR\**\*.cs" /&gt;
//      把 HealerACR 的源码又编译了一遍 ==> 基础设施**各有一份静态状态**。
//      正在改成「BlueWhale 引用 HealerACR」（见该 csproj 的注释）。
//
//  [!] 但直接引用会带来一个问题：
//      AEAssist 会用反射扫 ACR 程序集里的职业入口类
//      （`GetExportedTypes` + `IsAbstract` + `IsClass` + `CreateInstance`）。
//      ==> HealerACR.dll 的入口类也会被扫到
//      ==> 职业列表里多出 "HealerACR·白魔"，**而用户只要小鲸鱼**。
//
//  [!] 解法：**在 BlueWhale 里把入口类声明成 `abstract`** ——
//      扫描器跳过抽象类；而 `BlueWhaleWhiteMageEntry` 继承它，
//      **是唯一的具体实现**。
//      ==> HealerACR 独立编译时仍是具体类（那个 ACR 依然可用）。
// ══════════════════════════════════════════════════════════════════════
#if HEALERACR_STANDALONE
    public class 幻术师RotationEntry : HealerEntryBase
#else
    // 在 BlueWhale 里声明为 abstract —— 让 AEAssist 的扫描器跳过它
    // （`BlueWhaleEntry` 才是具体实现）。详见上面的条件编译说明。
    public abstract class 幻术师RotationEntry : HealerEntryBase
#endif
{
    private readonly 幻术师SpellTable _spells = new();

    public override Jobs TargetJob => Jobs.Conjurer;
    public override string OverlayTitle => "小鲸鱼 · 幻术师（日随）";
    public override string Description =>
        "日随用幻术师 ACR（1-50 级）。\n" +
        "白魔的前置职业，共用同一套治疗/输出逻辑。\n" +
        "注意：幻术师没有神圣、节制、天赐祝福 —— 那些是白魔技能。";
    public override JobSpellTable Spells => _spells;

    protected override List<SlotResolverData> 构建决策队列_职业专属()
    {
        return new List<SlotResolverData>
        {
            new SlotResolverData(new Res_Sprint(), SlotMode.Always),
            new SlotResolverData(new Res_OffensiveAbility(_spells), SlotMode.Always),
            // 宠物不在场就召唤（学者的朝日召唤）—— 排在脱战准备之前
            new SlotResolverData(new Res_SummonPet(_spells), SlotMode.Gcd),
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
            new SlotResolverData(new Res_SingleHoT(_spells), SlotMode.Gcd),

            // ── 治疗（幻术师只有 GCD 治疗，没有百合/天赐那些能力技）──
            new SlotResolverData(new Res_HealAoEGcd(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealSingleGcd(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealShield(_spells), SlotMode.Gcd),

            // ── 输出 ──
            // ══════════════════════════════════════════════════════════════
            //  ★ 顺序修正：**DoT 必须在 AOE 之前**（对齐两套参考实现的槽序）★
            //
            //  [!] 原来这里是 AOE 在前，注释写的是
            //        「AOE 优先：3 个以上敌人时不该先给单只怪挂 DoT」。
            //
            //  [!] 但参考实现**两套的槽序都是 DoT 在 AOE 之前**（IL 实证）：
            //        shiyuvi 41 槽：… 11 Dot · 12 ForceRuin2 · 13 AOE · 14 BaseGCD
            //        youshu  34 槽：… 28 DOT · 29 即刻极炎法 · 30 裂阵法 · 31 毁坏
            //      ==> 「DoT 在 AOE 之前」这一点**两套没有分歧**。
            //
            //  [!] 而且原来那句注释**与代码不符**：`Res_AoEDamage` 的门槛是
            //        `AOE最少敌人数 = 2`，**不是 3** ==>
            //        2 只敌人时 AOE 就够格了，DoT 会被推后一个 GCD。
            //
            //  [!] 差异范围（枚举验证过，不是猜）：
            //        · 1 只敌人：两边一样（AOE 门槛 2 不满足，落下来补 DoT）
            //        · DoT 还满着：两边一样（`Dot补判` 挡住，落下来放 AOE）
            //        · **只有「≥2 敌 且 DoT 到该补的时候」才有差异**
            //
            //  [!] 为什么改顺序而不是把门槛提到 3：
            //        门槛 2 = youshu 在 82 级以下的档位（它按等级分档 `>=82 ? 3 : 2`）；
            //        提门槛会**同时改变「2 只敌人要不要打 AOE」**这个更基础的行为。
            //
            //  [!] 两个 resolver 的守卫都是完整的（移动中可用 / 值得上Dot / Dot补判 /
            //      黑名单 / AOE 门槛），提前不会造成"移动中卡住"那类问题。
            // ══════════════════════════════════════════════════════════════
            new SlotResolverData(new Res_Dot(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_AoEDamage(_spells), SlotMode.Gcd),   // 幻术师没 AOE，会自己跳过
            new SlotResolverData(new Res_BaseDamage(_spells), SlotMode.Gcd),
            // 移动填充兜底 —— 必须在 Res_BaseDamage **之后**：
            // 破阵法（近战填充技）也是瞬发，应该由 Res_BaseDamage 优先选它，
            // 放前面会把 GCD 抢成毁坏（实测过的问题）。
            new SlotResolverData(new Res_MoveGcd(_spells), SlotMode.Gcd),

            // ── 能力技（减伤/资源）──
            new SlotResolverData(new Res_HealAoEAbility(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_SelfMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_TeamMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_LucidDreaming(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_HealBooster(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_SingleMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_LimitBreak(), SlotMode.OffGcd),
        };
    }
}
