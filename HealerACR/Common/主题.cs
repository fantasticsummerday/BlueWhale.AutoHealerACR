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

    // ══════════════════════════════════════════════════════════════
    //  ★ 面板背景图（小鲸鱼立绘，透明 + 模糊）★
    // ══════════════════════════════════════════════════════════════

    /// <summary>背景图文件名（和 dll 放一起）</summary>
    public const string 背景文件名 = "WhaleBgBlur.png";

    /// <summary>背景透明度（0~1）。**要低** —— 它是背景，不能压过文字</summary>
    public static float 背景透明 = 0.30f;

    /// <summary>是否启用背景图</summary>
    public static bool 背景开关 = true;

    private static Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? _背景贴图;
    private static bool _背景试过了;

    /// <summary>
    /// **加载背景贴图**（只试一次）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么用"缩小再放大"当模糊 ★
    ///
    ///    ImGui 的 `AddImage` **没有模糊参数** —— 它只会把贴图按矩形拉伸。
    ///
    ///    真做高斯模糊的三个办法都不划算：
    ///      · 自己写 shader        —— 插件里拿不到 ImGui 的 shader 接口
    ///      · 每帧偏移叠加画 N 次  —— 20+ 次 draw call，每帧都跑
    ///      · 运行时算一遍像素     —— 550x550 要几十毫秒，会卡帧
    ///
    ///    ⇒ **把模糊在离线做掉**（`WhaleBgBlur.png` 是 40x40，2.5KB）：
    ///      40x40 放大到 600px 本身就是一次巨大的双线性插值 = 天然的模糊，
    ///      再叠上离线的高斯，边缘完全化开。
    ///      **每帧零计算，一次 draw call。**
    ///
    ///  ⚠️ 找不到文件时**静默跳过** —— 背景是装饰，
    ///     不该因为它缺了就让面板画不出来。但打一行日志，
    ///     免得用户以为"开关坏了"。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static void 确保背景()
    {
        if (_背景试过了) return;
        _背景试过了 = true;

        if (!背景开关) return;

        try
        {
            var 目录 = 找资源目录();
            if (目录 == null)
            {
                LogHelper.Info("[主题] 找不到插件目录，背景图跳过");
                return;
            }

            var 路径 = Path.Combine(目录, 背景文件名);
            if (!File.Exists(路径))
            {
                LogHelper.Info($"[主题] 没有背景图，用纯色底板（应有：{路径}）");
                return;
            }

            // ⚠️ 用 Dalamud 的贴图服务，不要自己解析 PNG ——
            //    解码、缓存、显存上传它都做好了。
            //    测试确认 `Core.Resolve<ITextureProvider>()` 能拿到。
            var 服务 = Core.Resolve<Dalamud.Plugin.Services.ITextureProvider>();
            if (服务 == null) return;

            var 共享 = 服务.GetFromFileAbsolute(路径);
            _背景贴图 = 共享?.GetWrapOrEmpty();

            LogHelper.Info("[主题] 背景图已加载：" + 背景文件名);
        }
        catch (Exception e)
        {
            LogHelper.Info("[主题] 背景图加载失败（用纯色底板）：" + e.Message);
        }
    }

    /// <summary>
    /// 找插件自己的目录 —— 多候选，逐个试。
    ///
    /// ⚠️ 不能只用 `AppContext.BaseDirectory`（这个坑在"地名"那里踩过）：
    ///    它在某些宿主下不指向插件目录，结果文件永远找不到，而且**一声不响**。
    /// </summary>
    private static string? 找资源目录()
    {
        try
        {
            var 程序集 = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrWhiteSpace(程序集))
            {
                var d = Path.GetDirectoryName(程序集);
                if (!string.IsNullOrWhiteSpace(d) && File.Exists(Path.Combine(d, 背景文件名)))
                    return d;
            }
        }
        catch { }

        try
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, 背景文件名)))
                return AppContext.BaseDirectory;
        }
        catch { }

        return null;
    }

    /// <summary>
    /// **画背景图** —— 铺满给定矩形，按"填充"缩放并居中裁切。
    ///
    /// 调用点：`小鲸鱼面板.画底板()`（在底板之上、内容之下）。
    ///
    /// ⚠️ 用"填充 + 居中"而不是"拉伸"：
    ///     拉伸会改宽高比，人脸会被拉扁 —— 一眼就难看。
    ///     填充是"保证铺满、多出来的裁掉"，永远不会变形。
    /// </summary>
    public static void 画背景(Vector2 左上, Vector2 右下)
    {
        if (!背景开关) return;

        try
        {
            确保背景();
            if (_背景贴图 == null) return;

            var 贴图宽 = (float)_背景贴图.Width;
            var 贴图高 = (float)_背景贴图.Height;
            if (贴图宽 <= 0 || 贴图高 <= 0) return;

            var 框宽 = 右下.X - 左上.X;
            var 框高 = 右下.Y - 左上.Y;
            if (框宽 <= 1f || 框高 <= 1f) return;

            // ── 填充：取较大的缩放比，保证两个方向都盖满 ──
            var 比 = Math.Max(框宽 / 贴图宽, 框高 / 贴图高);
            var 画宽 = 贴图宽 * 比;
            var 画高 = 贴图高 * 比;

            // ── 居中：多出来的部分平均分到两边（被窗口边缘裁掉）──
            var x0 = 左上.X - (画宽 - 框宽) * 0.5f;
            var y0 = 左上.Y - (画高 - 框高) * 0.5f;

            var 绘制 = ImGui.GetWindowDrawList();
            var 色 = new Vector4(1f, 1f, 1f, Math.Clamp(背景透明, 0f, 1f));

            // ⚠️ 用 `IDalamudTextureWrap.Handle` —— 它的类型**就是** `ImTextureID`，
            //    直接能传给 `AddImage`，不需要任何转换。
            //    （属性名不是 `ImGuiHandle`，那是另一个版本的叫法。）
            绘制.AddImage(
                _背景贴图.Handle,
                new Vector2(x0, y0),
                new Vector2(x0 + 画宽, y0 + 画高),
                Vector2.Zero, Vector2.One,
                转U32(色));
        }
        catch { }
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
