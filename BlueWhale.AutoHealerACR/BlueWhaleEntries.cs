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

/// <summary>
/// AI 层的挂载 / 卸载 —— 五个职业入口共用同一套。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么抽出来 ★
///
///    原来这 5 个入口各自抄了一份**一模一样**的挂载代码
///    （记忆钩子 + 状态重置钩子 + 初始化 + 解除熔断 + AiHookInstaller）。
///    加一个钩子就要改 5 处 —— 漏一处就是"只有那个职业不生效"的静默 bug。
///
///  ★ 两个钩子各管一件事（别搞混）★
///
///    · `状态重置钩子` ← 换**副本**（OnTerritoryChanged）
///        → 清状态即可（同一个职业，技能表没变）
///    · `记忆钩子.进入循环` ← 切**职业** / 重载 ACR（OnEnterRotation）
///        → 清状态 **并且重新初始化**
///
///    切职业比换本更彻底：换了一整套技能表和判断逻辑，
///    AI 的倾向 / 阈值偏移 / 预取队列全是按**上一个职业**的局面得出的。
///    不清就会拿学者的结论去指导白魔（和"跨职业静态污染"同一类问题）。
///
///    ⚠️ 切职业还必须**重新初始化**（不只是清空）：
///       否则 AI 有一段"哑"的窗口（倾向=未知、阈值偏移=0），
///       开场那几秒等于没有 AI。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
internal static class Ai层挂载
{
    /// <summary>入口 Build 时调（5 个职业入口共用）</summary>
    public static void 挂载()
    {
        // ── 记忆采集（原版不挂 → 什么也不发生）──
        记忆钩子.记决策 = (id, 名) => 战斗记忆.记决策(id, 名, 战斗记忆.判来源(id));

        // ⚠️ `记忆钩子.每帧` **是死代码，不要往这里挂任何东西**
        //
        //    它的触发方法 `记忆钩子.通知每帧()` 在**全项目里一个调用点都没有**
        //    （只有定义）—— 所以赋给它的回调**永远不会被执行**。
        //
        //    原来这里写的是 `记忆钩子.每帧 = 战斗记忆.每帧更新;`，
        //    看起来像"每帧都在采集记忆"，其实**一次都没跑过**。
        //
        //    真正每帧驱动的是 `AiHeartbeat`（本文件下面那个 resolver）——
        //    它靠 `Check()` 返回 -1 常驻队列来当发动机。
        //    要加"每帧做的事"请加到那里。
        //
        //    ⚠️ 这个坑很隐蔽：赋值成功、编译通过、不报任何错，只是永远不跑。
        记忆钩子.每帧 = null;

        // ── ① 换副本：清状态 **并且** 重新初始化 ──
        //
        // ══════════════════════════════════════════════════════════════
        //  ★ 这里原来只清状态、不重新初始化 —— 那是个实打实的 bug ★
        //
        //  ── 日志实证 ──
        //      00:28:57  换本 -> 已重置倾向 / 建议队列 / 阈值平滑
        //      00:29:37  换本 -> 已重置倾向 / 建议队列 / 阈值平滑
        //      （之后**没有任何**「进本识别：」行）
        //
        //  ── 后果 ──
        //      AI 的"下一句"是活的，但它脑子里那份**局面报告是上一个地图的**。
        //      表现就是"AI 好像并不知道在打什么本" —— 因为它确实不知道：
        //      没有任何一步告诉过它。
        //
        //  ── 为什么 `进本识别.进入副本` 没能补上 ──
        //      那个事件的判据是 `地名.是副本(地图)`，
        //      而它靠 `ContentFinderCondition` 判定 ——
        //      **优雷卡这类特殊地图不在里面**，所以在那些地图里**永不触发**。
        //      （日志里那两行换本之后就再没有识别行，正是这个原因。）
        //
        //  ── 为什么换本**本来就该**重新初始化 ──
        //      换本 = 地图变了 = 时间轴变了 = 等级同步可能变了
        //      = 敌人变了 = 队伍可能变了。
        //      除了"职业没变"这一条，**其它全变了** ——
        //      和切职业的差别小到不值得区别对待。
        //
        //  ⚠️ 顺序必须 `重置()` → `开始()`：
        //     `开始()` 有幂等守卫（已经成功过就不重来），不先重置根本不会跑第二次。
        //
        //  ⚠️ 重复调用是安全的：`开始()` 自己会吃掉重入
        //     （日志里那几条「已经在初始化中，忽略这次重复的 开始()」就是它）。
        //     所以在房区/野外频繁换图不会打出一串请求。
        // ══════════════════════════════════════════════════════════════
        状态重置钩子.重置 = () =>
        {
            清AI状态();
            Ai调试.日志("换本 -> 已重置倾向 / 建议队列 / 阈值平滑");

            // ★ 重新初始化 —— 但**不在这里发请求** ★
            //
            // ══════════════════════════════════════════════════════════
            //  ★ 为什么改成"只标记、不发送"（实测日志）★
            //
            //  ── 原来的问题 ──
            //      换本钩子（OnTerritoryChanged）和 `进本识别` 几乎同时触发：
            //          00:42:54.213  开始初始化   ← 换本钩子
            //          00:42:54.220  开始初始化   ← 进本识别（20ms 后）
            //          00:42:54.233  开始初始化   ← 又 13ms 后
            //      三次触发 → 世代计数涨到 17 →
            //          00:43:00.604  「第 15 代已过期（当前 17 代），丢弃本次结果」
            //      ⇒ **白等一轮**，而且那 6 秒里 AI 是哑的。
            //
            //  ── 现在的做法 ──
            //      这里只清状态 + 标记"需要重建"。真正的请求由
            //      `进本识别` 的**落地稳定回调**发出 ——
            //      它自带 1.5 秒稳定窗口 + `IsBetweenAreas` 判定，
            //      天然把这几毫秒内的重复触发**合并成一次**。
            //
            //  ⚠️ 为什么不干脆在这里重试：那会和稳定窗口赛跑，
            //     谁先谁后又变成不可预测 —— 正是要消掉的东西。
            // ══════════════════════════════════════════════════════════
            try
            {
                // 只装时间轴（这一步幂等，重复调用无害），
                // 让紧接着的稳定回调能拿到「有机制的时间轴」
                try { HealerACR.Timeline.TimelineManager.现在加载(); } catch { }

                // ⚠️ **`Ai初始化.重置()` 必须在这里**（用户要求：初始化成功前走本地策略）
                //
                //  它会把 `已完成` 清掉 —— 那一份结论是**上一个地图**的：
                //  不同的副本名、不同等级同步、不同时间轴、不同敌人。
                //  不清的话 `已完成 == true` 会让：
                //    · `局面监控` 的剧变触发照常放行
                //    · `AiSuggestionResolver` 拿旧结论去套新副本
                //  表现就是"AI 以为自己还在上一个本"。
                //
                //  ⚠️ 这里 `进行中` 可能是 false（上一次已完成），
                //     所以 `重置()` 不会动世代 —— 紧接着的稳定回调会 `开始()`。
                Ai初始化.重置();

                Ai调试.日志("换本 -> 已重置 AI 层（含已完成标记），等落地稳定后重建上下文");
            }
            catch { }
        };

        // ── ② 切职业 / 重载 ACR：清状态 **并且** 重新初始化 ──
        记忆钩子.进入循环 = () =>
        {
            清AI状态();
            Ai初始化.重置();          // 先清"已完成/进行中"的标记，否则 开始() 会被挡掉
            Ai调试.日志("切换职业 -> 已重置 AI 层，重新初始化");

            // 重新跑一次完整请求，让 AI 立刻拿到**新职业**的结论
            Ai初始化.开始();
        };

        // ══════════════════════════════════════════════════════════════
        //  ── ③ 记录模式：观察玩家手动操作 + 对局收尾 ──
        //
        //  链路：记录模式（本地层观察）→ 对局记录（本层存盘）
        //        → 对局收尾（判结束）→ 记忆库（AI 提炼）
        //        → AiSituation（喂回给 AI）
        //
        //  ⚠️ 这里只负责**采集侧**。"停手"是本地层做的
        //     （HealerEntryBase.构建决策队列 返回空队列）——
        //     符合 开发约定.md G 节：判断和行为在本地，AI 只做增强。
        // ══════════════════════════════════════════════════════════════
        记录模式.记录一次 = 对局记录.记一次;

        // 关闭记录模式时，把没记完的残局丢掉（不算一局完整的）
        记录模式.进入记录模式 = 开 =>
        {
            try
            {
                if (!开 && 对局记录.有记录)
                {
                    Ai调试.日志($"记录模式关闭 -> 丢弃未完成的记录（{对局记录.本局条数} 条）");
                    对局记录.丢弃();
                    对局收尾.重置();
                }
                else if (开)
                {
                    对局收尾.重置();
                }
            }
            catch { }
        };

        // ── 进副本：重建 AI 上下文 + 输出核对结果 ──
        //
        //  ══════════════════════════════════════════════════════════════
        //  ★ 为什么必须在这里**重新初始化** ★
        //
        //  ── 原来的时序是错的 ──
        //     `Ai初始化.开始()` 在挂载时调（见下面"启动初始化"），
        //     而挂载发生在**进本之前**（切职业 / 重载 ACR）。
        //     那时人还在房区 —— 局面报告里：
        //       · 【副本】段 → 房区名 / 读不到
        //       · 【副本与机制时间轴】段 → "这个副本没有匹配到时间轴"
        //         （时间轴原来只在战斗中装载，见 `进本识别` 里的说明）
        //
        //     ⇒ AI 拿着**房区的局面**进了副本，整场都不知道在打什么本、
        //       也不知道后续机制 —— 只能纯反应式治疗。
        //       而"知道接下来有什么机制"恰恰是给它时间轴的全部意义。
        //
        //  ── 修法 ──
        //     进本时**重跑一次初始化**。这时：
        //       · 副本已加载 → 【副本】段是真名
        //       · 时间轴已装载（`进本识别` 里先调了 `TimelineManager.现在加载()`）
        //         → 【副本与机制时间轴】段有真机制
        //       · 等级同步已生效 → 技能清单按同步后的等级过滤
        //
        //  ⚠️ 代价：AI 要重新思考约 15-25 秒。
        //     但这是**一局一次**（进本时），而且开怪前本来就在等 ——
        //     拿"开头十几秒的建议质量"换"整场都知道在打什么本"，划算。
        //
        //  ⚠️ 必须 `重置()` 再 `开始()`：`开始()` 有幂等守卫
        //     （已经成功过就不重来），不重置根本不会跑第二次。
        // ══════════════════════════════════════════════════════════════
        HealerACR.Common.进本识别.进入副本 = () =>
        {
            try
            {
                // ① 先输出"本地看到了什么"—— 这一行不依赖 AI，一定会打出来
                var 识别 = AiSituation.识别摘要();
                Ai调试.日志("进本识别：" + 识别);
                屏幕提示.成功($"{识别}，重建 AI 上下文", "ai-duty-enter");

                // ② 重建 AI 上下文（这次带上副本和时间轴）
                Ai初始化.重置();
                Ai初始化.开始();
            }
            catch { }
        };

        // ── 启动初始化 ──
        //   加载后立刻跑一次完整请求，否则前 10 秒它是"哑"的。
        Ai初始化.开始();

        // ── 「强制熔断」QT 开关 —— **状态型**，不弹回 ──
        //
        //   勾上 = 停掉所有 AI 请求；取消 = 恢复。
        //  ⚠️ 参数是 bool（开还是关）—— 不能像下面那个一样无参。
        HealQt.强制熔断请求 = 开 =>
        {
            DeepSeekClient.设强制熔断(开);
        };

        // ── 「解除熔断」QT 开关接到实际动作 ──
        HealQt.解除熔断请求 = () =>
        {
            DeepSeekClient.解除熔断();

            // ⚠️ 必须把「强制熔断」的 QT 也写回 false ——
            //   `解除熔断()` 已经把它清了，但 QT 值还是 true。
            //   不写回的话，`HealQt.每帧更新` 下一帧发现
            //   "QT 还是 true 但状态已经是 false"→ 又把强制熔断打开，
            //   现象就是"按了解除熔断没反应"。
            HealQt.写回("强制熔断", false);
            Ai初始化.重置();
            Ai调试.日志("手动解除熔断");
            // ★ 彩蛋：同「初始化成功」—— 排队，之后逐句发 ★
            HealerACR.Common.彩蛋.试试();

            屏幕提示.成功("熔断已手动解除", "ai-manual-unfuse");
        };

        AiHookInstaller.挂载();     // 挂上 AI 阈值钩子
    }

    /// <summary>清 AI 层的所有状态（换本 / 切职业都走这里）</summary>
    private static void 清AI状态()
    {
        try { AiStrategyLayer.重置(); } catch { }
        try { AiDecisionLayer.重置(); } catch { }
        try { AiThresholdAdapter.重置平滑(); } catch { }
        try { 坦克压力.重置(); } catch { }
        try { 局面监控.重置(); } catch { }   // 血量/目标基准必须归零
        try { 对局收尾.重置(); } catch { }   // 副本收尾计时（换本后重新算）
    }

    /// <summary>退出时卸载，避免影响其他 ACR</summary>
    public static void 卸载()
    {
        AiHookInstaller.卸载();
        记忆钩子.卸载();
        状态重置钩子.卸载();
        HealQt.解除熔断请求 = null;
        HealQt.强制熔断请求 = null;

        // 记录模式的钩子也要摘掉 —— 否则关掉 ACR 之后
        // 本地层还会往一个已经没人管的采集器里塞数据
        记录模式.记录一次 = null;
        记录模式.进入记录模式 = null;
        记录模式.设置(false);   // 顺手关掉，别留着"停手"状态

        // ⚠️ 必须退订施法事件 —— 事件持有回调，
        //    不退订的话 ACR 停用了回调还会被触发（详见 记录模式.卸载 的说明）
        记录模式.卸载();
    }
}

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
        记忆库页面.画();           // 叠加记忆库（记录模式状态 + 库管理）
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

        // ★ 挂载 AI 层（记忆采集 + 换本重置 + 切职业重新初始化 + 启动初始化）★
        //   ⚠️ 别在这里内联展开 —— 5 个职业入口共用同一套，
        //      加钩子只改 Ai层挂载.挂载() 一处，避免"漏改某个职业"的静默 bug。
        Ai层挂载.挂载();


        return rot;
    }

    public override void Dispose()
    {
        Ai层挂载.卸载();                         // 卸载，避免影响其他 ACR
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

        // ══════════════════════════════════════════════════════════════
        //  ★ 记录模式：AI 层也要停手 ★
        //
        //  基类在 `构建决策队列()` 里已经对**职业专属队列**做了拦截
        //  （记录模式返回空队列）。但那里拦不到这一段 ——
        //  因为下面两个 resolver 是**在这里手动插进去的**。
        //
        //  不拦的话后果很严重：
        //    ① AI 照样出手 → 污染记录（记忆库里混入 ACR 的操作）
        //    ② 心跳还在跑 → **白白烧 API 额度**，而记录模式根本不需要 AI 决策
        //       （它只需要"观察 + 最后提炼一次"）
        //
        //  ⚠️ 这段守卫在 4 个职业入口里各有一份 ——
        //     加新的 resolver 时**别忘了它**。
        // ══════════════════════════════════════════════════════════════
        if (HealerACR.Common.记录模式.开启 && !HealerACR.Common.记录模式.保留保命兜底)
        {
            return 队列;
        }

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
        记忆库页面.画();           // 叠加记忆库（记录模式状态 + 库管理）
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

        // ★ 挂载 AI 层（记忆采集 + 换本重置 + 切职业重新初始化 + 启动初始化）★
        //   ⚠️ 别在这里内联展开 —— 5 个职业入口共用同一套，
        //      加钩子只改 Ai层挂载.挂载() 一处，避免"漏改某个职业"的静默 bug。
        Ai层挂载.挂载();


        return rot;
    }

    public override void Dispose()
    {
        Ai层挂载.卸载();                         // 卸载，避免影响其他 ACR
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

        // ══════════════════════════════════════════════════════════════
        //  ★ 记录模式：AI 层也要停手 ★
        //
        //  基类在 `构建决策队列()` 里已经对**职业专属队列**做了拦截
        //  （记录模式返回空队列）。但那里拦不到这一段 ——
        //  因为下面两个 resolver 是**在这里手动插进去的**。
        //
        //  不拦的话后果很严重：
        //    ① AI 照样出手 → 污染记录（记忆库里混入 ACR 的操作）
        //    ② 心跳还在跑 → **白白烧 API 额度**，而记录模式根本不需要 AI 决策
        //       （它只需要"观察 + 最后提炼一次"）
        //
        //  ⚠️ 这段守卫在 4 个职业入口里各有一份 ——
        //     加新的 resolver 时**别忘了它**。
        // ══════════════════════════════════════════════════════════════
        if (HealerACR.Common.记录模式.开启 && !HealerACR.Common.记录模式.保留保命兜底)
        {
            return 队列;
        }

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
        记忆库页面.画();           // 叠加记忆库（记录模式状态 + 库管理）
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

        // ★ 挂载 AI 层（记忆采集 + 换本重置 + 切职业重新初始化 + 启动初始化）★
        //   ⚠️ 别在这里内联展开 —— 5 个职业入口共用同一套，
        //      加钩子只改 Ai层挂载.挂载() 一处，避免"漏改某个职业"的静默 bug。
        Ai层挂载.挂载();


        return rot;
    }

    public override void Dispose()
    {
        Ai层挂载.卸载();                         // 卸载，避免影响其他 ACR
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

        // ══════════════════════════════════════════════════════════════
        //  ★ 记录模式：AI 层也要停手 ★
        //
        //  基类在 `构建决策队列()` 里已经对**职业专属队列**做了拦截
        //  （记录模式返回空队列）。但那里拦不到这一段 ——
        //  因为下面两个 resolver 是**在这里手动插进去的**。
        //
        //  不拦的话后果很严重：
        //    ① AI 照样出手 → 污染记录（记忆库里混入 ACR 的操作）
        //    ② 心跳还在跑 → **白白烧 API 额度**，而记录模式根本不需要 AI 决策
        //       （它只需要"观察 + 最后提炼一次"）
        //
        //  ⚠️ 这段守卫在 4 个职业入口里各有一份 ——
        //     加新的 resolver 时**别忘了它**。
        // ══════════════════════════════════════════════════════════════
        if (HealerACR.Common.记录模式.开启 && !HealerACR.Common.记录模式.保留保命兜底)
        {
            return 队列;
        }

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
        记忆库页面.画();           // 叠加记忆库（记录模式状态 + 库管理）
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

        // ★ 挂载 AI 层（记忆采集 + 换本重置 + 切职业重新初始化 + 启动初始化）★
        //   ⚠️ 别在这里内联展开 —— 5 个职业入口共用同一套，
        //      加钩子只改 Ai层挂载.挂载() 一处，避免"漏改某个职业"的静默 bug。
        Ai层挂载.挂载();


        return rot;
    }

    public override void Dispose()
    {
        Ai层挂载.卸载();                         // 卸载，避免影响其他 ACR
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

        // ══════════════════════════════════════════════════════════════
        //  ★ 记录模式：AI 层也要停手 ★
        //
        //  基类在 `构建决策队列()` 里已经对**职业专属队列**做了拦截
        //  （记录模式返回空队列）。但那里拦不到这一段 ——
        //  因为下面两个 resolver 是**在这里手动插进去的**。
        //
        //  不拦的话后果很严重：
        //    ① AI 照样出手 → 污染记录（记忆库里混入 ACR 的操作）
        //    ② 心跳还在跑 → **白白烧 API 额度**，而记录模式根本不需要 AI 决策
        //       （它只需要"观察 + 最后提炼一次"）
        //
        //  ⚠️ 这段守卫在 4 个职业入口里各有一份 ——
        //     加新的 resolver 时**别忘了它**。
        // ══════════════════════════════════════════════════════════════
        if (HealerACR.Common.记录模式.开启 && !HealerACR.Common.记录模式.保留保命兜底)
        {
            return 队列;
        }

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
            _保存提示 = $"已保存（{项}）-> {路径}";
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
            ImGui.TextDisabled("  ^ 填入 Key 后，下面会出现模型选择和开关。");
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
        ImGui.TextDisabled("  对延迟敏感（GCD 只有 2.5 秒），建议先只开阶段 A");

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
            if (s.调试模式) 屏幕提示.成功("调试模式已开启 —— 小鲸鱼的日志会直接显示在屏幕上", "dbg-on");
            else 屏幕提示.提示("小鲸鱼", "调试模式已关闭", "dbg-off");
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
                    LogHelper.Info($"[BlueWhale.AI] 连接测试成功！AI 回复：{r}");
                    LogHelper.Info("[BlueWhale.AI] 说明：Key、地址、模型都正常，可以开阶段 A 了。");
                    // 不去解析 AI 回了什么 —— 只要能拿到非空回复，
                    // 就说明 Key / 地址 / 模型 这一整条链路是通的。
                    _测试结果 = "连接正常";
                    _测试中 = false;
                }
                else
                {
                    LogHelper.Error("[BlueWhale.AI] 连接测试失败。常见原因：");
                    LogHelper.Error("    · Key 填错或已失效（检查 sk- 开头、有没有多余空格）");
                    LogHelper.Error("    · 网络不通 / 需要代理");
                    LogHelper.Error("    · 账户余额不足");
                    LogHelper.Error("    · 模型名写错（应该是 deepseek-chat 或 deepseek-reasoner）");
                    LogHelper.Error("[BlueWhale.AI] 具体错误看上面那行『请求失败（...）』的原因。");
                    _测试结果 = "连接失败 —— 看日志最后几行（Key / 网络 / 余额 / 模型名）";
                    _测试中 = false;
                }
            });
        }

        // ---- 实时状态 ----
        ImGui.Separator();
        ImGui.Text("AI 当前状态");
        ImGui.Text($"  状态：{DeepSeekClient.状态描述()}    连续失败：{DeepSeekClient.连续失败数}");

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
            ImGui.TextDisabled("  决策建议：无");
        }

        // ══════════════════════════════════════════════════════════════
        //  ★ 阶段 B 命中率统计（实测要看的一屏）★
        //
        //    旧版只显示「命中 X / 过期 Y」，看不出**为什么**没命中 ——
        //    所以装上实测时只能看到一个难看的数字，不知道该调哪里。
        //    现在把"被终审拦下的原因"也列出来，一次就能定位。
        // ══════════════════════════════════════════════════════════════
        ImGui.Separator();
        ImGui.TextDisabled("阶段 B 命中率统计");
        ImGui.TextWrapped("  " + AiDecisionLayer.状态描述());
        ImGui.TextWrapped("  被终审拦下：" + AiDecisionLayer.拦截摘要());
        ImGui.TextDisabled($"  其中排在队列第 2 位之后才过期的：{AiDecisionLayer.排队过期条数} 条");
        ImGui.TextDisabled("  " + 局面监控.状态描述());
        ImGui.TextDisabled("  说明：『过期』= 生成后在队列里放着没人用就超时了；");
        ImGui.TextDisabled("        命中率 = 命中 /(命中 + 过期)，被局面剧变清空的条数不计入分母。");
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

            坦克压力.每帧更新();          // 采样 T 的血量波动
            局面监控.每帧更新();          // ★ 检测局面剧变 → 作废预取队列（原先写了没人调）
            Ai初始化.每帧更新();          // 初始化超时检查
            Ai初始化.检查熔断提示();      // 熔断状态变化 → 屏幕横幅告知

            // ⚠️ `HealQt.每帧更新()` 已经**移到本地层**了
            //    （HealerEntryBase.OnBattleUpdate）—— 别在这里再调一次。
            //    原因见那里的注释：它原来只跟着 AI 心跳跑，
            //    而记录模式下心跳会被移出队列，那些开关就永远处理不到。

            // ⚠️ `以太管理.每帧更新()` 也**已经移到本地层**了
            //    （HealerEntryBase.OnBattleUpdate）—— 别在这里再调一次。
            //    它和上面的 `HealQt.每帧更新()` 是同一类问题：
            //    那是**本地逻辑**（学者的以太保留/抑制），
            //    按开发约定 G 不该依赖 AI 层存活。
            //    原来只在这里调 → 卸载 BlueWhale 后 `抑制中` 永远是 false。

            // 对局收尾：判断"这场打完了" → 落盘 + AI 提炼成记忆
            //   ⚠️ 只在记录模式里真正干活（内部第一行就检查），非记录模式零开销
            对局收尾.每帧更新();

            // 战斗记忆采集（阶段 1：只写不读，不影响任何决策）
            战斗记忆.每帧更新();

            // ⚠️ **进本监视不在这里** —— 它已经移到本地层
            //    （`HealerEntryBase.OnBattleUpdate` → `进本识别.每帧检查()`）。
            //    理由：识别进没进本是**本地能力**，不该依赖 AI 层存活；
            //    而且记录模式下心跳会被移出队列，这里根本跑不到。
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

        // ★ 挂载 AI 层（记忆采集 + 换本重置 + 切职业重新初始化 + 启动初始化）★
        //   ⚠️ 别在这里内联展开 —— 5 个职业入口共用同一套，
        //      加钩子只改 Ai层挂载.挂载() 一处，避免"漏改某个职业"的静默 bug。
        Ai层挂载.挂载();


        return rot;
    }

    public override void Dispose()
    {
        Ai层挂载.卸载();                         // 卸载，避免影响其他 ACR
        base.Dispose();
    }
}
}
