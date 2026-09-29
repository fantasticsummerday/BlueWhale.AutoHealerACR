using AEAssist;
using AEAssist.Helper;
using Dalamud.Bindings.ImGui;
using HealerACR.Common;
using HealerACR.Rotations;

namespace BlueWhale.AutoHealerACR;

// ============================================================================
//  BlueWhale —— AI 接管实验版
//
//  **设计要点：AI 是增强层，不是必需品。**
//
//    · 直接继承 HealerACR 的职业入口 → 技能逻辑 / timeline / 设置界面全部继承
//    · AI 未配置 或 失效 → 完全等同于 HealerACR（原有逻辑照跑）
//    · AI 可用 → 在原有逻辑之上叠加"阈值调整"（阶段 A）和"技能建议"（阶段 B）
//
//  这样才能保证：**API 挂了、没填 Key、断网了，这个 ACR 依然是个能用的治疗 ACR。**
// ============================================================================

/// <summary>白魔 —— AI 接管实验版</summary>
public class BlueWhaleWhiteMageEntry : WHMRotationEntry
{
    /// <summary>
    /// ★ 构造函数里改 AuthorName ★
    ///
    /// ACR 选择列表里显示的是 **AuthorName**，不是 OverlayTitle ——
    /// 我一开始改错了地方。（AuthorName 在基类里不是 virtual，只能构造时赋。）
    /// </summary>
    public BlueWhaleWhiteMageEntry()
    {
        AuthorName = "小鲸鱼统治世界";   // ⚠️ 不能带职业 —— AEAssist 拿它当设置目录名
    }
    public override string OverlayTitle => "小鲸鱼统治世界 · 白魔";
    public override string Description => "BlueWhale.AutoHealerACR — 实验性项目：让 AI 接管治疗输出决策";

    /// <summary>
    /// ★ 必须 override ★
    /// 基类的 OnDrawSetting 只画 HealerACR 的设置，
    /// 不调 base 就丢原版界面，不调 AiSettingPage 就没有 AI 界面 —— 两个都要。
    /// </summary>
    public override void OnDrawSetting()
    {
        base.OnDrawSetting();     // 原版全部设置（治疗/输出/资源/时间轴…）
        AiSettingPage.画();        // 叠加 AI 部分
    }

    /// <summary>
    /// ★ 挂 AI 钩子 ★
    /// 不 override 这里的话，钩子永远不会挂上 —— AI 就只是"显示"，不影响实际治疗。
    /// （这个坑我在 AiSettingPage 上已经踩过一次了。）
    /// </summary>
    public override Rotation Build(string settingFolder)
    {
        var rot = base.Build(settingFolder);   // 原版全部构建流程（队列/事件/起手）

        // AcrType 枚举：Both=日常&高难 / Normal=日常 / HighEnd=高难 / PVP
        // 基类默认是 Both，这里覆盖成 Normal —— 列表里只显示「日常」
        rot.AcrType = AcrType.Normal;

        AiSettings.初始化(settingFolder);        // ★ 先告诉 AiSettings 设置存哪 ★

        // 挂上记忆采集钩子（原版不挂 → 什么也不发生）
        HealerACR.Common.记忆钩子.记决策 = (id, 名) =>
            战斗记忆.记决策(id, 名, 战斗记忆.判来源(id));
        HealerACR.Common.记忆钩子.每帧 = 战斗记忆.每帧更新;

        // ★ 挂上状态重置钩子 ★
        //   换本时把 AI 层的状态清掉 —— 否则上个副本的判断会带过来
        //   （比如上个本一直打小怪判"激进"，进 Boss 本还是激进）。
        HealerACR.Common.状态重置钩子.重置 = () =>
        {
            AiStrategyLayer.重置();
            AiDecisionLayer.重置();
            AiThresholdAdapter.重置平滑();
            Ai调试.日志("换本 → 已重置倾向 / 建议队列 / 阈值平滑");
        };

        // ★ 启动 AI 初始化 ★
        //   加载后立刻跑一次完整请求，让 AI 先把局面过一遍 ——
        //   否则前 10 秒它是"哑"的（倾向=未知，阈值偏移=0）。
        Ai初始化.开始();

        // 把「解除熔断」这个 QT 开关接到实际动作上
        // （用户能在 QT 控制台给它绑快捷键 → 按一下就解除熔断）
        HealerACR.Common.HealQt.解除熔断请求 = () =>
        {
            DeepSeekClient.解除熔断();
            Ai初始化.重置();
            Ai调试.日志("手动解除熔断");
            屏幕提示.成功("AI 熔断已手动解除", "ai-manual-unfuse");
        };

        AiHookInstaller.挂载();                  // 挂上 AI 阈值钩子

        return rot;
    }

    public override void Dispose()
    {
        AiHookInstaller.卸载();                  // 卸载，避免影响其他 ACR
        HealerACR.Common.记忆钩子.卸载();
        HealerACR.Common.状态重置钩子.卸载();
        base.Dispose();
    }

    /// <summary>
    /// ★ 把 AI 建议插到队列最前面 ★
    ///
    /// **注意是 Insert(0) 而不是 Add** —— AI 建议要最高优先级才有意义，
    /// 否则它永远排在所有 resolver 后面，等于没用。
    ///
    /// 但"最高优先级"≠"一定执行"：
    /// AiSuggestionResolver 内部还会过一遍可用性判断，不过就放行给原队列。
    /// </summary>
    protected override List<AEAssist.CombatRoutine.Module.SlotResolverData> 构建决策队列()
    {
        var 队列 = base.构建决策队列();

        // ★ 心跳必须在队列里 —— 没有它 AI 层永远不刷新 ★
        队列.Insert(0, new AEAssist.CombatRoutine.Module.SlotResolverData(
            new AiHeartbeat(),
            AEAssist.CombatRoutine.Module.SlotMode.Always));

        队列.Insert(0, new AEAssist.CombatRoutine.Module.SlotResolverData(
            new AiSuggestionResolver(),
            AEAssist.CombatRoutine.Module.SlotMode.Always));

        return 队列;
    }
}

/// <summary>学者 —— AI 接管实验版</summary>
public class BlueWhaleScholarEntry : SCHRotationEntry
{
    /// <summary>
    /// ★ 构造函数里改 AuthorName ★
    ///
    /// ACR 选择列表里显示的是 **AuthorName**，不是 OverlayTitle ——
    /// 我一开始改错了地方。（AuthorName 在基类里不是 virtual，只能构造时赋。）
    /// </summary>
    public BlueWhaleScholarEntry()
    {
        AuthorName = "小鲸鱼统治世界";   // ⚠️ 不能带职业 —— AEAssist 拿它当设置目录名
    }
    public override string OverlayTitle => "小鲸鱼统治世界 · 学者";
    public override string Description => "BlueWhale.AutoHealerACR — 实验性项目：让 AI 接管治疗输出决策";

    /// <summary>
    /// ★ 必须 override ★
    /// 基类的 OnDrawSetting 只画 HealerACR 的设置，
    /// 不调 base 就丢原版界面，不调 AiSettingPage 就没有 AI 界面 —— 两个都要。
    /// </summary>
    public override void OnDrawSetting()
    {
        base.OnDrawSetting();     // 原版全部设置（治疗/输出/资源/时间轴…）
        AiSettingPage.画();        // 叠加 AI 部分
    }

    /// <summary>
    /// ★ 挂 AI 钩子 ★
    /// 不 override 这里的话，钩子永远不会挂上 —— AI 就只是"显示"，不影响实际治疗。
    /// （这个坑我在 AiSettingPage 上已经踩过一次了。）
    /// </summary>
    public override Rotation Build(string settingFolder)
    {
        var rot = base.Build(settingFolder);   // 原版全部构建流程（队列/事件/起手）

        // AcrType 枚举：Both=日常&高难 / Normal=日常 / HighEnd=高难 / PVP
        // 基类默认是 Both，这里覆盖成 Normal —— 列表里只显示「日常」
        rot.AcrType = AcrType.Normal;

        AiSettings.初始化(settingFolder);        // ★ 先告诉 AiSettings 设置存哪 ★

        // 挂上记忆采集钩子（原版不挂 → 什么也不发生）
        HealerACR.Common.记忆钩子.记决策 = (id, 名) =>
            战斗记忆.记决策(id, 名, 战斗记忆.判来源(id));
        HealerACR.Common.记忆钩子.每帧 = 战斗记忆.每帧更新;

        // ★ 挂上状态重置钩子 ★
        //   换本时把 AI 层的状态清掉 —— 否则上个副本的判断会带过来
        //   （比如上个本一直打小怪判"激进"，进 Boss 本还是激进）。
        HealerACR.Common.状态重置钩子.重置 = () =>
        {
            AiStrategyLayer.重置();
            AiDecisionLayer.重置();
            AiThresholdAdapter.重置平滑();
            Ai调试.日志("换本 → 已重置倾向 / 建议队列 / 阈值平滑");
        };

        // ★ 启动 AI 初始化 ★
        //   加载后立刻跑一次完整请求，让 AI 先把局面过一遍 ——
        //   否则前 10 秒它是"哑"的（倾向=未知，阈值偏移=0）。
        Ai初始化.开始();

        // 把「解除熔断」这个 QT 开关接到实际动作上
        // （用户能在 QT 控制台给它绑快捷键 → 按一下就解除熔断）
        HealerACR.Common.HealQt.解除熔断请求 = () =>
        {
            DeepSeekClient.解除熔断();
            Ai初始化.重置();
            Ai调试.日志("手动解除熔断");
            屏幕提示.成功("AI 熔断已手动解除", "ai-manual-unfuse");
        };

        AiHookInstaller.挂载();                  // 挂上 AI 阈值钩子

        return rot;
    }

    public override void Dispose()
    {
        AiHookInstaller.卸载();                  // 卸载，避免影响其他 ACR
        HealerACR.Common.记忆钩子.卸载();
        HealerACR.Common.状态重置钩子.卸载();
        base.Dispose();
    }

    /// <summary>
    /// ★ 把 AI 建议插到队列最前面 ★
    ///
    /// **注意是 Insert(0) 而不是 Add** —— AI 建议要最高优先级才有意义，
    /// 否则它永远排在所有 resolver 后面，等于没用。
    ///
    /// 但"最高优先级"≠"一定执行"：
    /// AiSuggestionResolver 内部还会过一遍可用性判断，不过就放行给原队列。
    /// </summary>
    protected override List<AEAssist.CombatRoutine.Module.SlotResolverData> 构建决策队列()
    {
        var 队列 = base.构建决策队列();

        // ★ 心跳必须在队列里 —— 没有它 AI 层永远不刷新 ★
        队列.Insert(0, new AEAssist.CombatRoutine.Module.SlotResolverData(
            new AiHeartbeat(),
            AEAssist.CombatRoutine.Module.SlotMode.Always));

        队列.Insert(0, new AEAssist.CombatRoutine.Module.SlotResolverData(
            new AiSuggestionResolver(),
            AEAssist.CombatRoutine.Module.SlotMode.Always));

        return 队列;
    }
}

/// <summary>占星 —— AI 接管实验版</summary>
public class BlueWhaleAstrologianEntry : ASTRotationEntry
{
    /// <summary>
    /// ★ 构造函数里改 AuthorName ★
    ///
    /// ACR 选择列表里显示的是 **AuthorName**，不是 OverlayTitle ——
    /// 我一开始改错了地方。（AuthorName 在基类里不是 virtual，只能构造时赋。）
    /// </summary>
    public BlueWhaleAstrologianEntry()
    {
        AuthorName = "小鲸鱼统治世界";   // ⚠️ 不能带职业 —— AEAssist 拿它当设置目录名
    }
    public override string OverlayTitle => "小鲸鱼统治世界 · 占星";
    public override string Description => "BlueWhale.AutoHealerACR — 实验性项目：让 AI 接管治疗输出决策";

    /// <summary>
    /// ★ 必须 override ★
    /// 基类的 OnDrawSetting 只画 HealerACR 的设置，
    /// 不调 base 就丢原版界面，不调 AiSettingPage 就没有 AI 界面 —— 两个都要。
    /// </summary>
    public override void OnDrawSetting()
    {
        base.OnDrawSetting();     // 原版全部设置（治疗/输出/资源/时间轴…）
        AiSettingPage.画();        // 叠加 AI 部分
    }

    /// <summary>
    /// ★ 挂 AI 钩子 ★
    /// 不 override 这里的话，钩子永远不会挂上 —— AI 就只是"显示"，不影响实际治疗。
    /// （这个坑我在 AiSettingPage 上已经踩过一次了。）
    /// </summary>
    public override Rotation Build(string settingFolder)
    {
        var rot = base.Build(settingFolder);   // 原版全部构建流程（队列/事件/起手）

        // AcrType 枚举：Both=日常&高难 / Normal=日常 / HighEnd=高难 / PVP
        // 基类默认是 Both，这里覆盖成 Normal —— 列表里只显示「日常」
        rot.AcrType = AcrType.Normal;

        AiSettings.初始化(settingFolder);        // ★ 先告诉 AiSettings 设置存哪 ★

        // 挂上记忆采集钩子（原版不挂 → 什么也不发生）
        HealerACR.Common.记忆钩子.记决策 = (id, 名) =>
            战斗记忆.记决策(id, 名, 战斗记忆.判来源(id));
        HealerACR.Common.记忆钩子.每帧 = 战斗记忆.每帧更新;

        // ★ 挂上状态重置钩子 ★
        //   换本时把 AI 层的状态清掉 —— 否则上个副本的判断会带过来
        //   （比如上个本一直打小怪判"激进"，进 Boss 本还是激进）。
        HealerACR.Common.状态重置钩子.重置 = () =>
        {
            AiStrategyLayer.重置();
            AiDecisionLayer.重置();
            AiThresholdAdapter.重置平滑();
            Ai调试.日志("换本 → 已重置倾向 / 建议队列 / 阈值平滑");
        };

        // ★ 启动 AI 初始化 ★
        //   加载后立刻跑一次完整请求，让 AI 先把局面过一遍 ——
        //   否则前 10 秒它是"哑"的（倾向=未知，阈值偏移=0）。
        Ai初始化.开始();

        // 把「解除熔断」这个 QT 开关接到实际动作上
        // （用户能在 QT 控制台给它绑快捷键 → 按一下就解除熔断）
        HealerACR.Common.HealQt.解除熔断请求 = () =>
        {
            DeepSeekClient.解除熔断();
            Ai初始化.重置();
            Ai调试.日志("手动解除熔断");
            屏幕提示.成功("AI 熔断已手动解除", "ai-manual-unfuse");
        };

        AiHookInstaller.挂载();                  // 挂上 AI 阈值钩子

        return rot;
    }

    public override void Dispose()
    {
        AiHookInstaller.卸载();                  // 卸载，避免影响其他 ACR
        HealerACR.Common.记忆钩子.卸载();
        HealerACR.Common.状态重置钩子.卸载();
        base.Dispose();
    }

    /// <summary>
    /// ★ 把 AI 建议插到队列最前面 ★
    ///
    /// **注意是 Insert(0) 而不是 Add** —— AI 建议要最高优先级才有意义，
    /// 否则它永远排在所有 resolver 后面，等于没用。
    ///
    /// 但"最高优先级"≠"一定执行"：
    /// AiSuggestionResolver 内部还会过一遍可用性判断，不过就放行给原队列。
    /// </summary>
    protected override List<AEAssist.CombatRoutine.Module.SlotResolverData> 构建决策队列()
    {
        var 队列 = base.构建决策队列();

        // ★ 心跳必须在队列里 —— 没有它 AI 层永远不刷新 ★
        队列.Insert(0, new AEAssist.CombatRoutine.Module.SlotResolverData(
            new AiHeartbeat(),
            AEAssist.CombatRoutine.Module.SlotMode.Always));

        队列.Insert(0, new AEAssist.CombatRoutine.Module.SlotResolverData(
            new AiSuggestionResolver(),
            AEAssist.CombatRoutine.Module.SlotMode.Always));

        return 队列;
    }
}

/// <summary>贤者 —— AI 接管实验版</summary>
public class BlueWhaleSageEntry : SGERotationEntry
{
    /// <summary>
    /// ★ 构造函数里改 AuthorName ★
    ///
    /// ACR 选择列表里显示的是 **AuthorName**，不是 OverlayTitle ——
    /// 我一开始改错了地方。（AuthorName 在基类里不是 virtual，只能构造时赋。）
    /// </summary>
    public BlueWhaleSageEntry()
    {
        AuthorName = "小鲸鱼统治世界";   // ⚠️ 不能带职业 —— AEAssist 拿它当设置目录名
    }
    public override string OverlayTitle => "小鲸鱼统治世界 · 贤者";
    public override string Description => "BlueWhale.AutoHealerACR — 实验性项目：让 AI 接管治疗输出决策";

    /// <summary>
    /// ★ 必须 override ★
    /// 基类的 OnDrawSetting 只画 HealerACR 的设置，
    /// 不调 base 就丢原版界面，不调 AiSettingPage 就没有 AI 界面 —— 两个都要。
    /// </summary>
    public override void OnDrawSetting()
    {
        base.OnDrawSetting();     // 原版全部设置（治疗/输出/资源/时间轴…）
        AiSettingPage.画();        // 叠加 AI 部分
    }

    /// <summary>
    /// ★ 挂 AI 钩子 ★
    /// 不 override 这里的话，钩子永远不会挂上 —— AI 就只是"显示"，不影响实际治疗。
    /// （这个坑我在 AiSettingPage 上已经踩过一次了。）
    /// </summary>
    public override Rotation Build(string settingFolder)
    {
        var rot = base.Build(settingFolder);   // 原版全部构建流程（队列/事件/起手）

        // AcrType 枚举：Both=日常&高难 / Normal=日常 / HighEnd=高难 / PVP
        // 基类默认是 Both，这里覆盖成 Normal —— 列表里只显示「日常」
        rot.AcrType = AcrType.Normal;

        AiSettings.初始化(settingFolder);        // ★ 先告诉 AiSettings 设置存哪 ★

        // 挂上记忆采集钩子（原版不挂 → 什么也不发生）
        HealerACR.Common.记忆钩子.记决策 = (id, 名) =>
            战斗记忆.记决策(id, 名, 战斗记忆.判来源(id));
        HealerACR.Common.记忆钩子.每帧 = 战斗记忆.每帧更新;

        // ★ 挂上状态重置钩子 ★
        //   换本时把 AI 层的状态清掉 —— 否则上个副本的判断会带过来
        //   （比如上个本一直打小怪判"激进"，进 Boss 本还是激进）。
        HealerACR.Common.状态重置钩子.重置 = () =>
        {
            AiStrategyLayer.重置();
            AiDecisionLayer.重置();
            AiThresholdAdapter.重置平滑();
            Ai调试.日志("换本 → 已重置倾向 / 建议队列 / 阈值平滑");
        };

        // ★ 启动 AI 初始化 ★
        //   加载后立刻跑一次完整请求，让 AI 先把局面过一遍 ——
        //   否则前 10 秒它是"哑"的（倾向=未知，阈值偏移=0）。
        Ai初始化.开始();

        // 把「解除熔断」这个 QT 开关接到实际动作上
        // （用户能在 QT 控制台给它绑快捷键 → 按一下就解除熔断）
        HealerACR.Common.HealQt.解除熔断请求 = () =>
        {
            DeepSeekClient.解除熔断();
            Ai初始化.重置();
            Ai调试.日志("手动解除熔断");
            屏幕提示.成功("AI 熔断已手动解除", "ai-manual-unfuse");
        };

        AiHookInstaller.挂载();                  // 挂上 AI 阈值钩子

        return rot;
    }

    public override void Dispose()
    {
        AiHookInstaller.卸载();                  // 卸载，避免影响其他 ACR
        HealerACR.Common.记忆钩子.卸载();
        HealerACR.Common.状态重置钩子.卸载();
        base.Dispose();
    }

    /// <summary>
    /// ★ 把 AI 建议插到队列最前面 ★
    ///
    /// **注意是 Insert(0) 而不是 Add** —— AI 建议要最高优先级才有意义，
    /// 否则它永远排在所有 resolver 后面，等于没用。
    ///
    /// 但"最高优先级"≠"一定执行"：
    /// AiSuggestionResolver 内部还会过一遍可用性判断，不过就放行给原队列。
    /// </summary>
    protected override List<AEAssist.CombatRoutine.Module.SlotResolverData> 构建决策队列()
    {
        var 队列 = base.构建决策队列();

        // ★ 心跳必须在队列里 —— 没有它 AI 层永远不刷新 ★
        队列.Insert(0, new AEAssist.CombatRoutine.Module.SlotResolverData(
            new AiHeartbeat(),
            AEAssist.CombatRoutine.Module.SlotMode.Always));

        队列.Insert(0, new AEAssist.CombatRoutine.Module.SlotResolverData(
            new AiSuggestionResolver(),
            AEAssist.CombatRoutine.Module.SlotMode.Always));

        return 队列;
    }
}

// ============================================================================
//  AI 设置界面（挂在 HealerACR 的设置页后面）
// ============================================================================

/// <summary>
/// AI 设置页。**故意做成独立的静态类** —— 不碰 HealerACR 的入口继承链，
/// 只在需要的地方调用一次 <see cref="画"/>。
/// </summary>
public static class AiSettingPage
{
    // ---- 测试连接的异步结果（要显示在界面上，不能只打日志）----
    private static volatile string _测试结果 = "";
    private static volatile bool _测试中;

    /// <summary>每帧调用（目前只占位，结果由后台任务写入）</summary>
    public static void 每帧更新() { }

    private static volatile string _保存提示 = "";

    /// <summary>
    /// 保存设置并给出反馈。
    ///
    /// **为什么不靠手动点「保存设置」**：
    ///   用户填完 Key 就直接切走了，很自然不会再去点按钮，
    ///   结果重载后 Key 没了 —— 这个坑我自己也踩过。
    /// </summary>
    private static void 保存并提示(string 项)
    {
        try
        {
            AiSettings.保存();
            var 路径 = AiSettings.当前路径();
            _保存提示 = $"已保存（{项}）→ {路径}";
        }
        catch (Exception e)
        {
            _保存提示 = $"保存失败：{e.Message}";
        }
    }
    public static void 画()
    {
        var s = AiSettings.Instance;

        ImGui.Separator();
        ImGui.TextDisabled("════ BlueWhale AI 决策层（可选，不填也完全可用）════");

        // ---- API Key ----
        var key = s.ApiKey ?? "";
        ImGui.SetNextItemWidth(380);
        if (ImGui.InputText("DeepSeek API Key", ref key, 200)) s.ApiKey = key;
        // ★ 失焦即保存 ★ —— 不用再手动点「保存设置」，重载后 Key 还在
        if (ImGui.IsItemDeactivatedAfterEdit()) 保存并提示("API Key");
        ImGui.TextDisabled("  sk- 开头。留空 = 不启用 AI，走原版逻辑。只存本地 json。");

        // ---- 只有填了 Key 才显示后面的 ----
        // （按你的要求：提供了 api key 再选择模型）
        if (!s.已配置)
        {
            ImGui.TextDisabled("  ↑ 填入 Key 后，下面会出现模型选择和开关。");
            return;
        }

        // ---- 模型选择 ----
        ImGui.Separator();
        ImGui.TextDisabled("模型（点一下切换，也可以直接手填）");

        // 预设：当前 DeepSeek 的模型是 flash / pro
        // ⚠️ 模型名会随版本变（之前是 chat / reasoner），所以下面保留了自定义输入框
        var 模型列表 = new[] { "deepseek-flash", "deepseek-pro" };

        for (var i = 0; i < 模型列表.Length; i++)
        {
            var 选中 = s.Model == 模型列表[i];
            if (ImGui.RadioButton(模型列表[i], 选中)) { s.Model = 模型列表[i]; AiSettings.保存(); }
            ImGui.SameLine();
        }

        ImGui.NewLine();
        ImGui.TextDisabled("  deepseek-flash = 快、便宜，适合高频决策（推荐先用这个）");
        ImGui.TextDisabled("  deepseek-pro   = 慢、贵、推理更强，适合低频策略");

        // 自定义（模型名会变，留个口子）
        var 模型名 = s.Model ?? "";
        ImGui.SetNextItemWidth(240);
        if (ImGui.InputText("模型名（可手填）", ref 模型名, 64)) s.Model = 模型名;
        if (ImGui.IsItemDeactivatedAfterEdit()) 保存并提示("模型名");
        ImGui.TextDisabled("  模型名随版本会变。如果上面两个都不通，去 DeepSeek 文档查当前名称填这里。");

        ImGui.SetNextItemWidth(200);
        ImGui.InputText("接口地址", ref s.Endpoint, 200);
        if (ImGui.IsItemDeactivatedAfterEdit()) 保存并提示("接口地址");
        ImGui.TextDisabled("  默认 https://api.deepseek.com");

        ImGui.SetNextItemWidth(160);
        ImGui.SliderInt("超时（毫秒）", ref s.超时毫秒, 500, 10000);
        if (ImGui.IsItemDeactivatedAfterEdit()) 保存并提示("超时");

        if (ImGui.Button("保存设置")) { AiSettings.保存(); LogHelper.Info("[BlueWhale.AI] 设置已保存"); }

        // ---- 两个阶段 ----
        ImGui.Separator();
        ImGui.TextDisabled("决策层开关");

        if (ImGui.Checkbox("阶段 A：策略层（调阈值，低频）", ref s.启用策略层)) 保存并提示("阶段A开关");
        ImGui.TextDisabled("  AI 判断『这波该保守还是该激进』，然后微调治疗阈值");

        if (ImGui.Checkbox("阶段 B：决策层（出技能建议，高频）", ref s.启用决策层)) 保存并提示("阶段B开关");
        ImGui.TextDisabled("  ⚠️ 对延迟敏感（GCD 只有 2.5 秒），建议先只开阶段 A");

        ImGui.SetNextItemWidth(160);
        ImGui.SliderInt("策略刷新（秒）", ref s.策略刷新秒, 3, 60);
        ImGui.SetNextItemWidth(160);
        ImGui.SliderInt("决策预取（毫秒）", ref s.决策预取毫秒, 500, 3000);

        // ---- 安全阀 ----
        ImGui.Separator();
        ImGui.TextDisabled("安全阀（AI 失败绝不影响战斗）");

        ImGui.SetNextItemWidth(160);
        ImGui.SliderInt("连续失败上限", ref s.连续失败上限, 1, 10);
        ImGui.SetNextItemWidth(160);
        ImGui.SliderInt("失败冷却（秒）", ref s.失败冷却秒, 10, 300);

        ImGui.Checkbox("记录原始回复（调试）", ref s.记录原始回复);

        // ★ 调试模式 ★ —— 开启后 AI 日志直接显示在游戏里
        if (ImGui.Checkbox("调试模式（AI 日志上屏）", ref s.调试模式))
        {
            AiSettings.保存();
            if (s.调试模式) 屏幕提示.成功("调试模式已开启 —— AI 日志会直接显示在屏幕上", "dbg-on");
            else 屏幕提示.提示("🐋 小鲸鱼", "调试模式已关闭", "dbg-off");
        }
        ImGui.TextDisabled("  开启后 AI 的请求/回复/采纳都会打到屏幕上（日志文件照常写）");

        if (ImGui.Button("解除熔断")) DeepSeekClient.解除熔断();

        ImGui.SameLine();

        // ★ 测试连接 —— 最直接的"AI 到底通不通"判断方式 ★
        if (ImGui.Button("测试连接"))
        {
            LogHelper.Info("[BlueWhale.AI] 开始测试连接…");
            _测试中 = true;
            _测试结果 = "测试中…（最多等 10 秒）";

            _ = Task.Run(async () =>
            {
                var r = await DeepSeekClient.提问(
                    "这是一个连接测试。只回复两个字：正常",
                    "回复：正常");

                if (r != null)
                {
                    LogHelper.Info($"[BlueWhale.AI] ✅ 连接测试成功！AI 回复：{r}");
                    LogHelper.Info("[BlueWhale.AI] 说明：Key、地址、模型都正常，可以开阶段 A 了。");
                    // 不去解析 AI 回了什么 —— 只要能拿到非空回复，
                    // 就说明 Key / 地址 / 模型 这一整条链路是通的。
                    _测试结果 = "✅ 连接正常";
                    _测试中 = false;
                }
                else
                {
                    LogHelper.Error("[BlueWhale.AI] ❌ 连接测试失败。常见原因：");
                    LogHelper.Error("    · Key 填错或已失效（检查 sk- 开头、有没有多余空格）");
                    LogHelper.Error("    · 网络不通 / 需要代理");
                    LogHelper.Error("    · 账户余额不足");
                    LogHelper.Error("    · 模型名写错（应该是 deepseek-chat 或 deepseek-reasoner）");
                    LogHelper.Error("[BlueWhale.AI] 具体错误看上面那行『请求失败（...）』的原因。");
                    _测试结果 = "❌ 连接失败 —— 看日志最后几行（Key / 网络 / 余额 / 模型名）";
                    _测试中 = false;
                }
            });
        }

        // ---- 实时状态 ----
        ImGui.Separator();
        ImGui.Text("AI 当前状态");
        ImGui.Text($"  熔断中：{(DeepSeekClient.熔断中 ? "是 ⚠️" : "否")}    连续失败：{DeepSeekClient.连续失败数}");

        // ── 最近的 AI 请求历史（对照你提的"多展示几条"）──
        if (AiStrategyLayer.历史.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextDisabled("最近几次 AI 请求");

            foreach (var 行 in AiStrategyLayer.历史)
            {
                ImGui.TextDisabled("  " + 行);
            }
        }

        if (_保存提示.Length > 0)
        {
            ImGui.TextDisabled("  " + _保存提示);
        }

        if (_测试中 || _测试结果.Length > 0)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.9f, 0.3f, 1f), "  " + _测试结果);
        }
        ImGui.TextWrapped("  " + AiThresholdAdapter.状态描述());

        ImGui.TextDisabled($"  策略层：启用={s.启用策略层}  刷新中={AiStrategyLayer.刷新中}  成功={AiStrategyLayer.成功次数} 次");
        ImGui.TextDisabled($"  上次结果：{AiStrategyLayer.上次结果}");

        var 建议 = AiDecisionLayer.当前建议;
        if (建议 != null)
        {
            ImGui.Text($"  决策建议：技能 {建议.技能Id}（{建议.理由}）");
        }
        else
        {
            ImGui.TextDisabled($"  决策建议：无（命中 {AiDecisionLayer.命中次数} / 过期 {AiDecisionLayer.过期次数}）");
        }
    }
}

// ============================================================================
//  AI 心跳 —— 每帧驱动 AI 层
// ============================================================================

/// <summary>
/// AI 心跳 —— **这是 AI 的发动机。**
///
/// ⚠️ 我在 0.3.0 重构入口时把它连同旧文件一起删掉了，
///    结果策略层永远不刷新（面板一直显示"还没请求过"）。
///    **没有它，AI 层就是死的。**
///
/// 它放在 Always 队列，**永远返回 -1**（不占 slot、不影响任何技能释放），
/// 唯一作用就是每帧调一次两个层的「每帧更新」。
/// </summary>
public class AiHeartbeat : ISlotResolver
{
    public int Check()
    {
        try
        {
            AiStrategyLayer.每帧更新();   // 阶段 A
            AiDecisionLayer.每帧更新();   // 阶段 B
            AiSettingPage.每帧更新();     // 收集"测试连接"的异步结果
            AiSettings.每帧更新();        // 脱战后补写攒下的设置保存（避免战斗中做 IO）

            Ai初始化.每帧更新();          // 初始化超时检查
            Ai初始化.检查熔断提示();      // 熔断状态变化 → 屏幕横幅告知
            HealerACR.Common.HealQt.每帧更新();   // 一次性开关（解除熔断）

            // 以太管理：通过观察以太数量变化检测"用掉了豆子"
            HealerACR.Common.以太管理.每帧更新();

            // 战斗记忆采集（阶段 1：只写不读，不影响任何决策）
            战斗记忆.每帧更新();
        }
        catch (Exception e)
        {
            LogHelper.Info("[BlueWhale.AI] 心跳异常（已忽略）：" + e.Message);
        }

        return -1;   // 永不触发 —— 只做驱动
    }

    public void Build(Slot slot)
    {
        // 空实现
    }

// ============================================================================
//  幻术师入口（白魔的前置职业）
// ============================================================================

/// <summary>
/// 幻术师（Conjurer）—— 白魔的前置职业，等级上限 50。
///
/// 直接继承 <see cref="HealerACR.Rotations.幻术师RotationEntry"/>，
/// 所以白魔那套治疗/输出逻辑、AI 双阶段、记忆采集全部自动获得。
/// </summary>
public class BlueWhale幻术师Entry : HealerACR.Rotations.幻术师RotationEntry
{
    public BlueWhale幻术师Entry()
    {
        AuthorName = "小鲸鱼统治世界";   // ⚠️ 不能带职业 —— AEAssist 拿它当设置目录名
    }

    public override string OverlayTitle => "小鲸鱼统治世界 · 幻术师";
    public override string Description => "BlueWhale.AutoHealerACR — 实验性项目：让 AI 接管治疗输出决策（幻术师 1-50 级）";

    public override void OnDrawSetting()
    {
        base.OnDrawSetting();
        AiSettingPage.画();
    }

public override Rotation Build(string settingFolder)
    {
        var rot = base.Build(settingFolder);   // 原版全部构建流程（队列/事件/起手）

        // AcrType 枚举：Both=日常&高难 / Normal=日常 / HighEnd=高难 / PVP
        // 基类默认是 Both，这里覆盖成 Normal —— 列表里只显示「日常」
        rot.AcrType = AcrType.Normal;

        AiSettings.初始化(settingFolder);        // ★ 先告诉 AiSettings 设置存哪 ★

        // 挂上记忆采集钩子（原版不挂 → 什么也不发生）
        HealerACR.Common.记忆钩子.记决策 = (id, 名) =>
            战斗记忆.记决策(id, 名, 战斗记忆.判来源(id));
        HealerACR.Common.记忆钩子.每帧 = 战斗记忆.每帧更新;

        // ★ 挂上状态重置钩子 ★
        //   换本时把 AI 层的状态清掉 —— 否则上个副本的判断会带过来
        //   （比如上个本一直打小怪判"激进"，进 Boss 本还是激进）。
        HealerACR.Common.状态重置钩子.重置 = () =>
        {
            AiStrategyLayer.重置();
            AiDecisionLayer.重置();
            AiThresholdAdapter.重置平滑();
            Ai调试.日志("换本 → 已重置倾向 / 建议队列 / 阈值平滑");
        };

        // ★ 启动 AI 初始化 ★
        //   加载后立刻跑一次完整请求，让 AI 先把局面过一遍 ——
        //   否则前 10 秒它是"哑"的（倾向=未知，阈值偏移=0）。
        Ai初始化.开始();

        // 把「解除熔断」这个 QT 开关接到实际动作上
        // （用户能在 QT 控制台给它绑快捷键 → 按一下就解除熔断）
        HealerACR.Common.HealQt.解除熔断请求 = () =>
        {
            DeepSeekClient.解除熔断();
            Ai初始化.重置();
            Ai调试.日志("手动解除熔断");
            屏幕提示.成功("AI 熔断已手动解除", "ai-manual-unfuse");
        };

        AiHookInstaller.挂载();                  // 挂上 AI 阈值钩子

        return rot;
    }

    public override void Dispose()
    {
        AiHookInstaller.卸载();                  // 卸载，避免影响其他 ACR
        HealerACR.Common.记忆钩子.卸载();
        HealerACR.Common.状态重置钩子.卸载();
        base.Dispose();
    }
}
}
