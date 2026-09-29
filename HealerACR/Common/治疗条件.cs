using AEAssist;
using AEAssist.CombatRoutine.Trigger;
using AEAssist.Extension;
using Dalamud.Bindings.ImGui;

namespace HealerACR.Common;

/// <summary>
/// 治疗条件 —— 参考同类 ACR 用的 `Rotation.AddTriggerCondition`。
///
/// ══════════════════════════════════════════════════════════════════
///  Trigger 体系有两半：
///    · **Action**（上轮做了）—— 触发时**做什么**
///    · **Condition**（这个文件）—— 判断**该不该触发**
///
///  官方文档（L109）提到：
///    "如果想确保用户不会填一些你预期之外的参数值，
///      可以让你的条件/行为类额外实现一个接口"
///
///  所以 Condition 通常带 **UI 配置**（Draw 方法）——
///  让用户在时间轴编辑器里填参数（比如"血量低于多少"）。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class 血量低于条件 : ITriggerCond
{
    public string DisplayName => "治疗：有人血量低于";

    public string Remark { get; set; } = "";

    /// <summary>用户可配的阈值（百分比）</summary>
    private int _阈值 = 60;

    public bool Handle(ITriggerCondParams? condParams = null)
    {
        try
        {
            return HealTargetHelper.低于阈值人数(_阈值 / 100f) > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>配置界面 —— 用户在时间轴编辑器里填阈值</summary>
    public bool Draw()
    {
        ImGui.Text("血量低于");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(120);
        var 变了 = ImGui.SliderInt("##阈值", ref _阈值, 10, 95, "%d%%");

        if (变了) Remark = $"{_阈值}%";

        return 变了;
    }
}

/// <summary>死亡人数条件 —— "有 N 个人躺着"</summary>
public class 死亡人数条件 : ITriggerCond
{
    public string DisplayName => "治疗：死亡人数达到";

    public string Remark { get; set; } = "";

    private int _人数 = 1;

    public bool Handle(ITriggerCondParams? condParams = null)
    {
        try
        {
            var 躺着的 = 0;
            foreach (var r in PartyHelper.DeadAllies)
            {
                if (r != null) 躺着的++;
            }
            return 躺着的 >= _人数;
        }
        catch
        {
            return false;
        }
    }

    public bool Draw()
    {
        ImGui.Text("死亡人数 ≥");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(120);
        var 变了 = ImGui.SliderInt("##人数", ref _人数, 1, 8, "%d 人");

        if (变了) Remark = $"{_人数} 人";

        return 变了;
    }
}

/// <summary>资源条件 —— "以太 ≥ N"（用来卡爆发时机）</summary>
public class 资源条件 : ITriggerCond
{
    public string DisplayName => "治疗：资源数量达到";

    public string Remark { get; set; } = "";

    private int _数量 = 2;

    public bool Handle(ITriggerCondParams? condParams = null)
    {
        try
        {
            // 以太超流 buff 304
            return JobApiHelper.以太 >= _数量;
        }
        catch
        {
            return false;
        }
    }

    public bool Draw()
    {
        ImGui.Text("以太 ≥");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(120);
        var 变了 = ImGui.SliderInt("##资源", ref _数量, 0, 3, "%d 颗");

        if (变了) Remark = $"{_数量} 颗";

        return 变了;
    }
}
