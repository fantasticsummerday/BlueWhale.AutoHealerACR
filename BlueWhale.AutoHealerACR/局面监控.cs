using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// 局面监控 —— **给 <see cref="AiDecisionLayer.局面剧变"/> 补上触发点。**
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么需要这个类 ★
///
///    `AiDecisionLayer.局面剧变()` 早就写好了（清空预取队列），
///    注释里也写明了判据是"换目标 / 血量骤降 / 进战脱战"——
///    但**没有任何地方调用它**，等于整段逻辑是死的。
///
///    这是个很典型的"静默失效"：代码能编译、不报错、不影响运行，
///    就是不做任何事。光看 `AiDecisionLayer` 自己看不出来 ——
///    必须**从调用点反查**才会发现"写了却没人调"。
///
///  ── 为什么不在 Check 里判，要单独一个类 ──
///
///    判据需要"上一帧的值"来对比（血量、目标、战斗状态），
///    这就是**状态**。而按开发约定，带状态的模块必须：
///      · 在 OnResetBattle 清一次
///      · 在 OnTerritoryChanged 清一次
///    集中在这一个类里，清理点只有一处，不容易漏。
///
///  ── 为什么会误触发也没关系 ──
///
///    清空队列的代价只是"重新问一次 AI"，
///    而**留着过期建议的代价是"按错的技能"** —— 后者严重得多。
///    所以阈值取得偏灵敏（见下面的常量）。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 局面监控
{
    /// <summary>一个人血量在单帧内掉超过这么多 → 判"骤降"</summary>
    private const float 骤降阈值 = 0.12f;

    /// <summary>两次剧变之间至少隔这么久（毫秒）—— 防止拉群怪时疯狂重取</summary>
    private const long 冷却毫秒 = 2000;

    private static long _上次剧变;

    /// <summary>上一帧看的"队伍最低血量"。-1 = 还没有基准</summary>
    private static float _上次最低血量 = -1f;

    /// <summary>上一帧的当前目标 ID（0 = 没目标）</summary>
    private static uint _上次目标;

    /// <summary>上一帧是否在战斗中</summary>
    private static bool _上次战斗中;

    /// <summary>最近一次剧变的原因（给调试面板看）</summary>
    public static string 最近原因 { get; private set; } = "（还没有过）";

    /// <summary>累计剧变次数</summary>
    public static int 剧变次数 { get; private set; }

    /// <summary>
    /// 每帧调用（挂在 <c>AiHeartbeat</c> 的心跳里）。
    ///
    /// 顺序有意安排：**先算出基准值，再判是否剧变** ——
    /// 反过来的话第一次调用会因为"没有上一帧"而误判。
    /// </summary>
    public static void 每帧更新()
    {
        try
        {
            var 战斗现在 = Core.Me.InCombat();

            // ① 战斗状态变化：脱战 → 进战（这是最强的"局面变了"信号）
            //    脱战方向不用管 —— OnResetBattle 已经会清一次了。
            if (战斗现在 && !_上次战斗中)
            {
                _上次战斗中 = 战斗现在;
                基准归零();          // 进战瞬间血量/目标都要重新取基准
                触发("进战");
                return;
            }
            _上次战斗中 = 战斗现在;

            if (!战斗现在)
            {
                基准归零();
                return;              // 不在战斗里不用管（也没队列要维护）
            }

            var 现在 = TimeHelper.Now();

            // ② 当前目标换了
            //    预取的建议是"针对某个目标"给的（打谁、给谁上 DoT），
            //    目标一换，队列里剩下的建议很可能全是给旧目标的。
            var 目标 = HealTargetHelper.当前目标();
            var 目标Id = 目标?.EntityId ?? 0;

            if (目标Id != _上次目标)
            {
                var 从有到有 = _上次目标 != 0 && 目标Id != 0;
                _上次目标 = 目标Id;

                // 只在"从一个目标换到另一个目标"时算剧变。
                // 从无到有 / 从有到无 不算 —— 那是很常见的瞬间状态
                // （目标死了、切视角），不值得重取。
                if (从有到有 && 现在 - _上次剧变 >= 冷却毫秒)
                {
                    基准归零();
                    触发("目标切换");
                    return;
                }
            }

            // ③ 队伍血量骤降
            var 最低 = 队伍最低血量();
            if (最低 < 0f) return;

            if (_上次最低血量 >= 0f)
            {
                var 跌幅 = _上次最低血量 - 最低;

                // 跌幅超过阈值 = 有人突然吃了一大口伤害
                // 注意：HoT 回血造成的"上涨"不算剧变，所以只判跌幅
                if (跌幅 >= 骤降阈值 && 现在 - _上次剧变 >= 冷却毫秒)
                {
                    // ⚠️ 顺序要紧：**先用旧值拼好文案，再更新基准**。
                    //    反过来写的话 _上次最低血量 已经被覆盖成新值，
                    //    日志里的跌幅会永远显示成 0%（自己踩过一次）。
                    var 旧 = _上次最低血量;
                    _上次最低血量 = 最低;

                    触发($"血量骤降 {跌幅 * 100f:F0}%（{旧 * 100f:F0}% -> {最低 * 100f:F0}%）");
                    return;
                }
            }

            _上次最低血量 = 最低;
        }
        catch
        {
            // 监控本身绝不能影响战斗
        }
    }

    /// <summary>真正触发：清空 + 立刻重取 + 记录</summary>
    private static void 触发(string 原因)
    {
        try
        {
            _上次剧变 = TimeHelper.Now();
            剧变次数++;
            最近原因 = 原因;

            Ai调试.日志($"局面剧变：{原因} -> 作废预取队列");

            // 用"并重取"而不是"只清空"：
            // 只清空的话还要等预取节流过去才补货，
            // 而剧变之后那几秒正是最需要建议的时候。
            AiDecisionLayer.局面剧变并重取();
        }
        catch { }
    }

    /// <summary>取队伍里的最低血量比例；取不到返回 -1</summary>
    private static float 队伍最低血量()
    {
        try
        {
            var 最低 = 2f;
            var 有 = false;

            // 自己也算一个 —— 奶妈自己吃伤害同样影响决策
            var 我 = Core.Me.CurrentHp * 1f / Math.Max(1u, Core.Me.MaxHp);
            最低 = 我;
            有 = true;

            var 队友 = PartyHelper.CastableAlliesWithin30;
            if (队友 != null)
            {
                foreach (var r in 队友)
                {
                    if (r == null) continue;
                    var 比例 = r.CurrentHp * 1f / Math.Max(1u, r.MaxHp);
                    if (比例 < 最低) 最低 = 比例;
                    有 = true;
                }
            }

            return 有 ? Math.Clamp(最低, 0f, 1f) : -1f;
        }
        catch
        {
            return -1f;
        }
    }

    private static void 基准归零()
    {
        _上次最低血量 = -1f;
        _上次目标 = 0;
    }

    /// <summary>战斗重置 / 换本时清（配合 状态重置钩子）</summary>
    public static void 重置()
    {
        try
        {
            _上次剧变 = 0;
            _上次战斗中 = false;
            基准归零();
            最近原因 = "（还没有过）";
        }
        catch { }
    }

    /// <summary>一行状态（设置页 / 调试用）</summary>
    public static string 状态描述()
        => $"局面剧变 {剧变次数} 次｜最近：{最近原因}";
}
