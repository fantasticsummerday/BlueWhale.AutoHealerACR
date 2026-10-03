using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;
using HealerACR.Resolvers;
using HealerACR.Timeline;

namespace HealerACR.Rotations;

/// <summary>占星技能表。技能 ID 走 SpellIds 内置表（中文名）。</summary>
public class ASTSpellTable : JobSpellTable
{
    public override Jobs Job => Jobs.Astrologian;
    public override string 职业名 => "占星术士";

    public override uint 基础输出 => SpellUtil.取已解锁(
        SpellIds.取("落陷凶星"), SpellIds.取("煞星"), SpellIds.取("祸星"),
        SpellIds.取("灾星"), SpellIds.取("凶星"));

    /// <summary>药尾声补刀用的瞬发填充技 —— 占星：落陷凶星（25871）—— 即刻下变瞬发。</summary>
    public override uint 药尾声填充技 => SpellIds.取("落陷凶星");
    public override uint 群体输出 => SpellUtil.取已解锁(
        SpellIds.取("中重力"), SpellIds.取("重力"));

    /// <summary>
    /// **占星的 AOE 伤害范围 = 8 米**（原来没覆写，用了默认 5）。
    ///
    /// [!] 游戏数据（`build/healer_shape.tsv`）：
    ///       3615  重力     EffectRange = 8
    ///       25872 中重力   EffectRange = 8
    ///     ⇒ 真实是 8，默认值是 5 ⇒ **少算了 3 米**。
    ///
    /// [!] 影响面（如实说）：`Res_AoEDamage` 走框架的
    ///     `TargetHelper.GetMostCanTargetObjects`，**不读这个常量** ⇒
    ///     实际放 AOE 的判定没错。受影响的是 `Res_Mitigation` 的
    ///     "敌人够多"与 AI 候选集里的敌数统计 ——
    ///     用 5 会**低估**能打到的敌人数 ⇒ 该放 AOE 时判不出来。
    /// </summary>
    public override int AOE伤害范围 => 8;
    /// <summary>
    /// **没有"自身 AOE"技** —— 占星的重力（3615）/中重力（25872）都是
    /// `射程=25` 的**目标中心** AOE（见 `tools\CastProbe`），不是以自己为中心。
    ///
    /// ⚠️ 所以这里留空，`选填充技` 会退回"基础输出 + 移动填充技"两选一。
    /// </summary>
    public override uint[] 自身AOE候选 => Array.Empty<uint>();

    /// <summary>
    /// **威力档位** —— 数据逐字来自游戏数据宏（`tools\HealPotency raw`）。
    ///
    ///    凶星     3596   Lv1   150
    ///    灾星     3598   Lv54  160
    ///    祸星     7442   Lv64  190
    ///    煞星     16555  Lv72  230
    ///    落陷凶星 25871  Lv82  if(等级>=94) 270 else 250
    ///    烧灼     3599   Lv4   （DoT，见 §1.3）
    /// </summary>
    protected override (int 等级, int 威力)[]? 威力表(uint 技能Id)
    {
        if (技能Id == SpellIds.取("凶星"))     return new[] { (54, 160), (0, 150) };
        if (技能Id == SpellIds.取("灾星"))     return new[] { (64, 190), (0, 160) };
        if (技能Id == SpellIds.取("祸星"))     return new[] { (72, 230), (0, 190) };
        if (技能Id == SpellIds.取("煞星"))     return new[] { (82, 250), (0, 230) };
        if (技能Id == SpellIds.取("落陷凶星")) return new[] { (94, 270), (0, 250) };

        // ══════════════════════════════════════════════════════════════
        //  ★ 群体输出 + Dot —— 补上（链条覆盖检查发现的缺口）★
        //
        //  [!] **不会导致选错技能**：占星**没有覆写 `自身AOE候选`** ⇒
        //      `选填充技` 的"近战候选"对它为空；
        //      它的 AOE 走 `Res_AoEDamage`（用 `技能数据.打得到`，
        //      **不经过 `查威力`**）。
        //  [!] Dot 同理，走 `值得上Dot()`，不比较威力。
        //  ⇒ 属于"AI 看不到数值"的缺口，不是"选错"。
        //
        //  数值来源：`Action:<id>` 页的结构化表头。
        // ══════════════════════════════════════════════════════════════
        if (技能Id == SpellIds.取("重力"))   return new[] { (0, 120) };
        if (技能Id == SpellIds.取("中重力")) return new[] { (0, 140) };

        if (技能Id == SpellIds.取("焚灼")) return new[] { (0, 70) };
        if (技能Id == SpellIds.取("炽灼")) return new[] { (0, 60) };
        if (技能Id == SpellIds.取("烧灼")) return new[] { (0, 50) };

        return null;
    }

    /// <summary>
    /// **咏唱时间**（秒）—— 来自游戏 `Cast100ms`。
    ///
    /// ⚠️ 占星的填充技里**只有 DoT 是瞬发**（烧灼/炽灼/焚灼 咏唱 0.0）——
    ///    所以移动中能打的就只有 DoT，没有瞬发伤害填充技可用。
    /// </summary>
    public override IReadOnlyDictionary<uint, float> 咏唱时间表 { get; } = new Dictionary<uint, float>
    {
        [SpellIds.取("凶星")] = 1.5f,
        [SpellIds.取("灾星")] = 1.5f,
        [SpellIds.取("祸星")] = 1.5f,
        [SpellIds.取("煞星")] = 1.5f,
        [SpellIds.取("落陷凶星")] = 1.5f,
    };


    // ══════════════════════════════════════════════════════════════════
    //  ★ 治疗候选集 ★  —— 数据来自游戏数据（见 `WhiteMageACR` 同处的说明）
    // ══════════════════════════════════════════════════════════════════
    private 治疗候选集? _治疗候选;

    public override 治疗候选集 治疗候选
    {
        get
        {
            if (_治疗候选 != null) return _治疗候选;

            var c = new 治疗候选集();

            // ── 单体 GCD ──
            c.加(new 治疗技能 { Id = SpellIds.取("吉星"), 名 = "吉星", 等级 = 2,
                恢复力 = 档位(SpellIds.取("吉星"), 500, 450, 85), MP = 400, 咏唱 = 1.5f, 复唱 = 2.5f });

            c.加(new 治疗技能 { Id = SpellIds.取("福星"), 名 = "福星", 等级 = 26,
                恢复力 = 档位(SpellIds.取("福星"), 800, 700, 85), MP = 700, 咏唱 = 1.5f, 复唱 = 2.5f });

            // ── 单体 GCD 盾 + HoT（吉星相位 = 治疗 + 盾）──
            c.加(new 治疗技能 { Id = SpellIds.取("吉星相位"), 名 = "吉星相位", 等级 = 34,
                恢复力 = 档位(SpellIds.取("吉星相位"), 250, 200, 85), MP = 400, 咏唱 = 0f, 复唱 = 2.5f,
                // [!] **修正：去掉 `是盾`**（数据核对）
                //     原文只有「恢复目标的体力　恢复力：　追加效果：令目标体力持续恢复」，
                //     **没有防护罩**。盾只在**中间学派**期间才有（吉星相位 250%），
                //     那是 buff 条件，不是技能自带的。
                //     标错 `是盾` 会凭空加一份吸收量 ⇒ 高估它。
                HoT恢复力 = 档位(SpellIds.取("吉星相位"), 250, 200, 85), HoT持续 = 15f });

            // ── 群体 GCD ──
            c.加(new 治疗技能 { Id = SpellIds.取("阳星"), 名 = "阳星", 等级 = 10,
                恢复力 = 档位(SpellIds.取("阳星"), 400, 330, 85), MP = 700, 咏唱 = 1.5f, 复唱 = 2.5f, 群体 = true });

            c.加(new 治疗技能 { Id = SpellIds.取("阳星相位"), 名 = "阳星相位", 等级 = 40,
                恢复力 = 档位(SpellIds.取("阳星相位"), 250, 200, 85), MP = 800, 咏唱 = 1.5f, 复唱 = 2.5f,
                // [!] **修正：去掉 `是盾`**（同吉星相位 —— 原文没有防护罩，
                //     盾只在中间学派期间有，阳星相位 125%）。
                群体 = true, HoT恢复力 = 档位(SpellIds.取("阳星相位"), 150, 100, 85), HoT持续 = 15f });

            c.加(new 治疗技能 { Id = SpellIds.取("阳星合相"), 名 = "阳星合相", 等级 = 96,
                恢复力 = 250, MP = 800, 咏唱 = 1.5f, 复唱 = 2.5f, 群体 = true,
                HoT恢复力 = 175, HoT持续 = 15f });

            // ── 单体能力技 ──
            c.加(new 治疗技能 { Id = SpellIds.取("先天禀赋"), 名 = "先天禀赋", 等级 = 15,
                // ⚠️ 恢复力是 **400～900 动态**（说明原文："目标剩余体力越少恢复力越高，
                //    30% 及更低时恢复力最高"）——原来写死 400，**严重低估**。
                //    这里取 **900**（救命场景的实际值）；非救命时它本就不该被选中。
                恢复力 = 900, MP = 0, 咏唱 = 0f, 冷却 = 40f, 充能 = 2 });

            c.加(new 治疗技能 { Id = SpellIds.取("天星交错"), 名 = "天星交错", 等级 = 74,
                恢复力 = 200, MP = 0, 咏唱 = 0f, 冷却 = 30f, 是盾 = true });

            c.加(new 治疗技能 { Id = SpellIds.取("擢升"), 名 = "擢升", 等级 = 86,
                恢复力 = 500, MP = 0, 咏唱 = 0f, 冷却 = 60f });

            // ── 群体能力技 ──
            c.加(new 治疗技能 { Id = SpellIds.取("命运之轮"), 名 = "命运之轮", 等级 = 58,
                恢复力 = 100, MP = 0, 咏唱 = 0f, 冷却 = 60f, 群体 = true });

            c.加(new 治疗技能 { Id = SpellIds.取("天星冲日"), 名 = "天星冲日", 等级 = 60,
                恢复力 = 200, MP = 0, 咏唱 = 0f, 冷却 = 60f, 群体 = true,
                HoT恢复力 = 100, HoT持续 = 15f });

            c.加(new 治疗技能 { Id = SpellIds.取("天宫图"), 名 = "天宫图", 等级 = 76,
                // ⚠️ 两档（说明原文）：天宫图 200 / **阳星天宫图 400**
                //    （"持续时间内受到阳星或阳星相位时变为阳星天宫图"）。
                //    取 400 —— 实战里基本都会先放阳星相位再放它。
                恢复力 = 400, MP = 0, 咏唱 = 0f, 冷却 = 60f, 群体 = true });

            // ── 地星（Lv62，`540 + 720`）──
            //    ⚠️ 它是**放置后手动引爆**的地面技（CastType=7），
            //       有效治疗量按引爆那一发 **720** 算（540 是提前引爆的小值）。
            c.加(new 治疗技能 { Id = SpellIds.取("地星"), 名 = "地星", 等级 = 62,
                // ⚠️ 两个引爆阶段（说明原文）：
                //      星体破裂（10 秒内手动引爆）恢复力 540
                //      **星体爆炸（等它自然强化后）恢复力 720**
                //    取 720（自然引爆炸的实际值）。
                恢复力 = 720, MP = 0, 咏唱 = 0f, 冷却 = 60f, 群体 = true });

            // ── 小宇宙（Lv90，群体 200）──
            c.加(new 治疗技能 { Id = SpellIds.取("小宇宙"), 名 = "小宇宙", 等级 = 90,
                // ⚠️ 说明原文："恢复力200 ＋ **积蓄所受伤害的50%**"，恢复量不超过最大体力。
                //    是个**动态值** —— 这里保守按 200 算（积蓄部分无法预知）。
                恢复力 = 200, MP = 0, 咏唱 = 0f, 冷却 = 1f, 群体 = true });

            _治疗候选 = c;
            return c;
        }
    }

    private static int 档位(uint 技能Id, int 高, int 低, int 提升等级)
    {
        try { return CharacterExt.我的等级() >= 提升等级 ? 高 : 低; }
        catch { return 低; }
    }

    public override uint Dot技能 => SpellUtil.取已解锁(
        SpellIds.取("焚灼"), SpellIds.取("炽灼"), SpellIds.取("烧灼"));
    public override uint DotBuff => AuraIds.占星Dot;
    // 焚灼有两档 id（1881 / 2041）
    public override uint[] 所有DotBuff => new[]
    {
        AuraIds.占星Dot, AuraIds.占星DotAlt, AuraIds.占星Dot2, AuraIds.占星Dot1
    };

    public override uint 单体治疗GCD => SpellUtil.取已解锁(SpellIds.取("福星"), SpellIds.取("吉星"));
    public override uint 群体治疗GCD => SpellUtil.取已解锁(
        SpellIds.取("阳星合相"), SpellIds.取("阳星相位"), SpellIds.取("阳星"));
    public override uint 紧急单奶 => SpellIds.取("先天禀赋");

    /// <summary>
    /// 天星交错：单体减伤/护盾，30 秒 CD —— **预铺类**（伤害来之前给）。
    ///
    /// ⚠️ 它原来只在 `Res_SingleMitigation` 里用（靠"伤害要来"触发），
    ///    这里同时登记为预铺单奶能力技，让 `Res_InstantHealAbility` 在
    ///    坦克掉血时也能主动交 —— "优先用不读条的"。
    /// </summary>
    /// <summary>单体 HoT：吉星相位（3595）—— 注意它是**读条**的（和白魔再生不同）。</summary>
    public override uint 单体HoT => SpellIds.取("吉星相位");

    public override uint 预铺单奶能力技 => SpellIds.取("天星交错");

    /// <summary>
    /// **单体盾：天星交错**（审查发现原来 `单体盾` 空着）。
    ///
    /// [!] 官表说明核实：天星交错 = 恢复力 200 +
    ///     "附加能够抵御一定伤害的**防护罩**，抵消相当于治疗量 200% 的伤害"
    ///     —— 是真正的盾（而且吸收量是治疗量的 2 倍，价值很高）。
    /// </summary>
    public override uint 单体盾 => SpellIds.取("天星交错");

    /// <summary>先天禀赋：瞬发、不读条的单体治疗（血量越低效果越强）。</summary>
    public override uint 瞬发单奶能力技 => SpellIds.取("先天禀赋");

    /// <summary>
    /// 先天禀赋的血线：它是"血量越低效果越强"的急救型，
    /// 所以给 **45%**（和学者的活性法同档），别拉到 75% 浪费。
    /// </summary>
    public override float 瞬发单奶血线 => 0.45f;
    public override uint 群体治疗能力技 => SpellIds.取("天星冲日");
    public override uint 团队减伤 => SpellIds.取("中间学派");
public override uint 个人减伤 => 0;   // ★ 2026-10-04：擢升改由 AST_AllyMitigation 负责（含自己血低时给自己）

    public override uint 复活 => SpellIds.取("生辰");
    public override uint 驱散 => SpellsDefine.Esuna;
    public override uint 醒梦 => SpellsDefine.LucidDreaming;

    // 需求 4：脱战先抽张牌，开战就能出
    public override uint[] 脱战准备技能 => new[] { SpellIds.取("星极抽卡") };
}

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
    public class ASTRotationEntry : HealerEntryBase
#else
    // 在 BlueWhale 里声明为 abstract —— 让 AEAssist 的扫描器跳过它
    // （`BlueWhaleEntry` 才是具体实现）。详见上面的条件编译说明。
    public abstract class ASTRotationEntry : HealerEntryBase
#endif
{
    private readonly ASTSpellTable _spells = new();

    public override Jobs TargetJob => Jobs.Astrologian;
    public override string OverlayTitle => "小鲸鱼 · 占星（日随）";
    public override string Description =>
        "日随用占星 ACR。卡牌按近战/远程分配，地星在时间轴预报前 10 秒预铺。\n" +
        "占星 30 级转职，实际可用 30-100；木桩模式打最优输出。";
    public override JobSpellTable Spells => _spells;

    protected override List<SlotResolverData> 构建决策队列_职业专属()
    {
        return new List<SlotResolverData>
        {
            new SlotResolverData(new Res_Sprint(), SlotMode.Always),              // 脱战自动疾跑
            new SlotResolverData(new Res_OffensiveAbility(_spells), SlotMode.Always),   // 按错题集：能力技进 Always，不受 GCD 就绪的影响
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
            new SlotResolverData(new Res_SingleHoT(_spells), SlotMode.Gcd),   // 吉星相位
                        // ══════════════════════════════════════════════════════════════
            //  ★ 移动中开即刻咏唱 ★
            //
            //  [!] 为什么需要（用户实测："全程移动 ai 不奶了"）：
            //      `SpellUtil.移动中能放()` 只**尊重**即刻 buff，
            //      **没有任何地方主动开它** —— 全项目只有 `Res_PotionTail`
            //      （爆发药）和 `Res_Raise`（拉人）用了即刻。
            //      => 移动中所有读条治疗/盾全被挡住，oGCD 治疗资源一空就一口都放不出来。
            //
            //  [!] 位置：在第一个 GCD 治疗之前（否则那个 GCD 会被别处抢走），
            //      但在 `Res_MustFullHeal` / `Res_HealEmergency` / `Res_Raise` 之后
            //      —— 那些是真救命，不能被"开即刻"抢在前面。
            //
            //  [!] 它自己会判"真的有东西需要即刻吗"（血线告急 / 马上挨大伤害），
            //      不是一移动就烧 60 秒 CD —— 详见 Res_移动开即刻 的注释。
            // ══════════════════════════════════════════════════════════════
            new SlotResolverData(new Res_移动开即刻(_spells), SlotMode.Gcd),
new SlotResolverData(new Res_HealAoEGcd(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealSingleGcd(_spells), SlotMode.Gcd),
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
            new SlotResolverData(new Res_AoEDamage(_spells), SlotMode.Gcd),
            // ⚠️ 位置很重要：必须在 `Res_MoveGcd`（移动填充）**之前** ——
            //   药尾声补刀最该生效的场景就是**移动中**（读条放不出来），
            //   排在移动填充后面等于**永远选不到**。
            new SlotResolverData(new Res_PotionTailDamage(_spells), SlotMode.Gcd),
            // ★ 多目标 DoT：主目标 DoT 还在时，把 DoT 扩散到**其他被拉到的怪** ★
            //   ⚠️ 必须在 Res_MoveGcd 之前（否则永远抢不到这个 GCD）
            //   ⚠️ 必须在 Res_Dot 之后（主目标的 DoT 优先级更高）
            new SlotResolverData(new Res_MultiDot(_spells), SlotMode.Gcd),
            // 基础输出：**必须排在 Res_MoveGcd 之前**。
            // 破阵法（近战填充技）也是瞬发，该由它优先选；
            // 排在移动填充之后会把 GCD 抢成毁坏（实测过的问题）。
            new SlotResolverData(new Res_BaseDamage(_spells), SlotMode.Gcd),
            // 移动填充兜底：当 Res_BaseDamage 因为基础输出是读条技而让位时顶上。
            new SlotResolverData(new Res_MoveGcd(_spells), SlotMode.Gcd),

            new SlotResolverData(new Res_HealAoEAbility(_spells), SlotMode.OffGcd),
            // ---- 爆发组合（参考实现的 AST.Ability.Burst 思路）----
            // 顺序：占卜 → 光速 → 王冠卡 → 出卡 → 小奥秘 → 抽卡
            // 占卜必须最先 —— 它是 120 秒团辅，后面的出卡/伤害都要吃它的增益。
            new SlotResolverData(new AST_Divination(), SlotMode.OffGcd),
            new SlotResolverData(new AST_Lightspeed(), SlotMode.OffGcd),
            new SlotResolverData(new AST_CrownPlay(), SlotMode.OffGcd),
            new SlotResolverData(new AST_Play(), SlotMode.OffGcd),
            new SlotResolverData(new AST_MinorArcana(), SlotMode.OffGcd),
            new SlotResolverData(new AST_Draw(), SlotMode.OffGcd),
            new SlotResolverData(new AST_Horoscope(), SlotMode.OffGcd),
            new SlotResolverData(new AST_EarthlyStar(), SlotMode.OffGcd),
            // ★ 2026-10-04：紧随其后 —— 地星放下后由它引爆 ✓
            new SlotResolverData(new AST_StarDetonation(), SlotMode.OffGcd),
            new SlotResolverData(new Res_SelfMitigation(_spells), SlotMode.OffGcd),
            // ★ 2026-10-04：擢升是**给队友**的减伤（原来只在个人减伤槽里 ⇒ 永远给不到坦克 ✗）
            new SlotResolverData(new AST_AllyMitigation(), SlotMode.OffGcd),
            new SlotResolverData(new Res_TeamMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_LucidDreaming(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_HealLink(_spells), SlotMode.OffGcd),             // 星位合图
            new SlotResolverData(new Res_SingleMitigation(_spells), SlotMode.OffGcd),     // 天星交错
            new SlotResolverData(new Res_GroupMitigationExtra(_spells), SlotMode.OffGcd), // 命运之轮
            new SlotResolverData(new Res_BigAoEHeal(_spells), SlotMode.OffGcd),           // 大宇宙/小宇宙
            new SlotResolverData(new Res_LimitBreak(), SlotMode.OffGcd),                  // 极限技
        };
    }

    protected override void 构建QT()
    {
        base.构建QT();
        加职业开关("光速", true);
        加职业开关("占卜", true);
        加职业开关("抽卡", true);
        加职业开关("地星", true);
    }
}

// ============================================================================
//  占星专属
// ============================================================================

/// <summary>
/// 占星的抽卡节流状态。
///
/// [!] 它**不再是"有没有牌"的替代品** —— 手上有没有牌一律读
///     <see cref="JobApiHelper.手牌"/>（`JobApi_Astrologian.DrawnCards`，
///     两套参考都直接读它）。这里只剩两件"卡面读不到时兜底"的事：
///       · 刚抽过就不会立刻再抽（`等待出卡` + <see cref="等待超时"/>）
///       · 抽卡之间的最小间隔（`上次抽卡`，见 `AST_Draw` 的限流）
///
/// [!] 为什么兜底还要留：万一哪天 `DrawnCards` 又读不到，
///     没有时间保险丝就会变成"一直抽、永远不出" —— 比少抽几张严重得多。
/// </summary>
public static class AST卡牌状态
{
    /// <summary>上一次抽卡的时间戳</summary>
    public static long 上次抽卡;

    /// <summary>抽了但还没出 —— 这时候不该再抽（卡面读得到时以卡面为准）</summary>
    public static bool 等待出卡;

    /// <summary>超过这个时间没出掉就放弃等待（防止卡死再也不抽）</summary>
    public const long 等待超时 = 20000;

    /// <summary>战斗重置 / 换本 / 切职业时清 —— 有状态就得清（开发约定 F①）</summary>
    public static void 重置()
    {
        上次抽卡 = 0;
        等待出卡 = false;
    }
}

/// <summary>抽卡：手上没牌才抽。</summary>
public class AST_Draw : ISlotResolver
{
    private static uint 技能 => SpellIds.取("星极抽卡");

    public int Check()
    {
        if (!HealQt.GetQt("抽卡", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        // ★ 主判断：**手上还有牌就别抽**（卡面读得到时完全按卡面走）。
        //   抽卡是 55 秒 CD 的充能技，抽了不出等于白等一轮；
        //   原来的"抽了还没出"二值状态做不到这一点 —— 它不知道手上到底几张。
        var 手上有牌 = JobApiHelper.手牌数 > 0;

        if (手上有牌)
        {
            // 手牌还在 ⇒ 保险丝立刻松开，等出卡把它放出去
            AST卡牌状态.等待出卡 = false;
        }
        else
        {
            // ★ 兜底（卡面读不到时才有意义）：刚抽过还在等出卡就别再抽。
            //   ⚠️ 用 `Environment.TickCount64` 而不是 `TimeHelper.Now()` ——
            //      它和下面的限流同一个时基，混用两个时基会算错间隔。
            if (AST卡牌状态.等待出卡
                && Environment.TickCount64 - AST卡牌状态.上次抽卡 < AST卡牌状态.等待超时)
            {
                return -7;
            }
        }

        // ⚠️ 而且必须用"当前形态"：星极抽卡（37017）和灵极抽卡会被游戏互相替换
        //    （共享 55 秒 CD）。死认 37017 的话，形态已经是灵极了我还在点星极，
        //    游戏一律拒绝，看起来就是"一直在点这个技能"。
        var 当前 = SpellUtil.当前形态(技能);
        if (当前 == null || !当前.IsReadyWithCanCast()) return -1;

        // 限流：万一可用性判断失灵，也不至于每帧刷
        if (Environment.TickCount64 - AST卡牌状态.上次抽卡 < 3000) return -6;

        return 3;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;

        slot.Add(spell);
        AST卡牌状态.上次抽卡 = Environment.TickCount64;
        AST卡牌状态.等待出卡 = true;
    }
}

/// <summary>
/// 出卡：**按卡面选槽、按卡面选目标**，近战卡给近战，其余给远程。
///
/// [!] 槽位由卡面决定，不能死认一个槽（原来恒取「出卡 III」✗）：
///     手上有牌时 `CheckActionChange` 会把对应的槽换成那张卡，
///     其余槽保持槽位本身 ⇒ "形态 ≠ 槽位 id"就说明这个槽里有卡。
///
/// [!] 目标也不能只按"近战/远程 + 血最少"发（原来就是这样 ✗）：
///     六张卡的收益差得很远 —— 树要避开已有减伤、塔要避开已有盾、
///     箭优先给开了秘策的学者、瓶优先给缺血的治疗。
///     规则统一放在 <see cref="占星卡目标"/>。
/// </summary>
public class AST_Play : ISlotResolver
{
    /// <summary>战斗开始后这么久内不出卡（开场先让占卜落地）</summary>
    private const long 开场闸门毫秒 = 5000;

    /// <summary>Check 里挑好的目标，给 Build 用（避免判 A 放 B）</summary>
    private static IBattleChara? 当前目标;

    /// <summary>这一次出的是哪张卡（卡面）</summary>
    private static CardType 当前卡面;

    public int Check()
    {
        当前目标 = null;
        当前卡面 = CardType.None;

        if (!HealQt.GetQt("抽卡", true)) return -101;

        var 槽 = 占星卡.按卡面选槽();
        if (槽 == 0) return -1;

        // 走"当前形态"：出卡 I/II/III 在有牌后会变成
        // 太阳神之衡 / 战争神之枪 / 世界树之干 …，
        // 所以必须顺着槽位取形态，不能死认出卡 I/II/III 的 id。
        var 当前 = SpellUtil.当前形态(槽);
        if (当前 == null || !当前.IsReadyWithCanCast()) return -1;

        // 卡面：优先读手牌；读不到就用"槽位换出来的形态"反推
        var 卡面 = 占星卡.第一张();
        if (卡面 == CardType.None) 卡面 = 占星卡.按技能反推卡面(当前.Id);
        if (卡面 == CardType.None) return -1;

        // 战斗刚开始时不出卡（占卜还没落地，卡先出会白白错过团辅窗口）
        try
        {
            if ((AI.Instance?.BattleData?.CurrBattleTimeInMs ?? 0) < 开场闸门毫秒) return -2;
        }
        catch { }

        // ★ 同一张卡不叠发：候选里已经有这张卡的人**不参与挑选**。
        //   于是"别人还空着"→ 自动换人；"所有人都挂着"→ 挑不出人 ⇒ 这次不发。
        //   ⚠️ 必须传卡面（`CardType`）而不是 buff id —— 避开的人取决于**手上的牌**，
        //      不是"目标身上有什么"。
        var 目标 = 占星卡目标.选目标(卡面, HealSettings.Instance.卡牌去重);

        if (目标 == null) return -1;

        当前目标 = 目标;
        当前卡面 = 卡面;

        return 4;
    }

    public void Build(Slot slot)
    {
        if (当前目标 == null || 当前卡面 == CardType.None) return;

        var 槽 = 占星卡.按卡面选槽();
        if (槽 == 0) return;

        var spell = SpellUtil.当前形态(槽);
        if (spell == null) return;

        slot.Add(new Spell(spell.Id, 当前目标));

        // 出掉了 ⇒ 保险丝松开（卡面数据回来前也允许下一轮抽卡）
        AST卡牌状态.等待出卡 = false;
    }
}

/// <summary>小奥秘卡：把手牌转成大阿卡纳备用。</summary>
public class AST_MinorArcana : ISlotResolver
{
    private static uint 技能 => SpellIds.取("小奥秘卡");

    public int Check()
    {

        // ★ 2026-10-04：**三 id 去重** —— 对照实现在放小奥秘卡前查
        //   `RecentlyUsed(37022 | 7444 | 7445, 5000)`，我们原来一次都没查 ✗
        //   ⇒ 同一次王冠卡可能被连放（小奥秘卡本体 / 领主 / 贵妇 三条路各放一次）✗
        try
        {
            if (AEAssist.Helper.SpellExtension.RecentlyUsed(37022, 5000)
                || AEAssist.Helper.SpellExtension.RecentlyUsed(SpellIds.取("王冠之领主"), 5000)
                || AEAssist.Helper.SpellExtension.RecentlyUsed(SpellIds.取("王冠之贵妇"), 5000))
                return -6;
        }
        catch { }
        if (!HealQt.GetQt("抽卡", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 只在"还是小奥秘卡"的时候按。满足条件后它会变成 王冠之贵妇 / 王冠之领主，
        // 那时候是 AST_CrownPlay 的活，这里不能再触发。
        var 当前 = SpellUtil.当前形态(技能);
        if (当前 == null || 当前.Id != 技能) return -1;
        if (!当前.IsReadyWithCanCast()) return -1;
        if (!CharacterExt.可以插能力技()) return -6;

        return 2;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>大阿卡纳：有人缺血当贵妇，否则当领主丢敌人。</summary>
public class AST_CrownPlay : ISlotResolver
{
    private static uint 贵妇 => SpellIds.取("王冠之贵妇");
    private static uint 领主 => SpellIds.取("王冠之领主");

    /// <summary>王冠卡是从"小奥秘卡"这个技能位变出来的，所以要顺着它拿形态</summary>
    private static uint 小奥秘卡 => SpellIds.取("小奥秘卡");

    /// <summary>Check 里选好的卡，给 Build 用</summary>
    private static Spell? 当前卡;

    private static long 上次快照;

    /// <summary>
    /// 状态快照：每 5 秒把所有关键状态打一行。
    /// 之前只埋了"拿不到卡"一个点，结果日志里什么都没出现 ——
    /// 说明 Check 在到达那里之前就返回了，等于没观测到。
    /// 现在把各分支依赖的状态全摊开，一次就能看出卡在哪。
    /// </summary>
    public static void 快照(string 环节)
    {
        var 现在 = AEAssist.Helper.TimeHelper.Now();
        if (现在 - 上次快照 < 5000) return;
        上次快照 = 现在;

        try
        {
            var 形态 = SpellUtil.当前形态(小奥秘卡);
            var 占卜 = SpellIds.取("占卜");
            var 占卜形态 = SpellUtil.当前形态(占卜);
            var 神谕 = SpellIds.取("神谕");

            LogHelper.Info(
                "[HealerACR] 占星诊断(" + 环节 + ")：" +
                "抽卡QT=" + HealQt.GetQt("抽卡", true) + " 占卜QT=" + HealQt.GetQt("占卜", true) +
                " | 小奥秘卡" + 小奥秘卡 + " 形态=" + (形态 == null ? "null" : 形态.Id.ToString()) +
                " 可用=" + (形态 != null && 形态.IsReadyWithCanCast()) +
                " | 贵妇" + 贵妇 + " 解锁=" + SpellUtil.已解锁(贵妇) + " 可用=" + SpellUtil.可用(贵妇) +
                " | 领主" + 领主 + " 解锁=" + SpellUtil.已解锁(领主) + " 可用=" + SpellUtil.可用(领主) +
                " | 占卜" + 占卜 + " 解锁=" + SpellUtil.已解锁(占卜) +
                " 形态=" + (占卜形态 == null ? "null" : 占卜形态.Id.ToString()) +
                " 可用=" + (占卜形态 != null && 占卜形态.IsReadyWithCanCast()) +
                " | 神谕" + 神谕 + " 可用=" + SpellUtil.可用(神谕) +
                " | 目标=" + (HealTargetHelper.当前目标() == null ? "无" : "有") +
                " GCD=" + GCDHelper.GetGCDCooldown());
        }
        catch (Exception e)
        {
            LogHelper.Error("[HealerACR] 占星诊断本身出错了：" + e.Message);
        }
    }

    /// <summary>诊断：同一个原因只报一次，避免刷屏</summary>
    private static readonly HashSet<string> 已报 = new(StringComparer.Ordinal);

    private static void 诊断(string 原因)
    {
        if (已报.Add(原因))
        {
            LogHelper.Info($"[HealerACR] 王冠卡没打出去：{原因}");
        }
    }

    public int Check()
    {

        // ★ 2026-10-04：**三 id 去重** —— 对照实现在放小奥秘卡前查
        //   `RecentlyUsed(37022 | 7444 | 7445, 5000)`，我们原来一次都没查 ✗
        //   ⇒ 同一次王冠卡可能被连放（小奥秘卡本体 / 领主 / 贵妇 三条路各放一次）✗
        try
        {
            if (AEAssist.Helper.SpellExtension.RecentlyUsed(37022, 5000)
                || AEAssist.Helper.SpellExtension.RecentlyUsed(SpellIds.取("王冠之领主"), 5000)
                || AEAssist.Helper.SpellExtension.RecentlyUsed(SpellIds.取("王冠之贵妇"), 5000))
                return -6;
        }
        catch { }
        快照("王冠");

        if (!HealQt.GetQt("抽卡", true)) return -101;
        if (小奥秘卡 == 0 || (贵妇 == 0 && 领主 == 0)) return -3;

        // 两条路都试：
        //   1. 小奥秘卡的"当前形态"（正常情况下游戏会把它变成贵妇/领主）
        //   2. 直接看王冠卡技能本身可不可用（万一 CheckActionChange 不认卡牌这类替换）
        var 卡 = SpellUtil.当前形态(小奥秘卡);
        if (卡 != null && 卡.Id == 小奥秘卡) 卡 = null;
        if (卡 != null && !卡.IsReadyWithCanCast()) 卡 = null;

        if (卡 == null)
        {
            var a = SpellUtil.Get(贵妇);
            if (a != null && a.IsReadyWithCanCast()) 卡 = a;
        }

        if (卡 == null)
        {
            var b = SpellUtil.Get(领主);
            if (b != null && b.IsReadyWithCanCast()) 卡 = b;
        }

        if (卡 == null)
        {
            诊断($"拿不到卡（小奥秘卡{小奥秘卡} 贵妇{贵妇} 领主{领主}，" +
                 $"形态={SpellUtil.当前形态(小奥秘卡)?.Id.ToString() ?? "null"}）");
            return -1;
        }

        // ★ 2026-10-04：**开场 5 秒内不用王冠卡** ——
        //   它是一张 60 秒 CD 的大牌，开场那一拍应该留给占卜和地星。
        //   （对照实现的小奥秘卡第一件事就是这个闸门）
        try
        {
            if ((AI.Instance?.BattleData?.CurrBattleTimeInMs ?? 0) < 5000) return -200;
        }
        catch { }

        var 阈值 = 治疗阈值表.取(卡.Id, HealSettings.Instance.单体治疗阈值);

        if (卡.Id == 贵妇) return 贵妇判定(阈值);
        if (卡.Id == 领主) { 当前卡 = 卡; return 领主判定(); }

        诊断($"卡面不认识（id={卡.Id}）");
        return -1;
    }

    /// <summary>
    /// 贵妇（单体治疗卡）的完整条件网。
    ///
    /// [!] 四条闸门，任何一条不过就**留着下次**（不是换个弱条件发出去）：
    ///       ① 地星在场 ⇒ 不发（两份群/单治疗撞同一拍，地星白铺）
    ///       ② 有人血低于阈值 ⇒ **直接发**（救人优先，后面两条不看）
    ///       ③ 20 米内敌人 < 3 ⇒ 不发（怪这么少，治疗压力用常规手段就够）
    ///       ④ 没有血线告急的坦克 ⇒ 不发（贵妇是单体卡，得有个明确的对象）
    /// </summary>
    private static int 贵妇判定(float 阈值)
    {
        if (HealTargetHelper.木桩模式) return -300;

        // ① 地星在场，别撞车
        if (占星卡目标.地星在场()) return -8;

        // ② 真有人缺血：发
        if (HealTargetHelper.低于阈值人数(阈值, 20f) > 0) return 3;

        // ③ 怪太少：不值得
        if (占星卡目标.近处敌人数量() < 3) return -5;

        // ④ 没有血线告急的坦克：不值得
        if (!占星卡目标.有血量危险的坦克(阈值)) return -5;

        return 4;
    }

    /// <summary>
    /// 领主（范围伤害卡）的完整条件网。
    ///
    /// [!] 两条闸门：
    ///       ① 没有敌人 / 敌人正在消失 ⇒ 不发
    ///       ② 敌人正好 3 个 ⇒ 不发（3 个时收益最低，留着打更多或更少的时候）
    ///       ≥6 个敌人时优先级最高 —— 一发打满。
    /// </summary>
    private static int 领主判定()
    {
        var 当前 = HealTargetHelper.当前目标();
        if (当前 != null && 当前.对象有效() && 当前.CurrentHp <= 0) return -1;

        var 敌数 = 占星卡目标.近处敌人数量();

        if (敌数 <= 0) return -2;

        // 正好 3 个是收益最低的一档，留着
        if (敌数 == 3) return -200;

        // 6 个以上打满，优先级最高
        if (敌数 >= 6) return 22;

        if (占星卡目标.有坦克死刑(2000)) return -13;   // 死刑马上来，先别交输出卡

        return 15;
    }

    public void Build(Slot slot)
    {
        var 卡 = 当前卡 ?? SpellUtil.当前形态(小奥秘卡);
        if (卡 == null) return;

        if (卡.Id == 贵妇)
        {
            var 缺血 = HealTargetHelper.最低血量队友(治疗阈值表.取(卡.Id, HealSettings.Instance.单体治疗阈值));
            if (缺血 == null) return;
            slot.Add(new Spell(卡.Id, 缺血));
            return;
        }

        // 领主：自身周围 AOE，不需要目标
        slot.Add(卡);
    }
}

/// <summary>地星：放下去 10 秒后炸，所以时间轴预报到伤害就提前铺。</summary>
public class AST_EarthlyStar : ISlotResolver
{
    private static uint 技能 => SpellIds.取("地星");

    public int Check()
    {
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("地星", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 木桩：直接放（练循环用）。
        // 原来这里一句 return -300 把木桩整个拦掉了，
        // 所以木桩上永远看不到地星。
        if (HealTargetHelper.木桩模式) return SpellUtil.可用(技能) ? 6 : -1;

        // 有时间轴：提前铺（地星 10 秒后才炸）
        if (TimelineManager.未来有减伤(HealSettings.Instance.地星提前秒, 2.0))
        {
            return SpellUtil.可用(技能) ? 6 : -1;
        }

        // 没时间轴兜底：多人掉血就直接放
        var s = HealSettings.Instance;
        if (HealTargetHelper.低于阈值人数(s.群体治疗阈值, 20f) >= HealTargetHelper.群疗能力技人数要求(s.群奶最少人数))
        {
            return SpellUtil.可用(技能) ? 6 : -1;
        }

        return -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;
            // 地面技能选位：敌人站得稳就放它脚下，否则放自己脚下
            // （参考同类 ACR 的敌人移动检测）
            var 落点 = 敌人移动检测.地面技能位置();
            slot.Add(new Spell(spell.Id, 落点));
    }
}

/// <summary>光速：瞬发窗口。</summary>
public class AST_Lightspeed : ISlotResolver
{
    private static uint 技能 => SpellIds.取("光速");

    /// <summary>上次按光速的时间（它有 2 层充能，要防连按）</summary>
    private static long 上次光速;

    public int Check()
    {
        if (!HealQt.GetQt("光速", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        // ⚠️ 光速有 **2 层充能**，所以 buff 检查必须在所有分支之前。
        //    之前木桩分支写在它前面，导致木桩上"可用就放"，两次充能一口气全交。
        if (AuraIds.光速 != 0 && CharacterExt.我有光环(AuraIds.光速)) return -3;

        // 充能判断（参考同类 ACR 的 GetCharges）：
        //   光速是 2 层充能技，**满层时不用就浪费**，所以满层时缩短限流尽快交掉；
        //   只剩 1 层时保持较长限流，留给真正需要的场合。读不到充能就退回原来的 2.5 秒。
        var 充能 = CharacterExt.充能数(技能);
        var 限流 = 充能 >= 2 ? 500 : 2500;
        if (TimeHelper.Now() - 上次光速 < 限流) return -7;

        // 木桩也要放（练循环用）
        if (HealTargetHelper.木桩模式)
        {
            上次光速 = TimeHelper.Now();
            return SpellUtil.可用(技能) ? 2 : -1;
        }

        if (HealTargetHelper.低于阈值人数(HealSettings.Instance.单体治疗阈值) == 0) return -1;

        上次光速 = TimeHelper.Now();
        return SpellUtil.可用(技能) ? 2 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>占卜：团辅。木桩卡 CD 放。</summary>
public class AST_Divination : ISlotResolver
{
    private static uint 技能 => SpellIds.取("占卜");

    public int Check()
    {
        AST_CrownPlay.快照("占卜");

        if (!HealQt.GetQt("占卜", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 注意：占卜是**以自己为中心**的团辅，不需要选中目标 ——
        // 之前这里写了"没目标就不放"，木桩上就容易一直不触发。
        if (!CharacterExt.可以插能力技()) return -6;

        // ⚠️ 占卜在 92 级后会变成"神谕"（37029）—— 同一个技能位的形态，
        //    和 闪灼→闪飒、星极→灵极 是一回事。
        //    硬放 16552 会被游戏拒绝，所以一律走当前形态。
        var 当前 = SpellUtil.当前形态(技能);
        if (当前 == null || !当前.IsReadyWithCanCast())
        {
            // 兜底：万一 CheckActionChange 不认这个替换，就直接看"神谕"能不能用
            var 神谕 = SpellIds.取("神谕");
            if (神谕 == 0) return -1;

            var s = SpellUtil.Get(神谕);
            if (s == null || !s.IsReadyWithCanCast()) return -1;
            当前 = s;
        }

        if (!HealTargetHelper.木桩模式)
        {
            if (HealTargetHelper.目标快死了()) return -4;
            if (HealSettings.Instance.时间轴攒资源 && TimelineManager.未来有减伤(8.0)) return -5;
        }

        return 2;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>
/// 星体爆轰（8324）—— **地星的引爆**。
///
/// ★ 2026-10-04 新增：原来**完全没有这个 resolver** ✗
///   地星（7439）放下去之后没有任何东西会引爆它 ⇒ **地星的实际价值 ≈ 0** ✗
///   （对照实现里 地星 与 星体爆轰 是两个独立 resolver ✓）
///
/// 判据（保守版，只做"能炸就炸"）：
///   · 可用(8324) 本身就编码了"地星已放下 + 满足引爆条件" ✓
///   · 2.5 秒内刚炸过就不再炸（防连按，与对照实现的 RecentlyUsed(8324,2500) 一致）✓
///   · 木桩模式让路 ✓
/// </summary>
public class AST_StarDetonation : ISlotResolver
{
    private static uint 技能 => SpellIds.取("星体爆轰");

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        try
        {
            if (AEAssist.Helper.SpellExtension.RecentlyUsed(技能, 2500)) return -7;
        }
        catch { }

        // ★ 2026-10-04 修（补深度发现）：**必须等到「巨星主宰」(1248) 才引爆** ✗
        //   地星放下后先是「地星主宰」(1224)，约 10 秒后才长大成「巨星主宰」(1248)，
        //   两者的伤害与盾量差一档（星体破裂 540 → 星体爆炸 720；盾 5% → 10%）✗
        //   原来只判「可用(8324)」⇒ 一放下就炸 ⇒ 恒吃低档 ✗
        //   对照实现的两条按血线引爆分支也全部锁在 1248 之后 ✓
        if (!CharacterExt.我有光环(1248)) return -1;

        return SpellUtil.可用(技能) ? 8 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>
/// 占星「擢升」(25873) —— **给队友**的单体减伤。
///
/// ★ 2026-10-04 新增：原来它被塞在 `个人减伤` 槽位里 ✗
///   由 Res_SelfMitigation 消费 ⇒ **只用在自己身上** ⇒ 永远给不到坦克 ✗
///   （对照实现里它是坦克死刑预判 / 双阈值选人 / 自己血低才给自己的独立解析器 ✓）
///
/// 判据：
///   · 坦克优先：队伍里有效血量比例最低、且低于「擢升」登记阈值（0.75）的坦克 ✓
///   · 没有这样的坦克时，**自己**低于同一阈值才给自己 ✓
///   · 都没有 ⇒ 不交（不浪费 60 秒 CD）✓
/// </summary>
public class AST_AllyMitigation : ISlotResolver
{
    private static uint 技能 => SpellIds.取("擢升");

    /// <summary>选目标：坦克优先，其次自己（都要过阈值）。</summary>
    private static IBattleChara? 选目标()
    {
        try
        {
            var 阈值 = 治疗阈值表.取(技能, HealSettings.Instance.单体治疗阈值);

            var 坦克 = HealTargetHelper.血量最低的坦克(阈值);
            if (坦克 != null && 坦克.对象有效() && 坦克.活着() && !坦克.处于假死状态())
                return 坦克;

            // 没有该治的坦克 ⇒ 自己血低才给自己
            var 我 = AEAssist.Core.Me;
            if (我 != null && 我.对象有效() && 我.活着() && 我.有效血量比例() <= 阈值)
                return 我;
        }
        catch { }
        return null;
    }

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("减伤", true)) return -100;
        if (!HealSettings.Instance.自动减伤) return -101;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        if (选目标() == null) return -1;

        return SpellUtil.可用(技能) ? 14 : -1;
    }

    public void Build(Slot slot)
    {
        var 目标 = 选目标();
        if (目标 == null) return;

        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(new Spell(spell.Id, 目标));
    }
}
