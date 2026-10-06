using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Resolvers;

/// <summary>
/// **位移技**（贤者 神翼 Icarus 24295）—— 表 #130。
///
/// ══════════════════════════════════════════════════════════════════
///  [!] 参考实现怎么做的（`神翼飞T热键.txt`，IL 直读）：
///      · **只有 Check + Run 两个方法** —— 这是一个**热键执行器**，
///        不是策略 resolver：`Run` 里直接把技能塞进 slot（`ReplaceNextSlot`）。
///      · `Check` 的全部内容 = 没被屏蔽 + 有最低血坦克 + 技能 Ready。
///      · **它的槽序表里没有这一项** ⇒ 参考**不自动飞**，等玩家按键。
///
///  [!] 所以我们的口径：
///      · **默认关**（`位移技` 开关 false）—— 和参考一致，不自动飞；
///      · 打开时才走这条 resolver，判据也照参考：**最低血的坦克** + 技能 Ready；
///      · 额外加两条参考没有但必要的守卫：
///          ① **正在走位**才飞（站着飞纯属浪费，还可能飞进机制）
///          ② 距离 **≥ 位移最短距离**（贴着坦克飞没有位移意义）
///
///  [!] 为什么不默认开：这是**位移**，落点由目标决定 ——
///     自动飞进 AOE / 飞出治疗范围都是实打实的负收益，
///     而"该不该飞"取决于机制，本地判不出来。宁可让玩家按。
///
///  [!] 占位设计：`_t.位移技 == 0` 的职业（白魔/学者/占星）这条直接不参与，
///      所以放在通用 resolver 里是安全的。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class Res_Dash : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_Dash(JobSpellTable table) => _t = table;

    /// <summary>Check 里选好的落点，给 Build 用（判 A 放 A）</summary>
    private static IBattleChara? 本帧目标;

    public int Check()
    {
        本帧目标 = null;

        if (_t.位移技 == 0) return -102;
        if (!HealQt.GetQt("位移技", false)) return -100;
        if (!SpellUtil.已解锁(_t.位移技)) return -2;
        if (!SpellUtil.可用(_t.位移技)) return -1;

        // 参考：目标是**最低血的坦克**
        var 目标 = HealTargetHelper.血量最低的坦克();
        if (目标 == null || !目标.对象有效() || !目标.活着()) return -1;

        // ① 正在走位才飞
        if (!SpellUtil.真在走位()) return -3;

        // ② 够远才值得飞
        try
        {
            // ★ 2026-10-15 修（崩溃防护）：不能把**未判有效**的 `Core.Me!` 交给**外部库**的
            //   `Distance` —— 换图/登录时 `Core.Me` 可为 null 或哨兵（0x12345679 ≠ null），
            //   原生访问违例发生在**库内部**，我们的 catch 拦不住 ✗
            //   （那个 `!` 还正好压制了指向这个问题的可空告警。）
            //   读不到自己 / 目标不可用 ⇒ **保守当"不够远"**（不飞），与项目"读不到就不放"同口径。
            if (Core.Me == null || !Core.Me.对象有效()) return -4;
            if (目标 == null || !目标.对象有效()) return -4;
            if (目标.Distance(Core.Me) < _t.位移最短距离) return -4;
        }
        catch { }

        // 插能力技的时机
        if (!CharacterExt.可以插能力技()) return -6;

        本帧目标 = 目标;
        return 5;
    }

    public void Build(Slot slot)
    {
        var 目标 = 本帧目标;
        if (目标 == null || !目标.对象有效()) return;

        var spell = SpellUtil.Get(_t.位移技);
        if (spell == null) return;

        slot.Add(new Spell(spell.Id, 目标));
    }
}
