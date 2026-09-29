using AEAssist.CombatRoutine;
using HealerACR.Common;
using HealerACR.Timeline;

namespace HealerACR.Resolvers;

// ============================================================================
//  减伤与预铺。两条触发路：
//    1. cactbot 时间轴（boss 还没读条就知道要来了）
//    2. boss 正在读大伤害（没有时间轴的副本兜底）
//  木桩模式下全部让路（需求 5）。
// ============================================================================

/// <summary>团队减伤。表里 团队减伤 = 0 的职业不会走到这。</summary>
public class Res_TeamMitigation : ISlotResolver
{
        private static long _上次诊断;
        private static int _上次敌人数量;
    private readonly JobSpellTable _t;

    public Res_TeamMitigation(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("减伤", true)) return -100;
        if (!HealSettings.Instance.自动减伤) return -101;
        if (_t.团队减伤 == 0) return -102;
        if (!SpellUtil.已解锁(_t.团队减伤)) return -2;

        // 是不是**地面放置型**（罩子 / 地星 / 庇护所…）——
        // 决定要不要做"移动中不铺"的判断（见下面那段长注释）。
        // ⚠️ Check 和 Build 都要用，所以在这里算一次。
        var 是地面 = 技能数据.是地面技能(_t.团队减伤);

        var 时间轴要求 = HealQt.GetQt("时间轴", true) && TimelineManager.该铺减伤();
        var 读条要求 = 减伤Helper.即将来大伤害();


            // ══════════════════════════════════════════════════════════
            //  ⚠️ 新增第三个触发条件：敌人够多时下罩子
            //
            //  现象（50 级主随 · 土神转场那类 AOE）：该下罩子却没下。
            //  原因是原逻辑只有两个触发源：
            //    · 时间轴标记（cactbot 里写了才触发）
            //    · 敌人正在放大伤害的读条
            //  小怪群殴 / 转场 AOE 这两种**都不满足**，于是永远不下。
            //
            //  罩子这类持续型团减，收益最大的场景恰恰是"一群怪在打你们"，
            //  所以补上"敌人数量"这个判据。
            // ══════════════════════════════════════════════════════════
            var 敌人够多 = false;
            try
            {
                var 范围 = _t.AOE伤害范围;
                _上次敌人数量 = HealTargetHelper.周围敌人数量(范围, 25f);
                敌人够多 = _上次敌人数量 >= 3;
            }
            catch { }

            // ── 诊断日志（每 3 秒最多一条，用来查"该下没下"）──
            //   现象：距离衰减 AOE 没触发罩子。需要看清楚是哪个条件没满足。
            try
            {
                var 现在 = TimeHelper.Now();
                if (现在 - _上次诊断 > 3000)
                {
                    _上次诊断 = 现在;
                    LogHelper.Info(
                        $"[HealerACR.团减] 时间轴={时间轴要求} 读条={读条要求} 敌人够多={敌人够多}" +
                        $"（{_上次敌人数量}个）" +
                        $" | 可用={SpellUtil.可用(_t.团队减伤)}");
                }
            }
            catch { }

            if (!时间轴要求 && !读条要求 && !敌人够多) return -1;

            // ══════════════════════════════════════════════════════════
            //  ★ **移动中不铺地面减伤**（道中小怪阶段实测报的问题）★
            //
            //  ── 现象 ──
            //     "在小怪阶段移动过程中又下罩子了" —— 跑路时往脚下丢一个罩子，
            //     人一走开，**整个罩子等于白铺**（还占了个大 CD）。
            //
            //  ── 根因 ──
            //    ① 地面技能是**瞬发**的，所以 `移动中可用()` 判 true，
            //       移动守卫拦不住它（读条守卫对地面技能天然失效）
            //    ② 落点逻辑 `敌人移动检测.地面技能位置()`：
            //         优选敌人**稳定观察满 3 秒** → 放它脚下
            //         否则                        → **放自己脚下**
            //       而道中小怪基本一直在动 → 永远走"放自己脚下"那条路 ——
            //       移动中放自己脚下 = 铺在一个马上没人待的位置
            //
            //  ── 判据 ──
            //     `敌人够多` 是**弱判据**（小怪群殴时永远成立），
            //     不足以支撑"移动中也要铺"。
            //     而 `时间轴要求` / `读条要求` 是**明确的即将来伤害** ——
            //     哪怕在移动，那一发也是真要吃的，值得铺。
            //
            //     ⇒ 移动中：**只认时间轴 / 读条这两个强判据**。
            //       仅靠"敌人够多"就先别铺，站定了再铺不迟
            //       （罩子持续 15 秒以上，站定的那一刻铺完全来得及）。
            // ══════════════════════════════════════════════════════════
            if (是地面 && SpellUtil.在移动() && !时间轴要求 && !读条要求) return -6;

            // ⚠️ 队伍里坦克正在假死 → 这段时间整体压力不大，罩子先留着
            //    （参考同类 ACR 的罩子 Check 里的 409/811/810）
            try
            {
                var 主坦 = HealTargetHelper.血量最低的坦克();
                if (主坦 != null && 主坦.处于假死状态()) return -5;
            }
            catch { }

        return SpellUtil.可用(_t.团队减伤) ? 15 : -1;
    }

    public void Build(Slot slot)
    {
        try
        {
            // ── 地面放置型技能的位置选择（参考同类 ACR 的 Qt「脚下放罩」）──
            //   · 勾上：放**自己脚下**（小怪散开时更稳，自己一定在队伍里）
            //   · 不勾：放**当前目标处**（Boss 战放 Boss 脚下，近战都在那）
            //
            // 自动识别"这个技能是不是要放地上"（对照 CastType=7），
            // 这样以后加新的地面技能不用再去改表。
            //
            // ⚠️ `Check` 里也有一份同名的 —— 两个方法各自作用域，不冲突。
            //    那边的用途是"移动中不铺"的判断（见 Check 里的说明）。
            var 是地面 = 技能数据.是地面技能(_t.团队减伤);

            var 放脚下 = HealQt.GetQt("脚下放罩", false);

            if (放脚下)
            {
                // 地面技能需要指定坐标
                slot.Add(new Spell(_t.团队减伤, Core.Me.Position));
                return;
            }

            var spell = SpellUtil.Get(_t.团队减伤);
            if (spell == null) return;

            // ── 延迟放置（参考同类 ACR 的 Slot.AddDelaySpell）──
            //   450ms 是它用的值：等动画锁结束再放，避免"技能按了但没落下去"。
            //   地面技能尤其容易吃这个亏 —— 按下去人一动，位置就飘了。
            //
            // ══════════════════════════════════════════════════════════
            //  ★ 选位：**优先跟当前目标**（IL 直证参考实现的做法）★
            //
            //  ── 参考实现根本不是"判断敌人稳不稳" ──
            //     它的落点函数（`GroundCastPosition`）只有三档：
            //       ① `preferCenter`     → 场地中心
            //       ② `cheatMode`        → 目标位置的场地中心
            //       ③ `preferTargetPosition` → **当前目标的位置**
            //     三个开关全关时才退回**自己脚下**。
            //
            //     它的 `HasEnemyMovedLessThan` / 敌人位置历史那些
            //     在整个 A 里**0 命中** —— 那是参考 B 的东西，
            //     而且 B 用它只是为了挑"连环计"的目标，不是给地面技能选位。
            //
            //  ── 我们原来错在哪 ──
            //     我们的 `敌人移动检测.地面技能位置()` 要求敌人
            //     "稳定观察满 3 秒"，道中小怪一直在动 → **永远不满足**
            //     → 永远退回"放自己脚下" → 移动中铺在自己脚下。
            //
            //  ⇒ 照抄它的做法：**落点跟目标走**。
            //     目标（被坦克拉住的怪 / Boss）比玩家**更不容易走开**，
            //     所以"人一走罩子白铺"这个问题自然就没了。
            //
            //  ⚠️ `HitboxRadius < 15f` 这条是参考实现的**硬常量**，别省 ——
            //     它是为了排除超大体积的 Boss：那种目标脚下给的坐标
            //     可能落在它的模型内部/场地外，罩子会放歪。
            // ══════════════════════════════════════════════════════════
            var 落点 = 敌人移动检测.地面技能位置();

            try
            {
                // ⚠️ 用我们自己的 `活着()` 而不是 `IsValid` ——
                //    `IsValid` 在这个项目里是 AEAssist 的扩展方法，
                //    本文件没有那个 using，写了会编译不过（实测）。
                //    `活着()` 只是 `CurrentHp > 0`，对这个判断足够：
                //    我们要的是"目标还在、能给它脚下放东西"。
                var 目标 = 输出目标.选();
                if (目标 != null && 目标.活着() && 目标.HitboxRadius < 15f)
                    落点 = 目标.Position;
            }
            catch { }

            slot.AddDelaySpell(450, new Spell(_t.团队减伤, 落点));
        }
        catch (Exception e)
        {
            LogHelper.Info("[HealerACR] 团减放置失败，退回普通放置：" + e.Message);

            var 兜底 = SpellUtil.Get(_t.团队减伤);
            if (兜底 != null) slot.Add(兜底);
        }
    }
}

/// <summary>个人减伤。默认没进队列，想用自己加。</summary>
public class Res_SelfMitigation : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_SelfMitigation(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("减伤", true)) return -100;
        if (!HealSettings.Instance.自动减伤) return -101;
        if (_t.个人减伤 == 0) return -102;
        if (!SpellUtil.已解锁(_t.个人减伤)) return -2;

        var 时间轴要求 = HealQt.GetQt("时间轴", true) && TimelineManager.该铺减伤();
        // ★ 用**有效血量**：自己身上有厚盾时不算"血少"，不该再交个人减伤
        var 自己血少 = AEAssist.Core.Me.有效血量比例() <= 0.6f;

        if (!时间轴要求 && !自己血少 && !减伤Helper.即将来大伤害()) return -1;

        return SpellUtil.可用(_t.个人减伤) ? 8 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(_t.个人减伤);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>群体护盾（预铺）：贤者的均衡预后。</summary>
public class Res_GroupShield : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_GroupShield(JobSpellTable table) => _t = table;

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("群盾", false)) return -101;
        if (_t.群体盾 == 0) return -102;
        if (!SpellUtil.已解锁(_t.群体盾)) return -2;

        var 时间轴要求 = HealQt.GetQt("时间轴", true) && TimelineManager.该铺减伤();
        if (!时间轴要求 && !减伤Helper.即将来大伤害()) return -1;

        // 对应 外部 ACR SuperAOEHeal 的"检测队友身上有没有盾"：
        // 血线够低、身上又没盾的人，才值得铺；都有盾了就跳过，别重叠浪费
        var s = HealSettings.Instance;
        // 排除坦克：它们有自己的减伤体系，群盾留给其他人更划算。
        // 参考同类 ACR 的"检测周围非T队内玩家身上有盾"——它里面也是先 IsTank 过滤。
        //
        // ⚠️ 半径必须传 **20 米**（不传会落到默认 30）——
        //    贤者群盾「预后」24286 的 EffectRange 是 20 米。
        //    按 30 米算会把 20 米外的人算成"缺盾"，交掉一个 GCD 群盾却漏人。
        var 需要盾 = HealTargetHelper.可治疗队友(20f)
            .Count(r => !r.IsTank()
                        && r.有效血量比例() <= s.群体治疗阈值   // ★ 有效血量
                        && !r.有该技能的Buff(_t.群体盾));
        if (需要盾 < 1) return -1;

        // ══════════════════════════════════════════════════════════════
        //  ★ 护盾前置（贤者「均衡」）必须**在这里**判，不能只在 Build 里加 ★
        //
        //  ── 原来错在哪（审计发现）──
        //    `Build` 无条件把均衡塞进 slot 的第一个位置（只要不在均衡状态），
        //    **从不检查均衡能不能放**。
        //    ⇒ 均衡在 CD 时也会被塞进去 → 那个 slot 的第一个动作是无效的，
        //      而且 `SGE_Dot` 的注释明确写过
        //      "同一帧里按完均衡就立刻放'均衡注药'是放不出来的" ——
        //      盾可能因此**根本出不来**（贤者靠盾吃饭，这很致命）。
        //
        //  ── 修法 ──
        //    在 Check 里判"要不要用前置、用了能不能放"，
        //    Build 只负责照 Check 的结论放（开发约定 F③ 同源）。
        // ══════════════════════════════════════════════════════════════
        if (_t.护盾前置 != 0 && !JobApiHelper.均衡中)
        {
            if (!SpellUtil.可用(_t.护盾前置)) return -1;
        }

        return SpellUtil.可用(_t.群体盾) ? 11 : -1;
    }

    public void Build(Slot slot)
    {
        // ⚠️ 必须和 Check 同源：Check 已经判过"要用前置且前置可用"，
        //    这里只重放同样的条件（判 A 放 A）
        if (_t.护盾前置 != 0 && !JobApiHelper.均衡中 && SpellUtil.可用(_t.护盾前置))
        {
            var pre = SpellUtil.Get(_t.护盾前置);
            if (pre != null) slot.Add(pre);
        }

        // 用当前形态：贤者的"预后"在均衡状态下会变成"均衡预后"
        var 盾 = SpellUtil.当前形态(_t.群体盾);
        if (盾 == null) return;
        slot.Add(new Spell(盾.Id, SpellTargetType.Self));
    }
}
