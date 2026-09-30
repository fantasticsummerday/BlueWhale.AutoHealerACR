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

    public Res_OffensiveAbility(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;
        if (_t.输出能力技.Length == 0) return -102;
        if (HealTargetHelper.当前目标() == null) return -1;

        // 视线被挡就别交 —— 输出能力技是对敌人的，打不出去等于白按
        // （诊断日志里"所有条件都 True 但技能不放"的成因之一就是这个）
        // [!] 传**这个技能自己的**有效射程 —— 自身中心 AOE 按 25 米判会误放
        if (!技能数据.打得到(HealTargetHelper.当前目标(), 技能数据.取有效射程(_t.输出能力技[0]))) return -6;

        // 残血小怪不交（木桩模式例外）——
        // 和 Res_Dot / Res_BaseDamage / 苦难之心 保持同一套判断。
        // 能力技 CD 长，砸在快死的小怪上是最亏的浪费。
        if (!HealTargetHelper.木桩模式 && HealTargetHelper.目标快死了()) return -4;

        // 整波快清完时也不交（对照同类 ACR 的 ShouldHoldForDyingTrash）
        if (!HealTargetHelper.木桩模式 && HealTargetHelper.敌人波次要结束()) return -4;

        // ⚠️ 这里原来有一条"每 5 秒打一次"的诊断日志（打印候选数 / 以太 / 读得到…）。
        //
        //    实测它**永远不会停**：条件是 `_t.AOE最少敌人数 > 0`，
        //    而那个值四个职业都恒为正 —— 所以整场战斗每 5 秒刷一条，
        //    还顺带做一次 `JobApiHelper.读得到("以太")` 反射查询。
        //
        //    它是排查"能量吸收为什么不放"时临时加的，问题已经定位并修掉了
        //    （根因是 `取已解锁` 顺序 + 返回值不参与仲裁，见下面的长注释）。
        //    ⇒ 对用户没有价值，删掉 —— 日志要留给**用户看得懂、能据此行动**的信息。
        //
        //    ⚠️ 以后再加这类诊断，记得：
        //       · 挂在"调试开关"后面，不要无条件打
        //       · 条件要**真的会变假**（`> 0` 这种恒真条件等于没有条件）

        foreach (var id in _t.输出能力技)
        {
            if (id == 0) continue;
            if (!SpellUtil.已解锁(id)) continue;
            if (SpellUtil.可用(id))
            {
                // ══════════════════════════════════════════════════════
                //  ★ 有人真需要治疗时，输出能力技**直接放弃这个 oGCD** ★
                //
                //  ⚠️⚠️ 这里我前后错了两次，写清楚免得再犯：⚠️⚠️
                //
                //    第一次：无条件 `return 21`。
                //      问题：`Res_OffensiveAbility` 在决策队列里排**第 2 位**
                //      （SlotMode.Always，GCD 趟和 oGCD 趟都会被考虑），
                //      所以它会抢在任何治疗前面。而输出能力技大多是
                //      **消耗治疗资源**换输出的（学者能量吸收吃 1 颗以太，
                //      那是以太超流攒的救命资源）→ 等于该救人的时候拿救命资源换输出。
                //
                //    第二次（也错）：改成"有人等治疗就 `return 13`"，
                //      以为 13 < 群体治疗(16~25) 就能让路。
                //      **但返回值根本不参与仲裁** —— 反汇编 AEAssist 实测
                //      （`PVE_RunSlotHelper+<CheckNext>d__4::MoveNext`）：
                //          IL_0066 ldc.i4.0 ; IL_0067 blt   ← 只看 < 0
                //          IL_0054 AppendFormatted<int>     ← 分值只进日志
                //      而 `<RunSlotResolvers>d__2` 是顺序试、第一个 >=0 的就退出。
                //      ⇒ 13 仍然是 >=0，照样抢先，**改了等于没改**。
                //
                //    ⇒ 正确写法只有一个：**`return -1`**（放弃这个 oGCD，
                //      让框架继续往下扫，治疗 resolver 就能拿到它）。
                //      这就是 开发约定 里说的"想表达让路，必须返回负数"。
                //
                //  ⚠️ 影响面：目前只有学者配了 `输出能力技`（能量吸收），
                //    白魔/占星/贤者/幻术师是空数组（`_t.输出能力技.Length == 0`
                //    在上面就 return -102 了）。而学者本身还有
                //    `以太管理.在爆发窗口()` / `以太超流快转好()` + 无治疗压力
                //    两道更严的把关 —— 所以这是**第三层保险**，不会让学者不打输出。
                // ══════════════════════════════════════════════════════
                if (有人在等治疗()) return -1;

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
