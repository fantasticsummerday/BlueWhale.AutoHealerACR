using System.Reflection;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.View.JobView;
using AEAssist.Helper;
using Dalamud.Bindings.ImGui;

namespace HealerACR.Common;

/// <summary>
/// **界面控制器** —— 一个 `IRotationUI`，内部按开关分流到两种界面。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 框架的窗口结构（决定了这里能做什么）★
///
///    `JobViewWindow` 内部是**四个独立对象**：
///        mainWindow    —— 主窗口（页签 + 内容）  ← **无法嵌入**（没有公开 Draw）
///        qtWindow      —— QT 开关面板            ← **能单独画**（DrawQtWindow 公开）
///        hotkeyWindow  —— 快捷键面板
///        style         —— 样式
///
///  ── 所以分成两条路 ──
///    ① **主面板**：自绘主题外壳，内容用 `OnDrawSetting()` ——
///       那正是 ACR 设置的绘制入口（原版设置 + AI 设置 + 记忆库），
///       **复用**而不是重写，两边就不可能不一致。
///
///    ② **QT 面板**：**用框架自己的 `DrawQtWindow`** ——
///       保持原来的样式（绿色按钮那套）。
///
///       ⚠️ 我第一版自己重画了 QT（复选框列表），**这是错的** ——
///          QT 面板有它自己的既定外观，用户认的是那个。
///          重画 = 白白多一套要维护的样式，而且和框架其他地方不一致。
///
///  ⚠️ `qtWindow` / `style` 是 `private` 字段，只能反射取。
///     取不到就**不画 QT**（框架会自己处理），不要抛异常。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public sealed class 界面控制器 : IRotationUI
{
    private readonly JobViewWindow _框架窗口;

    /// <summary>自绘主窗口</summary>
    private readonly 小鲸鱼面板 _主窗口;

    /// <summary>画「阈值」页（由入口类注入 = `画阈值设置`）</summary>
    private readonly Action? _画设置;

    /// <summary>画「优先级」页</summary>
    private readonly Action? _画优先级;

    /// <summary>画「职业」页（职业资源设置）</summary>
    private readonly Action? _画职业;

    /// <summary>画「AI」页（BlueWhale 注入）</summary>
    private readonly Action? _画Ai;

    /// <summary>画「记忆库」页（BlueWhale 注入）</summary>
    private readonly Action? _画记忆库;

    /// <summary>触发"保存设置"（由入口类注入）</summary>
    private readonly Action? _保存;

    // 反射拿到的框架内部对象（取不到就是 null）
    private readonly object? _qt窗口;
    private readonly object? _样式;
    private readonly object? _主窗口对象;      // MainWindow

    /// <summary>
    /// 「启动」按钮的状态 —— 传给框架的 `MainControlView`。
    ///
    /// ⚠️ 这个 bool **必须留在这里跨帧保留**，不能每帧从别处重算：
    ///     `MainControlView` 用 `ref` 收它、按它决定按钮画成什么样，
    ///     用户点了之后**改的也是它**。每帧重置 = 点了没反应。
    /// </summary>
    private bool _启动按钮;

    /// <summary>「停手」按钮的状态（同上）</summary>
    private bool _停手按钮;

    /// <summary>第一次画的时候从框架状态初始化一次</summary>
    private bool _启动初值取过了;

    /// <summary>
    /// 构造。
    ///
    /// ⚠️ 各个"画法"**由入口类注入**，控制器不认识具体的页面 ——
    ///     这样它不用知道 ACR 有哪些页，加页只改入口类。
    /// </summary>
    public 界面控制器(JobViewWindow 框架窗口,
                       Action? 画阈值 = null,
                       Action? 保存 = null,
                       Action? 画优先级 = null,
                       Action? 画职业 = null,
                       Action? 画Ai = null,
                       Action? 画记忆库 = null)
    {
        _框架窗口 = 框架窗口;
        _画设置 = 画阈值;
        _保存 = 保存;
        _画优先级 = 画优先级;
        _画职业 = 画职业;
        _画Ai = 画Ai;
        _画记忆库 = 画记忆库;

        _主窗口 = new 小鲸鱼面板("小鲸鱼##主", "小鲸鱼")
        {
            初始位置 = new System.Numerics.Vector2(120f, 140f),
            尺寸 = new System.Numerics.Vector2(620f, 540f),
        };

        // 标题栏按钮：保存设置 / 重载提示
        if (_保存 != null)
            _主窗口.标题栏按钮.Add(("保存设置", () => { try { _保存(); } catch { } }));

        // ★ 顶部常驻控件：启动 / 停手 ★
        //
        //  ⚠️ 这两个**必须自己做** ——
        //     它们原来画在框架主窗口里，而 `IsCustomMain()` 返回 true 时
        //     框架**不画主窗口** → 启动/停手就跟着消失了。
        //
        //  状态存在框架的 `AEAssist.Share` 里（public 静态字段，可读可写）：
        //     `Share.Pull`         —— 启动（开怪）
        //     `Share.TrustStopACR` —— 停手
        //  ⇒ 直接读写它们 = **和框架共用同一个状态**，
        //     不会出现"面板说停手了、实际还在打"这种两个真相。
        _主窗口.顶部控件 = 画启动停手;

        // 反射取框架的 qtWindow（用于把 QT 面板按原样画出来）
        try
        {
            var 类型 = 框架窗口.GetType();
            const BindingFlags 旗 = BindingFlags.NonPublic | BindingFlags.Instance;

            _qt窗口 = 类型.GetField("qtWindow", 旗)?.GetValue(框架窗口);
            _主窗口对象 = 类型.GetField("mainWindow", 旗)?.GetValue(框架窗口);

            // ⚠️ `style` 是 **public 字段**，直接访问 —— 不用反射。
            //    我第一版用 `GetField("style", NonPublic)` 取它，
            //    结果是**永远拿到 null**（因为它是 public），
            //    而 `DrawQtWindow(null)` 会把 QT 面板画崩或画不出来。
            _样式 = 框架窗口.style;

            LogHelper.Info($"[界面] 框架内部对象：qtWindow={(_qt窗口 != null ? "有" : "无")} " +
                           $"style={(_样式 != null ? "有" : "无")}");
        }
        catch (Exception e)
        {
            LogHelper.Info("[界面] 反射取框架内部对象失败：" + e.Message);
        }
    }

    /// <summary>
    /// **补上 AI / 记忆库两页**（BlueWhale 在构建时调）。
    ///
    /// ⚠️ 为什么不在构造里传：
    ///     那两个页在 `BlueWhale` 里，而控制器在 `HealerACR`。
    ///     构造时机在 `HealerEntryBase.Build`，
    ///     那时候子类（蓝鲸入口）还没走到自己的注入代码，
    ///     所以留一个入口给子类后补。
    /// </summary>
    public void 补Ai页(Action? 画Ai, Action? 画记忆库)
    {
        try
        {
            // ⚠️ 字段是 readonly，所以这里用**可变的回调包一层**
            //   —— 直接改 readonly 字段编译不过。
            if (画Ai != null) _画Ai额外 = 画Ai;
            if (画记忆库 != null) _画记忆库额外 = 画记忆库;
        }
        catch { }
    }

    private Action? _画Ai额外;
    private Action? _画记忆库额外;

    /// <summary>当前是不是走自绘界面</summary>
    private static bool 用主题
    {
        get
        {
            try { return HealSettings.Instance.使用主题界面; }
            catch { return false; }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  IRotationUI
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// **是不是自绘主窗口** —— 框架靠这个决定要不要画它自己的职业主窗口。
    ///
    /// ⚠️ 返回 true 时框架**不画主窗口**（但 QT / 快捷键窗口它照画）——
    ///     所以我们必须把设置内容画出来，否则用户会觉得"面板空了"。
    /// </summary>
    public bool IsCustomMain() => 用主题;

    public void Update()
    {
        try
        {
            // 缩放跟着设置走（每帧取，改了立刻生效）
            try { 主题.用户缩放 = Math.Clamp(HealSettings.Instance.界面缩放, 0.8f, 1.6f); }
            catch { }

            // 背景透明度也跟着设置走（改了立刻生效）
            try { 主题.背景透明 = Math.Clamp(HealSettings.Instance.背景透明度, 0f, 1f); }
            catch { }

            if (!用主题)
            {
                _框架窗口.Update();
                return;
            }
        }
        catch (Exception e)
        {
            LogHelper.Info("[界面] Update 异常（已忽略）：" + e.Message);
        }
    }

    public void OnDrawUI()
    {
        try
        {
            // ── 开关关掉 → 老路，原样转发 ──
            if (!用主题)
            {
                _框架窗口.OnDrawUI();
                return;
            }

            // ── ① QT 面板：**用框架自己的画法**（保持原样式）──
            //
            //   ⚠️ 顺序在自绘主窗口**之前** —— 主窗口是本 ACR 的主体，
            //      画在后面让它压在 QT 面板之上（QT 是"工具箱"，不该抢焦点）。
            画Qt原样();

            // ── ② 自绘主面板：**每个页签调一个对应的绘制方法** ──
            //
            //  ⚠️ 我上一版只加了**一个**「设置」页，把所有东西挤在一起 ——
            //     那是错的：原来框架窗口里是**七个页签**
            //     （优先级 / 职业 / 阈值 / 时间轴 / Qt / Hotkey / 风格），
            //     挤成一坨之后既难找、又和用户熟悉的布局不一样。
            //
            //  ⇒ 按页签拆开，**每页调它自己的绘制方法**（全是框架/项目里现成的）：
            //       · 优先级 / 职业 / 阈值 / AI / 记忆库 → 我们自己的画法
            //       · Qt / Hotkey / 风格                → 框架的公开方法
            //     这样既复用代码，布局也和原来对得上。
            装页签();
            _主窗口.画();
        }
        catch (Exception e)
        {
            // ⚠️ UI 异常绝不能往上抛 —— 会打断整个绘制循环，
            //    表现是"面板消失且再也不出现"。宁可这一帧画不出来。
            LogHelper.Info("[界面] OnDrawUI 异常（已忽略）：" + e.Message);
        }
    }

    /// <summary>
    /// **装页签** —— 每页对应一个绘制方法。
    ///
    /// ⚠️ 页签**每帧重建**：因为 `HealQt.绑定()` 会在切职业时换窗口，
    ///     而页签列表如果只在构造时装一次，切完职业内容就没了。
    ///     重建的开销是几个委托分配，可以忽略。
    ///
    /// ⚠️ 顺序是**刻意排的**：常用的（优先级/职业/阈值）放前面，
    ///     配置性的（Qt/Hotkey/风格）放后面 —— 和框架原来的顺序一致。
    /// </summary>
    private void 装页签()
    {
        _主窗口.页签.Clear();

        // ── 我们自己的页 ──
        if (_画优先级 != null) _主窗口.页签.Add(("优先级", () => 包一层("优先级", _画优先级)));
        if (_画职业 != null) _主窗口.页签.Add(("职业", () => 包一层("职业", _画职业)));

        if (_画设置 != null) _主窗口.页签.Add(("阈值", () => 包一层("阈值", _画设置)));

        // ── 框架的页（公开方法）──
        //
        //  ⚠️ 这三个是框架自带的：
        //     `QtSettingView` / `HotkeySettingView` / `ChangeStyleView`
        //     它们**只画内容**，不自己开窗口 —— 正好放进我们的子窗口。
        //
        //  ⚠️ **没有「时间轴」页**：时间轴在框架那边是 AEAssist 的
        //     时间轴编辑器（宿主插件的页面），不在 ACR 窗口里。
        //     我们的时间轴设置走「阈值」页里的「减伤 / 时间轴」折叠段。
        _主窗口.页签.Add(("Qt", () => 包一层("Qt", () => _框架窗口.QtSettingView())));
        _主窗口.页签.Add(("Hotkey", () => 包一层("Hotkey", () => _框架窗口.HotkeySettingView())));
        _主窗口.页签.Add(("风格", () => 包一层("风格", () => _框架窗口.ChangeStyleView())));

        // ── AI 相关（由 BlueWhale 注入）──
        var AI画 = _画Ai ?? _画Ai额外;
        if (AI画 != null) _主窗口.页签.Add(("AI", () => 包一层("AI", AI画)));
        var 记忆画 = _画记忆库 ?? _画记忆库额外;
        if (记忆画 != null) _主窗口.页签.Add(("记忆库", () => 包一层("记忆库", 记忆画)));
    }

    /// <summary>
    /// **把一页的内容包进子窗口** —— 滚动交给 ImGui 管，边框永远是我们的。
    ///
    /// ⚠️ 每页必须有自己的子窗口 ID（用页签名），
    ///     否则两页共用一个 ID 时 ImGui 会把滚动位置串起来 ——
    ///     现象是"切到下一页，滚动条停在上一页的位置"。
    /// </summary>
    private void 包一层(string 页名, Action 画)
    {
        try
        {
            ImGui.BeginChild("##页_" + 页名, new System.Numerics.Vector2(0, 0), false,
                             ImGuiWindowFlags.AlwaysVerticalScrollbar);
            try
            {
                画();
            }
            finally
            {
                ImGui.EndChild();
            }
        }
        catch (Exception e)
        {
            try { ImGui.TextColored(主题.危险, $"{页名} 页绘制异常：{e.Message}"); } catch { }
        }
    }

    /// <summary>
    /// **启动 / 停手** —— 找回框架主窗口里那两个按钮。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么状态用 `Share` 而不是自己存 ★
    ///
    ///    `AEAssist.Share.Pull` / `TrustStopACR` 是框架的 **public 静态字段**。
    ///    如果自己再存一份 `bool _已启动`，会出现：
    ///      · 按快捷键启动 → 框架改了 Share，我们的 bool 还是 false
    ///        → 面板显示"未启动"，但实际在打
    ///      · 按面板按钮 → 只改我们的 bool，框架不知道 → 点了没反应
    ///
    ///    ⇒ **直接读写 Share**，只有一个真相。
    ///
    ///  ⚠️ 按钮文案带状态（"启动"/"已启动"）而不是做成 toggle 外观 ——
    ///     这是**战斗中的关键开关**，一眼能看出当前状态比好看重要。
    ///     用颜色区分：未启用=灰、已启用=绿。
    ///
    ///  ⚠️ **每次点击都打一行日志**，把四个相关字段全打出来：
    ///     `Pull` / `TrustStopACR` / `CombatRun` / `StopNormalACR`。
    ///
    ///     为什么必须打：框架的 `MainControlView` **看不到内部实现**，
    ///     光靠签名猜不出它到底按哪个字段判断"启动了没有"。
    ///     点了没反应时，这行日志能直接指出：
    ///       · 字段根本没变        → 我们写失败了（或被框架每帧重置）
    ///       · 字段变了但没启动    → 我们写错了字段
    ///     没有这行日志，就只能继续猜。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private void 画启动停手()
    {
        try
        {
            // ══════════════════════════════════════════════════════════
            //  ★ 用框架自己的 `MainWindow.MainControlView` ★
            //
            //  ── 为什么不用自己画的按钮（IL 实证）──
            //    我第一版自绘了两个按钮、直接读写 `AEAssist.Share.Pull`。
            //    实测**点了没反应** —— 因为那个假设是错的：
            //
            //      · 扫 AEAssist 全量 IL：`MainWindow.MainControlView`
            //        **完全没有**读写过 `Share.Pull` / `CombatRun`
            //      · `Share.CombatRun` 有 9 处写，其中 `JobViewWindow.OnDrawUI`
            //        **读**它来显示状态 —— 说明界面显示的状态另有来源
            //      · 框架真正按什么判断"启动了没有"，从签名猜不出来
            //
            //    ⇒ **别再猜**。`MainControlView` 是公开方法，而且：
            //        · 里面 `ImGui.Begin` 调用 **0 次** → 它不自己开窗口，
            //          只画按钮，正好能放进我们的面板
            //        · 它**完全不碰 `Share.*`** → 按钮状态走两个 `ref` 参数
            //      让框架自己画、自己管状态，语义就不可能错。
            //
            //  ⚠️ 两个 bool **必须跨帧保留**（见字段说明）：
            //     每帧重置的话，用户点了下一帧就被覆盖 → 还是"点了没反应"。
            // ══════════════════════════════════════════════════════════
            if (_主窗口对象 == null)
            {
                ImGui.TextColored(主题.警告, "拿不到框架的启动控件（mainWindow）");
                return;
            }

            var 方法 = _主窗口对象.GetType().GetMethod("MainControlView",
                BindingFlags.Public | BindingFlags.Instance);

            if (方法 == null)
            {
                ImGui.TextColored(主题.警告, "框架没有 MainControlView 方法");
                return;
            }

            // ── 第一次画的时候，从框架状态取初值 ──
            //
            //  ⚠️ 只在**第一次**取 —— 之后就以按钮自己为准。
            //     每帧都从 Share 重算会把用户的点击覆盖掉。
            if (!_启动初值取过了)
            {
                _启动初值取过了 = true;
                try { _启动按钮 = AEAssist.Share.CombatRun; } catch { }
                try { _停手按钮 = AEAssist.Share.TrustStopACR; } catch { }
            }

            object?[] 参数 = { _启动按钮, _停手按钮, (Action)(() => { try { _保存?.Invoke(); } catch { } }) };

            try
            {
                方法.Invoke(_主窗口对象, 参数);
            }
            catch (Exception e)
            {
                ImGui.TextColored(主题.警告, "启动控件绘制失败：" + e.Message);
                return;
            }

            // ── 把框架改过的值收回来 ──
            //
            //  ⚠️ `ref` 参数在反射里就是"传进去、拿回来" ——
            //     `Invoke` 之后 `参数` 数组里的值已经是框架写过的新值。
            var 新启动 = 参数[0] is bool b1 ? b1 : _启动按钮;
            var 新停手 = 参数[1] is bool b2 ? b2 : _停手按钮;

            if (新启动 != _启动按钮 || 新停手 != _停手按钮)
            {
                _启动按钮 = 新启动;
                _停手按钮 = 新停手;
                记启动状态("框架按钮被点击");
            }
        }
        catch { }
    }
    /// <summary>
    /// 把启动相关的**四个字段全打出来**（诊断用）。
    ///
    /// ⚠️ 为什么四个都打：不知道框架按哪个判断，
    ///     全打出来才能反推 —— 点了哪个字段变了、哪个没变，一目了然。
    /// </summary>
    private static void 记启动状态(string 来源)
    {
        try
        {
            string 读(string 名, Func<bool> 取)
            {
                try { return 取() ? "真" : "假"; } catch { return "?"; }
            }

            LogHelper.Info($"[界面] {来源} → " +
                           $"Pull={读("Pull", () => AEAssist.Share.Pull)} " +
                           $"TrustStop={读("TrustStop", () => AEAssist.Share.TrustStopACR)} " +
                           $"CombatRun={读("CombatRun", () => AEAssist.Share.CombatRun)} " +
                           $"StopNormal={读("StopNormal", () => AEAssist.Share.StopNormalACR)}");
        }
        catch { }
    }


    /// <summary>
    /// **QT 面板：调框架的 `DrawQtWindow(style)`** —— 原样式，零维护。
    ///
    /// ⚠️ 拿不到 qtWindow / style 时**什么都不做** ——
    ///     那种情况下框架自己会把 QT 画出来（它没被我们挡掉），
    ///     我们再画一次就重了。
    /// </summary>
    private void 画Qt原样()
    {
        if (_qt窗口 == null) return;

        try
        {
            var 方法 = _qt窗口.GetType().GetMethod("DrawQtWindow",
                BindingFlags.Public | BindingFlags.Instance);

            if (方法 == null) return;

            // ⚠️ `style` 可能为 null（构造早期）—— 传 null 进去框架自己会处理
            方法.Invoke(_qt窗口, new[] { _样式 });
        }
        catch (Exception e)
        {
            LogHelper.Info("[界面] 画 QT 面板失败（已忽略）：" + e.Message);
        }
    }
}
