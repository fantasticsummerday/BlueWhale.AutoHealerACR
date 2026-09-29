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
