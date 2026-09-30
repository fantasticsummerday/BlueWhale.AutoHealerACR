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
    /// <summary>
    /// 目录输入框的缓冲。
    ///
    /// ⚠️ ImGui 的 InputText 需要一个**稳定**的 string 引用 ——
    ///    每帧新建字符串会让**光标每帧跳到末尾**（根本没法编辑）。
    ///    所以缓存起来，只在"外部值"变化时刷新。
    ///
    /// ⚠️ 但**用户打字时不能刷新** —— 那会把刚敲的字冲掉。
    ///    所以打字时要同步更新 `_缓冲来源`（见调用处）。
    /// </summary>
    private static string _缓冲 = "";
    private static string _缓冲来源 = "";

    private static void 输入缓冲(string 当前值)
    {
        if (当前值 == _缓冲来源) return;   // 没变 → 保留用户正在编辑的内容

        _缓冲 = 当前值;
        _缓冲来源 = 当前值;
    }

    public static void 画()
    {
        // [!] 这一行在**任何 try 之外、方法最前面** ——
        //     它打印了就说明确实进到了本方法。
        try { LogHelper.Info("[库诊断] 0-方法第一行"); } catch { }

        // ══════════════════════════════════════════════════════════════
        //  ★ 阶段标记 —— 定位"点设置后无响应 14 秒再崩" ★
        //
        //  [!] 实测：`AiSettingPage.画()` 走完了（T13），
        //      但外层的「3-记忆库页面.画」**一次都没打印**，
        //      而崩溃发生在最后一条日志之后 **14 秒**（17:10:01 → 17:10:15）。
        //      => 卡在这两者之间，或卡在本方法内部。
        //      本方法每帧只调 `输入缓冲()`，所以逐个标出来看卡在哪一步。
        // ══════════════════════════════════════════════════════════════
        static void 段(string 名)
        {
            try { LogHelper.Info("[库诊断] " + 名); } catch { }
        }

        段("a-进入画()");

        try
        {
            ImGui.Separator();
            ImGui.Text("战斗记忆库");
            ImGui.TextDisabled("  记录你手动操作时的打法，提炼成规律，用来优化 AI");

            ImGui.Spacing();

            // ══════════════════════════════════════════════════════════
            //  ★ 记录模式状态 —— 必须最显眼 ★
            // ══════════════════════════════════════════════════════════
            段("b-记录模式判断 前");
            if (记录模式.开启)
            {
                段("b1-记录模式开着");
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
            段("b2-记录模式分支 完成");

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

            // ══════════════════════════════════════════════════════════
            //  ★ 保存位置（能固定一个位置）★
            // ══════════════════════════════════════════════════════════
            ImGui.Text("保存位置");
            ImGui.TextDisabled("  记录 / 记忆库 都存这里。留空 = 用默认位置（我的文档\\BlueWhale记忆库）");

            段("c-保存位置段 前");
            var 设置 = AiSettings.Instance;
            段("c1-取到 Instance");
            var 当前 = 设置.记忆目录 ?? "";
            段("c2-取到 记忆目录");
            输入缓冲(当前);   // 外部值变了才刷新 _缓冲（见方法说明）
            段("c3-输入缓冲 完成");

            ImGui.SetNextItemWidth(-160);
            if (ImGui.InputText("##记忆目录", ref _缓冲, 512))
            {
                // ⚠️ 必须同步 _缓冲来源 —— 否则下一帧会被判定为"外部值变了"
                //    然后拿旧值把用户刚敲的字**冲掉**（表现为完全没法输入）。
                _缓冲来源 = _缓冲;

                // ⚠️ 改了之后**不要立刻做文件 IO** ——
                //    AiSettings.保存() 会把实际写盘推迟到脱战（见它的每帧更新）。
                设置.记忆目录 = _缓冲.Trim();
                AiSettings.保存();
            }

            ImGui.SameLine();
            if (ImGui.Button("打开文件夹")) 打开目录(对局记录.记忆路径());
            ImGui.SameLine();
            if (ImGui.Button("恢复默认"))
            {
                设置.记忆目录 = "";
                AiSettings.保存();
                // 清空缓冲来源，让下一帧从设置里重新取值
                _缓冲来源 = "\u0000";
                输入缓冲("");
            }

            // 显示**实际生效**的路径（留空时显示默认值，避免误以为没生效）
            ImGui.TextDisabled("  实际保存到：" + 对局记录.记忆路径());

            // ⚠️ 这里必须显示"实际路径"而不是输入框内容 ——
            //    留空时输入框是空的，但实际用的是默认目录，
            //    只显示输入框会造成"没设置 = 不保存"的误解。

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
