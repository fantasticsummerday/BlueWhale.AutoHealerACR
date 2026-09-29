using AEAssist;
using AEAssist.Helper;
using HealerACR.Common;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// AI 初始化 —— 每次启用 ACR 时，让 AI 先把当前局面完整过一遍。
///
/// ══════════════════════════════════════════════════════════════════
///  为什么需要"初始化"而不是直接开始跑：
///
///    阶段 A（策略层）平时是每 10 秒刷一次，但**刚加载时没有任何结论** ——
///    那段时间里 当前倾向 = 未知，阈值偏移 = 0，AI 等于没起作用。
///
///    如果什么都不做，用户会遇到：
///      · 加载后前 10 秒 AI 是"哑"的
///      · 面板上显示"还没请求过"，看起来像没配好
///
///    初始化做的事：**加载后立刻跑一次完整请求**，
///    把副本 / 队伍 / 资源 / 敌人全部喂进去，拿到第一个倾向结论。
///
///  ── 用屏幕横幅告知结果 ──
///
///    这是用户明确要求的：不想翻日志，就想在游戏里看到一行字。
///    所以：
///      · 成功 → "AI 已就绪：倾向 XXX"
///      · 失败 → "AI 初始化失败：<原因>"
///      · 熔断 → "AI 已熔断，期间走原版逻辑"
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class Ai初始化
{
    /// <summary>正在初始化</summary>
    public static bool 进行中 { get; private set; }

    /// <summary>有没有完成过一次</summary>
    public static bool 已完成 { get; private set; }

    /// <summary>上次初始化的结果描述</summary>
    public static string 结果 { get; private set; } = "（还没初始化）";

    private static long 开始时间;

    /// <summary>超时（毫秒）—— 超过就当失败，不让用户干等</summary>
    private const int 超时 = 15000;

    /// <summary>
    /// 启动初始化（入口 Build 时调）。
    /// </summary>
    public static void 开始()
    {
        try
        {
            var s = AiSettings.Instance;

            // 没配 Key → 不初始化，也不打扰用户
            if (!s.已配置)
            {
                结果 = "未配置 API Key（走原版逻辑）";
                return;
            }

            // 两个阶段都没开 → 同样不打扰
            if (!s.启用策略层 && !s.启用决策层)
            {
                结果 = "AI 未启用（走原版逻辑）";
                return;
            }

            if (进行中) return;

            进行中 = true;
            已完成 = false;
            开始时间 = TimeHelper.Now();
            结果 = "初始化中…";

            Ai调试.日志("开始初始化 —— 让 AI 先把当前局面过一遍");
            屏幕提示.提示("🐋 小鲸鱼", "初始化中…", "ai-init-start");

            _ = 跑一次();
        }
        catch (Exception e)
        {
            进行中 = false;
            结果 = "初始化异常：" + e.Message;
            Ai调试.错误("初始化异常：" + e.Message);
        }
    }

    private static async Task 跑一次()
    {
        try
        {
            // 直接走策略层的刷新逻辑 —— 它采集的情境最完整
            // （副本 / 时间轴 / 自己 / 资源 / 队友 / 敌人 / 可选技能）
            var 局面 = AiSituation.采集();

            var 回复 = await DeepSeekClient.提问(
                "这是一次初始化。请根据下面的局面给出你对该场战斗的策略倾向。",
                局面,
                超时).ConfigureAwait(false);

            已完成 = true;

            if (回复 != null)
            {
                结果 = "就绪";
                Ai调试.日志("初始化完成");

                屏幕提示.成功("初始化成功", "ai-init-done");
            }
            else
            {
                结果 = DeepSeekClient.熔断中 ? "熔断中（走原版逻辑）" : "请求失败（走原版逻辑）";
                Ai调试.日志("初始化未拿到回复：" + 结果);

                // ★ 失败时必须给出**具体原因** ★
                //   只说"失败了"用户没法排查 —— 得让他知道
                //   是 Key 错、网络不通、超时、还是模型名不对。
                var 原因 = string.IsNullOrWhiteSpace(DeepSeekClient.上次失败原因)
                    ? "未知（详见日志 [BlueWhale.AI]）"
                    : DeepSeekClient.上次失败原因;

                屏幕提示.警告($"初始化失败：{原因}", "ai-init-fail");
            }
        }
        catch (Exception e)
        {
            已完成 = true;
            结果 = "初始化异常：" + e.Message;
            Ai调试.错误("初始化异常：" + e.Message);

            屏幕提示.警告($"初始化失败：{e.Message}", "ai-init-ex");
        }
        finally
        {
            进行中 = false;
        }
    }

    /// <summary>每帧检查超时</summary>
    public static void 每帧更新()
    {
        try
        {
            if (!进行中) return;
            if (TimeHelper.Now() - 开始时间 < 超时) return;

            进行中 = false;
            已完成 = true;
            结果 = "初始化超时（走原版逻辑）";
            Ai调试.日志("初始化超时");

            屏幕提示.警告("初始化失败：超时（15 秒没响应）", "ai-init-timeout");
        }
        catch { }
    }

    /// <summary>熔断状态变化时提示（由心跳调用）</summary>
    private static bool _上次熔断;

    public static void 检查熔断提示()
    {
        try
        {
            var 现在熔断 = DeepSeekClient.熔断中;
            if (现在熔断 == _上次熔断) return;

            _上次熔断 = 现在熔断;

            if (现在熔断)
            {
                // 熔断也要说清原因 —— 和初始化失败一个道理
                var 原因 = string.IsNullOrWhiteSpace(DeepSeekClient.上次失败原因)
                    ? "未知"
                    : DeepSeekClient.上次失败原因;

                屏幕提示.警告($"已熔断：{原因}", "ai-fuse-on");
            }
            else
            {
                屏幕提示.成功("熔断已解除", "ai-fuse-off");
            }
        }
        catch { }
    }

    /// <summary>重置（换本 / 手动重载）</summary>
    public static void 重置()
    {
        进行中 = false;
        已完成 = false;
        结果 = "（还没初始化）";
        _上次熔断 = false;
        屏幕提示.重置();
    }
}
