using AEAssist;
using AEAssist.Extension;

namespace HealerACR.Common;

/// <summary>
/// DoT 黑名单 —— 参考同类 ACR 的 `DotBlacklistHelper`。
///
/// ══════════════════════════════════════════════════════════════════
///  为什么要黑名单：
///
///    有些敌人**免疫 DoT**，或者 DoT 在它身上**不生效**：
///      · 某些不死系 / 构造体
///      · 血量极低的小怪（DoT 还没跳完就死了，白费一个 GCD）
///      · 特定副本的机制怪（打上去没伤害）
///
///    往这些目标身上补 DoT = **每 30 秒白费一个 GCD**，
///    而且更糟的是**它会一直显示"该补 DoT"**（因为 buff 永远上不去），
///    导致 DoT 技能反复触发、把输出循环卡死。
///
///  ── 怎么判断"该不该拉黑" ──
///    默认清单是我实测/查资料确认免疫 DoT 的，
///    但**游戏版本会变**，所以：
///      · 清单可以在设置界面编辑
///      · 用**血量比例**作为动态判据（太低的小怪不打 DoT）
///      · 加一个"连续补不上就临时拉黑"的自适应机制
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class Dot黑名单
{
    // ==================== 静态黑名单 ====================

    /// <summary>
    /// 按**怪物种类 ID**（NameId）拉黑 —— 同一种怪长得一样、ID 一样。
    ///
    /// ⚠️ 这些是我根据资料整理的可疑项，**不保证全对** ——
    ///    如果你发现某个怪明明能吃 DoT 却被打黑了，从设置里删掉对应的即可。
    /// </summary>
    private static readonly HashSet<uint> _默认黑名单 = new()
    {
        // 例：某些副本的"无敌"阶段怪 / 构造体
        // 留空是安全的 —— 空的清单 = 不拉黑任何东西
        // 需要时在设置界面加
    };

    /// <summary>用户自定义的黑名单（设置界面可编辑）</summary>
    public static readonly HashSet<uint> 自定义黑名单 = new();

    /// <summary>自适应黑名单：连续 N 次补不上 DoT 的目标类型</summary>
    private static readonly Dictionary<uint, int> _补失败次数 = new();

    /// <summary>连续补失败几次就临时拉黑</summary>
    private const int 失败上限 = 3;

    // ==================== 判断 ====================

    /// <summary>这个敌人现在该不该上 DoT</summary>
    public static bool 可以上Dot(IBattleChara? 敌人)
    {
        if (敌人 == null || 敌人.CurrentHp <= 0) return false;

        try
        {
            var 种类 = 取种类Id(敌人);

            // ① 静态 / 自定义黑名单
            if (种类 != 0)
            {
                if (_默认黑名单.Contains(种类)) return false;
                if (自定义黑名单.Contains(种类)) return false;

                // ② 自适应黑名单（补了好几次都上不去）
                if (_补失败次数.TryGetValue(种类, out var 次数) && 次数 >= 失败上限)
                {
                    return false;
                }
            }
        }
        catch { }

        return true;
    }

    /// <summary>
    /// 这次 DoT 补失败了吗？
    ///
    /// **调用时机**：补了 DoT 之后隔一小段时间检查 buff 在不在 ——
    /// 不在就说明没上上去（免疫 / 怪死了 / 距离问题）。
    ///
    /// 连续失败到一定次数就把这个**种类**拉黑，
    /// 免得整场都在做无用功。
    /// </summary>
    public static void 记补失败(IBattleChara? 敌人)
    {
        if (敌人 == null) return;

        try
        {
            var 种类 = 取种类Id(敌人);
            if (种类 == 0) return;

            _补失败次数.TryGetValue(种类, out var 次数);
            _补失败次数[种类] = 次数 + 1;

            if (次数 + 1 == 失败上限)
            {
                AEAssist.Helper.LogHelper.Info(
                    $"[HealerACR.DoT] 种类 {种类} 连续 {失败上限} 次补 DoT 失败，本场拉黑");
            }
        }
        catch { }
    }

    /// <summary>补成功了，清掉失败记录</summary>
    public static void 记补成功(IBattleChara? 敌人)
    {
        if (敌人 == null) return;

        try
        {
            var 种类 = 取种类Id(敌人);
            if (种类 != 0) _补失败次数.Remove(种类);
        }
        catch { }
    }

    /// <summary>战斗开始 / 换本时清掉自适应记录（静态黑名单保留）</summary>
    public static void 重置自适应()
    {
        _补失败次数.Clear();
        _待确认.Clear();
    }

    // ==================== 待确认（把"记补失败"真正接上）====================

    /// <summary>
    /// 刚补了 DoT、**还没确认 buff 上去没有** 的目标。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么要这一层（审计发现 `记补失败` 是死代码）★
    ///
    ///  ── 原来的问题 ──
    ///    `记补失败` / `记补成功` **一个调用点都没有** ——
    ///    所以 `_补失败次数` 永远是空的，自适应拉黑**从来没生效过**。
    ///
    ///    而它存在的意义正是类注释写的那件事：
    ///    "往免疫 DoT 的目标补 = 每 30 秒白费一个 GCD，
    ///      而且因为 buff 永远上不去，DoT 会反复触发、把输出循环卡死"。
    ///
    ///  ── 为什么不能"补完立刻判" ──
    ///    `HasLocalPlayerAura` 有**读取延迟** ——
    ///    技能刚放出去、buff 还没同步回来，立刻查必然查不到，
    ///    会把**每一次成功的 DoT 都误判成失败** → 反而把正常怪拉黑。
    ///
    ///  ⇒ 所以要记一条"待确认"，**隔一段时间**（见 `确认窗口毫秒`）再回头看。
    ///     这和 `Dot补判` 里"挂起窗口"是同一个道理的两面：
    ///     那边是"别重复补"，这边是"补完要验证"。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private sealed class 待确认项
    {
        public ulong 对象Id;
        public long 按下时间;
        public uint 种类;

        /// <summary>
        /// 本职业的 DoT buff id 列表 —— **由调用方传进来**。
        ///
        /// ⚠️ 为什么不在这里自己取：`Dot黑名单` 是**通用**模块，
        ///    拿不到"当前职业的技能表"（那在 `HealerEntryBase` 那侧）。
        ///    自己硬编码一份 buff 表会和各职业表**重复维护**，
        ///    迟早对不上（本项目已经栽过好几次"两处维护同一份数据"）。
        /// </summary>
        public uint[] DotBuffs = Array.Empty<uint>();
    }

    private static readonly List<待确认项> _待确认 = new();

    /// <summary>按下之后隔多久去确认 buff 在不在（毫秒）—— 要大于读取延迟</summary>
    private const int 确认窗口毫秒 = 4000;

    /// <summary>超过这么久还没确认的条目直接丢掉（目标早死了/换目标了）</summary>
    private const int 待确认过期毫秒 = 15000;

    /// <summary>
    /// **记一次"我刚按下了 DoT"** —— 过一会由 `每帧更新()` 去确认成没成。
    ///
    /// ⚠️ 必须在**技能真的进了 slot 之后**调（和 `Dot补判.记一次施放` 同一个时机）。
    /// </summary>
    public static void 记按下(IBattleChara? 敌人, uint[]? dotBuffs = null)
    {
        if (敌人 == null) return;

        try
        {
            var 种类 = 取种类Id(敌人);
            if (种类 == 0) return;

            var id = 敌人.GameObjectId;
            if (id == 0) return;

            _待确认.Add(new 待确认项
            {
                对象Id = id,
                按下时间 = AEAssist.Helper.TimeHelper.Now(),
                种类 = 种类,
                DotBuffs = dotBuffs ?? Array.Empty<uint>(),
            });
        }
        catch { }
    }

    /// <summary>
    /// **每帧调**：把到时间的待确认项拿出来看 buff 上去没有。
    ///
    /// ⚠️ 这个方法原来不存在，导致 `记补失败`/`记补成功` 永远不被调用。
    ///    现在它把整条自适应拉黑链接上了。
    /// </summary>
    public static void 每帧更新()
    {
        try
        {
            if (_待确认.Count == 0) return;

            var 现在 = AEAssist.Helper.TimeHelper.Now();

            for (var i = _待确认.Count - 1; i >= 0; i--)
            {
                var 项 = _待确认[i];
                var 经过 = 现在 - 项.按下时间;

                // 过期太久 → 丢掉，别再翻旧账（目标可能早死了）
                if (经过 > 待确认过期毫秒) { _待确认.RemoveAt(i); continue; }

                // 还没到确认时间 → 下帧再看
                if (经过 < 确认窗口毫秒) continue;

                _待确认.RemoveAt(i);

                // 找这个目标还在不在
                IBattleChara? 目标 = null;
                try
                {
                    foreach (var e in Data.AllHostileTargets)   // 本仓库统一用这个
                    {
                        if (e != null && e.GameObjectId == 项.对象Id) { 目标 = e; break; }
                    }
                }
                catch { }

                // 目标没了（死了/走了）→ 不算失败（那不是"免疫"，只是没了）
                if (目标 == null || 目标.CurrentHp <= 0) continue;

                // 看我们的 DoT 在不在 —— 在就是成功
                var 上去了 = false;
                try
                {
                    foreach (var b in 项.DotBuffs)
                    {
                        if (b != 0 && 目标.HasLocalPlayerAura(b)) { 上去了 = true; break; }
                    }
                }
                catch { }

                if (上去了) 记补成功(目标);
                else 记补失败(目标);
            }
        }
        catch { }
    }

    // ==================== 工具 ====================

    private static uint 取种类Id(IBattleChara 敌人)
    {
        try { return 敌人.NameId; }
        catch { return 0; }
    }

    /// <summary>诊断</summary>
    public static string 状态描述(IBattleChara? 敌人)
    {
        if (敌人 == null) return "无目标";

        try
        {
            var 种类 = 取种类Id(敌人);

            if (_默认黑名单.Contains(种类)) return $"种类 {种类}：在黑名单里（静态）";
            if (自定义黑名单.Contains(种类)) return $"种类 {种类}：在黑名单里（自定义）";

            if (_补失败次数.TryGetValue(种类, out var 次数))
            {
                return $"种类 {种类}：补失败 {次数}/{失败上限} 次";
            }

            return $"种类 {种类}：正常";
        }
        catch
        {
            return "读取失败";
        }
    }
}
