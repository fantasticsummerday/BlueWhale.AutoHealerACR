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

    /// <summary>
    /// **本回调挂到的那个 UiBuilder 实例** —— `卸载()` 要靠它去摘订阅。
    ///
    /// [!] 为什么要存下来：`RemoveEventHandler` 需要"事件所在的那个对象"。
    ///     只存 `_已挂载 = true` 的话，卸载时**没有东西可摘**。
    /// </summary>
    private static object? _挂到的UiBuilder;

    /// <summary>
    /// **已卸载** —— 卸载之后绝不再画（哪怕 `绘制者` 还是非 null）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  [!] 为什么必须有它（2026-10-02 退游戏崩溃的成因之一）：
    ///
    ///      用户截图的栈：`…独立设置窗.每帧画() → 画调试窗() → 调试窗.绘制()
    ///                    → 对象有效() → GameObject.get_GameObjectId()` ⇒ AV ⇒ 进程死。
    ///      退游戏时游戏对象正在销毁，而这个回调**还在跑** —— 因为：
    ///        · `UiBuilder.Draw` 的订阅**从来没被摘过**
    ///        · `绘制者` 是 static，ACR 卸载**不会清空它**
    ///      ⇒ 那个属于"已卸载程序集"的入口实例还会被每帧调用一次。
    ///
    ///  [!] ⚠️ **复位必须放在 `_已挂载` 判断【之前】**（见 `尝试挂载()`）：
    ///      否则重载 ACR 之后没人把 `_已卸载` 复位 ⇒ 窗口**再也画不出来**（单向门）。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static bool _已卸载;

    /// <summary>是否已经成功挂上 Dalamud 的绘制回调。</summary>
    public static bool 已挂载 => _已挂载;

    /// <summary>是否已经卸载（诊断用）。</summary>
    public static bool 已卸载 => _已卸载;

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

        // ★★★ 复位放在 `_已挂载` 判断【之前】★
        //  [!] 重载 ACR 时框架会重新 Build ⇒ 重新调到这里 —— 这一步就是"重新可用"。
        //      放在后面的话：`_已卸载` 永远是 true ⇒ `每帧画()` 第一句就 return
        //      ⇒ **设置窗再也打不开，而且没有任何日志**（单向门）。
        _已卸载 = false;

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

            事件.AddEventHandler(uiBuilder, new Action(每帧画));
            _已挂载 = true;
            _挂到的UiBuilder = uiBuilder;   // ★ 存下来 —— `卸载()` 要用它摘订阅
            LogHelper.Info("[HealerACR] 独立设置窗：已挂上 Dalamud 的绘制回调" +
                           $"（接口={接口.GetType().FullName}）");
        }
        catch (Exception e)
        {
            报一次("挂载异常：" + e.Message);
        }
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
    ///
    /// [!] ⚠️ 但**最前面必须是"该不该画"的安全门**（2026-10-02 退游戏崩溃）。
    /// </summary>
    private static void 每帧画()
    {
        using var _深度 = HealerACR.Common.调用深度.进("独立设置窗.每帧画");

        // ══════════════════════════════════════════════════════════════════
        //  ★★★ **退游戏崩溃的第一道门：卸载之后一个窗口都不画** ★★★
        //
        //  [!] 用户截图的那次崩溃（`AccessViolationException`，进程直接死）：
        //      栈 = 本方法 → 画调试窗() → 调试窗.绘制() → 对象有效()
        //           → `GameObject.get_GameObjectId()` ⇐ 崩在这一帧。
        //      退游戏时游戏对象正在销毁，而本回调**还在跑**，因为：
        //        · `UiBuilder.Draw` 的订阅**从来没被摘过**
        //        · `绘制者` 是 static，ACR 卸载**不会清空它**
        //      ⇒ 一个属于"已卸载程序集"的入口实例还在被每帧调用。
        //
        //  [!] 两道门分工：
        //      ① `_已卸载`      —— 框架明确说了"这个 ACR 已经卸了"（`Dispose` 里置）
        //      ② 读不到当前角色 —— 退游戏 / 登出 / 角色选择界面，
        //                          此时**任何游戏对象都不可信**
        //        ⚠️ 这里是"读不到 ⇒ 拒绝"的方向：拒绝只少画一帧窗口，
        //           而接受一次就是**进程被杀**（工程约定 ★12 的例外情形）。
        //
        //  [!] ⚠️ 这只是第二层。第一层在 `CharacterExt.对象有效()`：
        //      它现在**先做纯托管的地址判据、再碰原生属性** ——
        //      因为 AV 是 corrupted state exception，`try/catch` 根本兜不住。
        // ══════════════════════════════════════════════════════════════════
        if (_已卸载) return;

        try
        {
            if (AEAssist.Core.Me == null) return;   // 退游戏 / 登出 ⇒ 什么都不画
        }
        catch { return; }                            // 读不到 ⇒ 保守不画

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

    /// <summary>
    /// **卸载** —— 摘掉 Dalamud 绘制订阅、丢弃入口实例、复位挂载标记。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  [!] 为什么必须做（用户截图的那次退游戏崩溃）：
    ///      原来 L131 挂上 `UiBuilder.Draw` 之后，**全工程没有一处摘它**：
    ///          · 重载 ACR ⇒ 旧委托仍挂在 Dalamud 的绘制事件上
    ///          · 退出游戏 ⇒ 它还在跑，而游戏对象已经在销毁
    ///          · 而 `绘制者` 指向的入口已失效
    ///        ==> 读已销毁对象的原生内存 ==> `AccessViolationException`
    ///            ==> **进程直接死**（栈：…独立设置窗.每帧画 → 画调试窗 →
    ///                调试窗.绘制 → 对象有效 → GameObject.get_GameObjectId）。
    ///
    ///  [!] 卸载顺序（**先断来源，再置标记**）：
    ///      ① 置 `_已卸载 = true` ⇒ 即使还有一帧漏进来，`每帧画` 也直接返回
    ///      ② 摘订阅（失败也无所谓，因为①已经生效）
    ///      ③ 丢 `绘制者` / 关窗口 / 复位 `_已挂载`（下次 `Build` 会重新挂上）
    ///
    ///  [!] ⚠️ **不要在这里调 `卸载钩子`**（会成环）——
    ///      `HealerEntryBase.Dispose` 是**先摘钩子再通知**的（那段注释记着
    ///      3.19.4 的栈溢出根因）。本方法只会被那个通知链之后调到。
    ///
    ///  [!] 幂等：可以重复调（ACR 重载时框架可能对同一入口多次 Dispose）。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static void 卸载()
    {
        _已卸载 = true;          // ★ 第一件事：先把"还会画"这条路的闸门关掉

        try
        {
            var uiBuilder = _挂到的UiBuilder;
            if (uiBuilder != null)
            {
                var 事件 = uiBuilder.GetType().GetEvent("Draw");
                事件?.RemoveEventHandler(uiBuilder, new Action(每帧画));
            }
        }
        catch { }

        _挂到的UiBuilder = null;
        _已挂载 = false;

        // 丢入口 + 收窗口（静态状态必须清干净）
        //  [!] 为什么必须丢 `绘制者`：它指向的实例可能属于**已经被卸载的程序集**
        //      ⇒ 留着它 = 下一帧继续读已销毁对象的原生内存 ⇒ 又是那个 AV。
        绘制者 = null;
        绘制中 = false;
        显示 = false;
    }

    /// <summary>切换显示（QT 面板的「设置」页签调它）。</summary>
    public static void 切换()
    {
        尝试挂载();
        显示 = !显示;
    }
}
