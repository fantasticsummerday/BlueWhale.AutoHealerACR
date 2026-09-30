using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;
using HealerACR.Resolvers;
using HealerACR.Timeline;

namespace HealerACR.Rotations;

/// <summary>
/// 白魔法师技能表。技能 ID 走 SpellIds 内置表（中文名，已用 Lumina dump 核对）。
/// </summary>
public class WHMSpellTable : JobSpellTable
{
    public override Jobs Job => Jobs.WhiteMage;
    public override string 职业名 => "白魔法师";

    // 输出：写最初形态，运行时 CheckActionChange 自动升到当前等级那个
    // 直接列等级链，比依赖 CheckActionChange 可靠（它不保证一路递归到最高级）
    public override uint 基础输出 => SpellUtil.取已解锁(
        SpellIds.取("闪灼"),    // 82 级 Glare III
        SpellIds.取("闪耀"),    // 72 级 Glare
        SpellIds.取("崩石"),    // 64
        SpellIds.取("垒石"),    // 54
        SpellIds.取("坚石"),    // 18
        SpellIds.取("飞石"));   // 1
    public override uint 群体输出 => SpellUtil.取已解锁(SpellIds.取("豪圣"), SpellIds.取("神圣"));

    /// <summary>
    /// **白魔的 AOE 门槛：72 级起用 3**（不是 2）。
    ///
    /// [!] 算术依据（恰好 2 只怪时群体技是**负收益**）：
    ///     72-82：神圣 140×2 = 280  <  闪耀 290   -> 少 10 威力
    ///     82-94：豪圣 150×2 = 300  <  闪灼 310   -> 少 10 威力
    ///     94+  ：豪圣 150×2 = 300  <  闪灼 350   -> 少 50 威力（14.3%）
    ///
    /// [!] 72 级以下的单体填充技威力低（垒石 220 / 崩石 260），
    ///     2 只怪时神圣 280 是**正收益** ⇒ 门槛保持 2。
    /// </summary>
    public override int AOE门槛(int 等级) => 等级 >= 72 ? 3 : 2;

    /// <summary>药尾声补刀用的瞬发填充技 —— 白魔：闪灼（25859）—— 基础输出，即刻下变瞬发。</summary>
    public override uint 药尾声填充技 => SpellIds.取("闪灼");
    /// <summary>
    /// **自身 AOE 技**（`CastType=2 Range=0`，以自己为中心的圆形攻击）。
    ///
    /// ⚠️ 数据依据（`tools\CastProbe` 读游戏 `CastType`/`Range`/`EffectRange`）：
    ///       神圣 139    Lv45  咏唱1.5  射程0  范围**8**  威力140
    ///       豪圣 25860  Lv82  咏唱1.5  射程0  范围**8**  威力150
    ///
    /// ⚠️ 半径是 **8 米** —— 比学者的破阵法（5 米）大，
    ///    所以不能共用同一个半径常量，必须按技能查。
    /// </summary>
    public override uint[] 自身AOE候选 => new[]
    {
        SpellIds.取("豪圣"),      // 82 级
        SpellIds.取("神圣"),      // 45 级
    };

    /// <summary>
    /// **威力档位** —— 数据逐字来自游戏数据宏（`tools\HealPotency raw`）。
    ///
    ///    飞石   119    Lv1   140
    ///    坚石   127    Lv18  190
    ///    垒石   3568   Lv54  220
    ///    崩石   7431   Lv64  260
    ///    闪耀   16533  Lv72  290
    ///    闪灼   25859  Lv82  if(等级>=94) 350 else 310
    ///    神圣   139    Lv45  140
    ///    豪圣   25860  Lv82  150
    ///
    /// ⚠️ 白魔是**纯读条**职业（除 DoT 外没有瞬发伤害技）——
    ///    所以"移动中该放什么"全靠 `移动填充技`，威力比较主要解决
    ///    **贴身时该不该用神圣/豪圣**。
    /// </summary>
    protected override (int 等级, int 威力)[]? 威力表(uint 技能Id)
    {
        if (技能Id == SpellIds.取("飞石"))   return new[] { (18, 190), (0, 140) };
        if (技能Id == SpellIds.取("坚石"))   return new[] { (54, 220), (0, 190) };
        if (技能Id == SpellIds.取("垒石"))   return new[] { (64, 260), (0, 220) };
        if (技能Id == SpellIds.取("崩石"))   return new[] { (72, 290), (0, 260) };
        if (技能Id == SpellIds.取("闪耀"))   return new[] { (82, 310), (0, 290) };
        if (技能Id == SpellIds.取("闪灼"))   return new[] { (94, 350), (0, 310) };
        if (技能Id == SpellIds.取("神圣"))   return new[] { (82, 150), (0, 140) };
        if (技能Id == SpellIds.取("豪圣"))   return new[] { (0, 150) };

        // ══════════════════════════════════════════════════════════════
        //  ★ Dot 三档 —— 补上（链条覆盖检查发现的缺口）★
        //
        //  [!] **不会导致选错技能**：DoT 走 `值得上Dot()`（判血量/血量倍数），
        //      **不比较威力**。所以这个缺口是"AI 看不到数值"，不是"选错"。
        //
        //  [!] 但补上仍然值得 —— 提示词里 `力N` 空着时，
        //      AI 判"该不该补 DoT"只能凭训练记忆。
        //
        //  数值来源：`Action:<id>` 页的结构化表头。
        //      ⚠️ DoT 技能页面里"威力："出现多次（直伤 + 每跳），
        //         这里填的是**每跳的 DoT 威力**（和 `HoT恢复力` 同一类语义）。
        // ══════════════════════════════════════════════════════════════
        if (技能Id == SpellIds.取("天辉")) return new[] { (0, 85) };
        if (技能Id == SpellIds.取("烈风")) return new[] { (0, 50) };
        if (技能Id == SpellIds.取("疾风")) return new[] { (46, 50), (0, 30) };

        return null;
    }

    /// <summary>
    /// **咏唱时间**（秒）—— 来自游戏 `Cast100ms`。
    ///
    /// ⚠️ 白魔的伤害技**全是 1.5 秒读条**（没有瞬发填充技）。
    ///    复唱都是 2.5 秒 ⇒ 都只占一个 GCD ⇒ 威力排序里咏唱时间约掉。
    /// </summary>
    public override IReadOnlyDictionary<uint, float> 咏唱时间表 { get; } = new Dictionary<uint, float>
    {
        [SpellIds.取("飞石")] = 1.5f,
        [SpellIds.取("坚石")] = 1.5f,
        [SpellIds.取("垒石")] = 1.5f,
        [SpellIds.取("崩石")] = 1.5f,
        [SpellIds.取("闪耀")] = 1.5f,
        [SpellIds.取("闪灼")] = 1.5f,
        [SpellIds.取("神圣")] = 1.5f,
        [SpellIds.取("豪圣")] = 1.5f,
    };

    /// <summary>
    /// 自身 AOE 的半径（**按技能查**，白魔是 8 米）。
    /// </summary>
    public override (bool 是, float 半径) 自身AOE半径(uint 技能Id)
        => (技能Id == SpellIds.取("神圣") || 技能Id == SpellIds.取("豪圣"))
            ? (true, 8f) : (false, 0f);


    // ══════════════════════════════════════════════════════════════════
    //  ★ 治疗候选集 —— 综合判定的输入 ★
    //
    //  ⚠️ 数据全部来自**游戏数据**（不是攻略站）：
    //     恢复力：`tools\HealPotency` 的 ToMacroString 解出的宏
    //     MP/复唱/咏唱/充能：`tools\CastProbe` 读的游戏 Action 表
    //
    //  ⚠️ 为什么要候选集而不是单个槽位：
    //     白魔有两个单体 GCD 治疗（治疗 500/450 MP400、救疗 800/700 MP1000）——
    //     缺口小该用**治疗**（省 600 MP），缺口大该用**救疗**（省一个 GCD）。
    //     槽位只能表达"用哪个"，候选集能让 `治疗决策` 按情况挑。
    // ══════════════════════════════════════════════════════════════════
    private 治疗候选集? _治疗候选;

    public override 治疗候选集 治疗候选
    {
        get
        {
            if (_治疗候选 != null) return _治疗候选;

            var c = new 治疗候选集();

            // ── 单体 GCD ──
            c.加(new 治疗技能 { Id = SpellIds.取("治疗"), 名 = "治疗", 等级 = 2,
                恢复力 = 档位(SpellIds.取("治疗"), 500, 450, 85), MP = 400, 咏唱 = 1.5f, 复唱 = 2.5f });

            c.加(new 治疗技能 { Id = SpellIds.取("救疗"), 名 = "救疗", 等级 = 30,
                恢复力 = 档位(SpellIds.取("救疗"), 800, 700, 85), MP = 1000, 咏唱 = 1.5f, 复唱 = 2.5f });

            c.加(new 治疗技能 { Id = SpellIds.取("安慰之心"), 名 = "安慰之心", 等级 = 52,
                恢复力 = 档位(SpellIds.取("安慰之心"), 800, 700, 85), MP = 0, 咏唱 = 0f, 复唱 = 2.5f });

            // ── 群体 GCD ──
            c.加(new 治疗技能 { Id = SpellIds.取("医治"), 名 = "医治", 等级 = 10,
                恢复力 = 档位(SpellIds.取("医治"), 400, 300, 85), MP = 900, 咏唱 = 2.0f, 复唱 = 2.5f, 群体 = true });

            c.加(new 治疗技能 { Id = SpellIds.取("医济"), 名 = "医济", 等级 = 50,
                恢复力 = 档位(SpellIds.取("医济"), 250, 200, 85), MP = 1000, 咏唱 = 2.0f, 复唱 = 2.5f,
                群体 = true, HoT恢复力 = 档位(SpellIds.取("医济"), 150, 100, 85), HoT持续 = 15f });

            // ══════════════════════════════════════════════════════════════
            //  ★ 医养（96 级）★ —— 原来**不在治疗候选里**
            //
            //  [!] 后果：`群体治疗GCD` 虽然写了 `取已解锁(医养, 医济, 医治)`，
            //      但真正被 `选最优` 用的是**这张治疗候选表** ——
            //      它没有医养 ⇒ 96 级以上永远轮不到最高档的群疗，
            //      只能退到 医济 / 医治。
            //
            //  真值（`dump_healpot.tsv`）：
            //      「恢复自身及周围队员的体力　恢复力：250
            //        追加效果：令目标体力持续恢复　恢复力：175　持续时间：15秒」
            //  费率：MP 1000（和医济同级）
            // ══════════════════════════════════════════════════════════════
            c.加(new 治疗技能 { Id = SpellIds.取("医养"), 名 = "医养", 等级 = 96,
                恢复力 = 250, MP = 1000, 咏唱 = 2.0f, 复唱 = 2.5f,
                群体 = true, HoT恢复力 = 175, HoT持续 = 15f });

            c.加(new 治疗技能 { Id = SpellIds.取("愈疗"), 名 = "愈疗", 等级 = 40,
                恢复力 = 档位(SpellIds.取("愈疗"), 600, 550, 85), MP = 1500, 咏唱 = 2.0f, 复唱 = 2.5f, 群体 = true });

            c.加(new 治疗技能 { Id = SpellIds.取("狂喜之心"), 名 = "狂喜之心", 等级 = 76,
                恢复力 = 档位(SpellIds.取("狂喜之心"), 400, 300, 85), MP = 0, 咏唱 = 0f, 复唱 = 2.5f, 群体 = true });

            // ── 单体 HoT（瞬发，移动中也能放）──
            //    ⚠️ 审计发现：它在 `单体HoT` 槽位，但**不在候选集** ——
            //       治疗决策看不到它，移动中少了一个可选项。
            // [!] **修正：再生是纯 HoT，没有直疗**（数据核对）
            //     原文：「令目标体力**持续**恢复　恢复力：　持续时间：18秒」
            //     ⇒ 那个 250 是**每跳**，不是"直疗 250 + HoT 250"。
            //     原来两边都写 250 ⇒ 多算一口 250 直疗
            //     （`即时恢复力` 会报 250，救急时可能误选它）。
            //     `恢复力 = 0` + `HoT恢复力 = 每跳` 才是正确写法。
            c.加(new 治疗技能 { Id = SpellIds.取("再生"), 名 = "再生", 等级 = 35,
                恢复力 = 0, MP = 400, 咏唱 = 0f, 复唱 = 2.5f,
                HoT恢复力 = 档位(SpellIds.取("再生"), 250, 200, 85), HoT持续 = 18f });

            // ── 单体能力技（不占 GCD）──
            // ── 单体能力技（不占 GCD）──
            c.加(new 治疗技能 { Id = SpellIds.取("神名"), 名 = "神名", 等级 = 60,
                // ⚠️ 98 级起 **2 充能**（说明原文），之前没记。
                恢复力 = 700, MP = 0, 咏唱 = 0f, 冷却 = 60f, 充能 = 2 });


            c.加(new 治疗技能 { Id = SpellIds.取("天赐祝福"), 名 = "天赐祝福", 等级 = 50,
                恢复力 = 9999, MP = 0, 咏唱 = 0f, 冷却 = 180f });

            // ── 群体能力技 ──
            c.加(new 治疗技能 { Id = SpellIds.取("法令"), 名 = "法令", 等级 = 56,
                恢复力 = 400, MP = 0, 咏唱 = 0f, 冷却 = 40f, 群体 = true });

            // [!] **修正：庇护所是纯 HoT**（数据核对）
            //     原文：「以指定地点为中心产生治疗区域　效果时间内，**持续**恢复
            //            进入该区域的自身及队员的体力　恢复力：100　持续时间：24秒」
            //     原来 `恢复力 = 100` + `HoT恢复力 = 100` ⇒ 多算一口 100 直疗。
            c.加(new 治疗技能 { Id = SpellIds.取("庇护所"), 名 = "庇护所", 等级 = 52,
                恢复力 = 0, MP = 0, 咏唱 = 0f, 冷却 = 90f, 群体 = true,
                HoT恢复力 = 100, HoT持续 = 24f });

            c.加(new 治疗技能 { Id = SpellIds.取("全大赦"), 名 = "全大赦", 等级 = 70,
                恢复力 = 200, MP = 0, 咏唱 = 0f, 冷却 = 60f, 群体 = true });

            // ── 86/90/100 级的补充（原候选集漏了）──

            c.加(new 治疗技能 { Id = SpellIds.取("礼仪之铃"), 名 = "礼仪之铃", 等级 = 90,
                恢复力 = 400, MP = 0, 咏唱 = 0f, 冷却 = 180f, 群体 = true,
                HoT恢复力 = 200, HoT持续 = 15f });

            // ══════════════════════════════════════════════════════════════
            //  [!] **修正：神爱抚是「盾 + HoT」，不是直疗**（数据核对）
            //
            //     原文：「为自身及周围队员附加防护罩，**抵消相当于恢复力 400 的伤害量**，
            //            持续 10 秒　追加效果：防护罩消失后附加**神爱环**状态，
            //            令目标体力持续恢复　恢复力：200　持续 15 秒」
            //
            //     ⇒ 结构是：**盾 400 + 之后 HoT 200×15s**，**没有直疗**。
            //     原来写成 `恢复力 = 200`（直疗 200）—— 把神爱环的每跳当成了直疗，
            //     而且**完全丢掉了 400 的盾**。
            //     ⇒ 现在：`恢复力 = 0`（无直疗）+ 盾交给 `是盾` + HoT 200×15。
            // ══════════════════════════════════════════════════════════════
            c.加(new 治疗技能 { Id = SpellIds.取("神爱抚"), 名 = "神爱抚", 等级 = 100,
                恢复力 = 0, MP = 0, 咏唱 = 0f, 冷却 = 1f, 群体 = true,
                是盾 = true, 盾百分比 = 40000 / 200,   // 盾 = 400 恢复力，直疗 = 200 ⇒ 200%
                HoT恢复力 = 200, HoT持续 = 15f });

            _治疗候选 = c;
            return c;
        }
    }

    /// <summary>
    /// **按等级查两档恢复力**（高等级档 / 低等级档）。
    ///
    /// ⚠️ 游戏数据宏的形式是 `if(等级>=N, 高, 低)` —— 有些技能不止两档，
    ///    那种用 <see cref="治疗技能"/> 直接写死当前等级的值。
    /// </summary>
    private static int 档位(uint 技能Id, int 高, int 低, int 提升等级)
    {
        try
        {
            return Core.Me.Level >= 提升等级 ? 高 : 低;
        }
        catch
        {
            return 低;
        }
    }

    public override uint Dot技能 => SpellUtil.取已解锁(
        SpellIds.取("天辉"), SpellIds.取("烈风"), SpellIds.取("疾风"));
    public override uint DotBuff => AuraIds.白魔Dot;
    // 把所有档位都列上：天辉有两个 id（1871 / 2035），漏一个就可能无限补 DoT
    public override uint[] 所有DotBuff => new[]
    {
        AuraIds.白魔Dot, AuraIds.白魔DotAlt, AuraIds.白魔Dot2, AuraIds.白魔Dot1
    };

    // ⚠️ **「愈疗」不能当单体治疗用** —— 已核实（dump_actions.tsv）：
    //      131 愈疗 = Lv40 / **CastType=2 / EffectRange=10** → 它是**群疗**（以目标为中心 10 米）
    //      135 救疗 = Lv30 / CastType=1 / EffectRange=0   → 这才是真·单体（Cure II）
    //
    //    原来写成 `取已解锁(救疗, 愈疗, 治疗)` 并注释"40 级后救疗的同级替代"——
    //    两处都错：① 愈疗不是单体；② 救疗 Lv30 比愈疗 Lv40 **先解锁**，
    //    所以 `取已解锁` 永远返回救疗，**愈疗那一项是死代码**，从来没生效过。
    //    删掉它是**行为不变**的清理，但避免后人照着错注释去"修一个不存在的缺口"。
    public override uint 单体治疗GCD => SpellUtil.取已解锁(
        SpellIds.取("救疗"),
        SpellIds.取("治疗"));
    public override uint 群体治疗GCD => SpellUtil.取已解锁(SpellIds.取("医养"), SpellIds.取("医济"), SpellIds.取("医治"));

    // ⚠️ 顺序**不能反**（这里踩过，审计发现）：
    //
    //    `取已解锁` 是"从前往后挑第一个**已解锁**的"。
    //    天赐祝福 = Lv50 / CD 180s / 无充能
    //    神名     = Lv60 / CD 60s / **1 充能**
    //
    //    原来写成 `取已解锁(天赐祝福, 神名)` → 60 级以后两个都解锁
    //    → **永远返回天赐祝福** → 神名一次都放不出来（死代码）。
    //    而且常规 <30% 急救会把 180 秒 CD 的保命大直接交掉。
    //
    //    ⇒ 常规急救先用**能攒充能的神名**；天赐祝福留给真正要命的时候
    //      （「必须奶满」机制走 `必须奶满.最该用的能力技`，那里直接取天赐，不受这里影响）。
    /// <summary>
    /// 常规急救 —— **优先神名**（60 秒 CD / 有充能，能攒两层）。
    ///
    /// 天赐祝福是"一次到满"的保命大（180 秒 CD），**不该当常规急救交**，
    /// 它由 `预铺单奶能力技`/「必须奶满」那条路专门使用。
    /// （历史上这里写反过：`取已解锁(天赐, 神名)` → 60 级后永远返回天赐，
    ///   神名成死代码，且 180 秒 CD 被随手花掉。见 交接文档。）
    /// </summary>
    public override uint 紧急单奶 => SpellIds.取("天赐祝福");

    /// <summary>水流幕：单体减伤/护盾，86 级 —— 预铺类（伤害来之前给）。</summary>
    /// <summary>单体 HoT：再生（137，瞬发）—— 移动中唯一能给出的治疗。</summary>
    public override uint 单体HoT => SpellIds.取("再生");

    public override uint 预铺单奶能力技 => SpellUtil.取已解锁(SpellIds.取("水流幕"));

    /// <summary>
    /// 瞬发槽：**神名**（60 秒 CD、1 充能）—— 常规补血用。
    ///
    /// ⚠️ 这里**不能再写 `取已解锁(神名, 天赐祝福)`** ——
    ///    那是同一个坑的**第三次**：`取已解锁` 取第一个**已解锁**的，
    ///    60 级后两个都解锁 → 永远返回神名 → 天赐永远拿不到。
    ///
    ///  参考实现的分工（IL 直读默认值）：
    ///      天赐阈值 = **20%**   ← 救命（180 秒 CD）
    ///      神名阈值 = **75%**   ← 常规补血（60 秒 CD、有充能）
    ///    两者是**两个独立 resolver、两条血线**，不是"二选一"，
    ///    所以不存在"谁等级高就永远是它"。
    ///    （前两次同类坑：`紧急单奶` 顺序写反、`单体治疗GCD` 把群疗「愈疗」当单体。）
    /// </summary>
    public override uint 瞬发单奶能力技 => SpellUtil.取已解锁(SpellIds.取("神名"));

    /// <summary>神名阈值 75%（参考实现 IL 直读）—— 比 GCD 单奶高得多，这就是"优先不读条的"。</summary>
    public override float 瞬发单奶血线 => 0.75f;
    public override uint 群体治疗能力技 => SpellIds.取("法令");
    public override bool 群体治疗能力技是输出型 => true;   // 法令要卡 CD
    public override uint 团队减伤 => SpellIds.取("节制");

    /// <summary>神圣/豪圣是 8 米范围（比其他奶妈的 AOE 大）</summary>
    public override int AOE伤害范围 => 8;
    public override uint 个人减伤 => SpellIds.取("神祝祷");

    /// <summary>
    /// **单体盾：神祝祷**（审查发现原来 `单体盾` 空着）。
    ///
    /// [!] 后果：`Res_HealShield`（预铺单体盾）读的是 `单体盾` ——
    ///     为 0 时直接 `return -102` => **白魔的单体盾预铺完全不工作**。
    ///
    /// [!] 官表说明核实：神祝祷 = "为自身或一名队员附加能够抵御一定伤害的
    ///     **防护罩**，抵消相当于 500 恢复力的伤害" —— 是真正的盾。
    ///
    /// [!] 它**同时**留在 `个人减伤`：那是两个不同的决策
    ///     （预铺给坦克 vs 给自己），不冲突。
    /// </summary>
    public override uint 单体盾 => SpellIds.取("神祝祷");

    /// <summary>神速咏唱期间基础输出会被替换成闪飒，这时候不能再硬放闪灼</summary>
    public override bool 有特殊输出形态 =>
        (AuraIds.闪飒预备 != 0 && Core.Me.HasAura(AuraIds.闪飒预备))
        || (AuraIds.闪飒预备2 != 0 && Core.Me.HasAura(AuraIds.闪飒预备2));

    public override uint 复活 => SpellIds.取("复活");
    public override uint 驱散 => SpellsDefine.Esuna;
    public override uint 醒梦 => SpellsDefine.LucidDreaming;

    // 白魔的百合是被动攒的，脱战不用主动做什么
}

public class WHMRotationEntry : HealerEntryBase
{
    private readonly WHMSpellTable _spells = new();

    public override Jobs TargetJob => Jobs.WhiteMage;
    public override string OverlayTitle => "小鲸鱼 · 白魔（日随）";
    public override string Description =>
        "日随用白魔 ACR。治疗优先，输出顺手；1-100 级通用。\n" +
        "百合技 / 节制减伤 / cactbot 时间轴 / 木桩最优输出 都接好了。";
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
            new SlotResolverData(new Res_SingleHoT(_spells), SlotMode.Gcd),   // 再生
            new SlotResolverData(new WHM_AfflatusRapture(_spells), SlotMode.Gcd),
            new SlotResolverData(new WHM_AfflatusSolace(_spells), SlotMode.Gcd),
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
new SlotResolverData(new Res_HealAoEGcd(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_HealSingleGcd(_spells), SlotMode.Gcd),
            // 输出顺序：AOE（敌人>=3 才触发）→ DoT → 职业输出 → 兜底
            // DoT 必须排在职业输出前面：起手先让它开始跳伤害，
            // 已上之后它自己的 Check 会跳过，不会重复占 GCD
            new SlotResolverData(new Res_AoEDamage(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_Dot(_spells), SlotMode.Gcd),
            new SlotResolverData(new WHM_AfflatusMisery(_spells), SlotMode.Gcd),
            new SlotResolverData(new WHM_GlareIV(), SlotMode.Gcd),   // 神速期间打闪飒
            // ⚠️ 位置很重要：必须在 `Res_MoveGcd`（移动填充）**之前** ——
            //   药尾声补刀最该生效的场景就是**移动中**（读条放不出来），
            //   排在移动填充后面等于**永远选不到**。
            new SlotResolverData(new Res_PotionTailDamage(_spells), SlotMode.Gcd),
            // ★ 多目标 DoT：主目标 DoT 还在时，把 DoT 扩散到**其他被拉到的怪** ★
            //   ⚠️ 必须在 Res_MoveGcd 之前（否则永远抢不到这个 GCD）
            //   ⚠️ 必须在 Res_Dot 之后（主目标的 DoT 优先级更高）
            new SlotResolverData(new Res_MultiDot(_spells), SlotMode.Gcd),
            // 基础输出：**必须排在 Res_MoveGcd 之前**。
            // 破阵法（近战填充技）也是瞬发，该由它优先选；
            // 排在移动填充之后会把 GCD 抢成毁坏（实测过的问题）。
            new SlotResolverData(new Res_BaseDamage(_spells), SlotMode.Gcd),
            // 移动填充兜底：当 Res_BaseDamage 因为基础输出是读条技而让位时顶上。
            new SlotResolverData(new Res_MoveGcd(_spells), SlotMode.Gcd),

            new SlotResolverData(new Res_HealAoEAbility(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_SelfMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_TeamMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_LucidDreaming(_spells), SlotMode.OffGcd),
            new SlotResolverData(new WHM_PresenceOfMind(), SlotMode.OffGcd),
            new SlotResolverData(new Res_FreeCast(_spells), SlotMode.OffGcd),        // 无中生有
            new SlotResolverData(new Res_HealBooster(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_GroupHoT(_spells), SlotMode.OffGcd),        // 庇护所
            new SlotResolverData(new Res_PlacedHeal(_spells), SlotMode.OffGcd),      // 礼仪之铃
            new SlotResolverData(new Res_BigAoEHeal(_spells), SlotMode.OffGcd),      // 全大赦
            new SlotResolverData(new Res_SingleMitigation(_spells), SlotMode.OffGcd), // 水流幕
            new SlotResolverData(new Res_LimitBreak(), SlotMode.OffGcd),                  // 极限技
        };
    }

    protected override void 构建QT()
    {
        base.构建QT();
        加职业开关("神速魔", true);
    }

    /// <summary>爆发轴（勾上「一键爆发」才生效）</summary>
    protected override AEAssist.CombatRoutine.Module.ISlotSequence[] 构建爆发轴()
        => new AEAssist.CombatRoutine.Module.ISlotSequence[] { new 白魔爆发轴(Spells) };
}

// ============================================================================
//  白魔专属
// ============================================================================

/// <summary>安慰之心：百合单体治疗，免蓝。</summary>
public class WHM_AfflatusSolace : ISlotResolver
{
    private readonly JobSpellTable _t;

    public WHM_AfflatusSolace(JobSpellTable table) => _t = table;

    private static uint 技能 => SpellIds.取("安慰之心");

    /// <summary>
    /// 选目标。两种场景：
    ///   1. 百合满 3 颗（溢出）→ 花掉一颗，哪怕全队满血 —— 因为不花就没有血百合，
    ///      血百合满 3 才能打苦难之心，攒着纯亏
    ///   2. 正常情况 → 只在血线更低时才动（百合要攒着用在刀刃上）
    /// </summary>
    private static IBattleChara? 选目标()
    {
        if (JobApiHelper.百合 >= 3)
        {
            // 兜底到自己：打木桩是单人环境，可能一个"队友"都没有（包括自己没被算进去），
            // 那样就永远找不到目标，百合也就永远卸不掉。
            //
            // ⚠️ 这个兜底**只在溢出分支**才允许 —— 见下面非溢出分支的说明。
            return HealTargetHelper.最危险队友()
                   ?? HealTargetHelper.血量最低的坦克()
                   ?? AEAssist.Core.Me;
        }

        var 血线 = Math.Min(HealSettings.Instance.单体治疗阈值, HealSettings.Instance.百合使用血线);

        // ⚠️ **非溢出分支不能写 `?? Core.Me`** —— 审计发现的真 bug。
        //
        //    原来这里是 `return 最低血量队友(血线) ?? AEAssist.Core.Me;`，
        //    于是**全员都在血线以上时也会返回自己** →
        //    选目标**永不返回 null** → Check 只要"百合>=1 且技能可用"就成立 →
        //    白魔会在**没人掉血的时候对满血的自己交百合**
        //    （它的位置在 HoT 之后、所有输出 resolver 之前）。
        //
        //    结果：① 百合被提前花掉（本该攒着）；② 白拿一个 GCD 不打输出。
        //    而上面注释写的"只在血线更低时才动"被这一行彻底抵消了。
        //
        //    ⇒ 非溢出时就**老老实实返回 null**，让别的 resolver 去干活。
        return HealTargetHelper.最低血量队友(血线);
    }

    public int Check()
    {
        if (!SpellUtil.已解锁(技能)) return -2;
        if (JobApiHelper.百合 < 1) return -3;

        // 百合满 3 颗 = 资源快溢出了。这时候**无视一切治疗开关**（木桩模式、奶人、单奶），
        // 因为花百合是为了攒血百合打苦难之心，属于资源循环，不是纯治疗。
        var 溢出 = JobApiHelper.百合 >= 3;
        if (!溢出)
        {
            if (HealTargetHelper.木桩模式) return -300;
            if (!HealQt.GetQt("奶人")) return -100;
            if (!HealQt.GetQt("单奶")) return -101;
        }

        var target = 选目标();
        if (target == null) return -1;

        // 溢出时优先级更高一点，尽早把百合花出去
        var 优先级 = JobApiHelper.百合 >= 3 ? 15 : 14;
        return SpellUtil.可用(技能) ? 优先级 : -1;
    }

    public void Build(Slot slot)
    {
        var target = 选目标();
        if (target == null) return;
        slot.Add(new Spell(技能, target));
    }
}

/// <summary>狂喜之心：百合群体治疗，免蓝。</summary>
public class WHM_AfflatusRapture : ISlotResolver
{
    private readonly JobSpellTable _t;

    public WHM_AfflatusRapture(JobSpellTable table) => _t = table;

    private static uint 技能 => SpellIds.取("狂喜之心");

    public int Check()
    {
        if (!SpellUtil.已解锁(技能)) return -2;
        if (JobApiHelper.百合 < 1) return -3;

        // 同安慰之心：溢出时无视治疗开关
        var 溢出 = JobApiHelper.百合 >= 3;
        if (!溢出)
        {
            if (HealTargetHelper.木桩模式) return -300;
            if (!HealQt.GetQt("奶人")) return -100;
            if (!HealQt.GetQt("群奶")) return -101;
        }

        var s = HealSettings.Instance;
        var 需要群奶 = HealTargetHelper.低于阈值人数(s.群体治疗阈值, 20f) >= s.群奶最少人数;

        // 百合满 3 颗 + 有两个以上人不在满血 → 顺手用群奶花掉（覆盖更划算）
        var 溢出可用 = JobApiHelper.百合 >= 3
                       && HealTargetHelper.低于阈值人数(0.95f) >= 2;

        if (!需要群奶 && !溢出可用) return -1;

        return SpellUtil.可用(技能) ? 16 : -1;
    }

    public void Build(Slot slot)
    {
        slot.Add(new Spell(技能, SpellTargetType.Self));
    }
}

/// <summary>苦难之心：血百合满 3 颗的免费大伤害。木桩模式不省。</summary>
public class WHM_AfflatusMisery : ISlotResolver
{
    private readonly JobSpellTable _t;

    public WHM_AfflatusMisery(JobSpellTable table) => _t = table;

    private static uint 技能 => SpellIds.取("苦难之心");

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;
        if (!SpellUtil.已解锁(技能)) return -2;
        if (!HealSettings.Instance.用苦难之心) return -6;
        if (JobApiHelper.血百合 < 3) return -3;
        if (HealTargetHelper.当前目标() == null) return -1;

        // 视线/射程：和别的输出技同一套判断（对着柱子放等于白按）
        if (!技能数据.打得到(HealTargetHelper.当前目标(), 技能数据.取有效射程(技能))) return -6;

        // 需求 2 + 5：残血小怪不交，但木桩模式不省
        if (!HealTargetHelper.木桩模式)
        {
            if (HealTargetHelper.目标快死了()) return -4;
            if (HealSettings.Instance.时间轴攒资源 && TimelineManager.未来有减伤(8.0)) return -5;
        }

        // ⚠️ 苦难之心 = 血百合满 3 的**免费大伤害**，不该被任何限时窗口挤掉。
        //    优先级给 9，**高于闪飒(8)** —— 闪飒是神速的 proc、过了会浪费，
        //    但苦难之心是"迟早要打、打了不花蓝"的资源，堵在窗口后面纯亏。
        //    （两者都可用时会先打这个，然后闪飒接着打，GCD 排得下。）
        return SpellUtil.可用(技能) ? 9 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}

/// <summary>神速咏唱：输出窗口。木桩模式卡 CD 开。</summary>
public class WHM_PresenceOfMind : ISlotResolver
{
    private static uint 技能 => SpellIds.取("神速咏唱");

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;
        if (!HealQt.GetQt("神速魔", true)) return -101;
        if (!SpellUtil.已解锁(技能)) return -2;
        // 神速咏唱是**自身 buff**，不需要选中目标（跟法令/王冠领主同一个教训）
        if (!CharacterExt.可以插能力技()) return -6;
        if (AuraIds.神速魔 != 0 && Core.Me.HasAura(AuraIds.神速魔)) return -7;

        // 需求 2：残血小怪不给爆发（木桩模式不判断）
        if (!HealTargetHelper.木桩模式 && HealTargetHelper.目标快死了()) return -4;

        return SpellUtil.可用(技能) ? 2 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.Get(技能);
        if (spell != null) slot.Add(spell);
    }
}


/// <summary>
/// 闪飒（Glare IV）。
///
/// 这是"神速咏唱期间"的强化闪耀 —— 游戏里只有在神速 buff 下才可用。
/// 优先级放在普通输出之前，神速一转好就能打上。
///
/// ══════════════════════════════════════════════════════════════════
///  ⚠️ 现象："白魔起手神速后不打闪飒"。这里做了两处针对性加固。
///
///  ① **判定顺序反过来**：先看 buff（「闪飒预备」= 神速给的 proc），
///     再看 IsReadyWithCanCast。
///
///     原来的写法是「buff 有 && 可用(闪飒)」两个都必须成立 ——
///     如果 `IsReadyWithCanCast()` 对闪飒返回了 false（语义没吃透的 API，
///     开发约定 E 节反复警告的那类），整个技能就**静默不放**，
///     而且表现正好是"神速开了、闪飒就是不出去"。
///
///     现在：**buff 在 = 该打**（proc 的存在本身就是"这个技能现在能放"的最强证据），
///     `可用()` 只作为二次确认，不通过时打一条节流日志，方便事后定位。
///
///  ② **闪飒需要目标**（数据：CastType=2 / Range=25 / EffectRange=5）——
///     没目标时明确不给（不能对着空气放）。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class WHM_GlareIV : ISlotResolver
{
    private static uint 技能 => SpellIds.取("闪飒");

    /// <summary>诊断节流：5 秒最多打一条，避免刷爆日志</summary>
    private static long 上次诊断;

    /// <summary>神速给的「闪飒预备」proc 在不在身上</summary>
    private static bool 有闪飒预备()
    {
        return (AuraIds.闪飒预备 != 0 && Core.Me.HasAura(AuraIds.闪飒预备))
               || (AuraIds.闪飒预备2 != 0 && Core.Me.HasAura(AuraIds.闪飒预备2));
    }

    public int Check()
    {
        if (!HealQt.GetQt("输出")) return -100;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 没 proc 就完全不是这个技能的场合
        if (!有闪飒预备()) return -1;

        // 闪飒要目标（Range=25）
        if (HealTargetHelper.当前目标() == null) return -1;

        // 视线/射程（和别的输出技同一套判断）
        if (!技能数据.打得到(HealTargetHelper.当前目标(), 技能数据.取有效射程(技能))) return -6;

        // 二次确认：按 开发约定 E 节，不拿没吃透的 API 当**唯一**依据，
        // 但也不让它把已经确定该放的技能挡掉 —— 挡掉时留下证据。
        if (!SpellUtil.可用(技能))
        {
            if (TimeHelper.Now() - 上次诊断 > 5000)
            {
                上次诊断 = TimeHelper.Now();
                LogHelper.Info("[HealerACR] 闪飒预备在身，但 可用(闪飒) = false -> 仍然放");
                // 追一层：直接问游戏自己"到底缺什么"（内部有 10 秒节流）
                技能诊断.报告一次(技能, HealTargetHelper.当前目标());
            }
        }

        return 8;   // 高于普通输出（普通输出是 1）
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(spell);
    }
}
