using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.CombatRoutine.Trigger;
using AEAssist.Extension;
using AEAssist.Helper;
using Dalamud.Bindings.ImGui;

namespace HealerACR.Common;

/// <summary>
/// 爆发轴 —— 对照同类 ACR 用的 `Rotation.AddSlotSequences`。
///
/// ══════════════════════════════════════════════════════════════════
///  官方文档（ACR开发 L55-74）对 SlotSequence 的定义：
///
///    "有时我们希望战斗的某个时间点/条件满足时，
///     接下来的技能**完全按照规划来使用**，而不采用优先级的设计
///     （毕竟优先级决策要判断一堆复杂条件，
///      而这种情况我们只需要判断技能冷却是否满足，
///      接下来的释放完全按照规划走就行）"
///
///  ── 为什么奶妈也需要这个 ──
///
///    优先级队列适合"随机应变"，但**爆发期是有固定套路的**：
///      学者：连环计 → 士气高扬 → 能量吸收×N → 气炎法…
///      白魔：神速咏唱 → 闪飒 → 豪圣…
///    这些顺序不该被优先级逻辑打断。
///
///  ── 关于两个开关的合并（v1.16 起）──
///
///    ⚠️ 原来是两个开关：「爆发轴」和「木桩爆发」。
///       实际用起来很别扭 —— 用户要"打木桩时也开爆发"就得同时勾两个，
///       而且木桩模式的判断散在两处，容易漏。
///
///    现在合并成**一个「一键爆发」**：
///      勾上 = 无论战斗还是木桩，只要条件满足就走爆发轴
///      不勾 = 永远不走
///
///    逻辑简化了，用户也少一个要理解的概念。
///
///  ── 优化过的判断逻辑 ──
///
///    旧的 StartCheck 只看"技能可用 + 有资源"，问题是：
///      · 资源够但队友正在掉血 → 开了爆发又要立刻中断，白开
///      · 爆发到一半资源断了 → 后半段序列空转
///
///    新的判断加了：
///      · **连续性检查**：上一段爆发没走完就不重开
///      · **资源充分度**：按"这条轴要花多少资源"和"现在有多少"比
///      · **血量稳定度**：近几秒队伍血量在回升才开（说明压力过去了）
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public abstract class 爆发轴基类 : ISlotSequence
{
    /// <summary>要在队列里按顺序执行的技能</summary>
    public List<Action<Slot>> Sequence { get; } = new();

    /// <summary>当前走到第几步（-1 = 没开始）</summary>
    protected int 当前步 = -1;

    /// <summary>开始时间（用于超时保护）</summary>
    protected long 开始时间;

    /// <summary>上一次结束的时间（用于"冷却期"判断，避免刚结束又开）</summary>
    private long 上次结束时间;

    /// <summary>整条轴的超时（毫秒）—— 防止卡在某一步出不来</summary>
    protected virtual int 超时毫秒 => 20000;

    /// <summary>两次爆发之间至少间隔多久（毫秒）—— 避免连续重开</summary>
    protected virtual int 最短间隔毫秒 => 8000;

    protected JobSpellTable 表 { get; }

    protected 爆发轴基类(JobSpellTable 表)
    {
        this.表 = 表;
        构建();
    }

    /// <summary>子类在这里往 Sequence 里加技能</summary>
    protected abstract void 构建();

    // ==================== 开始判断 ====================

    /// <summary>
    /// 现在该不该开始这条爆发轴。
    /// 返回 >= 0 表示开始（值本身是优先级，越大越优先）。
    /// </summary>
    public virtual int StartCheck()
    {
        try
        {
            // ① 总开关（合并后的「一键爆发」）
            if (!HealQt.GetQt("一键爆发", false)) return -1;

            // ③ 脱战不开
            if (!Core.Me.InCombat()) return -1;

            // ④ 距上次结束太近 → 不开（避免连续重开导致序列碎片化）
            if (上次结束时间 > 0 && TimeHelper.Now() - 上次结束时间 < 最短间隔毫秒) return -1;

            // ══════════════════════════════════════════════════════════
            //  ★ 去掉了两道血量门槛 ★
            //
            //  ── 现象 ──
            //    "启用一键爆发没有效果，豆子没有全打出去"
            //
            //  ── 去掉的是哪两道 ──
            //    ② `有人濒死() → 不开`（有人 <35% 就不开）
            //    ⑤ `队伍血量在下降() → 不开`
            //
            //  ── 为什么它们让功能**完全失效** ──
            //    在 50 级「究极神兵」这类本里，**全程都有人 <35%**、
            //    血量也一直在波动 —— 于是两条同时成立，
            //    **一次爆发都开不起来**，"一键爆发"等于没有。
            //
            //  ── 为什么去掉是安全的 ──
            //    保命那件事**由 `StopCheck` ③ 负责**：
            //    爆发走到一半有人濒死 → **立刻中断去救人**。
            //    既然中途会让路，开场就没必要因为"现在有人受伤"而拒绝开始 ——
            //    否则"有压力"和"没压力"之间的窗口太窄，永远踩不到。
            //
            //  ⚠️ 保留 ①③④：开关没开 / 脱战 / 刚结束过 ——
            //    这三条是"该不该走这条轴"的**定义**，不是血量判断。
            // ══════════════════════════════════════════════════════════

            // ⑥ 交给子类判断资源/技能条件
            return 子类开始判据();
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>子类实现：什么条件下开这条轴（只看技能和资源，不用重复判血量）</summary>
    protected abstract int 子类开始判据();

    // ==================== 停止判断 ====================

    /// <summary>
    /// 走到第 index 步时该不该停。
    /// 返回 >= 0 表示停止（放弃剩余步骤）。
    /// </summary>
    public virtual int StopCheck(int index)
    {
        try
        {
            // ① 走完了 → 正常结束
            if (index >= Sequence.Count)
            {
                上次结束时间 = TimeHelper.Now();
                return 1;
            }

            // ② 超时保护
            if (开始时间 > 0 && TimeHelper.Now() - 开始时间 > 超时毫秒)
            {
                上次结束时间 = TimeHelper.Now();
                return 2;
            }

            // ③ **有队友濒死 → 立刻中断去救人**
            //    这是奶妈爆发轴和 DPS 最大的区别 ——
            //    输出可以等，人命不能等。
            if (有人濒死())
            {
                上次结束时间 = TimeHelper.Now();
                return 3;
            }

            // ④ 脱战了
            if (!Core.Me.InCombat())
            {
                上次结束时间 = TimeHelper.Now();
                return 4;
            }

            return -1;
        }
        catch
        {
            return 5;
        }
    }

    // ==================== 辅助判断 ====================

    /// <summary>有人掉到危险血线（默认 35%）</summary>
    protected static bool 有人濒死(float 阈值 = 0.35f)
    {
        try { return HealTargetHelper.低于阈值人数(阈值) > 0; }
        catch { return false; }
    }

    // ⚠️ 原来这里还有一个 `队伍血量在下降()`（用"受伤人数"的变化判断趋势），
    //    只被 `StartCheck` 的"血量在下降就不开"用过。
    //    那道门槛已删（见 StartCheck 的说明：它和"有人濒死"一起
    //    让爆发在实战里永远开不起来），这个辅助方法随之失去调用点，一并删除。
    //    保命由 `StopCheck` ③ 负责 —— 爆发中途有人濒死会立刻中断。

    // ==================== 构建辅助 ====================

    /// <summary>加一个 GCD 技能（需要目标）</summary>
    protected void 加Gcd(uint 技能Id)
    {
        if (技能Id == 0) return;

        Sequence.Add(slot =>
        {
            var 目标 = HealTargetHelper.当前目标();
            if (目标 == null) return;

            var spell = SpellUtil.当前形态(技能Id);
            if (spell != null) slot.Add(new Spell(spell.Id, 目标));
        });
    }

    /// <summary>加一个能力技</summary>
    protected void 加能力技(uint 技能Id)
    {
        if (技能Id == 0) return;

        Sequence.Add(slot =>
        {
            var spell = SpellUtil.Get(技能Id);
            if (spell != null) slot.Add(spell);
        });
    }

    /// <summary>加一个对指定队友的能力技</summary>
    protected void 加队友能力技(uint 技能Id, Func<IBattleChara?> 选目标)
    {
        if (技能Id == 0) return;

        Sequence.Add(slot =>
        {
            var 目标 = 选目标();
            if (目标 == null) return;

            var spell = SpellUtil.Get(技能Id);
            if (spell != null) slot.Add(new Spell(spell.Id, 目标));
        });
    }

    // ==================== 爆发药 ====================

    /// <summary>
    /// 爆发药能不能吃（有没有配 / 够不够 / CD 好没好）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 药水 ID 从哪来 —— **不问用户，也不硬编码** ★
    ///
    ///    用 AEAssist 自带的 `ItemHelper.CheckCurrJobPotion(isHq)` ——
    ///    它内部自己去查 `PotionSetting`（用户在 AEAssist 的「爆发药设置」
    ///    里按职业配的，四个奶妈都绑在「意力」药上）。
    ///    我这边只负责"什么时候吃"，不负责"吃什么"。
    ///
    ///  ★ 为什么不自己写 GetPotionId ★
    ///
    ///    `PotionSetting.GetPotionId(Jobs)` 是**实例方法**，
    ///    而公开 API 里没有拿这个实例的入口（反射查过整个程序集：
    ///    `ChoosedPotion` / `Job2Potions` 也都是实例字段）。
    ///    硬去拿实例就等于依赖没公开的实现细节 —— 不如用现成的包装方法。
    ///
    ///  ★ 为什么不自己记时间 ★
    ///
    ///    `CheckCurrJobPotion` 已经含「数量够 + CD 好」，
    ///    而且**不引入任何需要清理的新状态** —— 按开发约定，
    ///    自带状态的模块必须在 OnResetBattle / OnTerritoryChanged
    ///    各加一行清理，少一行就是跨战斗脏数据。
    ///    这里一个状态都不加，也就没有"忘了清"的风险。
    ///
    ///  ★ isHq 为什么先 true 再 false ★
    ///
    ///    药水分 HQ / NQ（同一物品 ID，品质不同）。我**没有**找到
    ///    "用户想用哪种"的可读设置项，所以两种都问一遍 ——
    ///    代价只是多一次判断，好处是**不会因为猜错品质而永远吃不上药**。
    ///    （宁可多问一次，也不要"以为修好了"的静默失效。）
    ///
    ///  ── 和 AEAssist 自身「自动吃药」的关系（重要）──
    ///
    ///    AEAssist 自己也会吃药，受两个设置约束（XML 文档原文）：
    ///      · `NotAutoPotion3`                      = 「副本外不吃爆发药」
    ///      · `NotAutoPotionWithoutHighEndTerritory3` = 「非高难本不吃爆发药」
    ///    用户当前两个都是 false（= 都允许自动吃）。
    ///
    ///    所以这里**不是重复造轮子，而是补"时机"**：
    ///      · AEAssist 管"能不能吃"（受上面两个开关约束）
    ///      · 这里管"在爆发窗口的第一步吃"（这才是爆发药该有的时机）
    ///    两者不会吃两次 —— `CheckCurrJobPotion` 会看 CD，CD 内直接跳过。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    protected static bool 能吃爆发药()
    {
        try
        {
            if (!HealQt.GetQt("爆发药", false)) return false;

            // HQ 优先，没有再试 NQ（见上面 isHq 的说明）
            return ItemHelper.CheckCurrJobPotion(true)
                || ItemHelper.CheckCurrJobPotion(false);
        }
        catch
        {
            return false;   // 拿不到就当没有，绝不影响爆发轴其余部分
        }
    }

    /// <summary>
    /// 把"吃爆发药"加成爆发的第一步。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么放在爆发轴里，而不是新写一个 resolver ★
    ///
    ///    爆发药是**给爆发期服务的**，早了晚了都白吃。而"什么时候开爆发"
    ///    这个判断已经由 `StartCheck()` 做好了（濒死不开 / 血量在掉不开 /
    ///    资源不齐不开），**直接复用就是最准的时机** ——
    ///    再写一套判断只会和它不一致。
    ///
    ///  ★ 和倒计时那条路的关系 ★
    ///
    ///    `预铺起手.InitCountDown` 里也注册了 `AddPotionAction(2000)`，
    ///    那条**只在打 /countdown 时生效**（高难场景）。
    ///    两条路不会重复吃：`CheckCurrJobPotion` 会看 CD，CD 内第二次直接跳过。
    ///    而且两条路共用同一个「爆发药」开关，用户关掉就都不吃。
    ///
    ///  ★ Spell.CreatePotion() 无参 ★
    ///
    ///    签名是从 AEAssist.dll 反射确认过的：`Spell CreatePotion()`，
    ///    没有参数 —— 它内部自己去查 PotionSetting。
    ///    所以**构造失败时返回 null**，这里必须判空，否则空引用进 slot。
    ///
    ///    用法照抄 AEAssist 自己的 `HotKeyResolver_Potion.Run`：
    ///        Check → PotionSetting.GetPotionId + ItemHelper.CheckPotion
    ///        Run   → Spell.CreatePotion() + Slot.Add(...)
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    protected void 使用爆发药()
    {
        Sequence.Add(slot =>
        {
            try
            {
                if (!能吃爆发药()) return;

                var 药 = Spell.CreatePotion();
                if (药 == null) return;

                slot.Add(药);
                LogHelper.Info($"[HealerACR] 爆发轴：吃爆发药（{药.Id}）");
            }
            catch (Exception e)
            {
                // 吃不上药绝不能影响爆发轴剩下的技能
                LogHelper.Info("[HealerACR] 爆发药跳过：" + e.Message);
            }
        });
    }

    /// <summary>重置内部计时（换本时用）</summary>
    public void 重置()
    {
        当前步 = -1;
        开始时间 = 0;
        上次结束时间 = 0;
    }
}

/// <summary>学者的爆发轴：连环计 → 士气高扬 → 能量吸收 → 气炎法</summary>
public class 学者爆发轴 : 爆发轴基类
{
    public 学者爆发轴(JobSpellTable 表) : base(表) { }

    protected override void 构建()
    {
        // ★ 第一步：爆发药（需要「一键爆发」+「爆发药」都开，且药够、CD 好）★
        //   放在最前面 —— 爆发轴的增益要在所有输出之前吃。
        使用爆发药();

        // ⚠️ 连环计用 `加能力技` 排进来 —— 它 CD 没好时那一步会自己跳过，
        //    **不再作为"整条轴开不开"的前提**（见 子类开始判据 的说明）。
        加能力技(SpellIds.取("连环计"));
        加Gcd(表.群体治疗GCD);              // 士气高扬之策
        加能力技(SpellIds.取("能量吸收"));
        加Gcd(表.基础输出);
        加能力技(SpellIds.取("能量吸收"));
        加Gcd(表.基础输出);

        // ⚠️ 第三步卸豆：把**剩下的豆子也打出去**。
        //    原来只排 2 次，留 1 颗在手里 —— 而"一键爆发"的语义就是
        //    "这段时间豆子全换输出"。多排一步，豆子不够时它自己跳过。
        加能力技(SpellIds.取("能量吸收"));
        加Gcd(表.基础输出);
    }

    protected override int 子类开始判据()
    {
        // ══════════════════════════════════════════════════════════════
        //  ★ 不再要求"连环计可用" ★
        //
        //  ── 现象 ──
        //    "启用一键爆发没有效果，豆子没有全打出去"
        //
        //  ── 原因 ──
        //    这里原来第一句就是 `!SpellUtil.可用(连环计) → return -1`，
        //    连环计是 **120 秒 CD** —— 它没转好的绝大部分时间里，
        //    整条轴**根本不开**，于是那两次卸豆也永远不执行。
        //    而卸豆的时机本来是独立的（"有豆子就该换输出"），
        //    和连环计的 CD 没有必然关系。
        //
        //  ── 现在的判据 ──
        //    只要有 **≥1 颗豆子**就开（能卸一颗是一颗）；
        //    连环计那一步 CD 没好会自己跳过（`加能力技` 内部有就绪判断）。
        //
        //  ⚠️ 为什么留 1 颗也能开：`Energy Drain` 吃 1 颗，
        //    资源不足时跳过的是**那一步**，不是整条轴。
        // ══════════════════════════════════════════════════════════════
        var 以太 = JobApiHelper.读得到("以太") ? JobApiHelper.以太 : 0;
        if (以太 < 1) return -1;

        return 8;
    }
}

/// <summary>白魔的爆发轴：神速咏唱 → 闪飒 → 豪圣</summary>
public class 白魔爆发轴 : 爆发轴基类
{
    public 白魔爆发轴(JobSpellTable 表) : base(表) { }

    protected override void 构建()
    {
        // ★ 第一步：爆发药（同 学者爆发轴 的说明）★
        使用爆发药();

        加能力技(SpellIds.取("神速咏唱"));
        加Gcd(表.基础输出);
        加Gcd(表.基础输出);
        加Gcd(表.基础输出);
        加Gcd(表.基础输出);
    }

    protected override int 子类开始判据()
    {
        var 神速 = SpellIds.取("神速咏唱");
        if (神速 == 0 || !SpellUtil.可用(神速)) return -1;

        // 神速是 120 秒 CD，开了就要打满 —— 蓝量不够就别开
        try
        {
            if (蓝量.低蓝停手()) return -1;
        }
        catch { }

        return 8;
    }
}
