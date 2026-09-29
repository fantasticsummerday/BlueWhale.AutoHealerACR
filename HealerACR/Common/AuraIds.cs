namespace HealerACR.Common;

/// <summary>
/// buff / 状态 ID。数字都是从游戏 Status 表 dump 出来核对过的（工具见 tools/SpellDump）。
///
/// ⚠️ 特别注意 DoT：**每升一级 DoT，buff id 就换一个**。
///    占星 烧灼838 → 炽灼843 → 焚灼1881，学者 毒菌179 → 猛毒菌189 → 蛊毒法1895。
///    只检查一个的话，满级会一直认为"没上 DoT"，然后无限补 DoT。
///    所以这里按等级段都列出来，靠 JobSpellTable.所有DotBuff 一起检查。
///
/// 每个值都能在设置里覆盖（BuffId覆盖），写成 0 = 关掉那个判断。
/// </summary>
public static class AuraIds
{
    private static uint 取(string 键, uint 默认值)
    {
        try
        {
            if (HealSettings.Instance.BuffId覆盖.TryGetValue(键, out var v)) return v;
        }
        catch
        {
        }

        return 默认值;
    }

    // ---------------- 通用状态 ----------------

    /// <summary>即刻咏唱 = 167</summary>
    public static uint 即刻 => 取("即刻", 167);

    /// <summary>
    /// 正在被人复活 = 148（**防御性**：看到它就别重复拉，防"两个奶妈抢同一个尸体"）。
    ///
    /// ⚠️ 名字叫"复活等待"，但 `dump_status.tsv` 里 148 的正式名就是「复活」，
    ///    另外 1140 也是「复活」—— 名字对不上不要紧，**id 和用途是核实过的**。
    /// </summary>
    public static uint 复活等待 => 取("复活等待", 148);

    /// <summary>
    /// 「限制复活」类状态 —— 目标身上有这些时，**我们拉不起来**，别浪费即刻和 GCD。
    ///
    /// ⚠️ 对照分析查出来的缺口：原来只过滤 148（别人正在拉），
    ///    完全没过滤"**这个尸体根本拉不起来**"的情况。
    ///
    /// 来源：`dump_status.tsv` 关键词反查「限制复活」/「无法复活」，
    ///      命中 1755 / 2449 / 3380 / 4262 / 4263 全部列出 ——
    ///      **不是凭印象挑的**（其中 3380 / 4262 是同类 ACR 用到的两个，
    ///       1755 / 2449 / 4263 是反查时一起捞出来的）。
    ///
    /// 多列几个是安全的：这些状态在正常副本里不会出现，
    /// **列多了最多是"本来能拉却没拉"的极端情况，列少了是白交即刻**。
    /// </summary>
    public static List<uint> 限制复活
    {
        get
        {
            var 结果 = new List<uint>(5);
            foreach (var id in new[] { 1755u, 2449u, 3380u, 4262u, 4263u })
            {
                if (id != 0) 结果.Add(id);
            }
            return 结果;
        }
    }

    /// <summary>醒梦 = 1204</summary>
    public static uint 醒梦 => 取("醒梦", 1204);

    /// <summary>
    /// 转化中（学者）= 791。
    /// ⚠️ 这是「转化」（Dissipation）状态，**不是"小仙女在场"** —— 我一开始看反了。
    /// 从 同类 ACR 的 Scholar_GetPet 逆向出来：它 Check 里有 HasAura(791)，
    /// 意思是"转化期间小仙女被牺牲了，召唤无效，跳过"。
    /// </summary>
    public static uint 转化中 => 取("转化中", 791);

    /// <summary>
    /// 鼓舞（学者的护盾 buff，来自「鼓舞激励之策」）= 297。
    /// ⚠️ 注意它和技能 ID 185 **不是一回事** —— 这是 FF14 里少数技能/buff id 不一致的情况。
    /// </summary>
    public static uint 鼓舞 => 取("鼓舞", 297);

    /// <summary>
    /// 激励 = 1918 —— **鼓舞的暴击版**。
    ///
    /// ⚠️ 踩过的坑（会话对照分析时查出来的静默失效）：
    ///    学者「鼓舞激励之策」暴击时挂的不是 297 而是 **1918**。
    ///    我们原来只映射 `185 → 297`，于是"目标身上只有暴击盾"时
    ///    判断成"没有盾" → **又读条放一次鼓舞**，白费一个 GCD。
    ///
    ///    参考同类 ACR 的 `HasScholarShield`：297 和 1918 **两个都查**。
    ///
    /// 来源：`dump_status.tsv` 反查（297 鼓舞 / 1918 激励 / 3087 鼓舞 / 3088 激励），
    ///      不是凭印象写的。
    /// </summary>
    public static uint 激励 => 取("激励", 1918);

    /// <summary>埋伏之毒预备（学者，连环计给的）= 3882，有它才能放埋伏之毒</summary>
    public static uint 埋伏之毒预备 => 取("埋伏之毒预备", 3882);

    // ---------------- "假死"：掉血是设计如此，不该治 ----------------
    //
    // ⚠️ 这一组曾经有**名实不符**，已修（对照分析 + `dump_status.tsv` 反查时发现）：
    //
    //    原来写的是 `出死入生 => 取("出死入生", 811)` ——
    //    但 811 在 status 表里的正式名是 **死而不僵**，
    //    而**真正的「出死入生」是 3255**。
    //    等于"用错了名字去查一个查不到的状态"，3255 从来没被检查过。
    //
    //    功能上没出过事（810/811 都被 `假死不治` 覆盖），
    //    但按 开发约定.md ⑧ 的精神：**名实不符的常量比没有常量更危险** ——
    //    下一个人想改"出死入生"的逻辑时，会改到 811 上去。
    //
    //    现在三档全部按 status 表对齐（名字 = 数据里的名字）。

    /// <summary>出死入生（暗骑「行尸走肉」的**后续**形态）= 3255</summary>
    public static uint 出死入生 => 取("出死入生", 3255);

    /// <summary>死而不僵（暗骑）= 811；另有多变迷宫版 2303</summary>
    public static uint 死而不僵 => 取("死而不僵", 811);

    /// <summary>纯正死而不僵（多变迷宫）= 2303</summary>
    public static uint 纯正死而不僵 => 取("纯正死而不僵", 2303);

    /// <summary>行尸走肉（暗骑）= 810</summary>
    public static uint 行尸走肉 => 取("行尸走肉", 810);

    /// <summary>死斗（战士）= 409</summary>
    public static uint 死斗 => 取("死斗", 409);

    /// <summary>超火流星（枪刃）= 1836</summary>
    public static uint 超火流星 => 取("超火流星", 1836);

    // ---------------- 职业技能状态 ----------------

    /// <summary>神速咏唱（白魔）= 157</summary>
    public static uint 神速魔 => 取("神速魔", 157);

    /// <summary>闪飒预备（白魔 92 级，神速咏唱后获得）= 3879</summary>
    public static uint 闪飒预备 => 取("闪飒预备", 3879);

    /// <summary>闪飒预备的另一个 id = 4326</summary>
    public static uint 闪飒预备2 => 取("闪飒预备2", 4326);

    /// <summary>光速（占星）= 841</summary>
    public static uint 光速 => 取("光速", 841);

    /// <summary>均衡（贤者）= 2606</summary>
    public static uint 均衡 => 取("均衡", 2606);

    /// <summary>心关（贤者）= 2604</summary>
    public static uint 心关 => 取("心关", 2604);

    /// <summary>心关的另一个 id = 2871（同一 buff 的另一档，防御版本差异）</summary>
    public static uint 心关2 => 取("心关2", 2871);

    /// <summary>身上有没有心关（两个 id 都查）</summary>
    public static bool 有心关(IBattleChara 目标)
    {
        if (目标 == null) return false;
        if (心关 != 0 && 目标.HasLocalPlayerAura(心关)) return true;
        if (心关2 != 0 && 目标.HasLocalPlayerAura(心关2)) return true;
        return false;
    }

    // ---------------- DoT 状态（按等级段） ----------------

    /// <summary>白魔 DoT 满级（天辉）= 1871</summary>
    public static uint 白魔Dot => 取("白魔Dot", 1871);

    /// <summary>白魔 DoT 二级（烈风）= 144</summary>
    public static uint 白魔Dot2 => 取("白魔Dot2", 144);

    /// <summary>白魔 DoT 一级（疾风）= 143</summary>
    public static uint 白魔Dot1 => 取("白魔Dot1", 143);

    /// <summary>学者 DoT 满级（蛊毒法）= 1895</summary>
    public static uint 学者Dot => 取("学者Dot", 1895);

    /// <summary>学者 DoT 二级（猛毒菌）= 189</summary>
    public static uint 学者Dot2 => 取("学者Dot2", 189);

    /// <summary>学者 DoT 一级（毒菌）= 179</summary>
    public static uint 学者Dot1 => 取("学者Dot1", 179);

    /// <summary>占星 DoT 满级（焚灼）= 1881</summary>
    public static uint 占星Dot => 取("占星Dot", 1881);

    /// <summary>占星 DoT 二级（炽灼）= 843</summary>
    public static uint 占星Dot2 => 取("占星Dot2", 843);

    /// <summary>占星 DoT 一级（烧灼）= 838</summary>
    public static uint 占星Dot1 => 取("占星Dot1", 838);

    /// <summary>贤者 DoT 满级档（均衡注药III）= 2616</summary>
    public static uint 贤者Dot => 取("贤者Dot", 2616);

    /// <summary>贤者 DoT 二档（均衡注药II）= 2615</summary>
    public static uint 贤者Dot2 => 取("贤者Dot2", 2615);

    /// <summary>贤者 DoT 一档（均衡注药）= 2614</summary>
    public static uint 贤者Dot1 => 取("贤者Dot1", 2614);

    /// <summary>均衡注药III 的其他档 = 2864 / 3108 / 3976（同名不同档）</summary>
    public static uint 贤者DotAlt => 取("贤者DotAlt", 2864);

    public static uint 贤者DotAlt2 => 取("贤者DotAlt2", 3108);

    public static uint 贤者DotAlt3 => 取("贤者DotAlt3", 3976);

    /// <summary>白魔天辉的另一档 = 2035</summary>
    public static uint 白魔DotAlt => 取("白魔DotAlt", 2035);

    /// <summary>学者蛊毒法的其他档 = 2039 / 3089</summary>
    public static uint 学者DotAlt => 取("学者DotAlt", 2039);

    public static uint 学者DotAlt2 => 取("学者DotAlt2", 3089);

    /// <summary>占星焚灼的另一档 = 2041</summary>
    public static uint 占星DotAlt => 取("占星DotAlt", 2041);

    // ==================================================================
    //  技能 ID → buff ID 映射
    //
    //  ⚠️ 这一整块是从状态表**逐个核对**出来的。
    //     之前这些地方直接拿技能 ID 当 buff ID 查，**永远查不到**，
    //     导致"防止重复"的检查全部失效 —— 技能会被反复放
    //     （一直补再生、一直开秘策、一直放天宫图…）。
    //
    //    规律：这两个 id 相同的技能是多数，但**只要不同就必须在这里登记**。
    // ==================================================================

    /// <summary>再生（白魔 HoT）技能 137 / buff 158</summary>
    public static uint 再生 => 取("再生", 158);

    // ==================================================================
    //  持续伤害类状态（用于"该不该挂 HoT"的判断）
    //
    //  ⚠️ 为什么用**两张表**而不是一张：
    //    参考实现只查「出血」，那是他们自己的取舍。
    //    但游戏里"持续扣血"的状态远不止出血 ——
    //    用官方 Status.csv 按描述反查（「无属性持续伤害」「体力逐渐流失」）
    //    **一共 217 条**，名字分布是：
    //        火伤22 出血22 感电16 水毒16 裂伤13 冻伤10 污泥10 冻结10
    //        打伤9 切伤8 刺伤8 持续伤害6 …
    //
    //    全列 217 条不现实（而且版本会加），所以分两层：
    //      · <see cref="出血类"/>：22 条，**明确就是流血**，用于"止血优先"
    //      · <see cref="持续伤害类"/>：另外那些**常见且明确会持续扣血**的
    //
    //  ⚠️ 这里刻意**不追求穷尽**：漏一两条的代价只是"少一个优先挂 HoT 的理由"，
    //     而乱加 ID 的代价是"该救急的时候跑去挂 HoT" —— 后者严重得多。
    //     所以只收官方描述明确写着持续伤害的。
    // ==================================================================

    /// <summary>「出血」类 —— 22 条（名称就叫出血/流血，官方描述"无属性持续伤害"）</summary>
    private static readonly uint[] 出血类 =
    {
        273, 320, 339, 343, 642, 643, 940, 1074, 1714,
        2088, 2389, 2399, 2432, 2636, 2922, 2951, 3077,
        3078, 3966, 4068, 4137, 4138,
    };

    /// <summary>
    /// 其他常见持续伤害 —— 火伤 / 感电 / 水毒 / 裂伤 / 冻伤 / 污泥 / 打伤 / 切伤 / 刺伤。
    ///
    /// ⚠️ 这些 ID 是**按官方描述逐条核对**出来的（不是凭印象），
    ///    判据：Status.csv 的描述里含「无属性持续伤害」或「体力逐渐流失」。
    /// </summary>
    private static readonly uint[] 其他持续伤害 =
    {
        // 火伤
        129, 143, 144, 161, 162, 163, 235, 250, 264, 265, 266, 267, 268, 269, 270, 271, 272, 283, 284, 285, 286, 287,
        // 水毒 / 感电
        288, 289, 314, 352, 484, 485, 487, 503,
        // 裂伤 / 打伤 / 切伤 / 刺伤
        530, 531, 532, 533, 534, 535,
    };

    /// <summary>
    /// 目标身上有没有**持续伤害类**状态。
    ///
    /// 用途：持续掉血的人**挂 HoT 的收益最高**（HoT 本来就是对付细水长流的），
    /// 所以这类目标应该**优先挂 HoT 而不是硬读 GCD 直疗**。
    /// </summary>
    public static bool 有持续伤害(IBattleChara? 目标)
    {
        if (目标 == null) return false;

        try
        {
            foreach (var id in 出血类)
                if (目标.HasAura(id)) return true;

            foreach (var id in 其他持续伤害)
                if (目标.HasAura(id)) return true;
        }
        catch { }

        return false;
    }

    /// <summary>目标身上有没有「出血」类状态（比 <see cref="有持续伤害"/> 更窄）</summary>
    public static bool 有出血(IBattleChara? 目标)
    {
        if (目标 == null) return false;

        try
        {
            foreach (var id in 出血类)
                if (目标.HasAura(id)) return true;
        }
        catch { }

        return false;
    }

    /// <summary>水流幕（白魔）技能 25861 / buff 2708</summary>
    public static uint 水流幕 => 取("水流幕", 2708);

    /// <summary>生命回生法（学者）技能 25867 / buff 2710</summary>
    public static uint 生命回生法 => 取("生命回生法", 2710);

    /// <summary>天星交错（占星）技能 16556 / buff 1889</summary>
    public static uint 天星交错 => 取("天星交错", 1889);

    /// <summary>秘策（学者）技能 16542 / buff 1896</summary>
    public static uint 秘策 => 取("秘策", 1896);

    /// <summary>活化（贤者）技能 24300 / buff 2611</summary>
    public static uint 活化 => 取("活化", 2611);

    /// <summary>混合（贤者）技能 24317 / buff 2622</summary>
    public static uint 混合 => 取("混合", 2622);

    /// <summary>无中生有（白魔）技能 7430 / buff 1217</summary>
    public static uint 无中生有 => 取("无中生有", 1217);

    /// <summary>星位合图（占星）技能 3612 / buff 845</summary>
    public static uint 星位合图 => 取("星位合图", 845);

    /// <summary>拯救（贤者）技能 24294 / buff 2610</summary>
    public static uint 拯救 => 取("拯救", 2610);

    /// <summary>天宫图（占星）技能 16557 / buff 1890</summary>
    public static uint 天宫图 => 取("天宫图", 1890);

    /// <summary>
    /// 把**技能 ID** 换算成它对应的 **buff ID**。
    /// 没登记的直接原样返回（多数技能两者相同）。
    /// </summary>
    public static uint 技能转Buff(uint 技能Id)
    {
        return 技能Id switch
        {
            137 => 再生,
            158 => 再生,
            25861 => 水流幕,
            2708 => 水流幕,
            25867 => 生命回生法,
            2710 => 生命回生法,
            16556 => 天星交错,
            1889 => 天星交错,
            16542 => 秘策,
            1896 => 秘策,
            24300 => 活化,
            2611 => 活化,
            24317 => 混合,
            2622 => 混合,
            7430 => 无中生有,
            1217 => 无中生有,
            3612 => 星位合图,
            845 => 星位合图,
            24294 => 拯救,
            2610 => 拯救,
            16557 => 天宫图,
            1890 => 天宫图,
            185 => 鼓舞,
            297 => 鼓舞,
            _ => 技能Id,
        };
    }

    // ---------------- 组合 ----------------

    /// <summary>
    /// "假死"状态列表（值为 0 的会被自动剔除）。
    ///
    /// ⚠️ 这一组**必须列全** —— 漏一个的后果是"坦克开无敌的那几秒
    ///    被当成普通掉血猛灌治疗"，白交 CD 和百合。
    ///
    /// 暗骑「行尸走肉」其实是**两段**：
    ///   810 行尸走肉      → 10 秒内被打死会变成下一段
    ///   811 死而不僵      → 生效后必须**奶满**才解除，否则时间到即死
    ///   3255 出死入生     → 后续形态
    /// ⚠️ 注意 811 这一段**和别的无敌不一样**：它是"要奶满"的。
    ///    `可以治()` 把它排除掉是有意的（那段期间用别的逻辑处理），
    ///    但如果你以后要专门做"死而不僵抢救"，要**从这里挑出去**再做 ——
    ///    别在 可以治() 里又加回来（会互相打架）。
    /// </summary>
    public static List<uint> 假死不治
    {
        get
        {
            var 结果 = new List<uint>(6);
            foreach (var id in new[]
            {
                出死入生,        // 3255
                死而不僵,        // 811
                纯正死而不僵,    // 2303（多变迷宫）
                行尸走肉,        // 810
                死斗,            // 409
                超火流星,        // 1836
            })
            {
                if (id != 0) 结果.Add(id);
            }

            return 结果;
        }
    }
}
