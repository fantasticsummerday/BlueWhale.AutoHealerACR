using AEAssist;
using AEAssist.Helper;
using AEAssist.MemoryApi;

namespace HealerACR.Common;

/// <summary>
/// 技能可用性诊断 —— 回答"这个技能**为什么**放不出去"。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么需要它 ★
///
///    本项目最耗时的排查就是"条件全 True 但技能不出"。
///    `开发约定.md` 第三节专门写了排查顺序，但前几步都靠肉眼读 buff。
///
///    官方 API 文档的《调试技巧》给了一条正路：
///      "检查 SpellHelper.CanUseAction() 了解为什么不能使用技能"
///    —— 那个 API 在旧版（WinForms）里，我们引用的 AEAssist.NET 1.2.16
///       已经没有它了，但**同族的两个还在**，而且更有用：
///
///      · `CheckActionInRangeOrLoS(id, target)` → 射程 / 视线
///      · `CheckActionResourceAndConditions(id)` → 蓝量 / 资源 / 前置条件
///      两者内部都调游戏原生的 `CheckActionCanUse`，**是游戏自己的判断**，
///      比我们猜准得多。
///
///  ★ ⚠️ 绝对不要用的一个 API：`MemApiSpell.CanCast(id, chara)` ★
///
///    名字看起来像"能不能放"，但读 IL 后确认它**只做两件事**：
///        ① Action 表里有没有这一行
///        ② 传进去的角色是不是 null
///    然后**永远返回 true**。它不检查射程、不检查视线、不检查资源。
///    **拿它当判断依据 = 一个恒真的条件**（开发约定 E 节那类翻车的标准形态）。
///    要判"能不能放"用 `SpellUtil.可用()`（`IsReadyWithCanCast`），
///    要判"为什么不能放"用本类。
///
///  ★ 为什么做成"按需诊断"而不是每帧检查 ★
///
///    内部是原生函数指针调用 + Excel 查表，**不适合每帧跑**。
///    所以：只在"某技能连续 N 秒被选中却没放出去"时才调一次，
///    并带节流（同一技能 10 秒内只报一次）。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 技能诊断
{
    /// <summary>同一技能的诊断最短间隔（毫秒）</summary>
    private const int 节流毫秒 = 10000;

    private static readonly Dictionary<uint, long> _上次诊断 = new();

    /// <summary>
    /// 这个技能现在"游戏自己认为"能不能放。
    ///
    /// 返回 null 表示**判断不了**（拿不到 API / 异常）—— 调用方据此退回原逻辑，
    /// 绝不能因为"诊断失败"就去改行为。
    /// </summary>
    public static bool? 游戏认为可用(uint 技能Id, IGameObject? 目标 = null)
    {
        if (技能Id == 0) return null;

        try
        {
            var api = Core.Resolve<MemApiSpell>();

            // ① 资源 / 前置条件（蓝量、量谱、buff 前置…）
            if (!api.CheckActionResourceAndConditions(技能Id)) return false;

            // ② 射程 / 视线
            //    ⚠️ 传 null 时它内部会用当前目标；所以我们**显式传目标**，
            //       避免它去用玩家手动选的目标（那可能和我们想放的目标不是同一个）。
            if (目标 != null && !api.CheckActionInRangeOrLoS(技能Id, 目标)) return false;

            return true;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把"为什么不能放"写成一句人能读的话。
    ///
    /// **只输出事实，不猜** —— 分不清是哪一项时就直说分不清，
    /// 免得像"错误归因的注释"那样误导下一个看日志的人。
    /// </summary>
    public static string 解释(uint 技能Id, IGameObject? 目标 = null)
    {
        if (技能Id == 0) return "技能 id 为 0（SpellIds 表里没查到？）";

        var 名 = SpellIds.反查(技能Id);
        var 标签 = string.IsNullOrEmpty(名) ? 技能Id.ToString() : $"{技能Id}({名})";

        try
        {
            var api = Core.Resolve<MemApiSpell>();

            // 按"排查顺序"逐项报，和 开发约定.md 第三节一致
            if (!SpellUtil.已解锁(技能Id)) return $"{标签}：未解锁（等级/职业任务不够）";

            if (!api.CheckActionResourceAndConditions(技能Id))
                return $"{标签}：**资源/前置条件不足**（蓝量、量谱、或需要的前置 buff 没有）";

            if (目标 != null && !api.CheckActionInRangeOrLoS(技能Id, 目标))
                return $"{标签}：**目标不在射程内或被视线挡住**";

            if (!SpellUtil.可用(技能Id))
                return $"{标签}：游戏数据说条件都满足，但 IsReadyWithCanCast() = false" +
                       "（CD 中 / 形态不对 / 需要目标却没传）";

            return $"{标签}：各项检查都通过 —— 问题不在技能本身，看调用方的 Check 分支";
        }
        catch (Exception e)
        {
            return $"{标签}：诊断失败（{e.Message}）—— 语义未确认，不要据此改判断";
        }
    }

    /// <summary>
    /// 节流版诊断：同一技能 10 秒内只打一条日志。
    ///
    /// 给"被选中却一直没放出去"的地方用（这类日志会每帧触发，必须节流）。
    /// </summary>
    public static void 报告一次(uint 技能Id, IGameObject? 目标 = null)
    {
        try
        {
            var 现在 = TimeHelper.Now();
            if (_上次诊断.TryGetValue(技能Id, out var 上次) && 现在 - 上次 < 节流毫秒) return;
            _上次诊断[技能Id] = 现在;

            LogHelper.Info("[HealerACR] 技能诊断 → " + 解释(技能Id, 目标));
        }
        catch { }
    }

    /// <summary>战斗重置 / 换本时清（配合 OnResetBattle / OnTerritoryChanged）</summary>
    public static void 重置() => _上次诊断.Clear();

    /// <summary>
    /// 自检：`MemApiSpell.CanCast` **不是**可用性判断。
    ///
    /// 留着这个方法是为了让"我们曾经差点误用它"这件事**留在代码里**，
    /// 而不是只写在注释里。读 IL 确认过：它只检查
    /// "Action 表有没有这一行" + "角色是否为 null"，然后恒返回 true。
    ///
    /// @return 恒为 false —— 提醒调用方"这个 API 没有判断能力"。
    /// </summary>
    [Obsolete("CanCast 只查表里有没有这行，恒为 true，不能当可用性判断；请用 SpellUtil.可用() 或 技能诊断.游戏认为可用()", true)]
    public static bool CanCast是可用性判断() => false;
}
