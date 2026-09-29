using System.Text;
using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;
using HealerACR.Timeline;

namespace BlueWhale.AutoHealerACR;

/// <summary>
/// 局面采集 —— 把"游戏里实际发生了什么"翻译成 AI 能读的文字。
///
/// **这是整个 AI 接管的地基。**
/// 喂给 AI 的信息越全，它的决策越靠谱；信息不够它就只能在幻觉里编。
///
/// 采集范围对照 HealerACR 的调试页和 同类 ACR 的 Overlay.DrawDev ——
/// 那些面板上能看到的东西，AI 也应该能看到。
/// </summary>
public static class AiSituation
{
    /// <summary>
    /// 局面描述的最大长度（字符）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么需要上限 ★
    ///
    ///    这个字符串**每轮都要全量重发**（初始化 / 策略层 / 决策层），
    ///    而且现在包含：技能清单（带 CD/充能/蓝耗）+ 类别说明
    ///    + 每个队友的状态 + 敌人身上的状态 —— 内容比原来多了一大截。
    ///
    ///    太长会：① 占 token（花钱、变慢）
    ///            ② 把真正重要的**动态状态**淹在静态说明里
    ///               （模型对长提示词的中间部分注意力最差）
    ///
    ///  ★ 截断策略：**砍静态、保动态** ★
    ///
    ///    超长时优先截掉「技能类别说明」那一段 —— 它是**每轮都一样**的
    ///    静态参考，信息量最低；而血量 / 状态剩余时间 / 敌人 这些
    ///    **每轮都在变**的东西必须保住。
    ///
    ///    （如果连砍掉静态部分都还超长，就按总长硬截，
    ///      并在末尾标明"已截断"——绝不静默截断。）
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private const int 最大长度 = 6000;

    /// <summary>静态段落的起始标记（超长时从它开始砍）</summary>
    private const string 静态段标记 = "【技能类别说明";

    public static string 采集()
    {
        var sb = new StringBuilder();

        采副本(sb);
        采自己(sb);
        采资源(sb);
        采队友(sb);
        采必须奶满(sb);      // ★ 致死机制：必须放在队友之后、敌人之前 —— 它是最高优先信息
        采敌人(sb);
        采坦克压力(sb);
        采时间轴(sb);
        采可选技能(sb);
        采记忆库(sb);        // ★ 放最后：它是"参考"，前面才是"当前事实"

        return 控制长度(sb.ToString());
    }

    // ==================== 记忆库 ====================

    /// <summary>
    /// 把「记忆库」里与当前局面相关的结论拼进提示词。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 这是用户要的最后一环 ★
    ///
    ///    "每次初始化的时候先提供正常提示词然后要提供记忆库优化ai逻辑"
    ///
    ///    所以顺序是：**先当前局面（正常提示词），后记忆库（参考）**。
    ///    这个顺序不能反 —— 当前事实永远是第一位的，
    ///    记忆只是"过去人类怎么打的"，局面不同时必须以当下为准。
    ///
    ///  ── 放在这里而不是只放初始化 ──
    ///
    ///    这个方法被三处调用：初始化 / 策略层 / 决策层。
    ///    放在这里意味着**每一轮都带记忆**，不只是初始化那一次 ——
    ///    因为策略层和决策层才是真正做判断的地方，
    ///    只在初始化给一次的话，那一次之后就"忘"了。
    ///
    ///  ⚠️ 检索和拼接的逻辑在 `记忆库.取相关记忆`，
    ///     包括"职业必须相同 / 等级±10 / 同副本类型优先"这些规则。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static void 采记忆库(StringBuilder sb)
    {
        try
        {
            var 职业 = "未知";
            var 等级 = 1;
            try { 职业 = Core.Me.ClassJob.Value.Name.ToString(); } catch { }
            try { 等级 = Data.PlayerCurrentLevel; } catch { }

            var 记忆 = 记忆库.取相关记忆(职业, 等级, 对局记录.当前副本类型());
            if (string.IsNullOrWhiteSpace(记忆)) return;   // 没有相关记忆 → 不占篇幅

            sb.AppendLine(记忆);
        }
        catch { }
    }

    /// <summary>按 <see cref="最大长度"/> 控制提示词体积 —— 先砍静态，再硬截</summary>
    private static string 控制长度(string 全文)
    {
        if (全文.Length <= 最大长度) return 全文;

        // ① 先砍静态段（每轮一样的说明，信息量最低）
        var 切口 = 全文.IndexOf(静态段标记, StringComparison.Ordinal);
        if (切口 > 0)
        {
            var 砍掉静态 = 全文.Substring(0, 切口)
                           + "（技能类别说明已省略：本轮提示词过长，只保留动态局面）\n";

            if (砍掉静态.Length <= 最大长度) return 砍掉静态;

            全文 = 砍掉静态;
        }

        // ② 还超长就硬截，但**必须标明**（静默截断会让 AI 以为"就这些"）
        return 全文.Substring(0, 最大长度)
               + $"\n（⚠️ 局面描述超长已截断，上面只到 {最大长度} 字符；"
               + "如信息不足请输出 0|数据不足）\n";
    }

    // ==================== 必须奶满（致死机制） ====================

    /// <summary>
    /// 采集「必须奶满」类机制。
    ///
    /// ⚠️ **必须明确告诉 AI"这不是按血线判断的"** ——
    ///    否则它会按常识回"目标血量健康，不需要治疗"，
    ///    而那正是这个机制最危险的地方（血看着健康，时间一到直接死）。
    ///
    /// 没有命中时**什么都不输出**（不留空段落污染提示词）。
    /// </summary>
    private static void 采必须奶满(StringBuilder sb)
    {
        try
        {
            var 文本 = 必须奶满.状态描述();
            if (string.IsNullOrWhiteSpace(文本)) return;   // 没有就不占篇幅

            sb.Append(文本);
            sb.AppendLine();
        }
        catch (Exception e)
        {
            sb.AppendLine("（必须奶满机制读取失败：" + e.Message + "）");
            sb.AppendLine();
        }
    }

    // ==================== 副本 ====================

    /// <summary>
    /// 采集"现在在哪个副本"。
    ///
    /// ⚠️ **只给事实，不给推测。**
    ///    副本 ID 和名字是游戏数据，AI 可以据此知道"这是哪个本"，
    ///    但**绝不能让它去"回忆"这个本的机制** —— 那是幻觉的源头。
    /// </summary>
    private static void 采副本(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("【副本】");

            // 地图 ID 从 TimelineManager 拿（它内部已经维护了当前地图）
            var 地图 = TimelineManager.当前地图Id;
            sb.AppendLine($"地图 ID：{地图}");

            // 时间轴文件名往往就带副本名 —— 这是最可靠的信息源
            var 状态 = TimelineManager.状态;
            if (!string.IsNullOrWhiteSpace(状态))
            {
                sb.AppendLine($"时间轴：{状态}");
            }

            if (地图 == 0)
            {
                sb.AppendLine("（不在副本里 / 地图信息读不到）");
            }

            sb.AppendLine();
        }
        catch (Exception e)
        {
            sb.AppendLine("（副本信息读取失败：" + e.Message + "）");
            sb.AppendLine();
        }
    }

    // ==================== 状态列表（带剩余时间）====================

    /// <summary>
    /// 采集一个目标身上的状态，**带剩余秒数**。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么要给 AI"剩余时间"★
    ///
    ///    只写"身上有神速咏唱"是不够的 —— AI 没法判断
    ///    "还能打几个闪飒"。带上"还剩 12 秒"它才能**预测**。
    ///    这是用户明确要求的："让 ai 可以更好判断情况和预测情况"。
    ///
    ///  ── 输出量的控制 ──
    ///
    ///    状态可能很多（副本 buff、食物、药、队友给的增益…），
    ///    全列会挤爆提示词。所以：
    ///      · 只列**剩余 >= 1 秒**的（马上要消失的没预测价值）
    ///      · 按剩余时间**升序**（快没了的排前面 = 最需要关注的优先）
    ///      · 最多列 <see cref="状态显示上限"/> 条，超出只说个数
    ///
    ///    ⚠️ 这仍然是"只给事实"——不告诉 AI 这个 buff 是干嘛的，
    ///       让它自己结合上面的技能表推断（防幻觉原则）。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private const int 状态显示上限 = 12;

    private static void 采状态列表(StringBuilder sb, IBattleChara? 目标, string 标题)
    {
        try
        {
            var 全部 = CharacterExt.所有状态剩余(目标);
            if (全部.Count == 0) return;

            // 只留"还值得关注"的，按剩余升序（快没了的优先）
            var 有效 = 全部
                .Where(x => x.剩余秒 >= 1f)
                .OrderBy(x => x.剩余秒)
                .ToList();

            if (有效.Count == 0) return;

            var 显示 = 有效.Take(状态显示上限).ToList();
            var 文本 = string.Join("、", 显示.Select(x => $"{x.名} {x.剩余秒:F0}s"));

            sb.AppendLine($"{标题}（{有效.Count} 个）：{文本}"
                          + (有效.Count > 状态显示上限 ? $" …还有 {有效.Count - 状态显示上限} 个" : ""));
        }
        catch { }
    }

    // ==================== 自己 ====================

    private static void 采自己(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("【我】");
            sb.AppendLine($"职业：{Core.Me.ClassJob.Value.Name}");
            sb.AppendLine($"血量：{(Core.Me.CurrentHp * 100f / Math.Max(1, Core.Me.MaxHp)):F0}%");
            sb.AppendLine($"蓝量：{Core.Me.CurrentMp}");
            sb.AppendLine($"战斗中：{(Core.Me.InCombat() ? "是" : "否")}");
            sb.AppendLine($"GCD 剩余：{GCDHelper.GetGCDCooldown()} ms");
            sb.AppendLine($"能否插能力技：{(GCDHelper.GetGCDCooldown() < 600 ? "是" : "否")}");

            // ── 我自己身上的状态（含剩余时间）──
            //   给 AI 判断"我还能放什么"用，例如：
            //     · 神速咏唱还剩几秒 → 还能打几个闪飒
            //     · 无中生有还剩几秒 → 还能免费几个治疗
            //     · 光速还剩几秒   → 还能瞬发几个
            //   这类"限时窗口"是 AI 做**预测**的关键输入。
            采状态列表(sb, Core.Me, "我身上的状态");

            // ══════════════════════════════════════════════════════════
            //  ★ 移动状态 —— **决定 AI 能不能建议读条技能** ★
            //
            //  用户实测指出的问题：
            //    "本地逻辑已经会在移动时挡掉读条技能，
            //     但 **AI 还在建议读条技能**"
            //
            //  根因是**信息缺失**：`移动中能放()` 是本地层的事，
            //  AI 拿到的局面里没有这个事实，于是它按"站着不动"的前提给建议，
            //  那些建议全被本地拦掉 —— 表现为"AI 一直说用闪灼，一个都没打出去"。
            //
            //  这一行就是把事实喂给它。**必须显式写出"只能建议瞬发技能"**，
            //  否则它知道在移动、但未必联想到"所以我该换技能"。
            // ══════════════════════════════════════════════════════════
            var 移动状态 = SpellUtil.移动状态描述();
            sb.AppendLine($"移动状态：{移动状态}");

            if (SpellUtil.在移动())
            {
                sb.AppendLine("  ⚠️ 移动中——**不要建议需要读条的技能**（放不出来），" +
                              "请改用瞬发技能（下方技能表里标了「瞬发」的那些）");
            }

            sb.AppendLine();
        }
        catch (Exception e)
        {
            sb.AppendLine("（自己状态读取失败：" + e.Message + "）");
        }
    }

    // ==================== 职业资源 ====================

    private static void 采资源(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("【职业资源】");

            var 有 = false;

            // 学者
            if (JobApiHelper.读得到("以太"))
            {
                sb.AppendLine($"以太（0-3）：{JobApiHelper.以太}");
                sb.AppendLine($"小仙女在场：{(JobApiHelper.有小仙女 ? "是" : "否")}");
                sb.AppendLine($"炽天使剩余：{JobApiHelper.炽天使剩余}");
                sb.AppendLine($"妖精能量：{JobApiHelper.妖精能量}");
                有 = true;
            }

            // 白魔
            if (JobApiHelper.读得到("百合"))
            {
                sb.AppendLine($"百合（0-3）：{JobApiHelper.百合}");
                sb.AppendLine($"血百合（0-3）：{JobApiHelper.血百合}");
                有 = true;
            }

            // 贤者
            if (JobApiHelper.读得到("蛇胆"))
            {
                sb.AppendLine($"蛇胆（0-3）：{JobApiHelper.蛇胆}");
                sb.AppendLine($"毒刺（0-3）：{JobApiHelper.毒刺}");
                sb.AppendLine($"均衡中：{(JobApiHelper.均衡中 ? "是" : "否")}");
                有 = true;
            }

            // 占星
            if (JobApiHelper.读得到("手牌数"))
            {
                sb.AppendLine($"手牌数：{JobApiHelper.手牌数}");
                sb.AppendLine($"有近战卡：{(JobApiHelper.有近战卡() ? "是" : "否")}");
                有 = true;
            }

            if (!有) sb.AppendLine("（当前职业没有可读的资源，或是别的职业）");

            sb.AppendLine();
        }
        catch (Exception e)
        {
            sb.AppendLine("（资源读取失败：" + e.Message + "）");
        }
    }

    // ==================== 队友 ====================

    private static void 采队友(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("【队伍】");

            var 队友 = PartyHelper.CastableAlliesWithin30;
            if (队友 == null || 队友.Count == 0)
            {
                sb.AppendLine("（没有可施法的队友 —— 可能单人在打木桩）");
                sb.AppendLine();
                return;
            }

            var 低血 = 0;
            var 危急 = 0;

            foreach (var r in 队友)
            {
                if (r == null) continue;

                var 比例 = r.MaxHp > 0 ? r.CurrentHp * 100f / r.MaxHp : 100f;

                var 标记 = "";
                try
                {
                    if (r.IsTank()) 标记 = "[坦克]";
                    else if (r.IsHealer()) 标记 = "[治疗]";
                }
                catch { }

                // ── 这个队友身上的状态（带剩余时间）──
                //   价值最高的是**盾**和**HoT**：
                //     · 盾还剩几秒 → 决定"要不要现在补"（快没了才补，满的别浪费）
                //     · HoT 还剩几秒 → 决定"他会自己回上来还是要我出手"
                //   这直接支撑"该不该再投一个治疗"的判断。
                var 状态 = CharacterExt.所有状态剩余(r)
                    .Where(x => x.剩余秒 >= 1f)
                    .OrderBy(x => x.剩余秒)
                    .Take(6)
                    .Select(x => $"{x.名} {x.剩余秒:F0}s")
                    .ToList();

                var 状态文本 = 状态.Count > 0 ? "  状态：" + string.Join("、", 状态) : "";

                sb.AppendLine($"  {r.Name}{标记}：{比例:F0}%{状态文本}");

                if (比例 <= 70f) 低血++;
                if (比例 <= 35f) 危急++;
            }

            sb.AppendLine();
            sb.AppendLine($"低于 70%：{低血} 人");
            sb.AppendLine($"低于 35%：{危急} 人");

            // 坦克单独列 —— 治疗最关心它
            var 坦克 = HealTargetHelper.血量最低的坦克();
            if (坦克 != null)
            {
                sb.AppendLine($"最危险的坦克：{坦克.Name}（{坦克.血量比例() * 100f:F0}%）");
            }

            // 待复活 / 待驱散
            var 待复活 = HealTargetHelper.待复活队友();
            if (待复活 != null) sb.AppendLine($" 有队友倒地待复活：{待复活.Name}");

            var 待驱散 = HealTargetHelper.需要驱散队友();
            if (待驱散 != null) sb.AppendLine($" 有队友需要驱散：{待驱散.Name}");

            sb.AppendLine();
        }
        catch (Exception e)
        {
            sb.AppendLine("（队伍读取失败：" + e.Message + "）");
        }
    }

    // ==================== 敌人 ====================

    /// <summary>
    /// 坦克压力 —— 用户要求的三个判断：
    ///   · 接战时 T 的血量波动大不大
    ///   · 减伤够不够（用波动间接看，见 坦克压力 类的说明）
    ///   · 怪多不多
    /// </summary>
    private static void 采坦克压力(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("【坦克压力 / 接战强度】");
            sb.Append(坦克压力.状态描述());
            sb.Append(坦克压力.敌人描述());
            sb.AppendLine();
        }
        catch (Exception e)
        {
            sb.AppendLine("【坦克压力】读取失败：" + e.Message);
            sb.AppendLine();
        }
    }

    private static void 采敌人(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("【敌人】");

            var 目标 = HealTargetHelper.当前目标();

            if (目标 == null)
            {
                sb.AppendLine("当前没有选中目标");
                sb.AppendLine();
                return;
            }

            var 是Boss = false;
            try { 是Boss = 目标.IsBoss(); } catch { }

            sb.AppendLine($"当前目标：{目标.Name}");
            sb.AppendLine($"  类型：{(是Boss ? "Boss" : "普通怪")}");
            sb.AppendLine($"  血量：{目标.血量比例() * 100f:F0}%（{目标.CurrentHp} / {目标.MaxHp}）");

            var 快死了 = HealTargetHelper.目标快死了();
            if (快死了) sb.AppendLine("   这个目标快死了（预估 12 秒内会死）—— 别在它身上浪费爆发");

            // ══════════════════════════════════════════════════════════
            //  ★ 目标站得稳不稳（用户要求：让 AI 知道小怪是否稳定在固定位置）★
            //
            //  为什么要给这个：
            //    · **地面技能**（罩子 / 地星 / 庇护所）放下去敌人一走就废了 ——
            //      AI 建议这类技能前，得知道"扔这儿会不会白扔"
            //    · **AOE** 也一样：怪在跑动时，以它为中心的范围大概率打不满
            //
            //  ⚠️ 判据不是"现在没动"，而是**"已经稳定了足够久"** ——
            //     小怪刚出现时往往站着不动（待机 / 没被拉 / 在远处），
            //     那种"没动"是假象，马上就会冲进来。
            //     详情见 敌人移动检测.cs 的说明。
            // ══════════════════════════════════════════════════════════
            try
            {
                var 稳 = 敌人移动检测.移动很少(目标);
                sb.AppendLine($"  位置稳定性：{(稳 ? "稳定（已观察足够久，地面技能可放心铺）" : "不稳定/观察中（地面技能可能白铺）")}");
                sb.AppendLine($"    （采样：{敌人移动检测.状态描述(目标)}）");
            }
            catch { }

            // ══════════════════════════════════════════════════════════
            //  ★ 我挂在这个目标身上的状态（含 DoT 剩余时间）★
            //
            //  这是 AI 判断"该不该补 DoT"的**唯一正确输入**。
            //  没有它，AI 只能看着"我有个 DoT 技能、CD 也好了"就建议补 ——
            //  而那正是用户实测到的"不断补 DoT"（每 4.9 秒一次）。
            //
            //  现在它会看到「天辉 22s」这种事实，自然就知道不用补。
            //  （本地层也有 Dot补判 兜底，两道保险。）
            // ══════════════════════════════════════════════════════════
            var 目标状态 = CharacterExt.所有状态剩余(目标)
                .Where(x => x.剩余秒 >= 1f)
                .OrderBy(x => x.剩余秒)
                .Take(8)
                .Select(x => $"{x.名} {x.剩余秒:F0}s")
                .ToList();

            if (目标状态.Count > 0)
            {
                sb.AppendLine($"  我挂在它身上的状态：{string.Join("、", 目标状态)}");
                sb.AppendLine("    （DoT 类：剩余时间还多就别补，补了纯浪费一个 GCD）");
            }
            else
            {
                sb.AppendLine("  我挂在它身上的状态：无（DoT 还没上，或者已掉光）");
            }

            sb.AppendLine($"周围敌人数量（5 米内）：{HealTargetHelper.周围敌人数量()}");

            // AOE 判断：能用 AOE 打到几个
            var 可AOE = HealTargetHelper.周围敌人数量(8f) >= 3;
            sb.AppendLine($"适合放 AOE：{(可AOE ? "是（≥3 个）" : "否")}");

            sb.AppendLine();
        }
        catch (Exception e)
        {
            sb.AppendLine("（敌人读取失败：" + e.Message + "）");
        }
    }

    // ==================== 时间轴 ====================

    private static void 采时间轴(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("【时间轴】");

            if (!HealSettings.Instance.启用时间轴)
            {
                sb.AppendLine("（时间轴未启用）");
                sb.AppendLine();
                return;
            }

            sb.AppendLine($"本帧该铺减伤：{(TimelineManager.该铺减伤() ? "是 ⚠️" : "否")}");

            var 未来5秒 = TimelineManager.未来有减伤(5.0);
            sb.AppendLine($"未来 5 秒有减伤需求：{(未来5秒 ? "是 ⚠️" : "否")}");

            var 距下次 = TimelineManager.距下次减伤();
            if (距下次 > 0 && 距下次 < 30)
            {
                sb.AppendLine($"距下次减伤：{距下次:F1} 秒");
            }

            sb.AppendLine();
        }
        catch (Exception e)
        {
            sb.AppendLine("（时间轴读取失败：" + e.Message + "）");
        }
    }

    // ==================== 可选技能清单 ====================

    /// <summary>
    /// 采集**当前职业实际会用的技能清单**（ID + 中文名 + 用途分类）。
    ///
    /// **这是防幻觉的最后一道闸。**
    /// 阶段 B 让 AI 输出技能 ID —— 不给清单它就凭记忆编，
    /// 编错的后果是一次无效施放，甚至放出不该放的技能。
    ///
    /// 有了清单，提示词里就能写"只能从这里选"，
    /// 而且返回后还能做白名单校验（见 AiDecisionLayer.解析）。
    /// </summary>
    private static void 采可选技能(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("【可选技能（阶段 B 只能从这些 ID 里选）】");

            // ⚠️ 必须取"当前实际职业"的表，不能用静态的 当前技能表 ——
        //    那个会被最后加载的职业覆盖（用户实测：玩学者却收到幻术师的技能清单）
        var 表 = HealerACR.Common.HealRotationEventHandler.取当前职业技能表();
            if (表 == null)
            {
                sb.AppendLine("（拿不到技能表 —— 阶段 B 请输出 0|数据不足）");
                sb.AppendLine();
                return;
            }

            var 计数 = 0;

            // ══════════════════════════════════════════════════════════
            //  ★ 每个技能都带上**当前运行时状态** ★
            //
            //  只给"ID = 名字（类别）"是不够的 —— AI 不知道
            //  这个技能现在**转好没有**，于是会建议一个 CD 中的技能，
            //  然后在终审被"技能不可用"挡掉（白问一轮，命中率被拉低）。
            //
            //  加上：剩余 CD / 充能层数 / 施法时间 / 蓝耗 / 射程。
            //  这些都是**游戏里读出来的实时值**，不是让 AI 去背知识。
            //
            //  ⚠️ 读不到的项**不写**（不要写 "CD=?"）——
            //     宁可少一项，也不要给 AI 一个它只能瞎猜的占位符。
            // ══════════════════════════════════════════════════════════
            void 加(string 类别, uint id)
            {
                if (id == 0) return;

                var 名 = SpellIds.反查(id);
                var 附加 = new StringBuilder();

                // 充能技：显示"还剩几层/上限"（比 CD 更直观）
                var 上限 = CharacterExt.最大充能数(id);
                if (上限 > 1)
                {
                    var 当前 = CharacterExt.充能数(id);
                    if (当前 >= 0) 附加.Append($" 充能{当前}/{上限}");
                }
                else
                {
                    var cd = CharacterExt.冷却剩余秒(id);
                    if (cd > 0.05f) 附加.Append($" CD剩余{cd:F1}s");
                    else if (cd >= 0f) 附加.Append(" 已就绪");
                }

                var 施法 = CharacterExt.施法时间秒(id);

                // ★ 明确标出「瞬发」★ —— 这是移动中唯一能用的那一类。
                //
                //   ⚠️ 原来只在**有读条时**标注（`读条X.Xs`），
                //      瞬发的什么都不写 —— AI 分不清"瞬发"和"读不到施法时间"，
                //      于是移动时不知道该挑哪个。
                //      用户实测的问题就是"AI 一直在建议读条技能"。
                if (施法 > 0.05f) 附加.Append($" 读条{施法:F1}s");
                else if (施法 >= 0f) 附加.Append(" 瞬发");

                var 蓝 = CharacterExt.蓝耗(id);
                if (蓝 > 0) 附加.Append($" 蓝耗{蓝}");

                var 距离 = CharacterExt.射程(id);
                if (距离 > 0.5f) 附加.Append($" 射程{距离:F0}m");

                sb.AppendLine($"  {id} = {名}（{类别}）{附加}");
                计数++;
            }

            void 加数组(string 类别, uint[] ids)
            {
                if (ids == null) return;
                foreach (var id in ids) 加(类别, id);
            }

            加("基础输出", 表.基础输出);
            加("群体输出", 表.群体输出);
            加("DoT", 表.Dot技能);
            加("移动填充", 表.移动填充技);
            加("单体治疗", 表.单体治疗GCD);
            加("群体治疗", 表.群体治疗GCD);
            加("紧急单奶", 表.紧急单奶);
            加("群体治疗能力技", 表.群体治疗能力技);
            加("单体盾", 表.单体盾);
            加("群体盾", 表.群体盾);
            加("团队减伤", 表.团队减伤);
            加("复活", 表.复活);
            加("驱散", 表.驱散);
            加("醒梦", 表.醒梦);

            加数组("输出能力技", 表.输出能力技);
            加数组("脱战准备", 表.脱战准备技能);

            sb.AppendLine($"（共 {计数} 个）");
            sb.AppendLine();

            // ── 静态参考：每个类别"是什么用的" ──
            //   ⚠️ 这块是**静态**的（每轮都一样），所以它是超长时
            //      第一个该被砍掉的部分 —— 见 采集() 里的截断逻辑。
            采技能效果说明(sb);
        }
        catch (Exception e)
        {
            sb.AppendLine("（技能清单读取失败：" + e.Message + "）");
            sb.AppendLine();
        }
    }

    /// <summary>
    /// **技能效果说明** —— 静态参考，每轮都给。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么给这个（用户要求："告知 ai 所有技能和效果"）★
    ///
    ///    上面那张表只说"ID = 名字（类别）"，类别名是我们自己起的简写
    ///    （"紧急单奶"/"群体盾"…）。AI 未必能从这个词推断出
    ///    "什么时候该用、用了会怎样"，于是容易给出语境不对的建议
    ///    （实测例子：它建议在这时候补 DoT，而正确做法是留 GCD 给治疗）。
    ///
    ///    这里给每个**类别**一段固定说明，讲清楚：
    ///      · 这个类别的技能是干什么的
    ///      · 什么时机用它才对
    ///      · 用它有什么代价
    ///
    ///  ⚠️ **只讲类别，不讲具体副本机制** ——
    ///    这是防幻觉原则的边界：类别说明是"我们系统自己的定义"（可信），
    ///    而"某个副本该怎么打"是模型的知识盲区（不可信，必编）。
    ///    所以这里绝不出现任何副本名、Boss 名、机制名。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static void 采技能效果说明(StringBuilder sb)
    {
        sb.AppendLine("【技能类别说明（我们系统自己的定义，可据此判断时机）】");
        sb.AppendLine("  基础输出    ：不耗资源的常规输出，没事干时用它填 GCD。");
        sb.AppendLine("  群体输出    ：敌人成堆时用，比单体输出总伤害高；敌人少时不要用。");
        sb.AppendLine("  DoT         ：挂在敌人身上的持续伤害，**不要频繁补** ——"
                      + "它会显示「CD剩余/已就绪」，但真正的补判标准是目标身上 DoT 的**剩余时间**"
                      + "（上面敌人段里给了）。剩余还多就别补，补了纯浪费一个 GCD。");
        sb.AppendLine("  移动填充    ：移动中可用的瞬发技能，走位时填补 GCD。");
        sb.AppendLine("  单体治疗    ：救一个人的命，占一个 GCD。**优先度高于输出**。");
        sb.AppendLine("  群体治疗    ：多人同时掉血时用，占 GCD。");
        sb.AppendLine("  紧急单奶    ：能力技（不占 GCD），给血量危急的人，通常 CD 很长 ——"
                      + "**别随手交**，留给真正救命的时刻。");
        sb.AppendLine("  群体治疗能力技：能力技群奶，不占 GCD，比 GCD 群奶更舍得用。");
        sb.AppendLine("  单体盾/群体盾：**伤害来之前**铺才有价值；伤害已经落地再铺等于浪费"
                      + "（除非目标还在持续挨打）。");
        sb.AppendLine("  团队减伤    ：提前铺，覆盖即将到来的大伤害。伤害过了再放没用。");
        sb.AppendLine("  复活        ：拉死亡队友，通常要读条或耗即刻，优先级很高。");
        sb.AppendLine("  驱散        ：解除队友身上的有害状态，看上面队友段的标注。");
        sb.AppendLine("  醒梦        ：回蓝能力技，蓝量低时用。");
        sb.AppendLine("  输出能力技  ：能力技输出，不占 GCD，卡 CD 打不亏。");
        sb.AppendLine("  脱战准备    ：脱战时提前攒的资源，战斗中用。");
        sb.AppendLine();
    }
}
