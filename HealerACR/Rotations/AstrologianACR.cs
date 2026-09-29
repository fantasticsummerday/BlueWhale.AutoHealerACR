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

    /// <summary>先天禀赋：瞬发、不读条的单体治疗（血量越低效果越强）。</summary>
    public override uint 瞬发单奶能力技 => SpellIds.取("先天禀赋");

    /// <summary>
    /// 先天禀赋的血线：它是"血量越低效果越强"的急救型，
    /// 所以给 **45%**（和学者的活性法同档），别拉到 75% 浪费。
    /// </summary>
    public override float 瞬发单奶血线 => 0.45f;
    public override uint 群体治疗能力技 => SpellIds.取("天星冲日");
    public override uint 团队减伤 => SpellIds.取("中间学派");
    public override uint 个人减伤 => SpellIds.取("擢升");

    public override uint 复活 => SpellIds.取("生辰");
    public override uint 驱散 => SpellsDefine.Esuna;
    public override uint 醒梦 => SpellsDefine.LucidDreaming;

    // 需求 4：脱战先抽张牌，开战就能出
    public override uint[] 脱战准备技能 => new[] { SpellIds.取("星极抽卡") };
}

public class ASTRotationEntry : HealerEntryBase
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
            new SlotResolverData(new Res_HealAoEGcd(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealSingleGcd(_spells), SlotMode.Gcd),
            // AOE 优先：3 个以上敌人时不该先给单只怪挂 DoT
            new SlotResolverData(new Res_AoEDamage(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_Dot(_spells), SlotMode.Gcd),
            // ⚠️ 位置很重要：必须在 `Res_MoveGcd`（移动填充）**之前** ——
            //   药尾声补刀最该生效的场景就是**移动中**（读条放不出来），
            //   排在移动填充后面等于**永远选不到**。
            new SlotResolverData(new Res_PotionTailDamage(_spells), SlotMode.Gcd),
            // ★ 多目标 DoT：主目标 DoT 还在时，把 DoT 扩散到**其他被拉到的怪** ★
            //   ⚠️ 必须在 Res_MoveGcd 之前（否则永远抢不到这个 GCD）
            //   ⚠️ 必须在 Res_Dot 之后（主目标的 DoT 优先级更高）
            new SlotResolverData(new Res_MultiDot(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_MoveGcd(_spells), SlotMode.Gcd),            new SlotResolverData(new Res_BaseDamage(_spells), SlotMode.Gcd),

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
            new SlotResolverData(new Res_SelfMitigation(_spells), SlotMode.OffGcd),
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
/// 占星的卡牌状态。
///
/// 为什么需要它：JobApi 的 DrawnCards 在实机上读不到，所以"手上有几张牌"
/// 只能自己记账 —— 抽一次记一笔，出一次销一笔。这样即使技能可用性判断
/// 失灵（比如 AEAssist 不检查手牌条件），也不会出现"抽个不停"。
/// </summary>
public static class AST卡牌状态
{
    /// <summary>上一次抽卡的时间戳</summary>
    public static long 上次抽卡;

    /// <summary>抽了但还没出 —— 这时候不该再抽</summary>
    public static bool 等待出卡;

    /// <summary>超过这个时间没出掉就放弃等待（防止卡死再也不抽）</summary>
    public const long 等待超时 = 20000;
}

/// <summary>抽卡。</summary>
public class AST_Draw : ISlotResolver
{
    private static uint 技能 => SpellIds.取("星极抽卡");

    public int Check()
    {
        if (!HealQt.GetQt("抽卡", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 抽了还没出就别再抽（等出卡，超时自动放弃）
        if (AST卡牌状态.等待出卡 && TimeHelper.Now() - AST卡牌状态.上次抽卡 < AST卡牌状态.等待超时)
        {
            return -7;
        }

        // 不依赖 JobApi 的手牌数 —— 实机测试发现它一直返回空，
        // 结果就是"抽卡停不下来、出卡永远不出"。
        //
        // ⚠️ 而且必须用"当前形态"：星极抽卡（37017）和灵极抽卡会被游戏互相替换
        //    （共享 55 秒 CD）。死认 37017 的话，形态已经是灵极了我还在点星极，
        //    游戏一律拒绝，看起来就是"一直在点这个技能"。
        var 当前 = SpellUtil.当前形态(技能);
        if (当前 == null || !当前.IsReadyWithCanCast()) return -1;

        // 限流：万一可用性判断失灵，也不至于每帧刷
        if (TimeHelper.Now() - AST卡牌状态.上次抽卡 < 3000) return -6;

        return 3;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;

        slot.Add(spell);
        AST卡牌状态.上次抽卡 = TimeHelper.Now();
        AST卡牌状态.等待出卡 = true;
    }
}

/// <summary>出卡：近战卡给近战，其余给远程。</summary>
public class AST_Play : ISlotResolver
{
    private static uint 技能 => SpellUtil.取已解锁(
        SpellIds.取("出卡III"), SpellIds.取("出卡II"), SpellIds.取("出卡I"));

    public int Check()
    {
        if (!HealQt.GetQt("抽卡", true)) return -101;
        if (技能 == 0) return -2;

        // 同上：没牌时 Play 会被游戏禁用，"可用"就等于"手上确实有牌"。
        // 而且出卡 I/II/III 在有牌后会变成 战争神之枪 / 世界树之干 / 河流神之瓶，
        // 所以必须走"当前形态"，不能死认出卡I/II/III 的 id。
        var 当前 = SpellUtil.当前形态(技能);
        if (当前 == null || !当前.IsReadyWithCanCast()) return -1;

        if (出卡目标() == null) return -1;

        return 4;
    }

    public void Build(Slot slot)
    {
        var 目标 = 出卡目标();
        if (目标 == null) return;

        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;

        slot.Add(new Spell(spell.Id, 目标));

        // 出掉了，销账 —— 下次可以继续抽
        AST卡牌状态.等待出卡 = false;
    }

    private static IBattleChara? 出卡目标()
    {
        var 任意Dps = PartyHelper.CastableDps.OrderBy(r => r.血量比例()).FirstOrDefault();
        var 坦克 = PartyHelper.CastableTanks.FirstOrDefault();

        // 需求：按设置决定优先给近战还是远程
        if (JobApiHelper.有近战卡() == HealSettings.Instance.出卡优先近战)
        {
            return PartyHelper.CastableMelees.OrderBy(r => r.血量比例()).FirstOrDefault() ?? 任意Dps ?? 坦克;
        }

        return PartyHelper.CastableRangeds.OrderBy(r => r.血量比例()).FirstOrDefault() ?? 任意Dps ?? 坦克;
    }
}

/// <summary>小奥秘卡：把手牌转成大阿卡纳备用。</summary>
public class AST_MinorArcana : ISlotResolver
{
    private static uint 技能 => SpellIds.取("小奥秘卡");

    public int Check()
    {
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

        // 贵妇是治疗卡：没人缺血就先留着。
        // 领主**不要**检查目标 —— 它是"对自身周围敌人"的范围攻击，选中与否都能打。
        if (卡.Id == 贵妇 &&
            HealTargetHelper.最低血量队友(HealSettings.Instance.单体治疗阈值) == null)
        {
            return -1;
        }

        当前卡 = 卡;
        return 3;
    }

    public void Build(Slot slot)
    {
        var 卡 = 当前卡 ?? SpellUtil.当前形态(小奥秘卡);
        if (卡 == null) return;

        if (卡.Id == 贵妇)
        {
            var 缺血 = HealTargetHelper.最低血量队友(HealSettings.Instance.单体治疗阈值);
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
        if (HealTargetHelper.低于阈值人数(s.群体治疗阈值, 20f) >= Math.Max(1, s.群奶最少人数 - 1))
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
        if (AuraIds.光速 != 0 && Core.Me.HasAura(AuraIds.光速)) return -3;

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
