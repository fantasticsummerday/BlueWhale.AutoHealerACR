using AEAssist;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Common;

/// <summary>
/// **一个治疗技能的完整属性** —— 综合判定用的数据。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么不能只看"恢复力"★
///
///    只看恢复力会选出**又贵又慢又过量**的技能。举几个真实例子：
///
///      白魔 治疗 500/400 MP400   ← 便宜、够用
///      白魔 救疗 800/700 MP1000  ← 恢复力高 60%，但 MP 贵 **2.5 倍**
///      白魔 愈疗 600/550 MP1500  ← 更贵，而且是群体技
///
///    掉 3000 血时用「救疗」和用两次「治疗」：
///      · 救疗：一次 GCD，1000 MP，治 700 左右
///      · 治疗：一次 GCD，400 MP，治 400 左右 → 不够，还得再来一次
///    ⇒ 一次能补上缺口时，**大治疗更省 GCD**（GCD 才是最稀缺的资源）；
///      但如果缺口很小，**小治疗就够**，用大治疗是纯浪费 MP。
///
///    ⇒ 所以判定必须综合：**缺口大小 + MP + GCD 数 + 能不能放**。
///
///  ★ 数据来源（权威）★
///    · 恢复力：游戏数据宏（`tools\\HealPotency` 的 `ToMacroString`）
///    · MP / 复唱 / 咏唱 / CastType：游戏 `Action` 表（`tools\\CastProbe`）
///    · 特殊效果（盾 / HoT / 条件触发）：技能说明原文
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public sealed class 治疗技能
{
    /// <summary>技能 ID</summary>
    public uint Id { get; init; }

    /// <summary>学会等级</summary>
    public int 等级 { get; init; }

    /// <summary>名字（日志 / 诊断用）</summary>
    public string 名 { get; init; } = "";

    /// <summary>
    /// **恢复力**（当前等级下的值）。
    ///
    /// ⚠️ 多档位技能用 `恢复力表` 表达，这个属性是"当前等级查出来的值"。
    /// </summary>
    public int 恢复力 { get; init; }

    /// <summary>
    /// 额外的 HoT 恢复力（每跳）—— 像白魔医济是 `250 + 150`。
    /// 0 表示没有 HoT 部分。
    /// </summary>
    public int HoT恢复力 { get; init; }

    /// <summary>HoT 持续时间（秒），0 = 没有 HoT</summary>
    public float HoT持续 { get; init; }

    /// <summary>MP 消耗，0 = 不耗蓝</summary>
    public int MP { get; init; }

    /// <summary>咏唱时间（秒），0 = 瞬发</summary>
    public float 咏唱 { get; init; }

    /// <summary>复唱时间（秒）</summary>
    public float 复唱 { get; init; } = 2.5f;

    /// <summary>
    /// 冷却 / 充能时间（秒），0 = 走复唱（GCD 技）。
    /// 能力技填这里 —— 它决定"这个技能多久能用一次"。
    /// </summary>
    public float 冷却 { get; init; }

    /// <summary>是不是**群体**技能（对队伍 / 周围队友）</summary>
    public bool 群体 { get; init; }

    /// <summary>
    /// 是不是**只能给自己**的技能（如贤者自生、白魔法令的一部分效果）。
    /// 这类不能当"给队友的治疗"用。
    /// </summary>
    public bool 仅自己 { get; init; }

    /// <summary>是不是**护盾**类（治疗量表现为盾，不直接回血）</summary>
    public bool 是盾 { get; init; }

    /// <summary>
    /// **需要多少资源**（以太 / 百合 / 蛇胆…），0 = 不需要。
    /// 具体是什么资源由职业表解释 —— 这里只记数量。
    /// </summary>
    public int 资源消耗 { get; init; }

    /// <summary>
    /// **充能数**（1 = 无充能）。
    ///
    /// ⚠️ 有充能的技能（学者慰藉 2 充能、贤者发炎 2 充能）可以**连续用两次** ——
    ///    这在"缺口很大但冷却没转好"时是关键差别，所以要记录。
    /// </summary>
    public int 充能 { get; init; } = 1;

    // ══════════════════════════════════════════════════════════════
    //  派生属性
    // ══════════════════════════════════════════════════════════════

    /// <summary>是不是瞬发（咏唱 0）</summary>
    public bool 瞬发 => 咏唱 <= 0f;

    /// <summary>
    /// **总恢复力** —— 把 HoT 折算进来。
    ///
    /// ⚠️ 按 **3 秒一跳**算（FF14 的 DoT/HoT 都是 3 秒跳一次），
    ///    跳数 = 持续 / 3。
    ///
    /// ⚠️ HoT 的权重**低于直疗**：它需要时间才生效，救急时没用。
    ///    所以这里给 HoT 乘 **0.6** —— 一个折中值：
    ///      · 时间够长时 HoT 的总量确实可观（医济 250+150×10 = 1750）
    ///      · 但"现在这一下"只回了 250
    ///    ⇒ 用 0.6 表达"打七折"，避免 HoT 技在救急时被误选。
    /// </summary>
    public float 总恢复力
    {
        get
        {
            var 值 = (float)恢复力;
            if (HoT恢复力 > 0 && HoT持续 > 0)
                值 += HoT恢复力 * (HoT持续 / 3f) * 0.6f;
            return 值;
        }
    }

    /// <summary>
    /// **即时恢复力** —— 只看"这一下立刻回多少"（不含 HoT）。
    /// 救急判定用这个，不能用 `总恢复力`。
    /// </summary>
    public float 即时恢复力 => 恢复力;
}

/// <summary>
/// **治疗候选集** —— 一个职业的全部治疗技能。
///
/// ⚠️ 为什么要"候选集"而不是"槽位"：
///    槽位（单体治疗GCD / 群体治疗GCD / 瞬发单奶能力技…）只能表达
///    "这一类用哪个"，不能在**同类里按情况挑**。
///    比如白魔有两个单体 GCD 治疗（治疗 500 / 救疗 800）——
///    缺口小该用治疗（省 600 MP），缺口大该用救疗（省一个 GCD）。
///    槽位表达不了这个，候选集可以。
/// </summary>
public sealed class 治疗候选集
{
    public List<治疗技能> 全部 { get; } = new();

    public void 加(治疗技能 技) => 全部.Add(技);

    /// <summary>
    /// 已解锁的候选。
    ///
    /// ⚠️ 先收集到 List 再返回，**不用 `yield return`** ——
    ///    C# 不允许在带 `catch` 的 `try` 块里 yield（CS1626），
    ///    而 `SpellUtil.已解锁` 会抛（技能数据没加载时）。
    /// </summary>
    public List<治疗技能> 已解锁()
    {
        var 结果 = new List<治疗技能>(全部.Count);
        foreach (var s in 全部)
        {
            if (s.Id == 0) continue;

            try
            {
                if (SpellUtil.已解锁(s.Id)) 结果.Add(s);
            }
            catch { }
        }
        return 结果;
    }
}
