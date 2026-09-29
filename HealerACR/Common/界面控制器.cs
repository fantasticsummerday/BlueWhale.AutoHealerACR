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

    /// <summary>画 ACR 设置内容（由入口类注入 = `OnDrawSetting`）</summary>
    private readonly Action? _画设置;

    /// <summary>触发"保存设置"（由入口类注入）</summary>
    private readonly Action? _保存;

    // 反射拿到的框架内部对象（取不到就是 null）
    private readonly object? _qt窗口;
    private readonly object? _样式;

    public 界面控制器(JobViewWindow 框架窗口, Action? 画设置 = null, Action? 保存 = null)
    {
        _框架窗口 = 框架窗口;
        _画设置 = 画设置;
        _保存 = 保存;

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

            // ── ② 自绘主面板（里面是 ACR 设置）──
            _主窗口.页签.Clear();
            if (_画设置 != null)
                _主窗口.页签.Add(("设置", () => 画设置内容()));

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
    /// **把 ACR 设置画进主题面板的子窗口**。
    ///
    /// ⚠️ 必须用 `BeginChild` 包起来 ——
    ///     设置内容里有很多 `CollapsingHeader` 和控件，
    ///     直接画在面板里会撑破自绘的边框、滚动也不受控。
    ///     子窗口把滚动交给 ImGui 管，边框永远是我们的。
    /// </summary>
    private void 画设置内容()
    {
        try
        {
            ImGui.BeginChild("##设置内容", new System.Numerics.Vector2(0, 0), false,
                             ImGuiWindowFlags.AlwaysVerticalScrollbar);
            try
            {
                _画设置?.Invoke();
            }
            finally
            {
                ImGui.EndChild();
            }
        }
        catch (Exception e)
        {
            try { ImGui.TextColored(主题.危险, "设置绘制异常：" + e.Message); } catch { }
        }
    }

    /// <summary>
    /// **启动 / 停手** —— 找回框架主窗口里那两个按钮。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么状态用 `Share` 而不是自己存 ★
    ///
    ///    `AEAssist.Share.Pull` / `TrustStopACR` 是框架的 **public 静态字段** ——
    ///    框架自己的主窗口、快捷键、IPC、时间轴动作**全都读写这两个字段**。
    ///
    ///    如果我们在自己这边再存一份 `bool _已启动`，会出现：
    ///      · 用户按快捷键启动 → 框架改了 Share，我们的 bool 还是 false
    ///        → 面板显示"未启动"，但实际在打
    ///      · 用户按我们的按钮 → 只改了我们的 bool，框架不知道
    ///        → 点了没反应
    ///
    ///    ⇒ **直接读写 Share**，只有一个真相。
    ///
    ///  ⚠️ 按钮文案带状态（"启动"/"已启动"）而不是做成 toggle 外观 ——
    ///     这是**战斗中的关键开关**，一眼能看出当前状态比好看重要。
    ///     用颜色区分：未启用=灰、已启用=绿。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private void 画启动停手()
    {
        try
        {
            var 缩放 = 主题.缩放();

            // ── 启动 / 停止 ──
            bool 启动中;
            try { 启动中 = AEAssist.Share.Pull; } catch { 启动中 = false; }

            var 启动色 = 启动中 ? 主题.成功 : 主题.卡片悬停;
            if (画状态按钮(启动中 ? "已启动" : "启 动", 启动色, 90f * 缩放))
            {
                try { AEAssist.Share.Pull = !启动中; } catch { }
            }

            ImGui.SameLine(0, 8f * 缩放);

            // ── 停手 ──
            bool 停手中;
            try { 停手中 = AEAssist.Share.TrustStopACR; } catch { 停手中 = false; }

            var 停手色 = 停手中 ? 主题.危险 : 主题.卡片悬停;
            if (画状态按钮(停手中 ? "已停手" : "停 手", 停手色, 90f * 缩放))
            {
                try { AEAssist.Share.TrustStopACR = !停手中; } catch { }
            }

            // ── 右边补一句状态说明 ──
            //
            //  ⚠️ 为什么要有文字说明：这两个开关**决定了 ACR 动不动**，
            //     但按钮本身只能表达"开/关"。加一句白话，
            //     让"为什么它不动"一眼可查。
            ImGui.SameLine(0, 12f * 缩放);
            try
            {
                var 说明 = 停手中 ? "ACR 已停手（不会出手）"
                         : 启动中 ? "运行中"
                         : "未启动（需点启动）";
                ImGui.TextColored(主题.文字弱, 说明);
            }
            catch { }
        }
        catch { }
    }

    /// <summary>
    /// 画一个**带状态底色**的按钮。
    ///
    /// ⚠️ 不用 `ImGui.PushStyleColor(Button, ...)` 包一层再 Button ——
    ///     那样要推弹 3 个颜色 × 2 个按钮 = 12 次调用，还容易漏弹。
    ///     这里用一个 `InvisibleButton` + 自绘外观，和标题栏按钮同一套做法。
    /// </summary>
    /// <returns>是否被点击</returns>
    private static bool 画状态按钮(string 文本, System.Numerics.Vector4 底色, float 宽)
    {
        try
        {
            var 缩放 = 主题.缩放();
            var 高 = ImGui.GetFrameHeight();
            var 起点 = ImGui.GetCursorScreenPos();

            var 命中 = ImGui.InvisibleButton("##btn" + 文本, new System.Numerics.Vector2(宽, 高));
            var 悬停 = ImGui.IsItemHovered();

            var 绘制 = ImGui.GetWindowDrawList();
            var 左上 = 起点;
            var 右下 = new System.Numerics.Vector2(起点.X + 宽, 起点.Y + 高);

            // 底色（悬停时提亮一点，给点击反馈）
            var 实际 = 悬停
                ? new System.Numerics.Vector4(
                    Math.Min(1f, 底色.X + 0.12f),
                    Math.Min(1f, 底色.Y + 0.12f),
                    Math.Min(1f, 底色.Z + 0.12f),
                    底色.W)
                : 底色;

            绘制.AddRectFilled(左上, 右下,
                ImGui.ColorConvertFloat4ToU32(实际), 主题.圆角);

            // 描边（让灰底按钮在深色背景上也看得见轮廓）
            绘制.AddRect(左上, 右下, ImGui.ColorConvertFloat4ToU32(主题.边框),
                         主题.圆角, ImDrawFlags.None, 1f);

            // 文字居中
            var 文字尺寸 = ImGui.CalcTextSize(文本);
            var 文字位 = new System.Numerics.Vector2(
                起点.X + (宽 - 文字尺寸.X) * 0.5f,
                起点.Y + (高 - 文字尺寸.Y) * 0.5f);

            绘制.AddText(文字位, ImGui.ColorConvertFloat4ToU32(主题.文字强), 文本);

            return 命中;
        }
        catch
        {
            return false;
        }
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
