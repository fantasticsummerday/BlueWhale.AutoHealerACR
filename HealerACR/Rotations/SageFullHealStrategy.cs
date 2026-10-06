using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using HealerACR.Common;

namespace HealerACR.Rotations;

/// <summary>
/// 贤者「**奶满策略**」（复刻 youshu `ACR.Sage.Resolvers.Strategy.贤者奶满策略`）。
///
/// ══════════════════════════════════════════════════════════════════════
///  ★ 与「必须奶满」机制的分工 ★
///    · `Common\必须奶满.cs` = **四职业共用**的致死机制（Doom 类）路径，语义与实现**未改一行**；
///    · 本类 = 参考里的 **Strategy**：一套"把目标奶满"的**技能优先级序列**，
///      由它自己的 `StartCheck()` 决定何时生效。两者共存、互不削弱。
///
///  ★ 为什么走 `构建策略序列()` 而不是 `构建爆发轴()` ★
///    `构建爆发轴()` 里注册的序列**只在勾了「一键爆发」时才走**（`爆发轴.cs:95-96` 先判总开关），
///    而参考的 Strategy **独立于爆发** ⇒ 走 `HealerEntryBase.构建策略序列()`
///    （Round 46 新增，提交 `823962f`，**不受「一键爆发」门控**）。
///
///  ★ 为什么必须整条 override `StartCheck()` ★
///    `爆发轴基类.StartCheck()` 第一句就是 `if (!GetQt("一键爆发", false)) return -1;`
///    ⇒ 不复写就会变成"**只有开一键爆发才奶满**" ✗
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public class 贤者奶满策略 : 爆发轴基类
{
    public 贤者奶满策略(JobSpellTable 表) : base(表) { }

    /// <summary>
    /// 参考 `:: int StartCheck()`（IL 逐条直读，含分支方向）：
    /// <code>
    /// IL_0000: HasSkillSilenceStatus()  ; IL_0005 brtrue → -1
    /// IL_0007: SageSettings.高难模式
    /// IL_0011: ldc.i4.1 ; IL_0012 bne.un IL_0016
    /// IL_0014: ldc.i4.m1 ; ret          ← **高难模式 == 1 ⇒ -1（不生效）**
    /// IL_0016: FindTarget() ; IL_0025 brtrue → IL_0029
    /// IL_0027: -1 ; ret                 ← 目标为空 ⇒ -1
    /// IL_0029: ldc.i4.1 ; ret           ← 否则 +1
    /// </code>
    ///
    /// [!] ⚠️ **反直觉**：它是**日随向**策略 —— `高难模式 == 1` 时**不生效**，
    ///     不是"高难才奶满"。（两处材料对此自相矛盾，已按 IL 的 `bne.un` 方向定论。）
    /// </summary>
    public override int StartCheck()
    {
        try
        {
            // ① 参考 IL_0000~0005：`HealerRuntimeTools::HasSkillSilenceStatus()` 真 → -1
            //    ⚠️ **未复刻**：本项目**没有沉默/静默查询 API**
            //       —— 与 `AstrologianACR.cs:1793` 对同一条的处置一致（照标，不造假判据）。

            // ② 参考 IL_0007~0015：`高难模式 == 1` ⇒ -1
            //    ⚠️ 本项目**不做「高难模式」开关**（`HealSettings.cs:537-541` 明确写着）
            //       ⇒ 用既有的 `减伤Helper.是高难本()` 当代理（与占星「神谕」那处同口径）。
            if (减伤Helper.是高难本()) return -1;

            // ③ 参考 IL_0016~0028：`FindTarget()` 为空 ⇒ -1；否则 +1
            //    `HealerFullHealTargetSelector::FindTarget` 的**内部实现 IL 无法确定**
            //    ⇒ 映射到本项目的 `必须奶满.找目标()`（同义：**必须（被）奶满的那个目标**）。
            if (必须奶满.找目标() == null) return -1;

            return 1;
        }
        catch
        {
            return -1;   // 读失败 ⇒ 不启动（安全侧：不抢技能）
        }
    }

    /// <summary>
    /// 基类的子类判据（抽象成员，必须实现）。
    ///
    /// [!] 本类把 <see cref="StartCheck"/> **整条 override 掉了**（因为基类那份自带
    ///     「一键爆发」门）⇒ 这个成员**不在本类的生效路径上**；返回 1 即可。
    /// </summary>
    protected override int 子类开始判据() => 1;

    /// <summary>
    /// 参考 `:: static void BuildHealSlot(Slot slot)` —— **8 步优先级表**。
    ///
    /// [!] 形态：**每步命中即 `Add` + `ret`**（第 8 步除外）⇒ **顺序即优先级**
    ///     （本框架只有队列行号参与仲裁，"先加进去"就是"优先"）。
    ///     正因为"一次调用最多只加一个技能"，这里实现成**一个 Action**（整条序列一步走完），
    ///     而不是 8 个 Sequence 步骤 —— 后者会跨帧执行，与参考的"当帧命中即返回"不同。
    ///
    /// [!] 常量与判据照 IL（每步已标 `IL_00xx`）；「IL 无法确定」的 Helper 内部实现照标。
    /// </summary>
    protected override void 构建()
    {
        Sequence.Add(slot =>
        {
            try
            {
                // ── 前置门（参考 IL_0000~0018）──
                //   `QT「奶人」` 假 **或** `IsTargetInRange(...)` 假 ⇒ 什么都不加
                //   [!] `IsTargetInRange` 内部实现 IL 无法确定 ⇒ 用"有奶满目标"作等价前置
                //       （没有目标就无从谈"在射程内"）。
                if (!HealQt.GetQt("奶人")) return;
                if (必须奶满.找目标() == null) return;

                var 我 = AEAssist.Core.Me;
                if (我 == null || !我.对象有效()) return;

                // ── 第 1 步（IL_0019~005e）：智慧之爱 37035 ──
                if (!我.HasAura(3898)
                    && !最近放过(37035, 2000)
                    && 可用(37035))
                {
                    slot.Add(CharacterExt.能力技(37035));
                    return;
                }

                // ── 第 2 步（IL_005f~00da）：自生 24288 / 自生II 24302 ──
                if (!我.HasAura(2617) && !我.HasAura(2620)
                    && !最近放过(24288, 2000) && !最近放过(24302, 2000)
                    && CharacterExt.我的等级() >= 60)
                {
                    var 自生 = 可用(24288) ? 24288u : (可用(24302) ? 24302u : 0u);
                    if (自生 != 0) { slot.Add(CharacterExt.能力技(自生)); return; }
                }

                // ── 第 3 步（IL_00db~0116）：贤炮 24318 —— **GCD**，不能带 DontUseGcd ──
                if (HealQt.GetQt("贤炮", true)
                    && SpellUtil.移动中可用(24318)
                    && 可用(24318))
                {
                    slot.Add(new Spell(24318, SpellTargetType.Self));
                    return;
                }

                // ── 第 4 步（IL_0117~0156）：白牛清汁 24303（带最低血目标）──
                if (JobApiHelper.蛇胆 > 0)
                {
                    var 目标 = 最低血目标(24303);
                    if (目标 != null && 可用(24303))
                    {
                        slot.Add(CharacterExt.能力技(24303, 目标));
                        return;
                    }
                }

                // ── 第 5 步（IL_0157~018b）：活化 24300 ──
                if (!我.HasAura(2611) && 可用(24300))
                {
                    slot.Add(CharacterExt.能力技(24300));
                    return;
                }

                // ── 第 6 步（IL_018c~01b6）：寄生清汁 24299 ──
                if (JobApiHelper.蛇胆 > 0 && 可用(24299))
                {
                    slot.Add(CharacterExt.能力技(24299));
                    return;
                }

                // ── 第 7 步（IL_01b7~01f6）：灵橡清汁 24296（带最低血目标）──
                if (JobApiHelper.蛇胆 > 0)
                {
                    var 目标 = 最低血目标(24296);
                    if (目标 != null && 可用(24296))
                    {
                        slot.Add(CharacterExt.能力技(24296, 目标));
                        return;
                    }
                }

                // ── 第 8 步（IL_01f7~0226）：预后 24286 —— **GCD 兜底；这一支不提前 ret** ──
                if (SpellUtil.移动中可用(24286) && 可用(24286))
                    slot.Add(new Spell(24286, SpellTargetType.Self));
            }
            catch { }
        });
    }

    // ==================== 小工具（全部走项目既有入口） ====================

    /// <summary>参考的 `IsReady(id)` ⇒ 项目等价 `SpellUtil.可用(id)`</summary>
    private static bool 可用(uint id)
    {
        try { return id != 0 && SpellUtil.可用(id); } catch { return false; }
    }

    /// <summary>
    /// 参考的 `ActionHelper::RecentlyUsed(id, ms)` ⇒ 用**同族** `SpellExtension.RecentlyUsed`
    /// （参考 IL 直接调 `SpellHistoryHelper::RecentlyUsed`；`本地施放记录.刚放过` 只认 `记()` 写过的字典）。
    /// </summary>
    private static bool 最近放过(uint id, int 毫秒)
    {
        try { return id != 0 && AEAssist.Helper.SpellExtension.RecentlyUsed(id, 毫秒); }
        catch { return false; }
    }

    /// <summary>
    /// 参考的 `LowestHealTarget(int threshold)`（该校验体 184~225 行**未逐条解**）——
    /// 语义是"**低于阈值里血量最低的那个**"。
    ///
    /// [!] 阈值来源：参考各传一个设置字段（`白牛阈值` / `灵橡阈值`）；本项目**没有这两个设置项**，
    ///     而这两个技能的每技能阈值**已登记在 `治疗阈值表`**（白牛 0.65 / 灵橡 0.50）
    ///     ⇒ 直接按技能取表值（同一个决策只有一份判据，开发约定 F③）。
    /// </summary>
    private static Dalamud.Game.ClientState.Objects.Types.IBattleChara? 最低血目标(uint 技能Id)
    {
        try
        {
            var 血线 = 治疗阈值表.取(技能Id, HealSettings.Instance.单体治疗阈值);
            return HealTargetHelper.最低血量队友(血线);
        }
        catch { return null; }
    }
}
