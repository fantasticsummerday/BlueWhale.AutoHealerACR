using AEAssist.CombatRoutine.View.JobView;
using HealerACR.Common;

namespace HealerACR.Common;

/// <summary>
/// 每个职业自己的 QT 页（对照 鍚岀被 ACR 的 ASTOverlay / BLM_QT 那种做法）。
///
/// 0.2.1 起每页顶部都会**实时显示该职业的资源数值** ——
/// 排查"技能不触发"时，第一件事就是看资源到底读到没有。
/// </summary>
public static class 职业面板
{
    public static void 画(Jobs job, JobViewWindow window)
    {
        switch (job)
        {
            case Jobs.WhiteMage:
                白魔页();
                break;
            case Jobs.Scholar:
                学者页();
                break;
            case Jobs.Astrologian:
                占星页();
                break;
            case Jobs.Sage:
                贤者页();
                break;
            default:
                ImGui.TextDisabled("这个职业还没有专属页");
                break;
        }

        // 调试页（对照 鍚岀被 ACR 的 Overlay.DrawDev）—— 折叠面板，默认收起
        调试页();
    }

    /// <summary>实时资源条（读不到会标红提示）</summary>
    private static void 资源状态(string 名字, int 值, int 上限, bool 读得到)
    {
        if (!读得到)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f), $"{名字}：读取失败（看日志）");
            return;
        }

        ImGui.Text($"{名字}：{值} / {上限}");
        ImGui.SameLine();

        // 按比例压缩成固定长度的条。
        // 之前是直接按数值画字符 —— 妖精能量上限 100 就真画 100 个圈，长得没法看。
        // 上限小的（百合/蛇胆这种 3 格的）保持原样，大的统一压成 10 格。
        var 条长 = 上限 <= 8 ? Math.Max(1, 上限) : 10;
        var 填 = 上限 <= 0
            ? 0
            : (int)Math.Round((double)Math.Clamp(值, 0, 上限) / 上限 * 条长);

        ImGui.TextDisabled(new string('●', 填) + new string('○', Math.Max(0, 条长 - 填)));

        // 大数值额外给个百分比，光看 10 格条不够精确
        if (上限 > 8)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"({(上限 <= 0 ? 0 : 值 * 100 / 上限)}%)");
        }
    }

    // ==================== 白魔 ====================

    private static void 白魔页()
    {
        var s = HealSettings.Instance;

        ImGui.Text("实时资源");
        ImGui.Separator();
        资源状态("百合", JobApiHelper.百合, 3, JobApiHelper.读得到("百合"));
        资源状态("血百合", JobApiHelper.血百合, 3, JobApiHelper.读得到("血百合"));
        if (JobApiHelper.百合 >= 3)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.9f, 0.3f, 1f),
                "→ 百合已满：下一轮 GCD 会自动卸掉一颗（无视治疗开关）");
        }

        ImGui.Separator();
        ImGui.Text("百合 / 血百合");
        ImGui.Separator();
        ImGui.TextDisabled("百合每 30 秒攒 1 颗，花掉免蓝；每花 1 颗攒 1 颗血百合，满 3 打苦难之心。");
        ImGui.Checkbox("用苦难之心（血百合满 3 颗自动打）", ref s.用苦难之心);

        ImGui.SetNextItemWidth(240);
        ImGui.SliderFloat("百合使用血线", ref s.百合使用血线, 0.3f, 1.0f, "%.2f");
        ImGui.TextDisabled("低于这个血线才动百合；满了（3 颗）则无条件卸");
    }

    // ==================== 学者 ====================

    private static void 学者页()
    {
        var s = HealSettings.Instance;

        ImGui.Text("实时资源");
        ImGui.Separator();
        资源状态("以太", JobApiHelper.以太, 3, JobApiHelper.读得到("以太"));
        资源状态("妖精能量", JobApiHelper.妖精能量, 100, JobApiHelper.读得到("妖精能量"));
        ImGui.Text($"小仙女：{(JobApiHelper.有小仙女 ? "在场" : "不在场")}    炽天使剩余：{JobApiHelper.炽天使剩余}");

        ImGui.Separator();
        ImGui.Text("以太");
        ImGui.Separator();
        ImGui.SetNextItemWidth(240);
        ImGui.SliderInt("以太保留数", ref s.以太保留数, 0, 3);
        ImGui.TextDisabled("低于这个数就不放「能力技群奶」；木桩/脱战都会自动补");

        ImGui.Separator();
        ImGui.Text("小仙女 / 炽天使");
        ImGui.Separator();
        ImGui.SetNextItemWidth(240);
        ImGui.SliderFloat("妖精契约血线", ref s.妖精契约血线, 0.3f, 1.0f, "%.2f");
        ImGui.TextDisabled("仙光的低语（群奶 HoT）/ 异想的幻光（减伤）会自动放");
    }

    // ==================== 占星 ====================

    private static void 占星页()
    {
        var s = HealSettings.Instance;

        ImGui.Text("实时资源");
        ImGui.Separator();
        资源状态("手牌", JobApiHelper.手牌数, 2, JobApiHelper.读得到("手牌"));
        var 牌 = JobApiHelper.手牌名字();
        if (牌.Length > 0) ImGui.TextDisabled("手上的牌：" + string.Join("、", 牌));
        ImGui.Text($"有大阿卡纳：{(JobApiHelper.有大阿卡纳() ? "是" : "否")}");

        ImGui.Separator();
        ImGui.Text("卡牌");
        ImGui.Separator();
        ImGui.Checkbox("出卡优先给近战", ref s.出卡优先近战);
        ImGui.TextDisabled("判断依据是 CardType 的运行名字 + 设置里的「近战卡关键词」");

        ImGui.Separator();
        ImGui.Text("地星");
        ImGui.Separator();
        ImGui.SetNextItemWidth(240);
        ImGui.SliderFloat("地星提前秒", ref s.地星提前秒, 4f, 12f, "%.1f");
        ImGui.TextDisabled("时间轴预报到这么久后有大伤害就提前铺；没时间轴时多人掉血也放");
    }

    // ==================== 贤者 ====================

    private static void 贤者页()
    {
        var s = HealSettings.Instance;

        ImGui.Text("实时资源");
        ImGui.Separator();
        资源状态("蛇胆", JobApiHelper.蛇胆, 3, JobApiHelper.读得到("蛇胆"));
        资源状态("毒刺", JobApiHelper.毒刺, 3, JobApiHelper.读得到("毒刺"));
        ImGui.Text($"均衡中：{(JobApiHelper.均衡中 ? "是" : "否")}");
        if (JobApiHelper.蛇胆 >= 3)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.9f, 0.3f, 1f),
                "→ 蛇胆已满：会自动用灵橡清汁卸掉一颗");
        }

        ImGui.Separator();
        ImGui.Text("蛇胆 / 毒刺");
        ImGui.Separator();
        ImGui.SetNextItemWidth(240);
        ImGui.SliderInt("蛇胆保留数", ref s.蛇胆保留数, 0, 3);
        ImGui.TextDisabled("低于这个数就不放「能力技群奶」；根素在 0 颗时补");

        ImGui.SetNextItemWidth(240);
        ImGui.SliderInt("箭毒泄刺阈值", ref s.箭毒泄刺阈值, 1, 3);
        ImGui.TextDisabled("毒刺攒到这个数就泄掉，避免溢出");
    }

    /// <summary>
    /// 调试页 —— 对照 鍚岀被 ACR 的 Overlay.DrawDev。
    ///
    /// **为什么值得做**：前面几轮排查"能力技为什么不打"时，反复需要知道
    /// GCD 剩多少 / 能不能插能力技 / 以太读到几 —— 每次都靠加日志、跑一轮、
    /// 再把日志发回来。有这一页，面板上一眼就能看出卡在哪一环。
    /// </summary>
    private static void 调试页()
    {
        try
        {
            ImGui.Separator();
            if (!ImGui.CollapsingHeader("调试信息")) return;

            ImGui.TextDisabled("GCD / 能力技窗口");
            ImGui.Text("  GCD 可用：" + GCDHelper.CanUseGCD());
            ImGui.Text("  GCD 剩余：" + GCDHelper.GetGCDCooldown() + " ms");
            ImGui.Text("  可插能力技：" + CharacterExt.可以插能力技());
            ImGui.TextDisabled("  （< 600ms 才算能插，这是判断能力技放不放的关键）");

            ImGui.Separator();
            ImGui.TextDisabled("学者资源（其他职业显示 0 是正常的）");
            ImGui.Text("  以太：" + JobApiHelper.以太);
            ImGui.Text("  以太 buff304 层数：" + Core.Me.GetAuraStack(304));
            ImGui.Text("  小仙女：" + (JobApiHelper.有小仙女 ? "在场" : "不在场"));
            ImGui.Text("  转化中：" + (AuraIds.转化中 != 0 && Core.Me.HasAura(AuraIds.转化中)));

            ImGui.Separator();
            ImGui.TextDisabled("目标");
            var 目标 = HealTargetHelper.当前目标();
            if (目标 == null)
            {
                ImGui.Text("  当前目标：无");
            }
            else
            {
                var 是Boss = false;
                try { 是Boss = 目标.IsBoss(); } catch { }
                ImGui.Text("  当前目标：" + (是Boss ? "Boss" : "普通") +
                           "  血量 " + (目标.血量比例() * 100f).ToString("F0") + "%");
                ImGui.Text("  周围敌人：" + HealTargetHelper.周围敌人数量());

                // ── 敌人移动检测状态（地面技能选位依据）──
                //    "观察中" = 还没攒够数据，地面技能会退回放自己脚下
                //    "已就绪" = 判定可用，如果它在动就会放你脚下
                ImGui.Text("  移动检测：" + HealerACR.Common.敌人移动检测.状态描述(目标));
                ImGui.TextDisabled("  （观察满 3 秒 + 采样 4 个才算就绪；靠近中的敌人直接排除）");
            }
        }
        catch (Exception e)
        {
            ImGui.TextDisabled("调试信息读取失败：" + e.Message);
        }
    }
}
