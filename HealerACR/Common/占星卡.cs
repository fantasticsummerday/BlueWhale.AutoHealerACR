using Dalamud.Game.ClientState.JobGauge.Enums;

namespace HealerACR.Common;

/// <summary>
/// 占星「抽卡 / 出卡」的卡面识别。
///
/// ══════════════════════════════════════════════════════════════════
///  [!] 为什么要单独一个文件（原来卡面只能靠"技能可不可用"猜）：
///
///      `JobApi_Astrologian.DrawnCards` 返回的是 **手上的牌的卡面数组**
///      （`CardType[]`，卡面本身没有任何 id），这是唯一能"按卡面分流"的数据源。
///      当初误以为它读不到，于是退化成"抽了还没出"这个二值状态 ——
///      结果既没法按卡面选槽，也没法按卡面选目标。
///
///  [!] 卡面 → 出卡槽 → 技能 id（卡面值见 `CardType`）：
///
///      卡面        值  出的技能      槽      近战/远程
///      太阳神之衡   1  37023 出卡I   37019  近战
///      战争神之枪   4  37026 出卡I   37019  近战
///      放浪神之箭   3  37024 出卡II  37020  远程
///      世界树之干   2  37027 出卡II  37020  远程
///      建筑神之塔   6  37025 出卡III 37021  远程
///      河流神之瓶   5  37028 出卡III 37021  远程
///
///      所以「出卡 I / II / III」是**槽位**，手上是哪张卡就按哪张卡选槽 ——
///      死认一个槽（原来是恒取出卡 III）时，手上不是那一档的卡就放不出去。
///
///  [!] 只有 1/4 是近战卡：卡面 1/4 各挂一个"物理伤害提高"，
///      2/3/5/6 各挂一个"魔法/治疗相关"。见 <see cref="是近战卡"/>。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 占星卡
{
    /// <summary>抽卡：星极抽卡（37017）/ 灵极抽卡（37018）—— 同一个技能位的两个形态</summary>
    public static uint 抽卡基技 => SpellIds.取("星极抽卡");

    /// <summary>小奥秘卡（37022）—— 手上有小奥秘卡时才按它</summary>
    public static uint 小奥秘卡 => SpellIds.取("小奥秘卡");

    /// <summary>王冠之贵妇（治疗卡）</summary>
    public static uint 贵妇 => SpellIds.取("王冠之贵妇");

    /// <summary>王冠之领主（范围伤害卡）</summary>
    public static uint 领主 => SpellIds.取("王冠之领主");

    /// <summary>卡面 → 出卡槽位（0 = 不认识的卡面）</summary>
    public static uint 出卡槽(CardType 卡) => 卡 switch
    {
        CardType.Balance => SpellIds.取("出卡I"),
        CardType.Spear   => SpellIds.取("出卡I"),
        CardType.Arrow   => SpellIds.取("出卡II"),
        CardType.Bole    => SpellIds.取("出卡II"),
        CardType.Spire   => SpellIds.取("出卡III"),
        CardType.Ewer    => SpellIds.取("出卡III"),
        _ => 0,
    };

    /// <summary>卡面 → 出卡后会打出去的那个技能 id（0 = 不认识的卡面）</summary>
    public static uint 出卡技能(CardType 卡) => 卡 switch
    {
        CardType.Balance => 37023,   // 太阳神之衡
        CardType.Spear   => 37026,   // 战争神之枪
        CardType.Arrow   => 37024,   // 放浪神之箭
        CardType.Bole    => 37027,   // 世界树之干
        CardType.Spire   => 37025,   // 建筑神之塔
        CardType.Ewer    => 37028,   // 河流神之瓶
        _ => 0,
    };

    /// <summary>
    /// 是不是近战卡：**只有太阳神之衡（1）与战争神之枪（4）**。
    ///
    /// [!] 这一条两套参考都没有分歧（原来我们把世界树/放浪神也当近战卡 ✗）：
    ///      · 卡面枚举本身就只有这两张被当作近战档用；
    ///      · 出卡槽也是按这个分的 —— 1/4 走「出卡 I」，`出卡I` 这一档
    ///        在游戏里对应的就是这两张。
    /// </summary>
    public static bool 是近战卡(CardType 卡) => 卡 is CardType.Balance or CardType.Spear;

    /// <summary>这三张是"伤害卡"（给队友的增伤），其余四张是"辅助卡"</summary>
    public static bool 是伤害卡(CardType 卡) =>
        卡 is CardType.Balance or CardType.Spear or CardType.Arrow or CardType.Bole;

    /// <summary>手上第一张牌（没有牌返回 <see cref="CardType.None"/>）</summary>
    public static CardType 第一张()
    {
        var 手牌 = JobApiHelper.手牌();
        return 手牌.Length > 0 ? 手牌[0] : CardType.None;
    }

    /// <summary>
    /// 按"手上实际是哪张卡"选出该按的出卡槽位。
    ///
    /// [!] 返回值是**槽位 id**（出卡 I/II/III 之一），调用方还要再走
    ///     `SpellUtil.当前形态(槽位)` 才能拿到真正要放的技能 id。
    ///
    /// [!] 拿不到卡面时返回 0（**不再死认一个槽**）：
    ///     死认的代价是"手上不是那一档的卡就一律放不出去"，
    ///     而返回 0 只是这一拍不出卡，下一拍数据回来就能接着出。
    /// </summary>
    public static uint 按卡面选槽()
    {
        try
        {
            var 槽 = 出卡槽(第一张());
            if (槽 != 0) return 槽;

            // 兜底：手牌读不到时，反过来看哪个槽"形态已经不是槽位自己了"
            //       —— 卡在手上时 `CheckActionChange` 会把槽位换成卡，
            //          所以"形态 ≠ 槽位 id"就说明这个槽里有卡。
            foreach (var 候选 in new[] { "出卡I", "出卡II", "出卡III" })
            {
                var id = SpellIds.取(候选);
                if (id == 0) continue;

                var 形态 = SpellUtil.当前形态(id);
                if (形态 != null && 形态.Id != id) return id;
            }
        }
        catch { }

        return 0;
    }
}
