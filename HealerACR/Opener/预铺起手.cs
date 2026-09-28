using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.CombatRoutine.Module.Opener;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Opener;

/// <summary>
/// 日随用的"起手"。
///
/// 日随不需要固定技能序列（副本节奏千变万化，死循环式的起手反而碍事），
/// 所以这里 <see cref="Sequence"/> 故意留空，
/// 真正有用的是 <see cref="InitCountDown"/>：利用开怪倒计时做**预铺**。
///
/// 想加真起手序列的话，往 Sequence 里塞 Action&lt;Slot&gt; 就行，
/// 框架会按 StartCheck/StopCheck 的返回值决定怎么走。
/// </summary>
public class 预铺起手 : IOpener
{
    private readonly JobSpellTable _t;

    public 预铺起手(JobSpellTable table) => _t = table;

    /// <summary>空序列：日随靠优先级队列，不靠死循环</summary>
    public List<Action<Slot>> Sequence { get; } = new();

    /// <summary>返回负数 = 不启动序列</summary>
    public int StartCheck() => -1;

    /// <summary>返回正数 = 序列立刻结束</summary>
    public int StopCheck(int index) => 1;

    /// <summary>
    /// 倒计时预铺。CountDownHandler 的 AddAction(剩余毫秒, Action) 是现成 API。
    /// 时间点从远到近排：15 秒铺盾，3 秒读一个治疗/DoT。
    /// </summary>
    public void InitCountDown(CountDownHandler countDownHandler)
    {
        // 诊断：确认倒计时回调到底有没有被触发。
        // 日随基本是直接拉怪、没有倒计时，所以这条日志平时不会出现；
        // 8 人本打 /countdown 时才会看到 —— 有它就能确认链路是通的。
        LogHelper.Info("[HealerACR] 倒计时开始：注册预铺 + 爆发药");

        // ---- 爆发药：开怪前 2 秒 ----
        // 之前这行被注释掉了。现在打开 —— CountDownHandler 是实例方法，
        // 由 AEAssist 在倒计时时传进来，这才是吃药的正确入口。
        try { countDownHandler.AddPotionAction(2000); } catch { }

        // ---- 15 秒：给主坦预铺单体盾（有盾职业才有意义）----
        if (_t.单体盾 != 0)
        {
            countDownHandler.AddAction(15000, () =>
            {
                var 主坦 = PartyHelper.CastableTanks.FirstOrDefault();
                if (主坦 == null) return;

                var slot = new Slot();

                if (_t.护盾前置 != 0 && !JobApiHelper.均衡中)
                {
                    var pre = SpellUtil.Get(_t.护盾前置);
                    if (pre != null) slot.Add(pre);
                }

                slot.Add(new Spell(_t.单体盾, 主坦));
                AI.Instance.BattleData.NextSlot = slot;
            });
        }

        // ---- 3 秒：给自己/坦克补一个即时治疗（读条技能最后一下）----
        if (_t.单体治疗GCD != 0)
        {
            countDownHandler.AddAction(3000, () =>
            {
                var 坦克 = PartyHelper.CastableTanks.FirstOrDefault();
                if (坦克 == null) return;
                if (坦克.血量比例() > 0.95f) return;   // 满血就不浪费

                var slot = new Slot();
                slot.Add(new Spell(_t.单体治疗GCD, 坦克));
                AI.Instance.BattleData.NextSlot = slot;
            });
        }

        // 爆发药已经在上面注册了（AddPotionAction(2000)）
    }
}
