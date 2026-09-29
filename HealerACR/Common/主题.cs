using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace HealerACR.Common;

/// <summary>
/// **小鲸鱼主题** —— 一套自绘 UI 的配色与样式常量。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 设计基调 ★
///    主体是**深海蓝**（小鲸鱼的蓝），配一点点青做高亮。
///    不用高饱和色 —— FF14 的战斗界面本身就很花，
///    面板再鲜艳会看不清内容。所以：**低饱和 + 低对比 + 细边框**，
///    远看是一块整洁的深色板，近看层次分明。
///
///  ── 为什么颜色写成常量而不是散在代码里 ──
///    散着写会出现"这个按钮的蓝和那个标题的蓝差一点点"，
///    而人眼对**同一界面里的色差**极其敏感 —— 一眼就觉得"粗糙"。
///    统一从这里取，改一处就是全局一致。
///
///  ⚠️ Alpha 值的分工（很关键，写错就糊成一片）：
///      · 底板      0.92 —— 几乎不透明（要能看清字）
///      · 卡片      0.55 —— 半透明（比底板亮一点点，分出层次）
///      · 边框      0.45 —— 只在边缘，不压内容
///      · 悬停/选中 0.25 —— 淡淡的，不要抢眼
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 主题
{
    // ── 主色（小鲸鱼蓝）───────────────────────────────

    /// <summary>主色 —— 鲸蓝。标题栏、选中态、强调线都用它</summary>
    public static readonly Vector4 主色 = new(0.29f, 0.62f, 0.86f, 1f);

    /// <summary>主色（暗）—— 标题栏底、悬停底色</summary>
    public static readonly Vector4 主色暗 = new(0.16f, 0.36f, 0.55f, 1f);

    /// <summary>主色（亮）—— 高亮文字、激活态</summary>
    public static readonly Vector4 主色亮 = new(0.55f, 0.80f, 0.95f, 1f);

    /// <summary>辅色（青）—— 次要强调，和主色拉开一点点</summary>
    public static readonly Vector4 辅色 = new(0.35f, 0.78f, 0.80f, 1f);

    // ── 底色 ─────────────────────────────────────────

    /// <summary>窗口底板 —— 深海蓝黑</summary>
    public static readonly Vector4 底板 = new(0.055f, 0.075f, 0.105f, 0.92f);

    /// <summary>卡片底 —— 比底板亮一档，用来分组</summary>
    public static readonly Vector4 卡片 = new(0.10f, 0.14f, 0.19f, 0.55f);

    /// <summary>卡片底（悬停）</summary>
    public static readonly Vector4 卡片悬停 = new(0.14f, 0.20f, 0.27f, 0.70f);

    /// <summary>标题栏底</summary>
    public static readonly Vector4 标题栏 = new(0.10f, 0.26f, 0.42f, 0.95f);

    // ── 描边 ─────────────────────────────────────────

    /// <summary>外框描边</summary>
    public static readonly Vector4 边框 = new(0.29f, 0.62f, 0.86f, 0.45f);

    /// <summary>内部分隔线（很淡）</summary>
    public static readonly Vector4 分隔线 = new(0.35f, 0.45f, 0.55f, 0.22f);

    // ── 文字 ─────────────────────────────────────────

    public static readonly Vector4 文字 = new(0.88f, 0.92f, 0.96f, 1f);
    public static readonly Vector4 文字弱 = new(0.58f, 0.66f, 0.74f, 1f);
    public static readonly Vector4 文字强 = new(1f, 1f, 1f, 1f);

    // ── 语义色（状态用，不参与主题）─────────────────

    public static readonly Vector4 成功 = new(0.42f, 0.80f, 0.55f, 1f);
    public static readonly Vector4 警告 = new(0.95f, 0.75f, 0.35f, 1f);
    public static readonly Vector4 危险 = new(0.92f, 0.45f, 0.45f, 1f);

    // ── 尺寸 ─────────────────────────────────────────
    //
    //  ⚠️ 全部是**基准值**，实际用的时候过 `缩放()` ——
    //     不然在 4K 分辨率下细得像头发丝。

    /// <summary>缩放基准（UI 全局缩放的兜底值）</summary>
    public const float 基准缩放 = 1f;

    public static float 圆角 => 4f * 缩放();
    public static float 内边距 => 10f * 缩放();
    public static float 行距 => 4f * 缩放();
    public static float 标题栏高 => 30f * 缩放();
    public static float 边框粗细 => 1.5f * 缩放();

    /// <summary>
    /// 全局缩放系数。默认 1，可在设置里调。
    ///
    /// ⚠️ 用它而不是 `ImGui.GetIO().FontGlobalScale` ——
    ///     那个会连字体一起放大（字会模糊），我们只缩放**布局尺寸**。
    /// </summary>
    public static float 用户缩放 = 1f;

    public static float 缩放() => 用户缩放 <= 0f ? 基准缩放 : 用户缩放;

    // ── 自绘辅助 ─────────────────────────────────────

    /// <summary>
    /// **画一张卡片** —— 在当前位置画一块圆角背景，高度由调用方给。
    ///
    /// 用法：
    /// <code>
    /// 主题.画卡片(高);
    /// // ... 在里面画内容 ...
    /// </code>
    ///
    /// ⚠️ 它只画背景，**不裁剪**内容 —— 内容超出卡片高度就会溢出来。
    ///     高度要自己算准（或者给足）。
    /// </summary>
    public static void 画卡片(float 高, Vector4? 底色 = null)
    {
        try
        {
            var pos = ImGui.GetCursorScreenPos();
            var 宽 = ImGui.GetContentRegionAvail().X;
            var 色 = 底色 ?? 卡片;

            var draw = ImGui.GetWindowDrawList();
            var 圆 = 圆角;

            draw.AddRectFilled(pos, new Vector2(pos.X + 宽, pos.Y + 高), 转U32(色), 圆);

            // ⚠️ 左边一道细的强调线 —— 这一点点细节让卡片"有归属感"，
            //    比四条边框都画要干净。
            draw.AddRectFilled(pos, new Vector2(pos.X + 2f * 缩放(), pos.Y + 高),
                               转U32(主色), 圆);

            ImGui.Dummy(new Vector2(0, 高));
        }
        catch { }
    }

    /// <summary>画一条分隔线</summary>
    public static void 画分隔线()
    {
        try
        {
            ImGui.Dummy(new Vector2(0, 行距 * 0.5f));
            var pos = ImGui.GetCursorScreenPos();
            var 宽 = ImGui.GetContentRegionAvail().X;
            ImGui.GetWindowDrawList().AddLine(
                pos, new Vector2(pos.X + 宽, pos.Y), 转U32(分隔线), 1f);
            ImGui.Dummy(new Vector2(0, 行距 * 0.5f));
        }
        catch { }
    }

    /// <summary>小节标题 —— 主色 + 左侧竖条</summary>
    public static void 小节标题(string 文本)
    {
        try
        {
            var pos = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            var 高 = ImGui.GetTextLineHeight();

            draw.AddRectFilled(pos, new Vector2(pos.X + 3f * 缩放(), pos.Y + 高 + 2f),
                               转U32(主色), 1.5f);

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f * 缩放());
            ImGui.TextColored(主色亮, 文本);
        }
        catch { }
    }

    /// <summary>把 Vector4 转成 ImGui 的 U32 颜色</summary>
    private static uint 转U32(Vector4 c)
    {
        try { return ImGui.ColorConvertFloat4ToU32(c); }
        catch { return 0xFFFFFFFF; }
    }

    /// <summary>
    /// **把整套配色推给 ImGui** —— 在 `Begin` 之后、画内容之前调。
    ///
    /// ⚠️ 必须配对 `ImGui.PopStyleColor(n)` / `PopStyleVar(m)` ——
    ///     推了不弹会把后面所有 ImGui 窗口（包括别的插件的）都染成我们的色。
    ///     所以返回值告诉你**推了几个**，照着弹。
    /// </summary>
    public static (int 颜色数, int 变量数) 推样式()
    {
        try
        {
            var c = 0;

            ImGui.PushStyleColor(ImGuiCol.WindowBg, 底板); c++;
            ImGui.PushStyleColor(ImGuiCol.ChildBg, 卡片); c++;
            ImGui.PushStyleColor(ImGuiCol.PopupBg, 底板); c++;
            ImGui.PushStyleColor(ImGuiCol.Border, 边框); c++;

            ImGui.PushStyleColor(ImGuiCol.Text, 文字); c++;
            ImGui.PushStyleColor(ImGuiCol.TextDisabled, 文字弱); c++;

            ImGui.PushStyleColor(ImGuiCol.FrameBg, 卡片); c++;
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, 卡片悬停); c++;
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, 主色暗); c++;

            ImGui.PushStyleColor(ImGuiCol.Button, 主色暗); c++;
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 主色); c++;
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, 主色亮); c++;

            ImGui.PushStyleColor(ImGuiCol.Header, 主色暗); c++;
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, 主色); c++;
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, 主色亮); c++;

            ImGui.PushStyleColor(ImGuiCol.CheckMark, 主色亮); c++;
            ImGui.PushStyleColor(ImGuiCol.SliderGrab, 主色); c++;
            ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, 主色亮); c++;

            ImGui.PushStyleColor(ImGuiCol.Separator, 分隔线); c++;
            ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, 底板); c++;
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, 主色暗); c++;
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, 主色); c++;
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, 主色亮); c++;

            ImGui.PushStyleColor(ImGuiCol.Tab, 卡片); c++;
            ImGui.PushStyleColor(ImGuiCol.TabHovered, 主色); c++;
            // ⚠️ 选中的页签用 `TabActive`（这个绑定里没有 `TabSelected`）
            ImGui.PushStyleColor(ImGuiCol.TabActive, 主色暗); c++;
            ImGui.PushStyleColor(ImGuiCol.TabUnfocused, 卡片); c++;
            ImGui.PushStyleColor(ImGuiCol.TabUnfocusedActive, 主色暗); c++;

            var v = 0;
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 圆角); v++;
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 圆角); v++;
            ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 圆角); v++;
            ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 圆角); v++;
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 边框粗细); v++;
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f); v++;
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f * 缩放(), 行距)); v++;
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(内边距, 内边距)); v++;

            return (c, v);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>弹掉 <see cref="推样式"/> 推上去的东西</summary>
    public static void 弹样式((int 颜色数, int 变量数) 句柄)
    {
        try
        {
            if (句柄.颜色数 > 0) ImGui.PopStyleColor(句柄.颜色数);
            if (句柄.变量数 > 0) ImGui.PopStyleVar(句柄.变量数);
        }
        catch { }
    }
}
