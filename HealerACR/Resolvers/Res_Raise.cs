using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Resolvers;

/// <summary>
/// 复活。日随里优先级最高的一件事：队友躺着的时候，输出和治疗都往后排。
///
/// 等级覆盖：四个奶妈都是 12 级学会复活，所以 1 级起的 ACR 里它靠 IsUnlock 自动挡住。
/// </summary>
public class Res_Raise : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_Raise(JobSpellTable table) => _t = table;

    public int Check()
    {
        // 加兜底 true —— 万一这个开关没注册，也不能因此永不复活
        if (!HealQt.GetQt("复活", true)) return -100;

        // 没学会（低等级）
        if (!SpellUtil.已解锁(_t.复活)) return -2;

        // 没蓝拉不动
        if (Core.Me.CurrentMp < 2400) return -8;

        var target = HealTargetHelper.待复活队友();
        if (target == null) return -1;

        if (!SpellUtil.可用(_t.复活)) return -1;

        var 有即刻 = Core.Me.HasAura(AuraIds.即刻);

        // ── 躺够久了才拉（参考同类 ACR 的 IsTargetDeadLongEnough）──
        //   战斗中刚躺下的人可能马上被战复，或者下一秒就被 AOE 打死。
        //   等 2.5 秒更省资源。
        //   **但有即刻时不等** —— 即刻是瞬发，没有读条风险，早点拉起来多打一会儿。
        if (!有即刻 && !死亡追踪.躺够久了(target)) return -11;

        // 有即刻：随时拉，最高优先级
        if (有即刻) return 20;

        // ══════════════════════════════════════════════════════════
        //  ⚠️ 修正（用户实测：90 级本 T 死了没人复活）
        //
        //  原来是"没即刻 && 没开硬读 -> 直接不拉"，而「允许硬读复活」默认关，
        //  于是**只要即刻在 CD，队友躺在地上就永远没人管** —— 这显然不对。
        //
        //  新规则按"有没有危险"来分：
        //    · 脱战  → 无条件硬读。复活读 8 秒，脱战又不会挨打，不拉白不拉。
        //    · 战斗中 → 才看「允许硬读复活」这个设置（读条期间可能吃机制）
        // ══════════════════════════════════════════════════════════
        if (!Core.Me.InCombat()) return 15;   // 脱战硬读，优先级仅次于即刻

        if (HealSettings.Instance.允许硬读复活) return 10;

        return -9;
    }

    public void Build(Slot slot)
    {
        var target = HealTargetHelper.待复活队友();
        if (target == null) return;

        slot.Add(new Spell(_t.复活, target));

        // 拉人喊话
        var s = HealSettings.Instance;
        if (s.复活喊话开关 && s.复活喊话.Count > 0)
        {
            var 模板 = s.复活喊话[Random.Shared.Next(s.复活喊话.Count)];
            ChatHelper.SendMessage(s.复活喊话频道 + 模板.Replace("<t>", target.Name.ToString()));
        }
    }
}
