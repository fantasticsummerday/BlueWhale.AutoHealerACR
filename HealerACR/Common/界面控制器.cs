using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.View.JobView;
using AEAssist.Helper;
using Dalamud.Bindings.ImGui;

namespace HealerACR.Common;

/// <summary>
/// **界面控制器** —— 一个 `IRotationUI`，内部按开关分流到两种界面。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么要有这一层（而不是直接换掉窗口）★
///
///    框架通过 `IRotationEntry.GetRotationUI()` 拿一个 `IRotationUI`
///    来驱动界面。那个接口只有三个成员：
///        bool IsCustomMain();   // 是不是自绘主窗口
///        void OnDrawUI();       // 每帧画
///        void Update();         // 每帧更新
///
///    所以"换界面"= **换这个接口的实现**，不用碰框架。
///
///  ── 分流的意义 ──
///    · `使用主题界面` 开 → `IsCustomMain()` 返回 true，框架让开，
///      我们自己画主窗口 + QT 面板
///    · 关 → 全部转发给 `JobViewWindow`，**行为跟以前完全一样**
///
///    ⇒ 自绘万一哪里不对，用户拨一下开关就回到原样，不必等我修。
///      对"自己画窗口"这种事来说，这个退路是必要的 ——
///      窗口行为（拖动/缩放/滚动/贴边）出问题会直接影响能不能用。
///
///  ⚠️ **Qt 的登记转发到 `JobViewWindow`**：
///      `HealQt` 依赖 `AddQt` / `RemoveAllQt` / `SetQt` / `GetQt`，
///      而这些在框架窗口上是现成且经过验证的。
///      自绘窗口只**读**开关的值来画界面，不自己存 ——
///      自己存一份会立刻变成"两个真相"，那是这个项目反复踩过的坑。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public sealed class 界面控制器 : IRotationUI
{
    private readonly JobViewWindow _框架窗口;

    /// <summary>自绘主窗口</summary>
    private readonly 小鲸鱼面板 _主窗口;

    /// <summary>自绘 QT 面板（开关）</summary>
    private readonly 小鲸鱼面板 _Qt面板;

    /// <summary>内容装配（页签、标题栏按钮）—— 由入口类注入</summary>
    private readonly Action<小鲸鱼面板> _装内容;

    public 界面控制器(JobViewWindow 框架窗口, Action<小鲸鱼面板> 装内容)
    {
        _框架窗口 = 框架窗口;
        _装内容 = 装内容;

        _主窗口 = new 小鲸鱼面板("小鲸鱼##主", "小鲸鱼")
        {
            初始位置 = new System.Numerics.Vector2(120f, 140f),
            尺寸 = new System.Numerics.Vector2(600f, 520f),
        };

        _Qt面板 = new 小鲸鱼面板("小鲸鱼##QT", "快捷开关")
        {
            初始位置 = new System.Numerics.Vector2(760f, 140f),
            尺寸 = new System.Numerics.Vector2(300f, 420f),
        };

        _主窗口.标题栏按钮.Add(("开关", () => _Qt面板.可见 = !_Qt面板.可见));

        try { 装内容?.Invoke(_主窗口); } catch (Exception e) { LogHelper.Error("[界面] 装内容失败：" + e.Message); }
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
    /// **是不是自绘主窗口** —— 框架靠这个决定要不要画它自己的职业窗口。
    ///
    /// ⚠️ 返回 true 时框架**完全让开** —— 所以我们必须把该画的都画了，
    ///     否则用户会看到"面板不见了"。
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

            // ── 自绘：主窗口 + QT 面板 ──
            画Qt面板();
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
    /// 画 QT 面板 —— **一行两个开关**的紧凑排布。
    ///
    /// ⚠️ 开关的**值**从 `HealQt` 读、**写**回 `HealQt`，
    ///     不碰框架窗口的内部状态 —— 保证只有一个真相。
    /// </summary>
    private void 画Qt面板()
    {
        if (!_Qt面板.可见) return;

        try
        {
            _Qt面板.页签.Clear();

            var 全部 = HealQt.全部开关();
            if (全部 == null || 全部.Length == 0)
            {
                _Qt面板.页签.Add(("开关", () => ImGui.TextDisabled("（这个职业没有注册开关）")));
            }
            else
            {
                _Qt面板.页签.Add(("开关", () =>
                {
                    try
                    {
                        var 缩放 = 主题.缩放();
                        var 可用宽 = ImGui.GetContentRegionAvail().X;
                        var 列宽 = Math.Max(80f, (可用宽 - 8f * 缩放) * 0.5f);

                        var 列 = 0;

                        foreach (var 名 in 全部)
                        {
                            if (string.IsNullOrWhiteSpace(名)) continue;

                            // ⚠️ 用 `SameLine(列宽)` 手动定位第二列 ——
                            //     不用 `SameLine()`（它接在上一个控件后面，
                            //     而复选框宽度随文字长度变，两列会参差不齐）。
                            if (列 == 1)
                            {
                                ImGui.SameLine(列宽 + 8f * 缩放);
                            }

                            var v = HealQt.GetQt(名);
                            if (ImGui.Checkbox(名 + "##qt", ref v))
                                HealQt.SetQt(名, v);

                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(名);

                            // 满两列换行
                            列++;
                            if (列 >= 2) 列 = 0;
                        }
                    }
                    catch { }
                }));
            }

            _Qt面板.副标题 = $"共 {全部?.Length ?? 0} 个";
            _Qt面板.画();
        }
        catch { }
    }
}
