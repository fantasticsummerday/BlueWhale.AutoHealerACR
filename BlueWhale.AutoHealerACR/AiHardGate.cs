using System;



namespace BlueWhale.AutoHealerACR;



/// <summary>

/// **AI 硬闸** —— AI 的建议必须先过这一道，才谈得上"本地评分"（2026-10-03，审计 P0-5 / P2-14）。

///

/// ══════════════════════════════════════════════════════════════════════

///  [!] 审计原话（为什么需要它）：

///      「用户关闭『奶人 / 减伤 / 自动减伤』后，AI 路径仍然可能执行」——

///      因为 AI 建议的 resolver 优先级是 `return 100`，明显高于普通 resolver，

///      本地那些尊重开关的 resolver 不工作时，AI 那条路仍会把动作塞进 Slot ✗

///

///  [!] 铁律（写进这里，避免以后有人再绕过）：

///      **`完全采信 AI` 永远不能覆盖用户主动关闭的功能开关。**

///      开关是"我不想要这个功能"，AI 只是"这一拍我倾向哪个" ✓

///

///  [!] ⚠️ **fail-closed**：任何异常 ⇒ 拒绝（审计 P2-14：安全边界不允许"宁可放过"）✗→✓

///

///  [!] 目前覆盖面（AI 现在只能建议治疗 / 减伤两类，所以这几道就够）：

///        减伤类  ⇒ 开关「减伤」+「自动减伤」

///        治疗类  ⇒ 开关「奶人」，再按"盾 / 非盾"与"单 / 群"细分：

///                  群盾 / 单盾 / 群奶 / 单奶

///      查不到的开关按**默认开启**处理（`GetQt(name, true)`）——

///      这样"开关名改了"不会把功能整体锁死，而用户真正关掉的开关仍然生效 ✓

/// ══════════════════════════════════════════════════════════════════════

public static class AiHardGate

{

    /// <summary>这条候选现在允许被 AI 推动吗（用户开关层面）。</summary>

    public static bool 允许(候选集.候选 c, out string 原因)

    {

        原因 = "";

        try

        {

            // ★ 每技能 QT（学者 8 技能开关）：关掉的技能一律拒 ——
            //   无论 AI 是从候选集选还是走「技能ID抛接」的兜底路，这里兜住
            //   （和本地 `治疗决策.选最优` / 通用 resolver 同一判据，H9 联动）。
            if (!HealerACR.Common.HealQt.每技能通过(c.技能Id))

            {

                原因 = "用户关闭了该技能的每技能开关";

                return false;

            }


            // ★ 2026-10-15 修：**盾的细分开关必须按「是不是盾」判，不能按「类别」判** ★
            //
            //  [!] 原来 `群盾 / 单盾` **只在 `类别.治疗` 分支里查**，而**盾候选本身也有
            //      `类别.减伤` 的**（预铺盾：罩子 / 泛输血 / 均衡预后 —— `候选集` 里就标
            //      `类 = 类别.减伤`）⇒ 用户关掉「群盾」时：
            //        · 本地**三处 resolver 全部拒绝**
            //          （`Res_AllExtra.cs:781` / `:1283`、`Res_Mitigation.cs:1193`）
            //        · 而 **AI 这条路照样推**（硬闸根本没查这个开关）✗
            //      —— 这正是本类存在的意义（"完全采信 AI 永远不能覆盖用户主动关闭的功能开关"）。
            //
            //  [!] 开发约定 F③：同一个决策（"这个盾该不该放"）不论从哪条路进来判据必须一致。
            //      放到类别分支**之前**，治疗类与减伤类的盾就都过同一道闸了
            //      （治疗分支里那份同名检查因此变成冗余，保留不影响结果）。
            if (c.是盾)
            {
                var 盾开关 = c.群体 ? "群盾" : "单盾";
                if (!HealerACR.Common.HealQt.GetQt(盾开关, true))
                {
                    原因 = $"用户关闭了「{盾开关}」";
                    return false;
                }
            }

            if (c.类 == 候选集.类别.减伤)

            {

                if (!HealerACR.Common.HealQt.GetQt("减伤", true))

                {

                    原因 = "用户关闭了「减伤」";

                    return false;

                }

                // ★ 「自动减伤」读**设置项本体**，不读 QT（表外审计修正）
                //
                // [!] 原来写的是 GetQt("自动减伤", true) —— 而 Qt 里
                //     **从来没注册过这个名字** ⇒ 按"未注册返回兜底值"的语义，
                //     那次读取**恒为 true** ⇒ 用户把「自动减伤」勾掉时，
                //     本地不放、**AI 却照建议** —— 两边判据不一致 ✗
                //
                // [!] 本地真正的守卫（Res_TeamMitigation / Res_SelfMitigation）
                //     读的就是 HealSettings.自动减伤 ⇒ AI 侧必须和它**同源**
                //     （开发约定 F③：同一个决策不论从哪条路进来判据必须一致）。
                //     ⚠️ 不读 QT 的另一个原因：设置项改了 QT 不一定跟着刷新，
                //       那会变成"两个值谁赢看时机"的隐蔽故障。
                if (!HealerACR.Common.HealSettings.Instance.自动减伤)

                {

                    原因 = "用户关闭了「自动减伤」";

                    return false;

                }

                return true;

            }



            if (c.类 == 候选集.类别.治疗)

            {

                if (!HealerACR.Common.HealQt.GetQt("奶人", true))

                {

                    原因 = "用户关闭了「奶人」";

                    return false;

                }



                if (c.是盾)

                {

                    var 开关 = c.群体 ? "群盾" : "单盾";

                    if (!HealerACR.Common.HealQt.GetQt(开关, true))

                    {

                        原因 = $"用户关闭了「{开关}」";

                        return false;

                    }

                }

                else

                {

                    var 开关 = c.群体 ? "群奶" : "单奶";

                    if (!HealerACR.Common.HealQt.GetQt(开关, true))

                    {

                        原因 = $"用户关闭了「{开关}」";

                        return false;

                    }

                }

                return true;

            }



            // 其他类别（紧急 / 功能 / 输出）本就不该由 AI 推动（B 层边界），这里一并挡住

            原因 = "该类别不由 AI 决定（输出 / 驱散 / 濒死救人由本地逻辑算）";

            return false;

        }

        catch

        {

            // ★ fail-closed：判不出来就拒绝（审计 P2-14）

            原因 = "硬闸校验异常（安全起见拒绝）";

            return false;

        }

    }



    /// <summary>给日志/窗口用的简短描述。</summary>

    public static string 描述()

    {

        try

        {

            return $"奶人={HealerACR.Common.HealQt.GetQt("奶人", true)}" +

                   $"｜减伤={HealerACR.Common.HealQt.GetQt("减伤", true)}" +

                   $"｜自动减伤={HealerACR.Common.HealQt.GetQt("自动减伤", true)}";

        }

        catch { return "（读不到）"; }

    }

}

