using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.View.JobView;
using AEAssist.Helper;
using Dalamud.Bindings.ImGui;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// QT 面板 —— 实时显示 AI 在干什么。
///
/// 和 HealerACR 的「调试页」一个思路：**把 AI 的内部状态摊开给你看**，
/// 而不是让你对着日志猜。
/// </summary>
public class BlueWhaleViewWindow : IRotationUI
{
    private JobViewWindow? _窗口;

    public JobViewWindow GetJobViewWindow()
    {
        if (_窗口 != null) return _窗口;

        _窗口 = new JobViewWindow(new JobViewSave(), () => { }, "BlueWhale");
        _窗口.AddQt("AI总开关", false);
        _窗口.AddQt("策略层", false);
        _窗口.AddQt("决策层", false);

        _窗口.AddTab("AI 状态", w => 画状态());

        return _窗口;
    }

    /// <summary>主界面绘制（AEAssist 会调）</summary>
    public void OnDrawUI()
    {
        GetJobViewWindow().DrawQtWindow();
    }

    public void OnDrawSetting() { }

    public void Dispose() { }

    private static void 画状态()
    {
        try
        {
            var s = AiSettings.Instance;

            // ---- 开关 ----
            var 总开关 = s.需要AI;
            if (ImGui.Checkbox("AI 总开关（两个层任一启用）", ref 总开关))
            {
                s.启用策略层 = 总开关;
                s.启用决策层 = 总开关;
                AiSettings.保存();
            }

            var 策略 = s.启用策略层;
            if (ImGui.Checkbox("策略层（阶段 A）", ref 策略)) { s.启用策略层 = 策略; AiSettings.保存(); }

            var 决策 = s.启用决策层;
            if (ImGui.Checkbox("决策层（阶段 B）", ref 决策)) { s.启用决策层 = 决策; AiSettings.保存(); }

            ImGui.Separator();

            // ---- 连接健康度 ----
            ImGui.TextDisabled("连接");
            ImGui.Text($"  Key 已配置：{(s.已配置 ? "是" : "否")}");
            ImGui.Text($"  熔断中：{(DeepSeekClient.熔断中 ? "是 ⚠️" : "否")}");
            ImGui.Text($"  连续失败：{DeepSeekClient.连续失败数}");

            if (ImGui.Button("解除熔断")) DeepSeekClient.解除熔断();

            ImGui.Separator();

            // ---- 策略层状态 ----
            ImGui.TextDisabled("策略层（阶段 A）");
            ImGui.Text("  " + AiThresholdAdapter.状态描述());
            ImGui.Text($"  思考中：{(AiStrategyLayer.刷新中 ? "是" : "否")}");

            ImGui.Separator();

            // ---- 决策层状态 ----
            ImGui.TextDisabled("决策层（阶段 B）");
            var 建议 = AiDecisionLayer.当前建议;

            if (建议 != null)
            {
                ImGui.Text($"  建议技能：{建议.技能Id}");
                ImGui.Text($"  理由：{建议.理由}");
                ImGui.Text($"  剩余有效期：{Math.Max(0, AiDecisionLayer.建议.有效期毫秒 - (TimeHelper.Now() - 建议.生成时间))} ms");
            }
            else
            {
                ImGui.TextDisabled("  暂无可用建议");
            }

            ImGui.Text($"  命中 {AiDecisionLayer.命中次数} 次 / 过期 {AiDecisionLayer.过期次数} 次");
            ImGui.Text($"  预取中：{(AiDecisionLayer.预取中 ? "是" : "否")}");

            ImGui.Separator();

            // ---- 说明 ----
            ImGui.TextDisabled("说明");
            ImGui.TextWrapped("阶段 A 只调阈值（相对量，幅度上限 ±0.10），不会替你决定具体技能。");
            ImGui.TextWrapped("阶段 B 会给出具体技能建议，但只有 2.5 秒有效期 —— 过期就丢弃，退回原逻辑。");
            ImGui.TextWrapped("API 失败一律降级，不影响战斗。");
        }
        catch (Exception e)
        {
            ImGui.TextDisabled("状态读取失败：" + e.Message);
        }
    }
}
