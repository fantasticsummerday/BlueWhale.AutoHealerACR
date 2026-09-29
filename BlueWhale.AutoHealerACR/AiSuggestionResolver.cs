using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Helper;
using HealerACR.Common;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// AI 建议执行器 —— **AI 给建议，原有逻辑终审。**
///
/// ═══ 这是整个阶段 B 的安全设计核心 ═══
///
/// AI 的建议**不是命令**，只是一个"插队申请"：
///   1. 它把建议的技能提到队列最前面（最高优先级）
///   2. 但**仍然要过一遍原来的可用性判断**（已解锁 / 可用 / 有目标）
///   3. 任何一项不满足 → 直接放弃，让原来的队列照常跑
///
/// **为什么必须这样**：
///   白名单只能挡住"清单外的 ID"，挡不住"清单内但用错场景"的。
///   比如 AI 建议"以太超流"，ID 完全合法，但**以太已经满了** ——
///   这时候白名单是拦不住的，必须靠原有的 Check 条件。
///
/// **所以 AI 永远不是唯一决策源，它只是"提前插一句嘴"。**
/// </summary>
public class AiSuggestionResolver : ISlotResolver
{
    /// <summary>本帧是否采纳了 AI 建议（给调试面板看）</summary>
    public static bool 本帧采纳 { get; private set; }

    public static uint 本帧技能 { get; private set; }

    /// <summary>本帧没采纳的话，是被哪一条终审拦下的（给统计用）</summary>
    public static string 本帧拦截原因 { get; private set; } = "";

    public int Check()
    {
        本帧采纳 = false;
        本帧技能 = 0;
        本帧拦截原因 = "";

        try
        {
            var s = AiSettings.Instance;
            if (!s.启用决策层) return -1;

            // ⚠️ 这里必须用 当前建议（Peek，只看不动队列）
            //
            //    原来用的是 取建议()（Dequeue，出队）—— 那是个逻辑陷阱：
            //      · Check 出队后如果被更高优先级抢先，Build 不会被调用
            //        → 建议**永久丢失**
            //      · 而且 Build 里再 Peek 时已经是下一条
            //        → **Check 判 A，Build 放 B**
            //
            //    正确做法：Check 只看，Build 放成功了再调 消费()。
            var 建议 = AiDecisionLayer.当前建议;
            if (建议 == null) return -1;

            var id = 建议.技能Id;
            if (id == 0) return -1;

            // ---- 终审第一步：这个技能在当前职业真的存在吗 ----
            if (!SpellUtil.已解锁(id))
            {
                拦截("未解锁（等级/职业任务没到）");
                Ai调试.日志($"建议 {id} 未解锁，放弃（改用原逻辑）");
                return -1;
            }

            // ---- 终审第二步：技能本身可用吗（CD / 形态 / 资源前置）----
            //      这一条就是原 resolver 用的同一套判断。
            //      AI 说"放以太超流"但以太已满 → SpellUtil.可用() 会挡下来吗？
            //      不会 —— 所以还需要各 resolver 自己的资源条件，
            //      这里只能挡住"技能层面"不可用的（CD 中、没学会、形态不对）。
            if (!SpellUtil.可用(id))
            {
                // 这个很常见（AI 建议了一个 CD 中的技能），不打日志免得刷屏
                // ★ 但它是最值得看的一类拦截 ★
                //   "不可用"占比高 = AI 建议的技能 CD 还没转好，
                //   说明预取提前量太大或者提示词没把 CD 状态说清楚。
                拦截("技能不可用（CD中/形态不对）");
                return -1;
            }

            // ---- 终审第三步：需要目标的技能必须有目标 ----
            var 需目标 = 需要目标(id);
            if (需目标 && HealTargetHelper.当前目标() == null)
            {
                拦截("需要目标但没有目标");
                return -1;
            }

            // ---- 终审第三步半：打得到的吗（视线/射程）----
            //   只在"需要目标"的技能上判：治疗类是以自己为原点的，
            //   硬套视线检查会把"柱子后面的队友治不了"变成"不治了" —— 那是致命的。
            if (需目标 && !技能数据.打得到(HealTargetHelper.当前目标()))
            {
                拦截("视线被挡 / 超出射程");
                Ai调试.调试($"建议 {id} 的当前目标被挡住或太远 → 放弃（改用原逻辑）");
                return -1;
            }

            // ══════════════════════════════════════════════════════════
            //  ★ 终审第四步：治疗优先于输出 ★
            //
            //  用户实测指出的问题：
            //    "中断了治疗读条之后，优先继续的是输出，然后再治疗"
            //
            //  原因：AI 建议走的是最高优先级（100），
            //        **它会插到治疗 resolver 前面** ——
            //        于是 AI 一说"接坚石输出"，治疗就被挤到后面去了。
            //
            //  但奶妈的根本原则是治疗优先。所以：
            //    · 有人需要治疗 + AI 建议的是输出 → **让位**（返回 -1，走原队列）
            //    · 其他情况 → 照常采纳
            //
            //  这样 AI 依然能在"没治疗压力"时优化输出，
            //  但绝不会因为它的建议而耽误治疗。
            // ══════════════════════════════════════════════════════════
            if (是输出技能(id) && 有人需要治疗())
            {
                拦截("让位给治疗（AI 建议的是输出）");
                Ai调试.调试($"建议 {id} 是输出技能，但当前有治疗需求 → 让位给原队列");
                return -1;
            }

            本帧采纳 = true;
            本帧技能 = id;
            return 100;   // 最高优先级（比 Always 队列里的其他都高）
        }
        catch (Exception e)
        {
            Ai调试.日志("建议执行器异常（已忽略）：" + e.Message);
            return -1;
        }
    }

    public void Build(Slot slot)
    {
        try
        {
            var 建议 = AiDecisionLayer.当前建议;
            if (建议 == null) return;

            var id = 建议.技能Id;
            if (id == 0) return;

            var 目标 = HealTargetHelper.当前目标();

            // 和治疗类不同，输出类技能必须明确指定敌人目标
            if (需要目标(id) && 目标 != null)
            {
                slot.Add(new Spell(id, 目标));
            }
            else
            {
                slot.Add(new Spell(id, SpellTargetType.Self));
            }

            Ai调试.日志($"采纳建议：{id} = {SpellIds.反查(id)}（{建议.理由}）" +
                        $"｜出生排队第 {建议.出生时队列位置 + 1} 位 / 批次 {建议.批次}" +
                        $" / 等了 {建议.已等毫秒}ms");

            // ★ 技能真的进了 slot，才消费掉这条建议 ★
            //   放在最后：如果上面任何一步失败（目标为空、Spell 构造异常），
            //   建议不会被消费，下一帧还能再用 —— 比"判了不用"更合理。
            AiDecisionLayer.消费();
        }
        catch (Exception e)
        {
            Ai调试.日志("建议构建失败（已忽略）：" + e.Message);
        }
    }

    /// <summary>
    /// 记一次"建议被终审拦下"。
    ///
    /// 术语用"拦截"而不是"拒绝"，是为了和"AI 幻觉被白名单丢弃"区分开：
    ///   · 丢弃 = 建议本身就是错的（AI 编的）
    ///   · 拦截 = 建议合法，但**当前时机不合适**（原有逻辑说了算）
    /// 后者占比高不是坏事，说明"AI 给建议、原逻辑终审"这套设计在干活。
    /// </summary>
    private static void 拦截(string 原因)
    {
        本帧拦截原因 = 原因;
        AiDecisionLayer.记拦截(原因);
    }

    /// <summary>
    /// 这个技能是不是"输出技能"。
    ///
    /// 用途：**治疗优先** —— 有治疗需求时，AI 建议的输出技能要让位。
    /// </summary>
    private static bool 是输出技能(uint id)
    {
        if (id == 0) return false;

        try
        {
            var 表 = HealerACR.Common.HealRotationEventHandler.取当前职业技能表();
            if (表 == null) return false;   // 拿不到表就不拦（宁可放过）

            if (id == 表.基础输出) return true;
            if (id == 表.群体输出) return true;
            if (id == 表.Dot技能) return true;
            if (id == 表.移动填充技) return true;
            if (表.输出能力技 != null && Array.IndexOf(表.输出能力技, id) >= 0) return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 现在有没有人需要治疗。
    ///
    /// 判据：**低于单体治疗阈值的人数 > 0**（用玩家自己设的阈值，不另定标准）。
    /// </summary>
    private static bool 有人需要治疗()
    {
        try
        {
            var 阈值 = HealerACR.Common.HealSettings.Instance.单体治疗阈值;
            return HealerACR.Common.HealTargetHelper.低于阈值人数(阈值) > 0;
        }
        catch
        {
            return false;   // 判断不了就不拦
        }
    }

    /// <summary>
    /// 这个技能是不是必须指定目标。
    ///
    /// 判据很粗但够用：**输出类的都要目标，治疗类的都不要**（以自己为原点）。
    /// 对照官方错题集第 3 条：目标传错（该打敌人却传了 Self）会导致技能放不出去。
    /// </summary>
    private static bool 需要目标(uint id)
    {
        try
        {
            var 表 = HealerACR.Common.HealRotationEventHandler.取当前职业技能表();
            if (表 == null) return false;

            if (id == 表.基础输出) return true;
            if (id == 表.群体输出) return false;   // AOE 以自己/目标为中心，但通常不用手选
            if (id == 表.Dot技能) return true;
            if (表.输出能力技 != null && Array.IndexOf(表.输出能力技, id) >= 0) return true;

            return false;
        }
        catch
        {
            return false;
        }
    }
}
