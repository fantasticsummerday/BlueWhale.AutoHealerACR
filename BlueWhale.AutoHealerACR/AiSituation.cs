using System.Text;
using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;
using HealerACR.Timeline;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// 局面采集 —— 把"游戏里实际发生了什么"翻译成 AI 能读的文字。
///
/// **这是整个 AI 接管的地基。**
/// 喂给 AI 的信息越全，它的决策越靠谱；信息不够它就只能在幻觉里编。
///
/// 采集范围对照 HealerACR 的调试页和 鍚岀被 ACR 的 Overlay.DrawDev ——
/// 那些面板上能看到的东西，AI 也应该能看到。
/// </summary>
public static class AiSituation
{
    public static string 采集()
    {
        var sb = new StringBuilder();

        采副本(sb);
        采自己(sb);
        采资源(sb);
        采队友(sb);
        采敌人(sb);
        采时间轴(sb);
        采可选技能(sb);

        return sb.ToString();
    }

    // ==================== 副本 ====================

    /// <summary>
    /// 采集"现在在哪个副本"。
    ///
    /// ⚠️ **只给事实，不给推测。**
    ///    副本 ID 和名字是游戏数据，AI 可以据此知道"这是哪个本"，
    ///    但**绝不能让它去"回忆"这个本的机制** —— 那是幻觉的源头。
    /// </summary>
    private static void 采副本(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("【副本】");

            // 地图 ID 从 TimelineManager 拿（它内部已经维护了当前地图）
            var 地图 = TimelineManager.当前地图Id;
            sb.AppendLine($"地图 ID：{地图}");

            // 时间轴文件名往往就带副本名 —— 这是最可靠的信息源
            var 状态 = TimelineManager.状态;
            if (!string.IsNullOrWhiteSpace(状态))
            {
                sb.AppendLine($"时间轴：{状态}");
            }

            if (地图 == 0)
            {
                sb.AppendLine("（不在副本里 / 地图信息读不到）");
            }

            sb.AppendLine();
        }
        catch (Exception e)
        {
            sb.AppendLine("（副本信息读取失败：" + e.Message + "）");
            sb.AppendLine();
        }
    }

    // ==================== 自己 ====================

    private static void 采自己(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("【我】");
            sb.AppendLine($"职业：{Core.Me.ClassJob.Value.Name}");
            sb.AppendLine($"血量：{(Core.Me.CurrentHp * 100f / Math.Max(1, Core.Me.MaxHp)):F0}%");
            sb.AppendLine($"蓝量：{Core.Me.CurrentMp}");
            sb.AppendLine($"战斗中：{(Core.Me.InCombat() ? "是" : "否")}");
            sb.AppendLine($"GCD 剩余：{GCDHelper.GetGCDCooldown()} ms");
            sb.AppendLine($"能否插能力技：{(GCDHelper.GetGCDCooldown() < 600 ? "是" : "否")}");
            sb.AppendLine();
        }
        catch (Exception e)
        {
            sb.AppendLine("（自己状态读取失败：" + e.Message + "）");
        }
    }

    // ==================== 职业资源 ====================

    private static void 采资源(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("【职业资源】");

            var 有 = false;

            // 学者
            if (JobApiHelper.读得到("以太"))
            {
                sb.AppendLine($"以太（0-3）：{JobApiHelper.以太}");
                sb.AppendLine($"小仙女在场：{(JobApiHelper.有小仙女 ? "是" : "否")}");
                sb.AppendLine($"炽天使剩余：{JobApiHelper.炽天使剩余}");
                sb.AppendLine($"妖精能量：{JobApiHelper.妖精能量}");
                有 = true;
            }

            // 白魔
            if (JobApiHelper.读得到("百合"))
            {
                sb.AppendLine($"百合（0-3）：{JobApiHelper.百合}");
                sb.AppendLine($"血百合（0-3）：{JobApiHelper.血百合}");
                有 = true;
            }

            // 贤者
            if (JobApiHelper.读得到("蛇胆"))
            {
                sb.AppendLine($"蛇胆（0-3）：{JobApiHelper.蛇胆}");
                sb.AppendLine($"毒刺（0-3）：{JobApiHelper.毒刺}");
                sb.AppendLine($"均衡中：{(JobApiHelper.均衡中 ? "是" : "否")}");
                有 = true;
            }

            // 占星
            if (JobApiHelper.读得到("手牌数"))
            {
                sb.AppendLine($"手牌数：{JobApiHelper.手牌数}");
                sb.AppendLine($"有近战卡：{(JobApiHelper.有近战卡() ? "是" : "否")}");
                有 = true;
            }

            if (!有) sb.AppendLine("（当前职业没有可读的资源，或是别的职业）");

            sb.AppendLine();
        }
        catch (Exception e)
        {
            sb.AppendLine("（资源读取失败：" + e.Message + "）");
        }
    }

    // ==================== 队友 ====================

    private static void 采队友(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("【队伍】");

            var 队友 = PartyHelper.CastableAlliesWithin30;
            if (队友 == null || 队友.Count == 0)
            {
                sb.AppendLine("（没有可施法的队友 —— 可能单人在打木桩）");
                sb.AppendLine();
                return;
            }

            var 低血 = 0;
            var 危急 = 0;

            foreach (var r in 队友)
            {
                if (r == null) continue;

                var 比例 = r.MaxHp > 0 ? r.CurrentHp * 100f / r.MaxHp : 100f;

                var 标记 = "";
                try
                {
                    if (r.IsTank()) 标记 = "[坦克]";
                    else if (r.IsHealer()) 标记 = "[治疗]";
                }
                catch { }

                sb.AppendLine($"  {r.Name}{标记}：{比例:F0}%");

                if (比例 <= 70f) 低血++;
                if (比例 <= 35f) 危急++;
            }

            sb.AppendLine();
            sb.AppendLine($"低于 70%：{低血} 人");
            sb.AppendLine($"低于 35%：{危急} 人");

            // 坦克单独列 —— 治疗最关心它
            var 坦克 = HealTargetHelper.血量最低的坦克();
            if (坦克 != null)
            {
                sb.AppendLine($"最危险的坦克：{坦克.Name}（{坦克.血量比例() * 100f:F0}%）");
            }

            // 待复活 / 待驱散
            var 待复活 = HealTargetHelper.待复活队友();
            if (待复活 != null) sb.AppendLine($"⚠️ 有队友倒地待复活：{待复活.Name}");

            var 待驱散 = HealTargetHelper.需要驱散队友();
            if (待驱散 != null) sb.AppendLine($"⚠️ 有队友需要驱散：{待驱散.Name}");

            sb.AppendLine();
        }
        catch (Exception e)
        {
            sb.AppendLine("（队伍读取失败：" + e.Message + "）");
        }
    }

    // ==================== 敌人 ====================

    private static void 采敌人(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("【敌人】");

            var 目标 = HealTargetHelper.当前目标();

            if (目标 == null)
            {
                sb.AppendLine("当前没有选中目标");
                sb.AppendLine();
                return;
            }

            var 是Boss = false;
            try { 是Boss = 目标.IsBoss(); } catch { }

            sb.AppendLine($"当前目标：{目标.Name}");
            sb.AppendLine($"  类型：{(是Boss ? "Boss" : "普通怪")}");
            sb.AppendLine($"  血量：{目标.血量比例() * 100f:F0}%（{目标.CurrentHp} / {目标.MaxHp}）");

            var 快死了 = HealTargetHelper.目标快死了();
            if (快死了) sb.AppendLine("  ⚠️ 这个目标快死了（预估 12 秒内会死）—— 别在它身上浪费爆发");

            sb.AppendLine($"周围敌人数量（5 米内）：{HealTargetHelper.周围敌人数量()}");

            // AOE 判断：能用 AOE 打到几个
            var 可AOE = HealTargetHelper.周围敌人数量(8f) >= 3;
            sb.AppendLine($"适合放 AOE：{(可AOE ? "是（≥3 个）" : "否")}");

            sb.AppendLine();
        }
        catch (Exception e)
        {
            sb.AppendLine("（敌人读取失败：" + e.Message + "）");
        }
    }

    // ==================== 时间轴 ====================

    private static void 采时间轴(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("【时间轴】");

            if (!HealSettings.Instance.启用时间轴)
            {
                sb.AppendLine("（时间轴未启用）");
                sb.AppendLine();
                return;
            }

            sb.AppendLine($"本帧该铺减伤：{(TimelineManager.该铺减伤() ? "是 ⚠️" : "否")}");

            var 未来5秒 = TimelineManager.未来有减伤(5.0);
            sb.AppendLine($"未来 5 秒有减伤需求：{(未来5秒 ? "是 ⚠️" : "否")}");

            var 距下次 = TimelineManager.距下次减伤();
            if (距下次 > 0 && 距下次 < 30)
            {
                sb.AppendLine($"距下次减伤：{距下次:F1} 秒");
            }

            sb.AppendLine();
        }
        catch (Exception e)
        {
            sb.AppendLine("（时间轴读取失败：" + e.Message + "）");
        }
    }

    // ==================== 可选技能清单 ====================

    /// <summary>
    /// 采集**当前职业实际会用的技能清单**（ID + 中文名 + 用途分类）。
    ///
    /// **这是防幻觉的最后一道闸。**
    /// 阶段 B 让 AI 输出技能 ID —— 不给清单它就凭记忆编，
    /// 编错的后果是一次无效施放，甚至放出不该放的技能。
    ///
    /// 有了清单，提示词里就能写"只能从这里选"，
    /// 而且返回后还能做白名单校验（见 AiDecisionLayer.解析）。
    /// </summary>
    private static void 采可选技能(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("【可选技能（阶段 B 只能从这些 ID 里选）】");

            var 表 = HealerACR.Common.HealRotationEventHandler.当前技能表;
            if (表 == null)
            {
                sb.AppendLine("（拿不到技能表 —— 阶段 B 请输出 0|数据不足）");
                sb.AppendLine();
                return;
            }

            var 计数 = 0;

            void 加(string 类别, uint id)
            {
                if (id == 0) return;
                var 名 = SpellIds.反查(id);
                sb.AppendLine($"  {id} = {名}（{类别}）");
                计数++;
            }

            void 加数组(string 类别, uint[] ids)
            {
                if (ids == null) return;
                foreach (var id in ids) 加(类别, id);
            }

            加("基础输出", 表.基础输出);
            加("群体输出", 表.群体输出);
            加("DoT", 表.Dot技能);
            加("移动填充", 表.移动填充技);
            加("单体治疗", 表.单体治疗GCD);
            加("群体治疗", 表.群体治疗GCD);
            加("紧急单奶", 表.紧急单奶);
            加("群体治疗能力技", 表.群体治疗能力技);
            加("单体盾", 表.单体盾);
            加("群体盾", 表.群体盾);
            加("团队减伤", 表.团队减伤);
            加("复活", 表.复活);
            加("驱散", 表.驱散);
            加("醒梦", 表.醒梦);

            加数组("输出能力技", 表.输出能力技);
            加数组("脱战准备", 表.脱战准备技能);

            sb.AppendLine($"（共 {计数} 个）");
            sb.AppendLine();
        }
        catch (Exception e)
        {
            sb.AppendLine("（技能清单读取失败：" + e.Message + "）");
            sb.AppendLine();
        }
    }
}
