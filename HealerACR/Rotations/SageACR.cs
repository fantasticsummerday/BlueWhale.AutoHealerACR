using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;
using HealerACR.Resolvers;
using HealerACR.Timeline;

namespace HealerACR.Rotations;

/// <summary>贤者技能表。技能 ID 走 SpellIds 内置表（中文名）。</summary>
public class SGESpellTable : JobSpellTable
{
    public override Jobs Job => Jobs.Sage;
    public override string 职业名 => "贤者";

    public override uint 基础输出 => SpellUtil.取已解锁(
        SpellIds.取("注药III"), SpellIds.取("注药II"), SpellIds.取("注药"));

    /// <summary>药尾声补刀用的瞬发填充技 —— 贤者：注药（24283）—— 即刻下变瞬发。</summary>
    /// <summary>
    /// **药尾声填充技 = 基础输出**（自动取最高档：注药III / 注药II / 注药）。
    ///
    /// [!] 原来写死 `注药`（最低档 180/250/300）——
    ///     82 级后基础输出是 `注药III`(330/380)，
    ///     写死低档等于**每个爆发药窗口少 80~130 威力**。
    /// </summary>
    public override uint 药尾声填充技 => 基础输出;
    public override uint 群体输出 => SpellUtil.取已解锁(
        SpellIds.取("失衡II"), SpellIds.取("失衡"));

    /// <summary>
    /// **贤者的 AOE 门槛：94 级起用 3**（不是 2）。
    ///
    /// [!] 算术依据（恰好 2 只怪时群体技是负收益）：
    ///     94+：失衡II 170×2 = 340  <  注药III 380  -> 少 40 威力（10.5%）
    ///
    /// [!] 82-94 段：失衡II 340  vs  注药III 330 -> **正收益**，保持 2。
    ///     72-82 段：失衡 320 vs 注药II 320 -> **持平**，
    ///     持平时不改门槛（改了没收益，反而少覆盖一只怪）。
    /// </summary>
    public override int AOE门槛(int 等级) => 等级 >= 94 ? 3 : 2;
    // 写"注药III/II/注药"的等级链：均衡注药是它们的形态，
    // 只写 1 级的"注药"的话，高等级取不到"均衡注药III"
    /// <summary>
    /// **自身 AOE 技**（`CastType=2 Range=0`）。
    ///
    /// ⚠️ 数据依据（`tools\CastProbe`）：
    ///       失衡   24297  Lv46  咏唱**0.0**  射程0  范围5  威力160
    ///       失衡II 24315  Lv82  咏唱**0.0**  射程0  范围5  威力170
    ///
    /// ⚠️ 贤者的失衡是**瞬发** —— 所以移动中也能用（比占星强）。
    /// </summary>
    public override uint[] 自身AOE候选 => new[]
    {
        SpellIds.取("失衡II"),    // 82 级
        SpellIds.取("失衡"),      // 46 级
    };

    /// <summary>
    /// **威力档位** —— 数据逐字来自游戏数据宏（`tools\HealPotency raw`）。
    ///
    ///    注药    24283  Lv1   if(等级>=64) 300 else if(等级>=54) 250 else 180
    ///    注药II  24306  Lv72  320
    ///    注药III 24312  Lv82  if(等级>=94) 380 else 330
    ///    失衡    24297  Lv46  160
    ///    失衡II  24315  Lv82  170
    ///
    /// ⚠️ 注药有**三档**（180/250/300），别只写两档。
    /// </summary>
    protected override (int 等级, int 威力)[]? 威力表(uint 技能Id)
    {
        if (技能Id == SpellIds.取("注药"))    return new[] { (64, 300), (54, 250), (0, 180) };
        if (技能Id == SpellIds.取("注药II"))  return new[] { (82, 330), (0, 320) };
        if (技能Id == SpellIds.取("注药III")) return new[] { (94, 380), (0, 330) };
        if (技能Id == SpellIds.取("失衡"))    return new[] { (82, 170), (0, 160) };
        if (技能Id == SpellIds.取("失衡II"))  return new[] { (0, 170) };

        return null;
    }

    /// <summary>
    /// **咏唱时间**（秒）—— 来自游戏 `Cast100ms`。
    ///
    /// ⚠️ 注药系列是 **1.5 秒读条**，失衡系列是 **瞬发**。
    ///    两者复唱都是 2.5 秒 ⇒ 都占一个 GCD ⇒ 威力排序里约掉。
    /// </summary>
    public override IReadOnlyDictionary<uint, float> 咏唱时间表 { get; } = new Dictionary<uint, float>
    {
        [SpellIds.取("注药")] = 1.5f,
        [SpellIds.取("注药II")] = 1.5f,
        [SpellIds.取("注药III")] = 1.5f,
        [SpellIds.取("失衡")] = 0f,      // 瞬发
        [SpellIds.取("失衡II")] = 0f,    // 瞬发
    };

    /// <summary>自身 AOE 的半径（贤者是 5 米）</summary>
    public override (bool 是, float 半径) 自身AOE半径(uint 技能Id)
        => (技能Id == SpellIds.取("失衡") || 技能Id == SpellIds.取("失衡II"))
            ? (true, 5f) : (false, 0f);


    // ══════════════════════════════════════════════════════════════════
    //  ★ 治疗候选集 ★  —— 数据来自游戏数据（见 `WhiteMageACR` 同处的说明）
    //
    //  ⚠️ 贤者的恢复力档位在说明宏里是 `if(等级>=85, 高, 低)`（不是 85 级，
    //    是"85 级"这个门槛）—— 见 `tools\HealPotency raw` 的输出。
    // ══════════════════════════════════════════════════════════════════
    private 治疗候选集? _治疗候选;

    /// <summary>
    /// **按等级查两档恢复力**（高等级档 / 低等级档）。
    ///
    /// [!] 不分档会出错：诊断 450/400（85 级提升）——
    ///     写死 450 会让 85 级以下**高估 12.5%**，
    ///     而诊断 vs 预后只差 150，高估 50 就可能翻转选择。
    ///
    /// 来源：`dump_healpot.tsv`（游戏 ActionTransient 说明）。
    /// </summary>
    private static int 档位(uint 技能Id, int 高, int 低, int 提升等级)
    {
        try { return CharacterExt.我的等级() >= 提升等级 ? 高 : 低; }
        catch { return 低; }
    }
    
    public override 治疗候选集 治疗候选
    {
        get
        {
            if (_治疗候选 != null) return _治疗候选;

            var c = new 治疗候选集();

            // ── 单体 GCD ──
            c.加(new 治疗技能 { Id = SpellIds.取("诊断"), 名 = "诊断", 等级 = 2,
                恢复力 = 档位(SpellIds.取("诊断"), 450, 400, 85), MP = 400, 咏唱 = 1.5f, 复唱 = 2.5f });

            // ── 群体 GCD ──
            c.加(new 治疗技能 { Id = SpellIds.取("预后"), 名 = "预后", 等级 = 10,
                // [!] **修正：预后是纯治疗 300，没有盾**（数据核对）
                //     原文只有「恢复自身及周围队员的体力　恢复力：300」。
                //     盾来自**均衡预后**（另一个技能，buff 2609/2866）。
                //     标错 `是盾` 会让打分给它凭空加一份吸收量 ⇒ 高估它。
                恢复力 = 300, MP = 700, 咏唱 = 2.0f, 复唱 = 2.5f, 群体 = true });

            // ── 单体能力技 ──
            //
            // ★ **顺序与槽位定义一致**（`瞬发单奶能力技` 是 `取已解锁(输血, 白牛清汁, 灵橡清汁)`）★
            //
            //  [!] 为什么这一条必须补（本轮审计发现）：槽位里**第一个**是 `输血`，
            //      但治疗候选池里原来**没有它** ==>
            //        ① AI 的候选集里看不到"输血" ⇒ **建议不了它**
            //        ② 而本地 resolver 会从槽位里选出它（只要它已解锁且可用）
            //      ==> **AI 和本地看到的动作集合不一致** ——
            //          这正是"AI 建议的东西本地不做 / 本地做了 AI 没建议"的一类成因。
            //
            //  [!] 数值：输血是**单体大盾 + 持续治疗**（不占 GCD，2 分钟 CD）。
            //      恢复力按官表 1200（它是盾 + HoT 的组合技，这里只填直接治疗部分；
            //      盾由 `是盾` 那条路算，见 `治疗技能` 的说明）。
            //
            //  ⚠️ 表外数值审计（`tools/NumAudit.py`）修正：**等级 76 → 70**。
            //     `D:\Download\Action.csv` 里 24305「输血」的 ClassJobLevel 列逐字是 **70**，
            //     `dump_healpot.tsv` 也是 **70** —— 两处独立数据源一致，我们原来写 76 ✗
            //     [!] 影响面：**70~75 级这一段**。`等级` 只用于"表里这个技能够不够解锁"
            //        （真正的解锁判定走 `SpellUtil.已解锁`），
            //        所以写 76 的后果是：这一段本该能用的输血在**候选评分里被当成没解锁**，
            //        而 70 级学会它之后到 75 级之间用不上。
            c.加(new 治疗技能 { Id = SpellIds.取("输血"), 名 = "输血", 等级 = 70,
                恢复力 = 1200, MP = 0, 咏唱 = 0f, 冷却 = 120f, 是盾 = true });

            c.加(new 治疗技能 { Id = SpellIds.取("灵橡清汁"), 名 = "灵橡清汁", 等级 = 45,
                恢复力 = 600, MP = 0, 咏唱 = 0f, 冷却 = 1f, 资源消耗 = 1 });

            c.加(new 治疗技能 { Id = SpellIds.取("白牛清汁"), 名 = "白牛清汁", 等级 = 62,
                恢复力 = 700, MP = 0, 咏唱 = 0f, 冷却 = 45f });


            // ── 群体能力技 ──
            c.加(new 治疗技能 { Id = SpellIds.取("自生"), 名 = "自生", 等级 = 20,
                恢复力 = 100, MP = 0, 咏唱 = 0f, 冷却 = 60f, 群体 = true,
                HoT恢复力 = 100, HoT持续 = 15f });

            c.加(new 治疗技能 { Id = SpellIds.取("自生II"), 名 = "自生II", 等级 = 60,
                恢复力 = 130, MP = 0, 咏唱 = 0f, 冷却 = 60f, 群体 = true,
                HoT恢复力 = 130, HoT持续 = 15f });

            c.加(new 治疗技能 { Id = SpellIds.取("寄生清汁"), 名 = "寄生清汁", 等级 = 52,
                恢复力 = 400, MP = 0, 咏唱 = 0f, 冷却 = 30f, 群体 = true, 资源消耗 = 1 });

            c.加(new 治疗技能 { Id = SpellIds.取("整体论"), 名 = "整体论", 等级 = 76,
                恢复力 = 300, MP = 0, 咏唱 = 0f, 冷却 = 120f, 群体 = true, 是盾 = true });


            // ── 消化（Lv58，群体，需要蛇胆）──
            //    ⚠️ 审计发现：它在 `群体治疗能力技` 槽位，但**不在候选集**。
            //       数据里的 `450 + 350` 是"打敌人 + 给带心关的人回血"，
            //       对队伍而言的有效治疗量取 **350**（心关部分）。
            c.加(new 治疗技能 { Id = SpellIds.取("消化"), 名 = "消化（盾转治疗）", 等级 = 58,
                恢复力 = 350, MP = 0, 咏唱 = 0f, 冷却 = 1f, 群体 = true, 资源消耗 = 1 });

            // ── 魂灵风息（Lv90，`600 + 170/130`）──
            c.加(new 治疗技能 { Id = SpellIds.取("魂灵风息"), 名 = "魂灵风息", 等级 = 90,
                // [!] **修正：600 是纯治疗**（数据核对）
                //     原文：「令自身及周围 20 米内的队员恢复体力　恢复力：600
                //            追加效果：令带有关心状态的目标恢复体力　恢复力：…」
                //     170/130 是**关心（心关）那一口**的恢复力，**不是 HoT**。
                //     原来标了 `是盾` + `HoT恢复力=170`，两项都错。
                恢复力 = 600, MP = 700, 咏唱 = 1.5f, 冷却 = 120f, 群体 = true,
                // [!] **显式标能力技** —— 推导判据（`MP > 0 || 冷却 == 0`）会把它
                //     判成 GCD（因为项目给它的 MP 填了 700），
                //     但它实际是 90 级能力技（不占 GCD）。
                //     这是 51 个候选里唯一需要手工标注的一个。
                是能力技 = true,
                HoT恢复力 = 170, HoT持续 = 15f });

            // ── 智慧之爱（Lv100，群体 150）──
            c.加(new 治疗技能 { Id = SpellIds.取("智慧之爱"), 名 = "智慧之爱", 等级 = 100,
                恢复力 = 150, MP = 0, 咏唱 = 0f, 冷却 = 180f, 群体 = true });

            _治疗候选 = c;
            return c;
        }
    }

    public override uint Dot技能 => SpellUtil.取已解锁(
        SpellIds.取("注药III"), SpellIds.取("注药II"), SpellIds.取("注药"));
    public override uint DotBuff => AuraIds.贤者Dot;
    // 均衡注药有三档 buff（2614/2615/2616，另有 2864 同名的另一档），
    // 只查一档的话满级会一直认为"没上 DoT"→ 无限补
    //
    // ★ 2026-10-04（表 #104）：**加上群 DOT 的 3897「均衡失衡」** ★
    //   [!] 参考实现（shiyuvi）把 3897 和三个单体毒放在**同一条判据**里 ——
    //       也就是"目标身上有没有我的毒"这一个问题，四个 id 一起问。
    //       我们原来一条都没登记 ⇒ AI 连"它是一个 DoT"都不知道 ✗
    //   [!] 加进来的效果：`可补Dot的敌人` 会把"只被群毒盖住"的怪也认成
    //       "已经有我的毒" ⇒ 不会又给它单体毒（那是重复投入）。
    public override uint[] 所有DotBuff => new[]
    {
        AuraIds.贤者Dot, AuraIds.贤者DotAlt, AuraIds.贤者DotAlt2, AuraIds.贤者DotAlt3,
        AuraIds.贤者Dot2, AuraIds.贤者Dot1,
        AuraIds.均衡失衡,
    };

    public override uint 单体治疗GCD => SpellIds.取("诊断");
    public override uint 群体治疗GCD => SpellIds.取("预后");
    public override uint 紧急单奶 => SpellUtil.取已解锁(
        SpellIds.取("输血"), SpellIds.取("白牛清汁"), SpellIds.取("灵橡清汁"));

    /// <summary>
    /// 白牛清汁 / 灵橡清汁：瞬发单体治疗（蛇胆资源，不读条）。
    ///
    /// ⚠️ 参考实现的贤者能力技顺序（IL 直证）：
    ///      输血 → 白牛清汁 → 灵橡清汁
    ///    而且有"最近用过就不重复"的时间窗（2000ms）。
    /// </summary>
    public override uint 预铺单奶能力技 => SpellUtil.取已解锁(SpellIds.取("白牛清汁"));

    /// <summary>输血 / 白牛：不读条，移动中也能用。</summary>
    public override uint 瞬发单奶能力技 => SpellUtil.取已解锁(
        SpellIds.取("输血"), SpellIds.取("白牛清汁"), SpellIds.取("灵橡清汁"));

    /// <summary>蛇胆系瞬发治疗的阈值 45%（和学者活性法同档）。</summary>
    public override float 瞬发单奶血线 => 0.45f;
    // ★ 2026-10-04 修：原来填的是「消化」(24301) ✗
        //   消化是「**解除自己挂的均衡诊断/均衡预后**换治疗」的转换技，
        //   不是群疗 ⇒ 放进这个槽位会在群盾刚铺上时**把盾吃掉** ✗
        //   贤者的群疗走「寄生清汁(Res_Emergency)」与「整体论(Res_BigAoEHeal)」两条路 ✓
        public override uint 群体治疗能力技 => 0;

    // ⚠️ 写"诊断/预后"而不是"均衡诊断/均衡预后"：
    //    后者是前者在均衡状态下的 action change 形态，硬放会被游戏拒绝。
    //    配合 SpellUtil.当前形态() 使用，游戏自己会变成均衡版。
    public override uint 单体盾 => SpellIds.取("诊断");
    public override uint 群体盾 => SpellIds.取("预后");
    public override uint 护盾前置 => SpellIds.取("均衡");
    // ★ 2026-10-04 补：贤者的**失衡是瞬发**（注药是 1.5 秒读条）✗
        //   原来没填「移动填充技」⇒ 移动中一条输出填充都没有 ✗
        public override uint 移动填充技 => SpellIds.取("失衡");

    // ══════════════════════════════════════════════════════════════════
    //  ★ 失衡的四条职业级规则（表 #100 / #101 / #102）★
    //
    //  [!] 参考实现（`失衡.txt` 的 Check，IL 直读）：
    //      ① **地图黑名单**：`GetCurrTerrId()` 是 293 / 296 / 1046 时整条禁用
    //         （那几张图里失衡会误伤 / 完全无效）。
    //      ② **低蓝豁免**：`MP < 1000` **且** 身上有 43 或 44 时才挡 ——
    //         没有那两个状态时，低蓝照样能用失衡。
    //         （⚠️ 43/44 在官方表里的名字是「衰弱」「濒死」，
    //           不是食物 —— 参考实现就是查这两个 id，我们照做。）
    //      ③ **走位开关**：移动中允许拿失衡当填充（QT `失衡走位`，默认开）。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>失衡在 293 / 296 / 1046 这三张图里禁用</summary>
    public override uint[] 自身AOE地图黑名单 => new[] { 293u, 296u, 1046u };

    /// <summary>低蓝豁免状态：43「衰弱」/ 44「濒死」在身时，低蓝才真的挡</summary>
    public override uint[] 自身AOE低蓝豁免状态 => new[] { 43u, 44u };

    /// <summary>失衡可以当走位填充（参考的 `失衡走位` 默认开）</summary>
    public override bool 自身AOE可走位 => true;

    /// <summary>
    /// **心关的回血量（170/130）** —— 表 #115。
    ///
    /// [!] 数据来源：`dump_healpot.tsv` 直读
    ///      `贤者 24285 心关 … 特定攻击魔法命中后，令带有关心状态的目标恢复体力 恢复力：170/130`
    ///      —— 也就是**贤者所有伤害技能**命中后，都会给「关心」目标回这一口。
    ///      （170 = 带 XX 特性 / 130 = 不带，同一格的两种取值；表里统一记 170。）
    ///
    /// [!] 为什么它算在**伤害技能**头上而不是"心关自己"头上：
    ///      心关本身是 5 秒 CD 的挂载技能、不回血；
    ///      真正回血的是**打出去的那些攻击魔法**。
    ///      所以"这一发能回多少血"要挂在伤害技能上，
    ///      AI 在权衡「打输出 vs 读条治疗」时才看得到这个正收益。
    ///
    /// [!] 只影响**给 AI 看的候选注释**，不参与本地威力比较。
    /// </summary>
    public override int 回血量(uint 技能Id)
    {
        if (技能Id == 0) return 0;

        // 贤者的"特定攻击魔法"= 注药系列 / 发炎系列 / 失衡系列 / 箭毒系列
        foreach (var id in new[]
                 {
                     SpellIds.取("注药"), SpellIds.取("注药II"), SpellIds.取("注药III"), 基础输出,
                     SpellIds.取("发炎"), SpellIds.取("发炎II"), SpellIds.取("发炎III"),
                     SpellIds.取("失衡"), SpellIds.取("失衡II"), 群体输出,
                     SpellIds.取("箭毒"), SpellIds.取("箭毒II"),
                 })
        {
            if (id != 0 && id == 技能Id) return 心关回血量;
        }

        return 0;
    }

    /// <summary>心关每次触发的回血量（恢复力 170）</summary>
    public const int 心关回血量 = 170;

    /// <summary>贤者的位移技 = 神翼（Icarus 24295，Lv74）</summary>
    public override uint 位移技 => SpellIds.取("神翼");

    /// <summary>
    /// **失衡本身的两道硬闸门**（表 #100 / #101）——
    /// 命中任何一条 ⇒ **这个技能这一拍整个不能用**（连单体输出也不能退回它）。
    ///
    /// [!] 为什么是覆写而不是新加一个方法名：
    ///      通用 resolver 只认识 `自身AOE硬闸门()` 这一个入口，
    ///      谁有独有规则谁覆写 —— 白魔/学者/占星不覆写就恒 false，行为不变。
    ///
    /// [!] 判据（IL 直读 `失衡.txt`）：
    ///      ① 当前地图 ∈ {293, 296, 1046} ⇒ 禁用
    ///      ② `MP < 1000` **且** 身上有 43「衰弱」/ 44「濒死」⇒ 禁用
    ///         （注意：**不是**"低蓝就禁用"—— 没有那两个状态时低蓝照样能用）
    ///      ③ 均衡中 ⇒ 禁用（失衡被换成群毒，交给 SGE_AoeDot）
    /// </summary>
    public override bool 自身AOE硬闸门()
    {
        try
        {
            // ③ 均衡中硬闸门（复刻 youshu 失衡.txt:26-29 `IsEukrasiaActiveOrBuffed`）：
            //    均衡状态下的失衡会被换成群毒（均衡失衡），不是我们要的瞬发 AOE ——
            //    这一拍整条禁用，群毒交给 SGE_AoeDot 走它自己的路线。
            if (JobApiHelper.均衡中) return true;
        }
        catch { }

        try
        {
            // ① 地图黑名单
            var 地图 = TimelineManager.实时副本Id();
            if (地图 != 0 && (地图 == 293u || 地图 == 296u || 地图 == 1046u)) return true;
        }
        catch { }

        try
        {
            // ② 低蓝 + 豁免状态
            if (蓝量.当前蓝量 < 自身AOE低蓝线)
            {
                foreach (var id in 自身AOE低蓝豁免状态)
                {
                    if (id != 0 && CharacterExt.我有光环(id)) return true;
                }
            }
        }
        catch { }

        return false;
    }

        public override uint 团队减伤 => SpellIds.取("坚角清汁");

    /// <summary>
    /// 均衡状态下"注药"会被游戏替换成"均衡注药"。
    /// 这时候基础输出必须让路 —— 否则 DoT 已经上过之后，
    /// Res_BaseDamage 还会再放一次均衡注药，白白覆盖掉 DoT。
    /// 交给 SGE_Dot 统一处理。
    /// </summary>
    public override bool 有特殊输出形态 => JobApiHelper.均衡中;

    public override uint 复活 => SpellIds.取("复苏");
    public override uint 驱散 => SpellsDefine.Esuna;
    public override uint 醒梦 => SpellsDefine.LucidDreaming;

    // 需求 3：蛇胆就是贤者的"群奶能力技资源"，没蛇胆就退回 GCD 群奶
    // ★ 保留数按队伍规模调整：四人本（单奶）留更多，八人本有搭档可以少留 ★
    public override bool 治疗资源充足 => JobApiHelper.蛇胆 >=
        HealTargetHelper.资源保留调整(HealSettings.Instance.蛇胆保留数);

    // 需求 4：脱战补蛇胆
    public override uint[] 脱战准备技能 => new[] { SpellIds.取("根素") };
}

// ══════════════════════════════════════════════════════════════════════
//  ★ 条件编译：这份源码会被编译进【两个】程序集 ★
//
//  [!] 背景：`BlueWhale.AutoHealerACR.csproj` 用
//        &lt;Compile Include="..\HealerACR\**\*.cs" /&gt;
//      把 HealerACR 的源码又编译了一遍 ==> 基础设施**各有一份静态状态**。
//      正在改成「BlueWhale 引用 HealerACR」（见该 csproj 的注释）。
//
//  [!] 但直接引用会带来一个问题：
//      AEAssist 会用反射扫 ACR 程序集里的职业入口类
//      （`GetExportedTypes` + `IsAbstract` + `IsClass` + `CreateInstance`）。
//      ==> HealerACR.dll 的入口类也会被扫到
//      ==> 职业列表里多出 "HealerACR·白魔"，**而用户只要小鲸鱼**。
//
//  [!] 解法：**在 BlueWhale 里把入口类声明成 `abstract`** ——
//      扫描器跳过抽象类；而 `BlueWhaleWhiteMageEntry` 继承它，
//      **是唯一的具体实现**。
//      ==> HealerACR 独立编译时仍是具体类（那个 ACR 依然可用）。
// ══════════════════════════════════════════════════════════════════════
#if HEALERACR_STANDALONE
    public class SGERotationEntry : HealerEntryBase
#else
    // 在 BlueWhale 里声明为 abstract —— 让 AEAssist 的扫描器跳过它
    // （`BlueWhaleEntry` 才是具体实现）。详见上面的条件编译说明。
    public abstract class SGERotationEntry : HealerEntryBase
#endif
{
    private readonly SGESpellTable _spells = new();

    public override Jobs TargetJob => Jobs.Sage;
    public override string OverlayTitle => "小鲸鱼 · 贤者（日随）";

    public override string Description =>
        "日随用贤者 ACR。自动心关、均衡 DoT、蛇胆优先、毒刺不浪费。\n" +
        "贤者 70 级转职，实际可用 70-100；脱战自动补蛇胆。";
    public override JobSpellTable Spells => _spells;

    protected override List<SlotResolverData> 构建决策队列_职业专属()
    {
        return new List<SlotResolverData>
        {
            new SlotResolverData(new Res_Sprint(), SlotMode.Always),              // 脱战自动疾跑
            new SlotResolverData(new Res_OffensiveAbility(_spells), SlotMode.Always),   // 按错题集：能力技进 Always，不受 GCD 就绪的影响
            // 宠物不在场就召唤（学者的朝日召唤）—— 排在脱战准备之前
            new SlotResolverData(new Res_SummonPet(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_PrepareResources(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_Raise(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_Esuna(_spells), SlotMode.Gcd),
            // ★ 「必须奶满」机制 —— 插在最前面，理由是行号才是优先级。
            //   这类 debuff 是“奶到 100% 才解除”，晚一个 GCD 人就没了；
            //   而 Check() 的返回值**不参与仲裁**（反汇编已证），只有行号算数。
            //   原来它排在所有治疗 GCD 之后 —— 场上有人被再生/医济抢走 GCD 时，
            //   就永远轮不到它。详见 Res_MustFullHeal 的类注释。
            new SlotResolverData(new Res_MustFullHeal(_spells), SlotMode.Gcd),
            // ★ 预铺 / 瞬发治疗能力技 —— **必须在所有治疗 GCD 之前**。
            //   需求是“治疗应该优先能力技 / 不读条的技能”，
            //   而 Check() 的返回值**不参与仲裁** —— 排在哪一行才算数。
            //   参考实现的 resolver 列表也是能力技在前（IL 直证）。
            //   详见 Res_InstantHealAbility 的类注释。
            new SlotResolverData(new Res_HealEmergency(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_InstantHealAbility(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_GroupShield(_spells), SlotMode.Gcd),

            // ══════════════════════════════════════════════════════════════
            //  ★★ 槽序对齐参考实现（表 #110 / #111）★★
            //
            //  [!] 参考的贤者槽序（IL 直读 `贤者技能策略` 的 cctor，逐个 newobj）：
            //        1 爆发药 · 2 过路圣人复活 · 3 复苏 · 4 **自动减伤**
            //        5 康复 · 6 群盾 · 7 预后 · 8 自生 · 9 智慧之爱 · 10 寄生
            //        11 单盾 · 12 诊断 · 13 输血 · 14 自动单奶 · 15 拯救
            //        16 醒梦 · **17 心关 · 18 根素** · 19 心神风息 · 20 强制发炎
            //        21 即刻注药 · 22 群DOT · 23 双DOT · 24 DOT
            //        25 移动走位即刻注药 · 26 发炎 · 27 箭毒 · 28 贤炮 · 29 失衡
            //
            //  [!] 我们原来错在两处：
            //      ① **团减在第 30/31 位** —— 输出 GCD 一路抢在它前面，
            //         等轮到它时团减窗口常常已经过了 ✗
            //      ② **心关/根素在 27/29 位** —— 它们是**资源类**，
            //         让位于输出 = 心关掉了没人补、根素满了没人收 ✗
            //
            //  [!] 现在按参考提前：
            //       · 自动减伤（自己 + 团队）→ **紧跟在群盾之后、输出之前**
            //       · 心关 / 根素 → **醒梦之后、心神风息之前**
            //       · 其余保持原相对次序（各自都有完整守卫，提前不会"抢错"）
            //
            //  [!] 为什么提前是安全的（和 #94 同一套论证）：
            //       每条 resolver 的 `Check()` 自带完整守卫
            //       （移动中 / 资源 / 血线 / 时间轴 / 敌人数量 / 团减快照）；
            //       返回值不参与仲裁，**只有行号算优先级**（反汇编已证）。
            //       而"已有减伤就不再叠"由 `团减快照.已有减伤()` 兜着 ——
            //       参考那边也有 `HasActiveMitigation(1)`，同一个语义。
            // ══════════════════════════════════════════════════════════════
            new SlotResolverData(new Res_SelfMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_TeamMitigation(_spells), SlotMode.OffGcd),

            // 心关 / 根素：**资源类，排在输出之前**
            new SlotResolverData(new SGE_Kardia(_spells), SlotMode.OffGcd),
            new SlotResolverData(new SGE_Rhizomata(), SlotMode.OffGcd),
                        // ══════════════════════════════════════════════════════════════
            //  ★ 移动中开即刻咏唱 ★
            //
            //  [!] 为什么需要（用户实测："全程移动 ai 不奶了"）：
            //      `SpellUtil.移动中能放()` 只**尊重**即刻 buff，
            //      **没有任何地方主动开它** —— 全项目只有 `Res_PotionTail`
            //      （爆发药）和 `Res_Raise`（拉人）用了即刻。
            //      => 移动中所有读条治疗/盾全被挡住，oGCD 治疗资源一空就一口都放不出来。
            //
            //  [!] 位置：在第一个 GCD 治疗之前（否则那个 GCD 会被别处抢走），
            //      但在 `Res_MustFullHeal` / `Res_HealEmergency` / `Res_Raise` 之后
            //      —— 那些是真救命，不能被"开即刻"抢在前面。
            //
            //  [!] 它自己会判"真的有东西需要即刻吗"（血线告急 / 马上挨大伤害），
            //      不是一移动就烧 60 秒 CD —— 详见 Res_移动开即刻 的注释。
            // ══════════════════════════════════════════════════════════════
            new SlotResolverData(new Res_移动开即刻(_spells), SlotMode.Gcd),
            // ★ 紧跟着：即刻已经在身上时，把这一发打在**最高档的输出 GCD** 上
            //   （奶妈的输出 GCD 全是读条的，不放这条移动中就一个都打不出来）。
            new SlotResolverData(new Res_MoveInstantOutput(_spells), SlotMode.Gcd),
new SlotResolverData(new Res_HealAoEGcd(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealSingleGcd(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealShield(_spells), SlotMode.Gcd),
            // ══════════════════════════════════════════════════════════════
            //  ★ 顺序修正：**DoT 必须在 AOE 之前**（对齐两套参考实现的槽序）★
            //
            //  [!] 原来这里是 AOE 在前，注释写的是
            //        「AOE 优先：3 个以上敌人时不该先给单只怪挂 DoT」。
            //
            //  [!] 但参考实现**两套的槽序都是 DoT 在 AOE 之前**（IL 实证）：
            //        shiyuvi 41 槽：… 11 Dot · 12 ForceRuin2 · 13 AOE · 14 BaseGCD
            //        youshu  34 槽：… 28 DOT · 29 即刻极炎法 · 30 裂阵法 · 31 毁坏
            //      ==> 「DoT 在 AOE 之前」这一点**两套没有分歧**。
            //
            //  [!] 而且原来那句注释**与代码不符**：`Res_AoEDamage` 的门槛是
            //        `AOE最少敌人数 = 2`，**不是 3** ==>
            //        2 只敌人时 AOE 就够格了，DoT 会被推后一个 GCD。
            //
            //  [!] 差异范围（枚举验证过，不是猜）：
            //        · 1 只敌人：两边一样（AOE 门槛 2 不满足，落下来补 DoT）
            //        · DoT 还满着：两边一样（`Dot补判` 挡住，落下来放 AOE）
            //        · **只有「≥2 敌 且 DoT 到该补的时候」才有差异**
            //
            //  [!] 为什么改顺序而不是把门槛提到 3：
            //        门槛 2 = youshu 在 82 级以下的档位（它按等级分档 `>=82 ? 3 : 2`）；
            //        提门槛会**同时改变「2 只敌人要不要打 AOE」**这个更基础的行为。
            //
            //  [!] 两个 resolver 的守卫都是完整的（移动中可用 / 值得上Dot / Dot补判 /
            //      黑名单 / AOE 门槛），提前不会造成"移动中卡住"那类问题。
            // ══════════════════════════════════════════════════════════════
            // ★ 多目标 DoT **排在单体 DoT 之前**（对照实现槽序 `双DOT` → `DOT`）★
            //   [!] 两根 resolver 共用 `Dot补判` 那根保险丝，谁先跑谁补；
            //       放前面才能保证多目标时先铺"还没毒的那只"。
            //   [!] 它自带"候选够 2 个"的判据，单体场景直接跳过 ⇒ 不抢单体毒。
            // ★ 群 DOT（均衡失衡，Lv82+ 三怪以上）—— **排在单体毒之前** ★
            //   [!] 它是"一发铺开一整片"的技能，收益量级和单体毒不同：
            //       三怪场景下先铺群毒，单体毒随后补漏（两者共用 DoT 保险丝）。
            //   [!] 槽位必须在 `Res_MultiDot` 之前 —— 返回值不参与仲裁，
            //       位置才算优先级（反汇编已证）。
            new SlotResolverData(new SGE_AoeDot(), SlotMode.Gcd),
            new SlotResolverData(new Res_MultiDot(_spells), SlotMode.Gcd),
            new SlotResolverData(new SGE_Dot(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_AoEDamage(_spells), SlotMode.Gcd),
            new SlotResolverData(new SGE_Phlegma(), SlotMode.Gcd),   // 发炎：DoT 之后才轮到它
            new SlotResolverData(new SGE_Toxikon(), SlotMode.Gcd),
            // ⚠️ 位置很重要：必须在 `Res_MoveGcd`（移动填充）**之前** ——
            //   药尾声补刀最该生效的场景就是**移动中**（读条放不出来），
            //   排在移动填充后面等于**永远选不到**。
            new SlotResolverData(new Res_PotionTailDamage(_spells), SlotMode.Gcd),
            // 基础输出：**必须排在 Res_MoveGcd 之前**。
            // 破阵法（近战填充技）也是瞬发，该由它优先选；
            // 排在移动填充之后会把 GCD 抢成毁坏（实测过的问题）。
            new SlotResolverData(new Res_BaseDamage(_spells), SlotMode.Gcd),
            // 移动填充兜底：当 Res_BaseDamage 因为基础输出是读条技而让位时顶上。
            new SlotResolverData(new Res_MoveGcd(_spells), SlotMode.Gcd),

            new SlotResolverData(new SGE_Philosophia(), SlotMode.OffGcd),            // 智慧之爱（Lv100 群疗大招）
            new SlotResolverData(new Res_HealAoEAbility(_spells), SlotMode.OffGcd),
            new SlotResolverData(new SGE_CholeOverflow(), SlotMode.OffGcd),
            new SlotResolverData(new Res_LucidDreaming(_spells), SlotMode.OffGcd),
            new SlotResolverData(new SGE_Psyche(), SlotMode.OffGcd),
            // ⚠️ 默认关（和参考一致：神翼在参考里只是个热键，策略槽里没有它）
            new SlotResolverData(new Res_Dash(_spells), SlotMode.OffGcd),
            // ══════════════════════════════════════════════════════════
            //  ★ 贤者**不再单独排**「活化」与「混合」（表 #128 / #129）★
            //
            //  [!] 参考里这两个都**不是独立 resolver**：
            //       · **活化（Zoe）** 是 `群盾.Build` 里的一步
            //         （`shouldUseZoe` 由群盾的 Check 算出，Build 先塞活化再塞群盾）；
            //       · **混合（Krasis）** 是 `自动单奶.Build` 里的一步
            //         （`useKrasis` 针对**那一发选出来的 target**，两个动作同目标）。
            //
            //  [!] 我们原来两条都做成了独立 resolver ⇒ 会和真正的治疗**错开**：
            //       活化的增疗窗口开在"没有盾要铺"的时刻；
            //       混合加在坦克身上而那一发单奶治的是别人 ✗
            //       ⇒ 两条都已挪进对应的 Build（见 `Res_GroupShield.Build`
            //         与 `Res_InstantHealAbility.Build`）。
            //
            //  [!] 这里**不排**它们，但两个 resolver 类都还留着 ——
            //      别的职业/别的场景没有这两条替代路径，将来的扩展也要用。
            // ══════════════════════════════════════════════════════════
            new SlotResolverData(new Res_KardiaBoost(_spells), SlotMode.OffGcd),          // 拯救
            new SlotResolverData(new Res_Emergency(_spells), SlotMode.OffGcd),            // 寄生清汁
            new SlotResolverData(new Res_GroupHoT(_spells), SlotMode.OffGcd),             // 自生
            new SlotResolverData(new Res_GroupShieldAbility(_spells), SlotMode.OffGcd),   // 泛输血
            new SlotResolverData(new Res_BigAoEHeal(_spells), SlotMode.OffGcd),           // 整体论/魂灵风息
            new SlotResolverData(new Res_LimitBreak(), SlotMode.OffGcd),                  // 极限技
        };
    }

    protected override void 构建QT()
    {
        base.构建QT();
        加职业开关("自动心关", true);
        加职业开关("根素", true);
        加职业开关("箭毒", true);
        加职业开关("群DOT", true);
        加职业开关("发炎", true);
        // ★ 参考实现的发炎是**四 QT 分层**（表 #108）——
        //   这两个是"另两条路"：强制放 / 反过来留着。
        加职业开关("强制发炎", false);   // 不看任何条件，直接放（手动用）
        加职业开关("保留发炎", false);   // 保住充能，只在移动/溢出时交
        // ★ 心神风息有**自己的开关**（参考里它不复用「输出」，表 #127）
        加职业开关("心神风息", true);
        // ★ 位移技（神翼）默认**关** —— 和参考一致：参考只把它做成热键，
        //   策略槽里没有它（表 #130）。打开后才会"走位中飞向最低血坦克"。
        加职业开关("位移技", false);
    }
}

// ============================================================================
//  贤者专属
// ============================================================================

/// <summary>均衡注药（DoT）：没开均衡就先开均衡。</summary>
public class SGE_Dot : ISlotResolver
{
    private readonly JobSpellTable _t;

    public SGE_Dot(JobSpellTable table) => _t = table;

    private static uint 均衡 => SpellIds.取("均衡");
    private static uint 技能 => SpellIds.取("均衡注药");

    public int Check()
    {
        if (!HealQt.GetQt("DOT", true)) return -100;
        if (!HealQt.GetQt("输出", true)) return -101;
        if (蓝量.低蓝停手()) return -9;   // 蓝留给治疗
        if (!SpellUtil.已解锁(均衡)) return -2;

        var target = HealTargetHelper.当前目标();
        if (target == null) return -1;
        if (!HealTargetHelper.木桩模式 && target.血量比例() <= HealSettings.Instance.不挂Dot血线) return -3;

        // ⚠️ 判断走**共享**的 Dot补判（含防死循环保险丝）——
        //    原来这里是自己写的一套（自留 `上次DoT` 字段），
        //    和 `Res_Dot` 的逻辑各写一份 → 改一处漏一处。
        //    而且 AI 建议那条路也走 Dot补判，三边必须看同一份数据
        //    （开发约定 F③ 的推广）。
        if (!Dot补判.该补(target, _t.所有DotBuff, HealSettings.Instance.Dot持续时间 - 3f))
            return -4;

        return SpellUtil.可用(均衡) ? 6 : -1;
    }

    public void Build(Slot slot)
    {
        var target = HealTargetHelper.当前目标();
        if (target == null) return;

        // 不在均衡中：这一轮只开均衡，等服务器确认了下一轮再放 DoT。
        // 同一帧里按完均衡就立刻放"均衡注药"是放不出来的。
        if (!JobApiHelper.均衡中)
        {
            var ek = SpellUtil.Get(均衡);
            if (ek != null) slot.Add(ek);
            return;
        }

        var 当前 = SpellUtil.当前形态(技能);
        if (当前 == null) return;

        slot.Add(new Spell(当前.Id, target));

        // ⚠️ 记到**共享**的 Dot补判（不是本类的静态字段）——
        //    保险丝必须和 Res_Dot / AI 建议路径共用，否则那条路能绕过它。
        Dot补判.记一次施放(target);   // 按目标记
        Dot黑名单.记按下(target, _t.所有DotBuff);
    }
}

/// <summary>
/// 箭毒：毒刺快溢出或打 AOE 时泄掉。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 按参考实现（`箭毒.txt`）补齐的完整闸门网（表 #106 / #109）★
///
///  [!] 原来只有三条：毒刺 &gt; 0、`毒刺 &lt; 泄刺阈值 且 周围 &lt; 2` 就挡、木桩/攒资源让路 ✗
///      缺了：目标与视线、走位延迟、攒爆发/倾泻资源两档、**AOE 智能换目标**。
///
///  ── 闸门（顺序即参考的 IL 顺序）──
///    ① QT「箭毒」开着、等级够、没被静默
///    ② 有目标 + 不是"打上去没用"（无敌/反射）+ 25 米内且没死
///    ③ **走位延迟**：刚起步那几帧不按（`移动时长 < 箭毒延迟毫秒` → -8）
///    ④ **攒爆发中**（占卜临近）且**没在倾泻资源** ⇒ 毒刺 ≤ 2 时留着（-30）
///    ⑤ **倾泻资源中** ⇒ 毒刺 == 0 才停（-9）
///    ⑥ **保留红豆**（= 我们的 `箭毒泄刺阈值`）：毒刺 ≤ 保留数 ⇒ 留着（-9）
///    ⑦ 毒刺 ≤ 0 ⇒ -9
///    ⑧ 技能要可用（视线/射程）
///    ⑨ **倾泻资源 ⇒ 无条件交**（返回 50）
///    ⑩ **智能 AOE 换目标**：以当前目标为锚找一个能覆盖 ≥2 个敌人的圆形落点
///       （半径 5 米）—— 换到了就返回 **15**（比走位那条更高）
///    ⑪ 落点必须在 25 米内、可见
///    ⑫ 没换目标时：**只有在移动中且 GCD 已就绪**才交（返回 10）
///       —— 箭毒是瞬发，站着的时候该让给读条的高威力填充
///
///  [!] 「攒爆发」用**占卜 CD** 当判据（我们不做职业级 QT）：
///      `占卜 CD &lt; 8000ms` 视为攒爆发窗口，和 `占卜临近()` 同一个口径。
///  [!] 「倾泻资源」用**一键爆发**（`HealQt.GetQt("一键爆发")`）当判据。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class SGE_Toxikon : ISlotResolver
{
    // 箭毒 66 级、箭毒II 82 级，用等级链取当前该用的那个
    private static uint 技能 => SpellUtil.取已解锁(SpellIds.取("箭毒II"), SpellIds.取("箭毒"));

    /// <summary>智能 AOE 落点的半径（箭毒是 5 米圆形）</summary>
    private const float 落点半径 = 5f;

    /// <summary>落点至少覆盖几个敌人才值得换目标</summary>
    private const int 落点门槛 = 2;

    /// <summary>Check 里选好的落点，给 Build 用（避免判 A 放 B）</summary>
    private static IBattleChara? 本帧目标;

    public int Check()
    {
        本帧目标 = null;

        if (!HealQt.GetQt("输出")) return -100;
        if (!HealQt.GetQt("箭毒", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        // ② 目标与视线
        var 目标 = 输出目标.选();
        if (目标 == null || !目标.对象有效()) return -1;
        if (敌人状态.攻击无效(目标)) return -1;
        if (目标.CurrentHp <= 0) return -2;

        try
        {
            if (目标.Distance(Core.Me!) > 25f) return -2;
        }
        catch { }

        // ③ 走位延迟：刚起步那几帧不按
        try
        {
            var 移动 = SpellUtil.移动时长毫秒();
            if (移动 > 0 && 移动 < HealSettings.Instance.箭毒延迟毫秒) return -8;
        }
        catch { }

        var 毒刺 = JobApiHelper.毒刺;

        // ④ 攒爆发中且没在倾泻 ⇒ **毒刺 > 2 就留着**
        //   [!] 表外审计修正：原来写的是 `毒刺 <= 2` —— **判据写反了** ✗
        //       参考 IL（`箭毒.txt:68-72`）：
        //         `IL_00bb get_Addersting` / `IL_00c0 ldc.i4.2` / `IL_00c1 bgt.s IL_00c6`
        //         / `IL_00c3 ldc.i4.s -30`
        //       `bgt` = "栈顶(int32) > 栈底(int32)"，此时栈是 (2, Addersting)
        //       ⇒ 判的是 **`2 > Addersting`**，即 **Addersting < 2** 才留（-30）。
        //       等价写法就是 `毒刺 > 2` 时不拦 —— 但语义上更清楚的是
        //       "攒爆发窗口里，**攒够 2 颗以上**才值得留着爆发用"。
        //   [!] 写反的后果：攒爆发窗口（占卜 CD < 8 秒，而占卜 120 秒 CD ⇒
        //       每两分钟里有一大段被算作"攒爆发"）里，毒刺少的时候反而被压住，
        //       毒刺多的时候反而乱交 —— 正好和参考相反。
        if (占卜临近() && !倾泻资源中())
        {
            if (毒刺 > 2) return -30;
        }

        // ⑤ 倾泻资源中 ⇒ 毒刺 0 才停
        if (倾泻资源中())
        {
            if (毒刺 <= 0) return -9;
        }

        // ⑥ 保留红豆
        //   [!] 表外审计修正：原来多了一条参考**没有**的 `&& 周围敌人数量() < 2` ✗
        //       参考 IL（`箭毒.txt:81-88`）只比毒刺一个量：
        //         `IL_00dd ldstr "保留红豆"` / `IL_00e9 get_Addersting`
        //         / `IL_00ee get_保留红豆数量` / `IL_00f3 bgt.s IL_00f8` / `IL_00f5 ldc.i4.s -9`
        //       ⇒ **毒刺 ≤ 保留数 ⇒ -9**，和"周围有几个敌人"无关。
        //   [!] 那条多出来的条件在**单体 Boss 战**里是致命的：
        //       周围敌人恒为 1（< 2 恒真）⇒ 毒刺 ≤ 阈值时永远不交
        //       ⇒ 单体场景下箭毒几乎被封死 ✗
        if (毒刺 <= HealSettings.Instance.箭毒泄刺阈值) return -9;

        // ⑦ 没豆子就没什么可放的
        if (毒刺 <= 0) return -9;

        // ⑧ 技能要可用
        if (!SpellUtil.可用(技能)) return -3;

        // ⑨ 倾泻资源 ⇒ 无条件交
        if (倾泻资源中())
        {
            本帧目标 = 目标;
            return 50;
        }

        // ⑩ 智能 AOE 换目标：以当前目标为锚找能覆盖 ≥2 个敌人的落点
        try
        {
            var 落点 = 智能选目标.圆形最优(落点半径, 落点门槛);
            if (落点 != null && 落点.对象有效()
                && HealTargetHelper.自身周围敌人数量(落点半径) >= 落点门槛)
            {
                本帧目标 = 落点;
                return 15;
            }
        }
        catch { }

        // ⑪/⑫ 没换目标：只有在**移动中且 GCD 已就绪**才交
        //     [!] 箭毒是瞬发 —— 站着的时候该让给读条的高威力填充（注药）。
        if (!SpellUtil.在移动()) return -1;

        // ⚠️ 表外审计 S3：缺「GCD 已就绪」门（注释一直写着有，代码里其实没判）✗
        //     [!] 箭毒是**瞬发但占 GCD** 的技能：移动中 GCD 还在转时硬塞，
        //         这一发会直接失败（GCD 没好）⇒ 白点一下、还打断注药的填充节奏。
        //     [!] 参考 IL（`箭毒.txt:146`）在这里是 `GCDHelper::GetGCDDuration` + `brfalse`
        //         —— 语义是「GCD 没转好（剩余 > 0）⇒ 让位」。
        //         我们这边读「GCD 剩余」用 `GetGCDCooldown()`（和 `可以插能力技` 同源），
        //         统一 < 600ms 算就绪（网络/排队余量）。
        try
        {
            if (GCDHelper.GetGCDCooldown() >= 600) return -1;
        }
        catch { }

        if (!HealTargetHelper.木桩模式
            && HealSettings.Instance.时间轴攒资源
            && TimelineManager.未来有减伤(8.0)) return -5;

        本帧目标 = 目标;
        return 10;
    }

    public void Build(Slot slot)
    {
        var 目标 = 本帧目标;
        if (目标 == null || !目标.对象有效()) return;

        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;

        slot.Add(new Spell(spell.Id, 目标));
    }

    /// <summary>占卜 CD &lt; 8 秒 = 攒爆发窗口（与 `AST_Divination.占卜临近` 同口径）</summary>
    private static bool 占卜临近()
    {
        try
        {
            var 占卜 = SpellIds.取("占卜");
            if (占卜 == 0) return false;

            var s = SpellUtil.Get(占卜);
            if (s == null) return false;

            return s.Cooldown.TotalMilliseconds < 8000;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>倾泻资源中（用「一键爆发」开关当判据，我们不做职业级 QT）</summary>
    private static bool 倾泻资源中()
    {
        try { return HealQt.GetQt("一键爆发", false); }
        catch { return false; }
    }
}

/// <summary>心关：给坦克挂上，之后打输出顺带回血。</summary>
public class SGE_Kardia : ISlotResolver
{
    private readonly JobSpellTable _t;

    public SGE_Kardia(JobSpellTable table) => _t = table;

    private static uint 技能 => SpellIds.取("心关");

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("自动心关", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        // ★ oGCD 队列深度闸门（参考口径 `CanUseOffGcd(1)` —— **心关是 1，不是 2**）
        //   [!] 参考里贤者的 11 个 oGCD 中**只有心关与醒梦是 `CanUseOffGcd(1)`**，
        //       其余 9 个（根素/拯救/寄生/自生/输血/智慧之爱/心神风息/自动减伤/…）全是 **2**。
        if (!OffGcd闸门.可以排(1)) return -4;

        // [!] **必须用 `主坦()`，不能用血量最低的坦克()**（MT/ST 修正）——
        //     心关是打敌人顺带治 MT的机制，**钉错人整场少一大块治疗**。
        //     而 `血量最低的坦克()` 是跟着谁掉血跑的 ==> 八人本 ST 掉血时会跑到 ST 上。
        //     用户原话：「t应该看的是**主仇恨的 mt**，八人本直接就区分 mtst」。
        var tank = 贤者心关.目标();
        if (tank == null) return -1;
        if (AuraIds.有心关(tank)) return -3;

        return SpellUtil.可用(技能) ? 3 : -1;
    }

    public void Build(Slot slot)
    {
        var tank = 贤者心关.目标();
        if (tank == null) return;
        slot.Add(new Spell(技能, tank));
    }
}

/// <summary>根素：补蛇胆，有豆子就不放。</summary>
public class SGE_Rhizomata : ISlotResolver
{
    private static uint 技能 => SpellIds.取("根素");

    public int Check()
    {
        // 木桩也要补蛇胆 —— 补了才有得花，属于资源循环
        if (!HealQt.GetQt("根素", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;

        // ★ oGCD 队列深度闸门（参考口径 `CanUseOffGcd(2)` —— 根素在连发池里）
        if (!OffGcd闸门.可以排(2)) return -4;
        if (JobApiHelper.蛇胆 >= 2) return -3;   // ★ 2026-10-04：对照实现是「≤1 颗就补」，原来 >=1 ⇒ 只在 0 颗时补，平均少一颗 ✗

        // ★ 低蓝时不补蛇胆（和学者的补豆同一个道理）★
        //   补资源是为了花出去换治疗/输出，低蓝时该省蓝。
        if (蓝量.低蓝停手()) return -4;

        if (!CharacterExt.可以插能力技()) return -6;

        return SpellUtil.可用(技能) ? 2 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>
/// 贤者心关目标选择的**完整阶梯**。
///
/// ★ 2026-10-04 新增：原来只取 `主坦()`，为空直接放弃 ⇒ 无 T 场景永远挂不上心关 ✗
///
/// ══════════════════════════════════════════════════════════════════
///  [!] 按参考实现（`心关.txt` 的 `ResolveKardiaTarget`）逐级对齐：
///
///    **坦克池**（`CastableTanks`，全部要过 `CanUseKardiaTarget`）：
///      每只坦克都读出四个量：`hp`、`HasKardion`（**带出 `fromCurrentSage`**）
///      ① **低于 75% 的坦克**（阈值 0.75，取最低）→ 最优先
///      ② **当前敌人目标正在打的那只坦克**（target-of-target）→ 次优先
///      ③ **没有「我挂的关心」的坦克**里血最低的
///         ⚠️ 这一级**原来缺失** ✗ —— 它才是"该换人了"的正解
///      ④ 我自己身上**已经有心关**（`Me.HasAura(2604)`）⇒ **返回 null，别动** ✗
///         ⚠️ 这一级**原来也缺失** —— 缺了它就会一直重挂自己
///      ⑤ 血最低的坦克（兜底）
///
///    **坦克池为空** ⇒ 非坦克阶梯（`LowestNonTankKardiaTarget`）：
///      ⑥ 非坦克里**低于 50% 且血最低**的（排除已有「我挂的关心」的）
///      ⑦ 非坦克里血最低的（❓ 若"我已有心关"则返回 null）
///      ⑧ 非坦克里血最低的
///      ⑨ 都没有 ⇒ 自己（但**我已有心关时返回 null**，别重复挂）
///
///  [!] 三个结构性差异（表 #112）就是上面 ③ / ⑥ 的"取最低而不是第一个" /
///      ⑦ 的"已有心关就返回 null"：
///        · ① 低血池**必须排除已有「我挂的关心」的坦克**（否则它一直霸着这一级）
///        · ② 缺"**没有关心的坦克优先**"这一级
///        · ③ 非坦克取"**最低**"而不是"第一个 &lt; 50%"
///
///  [!] `Me.HasAura(2604)` 那道收尾闸门（表 #113）：
///      **自己身上有心关时一律返回 null** —— 心关是"给自己"的 buff，
///      挂在身上说明这一发已经生效了，再按一次就是空转（而且会顶掉队友的关心）。
///
///  [!] 「关心」是挂在**目标**身上的 2605，且带 `SourceId` —— 必须用
///      `AuraIds.有心关(目标)`（fromMe），不能只查 2605（别人奶妈的关心不算我的）。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 贤者心关
{
    private const uint 心关技能 = 24285;

    /// <summary>低血坦克的阈值（参考口径 0.75）</summary>
    private const float 低血阈值 = 0.75f;

    /// <summary>非坦克的阈值（参考口径 0.50）</summary>
    private const float 非坦阈值 = 0.50f;

    public static IBattleChara? 目标()
    {
        try
        {
            // 5 秒内刚按过心关就不再换（对齐对照实现的 RecentlyUsed(24285, 5000)）
            if (AEAssist.Helper.SpellExtension.RecentlyUsed(心关技能, 5000)) return null;

            // 目标选择开始前先看一次"我身上有没有心关"（收尾闸门用）
            var 我有心关 = AuraIds.我有心关();

            // ★ 判 对象有效()：换图时成员被释放但仍非 null（哨兵 0x12345679），只判 null 会崩
            var 坦克池 = PartyHelper.CastableTanks?
                .Where(r => r != null && r.对象有效() && r.活着())
                .ToList();
            if (坦克池 != null && 坦克池.Count > 0)
            {
                IBattleChara? 低血 = null;      var 低血比 = 低血阈值;
                IBattleChara? 敌人目标 = null;
                IBattleChara? 无关心 = null;    var 无关心比 = float.MaxValue;
                IBattleChara? 最低 = null;      var 最低比 = float.MaxValue;
                var 有人带我的关心 = false;

                // 敌人正在打谁（target-of-target）
                IBattleChara? 敌人打的人 = null;
                try { 敌人打的人 = HealTargetHelper.当前目标()?.GetCurrTarget(); } catch { }

                foreach (var t in 坦克池)
                {
                    if (!可挂心关(t)) continue;

                    var 比 = t.有效血量比例();

                    if (AuraIds.有心关(t))
                    {
                        // 这只坦克身上有**我挂的**关心 ⇒ 不参与"低血/无关心"两级
                        有人带我的关心 = true;
                    }
                    else
                    {
                        // ① 低血坦克（排除已有我挂的关心的 —— 表 #112①）
                        if (比 < 低血比) { 低血比 = 比; 低血 = t; }

                        // ② 敌人当前目标
                        if (敌人打的人 != null && t.GameObjectId == 敌人打的人.GameObjectId) 敌人目标 = t;

                        // ③ **没有我挂的关心**的坦克里血最低的（表 #112② 新增）
                        if (比 < 无关心比) { 无关心比 = 比; 无关心 = t; }
                    }

                    // ⑤ 血最低的坦克（兜底，不看关心）
                    if (比 < 最低比) { 最低比 = 比; 最低 = t; }
                }

                if (最低 == null) goto 非坦克;   // 坦克池里一个能挂的都没有

                if (低血 != null) return 低血;

                // ② 敌人当前目标（target-of-target）= 参考的"接怪那一下最准"
                if (敌人目标 != null) return 敌人目标;

                // ②′ **退回我们自己的粘滞 MT**（表 #115 的"两套主坦口径"）——
                //     [!] 参考的 target-of-target 在**没怪打坦克的那一刻**会取不到
                //         （转场 / 死刑间隔 / 小怪刚死），那一瞬间参考没有这一级兜底。
                //     [!] 我们的 `主坦()` 是**粘滞 + 按"怪在看谁"投票**的，
                //         刚好补上这个空档，而且口径同源：**都看 `TargetObjectId`**。
                //     [!] 这样"心关钉谁"与"治疗血线看谁"就统一到同一个定义上了 ——
                //         不会再出现"心关钉 A、判据看 B"。
                try
                {
                    var 粘滞MT = HealTargetHelper.主坦();
                    if (粘滞MT != null && 粘滞MT.对象有效() && 粘滞MT.活着()
                        && 粘滞MT.GameObjectId != (Core.Me?.GameObjectId ?? 0))
                        return 粘滞MT;
                }
                catch { }

                // ③ 该换人了：有坦克没带我的关心
                if (有人带我的关心)
                {
                    // ④ 我自己已有心关 ⇒ 别动（表 #113 收尾闸门）
                    if (我有心关) return null;

                    // 有坦克带着我的关心、但都不是它了 ⇒ 换到"没关心"的那只
                    if (无关心 != null) return 无关心;
                }

                if (无关心 != null) return 无关心;
                return 最低;
            }

        非坦克:
            // ⑥⑦⑧ 没有坦克 ⇒ 非坦克阶梯
            {
                IBattleChara? 非坦低于半血 = null; var 非坦低血比 = 非坦阈值;
                IBattleChara? 非坦最低 = null;     var 非坦最低比 = float.MaxValue;
                var 非坦带我的关心 = false;

                foreach (var a in PartyHelper.CastableAlliesWithin30)
                {
                    if (!可挂心关(a)) continue;
                    if (a.IsTank()) continue;
                    if (a.GameObjectId == (AEAssist.Core.Me?.GameObjectId ?? 0)) continue;   // 自己最后单独判

                    var 比 = a.有效血量比例();

                    if (AuraIds.有心关(a))
                    {
                        非坦带我的关心 = true;
                        continue;
                    }

                    // ⑥ **取最低**（不是"第一个低于 50%"，表 #112③）
                    if (比 < 非坦低血比) { 非坦低血比 = 比; 非坦低于半血 = a; }
                    if (比 < 非坦最低比) { 非坦最低比 = 比; 非坦最低 = a; }
                }

                if (非坦低于半血 != null) return 非坦低于半血;

                if (非坦带我的关心)
                {
                    if (我有心关) return null;
                    if (非坦最低 != null) return 非坦最低;
                }

                if (非坦最低 != null) return 非坦最低;
            }

            // ⑨ 都没有 ⇒ 自己（但我已有心关时返回 null，别重复挂）
            if (我有心关) return null;

            var 我 = AEAssist.Core.Me;
            if (我 != null && 我.对象有效() && 我.活着()) return 我;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 这只目标能不能挂心关（参考的 `CanUseKardiaTarget`）：
    /// 有效 + **不是敌人** + 活着。
    /// </summary>
    private static bool 可挂心关(IBattleChara? c)
    {
        if (c == null || !c.对象有效()) return false;

        try
        {
            if (c.IsEnemy()) return false;
            if (!c.活着()) return false;
        }
        catch { }

        return true;
    }
}
