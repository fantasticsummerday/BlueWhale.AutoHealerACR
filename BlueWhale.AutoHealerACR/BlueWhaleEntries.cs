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
/// <summary>
/// **AI 层挂载 / 卸载** —— 五个职业入口共用同一套。
///
/// [!] 为什么是 public：`HealerEntryBase.OnEnterRotation()` 要用**反射**
///     调 `尝试挂载AI层()`（它属于 HealerACR 项目，不能直接引用 BlueWhale），
///     而反射找不到 internal 类型。
/// </summary>
public static class Ai层挂载
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

    /// <summary>是否已经挂载过（`挂载()` **不是幂等的** —— 每次都会重设钩子 + 重新初始化）</summary>
    private static bool _已挂载;

    /// <summary>"跳过挂载"诊断日志的节流时间戳（每帧回调会反复触发）</summary>
    private static long _上次跳过日志;

    /// <summary>
    /// **是否已经真的尝试过挂载** —— 和 `_已挂载` 的区别很重要：
    ///   · `_已挂载`   = 挂载**成功**了
    ///   · `_已尝试挂载` = 已经调过 `挂载()`（不论成败）
    ///
    /// [!] 为什么要两个：
    ///   只用 `_已挂载` 的话，`挂载()` 抛异常（被 catch 吞掉）后它是 false，
    ///   而每帧回调都会再调一次 => **每帧重挂** => 崩溃。
    ///   只用"已尝试"的话，发现阶段判"不是当前"就标上 =>
    ///   之后永远不挂 => AI 永不初始化。
    /// </summary>
    private static bool _已尝试挂载;

    /// <summary>
    /// **尝试挂载 AI 层** —— 只在"我确实是当前 ACR"时才真挂。
    ///
    /// ══════════════════════════════════════════════════════════════
    ///  [!] 为什么需要身份判断（实测 bug 的根因）
    ///
    ///    `Build()` 在 **ACR 发现阶段** 对**每一个** ACR 都跑，
    ///    而入口的 `Build()` 里原本无条件 `Ai层挂载.挂载()` ——
    ///    它会挂阈值钩子 + `Ai初始化.开始()`（**真实 API 请求**）。
    ///    于是"选的是别的 ACR，小鲸鱼照样初始化"。
    ///
    ///  [!] 为什么由本方法统一判、而不是各调用点自己判
    ///
    ///    5 个职业入口 + `OnEnterRotation` 一共 6 个触发点，
    ///    各写一遍迟早漏一个（本项目在这种"漏一处"上栽过多次）。
    ///
    ///  [!] 为什么要"只挂一次"
    ///
    ///    `挂载()` 每次都会重设钩子并重新 `Ai初始化.开始()`
    ///    ⇒ 重复调会白花 API 请求。
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    public static void 尝试挂载AI层()
    {
        // ══════════════════════════════════════════════════════════════
        //  ★ 先标记"已尝试"，再判身份 ★
        //
        //  [!] 为什么顺序很重要（可能是闪退的根因）：
        //      原来 `_已挂载 = true` 只在 `挂载()` **完全成功**后才执行。
        //      如果 `挂载()` 中途抛异常（被下面的 catch 吞掉），
        //      `_已挂载` 就一直是 false ——
        //      而**每帧回调都会再调一次本方法**（第三道兜底）
        //      => 每帧重走全套"挂钩子 + 订阅事件 + 开初始化"
        //      => 钩子/订阅无限累积 => 崩溃（访问违规 0xc0000005）。
        //
        //  [!] 为什么"失败也不重试"是对的：
        //      `挂载()` 做的事（挂钩子 / 订阅事件 / 开初始化）**幂等性很差**，
        //      重复调比不调危险得多。失败一次就停（日志里有痕），
        //      比每帧重来安全得多。
        // ══════════════════════════════════════════════════════════════
        if (_已挂载 || _已尝试挂载) return;

        try
        {
            if (!HealerACR.Common.ACR身份.是当前())
            {
                // [!] 必须节流：每帧回调也会调到这里（第三道兜底），
                //     不节流的话"没选中小鲸鱼"时会**每帧写一行日志**。
                //     30 秒一条足够核对"到底跳过了没有"。
                var 现在 = AEAssist.Helper.TimeHelper.Now();
                if (现在 - _上次跳过日志 > 30000)
                {
                    _上次跳过日志 = 现在;
                    // [!] 带上诊断串 —— 前三版都是"猜为什么没匹配上"，
                    //     有它就不用猜了（直接看到真实的 currRotation 是什么）。
                    LogHelper.Info("[BlueWhale.AI] 当前 ACR 不是小鲸鱼 -> 跳过 AI 层挂载（不发请求）｜"
                        + HealerACR.Common.ACR身份.诊断());
                }
                return;
            }

            // ══════════════════════════════════════════════════════════
            //  ★ 在真的调 `挂载()` **之前**标记"已尝试" ★
            //
            //  [!] 为什么必须用单独的标记、且放在这里：
            //      · 放在"判身份之前"是错的 —— 发现阶段 `currRotation`
            //        还不是小鲸鱼，会被判"不是当前"直接返回，
            //        但标记已经设上 => **之后永远不再挂载 => AI 永不初始化**
            //      · 放在"挂载成功之后"也不够 —— `挂载()` 中途抛异常时
            //        `_已挂载` 仍是 false，而**每帧回调都会再调一次**
            //        => 每帧重走全套"挂钩子 + 订阅事件 + 开初始化"
            //        => 钩子/订阅无限累积 => 崩溃（访问违规 0xc0000005）
            //
            //  ⇒ 正确位置：**已经确定要挂、就在调之前**。
            //     成功就挂上了；失败也不会每帧重试（日志里有痕）。
            //     挂载做的事幂等性很差，重复调比不调危险得多。
            // ══════════════════════════════════════════════════════════
            _已尝试挂载 = true;
            挂载();
            _已挂载 = true;
            LogHelper.Info("[BlueWhale.AI] 当前 ACR 是小鲸鱼 -> AI 层已挂载｜"
                + HealerACR.Common.ACR身份.诊断());
        }
        catch (Exception e)
        {
            LogHelper.Error("[BlueWhale.AI] 尝试挂载 AI 层失败：" + e.Message);
        }
    }

    /// <summary>退出时卸载，避免影响其他 ACR</summary>
    public static void 卸载()
    {
        // ══════════════════════════════════════════════════════════════
        //  ★ 第一件事：把"ACR 卸载钩子"摘掉（**断递归**）★
        //
        //  [!] 为什么必须是第一件事（3.19.4 引入的闪退根因）：
        //      链是 `HealerEntryBase.Dispose()` -> `卸载钩子.通知()`
        //         -> `Ai层挂载.卸载()`（钩子本来就指向这里）-> …
        //      而 `卸载钩子.卸载` 是**静态字段**、指向本方法 ——
        //      只要卸载过程中有任何一条路径再触发一次通知，
        //      就会 `Dispose -> 通知 -> 卸载 -> … -> Dispose` **无限递归**
        //      => `StackOverflowException`
        //      => Windows 报 `0xc0000005`（栈溢出表现为访问违规）
        //      => **游戏直接闪退，绕过所有 try/catch**
        //
        //      崩溃处理器日志实证：`Failed to read exception information; error: 0x6d`
        //      —— 托管异常都能正常读到，读不到正是栈溢出的特征。
        //
        //  [!] 为什么防御放在这里而不是调用方：
        //      调用方不一定知道要先摘；而且本方法也可能被**直接**调用
        //      （切职业路径）。防御要放在**被保护的一方**。
        // ══════════════════════════════════════════════════════════════
        try { HealerACR.Common.卸载钩子.卸载 = null; } catch { }

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

        // 复位两个标记 —— 否则卸载后再被选中时不会再挂上去
        _已挂载 = false;
        _已尝试挂载 = false;
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
    public override string Description => "BlueWhale.AutoHealerACR — 实验性项目：让 AI 接管白魔的输出决策";

    /// <summary>
    /// ★ 必须 override ★
    /// 基类的 OnDrawSetting 只画 HealerACR 的设置，
    /// 不调 base 就丢原版界面，不调 AiSettingPage 就没有 AI 界面 —— 两个都要。
    /// </summary>
    public override void OnDrawSetting()
    {
        // ══════════════════════════════════════════════════════════════
        //  ★ 阶段日志 —— 定位"点设置就闪退"（实测 bug）★
        //
        //  [!] 为什么加：这个闪退推理了五次都没定位（Dispose / OnEnterRotation /
        //      Build / ACR身份 / 递归 全否）。`OnDrawSetting` 是**唯一的确定入口**
        //      （用户点设置就崩）—— 所以在每一步打日志，
        //      **崩溃前最后打出来的那一行就是崩溃点所在阶段**。
        //
        //  [!] 为什么要 try/catch：托管异常能被挡住并记下来（那就是普通 bug）；
        //      挡不住的是栈溢出（闪退的常见原因）——
        //      那种情况下"最后一行的阶段名"仍然有效。
        //
        //  [!] 为什么不直接二分回退版本：回退只能定位到"哪个提交"，
        //      而那个提交里有 5 处改动，还得再猜。日志能直接定位到**哪一行**。
        // ══════════════════════════════════════════════════════════════
        static void 记(string 阶段)
        {
            try { LogHelper.Info("[设置诊断] 进入阶段：" + 阶段); } catch { }
        }

        记("0-开始");
        try
        {
            记("1-base.OnDrawSetting（原版设置）");
            base.OnDrawSetting();
            记("1-完成");
        }
        catch (Exception e)
        {
            LogHelper.Error("[设置诊断] base.OnDrawSetting 抛异常：" + e);
            记("1-异常已捕获");
        }

        try
        {
            记("2-AiSettingPage.画");
            AiSettingPage.画();
            记("2.5-AiSettingPage 已返回");   // ★ 区分"A 没返回"还是"B 卡在后面"
            记("2-完成");
        }
        catch (Exception e)
        {
            LogHelper.Error("[设置诊断] AiSettingPage.画 抛异常：" + e);
            记("2-异常已捕获");
        }

        try
        {
            记("3-记忆库页面.画");
            记忆库页面.画();
            记("3-完成");
        }
        catch (Exception e)
        {
            LogHelper.Error("[设置诊断] 记忆库页面.画 抛异常：" + e);
            记("3-异常已捕获");
        }

        记("9-全部完成");
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

        //  ★ 尝试挂载 AI 层 —— 只在"我确实是当前 ACR"时真挂 ★
        //
        //  [!] 为什么需要判断（实测 bug 的根因，第三次修）：
        //      `Build()` 在 **ACR 发现阶段** 对 **每一个** ACR 都跑 ——
        //      日志实证：
        //          16:46:28.845  加载全部ACR : D:/FF14/ACR   <- 扫描发现
        //          16:46:29.197  阈值钩子已挂载               <- AI 层被挂载
        //          16:46:29.198  成功加载acr: BlueWhaleWhiteMageEntry
        //          16:46:29.597  LoadRotation Success: Shiyuvi Scholar <- 用户选的 Shiyu
        //      => 不管选谁，小鲸鱼都挂钩子 + **发一整轮 API 请求**。
        //
        //  [!] 为什么"两处都试"而不是只看这里：
        //      发现阶段 `Data.currRotation` **可能还是 null**
        //      => 只在这里判会把"真被选中"那次也挡掉，AI 永远不初始化。
        //      所以 `OnEnterRotation()` 也会再试一次；`挂载()` 自带"只挂一次"开关。
        // ══════════════════════════════════════════════════════════════
        Ai层挂载.尝试挂载AI层();


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
    public override string Description => "BlueWhale.AutoHealerACR — 实验性项目：让 AI 接管学者的输出决策";

    /// <summary>
    /// ★ 必须 override ★
    /// 基类的 OnDrawSetting 只画 HealerACR 的设置，
    /// 不调 base 就丢原版界面，不调 AiSettingPage 就没有 AI 界面 —— 两个都要。
    /// </summary>
    public override void OnDrawSetting()
    {
        // ══════════════════════════════════════════════════════════════
        //  ★ 阶段日志 —— 定位"点设置就闪退"（实测 bug）★
        //
        //  [!] 为什么加：这个闪退推理了五次都没定位（Dispose / OnEnterRotation /
        //      Build / ACR身份 / 递归 全否）。`OnDrawSetting` 是**唯一的确定入口**
        //      （用户点设置就崩）—— 所以在每一步打日志，
        //      **崩溃前最后打出来的那一行就是崩溃点所在阶段**。
        //
        //  [!] 为什么要 try/catch：托管异常能被挡住并记下来（那就是普通 bug）；
        //      挡不住的是栈溢出（闪退的常见原因）——
        //      那种情况下"最后一行的阶段名"仍然有效。
        //
        //  [!] 为什么不直接二分回退版本：回退只能定位到"哪个提交"，
        //      而那个提交里有 5 处改动，还得再猜。日志能直接定位到**哪一行**。
        // ══════════════════════════════════════════════════════════════
        static void 记(string 阶段)
        {
            try { LogHelper.Info("[设置诊断] 进入阶段：" + 阶段); } catch { }
        }

        记("0-开始");
        try
        {
            记("1-base.OnDrawSetting（原版设置）");
            base.OnDrawSetting();
            记("1-完成");
        }
        catch (Exception e)
        {
            LogHelper.Error("[设置诊断] base.OnDrawSetting 抛异常：" + e);
            记("1-异常已捕获");
        }

        try
        {
            记("2-AiSettingPage.画");
            AiSettingPage.画();
            记("2-完成");
        }
        catch (Exception e)
        {
            LogHelper.Error("[设置诊断] AiSettingPage.画 抛异常：" + e);
            记("2-异常已捕获");
        }

        try
        {
            记("3-记忆库页面.画");
            记忆库页面.画();
            记("3-完成");
        }
        catch (Exception e)
        {
            LogHelper.Error("[设置诊断] 记忆库页面.画 抛异常：" + e);
            记("3-异常已捕获");
        }

        记("9-全部完成");
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
        //      加钩子只改 Ai层挂载.尝试挂载AI层() 一处，避免"漏改某个职业"的静默 bug。
        // ══════════════════════════════════════════════════════════════
        //  ★ 只在"我确实是当前 ACR"时才挂载 AI 层 ★
        //
        //  [!] 这是实测 bug 的根因（第三次修，前两次修错了地方）：
        //      `Build()` 在 **ACR 发现阶段** 对 **每一个** ACR 都跑 ——
        //      日志实证：
        //          16:46:28.845  加载全部ACR : D:/FF14/ACR        <- 扫描发现
        //          16:46:29.197  阈值钩子已挂载                    <- AI 层被挂载
        //          16:46:29.198  成功加载acr: BlueWhaleWhiteMageEntry
        //          16:46:29.597  LoadRotation Success: Shiyuvi Scholar  <- 用户选的 Shiyu
        //      => 不管选谁，小鲸鱼都会挂钩子 + **发一整轮 API 请求**。
        //
        //  [!] 为什么守卫放这里而不是 `OnEnterRotation`：
        //      重载时 `OnEnterRotation` **压根没被调**
        //      （日志里"进入职业循环"只出现过 1 次，且不在重载时刻）。
        //      而 `Build()` 是每次都会跑的那个点 —— 判身份后在这里做最稳。
        //
        //  [!] 不是当前 ACR 时什么都**不做**：
        //      不挂钩子、不发 API 请求、不上屏；只写一行日志便于以后核对。
        //      真正被选中时 `OnEnterRotation` 会补上。
        // ══════════════════════════════════════════════════════════════
        Ai层挂载.尝试挂载AI层();


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
    public override string Description => "BlueWhale.AutoHealerACR — 实验性项目：让 AI 接管占星的输出决策";

    /// <summary>
    /// ★ 必须 override ★
    /// 基类的 OnDrawSetting 只画 HealerACR 的设置，
    /// 不调 base 就丢原版界面，不调 AiSettingPage 就没有 AI 界面 —— 两个都要。
    /// </summary>
    public override void OnDrawSetting()
    {
        // ══════════════════════════════════════════════════════════════
        //  ★ 阶段日志 —— 定位"点设置就闪退"（实测 bug）★
        //
        //  [!] 为什么加：这个闪退推理了五次都没定位（Dispose / OnEnterRotation /
        //      Build / ACR身份 / 递归 全否）。`OnDrawSetting` 是**唯一的确定入口**
        //      （用户点设置就崩）—— 所以在每一步打日志，
        //      **崩溃前最后打出来的那一行就是崩溃点所在阶段**。
        //
        //  [!] 为什么要 try/catch：托管异常能被挡住并记下来（那就是普通 bug）；
        //      挡不住的是栈溢出（闪退的常见原因）——
        //      那种情况下"最后一行的阶段名"仍然有效。
        //
        //  [!] 为什么不直接二分回退版本：回退只能定位到"哪个提交"，
        //      而那个提交里有 5 处改动，还得再猜。日志能直接定位到**哪一行**。
        // ══════════════════════════════════════════════════════════════
        static void 记(string 阶段)
        {
            try { LogHelper.Info("[设置诊断] 进入阶段：" + 阶段); } catch { }
        }

        记("0-开始");
        try
        {
            记("1-base.OnDrawSetting（原版设置）");
            base.OnDrawSetting();
            记("1-完成");
        }
        catch (Exception e)
        {
            LogHelper.Error("[设置诊断] base.OnDrawSetting 抛异常：" + e);
            记("1-异常已捕获");
        }

        try
        {
            记("2-AiSettingPage.画");
            AiSettingPage.画();
            记("2-完成");
        }
        catch (Exception e)
        {
            LogHelper.Error("[设置诊断] AiSettingPage.画 抛异常：" + e);
            记("2-异常已捕获");
        }

        try
        {
            记("3-记忆库页面.画");
            记忆库页面.画();
            记("3-完成");
        }
        catch (Exception e)
        {
            LogHelper.Error("[设置诊断] 记忆库页面.画 抛异常：" + e);
            记("3-异常已捕获");
        }

        记("9-全部完成");
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
        //      加钩子只改 Ai层挂载.尝试挂载AI层() 一处，避免"漏改某个职业"的静默 bug。
        // ══════════════════════════════════════════════════════════════
        //  ★ 只在"我确实是当前 ACR"时才挂载 AI 层 ★
        //
        //  [!] 这是实测 bug 的根因（第三次修，前两次修错了地方）：
        //      `Build()` 在 **ACR 发现阶段** 对 **每一个** ACR 都跑 ——
        //      日志实证：
        //          16:46:28.845  加载全部ACR : D:/FF14/ACR        <- 扫描发现
        //          16:46:29.197  阈值钩子已挂载                    <- AI 层被挂载
        //          16:46:29.198  成功加载acr: BlueWhaleWhiteMageEntry
        //          16:46:29.597  LoadRotation Success: Shiyuvi Scholar  <- 用户选的 Shiyu
        //      => 不管选谁，小鲸鱼都会挂钩子 + **发一整轮 API 请求**。
        //
        //  [!] 为什么守卫放这里而不是 `OnEnterRotation`：
        //      重载时 `OnEnterRotation` **压根没被调**
        //      （日志里"进入职业循环"只出现过 1 次，且不在重载时刻）。
        //      而 `Build()` 是每次都会跑的那个点 —— 判身份后在这里做最稳。
        //
        //  [!] 不是当前 ACR 时什么都**不做**：
        //      不挂钩子、不发 API 请求、不上屏；只写一行日志便于以后核对。
        //      真正被选中时 `OnEnterRotation` 会补上。
        // ══════════════════════════════════════════════════════════════
        Ai层挂载.尝试挂载AI层();


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
    public override string Description => "BlueWhale.AutoHealerACR — 实验性项目：让 AI 接管贤者的输出决策";

    /// <summary>
    /// ★ 必须 override ★
    /// 基类的 OnDrawSetting 只画 HealerACR 的设置，
    /// 不调 base 就丢原版界面，不调 AiSettingPage 就没有 AI 界面 —— 两个都要。
    /// </summary>
    public override void OnDrawSetting()
    {
        // ══════════════════════════════════════════════════════════════
        //  ★ 阶段日志 —— 定位"点设置就闪退"（实测 bug）★
        //
        //  [!] 为什么加：这个闪退推理了五次都没定位（Dispose / OnEnterRotation /
        //      Build / ACR身份 / 递归 全否）。`OnDrawSetting` 是**唯一的确定入口**
        //      （用户点设置就崩）—— 所以在每一步打日志，
        //      **崩溃前最后打出来的那一行就是崩溃点所在阶段**。
        //
        //  [!] 为什么要 try/catch：托管异常能被挡住并记下来（那就是普通 bug）；
        //      挡不住的是栈溢出（闪退的常见原因）——
        //      那种情况下"最后一行的阶段名"仍然有效。
        //
        //  [!] 为什么不直接二分回退版本：回退只能定位到"哪个提交"，
        //      而那个提交里有 5 处改动，还得再猜。日志能直接定位到**哪一行**。
        // ══════════════════════════════════════════════════════════════
        static void 记(string 阶段)
        {
            try { LogHelper.Info("[设置诊断] 进入阶段：" + 阶段); } catch { }
        }

        记("0-开始");
        try
        {
            记("1-base.OnDrawSetting（原版设置）");
            base.OnDrawSetting();
            记("1-完成");
        }
        catch (Exception e)
        {
            LogHelper.Error("[设置诊断] base.OnDrawSetting 抛异常：" + e);
            记("1-异常已捕获");
        }

        try
        {
            记("2-AiSettingPage.画");
            AiSettingPage.画();
            记("2-完成");
        }
        catch (Exception e)
        {
            LogHelper.Error("[设置诊断] AiSettingPage.画 抛异常：" + e);
            记("2-异常已捕获");
        }

        try
        {
            记("3-记忆库页面.画");
            记忆库页面.画();
            记("3-完成");
        }
        catch (Exception e)
        {
            LogHelper.Error("[设置诊断] 记忆库页面.画 抛异常：" + e);
            记("3-异常已捕获");
        }

        记("9-全部完成");
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
        //      加钩子只改 Ai层挂载.尝试挂载AI层() 一处，避免"漏改某个职业"的静默 bug。
        // ══════════════════════════════════════════════════════════════
        //  ★ 只在"我确实是当前 ACR"时才挂载 AI 层 ★
        //
        //  [!] 这是实测 bug 的根因（第三次修，前两次修错了地方）：
        //      `Build()` 在 **ACR 发现阶段** 对 **每一个** ACR 都跑 ——
        //      日志实证：
        //          16:46:28.845  加载全部ACR : D:/FF14/ACR        <- 扫描发现
        //          16:46:29.197  阈值钩子已挂载                    <- AI 层被挂载
        //          16:46:29.198  成功加载acr: BlueWhaleWhiteMageEntry
        //          16:46:29.597  LoadRotation Success: Shiyuvi Scholar  <- 用户选的 Shiyu
        //      => 不管选谁，小鲸鱼都会挂钩子 + **发一整轮 API 请求**。
        //
        //  [!] 为什么守卫放这里而不是 `OnEnterRotation`：
        //      重载时 `OnEnterRotation` **压根没被调**
        //      （日志里"进入职业循环"只出现过 1 次，且不在重载时刻）。
        //      而 `Build()` 是每次都会跑的那个点 —— 判身份后在这里做最稳。
        //
        //  [!] 不是当前 ACR 时什么都**不做**：
        //      不挂钩子、不发 API 请求、不上屏；只写一行日志便于以后核对。
        //      真正被选中时 `OnEnterRotation` 会补上。
        // ══════════════════════════════════════════════════════════════
        Ai层挂载.尝试挂载AI层();


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
        // ══════════════════════════════════════════════════════════════
        //  ★ 阶段日志 —— 定位"点设置就闪退"（实测，已缩到此方法内）★
        //
        //  [!] 外面那层已经证明了崩溃点在这里：
        //        [设置诊断] 进入阶段：2-AiSettingPage.画   <- 最后一行
        //      没有 "2-完成"，也没有 "2-异常已捕获"
        //      => 崩在本方法内，且**不是托管异常**（原生 AV / 栈溢出）
        //      => 只能靠"最后进入的阶段"定位
        //
        //  [!] 为什么插这么密：原生 AV 抓不到堆栈，
        //      唯一可行的办法就是把范围一段段缩到单行。
        //      定位后这些日志会被删掉（它们是诊断用的，不是功能）。
        // ══════════════════════════════════════════════════════════════
        static void 段(string 名)
        {
            try { LogHelper.Info("[画诊断] " + 名); } catch { }
        }

        段("a-取 Instance");
        var s = AiSettings.Instance;
        段("a-完成");

        段("b-第一次 ImGui 调用");
        ImGui.Separator();
        ImGui.TextDisabled("════ BlueWhale AI 决策层（可选，不填也完全可用）════");

        // ---- API Key ----
        var key = s.ApiKey ?? "";
        ImGui.SetNextItemWidth(380);
        if (ImGui.InputText("DeepSeek API Key", ref key, 200)) s.ApiKey = key;
        // ★ 失焦即保存 ★ —— 不用再手动点「保存设置」，重载后 Key 还在
        if (ImGui.IsItemDeactivatedAfterEdit()) 保存并提示("API Key");
        ImGui.TextDisabled("  sk- 开头。留空 = 不启用 AI，走原版逻辑。只存本地 json。");
        段("c-API Key 段完成");

        // ---- 只有填了 Key 才显示后面的 ----
        // （按你的要求：提供了 api key 再选择模型）
        if (!s.已配置)
        {
            ImGui.TextDisabled("  ^ 填入 Key 后，下面会出现模型选择和开关。");
            return;
        }
        段("d-已配置，进入模型段");

        // ---- 模型选择 ----
        ImGui.Separator();
        ImGui.TextDisabled("模型（点一下切换，也可以直接手填）");

        // ══════════════════════════════════════════════════════════
        //  ⚠️ 模型名**会随版本变**，硬编码一定会过期 ——
        //     这里踩过一次真 bug：预设里写的是 `deepseek-pro`，
        //     而官方**没有这个模型名**（正确的是 `deepseek-v4-pro`），
        //     用户点了就 400。
        //
        //  ⇒ 所以：**优先用从 API 拉到的真实列表**，拉不到才退回内置预设。
        //     预设值只作兜底，不作为唯一依据。
        // ══════════════════════════════════════════════════════════
        var 模型列表 = DeepSeekClient.模型列表.Count > 0
            ? DeepSeekClient.模型列表.ToArray()
            : new[] { "deepseek-flash", "deepseek-v4-pro" };
        段("e-模型列表长度=" + 模型列表.Length);

        for (var i = 0; i < 模型列表.Length; i++)
        {
            段("e" + i + "-RadioButton " + (模型列表[i] ?? "(null)"));
            var 选中 = s.Model == 模型列表[i];
            if (ImGui.RadioButton(模型列表[i] ?? "(null)", 选中)) { s.Model = 模型列表[i]; AiSettings.保存(); }
            ImGui.SameLine();
        }
        段("e-模型循环完成");

        段("f1-NewLine 前");
        ImGui.NewLine();
        段("f2-NewLine 后 即将 TextDisabled 1");
        ImGui.TextDisabled("  deepseek-flash   = 快、便宜，适合高频决策（推荐先用这个）");
        段("f3-TextDisabled 1 后 即将 TextDisabled 2");
        ImGui.TextDisabled("  deepseek-v4-pro = 慢、贵、推理更强，适合低频策略");
        段("f4-两个 TextDisabled 完成");

        // ★ 从 API 拉真实列表 —— 根治"模型名会过期" ★
        段("g1-Button 前");
        if (ImGui.Button("从 API 拉取模型列表")) _ = DeepSeekClient.拉取模型列表();
        段("g2-Button 后 即将 SameLine");
        ImGui.SameLine();
        段("g3-SameLine 后 即将读 模型列表状态");
        var 状态串 = DeepSeekClient.模型列表状态 ?? "";
        段("g4-读到状态串 len=" + 状态串.Length);
        ImGui.TextDisabled(状态串);
        段("g5-状态串已画");

        // 自定义（模型名会变，留个口子）
        var 模型名 = s.Model ?? "";
        段("h1-SetNextItemWidth 前");
        ImGui.SetNextItemWidth(240);
        段("h2-InputText(模型名) 前");
        if (ImGui.InputText("模型名（可手填）", ref 模型名, 64)) s.Model = 模型名;
        段("h3-InputText(模型名) 后");
        if (ImGui.IsItemDeactivatedAfterEdit()) 保存并提示("模型名");
        段("h4-模型名段完成");
        ImGui.TextDisabled("  模型名随版本会变。如果上面两个都不通，去 DeepSeek 文档查当前名称填这里。");

        段("i1-InputText(接口地址) 前");
        ImGui.SetNextItemWidth(200);
        ImGui.InputText("接口地址", ref s.Endpoint, 200);
        段("i2-InputText(接口地址) 后");
        if (ImGui.IsItemDeactivatedAfterEdit()) 保存并提示("接口地址");
        ImGui.TextDisabled("  默认 https://api.deepseek.com");

        段("j1-SliderInt(超时) 前");
        ImGui.SetNextItemWidth(160);
        ImGui.SliderInt("超时（毫秒）", ref s.超时毫秒, 500, 10000);
        段("j2-SliderInt(超时) 后");
        if (ImGui.IsItemDeactivatedAfterEdit()) 保存并提示("超时");

        段("k1-Button(保存设置) 前");
        if (ImGui.Button("保存设置")) { AiSettings.保存(); LogHelper.Info("[BlueWhale.AI] 设置已保存"); }
        段("k2-Button(保存设置) 后");

        // ---- 两个阶段 ----
        段("T01-决策层开关 段");
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
        段("T02-安全阀 段");
        ImGui.Separator();
        ImGui.TextDisabled("安全阀（AI 失败绝不影响战斗）");

        ImGui.SetNextItemWidth(160);
        ImGui.SliderInt("连续失败上限", ref s.连续失败上限, 1, 10);
        ImGui.SetNextItemWidth(160);
        ImGui.SliderInt("失败冷却（秒）", ref s.失败冷却秒, 10, 300);

        段("T03-记录原始回复 前");
        ImGui.Checkbox("记录原始回复（调试）", ref s.记录原始回复);

        // ★ 调试模式 ★ —— 开启后 AI 日志直接显示在游戏里
        段("T04-调试模式 前");
        if (ImGui.Checkbox("调试模式（AI 日志上屏）", ref s.调试模式))
        {
            AiSettings.保存();
            if (s.调试模式) 屏幕提示.成功("调试模式已开启 —— 小鲸鱼的日志会直接显示在屏幕上", "dbg-on");
            else 屏幕提示.提示("小鲸鱼", "调试模式已关闭", "dbg-off");
        }
        ImGui.TextDisabled("  开启后 AI 的请求/回复/采纳都会打到屏幕上（日志文件照常写）");

        段("T05-解除熔断 前");
        if (ImGui.Button("解除熔断")) DeepSeekClient.解除熔断();

        ImGui.SameLine();

        // ★ 测试连接 —— 最直接的"AI 到底通不通"判断方式 ★
        段("T06-测试连接 前");
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
                    LogHelper.Error("    · 模型名写错（点『从 API 拉取模型列表』看真实可用的名字）");
                    LogHelper.Error("[BlueWhale.AI] 具体错误看上面那行『请求失败（...）』的原因。");
                    _测试结果 = "连接失败 —— 看日志最后几行（Key / 网络 / 余额 / 模型名）";
                    _测试中 = false;
                }
            });
        }

        // ---- 实时状态 ----
        段("T07-AI当前状态 段");
        ImGui.Separator();
        ImGui.Text("AI 当前状态");
        ImGui.Text($"  状态：{DeepSeekClient.状态描述()}    连续失败：{DeepSeekClient.连续失败数}");

        // ══════════════════════════════════════════════════════════════
        //  ── 最近的 AI 请求历史 ──
        //
        //  [!] 必须用**快照**，不能直接遍历公开集合 ★
        //
        //  实测闪退根因：这里原来写的是
        //      foreach (var 行 in AiStrategyLayer.历史)
        //  而那个 `历史` 是裸 `List<string>`，**后台线程正在 Add/RemoveAt**
        //  （AI 请求的 await 续体在别的线程上跑）。
        //    => 遍历走进损坏的内部状态 => **卡住（无响应）**
        //    => 最终原生访问违规（0xc0000005）=> **闪退**
        //       （绕过所有 try/catch，所以日志里什么都没有）
        //
        //  [!] 症状为什么是"先无响应一会儿再崩"：
        //      集合内部状态被写坏后，遍历会在坏掉的链表/索引上打转，
        //      所以先卡住；等原生层解引用到非法地址才崩。
        //
        //  => `历史快照()` 返回副本，遍历副本永远安全。
        // ══════════════════════════════════════════════════════════════
        段("T08-历史快照 前");
        var 历史 = AiStrategyLayer.历史快照();
        if (历史.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextDisabled("最近几次 AI 请求");

            foreach (var 行 in 历史)
            {
                ImGui.TextDisabled("  " + 行);
            }
        }

        段("T09-保存提示 前");
        if (_保存提示.Length > 0)
        {
            ImGui.TextDisabled("  " + _保存提示);
        }

        段("T10-测试结果 前");
        if (_测试中 || _测试结果.Length > 0)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.9f, 0.3f, 1f), "  " + _测试结果);
        }
        段("T11-阈值适配器状态 前");
        ImGui.TextWrapped("  " + AiThresholdAdapter.状态描述());

        ImGui.TextDisabled($"  策略层：启用={s.启用策略层}  刷新中={AiStrategyLayer.刷新中}  成功={AiStrategyLayer.成功次数} 次");
        ImGui.TextDisabled($"  上次结果：{AiStrategyLayer.上次结果}");

        段("T12-当前建议 前");
        var 建议 = AiDecisionLayer.当前建议;
        if (建议 != null)
        {
            ImGui.Text($"  决策建议：技能 {建议.技能Id}（{建议.理由}）");
        }
        else
        {
            ImGui.TextDisabled("  决策建议：无");
        }

        段("T13-建议显示完成");

        // ══════════════════════════════════════════════════════════════
        //  ★ 阶段 B 命中率统计（实测要看的一屏）★
        //
        //    旧版只显示「命中 X / 过期 Y」，看不出**为什么**没命中 ——
        //    所以装上实测时只能看到一个难看的数字，不知道该调哪里。
        //    现在把"被终审拦下的原因"也列出来，一次就能定位。
        //
        //  [!] 逐句标记 —— 定位"点设置后卡 16 秒再崩" ★
        //      实测：`T13` 打出后 **16 秒**才崩（17:13:10 → 17:13:26），
        //      而 `2.5-AiSettingPage 已返回` 从未打印
        //      => 卡在下面这几行里面。它们都在读**共享状态**
        //      （`AiDecisionLayer` 的队列/字典、`局面监控` 的状态），
        //      而后台线程（AI 请求的续体）正在写这些 —— 高度怀疑是**死锁**。
        // ══════════════════════════════════════════════════════════════
        段("U1-Separator 前");
        ImGui.Separator();
        段("U2-标题前");
        ImGui.TextDisabled("阶段 B 命中率统计");

        段("U3-AiDecisionLayer.状态描述() 前");
        var 决策状态 = AiDecisionLayer.状态描述();
        段("U4-AiDecisionLayer.状态描述() 后");
        ImGui.TextWrapped("  " + 决策状态);

        段("U5-AiDecisionLayer.拦截摘要() 前");
        var 拦截 = AiDecisionLayer.拦截摘要();
        段("U6-AiDecisionLayer.拦截摘要() 后");
        ImGui.TextWrapped("  被终审拦下：" + 拦截);

        段("U7-排队过期条数 前");
        var 排队过期 = AiDecisionLayer.排队过期条数;
        段("U8-排队过期条数 后=" + 排队过期);
        ImGui.TextDisabled($"  其中排在队列第 2 位之后才过期的：{排队过期} 条");

        段("U9-局面监控.状态描述() 前");
        var 局面 = 局面监控.状态描述();
        段("U10-局面监控.状态描述() 后");
        ImGui.TextDisabled("  " + 局面);

        段("U11-说明行 前");
        ImGui.TextDisabled("  说明：『过期』= 生成后在队列里放着没人用就超时了；");
        ImGui.TextDisabled("        命中率 = 命中 /(命中 + 过期)，被局面剧变清空的条数不计入分母。");
        段("U12-全部完成，即将 return");
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
        //      加钩子只改 Ai层挂载.尝试挂载AI层() 一处，避免"漏改某个职业"的静默 bug。
        // ══════════════════════════════════════════════════════════════
        //  ★ 只在"我确实是当前 ACR"时才挂载 AI 层 ★
        //
        //  [!] 这是实测 bug 的根因（第三次修，前两次修错了地方）：
        //      `Build()` 在 **ACR 发现阶段** 对 **每一个** ACR 都跑 ——
        //      日志实证：
        //          16:46:28.845  加载全部ACR : D:/FF14/ACR        <- 扫描发现
        //          16:46:29.197  阈值钩子已挂载                    <- AI 层被挂载
        //          16:46:29.198  成功加载acr: BlueWhaleWhiteMageEntry
        //          16:46:29.597  LoadRotation Success: Shiyuvi Scholar  <- 用户选的 Shiyu
        //      => 不管选谁，小鲸鱼都会挂钩子 + **发一整轮 API 请求**。
        //
        //  [!] 为什么守卫放这里而不是 `OnEnterRotation`：
        //      重载时 `OnEnterRotation` **压根没被调**
        //      （日志里"进入职业循环"只出现过 1 次，且不在重载时刻）。
        //      而 `Build()` 是每次都会跑的那个点 —— 判身份后在这里做最稳。
        //
        //  [!] 不是当前 ACR 时什么都**不做**：
        //      不挂钩子、不发 API 请求、不上屏；只写一行日志便于以后核对。
        //      真正被选中时 `OnEnterRotation` 会补上。
        // ══════════════════════════════════════════════════════════════
        Ai层挂载.尝试挂载AI层();


        return rot;
    }

    public override void Dispose()
    {
        Ai层挂载.卸载();                         // 卸载，避免影响其他 ACR
        base.Dispose();
    }
}
}
