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
        //
        // ⚠️ 两条吃药的路径，别重复理解：
        //    · 这里 = **只在打 /countdown 时**生效（高难场景）
        //    · 爆发轴.cs 的 使用爆发药() = 日常开「一键爆发」时生效
        //    两边共用「爆发药」开关，且 CheckPotion 会看 CD，
        //    所以就算都走也不会吃两次。
        try
        {
            if (HealQt.GetQt("爆发药", false))
            {
                countDownHandler.AddPotionAction(2000);
                LogHelper.Info("[HealerACR] 倒计时：已注册爆发药（开怪前 2 秒）");
            }
            else
            {
                LogHelper.Info("[HealerACR] 倒计时：爆发药未开启（QT 开关『爆发药』= 关），跳过");
            }
        }
        catch (Exception e)
        {
            LogHelper.Info("[HealerACR] 倒计时：注册爆发药失败（已忽略）：" + e.Message);
        }

        // ---- 15 秒：给主坦预铺单体盾（有盾职业才有意义）----
        if (_t.单体盾 != 0)
        {
            countDownHandler.AddAction(15000, () =>
            {
                var 主坦 = PartyHelper.CastableTanks.FirstOrDefault();
                if (主坦 == null) return;

                // ⚠️ 用框架的 `AddSpell2NextSlot`，**不要** `NextSlot = new Slot()`
                //
                //    框架实现（IL 直读，41 字节）：
                //        if (BattleData.NextSlot == null) BattleData.NextSlot = new Slot();
                //        BattleData.NextSlot.Add(spell);
                //
                //    差别在**要不要覆盖已有的下一槽**：
                //      · `NextSlot = new Slot()`  → **覆盖**（别处排好的会被丢掉）
                //      · `AddSpell2NextSlot(spell)` → 追加（没有才新建）
                //
                //    同一个倒计时里我们可能连注册好几个动作（预铺盾 / 补治疗 / …），
                //    直接赋值会让**后注册的覆盖先注册的**，前面的准备白做。
                //    参考实现 A/B 处理"强制插队放技能"用的都是这个方法。
                if (_t.护盾前置 != 0 && !JobApiHelper.均衡中)
                {
                    var pre = SpellUtil.Get(_t.护盾前置);
                    if (pre != null) AI.Instance.BattleData.AddSpell2NextSlot(pre);
                }

                AI.Instance.BattleData.AddSpell2NextSlot(new Spell(_t.单体盾, 主坦));
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

                AI.Instance.BattleData.AddSpell2NextSlot(new Spell(_t.单体治疗GCD, 坦克));
            });
        }

        // ---- 起手地面预铺（目前只有占星的地星用）----
        //
        //  [!] 为什么要它：地星放下 10 秒后才长大成满档（星体爆炸 720），
        //      而**开怪后才发现要铺就已经晚了** —— 10 秒的空窗里它是半档。
        //      8 人本打 `/countdown` 时，倒数 10 秒正好把这一发铺下去，
        //      开怪瞬间它已经长大、正好吃第一波伤害。
        //
        //  [!] 落点用同一套"敌人站得稳就放它脚下"的选位（`地面技能位置`）——
        //      倒数阶段怪还没被拉，会退回自己脚下，这是可接受的
        //      （总比不放好；而且地星是圆形 20 米，站自己脚下也覆盖得到近战位）。
        //
        //  [!] 只有表里登记了才注册 —— 白魔/学者/贤者没有这个技能，什么都不做。
        if (_t.起手预铺地面技 != 0)
        {
            var 地面技 = _t.起手预铺地面技;
            countDownHandler.AddAction(10000, () =>
            {
                try
                {
                    if (!SpellUtil.已解锁(地面技)) return;
                    if (!SpellUtil.可用(地面技)) return;

                    var 落点 = 敌人移动检测.地面技能位置();
                    AI.Instance.BattleData.AddSpell2NextSlot(new Spell(地面技, 落点));
                }
                catch { }
            });
        }

        // ---- 1.5 秒：预读一个读条填充（开怪那一下正好读完）----
        if (_t.预读填充技 != 0)
        {
            var 填充 = _t.预读填充技;
            countDownHandler.AddAction(1500, () =>
            {
                try
                {
                    if (!SpellUtil.已解锁(填充)) return;
                    if (!SpellUtil.可用(填充)) return;

                    AI.Instance.BattleData.AddSpell2NextSlot(new Spell(填充, Core.Me));
                }
                catch { }
            });
        }

        // 爆发药已经在上面注册了（AddPotionAction(2000)）
    }
}
