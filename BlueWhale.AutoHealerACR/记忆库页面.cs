using Dalamud.Bindings.ImGui;
using AEAssist.Helper;
using HealerACR.Common;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// 记忆库界面 —— 挂在 BlueWhale 的设置页里。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么需要这个界面（不是"顺便做的"）★
///
///    记录模式有几个**必须让用户看得见**的东西，否则会变成"黑盒"：
///
///      ① **ACR 现在停手了** —— 这很重要！用户可能忘了自己开过记录模式，
///         然后在副本里被打了才发现"怎么不奶我"。
///         必须有个显眼的地方一直提醒。
///      ② **正在记录多少条** —— 让用户知道它真的在工作。
///      ③ **提炼状态 / 失败原因** —— 提炼会调 AI，可能失败（没配 Key、
///         熔断、超时）。失败必须能看到原因，否则用户只会觉得"没生效"。
///      ④ **手动提炼按钮** —— 自动提炼有 20 秒延迟判定，
///         用户不想等、或者判定没触发时，得能手动来一下。
///
///  ⚠️ 界面里所有文字**都不带图标符号** —— FF14 字体不认（见交接文档偏好）。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 记忆库页面
{
    public static void 画()
    {
        try
        {
            ImGui.Separator();
            ImGui.Text("战斗记忆库");
            ImGui.TextDisabled("  记录你手动操作时的打法，提炼成规律，用来优化 AI");

            ImGui.Spacing();

            // ══════════════════════════════════════════════════════════
            //  ★ 记录模式状态 —— 必须最显眼 ★
            // ══════════════════════════════════════════════════════════
            if (记录模式.开启)
            {
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.75f, 0.2f, 1f),
                    "记录模式：已开启（ACR 不会输出也不会治疗，请自己操作）");

                if (记录模式.保留保命兜底)
                {
                    ImGui.TextColored(new System.Numerics.Vector4(1f, 0.5f, 0.5f, 1f),
                        "  注意：保命兜底开着 —— 记录里会混入 ACR 的操作，数据不纯");
                }

                ImGui.TextDisabled("  开关在 QT 面板的「记录模式」（可以绑快捷键）");
            }
            else
            {
                ImGui.TextDisabled("记录模式：未开启（ACR 正常运行）");
            }

            ImGui.Spacing();

            // ── 本局进度 ──
            if (记录模式.开启)
            {
                var 本局 = 对局记录.本局条数;
                var 脱战 = 对局收尾.脱战已过秒();

                ImGui.Text($"本局已记录：{本局} 条");

                if (脱战 >= 0)
                {
                    var 剩余 = 对局收尾.收尾延迟秒 - 脱战;
                    var 提示 = 剩余 > 0
                        ? $"脱战中 {脱战:F0}s —— 再过 {剩余:F0}s 自动提炼"
                        : "脱战中 —— 即将提炼";
                    ImGui.TextDisabled("  " + 提示);
                }

                ImGui.TextDisabled("  只记录有读条的技能；瞬发能力技记录不到（详见说明）");

                if (ImGui.Button("立即收尾并提炼"))
                {
                    对局收尾.立即收尾();
                }
            }

            ImGui.Spacing();

            // ── 库的概览 ──
            var (条目, 职业数, 最近) = 记忆库.概览();
            ImGui.Text($"记忆库：{条目} 条结论 / {职业数} 个职业");
            ImGui.TextDisabled($"  最近提炼：{最近}");
            ImGui.TextDisabled($"  提炼状态：{记忆库.状态}");

            if (对局收尾.上次结果 != "（还没有收尾过）")
                ImGui.TextDisabled($"  上次收尾：{对局收尾.上次结果}");

            ImGui.Spacing();

            // ── 操作 ──
            if (ImGui.Button("打开记忆库文件夹"))
            {
                打开目录(对局记录.记忆路径());
            }

            ImGui.SameLine();

            if (ImGui.Button("重新载入（刷新条目数）"))
            {
                记忆库.概览();
            }

            ImGui.SameLine();

            if (ImGui.Button("清空记忆库"))
            {
                ImGui.OpenPopup("确认清空记忆库");
            }

            if (ImGui.BeginPopup("确认清空记忆库"))
            {
                ImGui.Text("确定要清空记忆库吗？此操作不可撤销。");
                ImGui.TextDisabled("（记录文件不会被删除，只清结论库）");
                ImGui.Separator();

                if (ImGui.Button("确定清空"))
                {
                    记忆库.清空();
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();

                if (ImGui.Button("取消"))
                {
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }

            ImGui.Spacing();
            ImGui.TextDisabled("  归档维度：职业 + 等级 + 副本类型（四人/八人）");
            ImGui.TextDisabled("  初始化时会把相关记忆附在提示词后面，供 AI 参考");
        }
        catch (Exception e)
        {
            ImGui.TextDisabled("记忆库界面出错：" + e.Message);
        }
    }

    /// <summary>用系统资源管理器打开目录（失败不影响游戏）</summary>
    private static void 打开目录(string 目录)
    {
        try
        {
            Directory.CreateDirectory(目录);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = 目录,
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            LogHelper.Error("[记忆库] 打开目录失败：" + e.Message);
        }
    }
}
