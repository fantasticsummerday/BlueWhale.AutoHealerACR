using AEAssist.CombatRoutine;
using HealerACR.Common;

namespace HealerACR.Resolvers;

/// <summary>
/// 卡 CD 打的输出能力技（学者的「能量吸收」这类）。
///
/// 跟"群体治疗能力技是输出型"（白魔法令）同一个道理：
/// 这种技能早用早 CD，输出和资源回收都不亏，**不该等条件**。
///
/// 哪个技能进来由 <see cref="JobSpellTable.输出能力技"/> 决定。
/// </summary>
public class Res_OffensiveAbility : ISlotResolver
{
    private readonly JobSpellTable _t;
    private static long 上次诊断;

    public Res_OffensiveAbility(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;
        if (_t.输出能力技.Length == 0) return -102;
        if (HealTargetHelper.当前目标() == null) return -1;

        // 视线被挡就别交 —— 输出能力技是对敌人的，打不出去等于白按
        // （诊断日志里"所有条件都 True 但技能不放"的成因之一就是这个）
        if (!技能数据.打得到(HealTargetHelper.当前目标())) return -6;

        // 残血小怪不交（木桩模式例外）——
        // 和 Res_Dot / Res_BaseDamage / 苦难之心 保持同一套判断。
        // 能力技 CD 长，砸在快死的小怪上是最亏的浪费。
        if (!HealTargetHelper.木桩模式 && HealTargetHelper.目标快死了()) return -4;

        // 整波快清完时也不交（对照同类 ACR 的 ShouldHoldForDyingTrash）
        if (!HealTargetHelper.木桩模式 && HealTargetHelper.敌人波次要结束()) return -4;

        // ---- 诊断：每 5 秒打一次，看能量吸收到底卡在哪 ----
        var 候选 = _t.输出能力技;
        if (TimeHelper.Now() - 上次诊断 > 5000 && _t.AOE最少敌人数 > 0)
        {
            上次诊断 = TimeHelper.Now();
            var 首个 = 候选.Length > 0 ? 候选[0] : 0;
            LogHelper.Info(
                "[HealerACR] 输出能力技诊断：候选数=" + 候选.Length +
                " 首个id=" + 首个 +
                " 以太=" + JobApiHelper.以太 +
                " 读得到=" + JobApiHelper.读得到("以太") +
                " 已解锁=" + (首个 != 0 && SpellUtil.已解锁(首个)) +
                " 可用=" + (首个 != 0 && SpellUtil.可用(首个)) +
                " 可插能力技=" + CharacterExt.可以插能力技() +
                " 目标=" + (HealTargetHelper.当前目标() == null ? "无" : "有"));
        }

        foreach (var id in _t.输出能力技)
        {
            if (id == 0) continue;
            if (!SpellUtil.已解锁(id)) continue;
            if (SpellUtil.可用(id))
            {
                // ══════════════════════════════════════════════════════
                //  ★ 有人真需要治疗时，输出能力技让路 ★
                //
                //  由来（全面排查治疗/输出优先级时发现）：
                //    这个 resolver 原本**无条件返回 21**，
                //    而治疗侧的分值是：
                //        紧急单奶 / 必须奶满   30~35
                //        群体治疗能力技        16~25
                //        群盾能力技            23
                //        群体减伤              14
                //    于是 21 会**抢在群体治疗和群盾前面** ——
                //    而输出能力技大多是**消耗治疗资源**换输出的
                //    （学者能量吸收吃 1 颗以太，那是以太超流攒的救命资源）。
                //    等于"该救人的时候拿救命资源去换输出"。
                //
                //  ⚠️ 为什么是 13 而不是降到很低：
                //    要让它在**真需要治疗**时退让，但**正常输出节奏不受影响**。
                //    13 卡在"群体减伤 14"和"单体治疗 GCD 10"之间 ——
                //      · 高于单体治疗 GCD  → 没人急需时照常打输出，不浪费
                //      · 低于群体治疗/群盾/急救 → 该奶的时候一定让路
                //    如果无脑降到很低，学者会变成"几乎不打能量吸收"，
                //    那是另一个方向的 bug（豆子憋着不用，见 学者豆子机制）。
                // ══════════════════════════════════════════════════════
                if (有人在等治疗()) return 13;

                return 21;
            }
        }

        return -1;
    }

    /// <summary>
    /// 现在有没有人**真的需要治疗**（需要就让输出能力技退让）。
    ///
    /// 判据刻意保守 —— 只看"低于单体治疗阈值的人数"，
    /// 不用"掉血趋势"之类更敏感的判断，免得正常掉血就停输出。
    /// </summary>
    private static bool 有人在等治疗()
    {
        try
        {
            var s = HealSettings.Instance;

            // 有人掉到单体治疗阈值以下 → 该奶了
            if (HealTargetHelper.低于阈值人数(s.单体治疗阈值) > 0) return true;

            // 有人中「必须奶满」机制（Doom / 石化 / 塞壬之歌）→
            // 这个优先级最高，任何输出都该让路
            if (必须奶满.找目标() != null) return true;

            return false;
        }
        catch
        {
            // 判断不了就当作"有人在等"（保守：宁可少打一个输出能力技）
            return true;
        }
    }

    public void Build(Slot slot)
    {
        // ⚠️ 输出能力技是**对敌人**的技能，绝对不能用 SpellTargetType.Self。
        //
        //    0.6.8 我为了加 WaitServerAcq=false，顺手用了 CharacterExt.能力技(id)，
        //    而那个便利方法默认 Self —— 于是能量吸收变成了"对自己用"，
        //    底层直接拒绝施放。表现就是：Check 全部条件都通过、返回 21，
        //    但 CastSpell 里永远看不到它（日志诊断证明七个条件全 True）。
        //
        //    这正是官方错题集第 3 条「技能目标错误」的变种。
        var 目标 = HealTargetHelper.当前目标();

        foreach (var id in _t.输出能力技)
        {
            if (id == 0) continue;
            if (!SpellUtil.已解锁(id)) continue;
            if (!SpellUtil.可用(id)) continue;

            // 输出能力技**必须有目标**：传 SpellTargetType.Self 会打不出去，
            // 传 null 底层会拒绝。所以没目标就直接不放。
            if (目标 == null) return;

            // 明确打当前目标（能力技不等服务器回包）
            slot.Add(CharacterExt.能力技(id, 目标));

            return;
        }
    }
}
