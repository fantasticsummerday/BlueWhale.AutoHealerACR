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

    /// <summary>**已卸载** —— 卸载之后绝不再画（哪怕 `绘制者` 还是非 null）。</summary>
    private static bool _已卸载;

    /// <summary>"拒绝残留入口"日志的节流时刻（30 秒最多一条）。</summary>
    private static long _拒绝日志时刻;

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
        // ⚠️ **不要**写成 `绘制者 ??= 入口`。
        //    那个写法会让"**第一个** Build 的入口"当绘制者（白魔），
        //    和"最后一个"（幻术师）一样是错的职业。
        //    ==> 交给 `换入口()`：它**只认职业匹配的那个**（见那边的长注释）。
        if (入口 != null) 换入口(入口);

        // ══════════════════════════════════════════════════════════════════
        //  ★★★ **必须先把"已卸载"复位** ★★★
        //
        //  [!] 修的是用户实测 bug（2026-10-04）：
        //      「切换到别的 ACR 之后，设置里再也打不开（设置窗）」
        //
        //      成因链（我引入的）：
        //        ① 切到别的 ACR ⇒ 框架调我们的 `Dispose`
        //           ⇒ `独立设置窗.卸载()` ⇒ `_已卸载 = true`
        //        ② 切回小鲸鱼 ⇒ 框架重新 `Build()`
        //           ⇒ 本方法被调
        //        ③ 但 `_已挂载` 还是 true ⇒ 这里 `return`（幂等提前退出）
        //           ⇒ **没人把 `_已卸载` 复位**
        //        ④ `每帧画()` 第一句 `if (_已卸载) return;`
        //           ⇒ **永远不再画任何窗口** ⇒ 设置窗再也打不开
        //
        //      ==> 复位放在 `_已挂载` 判断**之前**（这一步本身就是"重新可用"）。
        // ══════════════════════════════════════════════════════════════════
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
            _已卸载 = false;
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
    /// </summary>
    private static void 每帧画()
    {
        using var _深度 = HealerACR.Common.调用深度.进("独立设置窗.每帧画");

        // ══════════════════════════════════════════════════════════════════
        //  ★★★ **第一道门：已卸载 或 游戏状态不可读 ⇒ 一个窗口都不画** ★★★
        //
        //  [!] 修的是用户实测的两个 bug（2026-10-02）：
        //      ① 「上次没关调试窗 ⇒ 这次 AE 一加载窗口就出来了，ACR 都还没加载」
        //      ② 「在这个情况下开着调试窗退出游戏 ⇒ 报错」
        //
        //  [!] 为什么原来会这样（注释里那个"保证"是**错的**）：
        //      原来这里写着「`绘制者` 是 `Build()` 里登记的，没构建就没有它
        //      ⇒ 不会再出现"没加载 ACR 却弹出窗口"」——
        //      但 `绘制者` 是 **static**，ACR 卸载**不会清空 static**：
        //          · 上次加载时登记的那个入口实例**还在** ⇒ 窗口照画
        //          · 那个实例可能属于**已被卸载的程序集**
        //          · 而 `UiBuilder.Draw` 的订阅**从来没被摘掉**
        //      ==> "ACR 没加载窗口就出现" + "退出时报错"。
        //
        //  [!] 退出时为什么必然崩（用户截图的栈）：
        //          Dalamud…PresentDetour → UiBuilder.Draw → 每帧画()
        //            → 绘制者.画调试窗() → 调试窗.绘制()
        //              → CharacterExt.对象有效() → GameObject.get_GameObjectId()
        //                ⇒ **AccessViolationException**（读取已销毁对象的原生内存）
        //      退出时游戏对象正在销毁 ⇒ 任何对它们的读取都是 AV ⇒ 进程直接死。
        //      （`CharacterExt.对象有效` 的顺序问题已单独修，但"退出门"必须也有
        //        —— 别的地方读对象时同样会踩到。）
        //
        //  [!] 判据复用仓库里**已有**的 API（`进本识别.cs` / `调试窗.绘制` 同款），
        //      不要自造：`MemApiCondition.IsBetweenAreas()` + `实时副本Id() == 0`。
        //      ⚠️ 这两条**读不到时保守当作"不可读"** —— 崩一次比少显示几行严重得多。
        // ══════════════════════════════════════════════════════════════════
        if (_已卸载) return;

        var 可以画 = true;
        try
        {
            if (Core.Resolve<AEAssist.MemoryApi.MemApiCondition>().IsBetweenAreas()) 可以画 = false;
            if (可以画 && HealerACR.Timeline.TimelineManager.实时副本Id() == 0) 可以画 = false;
        }
        catch
        {
            可以画 = false;   // 读不到条件 ⇒ 保守不画
        }

        if (!可以画) return;

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
            //          这里只管"有没有**活着的**入口实例可以问"。
            //
            //  [!] ⚠️ 「活着的」是关键 —— 见下面 `入口还活着()` 的说明：
            //      只看非 null 是不够的，那是"ACR 没加载窗口就出现"的成因。
            var 入口 = 入口还活着();
            if (入口 != null)
                入口.画调试窗();
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
                var 入口 = 入口还活着();
                if (入口 == null)
                {
                    ImGui.TextDisabled("  设置尚未就绪（入口还没构建完，或已卸载）。");
                }
                else
                {
                    // 置标记 ==> `OnDrawSetting()` 会走"正常画"那一条
                    绘制中 = true;
                    try { 入口.OnDrawSetting(); }
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
    /// **换入口** —— 某个入口 `Build()` 时告诉本类"我构建好了"。
    ///
    /// ══════════════════════════════════════════════════════════════════════
    ///  [!] ⚠️ **只有职业匹配的那个入口会成为绘制者** —— 这是本方法的全部要点。
    ///
    ///      背景（用户实测，我在这里错了五次）：框架在**发现阶段**会
    ///      `Build()` **全部 5 个入口**（白魔 / 学者 / 占星 / 贤者 / 幻术师），
    ///      而最后一个构建的是**幻术师**。
    ///
    ///      ==> 任何"谁先登记 / 谁最后登记"的做法都会选中**错的职业**：
    ///          · `绘制者 ??= 入口`（第一个）⇒ 白魔
    ///          · `_最新入口 = this`（最后一个）⇒ 幻术师
    ///          而你实际在玩的是学者 ⇒ 设置页内容、调试窗数据源全错。
    ///
    ///      ⚠️ 这个坑在本仓库**已经犯过一次**，就在 `构建QT()` 上面那段注释里：
    ///          「原来有一行 `HealRotationEventHandler.当前技能表 = Spells` ——
    ///            已删除：那是个单一静态字段，而 AEAssist 加载时会依次 Build
    ///            每个 ACR ⇒ **最后一个 Build 的职业赢**，于是玩学者时 AI
    ///            拿到白魔的技能清单。」（`HealerEntryBase.cs` L257-264）
    ///          当时的修法是"按职业登记进字典"—— 本方法用的是同一个思路。
    ///
    ///  [!] 判据：**入口的 `TargetJob` == 当前职业** ⇒ 它才是绘制者。
    ///      · `TargetJob` 是子类硬编码的编译期常量（`=> Jobs.Scholar`）
    ///      · 当前职业读不到（不在游戏 / 加载中）⇒ **不动 `绘制者`**，
    ///        免得在加载窗口里把它清成 null 又画不出东西
    ///
    ///  [!] 换人时**收起窗口**：换了职业还开着上一个职业的设置窗是误导。
    /// ══════════════════════════════════════════════════════════════════════
    /// </summary>
    public static void 换入口(HealerEntryBase 入口)
    {
        try
        {
            uint 职业;
            try
            {
                var 我 = Core.Me;
                if (我 == null) return;            // 读不到 ⇒ 不动（保持现状）
                职业 = 我.ClassJob.RowId;
            }
            catch { return; }

            if (职业 == 0) return;
            if (职业 != (uint)入口.TargetJob) return;   // ★ 不是本职业的入口 ⇒ 不当绘制者

            if (!ReferenceEquals(绘制者, 入口))
            {
                绘制者 = 入口;      // 就是它
                绘制中 = false;     // 保险：万一上一帧异常退出没复位

                // ⚠️ 这里**不重置 `显示`** —— 那会把用户点「关闭」的效果覆盖掉。
                //    只在"换了人"（换职业 / 重新加载）时收起窗口：
                //    换了职业还开着上一个职业的设置窗是误导。
                显示 = false;
            }
        }
        catch { }
    }

    /// <summary>
    /// **入口还活着吗** —— 只看 `绘制者 != null` 是不够的。
    ///
    /// ══════════════════════════════════════════════════════════════════════
    ///  [!] 为什么必须多这一层（用户实测 bug）：
    ///      「上次加载 ACR 没关调试窗 ⇒ 这次 AE 一加载调试窗就出来了，
    ///        **ACR 都没加载**」
    ///
    ///      成因：`绘制者` 是 **static**，ACR 卸载**不会清空它** ——
    ///      上次加载时登记的入口实例一直在那儿，而
    ///      `UiBuilder.Draw` 的订阅也从来没被摘掉
    ///      ⇒ 游戏一启动、Dalamud 一渲染，`每帧画` 就跑了
    ///      ⇒ `绘制者 != null` ⇒ 画调试窗 ⇒ **窗口出现在 ACR 加载之前**。
    ///
    ///      ⚠️ 更糟的是：那个实例可能属于**已经被卸载的程序集**
    ///         ⇒ 读游戏对象 ⇒ `AccessViolationException`（退出时必崩）。
    ///
    ///  [!] 判据：**职业匹配**。`绘制者` 只有在"它的 `TargetJob` == 当前职业"
    ///      时才算活着。这一条同时解决两件事：
    ///        · **画对职业** —— `换入口()` 只让匹配的那个当绘制者
    ///        · **防残留** —— 上次加载的残留实例可能职业也匹配，
    ///          所以真正的第一道防线是 `卸载()`（置 null + 摘订阅）
    ///
    ///  [!] ⚠️ **不要**用"框架当前选中了哪个 ACR"当判据（我在这里错了四次）：
    ///      `Job2Rotations` 里同时存着多个奶妈 ACR，
    ///      小鲸鱼没被选中时 `currRotation` 就是**别人的**
    ///      ⇒ 任何"框架当前是不是我们"的判据都会误杀。
    /// ══════════════════════════════════════════════════════════════════════
    /// </summary>
    private static HealerEntryBase? 入口还活着()
    {
        var 入口 = 绘制者;
        if (入口 == null) return null;

        try
        {
            uint 职业;
            try
            {
                var 我 = Core.Me;
                if (我 == null) return 入口;       // 读不到 ⇒ 先接受（下一帧再看）
                职业 = 我.ClassJob.RowId;
            }
            catch { return 入口; }

            if (职业 == 0) return 入口;

            if (职业 != (uint)入口.TargetJob)
            {
                // ⚠️ 打一条日志（30 秒最多一次）——
                //    这个判据已经让我把设置页弄空白五次，所以**不能静默失败**。
                var 现在 = Environment.TickCount64;
                if (现在 - _拒绝日志时刻 >= 30000)
                {
                    _拒绝日志时刻 = 现在;
                    LogHelper.Info("[HealerACR] 独立设置窗：绘制者的职业不匹配 —— " +
                                   $"设置页会显示「尚未就绪」｜入口职业={入口.TargetJob}" +
                                   $"｜当前职业={职业}｜入口类型={入口.GetType().Name}");
                }

                绘制者 = null;                     // 丢弃，等 `换入口()` 换对的进来
                return null;
            }
        }
        catch
        {
            // 判据自己异常 ⇒ 接受（宁可不误伤；残留由 `卸载()` 兜）
            return 入口;
        }

        return 入口;
    }

    /// <summary>
    /// **卸载** —— 摘掉 Dalamud 绘制订阅、丢弃入口实例、复位挂载标记。
    ///
    /// ══════════════════════════════════════════════════════════════════════
    ///  [!] 为什么必须做（用户实测 bug 第 2 条）：
    ///      「在这个情况下开着调试窗退出游戏 ⇒ 报错」
    ///
    ///      原来 L131 挂上 `UiBuilder.Draw` 之后，**全工程没有一处摘它**：
    ///          · 重载 ACR ⇒ 旧委托仍挂在 Dalamud 的绘制事件上
    ///          · 退出游戏 ⇒ 它还在跑，而游戏对象已经在销毁
    ///          · 而 `绘制者` 指向的入口已失效
    ///        ==> 读已销毁对象的原生内存 ==> `AccessViolationException`
    ///            ==> **进程直接死**（用户截图的那个栈）。
    ///
    ///  [!] 卸载顺序（**先断来源，再置标记**）：
    ///      ① 置 `_已卸载 = true` ⇒ 即使还有一帧漏进来，`每帧画` 也直接返回
    ///      ② 摘订阅（失败也无所谓，因为①已经生效）
    ///      ③ 丢 `绘制者` / 关窗口 / 复位 `_已挂载`
    ///
    ///  [!] ⚠️ **不要在这里调 `卸载钩子`**（会成环）——
    ///      `HealerEntryBase.Dispose` 是**先摘钩子再通知**的（那段注释记着
    ///      3.19.4 的栈溢出根因）。本方法只会被那个通知链调到。
    ///
    ///  [!] 幂等：可以重复调（ACR 重载时框架可能对同一入口多次 Dispose）。
    /// ══════════════════════════════════════════════════════════════════════
    /// </summary>
    public static void 卸载()
    {
        _已卸载 = true;          // ★ 第一件事：先把"还会画"这条路的闸门关掉

        // ★ **收起调试窗**（用户要求：只有 BlueWhale 在用的时候才该出现）
        //   反射调 `BlueWhale.AutoHealerACR.调试窗.收起()` ——
        //   `_窗口开` 是 BlueWhale 那份 static，这里改不了。
        //   ⚠️ 心跳（`调试窗心跳`）会在 2.5 秒内**自动过期**，所以这一句
        //      只是"立刻消失"的加速；即使它失败，窗口也会自己没。
        try { 收起调试窗(); } catch { }

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

        // 丢入口 + 收窗口（静态状态必须清干净，否则下次加载会继承上一次的）
        绘制者 = null;
        绘制中 = false;
        显示 = false;
    }

    /// <summary>
    /// 反射调 `BlueWhale.AutoHealerACR.调试窗.收起()`（见 `卸载()` 的说明）。
    /// </summary>
    private static void 收起调试窗()
    {
        try
        {
            foreach (var 程序集 in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? 类型 = null;
                try { 类型 = 程序集.GetType("BlueWhale.AutoHealerACR.调试窗", false); }
                catch { }
                if (类型 == null) continue;

                var 方法 = 类型.GetMethod("收起",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, Type.EmptyTypes, null);
                if (方法 == null) continue;

                方法.Invoke(null, null);
                return;
            }
        }
        catch { }
    }

    /// <summary>切换显示（QT 面板的「设置」页签调它）。</summary>
    public static void 切换()
    {
        尝试挂载();
        显示 = !显示;
    }
}
