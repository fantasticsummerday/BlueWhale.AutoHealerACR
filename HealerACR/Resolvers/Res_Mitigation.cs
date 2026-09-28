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

        var 时间轴要求 = HealQt.GetQt("时间轴", true) && TimelineManager.该铺减伤();
        var 读条要求 = 减伤Helper.即将来大伤害();


            // ══════════════════════════════════════════════════════════
            //  ⚠️ 新增第三个触发条件：敌人够多时下罩子
            //
            //  用户实测（50 级主随 · 土神转场那类 AOE）：该下罩子却没下。
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
            //   用户实测：距离衰减 AOE 没触发罩子。需要看清楚是哪个条件没满足。
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

            // ⚠️ 队伍里坦克正在假死 → 这段时间整体压力不大，罩子先留着
            //    （对照 鍚岀被 ACR 罩子 Check 里的 409/811/810）
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
            // ── 地面放置型技能的位置选择（对照 鍚岀被 ACR 的 Qt「脚下放罩」）──
            //   · 勾上：放**自己脚下**（小怪散开时更稳，自己一定在队伍里）
            //   · 不勾：放**当前目标处**（Boss 战放 Boss 脚下，近战都在那）
            // 自动识别"这个技能是不是要放地上"（对照 CastType=7）
            // 这样以后加新的地面技能不用再去改表
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

            // ── 延迟放置（对照 鍚岀被 ACR 的 Slot.AddDelaySpell）──
            //   450ms 是它用的值：等动画锁结束再放，避免"技能按了但没落下去"。
            //   地面技能尤其容易吃这个亏 —— 按下去人一动，位置就飘了。
            // ── 选位（对照 鍚岀被 ACR 的 GetOptimalTargetWithEnemyMovementCheck）──
            //   敌人站得稳 → 放它脚下；它在动 → 放自己脚下。
            //   这样不会出现"罩子扔下去、敌人跑了、技能白放"。
            var 落点 = 敌人移动检测.地面技能位置();

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
        var 自己血少 = AEAssist.Core.Me.血量比例() <= 0.6f;

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

        // 对应 鍚岀被 ACR SuperAOEHeal 的"检测队友身上有没有盾"：
        // 血线够低、身上又没盾的人，才值得铺；都有盾了就跳过，别重叠浪费
        var s = HealSettings.Instance;
        // 排除坦克：它们有自己的减伤体系，群盾留给其他人更划算。
        // 对照 鍚岀被 ACR 的"检测周围非T队内玩家身上有盾"——它里面也是先 IsTank 过滤。
        var 需要盾 = HealTargetHelper.可治疗队友()
            .Count(r => !r.IsTank()
                        && r.血量比例() <= s.群体治疗阈值
                        && !r.有该技能的Buff(_t.群体盾));
        if (需要盾 < 1) return -1;

        return SpellUtil.可用(_t.群体盾) ? 11 : -1;
    }

    public void Build(Slot slot)
    {
        if (_t.护盾前置 != 0 && !JobApiHelper.均衡中)
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
