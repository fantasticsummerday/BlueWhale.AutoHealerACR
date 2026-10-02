using System;

using System.Collections.Generic;

using AEAssist.Helper;



namespace HealerACR.Common;



/// <summary>

/// **结构化倾向**（AI → 本地评分的一层软修正）—— 按设计文档 V1 实现（2026-10-03）。

///

/// ══════════════════════════════════════════════════════════════════════

///  [!] 原则（文档原话）：

///      「AI 不直接决定技能，只表达当前希望本地评分系统往哪个方向偏。」

///      「AI 负责『往哪个方向偏』，本地系统负责『能不能这么做』。」

///

///  [!] V1 只有三个维度（`risk` / `confidence` 按文档留到 V2）：

///        output     : AOE | SINGLE | BALANCED

///        mitigation : HOLD | NORMAL | SPEND

///        resource   : CONSERVE | NORMAL | SPEND

///

///  [!] ⚠️ **一律用"加减分"，不用倍率**（文档第 6 节专门强调）：

///      倍率会让 AI 倾向压过本地逻辑（治疗价值 / 紧急程度 / CD / 战斗阶段…），

///      而 AI 只是"在都合理的候选之间改一下先后"。

///

///  [!] 限幅（文档第 7 节）：

///        · 单维度：Clamp(±15)

///        · 总    ：Clamp(±20)

///

///  [!] TTL（文档第 9 节）：倾向 **3~5 秒不抖** ——

///      一次回复不立刻切换；同一个新值**连续出现两次**才认，或当前值已过期才认。

///

///  [!] 安全底线（文档第 3/4 节）：

///        · `HOLD`（保留减伤）在"预计伤害 ≥ 紧急阈值"时**必须被无视** ——

///          由调用方 `减伤加分(紧急: true)` 直接返回 0 实现 ✓

///        · `CONSERVE`（省资源）不阻止"资源即将溢出"的本地强制处理 ✓

/// ══════════════════════════════════════════════════════════════════════

public static class Ai倾向

{

    public enum 输出向 { 均衡 = 0, 群攻 = 1, 单体 = 2 }

    public enum 减伤向 { 正常 = 0, 保留 = 1, 提前交 = 2 }

    public enum 资源向 { 正常 = 0, 省着 = 1, 多花 = 2 }



    /// <summary>倾向有效期（毫秒）—— 文档建议 3~5 秒，取 4 秒。</summary>

    public const int 有效期毫秒 = 4000;



    /// <summary>单维度限幅（文档第 7 节）。</summary>

    public const int 单维上限 = 15;



    /// <summary>总限幅（文档第 7 节）。</summary>

    public const int 总上限 = 20;



    // ── 当前生效值（含 TTL）──

    private static 输出向 _输出 = 输出向.均衡;

    private static 减伤向 _减伤 = 减伤向.正常;

    private static 资源向 _资源 = 资源向.正常;

    private static long _生效时刻;



    // ── "连续出现两次才切换"用 ──

    private static 输出向 _待确认输出 = 输出向.均衡;

    private static 减伤向 _待确认减伤 = 减伤向.正常;

    private static 资源向 _待确认资源 = 资源向.正常;

    private static bool _有待确认;



    /// <summary>最近一次原始回复（给窗口/日志看）。</summary>

    public static string 最近原文 = "";



    public static 输出向 输出 => _输出;

    public static 减伤向 减伤 => _减伤;

    public static 资源向 资源 => _资源;



    /// <summary>当前倾向是否还有效（超过 TTL 就当作"没表达"）。</summary>

    public static bool 有效

    {

        get

        {

            try { return _生效时刻 != 0 && TimeHelper.Now() - _生效时刻 <= 有效期毫秒; }

            catch { return false; }

        }

    }



    /// <summary>

    /// **解析 AI 回复里的倾向行**（形如 `{"output":"AOE","mitigation":"NORMAL","resource":"CONSERVE"}`）。

    ///

    /// [!] 宽容解析：字段缺失 ⇒ 当作"没表达"（文档第 8 节：不强制每轮都表态）✓

    /// [!] 抖动抑制：新值**连续出现两次**才切换；或旧值已过期（超过 TTL）则立即接受 ✓

    /// </summary>

    public static void 解析(string? 回复)

    {

        try

        {

            if (string.IsNullOrEmpty(回复)) return;

            最近原文 = 回复!;

            // ══════════════════════════════════════════════════════════════
            //  ★ 审计 ④ + ⑥（2026-10-03）：**只认协议行** ★
            //
            //  [!] 协议：单独一行，形如
            //        TENDENCY|output=AOE|mitigation=NORMAL|mp=CONSERVE
            //      可以只写其中几项；不写这一行 = **本轮不表态** ✓
            //
            //  [!] 为什么必须这样（两件事一次解决）：
            //      ① 原来是从**自由文本**里模糊匹配 `output=` / `AOE` 这类词 ✗
            //         模型在理由里写一句"这波不该 AOE"就可能被误提取 ✗
            //      ② 原来对所有通道都解析 ⇒ 初始化/策略/记忆的回复也会碰到倾向 ✗
            //         （已知只有决策层提示词要求这一行 ⇒ 只认协议行 = 等于只认决策层 ✓）
            //
            //  [!] 无效行/无协议行 ⇒ **一个字都不改**（不改状态、不刷日志）✓
            // ══════════════════════════════════════════════════════════════
            var 协议行 = 取协议行(回复!);
            if (协议行 == null) return;
            回复 = 协议行;   // 下面三个字段都从这一行里取




            var 新输出 = 取枚举(回复!, "output", _输出,

                ("AOE", 输出向.群攻), ("SINGLE", 输出向.单体), ("BALANCED", 输出向.均衡));

            var 新减伤 = 取枚举(回复!, "mitigation", _减伤,

                ("HOLD", 减伤向.保留), ("NORMAL", 减伤向.正常), ("SPEND", 减伤向.提前交));

            var 新资源 = 取枚举(回复!, "mp",
                取枚举(回复!, "resource", _资源,
                    ("CONSERVE", 资源向.省着), ("NORMAL", 资源向.正常), ("SPEND", 资源向.多花)),

                ("CONSERVE", 资源向.省着), ("NORMAL", 资源向.正常), ("SPEND", 资源向.多花));



            var 变了 = 新输出 != _输出 || 新减伤 != _减伤 || 新资源 != _资源;

            if (!变了) { _生效时刻 = TimeHelper.Now(); return; }   // 和现在一样 ⇒ 续期



            // 抖动抑制：连续两次相同才切；旧值过期则立即切

            if (_有待确认 && 新输出 == _待确认输出 && 新减伤 == _待确认减伤 && 新资源 == _待确认资源)

            {

                _输出 = 新输出; _减伤 = 新减伤; _资源 = 新资源;

                _生效时刻 = TimeHelper.Now();

                _有待确认 = false;

                LogHelper.Info($"[HealerACR] AI 倾向切换：输出={_输出}｜减伤={_减伤}｜资源={_资源}");

                return;

            }



            _待确认输出 = 新输出; _待确认减伤 = 新减伤; _待确认资源 = 新资源;

            _有待确认 = true;



            if (!有效)

            {

                // 旧值已过期 ⇒ 不等第二次，直接采用（否则会一直停在旧倾向上）

                _输出 = 新输出; _减伤 = 新减伤; _资源 = 新资源;

                _生效时刻 = TimeHelper.Now();

                _有待确认 = false;

                LogHelper.Info($"[HealerACR] AI 倾向（旧值已过期，直接采用）：输出={_输出}｜减伤={_减伤}｜资源={_资源}");

            }

        }

        catch { }

    }



    /// <summary>
    /// **取出协议行** —— 逐行找以 `TENDENCY` 开头的那一行（大小写不敏感，允许前面有空格）。
    /// 找不到返回 null（= 本轮不表态，倾向一个字都不改）。审计 ⑥。
    /// </summary>
    private static string? 取协议行(string 文本)
    {
        try
        {
            foreach (var 行 in 文本.Split('\n'))
            {
                var 干净 = 行.Trim().TrimStart('*', '-', '`', ' ');
                // [!] 复审第 7 条：`TENDENCY` 必须是**完整 token** ——
                //     原来 `StartsWith("TENDENCY")` 会接受 `TENDENCY_FAKE...` ✗
                //     ⇒ 现在只接受 `TENDENCY|…` 或整行只有 `TENDENCY` ✓
                if (干净.Equals("TENDENCY", StringComparison.OrdinalIgnoreCase)) return 干净;
                if (干净.StartsWith("TENDENCY|", StringComparison.OrdinalIgnoreCase)) return 干净;
            }
            return null;
        }
        catch { return null; }
    }
    private static T 取枚举<T>(string 文本, string 键, T 兜底, params (string 值, T 结果)[] 表) where T : struct

    {

        try

        {

            // 兼容两种写法：JSON 的 "key":"值"，以及 key=值
            //  [!] 为什么：提示词跑在内插原始字符串里，`{` 会被当成插值 ⇒ 提示里用的是
            //      `output=AOE|SINGLE|BALANCED` 这种不含花括号的写法 ✓
            var i = 文本.IndexOf('"' + 键 + '"', StringComparison.OrdinalIgnoreCase);

            if (i < 0) i = 文本.IndexOf('"' + 键 + '=', StringComparison.OrdinalIgnoreCase);

            if (i < 0) i = 文本.IndexOf(键 + "=", StringComparison.OrdinalIgnoreCase);

            if (i < 0) return 兜底;                        // 没这个字段 ⇒ 当作没表达

            var 段 = 文本.Substring(i, Math.Min(120, 文本.Length - i)).ToUpperInvariant();

            // [!] 复审第 7 条：值必须**整段命中**，不能再 Contains ——
            //     否则 output=AOE_xxx 这类也会被当成 AOE ✗
            //     做法：按 | 切段 → 找 key=value 那一段 → 值整段比较 ✓
            foreach (var 段2 in 段.Split('|'))
            {
                var 等号 = 段2.IndexOf('=');
                if (等号 < 0) continue;
                var 键2 = 段2.Substring(0, 等号).Trim().Trim('"', ' ', '{', '}');
                if (!键2.Equals(键, StringComparison.OrdinalIgnoreCase)) continue;
                var 值2 = 段2.Substring(等号 + 1).Trim().Trim('"', ' ', '{', '}', ',');
                foreach (var (值, 结果) in 表)
                    if (值2.Equals(值, StringComparison.OrdinalIgnoreCase)) return 结果;
                return 兜底;   // 键对但值非法 ⇒ 只丢这个字段 ✓
            }
            return 兜底;

            return 兜底;

        }

        catch { return 兜底; }

    }



    // ══════════════════════════════════════════════════════════════════

    //  ↓↓↓ 下面三个就是"喂给本地评分"的加减分入口（文档第 2/3/4 节的数值）

    // ══════════════════════════════════════════════════════════════════



    /// <summary>

    /// **输出倾向的加分**（文档：AOE ⇒ 群攻 +12 / 单体 -4；SINGLE ⇒ 反之；BALANCED ⇒ 0）。

    /// [!] 用加减分而不是倍率 —— 见类注释。

    /// </summary>

    public static int 输出加分(bool 是群攻)

    {

        try

        {

            if (!有效 || _输出 == 输出向.均衡) return 0;

            var 值 = 是群攻

                ? (_输出 == 输出向.群攻 ? 12 : -4)

                : (_输出 == 输出向.单体 ? 12 : -4);

            return 限幅(值);

        }

        catch { return 0; }

    }



    /// <summary>

    /// **减伤倾向的加分**（文档：HOLD -8 / NORMAL 0 / SPEND +10）。

    /// [!] ⚠️ **安全底线**：`预计伤害 ≥ 紧急阈值` 时调用方必须传 `紧急: true`

    ///     ⇒ 这里直接返回 0（无视 HOLD）—— AI 倾向不能覆盖本地安全规则 ✓

    /// </summary>

    public static int 减伤加分(bool 紧急 = false)

    {

        try

        {

            if (紧急) return 0;                    // ★ 硬安全例外（文档第 3 节）

            if (!有效 || _减伤 == 减伤向.正常) return 0;

            return 限幅(_减伤 == 减伤向.保留 ? -8 : 10);

        }

        catch { return 0; }

    }

    // [!] 审计 P2-17（2026-10-03）：原 资源加分() 已删除 ——
    //     资源倾向的唯一消费点是 成本倍率()（调效率罚权重），
    //     留着旧的加分接口，日后容易被人再塞进总分里。




    /// <summary>
    /// **减伤偏置**（用于**候选桶内排序**，不改任何事实字段）—— 审查文档第 5/19 节。
    ///
    ///  [!] 文档明确划出的红线（第 3/8/18 节）：
    ///      · `真实收益` / `减伤百分点` / `盾真实倍率` / `本地分` **一律不动**
    ///      · AI 只提供"我现在更看重它多少" ⇒ **只参与排序** ✓
    ///  [!] 盾单独放（第 7 节）：第一版**不给盾加偏置**（盾走另一套收益模型）✓
    ///  [!] 硬安全例外（第 3 节 / V1 设计第 3 节）：
    ///      `紧急`（预计伤害达本地阈值）时**直接返回 0** —— HOLD 挡不住本地交减伤 ✓
    /// </summary>
    public static int 减伤偏置(bool 是盾, bool 紧急 = false)
    {
        try
        {
            if (紧急) return 0;          // ★ 本地硬规则优先

            // [!] 审计 P1-7：调用点传的是"候选的紧急等级"，而减伤候选的紧急等级恒为 0 ✗
            //     ⇒ 这里**自己再问一次本地判据**：未来几秒是否要来大伤害。
            //     任一为真就当"紧急" ⇒ 偏置归零 ⇒ HOLD 挡不住本地交减伤 ✓
            try
            {
                if (HealerACR.Timeline.TimelineManager.未来有减伤(5f)) return 0;
            }
            catch { }
            try
            {
                if (HealerACR.Common.伤害预测.要预铺(5f)) return 0;
            }
            catch { }

            if (是盾) return 0;          // ★ 第一版不动盾（文档第 7 节）
            if (!有效 || _减伤 == 减伤向.正常) return 0;
            return 限幅(_减伤 == 减伤向.保留 ? -8 : 10);
        }
        catch { return 0; }
    }

    /// <summary>
    /// **MP 成本倍率** —— 用来调**效率罚的权重**，而不是改 `MP / 真实治疗量` 本身（审查文档第 9~12 节）。
    /// CONSERVE 1.25 / NORMAL 1.00 / SPEND 0.75 ✓
    /// </summary>
    public static float 成本倍率()
    {
        try
        {
            if (!有效 || _资源 == 资源向.正常) return 1f;
            return _资源 == 资源向.省着 ? 1.25f : 0.75f;
        }
        catch { return 1f; }
    }

    /// <summary>把三个维度的总影响限幅到 ±20（文档第 7 节）。</summary>

    public static int 总限幅(int 合计)

    {

        try { return Math.Clamp(合计, -总上限, 总上限); } catch { return 0; }

    }



    private static int 限幅(int v)

    {

        try { return Math.Clamp(v, -单维上限, 单维上限); } catch { return 0; }

    }



    /// <summary>给设置页 / 调试窗显示的一句话。</summary>

    public static string 描述()

    {

        try

        {

            if (!有效) return "（没有生效中的倾向）";

            var 剩 = (有效期毫秒 - (TimeHelper.Now() - _生效时刻)) / 1000.0;

            return $"输出={_输出}｜减伤={_减伤}｜资源={_资源}（还剩 {剩:F1}s）";

        }

        catch { return "（读不到）"; }

    }

}

