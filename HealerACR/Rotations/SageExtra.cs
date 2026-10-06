using AEAssist.CombatRoutine;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Rotations;

// ============================================================================
//  贤者的输出技能（0.3.6 补）。
//
//  这两个之前一直没实现 —— SpellIds 表里有 ID，但没有任何 resolver 引用它们。
// ============================================================================

/// <summary>
/// 发炎（Phlegma）：贤者的 **GCD 输出技**，对目标造成伤害并治疗周围队友。
///
/// ⚠️ 它有 **2 层充能、40 秒复唱**（从游戏 Action 表 dump 出来的 Confirmed 数据），
///    所以既要防"同一瞬间把两层全交"，又不能间隔太长 —— 不然充能会白白溢出。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 按参考实现（`发炎.txt`）补齐的完整闸门网（表 #108 / #109）★
///
///  [!] 原来只有：QT + 目标 + 一个按充能的粗限流 + 残血跳过 ✗
///      缺了：**四个 QT 分层**（强制发炎 / 保留发炎 / 攒爆发 / 倾泻资源）、
///      **爆发药窗口**、**起手门**、**AOE 智能换目标**、无充能时的溢出判据。
///
///  ── 闸门（顺序即参考的 IL 顺序）──
///    ① QT：**只认 `发炎`**；`发炎` 关着 ⇒ 直接 -100
///       （IL 出处：`.il\cls_youshu\ACR.Sage.Resolvers.GCD.发炎.txt:14-21`
///        —— `ldstr "发炎"` → `GetQt` → `brfalse` 跳过 `ldc.i4.s -100`/`ret`；
///        该分支体**不读** `强制发炎`）
///       `强制发炎` 是**另一条独立 resolver**（IL 同名文件），
///       槽序在发炎之前、判定更宽（不查充能/药窗），见 `SGE_PhlegmaForce`
///    ② 目标 + 打上去有用 + 6 米内且没死
///    ③ 技能可用（视线/射程）
///    ④ `保留发炎` 且**没在倾泻资源**，且充能 ≤ 1 ⇒ 留着（-9）
///    ⑤ **攒爆发中**且没在倾泻 ⇒ 留着（-30）
///    ⑥ **倾泻资源 ⇒ 无条件交**（返回 50）
///    ⑦ **起手门**：`战斗时间 ≤ 起手发炎延迟 × 2500ms` ⇒ 不交（-8）
///       —— 发炎是"卡 CD 交"的技能，起手那几拍该先铺毒/群攻
///    ⑧ 战斗 ≤ 5 秒且目标 &gt; 6 米 ⇒ 不交（-8）—— 太远，走过去再说
///    ⑨ **AOE 智能换目标**：以当前目标为锚找能覆盖 ≥ 门槛的圆形落点
///       （半径 5 米；门槛按形态分：单体发炎 2 / 群体发炎 3）
///       换到了 ⇒ **返回 20**（比普通那条高）
///    ⑩ **爆发药窗口**：身上有强化药(49)、剩余在 **3~28 秒**之间 ⇒ 返回 15
///       —— 药还在生效，这一发打在窗口里最赚
///    ⑪ 充能 &gt; 1.9（= 满 2 层）⇒ 返回 10（防溢出）
///    ⑫ **移动中**：开了 `保留发炎` 就不交（-9），没开则返回 7
///    ⑬ 站着、充能不满 ⇒ 不交（-1）—— 等充能，别把最后一层随手花掉
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class SGE_Phlegma : ISlotResolver
{
    /// <summary>发炎 III / II / I 的等级链（82 / 72 / 26 级）</summary>
    private static uint 技能 => SpellUtil.取已解锁(
        SpellIds.取("发炎III"), SpellIds.取("发炎II"), SpellIds.取("发炎"));

    /// <summary>群体发炎的技能 id（形态换出来的那个）</summary>
    private static uint 群体发炎Id => SpellIds.取("发炎II");

    /// <summary>Check 里选好的落点，给 Build 用（避免判 A 放 B）</summary>
    private static IBattleChara? 本帧目标;

    /// <summary>起手门的时间单位（参考口径：`起手发炎延迟 × 2500` 毫秒）</summary>
    private const int 起手单位毫秒 = 2500;

    /// <summary>战斗开始这么久内、目标又超过 6 米 ⇒ 先别交</summary>
    private const long 近战距离门槛毫秒 = 5000;
    private const float 近战距离 = 6f;

    /// <summary>爆发药窗口的上下界（毫秒）</summary>
    private const int 药窗口下限 = 3000;
    private const int 药窗口上限 = 28000;

    public int Check()
    {
        本帧目标 = null;

        // ① QT 门：**`发炎` 关着就不放**（方向已按 IL 修正 —— 原来写成
        //    `!发炎 && !强制发炎`，等于"两个都关才放"，恰好写反；表 #108 的
        //    「发炎关 **或** 强制发炎开 ⇒ -100」其实是**两条 resolver 各自的**门）。
        //    IL 出处：`.il\cls_youshu\ACR.Sage.Resolvers.GCD.发炎.txt:14-21`。
        if (!HealQt.GetQt("发炎", true)) return -100;
        if (技能 == 0 || !SpellUtil.已解锁(技能)) return -2;

        // ② 目标
        var 目标 = 输出目标.选();
        if (目标 == null || !目标.对象有效()) return -1;
        if (敌人状态.攻击无效(目标)) return -1;
        if (目标.CurrentHp <= 0) return -2;

        var 距离 = 0f;
        try { 距离 = 目标.Distance(Core.Me!); } catch { }
        if (距离 > 近战距离) return -2;

        // ③ 技能可用
        var spell = SpellUtil.当前形态(技能);
        if (spell == null || !spell.IsReadyWithCanCast()) return -3;

        var 充能 = CharacterExt.充能数(技能);

        // ④ 保留发炎（且没在倾泻、且充能 ≤ 1）⇒ 留着
        if (HealQt.GetQt("保留发炎", false) && !倾泻资源中() && 充能 <= 1) return -9;

        // ⑤ 攒爆发中 ⇒ 留着
        if (占卜临近() && !倾泻资源中()) return -30;

        // ⑥ 倾泻资源 ⇒ 无条件交
        if (倾泻资源中())
        {
            本帧目标 = 目标;
            return 50;
        }

        // ⑦ 起手门
        var 战斗时间 = 战斗毫秒();
        var 起手门 = (long)Math.Max(0, HealSettings.Instance.起手发炎延迟) * 起手单位毫秒;
        if (战斗时间 <= 起手门) return -8;

        // ⑧ 开场 5 秒内且目标太远 ⇒ 先别交
        if (战斗时间 <= 近战距离门槛毫秒 && 距离 > 近战距离) return -8;

        // ⑨ AOE 智能换目标（门槛按单体/群体分档）
        var 是群体形态 = spell.Id == 群体发炎Id;
        var 落点门槛 = 是群体形态 ? 3 : 2;
        try
        {
            var 落点 = 智能选目标.圆形最优(5f, 落点门槛);
            if (落点 != null && 落点.对象有效()
                && HealTargetHelper.自身周围敌人数量(5f) >= 落点门槛)
            {
                本帧目标 = 落点;
                return 20;
            }
        }
        catch { }

        // ⑩ 爆发药窗口
        try
        {
            var 药剩 = (int)AuraIds.强化药剩余毫秒();
            if (药剩 >= 药窗口下限 && 药剩 <= 药窗口上限)
            {
                本帧目标 = 目标;
                return 15;
            }
        }
        catch { }

        // ⑪ 充能满 2 层 ⇒ 交（防溢出）
        if (充能 > 1.9f)
        {
            本帧目标 = 目标;
            return 10;
        }

        // ⑫ 移动中
        if (SpellUtil.在移动())
        {
            本帧目标 = 目标;
            return HealQt.GetQt("保留发炎", false) ? -9 : 7;
        }

        // ⑬ 站着、充能不满 ⇒ 等充能
        return -1;
    }

    public void Build(Slot slot)
    {
        var 目标 = 本帧目标;
        if (目标 == null || !目标.对象有效()) return;

        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;

        slot.Add(new Spell(spell.Id, 目标));
    }

    /// <summary>当前战斗时间（毫秒；拿不到返回 0）</summary>
    private static long 战斗毫秒()
    {
        try { return AI.Instance?.BattleData?.CurrBattleTimeInMs ?? 0; }
        catch { return 0; }
    }

    /// <summary>占卜 CD &lt; 8 秒 = 攒爆发窗口（与 `SGE_Toxikon` 同口径）</summary>
    private static bool 占卜临近()
    {
        try
        {
            var 占卜 = SpellIds.取("占卜");
            if (占卜 == 0) return false;

            var s = SpellUtil.Get(占卜);
            if (s == null) return false;

            return s.Cooldown.TotalMilliseconds < 8000;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>倾泻资源中（用「一键爆发」开关当判据）</summary>
    private static bool 倾泻资源中()
    {
        try { return HealQt.GetQt("一键爆发", false); }
        catch { return false; }
    }
}

/// <summary>
/// **强制发炎**（独立 resolver，表 #108）。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么要有这么一条（原来是错的）★
///
///  [!] 参考实现里 `强制发炎` 是**一条自己的 resolver**
///      （IL 文件：`.il\cls_youshu\ACR.Sage.Resolvers.GCD.强制发炎.txt`，
///       槽序见 `ACR.Sage.Resolvers.Strategy.贤者技能策略.txt`：第 20 位，
///       **在 `发炎`（第 26 位）之前**）。
///      我们原来把它的开关**并进了 `SGE_Phlegma` 的那一行门**里 ——
///      两个开关同开时会照打，而参考里「强制发炎开」本身并不代表 `发炎` 也开。
///
///  [!] 现在按 IL 拆回独立判定（槽序仍在 `发炎` 之前，所以"强制"确实更优先）。
///
///  ── IL 逐条（出处：`ACR.Sage.Resolvers.GCD.强制发炎.txt`）──
///    ⓘ 上界：本类**不查充能、不查爆发药窗口、不查起手门** ——
///      这正是它和 `SGE_Phlegma` 的全部差别（那些闸门只在 `发炎.txt` 里）。
///    ① `!GetQt("强制发炎")` ⇒ **-100**（IL_0022-0030）
///    ② 技能不可用 ⇒ 放过（IL_00a2-00c0：`GetAdjustedActionId(24289)` + `IsReady`）
///       24289 = `SpellIds.取("发炎")`（谱面基准技）
///    ③ 没有目标 / 攻击无效 ⇒ **-1**（IL_003a-004c）
///    ④ 目标为空、或 距离 &gt; 6 、或濒死 ⇒ **-200**（IL_004d-0086）
///    ⑤ `攒爆发` 开且 `倾泻资源` 关 ⇒ **-30**（IL_0087-00a1）
///    ⑥ `倾泻资源` 开 ⇒ **50**（IL_00c1-00d6）
///    ⑦ 战斗 ≤ 5000ms 且目标 &gt; 6 米 ⇒ **-9**（IL_00d7-0103）
///    ⑧ 选目标：`智能AOE目标` 关 ⇒ 当前目标；开 ⇒ 圆形落点，门槛
///       `GetAdjustedActionId(24289) == 24307 ? 3 : 2`（24307 = `发炎II`）
///       （IL_0104-011f 与 `SelectTarget` IL_0000-004a）
///    ⑨ 落点选中（`useSmartTarget`）⇒ **20**；否则 **10**（IL_0120-012d）
///
///  [!] 「IL 无法确定」标注两处（**不猜**）：
///       · `SelectCircularAoeTarget` 的第 4 参（IL 的 `ldc.i4.1`）语义未知，
///         我们沿用项目里 `SGE_Phlegma` 的同名调用形态 `圆形最优(5f, 门槛)`
///         —— **半径 5 米与 IL 字面的 6 只能二者取一，取项目一致的那个**；
///       · IL 用 `TargetHelper.GetNearbyEnemyCount` 复核，我们用
///         `HealTargetHelper.自身周围敌人数量`（项目统一的安全实现，口径相同）。
///
///  ⚠ 返回值语义：负数 = 不放，正数 = 放行；**返回值不参与仲裁**
///    ⇒ 所有"让路"都写成 `return -1`（照 IL，不用别的负数表示让路）。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class SGE_PhlegmaForce : ISlotResolver
{
    /// <summary>发炎 III / II / I 的等级链（与 `SGE_Phlegma` 同一口径）</summary>
    private static uint 技能 => SpellUtil.取已解锁(
        SpellIds.取("发炎III"), SpellIds.取("发炎II"), SpellIds.取("发炎"));

    /// <summary>Check 里选好的落点，给 Build 用（避免判 A 放 B）</summary>
    private static IBattleChara? 本帧目标;

    /// <summary>近战距离门槛（IL_005c 的 `ldc.r4 6`）</summary>
    private const float 近战距离 = 6f;

    /// <summary>开场这么久的距离门（IL_00e6 的 `ldc.i4 5000`，毫秒）</summary>
    private const long 近战距离门槛毫秒 = 5000;

    /// <summary>群体形态（发炎II）的 AOE 门槛；单体形态是 2（IL_013a-013b）</summary>
    private const int 群体落点门槛 = 3;

    public int Check()
    {
        本帧目标 = null;

        // ① 本 resolver 只管自己的开关（IL_0022-0030）
        if (!HealQt.GetQt("强制发炎", false)) return -100;

        var 技能Id = 技能;
        if (技能Id == 0 || !SpellUtil.已解锁(技能Id)) return -2;

        // ② 目标
        var 目标 = 输出目标.选();
        if (目标 == null || !目标.对象有效()) return -1;
        if (敌人状态.攻击无效(目标)) return -1;

        var 距离 = 0f;
        try { 距离 = 目标.Distance(Core.Me!); } catch { }
        if (目标.CurrentHp <= 0) return -200;
        if (距离 > 近战距离) return -200;

        // ③ 技能可用（不查充能 —— 这是「强制」的定义）
        var spell = SpellUtil.当前形态(技能Id);
        if (spell == null || !spell.IsReadyWithCanCast()) return -1;

        // ④ 攒爆发 ⇒ 留着
        if (HealQt.GetQt("攒爆发", false) && !倾泻资源中()) return -30;

        // ⑤ 倾泻资源 ⇒ 无条件交
        if (倾泻资源中())
        {
            本帧目标 = 目标;
            return 50;
        }

        // ⑥ 开场 5 秒内且目标太远 ⇒ 先别交
        if (战斗毫秒() <= 近战距离门槛毫秒 && 距离 > 近战距离) return -9;

        // ⑦ AOE 智能换目标（门槛按形态分档）
        var 是群体形态 = spell.Id == SpellIds.取("发炎II");
        var 落点门槛 = 是群体形态 ? 群体落点门槛 : 2;
        var 用智能落点 = false;
        try
        {
            // 参考的 `智能AOE目标` 开关在本项目**没有同名设置**
            // （`HealSettings.cs` 里只有 `AOE`，:197）—— 这里用它当代理判据，
            // 语义一致（"允许 AOE 目标优化"）；**没有新增设置项**。
            if (HealSettings.Instance.AOE)
            {
                var 落点 = 智能选目标.圆形最优(5f, 落点门槛);
                if (落点 != null && 落点.对象有效()
                    && HealTargetHelper.自身周围敌人数量(5f) >= 落点门槛)
                {
                    本帧目标 = 落点;
                    用智能落点 = true;
                }
            }
        }
        catch { }

        if (用智能落点) return 20;

        本帧目标 = 目标;
        return 10;
    }

    public void Build(Slot slot)
    {
        var 目标 = 本帧目标;
        if (目标 == null || !目标.对象有效()) return;

        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;

        slot.Add(new Spell(spell.Id, 目标));
    }

    /// <summary>当前战斗时间（毫秒；拿不到返回 0）</summary>
    private static long 战斗毫秒()
    {
        try { return AI.Instance?.BattleData?.CurrBattleTimeInMs ?? 0; }
        catch { return 0; }
    }

    /// <summary>倾泻资源中（与 `SGE_Phlegma` 同口径：用「一键爆发」开关当判据）</summary>
    private static bool 倾泻资源中()
    {
        try { return HealQt.GetQt("一键爆发", false); }
        catch { return false; }
    }
}

/// <summary>
/// 心神风息（Psyche，92 级 / 37033）：**能力技**，对目标造成伤害并给周围队友上盾。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 按参考实现（`心神风息.txt`）补齐的完整闸门网（表 #127）★
///
///  [!] 原来只有：QT「输出」+ 有目标 + 能插能力技 + 残血跳过 ✗
///      缺了：**独立 QT**、视线与射程、攒爆发/倾泻资源两档、
///      **起手门**、**AOE 智能换目标**、**爆发药窗口**。
///
///  ── 闸门（顺序即参考的 IL 顺序）──
///    ① 自己的 QT「心神风息」（参考是独立开关，不复用「输出」）
///    ② CD 没好 ⇒ -1
///    ③ 静默中 ⇒ -1
///    ④ 目标：打上去有用 + 25 米内且没死
///    ⑤ oGCD 深度闸门 `CanUseOffGcd(2)`
///    ⑥ **攒爆发中**（占卜临近）且没在倾泻 ⇒ 留着（-30）
///    ⑦ **倾泻资源 ⇒ 无条件交**（返回 50）
///    ⑧ **起手门**：`战斗时间 ≤ 起手心神延迟 × 2500ms` ⇒ 不交（-8）
///    ⑨ **AOE 智能换目标**：以当前目标为锚找能覆盖 ≥2 个敌人的圆形落点
///       （半径 5 米）⇒ 换到了返回 **20**
///    ⑩ **爆发药窗口**：身上有强化药(49) 且**剩余 &gt; 0** ⇒ 返回 **15**
///       （参考只查"药还在"，不像发炎那样卡 3~28 秒）
///    ⑪ 其余 ⇒ -1（**不是 20** —— 没换目标、没药的时候它排在后面）
///
///  [!] 返回值差异值得记一笔：参考这条**没有"普通情况就交"**那一档 ——
///      只有"换到 AOE 落点(20)"和"药窗口里(15)"两种情况会真的交，
///      其余一律 -1。它是个**伤害型 oGCD**，不打在窗口里就是浪费。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class SGE_Psyche : ISlotResolver
{
    private static uint 技能 => SpellIds.取("心神风息");

    /// <summary>Check 里选好的落点，给 Build 用（判 A 放 A）</summary>
    private static IBattleChara? 本帧目标;

    /// <summary>落点半径与门槛（参考：圆形 5 米、至少 2 个敌人）</summary>
    private const float 落点半径 = 5f;
    private const int 落点门槛 = 2;

    /// <summary>起手门的单位（参考口径 `起手心神延迟 × 2500` 毫秒）</summary>
    private const int 起手单位毫秒 = 2500;

    public int Check()
    {
        本帧目标 = null;

        if (!HealQt.GetQt("心神风息", true)) return -100;
        if (!SpellUtil.已解锁(技能)) return -2;
        if (!SpellUtil.可用(技能)) return -1;

        // ④ 目标与视线
        var 目标 = 输出目标.选();
        if (目标 == null || !目标.对象有效()) return -1;
        if (敌人状态.攻击无效(目标)) return -1;
        if (目标.CurrentHp <= 0) return -200;

        try
        {
            if (目标.Distance(Core.Me!) > 25f) return -200;
        }
        catch { }

        // ⑤ oGCD 深度闸门
        if (!OffGcd闸门.可以排(2)) return -4;

        // ⑥ 攒爆发中 ⇒ 留着
        if (占卜临近() && !倾泻资源中()) return -30;

        // ⑦ 倾泻资源 ⇒ 无条件交
        if (倾泻资源中())
        {
            本帧目标 = 目标;
            return 50;
        }

        // ⑧ 起手门
        var 起手门 = (long)Math.Max(0, HealSettings.Instance.起手心神延迟) * 起手单位毫秒;
        if (战斗毫秒() <= 起手门) return -8;

        // ⑨ AOE 智能换目标
        try
        {
            var 落点 = 智能选目标.圆形最优(落点半径, 落点门槛);
            if (落点 != null && 落点.对象有效()
                && HealTargetHelper.自身周围敌人数量(落点半径) >= 落点门槛)
            {
                本帧目标 = 落点;
                return 20;
            }
        }
        catch { }

        // ⑩ 爆发药窗口（只查"药还在"，参考没卡上下界）
        try
        {
            if (CharacterExt.我有光环(AuraIds.强化药))
            {
                本帧目标 = 目标;
                return 15;
            }
        }
        catch { }

        return -1;
    }

    public void Build(Slot slot)
    {
        var 目标 = 本帧目标;
        if (目标 == null || !目标.对象有效()) return;

        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;

        slot.Add(new Spell(spell.Id, 目标));
    }

    /// <summary>当前战斗时间（毫秒；拿不到返回 0）</summary>
    private static long 战斗毫秒()
    {
        try { return AI.Instance?.BattleData?.CurrBattleTimeInMs ?? 0; }
        catch { return 0; }
    }

    /// <summary>占卜 CD &lt; 8 秒 = 攒爆发窗口（与 `SGE_Toxikon` / `SGE_Phlegma` 同口径）</summary>
    private static bool 占卜临近()
    {
        try
        {
            var 占卜 = SpellIds.取("占卜");
            if (占卜 == 0) return false;

            var s = SpellUtil.Get(占卜);
            if (s == null) return false;

            return s.Cooldown.TotalMilliseconds < 8000;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>倾泻资源中（用「一键爆发」开关当判据）</summary>
    private static bool 倾泻资源中()
    {
        try { return HealQt.GetQt("一键爆发", false); }
        catch { return false; }
    }
}

/// <summary>
/// 智慧之爱（Lv100）—— 贤者的群疗大招。
///
/// 数据（dump_actions.tsv）：id=37035，Lv100，CD 180s，
///   CastType=2（圆形，目标中心），Range=0（自身），EffectRange=30。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 按参考实现（`智慧之爱.txt`）逐条对齐（表 #126）★
///
///  [!] 原来错在四处：
///      ① 半径写 **20 米** —— 参考是 **30 米**（技能自己的 EffectRange）✗
///         ⇒ 20 米外的人算不进来，该交的时候不交。
///      ② 阈值读 `大招血线`（全局）—— 参考读**本技能登记的**
///         `智慧之爱阈值`（我们表里 = 0.70）✗
///      ③ **没有去重** —— 参考查 4 个技能 `RecentlyUsed(x, 2000)`：
///           寄生清汁 24299 / 自生 24288 / 消化 24302 / 输血 24305
///         任一在 2 秒内用过 ⇒ 不交 ✗（同帧连着倒大招）
///      ④ 参考还有"身上已有这三种 buff 之一就别交"：
///           3898 / 2620 / 3899
///         （它们是智慧之爱自己挂的那几条，重复交纯浪费）
///
///  [!] 人数门槛用 `群奶人数要求(群奶最少人数)`（参考是 `Clamp(群奶人数,0,8)`），
///      而不是写死 2 —— 四人本/八人本的期望人数本来就不同。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class SGE_Philosophia : ISlotResolver
{
    private static uint 技能 => SpellIds.取("智慧之爱");

    /// <summary>技能自己的范围（`dump_actions.tsv`：EffectRange=30）</summary>
    private const float 半径 = 30f;

    /// <summary>去重窗口（参考口径 2000ms）</summary>
    private const int 去重毫秒 = 2000;

    /// <summary>参考取查的四个"刚用过就别交"的技能</summary>
    private static readonly string[] 去重技能名 = { "寄生清汁", "自生", "消化", "输血" };

    /// <summary>智慧之爱自己挂的三种 buff（有就别重复交）</summary>
    private static readonly uint[] 已有BuffIds = { 3898, 2620, 3899 };

    public int Check()
    {
        if (技能 == 0) return -101;
        if (!HealQt.GetQt("奶人", true)) return -100;
        if (!HealQt.GetQt("群奶", true)) return -101;     // 属于群奶范畴
        if (!SpellUtil.已解锁(技能)) return -2;
        if (!SpellUtil.可用(技能)) return -6;             // 参考：CD 没好直接 -6

        // ★ oGCD 队列深度闸门（参考口径 `CanUseOffGcd(2)` —— 智慧之爱在连发池里）
        if (!OffGcd闸门.可以排(2)) return -4;

        // ③ 去重：2 秒内用过这四个中的任何一个 ⇒ 不交
        try
        {
            foreach (var 名 in 去重技能名)
            {
                var id = SpellIds.取(名);
                if (id != 0 && AEAssist.Helper.SpellExtension.RecentlyUsed(id, 去重毫秒)) return -5;
            }
        }
        catch { }

        // ④ 身上已有智慧之爱自己挂的 buff ⇒ 不交
        try
        {
            foreach (var id in 已有BuffIds)
            {
                if (CharacterExt.我有光环(id)) return -8;
            }
        }
        catch { }

        // ①② 阈值**按技能查**（我们表里 = 0.70），半径 **30 米**
        var 阈值 = 治疗阈值表.取(技能, HealSettings.Instance.大招血线);
        var 人数要求 = HealTargetHelper.群奶人数要求(HealSettings.Instance.群奶最少人数);
        if (HealTargetHelper.低于阈值人数(阈值, 半径) < 人数要求) return -1;

        if (低蓝停手()) return -5;

        return 2;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);   // 以自身为中心
    }

    private static bool 低蓝停手()
    {
        try { return HealerACR.Common.蓝量.低蓝停手(); } catch { return false; }
    }
}