using System.Numerics;
using AEAssist.Helper;
using Dalamud.Bindings.ImGui;

namespace HealerACR.Common;

/// <summary>
/// **小鲸鱼面板** —— 自绘的带边框主题窗口。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么不用框架的 `JobViewWindow` ★
///
///    框架窗口的外观是固定的（系统风格标题栏 + 无边框内容），
///    改不了圆角、描边、标题栏底色 —— 而"主题"恰恰就是这些东西。
///
///    所以这里**自己 `ImGui.Begin`**，用 NoTitleBar|NoBackground
///    把系统外观全部关掉，然后**自己画**：
///        · 底板（圆角矩形）
///        · 标题栏（主色渐变 + 标题 + 折叠按钮）
///        · 外框描边
///        · 页签
///    这和参考实现 A 的做法一致（它 `JobViewWindow` 用了 0 次，
///    全自研 26 个 UI 文件）。
///
///  ── 视觉规格（决定了"好不好看"）──
///    · 圆角 4px、描边 1.5px —— 太圆显廉价，太直显生硬
///    · 标题栏高 30px，底色是主色暗；底部一条主色亮的分隔线
///    · 底板 alpha 0.92 —— 能看清字，又能透出一点游戏画面
///    · 所有尺寸过 `主题.缩放()` —— 4K 下不会细成头发丝
///
///  ⚠️ 样式推弹必须配对（`主题.推样式` / `主题.弹样式`）——
///     推了不弹会把**别的插件**的窗口一起染色。
///     这里用 `try/finally` 保证异常时也弹。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public sealed class 小鲸鱼面板
{
    /// <summary>面板标识（ImGui 用它区分窗口，必须唯一）</summary>
    public string 标识 { get; }

    /// <summary>标题（显示在自绘标题栏里）</summary>
    public string 标题 { get; set; }

    /// <summary>副标题（标题右边的小字，可空）</summary>
    public string 副标题 { get; set; } = "";

    /// <summary>是否折叠（只留标题栏）</summary>
    public bool 折叠 { get; set; }

    /// <summary>是否可见</summary>
    public bool 可见 { get; set; } = true;

    /// <summary>窗口位置（首次打开时用；用户拖动后由 ImGui 自己记）</summary>
    public Vector2 初始位置 { get; set; } = new(120f, 120f);

    /// <summary>窗口尺寸</summary>
    public Vector2 尺寸 { get; set; } = new(560f, 480f);

    /// <summary>
    /// **页签** —— 名字 + 绘制函数。空列表 = 不画页签栏（单页窗口）。
    ///
    /// ⚠️ 绘制函数里**不要自己 Begin/End 窗口** —— 已经在窗口里了。
    ///     要滚动的话用 `ImGui.BeginChild`。
    /// </summary>
    public List<(string 名字, Action 画)> 页签 { get; } = new();

    /// <summary>当前选中的页签下标</summary>
    private int _当前页;

    /// <summary>标题栏右侧的自定义按钮（比如"保存""重置"）</summary>
    public List<(string 名字, Action 点击)> 标题栏按钮 { get; } = new();

    /// <summary>
    /// **内容区最上面的一行自定义控件**（可选）。
    ///
    /// 用来放"启动 / 停手"这类**常驻控制** ——
    /// 它们不属于任何页签，应该一直在最上面。
    /// 为空则不占空间。
    /// </summary>
    public Action? 顶部控件 { get; set; }

    public 小鲸鱼面板(string 标识, string 标题)
    {
        this.标识 = 标识;
        this.标题 = 标题;
    }

    /// <summary>
    /// **每帧调用一次** —— 画整个窗口。
    ///
    /// 调用点：由 `界面控制器.OnDrawUI` 每帧调。
    /// </summary>
    public void 画()
    {
        if (!可见) return;

        try
        {
            var 缩放 = 主题.缩放();

            // 首次出现时定位（`Once` = 用户拖过之后就不再干预）
            if (初始位置 != Vector2.Zero)
                ImGui.SetNextWindowPos(初始位置, ImGuiCond.Once);

            // ══════════════════════════════════════════════════════
            //  ★ 折叠 / 展开的尺寸（两个 bug 都在这里）★
            //
            //  ── bug ①：折叠后是一大片空白（不是"没画标题栏"）──
            //     折叠时想把高度设成 `标题栏高 + 8` ≈ 38px，
            //     但 `SetNextWindowSizeConstraints` 的**最小高是 120px**，
            //     ImGui 把 38 **钳回 120** → 窗口还是一大块，
            //     而内容区因为 `折叠` 为 true 不画 → 一大片空白。
            //     ⇒ 约束必须跟着一起降。
            //
            //  ── bug ②：展开后内容全没了（**这个是更隐蔽的**）──
            //     折叠时用了 `ImGuiCond.Always`（每帧强制设成 38px），
            //     而展开时用的是 `ImGuiCond.FirstUseEver`（**只在窗口首次出现时生效**）。
            //     ⇒ 对已经存在的窗口，`FirstUseEver` **什么都不做** ——
            //       窗口高度**一直停在折叠时的 38px**，
            //       内容区高度 = 38 - 标题栏 ≈ 0 → 展开后一片空。
            //
            //     为什么会长得跟"内容没画"一样：其实内容是画了的，
            //     只是被压在一个 0 高度的区域里，一个像素都露不出来。
            //
            //     ⇒ **展开时也必须 `Always` 把尺寸设回去**，
            //       而且要设成 `尺寸`（那个会被 `记录尺寸()` 更新的"上次用户尺寸"），
            //       不是原始默认值 —— 否则用户调过的窗口大小会被重置。
            // ══════════════════════════════════════════════════════
            // ⚠️ 折叠宽度的下限**不能随便定** ——
            //    标题栏上的按钮是**从右往左**排的（折叠 / 保存设置），
            //    再加上左边的标题文字。窄过头就会出现
            //    "标题压在按钮下面"或者"按钮跑出窗口"。
            //
            //    所以按**实际内容宽度**算下限，而不是拍一个固定数字。
            var 折叠宽 = Math.Max(标题栏最小宽度(), 尺寸.X * 缩放 * 0.42f);
            var 折叠高 = 主题.标题栏高 + 8f * 缩放;

            // ⚠️ 目标尺寸
            var 目标 = 折叠 ? new Vector2(折叠宽, 折叠高)
                            : new Vector2(尺寸.X * 缩放, 尺寸.Y * 缩放);

            // ⚠️ 两个方向都用 `Always` —— 见上面 bug ② 的说明
            ImGui.SetNextWindowSize(目标, ImGuiCond.Always);

            // ⚠️ 约束**必须**跟着折叠状态走，否则 `SetNextWindowSize` 会被钳掉
            //
            //   最小宽同理：展开时 320px 是排版下限，
            //   折叠时只有标题栏，可以窄得多。
            var 最小宽 = 折叠 ? 120f * 缩放 : 320f * 缩放;
            var 最小高 = 折叠 ? 折叠高 : 120f * 缩放;

            ImGui.SetNextWindowSizeConstraints(
                new Vector2(最小宽, 最小高),
                new Vector2(float.MaxValue, float.MaxValue));

            // ══════════════════════════════════════════════════════
            //  ★ 关键：把系统外观全关掉 ★
            //     NoTitleBar       —— 标题栏自己画（要主题色）
            //     NoBackground     —— 背景自己画（要圆角 + 半透明）
            //     NoScrollbar      —— 内容自己管滚动（否则和自绘边框打架）
            //     NoScrollWithMouse—— 防止滚轮滚了整个窗口而不是内容
            //     NoCollapse       —— 折叠由标题栏按钮控制，不用系统那个
            //     NoBringToFrontOnFocus —— 面板不该盖住游戏里的重要提示
            // ══════════════════════════════════════════════════════
            var flags = ImGuiWindowFlags.NoTitleBar
                      | ImGuiWindowFlags.NoBackground
                      | ImGuiWindowFlags.NoScrollbar
                      | ImGuiWindowFlags.NoScrollWithMouse
                      | ImGuiWindowFlags.NoCollapse
                      | ImGuiWindowFlags.NoBringToFrontOnFocus;

            if (折叠) flags |= ImGuiWindowFlags.NoResize;

            var 样式 = 主题.推样式();
            try
            {
                if (!ImGui.Begin(标识, flags))
                {
                    // 被折叠/裁剪时也要 End —— 这是 ImGui 的约定
                    ImGui.End();
                    return;
                }

                画底板();
                画标题栏();

                if (!折叠)
                {
                    ImGui.Dummy(new Vector2(0, 主题.行距));

                    // ★ 顶部常驻控件（启动 / 停手）★
                    //   放在页签**之前** —— 它不属于任何页签，
                    //   切页签时也要一直在。
                    if (顶部控件 != null)
                    {
                        try { 顶部控件(); } catch { }
                        主题.画分隔线();
                    }

                    if (页签.Count > 0) 画页签栏();
                    画内容();

                    记录尺寸();
                }

                画外框();
                ImGui.End();
            }
            finally
            {
                主题.弹样式(样式);
            }
        }
        catch (Exception e)
        {
            // ⚠️ UI 异常绝不能往上抛 —— 那会把整个 ACR 的绘制循环打断，
            //    表现是"面板突然消失、而且再也不出现"。
            LogHelper.Info("[小鲸鱼面板] 绘制异常（已忽略）：" + e.Message);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  自绘部分
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// **标题栏放得下的最小宽度** —— 左标题 + 右按钮，算实际文字宽度。
    ///
    /// ⚠️ 必须用 `CalcTextSize` 实测，不能拍一个固定值：
    ///     中文字宽随字体变，固定值在不同分辨率/字体下要么太宽（白占地方）
    ///     要么太窄（标题和按钮重叠）。
    ///
    /// 拿不到字体信息时（窗口还没建）返回一个保守的兜底值。
    /// </summary>
    private float 标题栏最小宽度()
    {
        try
        {
            var 缩放 = 主题.缩放();
            var 宽 = 主题.内边距 * 2f + ImGui.CalcTextSize(标题).X;

            // 右边的按钮（和 画标题栏 的排布保持一致）
            foreach (var (名, _) in 标题栏按钮)
                宽 += ImGui.CalcTextSize(名).X + 12f * 缩放 + 6f * 缩放;

            // 折叠按钮本身
            宽 += ImGui.CalcTextSize(折叠 ? "展开" : "折叠").X + 12f * 缩放 + 6f * 缩放;

            // 再留一点余量，免得刚好卡住
            宽 += 16f * 缩放;

            return 宽;
        }
        catch
        {
            return 240f;
        }
    }

    /// <summary>底板 —— 圆角矩形 + 顶部标题栏带 + 背景图</summary>
    private void 画底板()
    {
        try
        {
            var draw = ImGui.GetWindowDrawList();
            var 左上 = ImGui.GetWindowPos();
            var 右下 = 左上 + ImGui.GetWindowSize();

            // ① 底板（先铺，背景图叠在它上面）
            draw.AddRectFilled(左上, 右下, 转U32(主题.底板), 主题.圆角);

            // ② 背景图（透明 + 模糊的小鲸鱼立绘）
            //
            //  ⚠️ 顺序：底板 → 背景图 → 标题栏带
            //     背景图必须在底板上（否则被不透明的底板盖住 = 看不见），
            //     又必须在标题栏下面（否则标题文字会糊在图上）。
            //     `主题.画背景` 内部会自己判断开关和贴图有没有加载好。
            主题.画背景(左上, 右下);

            // ③ 顶部标题栏带
            //
            //  ⚠️ 这条带子**必须是不透明的**（alpha 0.95）——
            //     它的作用是"把背景图压在下面"，让标题文字永远清晰。
            //     如果跟着做成半透明，背景图的花纹会透上来干扰读标题。
            var 带高 = 主题.标题栏高;
            draw.AddRectFilled(左上,
                new Vector2(右下.X, 左上.Y + 带高),
                转U32(主题.标题栏),
                主题.圆角,
                ImDrawFlags.RoundCornersTop);
        }
        catch { }
    }

    /// <summary>标题栏 —— 标题 + 副标题 + 折叠按钮 + 自定义按钮</summary>
    private void 画标题栏()
    {
        try
        {
            var 缩放 = 主题.缩放();
            var draw = ImGui.GetWindowDrawList();
            var 左上 = ImGui.GetWindowPos();
            var 宽 = ImGui.GetWindowSize().X;
            var 高 = 主题.标题栏高;

            // ── 标题文字 ──
            var 文字高 = ImGui.GetTextLineHeight();
            var 文字Y = 左上.Y + (高 - 文字高) * 0.5f;

            draw.AddText(new Vector2(左上.X + 主题.内边距, 文字Y),
                         转U32(主题.文字强), 标题);

            // ── 副标题（跟在标题后面，弱色）──
            if (!string.IsNullOrWhiteSpace(副标题))
            {
                var 标题宽 = ImGui.CalcTextSize(标题).X;
                draw.AddText(new Vector2(左上.X + 主题.内边距 + 标题宽 + 10f * 缩放, 文字Y),
                             转U32(主题.文字弱), 副标题);
            }

            // ── 标题栏底部的强调线 ──
            draw.AddLine(new Vector2(左上.X, 左上.Y + 高 - 1f),
                         new Vector2(左上.X + 宽, 左上.Y + 高 - 1f),
                         转U32(主题.主色), 1.5f * 缩放);

            // ── 右侧按钮（从右往左排）──
            var x = 左上.X + 宽 - 主题.内边距;

            // 折叠按钮（最右）
            x = 画标题栏按钮(draw, x, 左上.Y, 高, 折叠 ? "展开" : "折叠",
                            () => 折叠 = !折叠);

            // 自定义按钮
            for (var i = 标题栏按钮.Count - 1; i >= 0; i--)
            {
                var (名, 点) = 标题栏按钮[i];
                x = 画标题栏按钮(draw, x, 左上.Y, 高, 名, 点);
            }

            // ⚠️ 标题栏是**自绘**的，没有占用 ImGui 的布局空间 ——
            //    所以要手动空出这些高度，否则内容会画到标题栏上面。
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 高);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 主题.内边距 - 8f);
        }
        catch { }
    }

    /// <summary>
    /// 在标题栏上画一个按钮（自绘 + 手动画命中区）。
    ///
    /// ⚠️ 不能用 `ImGui.Button` —— 它的位置由布局决定，
    ///     而标题栏是 `GetWindowPos()` 算出来的**绝对坐标**，两者对不上。
    ///     所以自己用 `InvisibleButton` 造命中区再自绘外观。
    /// </summary>
    /// <returns>按钮左边的 x（给下一个按钮接着排）</returns>
    private float 画标题栏按钮(ImDrawListPtr draw, float 右X, float 顶Y,
                              float 标题栏高, string 文本, Action 点击)
    {
        try
        {
            var 缩放 = 主题.缩放();
            var 文字宽 = ImGui.CalcTextSize(文本).X;
            var 内边 = 6f * 缩放;
            var 宽 = 文字宽 + 内边 * 2f;
            var 高 = 标题栏高 - 8f * 缩放;

            var 左 = 右X - 宽;
            var 上 = 顶Y + 4f * 缩放;

            // 命中区：用 ImGui 自己的不可见按钮，位置设成绝对坐标
            ImGui.SetCursorScreenPos(new Vector2(左, 上));
            var 命中 = ImGui.InvisibleButton("##hdr_" + 文本 + 标识 + 左, new Vector2(宽, 高));

            var 悬停 = ImGui.IsItemHovered();

            draw.AddRectFilled(new Vector2(左, 上), new Vector2(左 + 宽, 上 + 高),
                               转U32(悬停 ? 主题.主色 : 主题.主色暗), 主题.圆角);

            var 文字高 = ImGui.GetTextLineHeight();
            draw.AddText(new Vector2(左 + 内边, 上 + (高 - 文字高) * 0.5f),
                         转U32(主题.文字强), 文本);

            if (命中)
            {
                try { 点击(); } catch { }
            }

            return 左 - 6f * 缩放;
        }
        catch
        {
            return 右X;
        }
    }

    /// <summary>页签栏 —— 自绘下划线式页签（比系统的方块页签清爽）</summary>
    private void 画页签栏()
    {
        try
        {
            var 缩放 = 主题.缩放();
            var draw = ImGui.GetWindowDrawList();

            // 先量一遍宽度，才知道下划线画多长
            var 起点 = ImGui.GetCursorScreenPos();
            var x = 起点.X;
            var 高 = ImGui.GetTextLineHeight() + 10f * 缩放;

            for (var i = 0; i < 页签.Count; i++)
            {
                var 名 = 页签[i].名字;
                var 宽 = ImGui.CalcTextSize(名).X + 20f * 缩放;

                var 选中 = i == _当前页;

                // 命中区
                ImGui.SetCursorScreenPos(new Vector2(x, 起点.Y));
                ImGui.PushID("tab" + i + 标识);
                if (ImGui.InvisibleButton("##t", new Vector2(宽, 高))) _当前页 = i;
                var 悬停 = ImGui.IsItemHovered();
                ImGui.PopID();

                // 文字
                var 文字高 = ImGui.GetTextLineHeight();
                draw.AddText(new Vector2(x + 10f * 缩放, 起点.Y + 5f * 缩放),
                             转U32(选中 ? 主题.主色亮 : (悬停 ? 主题.文字 : 主题.文字弱)),
                             名);

                // ⚠️ 选中的用**下划线**而不是底色块 ——
                //    色块和背景的对比太强，一排页签会很吵。
                if (选中)
                {
                    draw.AddLine(new Vector2(x + 6f * 缩放, 起点.Y + 高 - 1f),
                                 new Vector2(x + 宽 - 6f * 缩放, 起点.Y + 高 - 1f),
                                 转U32(主题.主色), 2f * 缩放);
                }

                x += 宽;
            }

            // 页签栏下面一条很淡的分隔线
            var 宽总 = ImGui.GetWindowSize().X;
            draw.AddLine(new Vector2(起点.X, 起点.Y + 高),
                         new Vector2(起点.X + 宽总 - 主题.内边距 * 2f, 起点.Y + 高),
                         转U32(主题.分隔线), 1f);

            // 让出页签栏占的高度
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 高 + 主题.行距);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 主题.内边距 - 8f);
        }
        catch { }
    }

    /// <summary>内容区（可滚动）</summary>
    private void 画内容()
    {
        try
        {
            var 缩放 = 主题.缩放();

            ImGui.BeginChild("##内容" + 标识, new Vector2(0, 0), false,
                             ImGuiWindowFlags.AlwaysVerticalScrollbar);

            try
            {
                if (页签.Count > 0)
                {
                    var i = Math.Clamp(_当前页, 0, 页签.Count - 1);
                    try { 页签[i].画(); } catch (Exception e) { ImGui.TextColored(主题.危险, "页签绘制异常：" + e.Message); }
                }
            }
            finally
            {
                ImGui.EndChild();
            }
        }
        catch { }
    }

    /// <summary>外框描边 —— 最后画，压在所有内容之上（只占边缘 1.5px）</summary>
    private void 画外框()
    {
        try
        {
            var draw = ImGui.GetWindowDrawList();
            var 左上 = ImGui.GetWindowPos();
            var 右下 = 左上 + ImGui.GetWindowSize();

            draw.AddRect(左上, 右下, 转U32(主题.边框), 主题.圆角, ImDrawFlags.None, 主题.边框粗细);
        }
        catch { }
    }

    /// <summary>记住用户调过的尺寸（下次打开用同样的）</summary>
    private void 记录尺寸()
    {
        try
        {
            var s = ImGui.GetWindowSize();
            if (s.X > 100f && s.Y > 80f) 尺寸 = s;
        }
        catch { }
    }

    private static uint 转U32(Vector4 c)
    {
        try { return ImGui.ColorConvertFloat4ToU32(c); }
        catch { return 0xFFFFFFFF; }
    }
}
