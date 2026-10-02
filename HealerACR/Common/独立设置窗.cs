using System;
using System.Reflection;
using AEAssist.Helper;
using Dalamud.Bindings.ImGui;

namespace HealerACR.Common;

/// <summary>
/// **独立设置窗** —— 把设置界面搬到一个**自己的悬浮窗**里，
/// 由 Dalamud 的绘制回调驱动，**完全不经过 AEAssist 的 `AcrUiSandbox`**。
///
/// ══════════════════════════════════════════════════════════════════════════
///  [!] 为什么必须搬走（用户实测 + AEAssist 反编译实证）
///
///      症状：AEAssist 的「ACR设置」页签里，我们画的文字**逐帧出现/消失**；
///            而 QT 面板（悬浮窗）完全正常。
///
///      根因：AEAssist 把 `OnDrawSetting` 包在 `AcrUiSandbox` 里跑：
///
///          // AEAssist/CombatRoutine/CombatRoutine2.cs L1999
///          AcrUiSandbox.Draw(currRotation2.RotationEntry.AuthorName,
///                            currRotation2.RotationEntry.OnDrawSetting);
///
///      而这个沙箱**每次调用**都会：
///        ① 快照 `g.Style` 的 55 个颜色 + 30 个标量（反编译 `xgMBDC63Qv`）
///        ② 退出时**整份写回**（`PZPB8dHx4A` → `epjBTm3kGt`）
///
///      实测这个回调每秒被调 **~520 次**
///      （日志：`路标=0` 累计 70461 -> 71475，每秒 +520）
///      ==> 每秒 520 次把全局样式写回去
///      ==> 同一帧里被 ImGui 读到的样式值在抖
///      ==> **文字逐帧出现/消失**。
///
///      ==> **这不是我们能修的** —— 它是 AEAssist 的行为，
///          我们唯一能做的是**不走那条路**。
///
///  [!] 所以本类的做法：**自己订阅 Dalamud 的绘制回调**
///
///      AEAssist 自己就是这么挂主绘制的（实证）：
///          // AEAssist/Plugin.cs L118
///          Svc.PluginInterface.UiBuilder.Draw += W8qybFkoGG;
///
///      我们挂同一个事件 ==> 一条**独立**的绘制路径
///      ==> **不经过 `AcrUiSandbox`** ==> 不会被它的样式快照/写回干扰。
///
///  [!] 设置内容怎么复用（**关键设计**）
///
///      设置内容（13 段 + AI 设置页 + 记忆库）**原封不动留在
///      `HealerEntryBase.OnDrawSetting()` 里**，一个字符都没搬。
///
///      两条路的区分靠本类的 `绘制中` 标记：
///        · 本类绘制前置 `绘制中 = true` ==> `OnDrawSetting` **正常画**
///        · AEAssist 的沙箱调时它是 false ==> `OnDrawSetting` **直接挡回**
///
///      ==> 既复用同一份代码，又**零结构风险**（不搬代码就不会改坏花括号）。
///
///  [!] 开关方式：QT 面板上的「设置」页签（`视图窗口.AddTab`）
///      那个页签**不画窗口**，只当按钮 —— 窗口由 Dalamud 回调常驻绘制。
///      画在页签里的话，一切页签窗口就消失（本项目踩过两次的坑）。
/// ══════════════════════════════════════════════════════════════════════════
/// </summary>
public static class 独立设置窗
{
    /// <summary>窗口是否显示（由 QT 面板的「设置」页签切换）。</summary>
    public static bool 显示;

    /// <summary>
    /// **本类正在绘制** —— `OnDrawSetting()` 靠它区分"是独立窗口在调"还是"沙箱在调"。
    ///
    /// [!] 只有本类会把它置 true，且**用 try/finally 保证复位**
    ///     （中途抛异常也必须复位，否则沙箱那条路永远画不了）。
    /// </summary>
    public static bool 绘制中;

    /// <summary>
    /// **画设置用的那个入口实例** —— 由第一个 `Build()` 的入口登记。
    ///
    /// [!] 为什么需要：`OnDrawSetting()` 是**实例方法**，而本类的绘制回调是静态的。
    ///
    /// [!] 为什么谁先登记就用谁：设置内容对 5 个职业入口是**同一份代码**
    ///     （都在 `HealerEntryBase` 里，读共享的 `HealSettings`），用哪个都一样。
    /// </summary>
    public static HealerEntryBase? 绘制者;

    private static bool _已挂载;
    private static bool _失败已报;

    /// <summary>是否已经成功挂上 Dalamud 的绘制回调。</summary>
    public static bool 已挂载 => _已挂载;

    /// <summary>
    /// **挂上 Dalamud 的绘制回调**（幂等 —— 5 个职业入口都会调，只挂一次）。
    ///
    /// [!] 为什么用反射找 `UiBuilder`：
    ///      本工程只 `PackageReference` 了 `AEAssist.NET`；`Dalamud.dll`
    ///      虽然随包提供，但 ACR 运行在宿主的加载上下文里、版本由宿主决定。
    ///      反射取"属性 + 事件"最稳：**找不到就什么都不做**（降级，不影响其他功能）。
    ///
    /// [!] 候选路径（逐个尝试）：
    ///      ① `ECommons.DalamudServices.Svc.PluginInterface`（AEAssist 自己用的）
    ///      ② `DalamudApi.PluginInterface`
    /// </summary>
    public static void 尝试挂载(HealerEntryBase? 入口 = null)
    {
        if (入口 != null) 绘制者 ??= 入口;
        if (_已挂载) return;

        try
        {
            var 接口 = 找插件接口();
            if (接口 == null)
            {
                报一次("找不到 Dalamud 插件接口");
                return;
            }

            var uiBuilder = 接口.GetType().GetProperty("UiBuilder")?.GetValue(接口);
            if (uiBuilder == null)
            {
                报一次("插件接口没有 UiBuilder 属性");
                return;
            }

            var 事件 = uiBuilder.GetType().GetEvent("Draw");
            if (事件 == null)
            {
                报一次("UiBuilder 没有 Draw 事件");
                return;
            }

            // ══════════════════════════════════════════════════════════════════
            //  ★ 每次加载都把调试窗开关**强制复位为"关"**（用户明确要求）
            //
            //  [!] 用户原话（2026-10-03）：
            //      「我要达成的目的是：把逻辑改成**每次启动加载的时候**
            //        调试窗开关改成**默认关**」
            //
            //  [!] 为什么放在这里（`尝试挂载` 只在**这次加载的第一次**跑到这里，
            //      因为上面 `if (_已挂载) return;` 会挡住后续调用）：
            //      这个位置 = "本次加载刚开始、还没画过任何东西"
            //      ⇒ 无论之前发生过什么，调试窗都从"关"开始。
            //
            //  [!] 它和 `调试窗全局开关.启用` 的"默认 false"是**两道**保险：
            //      默认值保证"没人动过就是关"，这一句保证"就算有人动过也被抹掉"。
            // ══════════════════════════════════════════════════════════════════
            调试窗全局开关.启用 = false;

            // ══════════════════════════════════════════════════════════════════
            //  ★★★ **摘掉"上一份副本"遗留的绘制订阅** ★★★
            //
            //  [!] 为什么必须做（这才是"一加载 AE 就冒出调试窗"的真正原因）：
            //      ACR 重载**不会卸载旧程序集**（本项目反复实测过：一个游戏进程里
            //      能堆 20~30 份副本）。旧副本里的 `独立设置窗` 把**它自己的**
            //      `每帧画` 挂在 Dalamud 的 `UiBuilder.Draw` 上，而且**从来不摘**
            //      （0.6.0.x 全工程没有一处 RemoveEventHandler）。
            //      那些旧副本里，有的加载时读到的开关是 `true`
            //      （`调试窗开关.json` 在 22:26 之前一直是 true）
            //      ⇒ **它们会一直画那个窗口**，而我改的任何新代码都管不到旧副本
            //      ⇒ 用户看到的就是"改了好几版还是一加载就冒出来"。
            //
            //  [!] 判据（只摘"自己人但是别的副本"）：
            //        · `Method.Name == "每帧画"`
            //        · `DeclaringType.FullName == "HealerACR.Common.独立设置窗"`
            //        · `DeclaringType.Assembly != 本程序集`   ← 关键：绝不摘自己
            //      ⇒ 摘不到、或 Dalamud 以后换成自定义事件（没有背后的字段）
            //        都只是"什么都不做"，不影响本次挂载。
            //
            //  [!] ⚠️ 顺序：**先清旧的，再挂自己的** —— 反了的话可能把自己摘掉。
            // ══════════════════════════════════════════════════════════════════
            摘掉旧副本的订阅(事件, uiBuilder);

            事件.AddEventHandler(uiBuilder, new Action(每帧画));
            _已挂载 = true;
            LogHelper.Info("[HealerACR] 独立设置窗：已挂上 Dalamud 的绘制回调" +
                           $"（接口={接口.GetType().FullName}）");
        }
        catch (Exception e)
        {
            报一次("挂载异常：" + e.Message);
        }
    }

    /// <summary>
    /// **摘掉"上一份/上几份程序集副本"留在 Dalamud 绘制事件上的回调**。
    ///
    /// [!] 见调用点那段说明：旧副本的 `每帧画` 从来不摘，而它们里的开关可能是
    ///     `true`（当年 json 里存过 true）⇒ 它们会永远画那个调试窗。
    ///     不重启游戏的话，**只有把它们从这个事件上摘下来**才能让窗口真的消失。
    ///
    /// [!] 只摘"类型全名相同、但属于**别的程序集**"的委托：
    ///     · 名字对不上 ⇒ 不动（可能是 Dalamud 自己的或别的插件的）
    ///     · 程序集相同 ⇒ 不动（那是**本次**加载挂的，摘了就没人画了）
    ///
    /// [!] 全程 try/catch：这是"顺手清理"，失败绝不能影响本次挂载。
    /// </summary>
    private static void 摘掉旧副本的订阅(EventInfo 事件, object uiBuilder)
    {
        try
        {
            var 本程序集 = typeof(独立设置窗).Assembly;

            // 字段式事件的背后字段就叫事件名（Dalamud 的 UiBuilder 是 `public event Action Draw`）
            var 字段 = uiBuilder.GetType().GetField(事件.Name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (字段?.GetValue(uiBuilder) is not Delegate 当前) return;

            var 摘掉 = 0;
            foreach (var d in 当前.GetInvocationList())
            {
                try
                {
                    var 声明类型 = d.Method.DeclaringType;
                    if (声明类型 == null) continue;
                    if (d.Method.Name != "每帧画") continue;
                    if (声明类型.FullName != "HealerACR.Common.独立设置窗") continue;
                    if (ReferenceEquals(声明类型.Assembly, 本程序集)) continue;   // ★ 别摘自己

                    事件.RemoveEventHandler(uiBuilder, d);
                    摘掉++;
                }
                catch { }
            }

            if (摘掉 > 0)
            {
                LogHelper.Info($"[HealerACR] 独立设置窗：已摘掉 {摘掉} 个【旧副本】遗留的绘制回调" +
                               "（它们会一直画调试窗 —— 这是「重载而不重启」的后果）");
            }
        }
        catch { }
    }

    private static void 报一次(string 原因)
    {
        if (_失败已报) return;
        _失败已报 = true;
        LogHelper.Info($"[HealerACR] 独立设置窗不可用（{原因}）—— " +
                       "设置仍可在「ACR设置」页签看到（但那一页会闪）");
    }

    /// <summary>逐个尝试已知路径，返回第一个拿到的插件接口对象。</summary>
    private static object? 找插件接口()
    {
        foreach (var 类型名 in new[] { "ECommons.DalamudServices.Svc", "DalamudApi" })
        {
            var 值 = 取静态属性(类型名, "PluginInterface");
            if (值 != null) return 值;
        }
        return null;
    }

    /// <summary>按类型全名取静态属性值（在已加载程序集里找，找不到返回 null）。</summary>
    private static object? 取静态属性(string 类型全名, string 属性名)
    {
        try
        {
            foreach (var 程序集 in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? 类型 = null;
                try { 类型 = 程序集.GetType(类型全名, false); } catch { }
                if (类型 == null) continue;

                var 值 = 类型.GetProperty(属性名,
                                          BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (值 != null) return 值;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// **每帧的绘制**（由 Dalamud 的 `UiBuilder.Draw` 驱动）。
    ///
    /// [!] 这里**不开任何节流门** —— 悬浮窗必须每帧提交，
    ///    少提交一帧它在那一帧就"不存在"（这正是原来调试窗闪的原因之一）。
    /// </summary>
    private static void 每帧画()
    {
        using var _深度 = HealerACR.Common.调用深度.进("独立设置窗.每帧画");
        // ══════════════════════════════════════════════════════════════════
        //  ★★ **调试窗也在这里画** —— 它和设置窗是**同一条受信的路** ★★
        //
        //  [!] 为什么（用户实测：「这下有了，但是调试窗打不开了」）：
        //      调试窗原来的画点在 `HealerEntryBase.OnDrawSetting()` 里
        //      （`面板路标(5)` 与 `面板路标(6)` 之间）。
        //      而我们已经把那条路（沙箱路）**挡回了** ⇒ 那个画点不再执行
        //      ⇒ **调试窗打不开**。
        //
        //  [!] 而且它**本来就不该挂在设置页上**：
        //      · 它是**常驻悬浮窗**（用户勾了开关就该一直在屏幕上）
        //      · 挂在设置页上 ⇒ 不开设置页它就不刷新（历史 bug：
        //        「不开设置页，悬浮窗就不更新」）
        //
        //  ==> 它和设置窗一样是**自己的 ImGui 窗口**，同属"受信的路"，
        //      所以放在这里最自然：**独立于设置窗的开关**，
        //      只由它自己的全局开关（`调试窗全局开关.启用`）控制。
        //
        //  [!] ⚠️ **它必须在 `显示` 判断【之前】** ——
        //      否则"设置窗没开"时调试窗也不会画（那就又回到打不开了）。
        //
        //  [!] ⚠️ **每帧都要提交**，不能加节流门：
        //      少提交一帧，窗口在那一帧就"不存在"（这正是它以前闪的成因）。
        //      `画AI层调试窗()` 内部有它自己的失败计数/冷却，防的是"反复抛异常"。
        // ══════════════════════════════════════════════════════════════════
        try
        {
            // ★ 开关判断**不在这里** —— 交给 `绘制者.画调试窗()` ★
            //
            //  [!] 为什么（用户实测「开关关不掉 / 窗口提前出现」）：
            //      `调试窗全局开关` 在这个进程里有**两份**（两个程序集各一份
            //      静态字段）。如果这里读，读到的可能是另一份的值
            //      ⇒ 设置窗里取消勾选，窗口照样出现。
            //      ==> 只让**入口那一侧**读（和 Checkbox 是同一份），
            //          这里只管"有没有入口实例可以问"。
            //
            //  [!] 同时这也修了「ACR 没加载窗口就出现」：
            //      `绘制者` 是 `Build()` 里登记的，没构建就没有它
            //      ⇒ 不会再出现"没加载 ACR 却弹出窗口"。
            if (绘制者 != null)
                绘制者.画调试窗();
        }
        catch { }

        if (!显示) return;

        try
        {
            ImGui.SetNextWindowSize(new System.Numerics.Vector2(560f, 640f),
                                    ImGuiCond.FirstUseEver);

            var 开着 = true;
            if (!ImGui.Begin("小鲸鱼 · 设置", ref 开着, ImGuiWindowFlags.NoCollapse))
            {
                // `Begin` 返回 false 也必须 `End`（ImGui 契约，见工程里的说明）
                ImGui.End();
                return;
            }

            try
            {
                if (绘制者 == null)
                {
                    ImGui.TextDisabled("  设置尚未就绪（入口还没构建完）。");
                }
                else
                {
                    // 置标记 ==> `OnDrawSetting()` 会走"正常画"那一条
                    绘制中 = true;
                    try { 绘制者.OnDrawSetting(); }
                    finally { 绘制中 = false; }
                }

                ImGui.Separator();
                if (ImGui.Button("关闭"))
                    显示 = false;
            }
            finally
            {
                ImGui.End();
            }

            if (!开着) 显示 = false;
        }
        catch (Exception e)
        {
            LogHelper.Info("[HealerACR] 独立设置窗绘制异常（已忽略）：" + e.Message);
        }
    }

    /// <summary>切换显示（QT 面板的「设置」页签调它）。</summary>
    public static void 切换()
    {
        尝试挂载();
        显示 = !显示;
    }
}
