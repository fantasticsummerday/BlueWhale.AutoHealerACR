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
            //  ★★ 医养（96 级 / 37010）—— **最后决定：不放** ★★
            //
            //  [!] 这里先后有过两次相反的结论，把过程写下来免得再翻烧饼：
            //      ① 最初：`群体治疗GCD` 写了 `取已解锁(医养, 医济, 医治)`，
            //         但**治疗候选表里没有它** ⇒ `选最优` 看不到它，
            //         96 级以上永远只用 医济 / 医治 ⇒ 判定为"缺口"。
            //      ② 于是把医养加进候选（数据也核过，见下）。
            //      ③ **用户 2026-10-04 拍板：按参考 —— 不放。**
            //         理由：参考实现（youshu）的 `GCD.医养` 实际 Build 的是
            //         **133（医济）**，**从不放 37010**。
            //         "最高档群疗"那一格在参考里本来就是空着的 ——
            //         我们补上它反而会和参考分叉（多一层 `选最优` 比较，
            //         而且它带 HoT，会改变"该交群疗还是该交 HoT"的判断）。
            //
            //  [!] 所以**两处都去掉了**：
            //       · 候选表这里 → 注释掉（见下，数据留着备用）
            //       · `群体治疗GCD` → 改成 `取已解锁(医济, 医治)`
            //      96 级以上的群疗走 **医济**，与参考一致。
            //
            //  真值（`dump_healpot.tsv`，留着将来要改回来时用）：
            //      「恢复自身及周围队员的体力　恢复力：250
            //        追加效果：令目标体力持续恢复　恢复力：175　持续时间：15秒」
            //  费率：MP 1000（和医济同级）
            // ══════════════════════════════════════════════════════════════
            // ══════════════════════════════════════════════════════════════
            //  ★★ 医养（37010）**不放进候选** —— 用户 2026-10-04 拍板 ★★
            //
            //  [!] 参考实现（youshu）的 `GCD.医养` 实际 Build 的是 **133（医济）**，
            //      **从不放 37010** —— 也就是说"最高档群疗"那一格参考是空着的。
            //
            //  [!] 原来这里把医养加进了候选（理由：它是 96 级最高档群疗，
            //      恢复力 250 + HoT 175/15s，MP 1000 和医济同级）。
            //      但既然口径是"按参考"，那就**不放**：
            //        · 多一个候选 = `选最优` 多一层比较，行为会和参考分叉；
            //        · 而且它带 HoT，会改变"该交群疗还是该交 HoT"的判断。
            //
            //  [!] 保留的后果：96 级以上群疗走 **医济(133)** —— 和参考一致。
            //      恢复力那一栏的注释留着，将来要改回来时数据是现成的。
            //
            //  真值（`dump_healpot.tsv`）：
            //      「恢复自身及周围队员的体力　恢复力：250
            //        追加效果：令目标体力持续恢复　恢复力：175　持续时间：15秒」
            //  费率：MP 1000（和医济同级）
            // ══════════════════════════════════════════════════════════════
            // c.加(new 治疗技能 { Id = SpellIds.取("医养"), 名 = "医养", 等级 = 96,
            //     恢复力 = 250, MP = 1000, 咏唱 = 2.0f, 复唱 = 2.5f,
            //     群体 = true, HoT恢复力 = 175, HoT持续 = 15f });

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
            return CharacterExt.我的等级() >= 提升等级 ? 高 : 低;
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
    /// <summary>
    /// 群体治疗 GCD 的"槽位技能"。
    ///
    /// [!] **不含医养（37010）** —— 用户 2026-10-04 拍板按参考：
    ///     参考的 `GCD.医养` 实际 Build 的是 133（医济），从不放 37010。
    ///     ⇒ 96 级以上的群疗走 **医济**，与参考一致。
    /// </summary>
    public override uint 群体治疗GCD => SpellUtil.取已解锁(SpellIds.取("医济"), SpellIds.取("医治"));

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

    /// <summary>
    /// 预铺单奶能力技：水流幕（25861，30 秒 CD）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  [!] **它有两个角色，这是有意的 —— 但它【不在治疗候选池里】，也不是漏了** ★
    ///
    ///  [!] 两个角色：
    ///        ① **盾** —— 由 `Res_HealShield` 按 `治疗阈值表.取(_t.单体盾)` 用它
    ///           （表里登记 0.70，来源 `// youshu 水流幕阈值 70`）
    ///        ② **预铺单奶** —— 由 `Res_InstantHealAbility` 的"预铺"分支用它，
    ///           **但那个分支只在 `低于阈值人数(0.30f) == 0` 时**才走
    ///           （即"没人濒危"时顺手铺一个）
    ///
    ///  [!] 为什么**不**把它加进治疗候选池（横向检查问过这个）：
    ///        候选集里**已经有它的盾候选**（`加功能候选` 会遍历 `单体盾` 槽位），
    ///        ==> AI **看得到它**。再往治疗池里加一条会**重复计一个动作**，
    ///            而且两条的语义不同（盾 vs 预铺单奶），会让 AI 分不清在选哪个。
    ///
    ///  [!] 所以：**横向一致性检查报"水流幕不在治疗池"时，这是预期的，不是缺口。**
    ///      （同类：`神祝祷` 同时是 `个人减伤` 和 `单体盾`，走的是减伤/盾那条路。）
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
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
        (AuraIds.闪飒预备 != 0 && CharacterExt.我有光环(AuraIds.闪飒预备))
        || (AuraIds.闪飒预备2 != 0 && CharacterExt.我有光环(AuraIds.闪飒预备2));

    public override uint 复活 => SpellIds.取("复活");
    public override uint 驱散 => SpellsDefine.Esuna;
    public override uint 醒梦 => SpellsDefine.LucidDreaming;

    // 白魔的百合是被动攒的，脱战不用主动做什么
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
    public class WHMRotationEntry : HealerEntryBase
#else
    // 在 BlueWhale 里声明为 abstract —— 让 AEAssist 的扫描器跳过它
    // （`BlueWhaleEntry` 才是具体实现）。详见上面的条件编译说明。
    public abstract class WHMRotationEntry : HealerEntryBase
#endif
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
            //  ★★ **白魔的「预铺单体盾」通路**（表外审计 P1-1 —— 整条缺失）★★
            //
            //  [!] 现象：`WhiteMageACR.单体盾 => 神祝祷` 这个登记
            //      **全项目零消费者** ✗ —— 四奶里只有白魔的队列没有 `Res_HealShield`
            //      （贤者 `SageACR`、学者 `ScholarACR`、幻术师 `ConjurerACR` 都有）。
            //      后果：
            //        · 神祝祷**永远不会被预铺**（它只在 `个人减伤` 那条路上出现，
            //          而那条是"给自己、血少/时间轴"的语义，不是"给坦克预铺"）
            //        · `治疗阈值表` 登记的「神祝祷 0.70」成了死登记
            //        · 表 #65 修的「水流幕坦克/非坦双档」是另一条路（`Res_SingleMitigation`），
            //          和这一条不是一回事
            //
            //  [!] 参考位置：`白魔技能策略` **slot 21**（神祝祷），
            //      在 天赐/神名（slot 19/20 一类的能力技）之后、醒梦之前。
            //      我们这里对应"治疗能力技之后"（`Res_InstantHealAbility` 是 9 位），
            //      所以放在这条治疗 GCD 段末尾、减伤整组之前。
            //
            //  [!] `Res_HealShield` 是**通用的**（读 `_t.单体盾` + `_t.护盾前置`）——
            //      贤者"均衡 → 均衡诊断"和学者"鼓舞激励之策"走的就是这条路，
            //      白魔没有护盾前置（`_t.护盾前置 == 0`）时那一段自动跳过 ✓
            // ══════════════════════════════════════════════════════════════
            new SlotResolverData(new Res_HealShield(_spells), SlotMode.Gcd),

            // ══════════════════════════════════════════════════════════════
            //  ★★ **减伤整组提前**（表 #94）★★
            //
            //  [!] 参考实现的槽序（`占星技能策略` IL 直读）：**治疗 1~4 位铺完，
            //      紧接着就是减伤整组 5~8 位，然后才是所有输出（20 位以后）**。
            //      而我们把减伤放在**输出之后**（原 26/27/36 位）——
            //      ⇒ 输出 GCD 长期抢占减伤窗口：等轮到减伤时，
            //        boss 那一发**已经落地了** ✗
            //
            //  [!] 位置：**所有治疗之后、所有输出之前**。
            //      · 放治疗之后 —— 减伤再急也不该抢 `必须奶满` / 急救 / 复活
            //      · 放输出之前 —— 这才是这一条的目的
            //
            //  [!] 为什么搬位置是安全的：每条 resolver 的 `Check()` 自带完整守卫
            //      （移动中 / 资源 / 血线 / 时间轴 / 敌人数量 / `团减快照`）；
            //      返回值不参与仲裁，**只有行号算优先级**（反汇编已证）。
            //      贤者那一路已由 #110 按同一口径搬过，行为符合预期。
            //
            //  [!] 一并搬过来的还有 `Res_SingleMitigation`（单盾类）——
            //      它是"给某个人交减伤"，和团减同族，分开摆会互相抢窗口。
            // ══════════════════════════════════════════════════════════════
            new SlotResolverData(new Res_SelfMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_TeamMitigation(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_SingleMitigation(_spells), SlotMode.OffGcd),
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
            // 输出顺序：AOE（敌人>=3 才触发）→ DoT → 职业输出 → 兜底
            // DoT 必须排在职业输出前面：起手先让它开始跳伤害，
            // 已上之后它自己的 Check 会跳过，不会重复占 GCD
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
            // ★ 2026-10-04：苦难之心提到 DoT 之前（对照实现槽 25 < 双DOT 26 < DOT 27）
            new SlotResolverData(new WHM_AfflatusMisery(_spells), SlotMode.Gcd),
            // ★ 多目标 DoT **排在单体 DoT 之前**（对照实现槽序 `双DOT 26` → `DOT 27`）★
            //   [!] 两根 resolver 共用 `Dot补判` 那根保险丝，谁先跑谁补；
            //       放前面才能保证多目标时先铺"还没毒的那只"。
            //   [!] 它自带"候选够 2 个"的判据，单体场景直接跳过 ⇒ 不抢单体毒。
            new SlotResolverData(new Res_MultiDot(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_Dot(_spells), SlotMode.Gcd),
            new SlotResolverData(new Res_AoEDamage(_spells), SlotMode.Gcd),
            new SlotResolverData(new WHM_GlareIV(), SlotMode.Gcd),   // 神速期间打闪飒
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

            new SlotResolverData(new Res_HealAoEAbility(_spells), SlotMode.OffGcd),
            new SlotResolverData(new Res_LucidDreaming(_spells), SlotMode.OffGcd),
            new SlotResolverData(new WHM_PresenceOfMind(), SlotMode.OffGcd),
            new SlotResolverData(new Res_FreeCast(_spells), SlotMode.OffGcd),        // 无中生有
            new SlotResolverData(new Res_HealBooster(_spells), SlotMode.OffGcd),
            // ★ 2026-10-04：神爱抚(37011) 原来**只是候选集里的一条、没有任何 resolver** ✗
            //    ⇒ 满级那层「群盾 400 + HoT 200×15s」永远交不出去 ✗（对照实现里它是独立解析器，紧邻节制）
            new SlotResolverData(new WHM_Serenity(), SlotMode.OffGcd),
            new SlotResolverData(new Res_GroupHoT(_spells), SlotMode.OffGcd),        // 庇护所
            new SlotResolverData(new Res_PlacedHeal(_spells), SlotMode.OffGcd),      // 礼仪之铃
            new SlotResolverData(new Res_BigAoEHeal(_spells), SlotMode.OffGcd),      // 全大赦
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

        // ★ 2026-10-04：**先读本技能登记值**（表里是 0.40），读不到才用 min 兜底 ✓
        //   原来直接用 min(单体 0.52, 百合 0.50) = 0.50 ⇒ 40%~50% 区间就提前把百合花掉了 ✗
        var 血线 = 治疗阈值表.取(SpellIds.取("安慰之心"),
                     Math.Min(HealSettings.Instance.单体治疗阈值, HealSettings.Instance.百合使用血线));

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

        // ══════════════════════════════════════════════════════════════════
        //  ★ **血百合满 3 时不要再花百合**（表外审计 P1-3）★
        //
        //  [!] 参考 `安慰之心.txt` 的 IL：
        //        `IL_0095 call get_BloodLilyStacks` / `IL_009a ldc.i4.3` / `IL_009d ldc.i4.s -4`
        //      `狂喜之心.txt`：`IL_0047` 同构 ⇒ **血百合 == 3 ⇒ -4**。
        //
        //  [!] 为什么：花一朵蓝花会**攒一颗血百合**，而血百合上限就是 3 ——
        //      已经满 3 时再花，那一颗**直接溢出白扔** ✗
        //      而且 `苦难之心` 还压着没打（它的门是 `血百合 >= 3`），
        //      正确顺序是"**先打苦难之心把血百合花掉，再继续攒**"。
        //
        //  [!] 放在最前面（连 `百合 < 1` 之前）—— 这是资源规则，不是治疗规则。
        // ══════════════════════════════════════════════════════════════════
        if (JobApiHelper.血百合 >= 3) return -4;

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

        // ★ **血百合满 3 时不要再花百合**（表外审计 P1-3）——
        //   和 `WHM_AfflatusSolace` 同一条判据（参考 `狂喜之心.txt:47-51` 也是 `== 3 ⇒ -4`）。
        //   血百合已满时花百合 = 那一颗血百合**直接溢出白扔** ✗
        //   正确顺序是先打苦难之心把血百合花掉。
        if (JobApiHelper.血百合 >= 3) return -4;

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
        // ★ 2026-10-04：读本技能登记值（表里 0.75，原来吃大类 0.62 ⇒ 交得太早）✗
        //   半径也从 20 改 30 —— 对照实现按 30 米数人，20 米会漏算 20~30 米的人 ✗
        var 需要群奶 = HealTargetHelper.低于阈值人数(
                           治疗阈值表.取(SpellIds.取("狂喜之心"), s.群体治疗阈值), 30f)
                       >= s.群奶最少人数;

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
        if (AuraIds.神速魔 != 0 && CharacterExt.我有光环(AuraIds.神速魔)) return -7;

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

    /// <summary>Check 里选好的落点目标，给 Build 用（避免判 A 放 B）</summary>
    private static IBattleChara? 本帧目标;

    /// <summary>神速给的「闪飒预备」proc 在不在身上</summary>
    private static bool 有闪飒预备()
    {
        return (AuraIds.闪飒预备 != 0 && CharacterExt.我有光环(AuraIds.闪飒预备))
               || (AuraIds.闪飒预备2 != 0 && CharacterExt.我有光环(AuraIds.闪飒预备2));
    }

    public int Check()
    {
        本帧目标 = null;

        if (!HealQt.GetQt("输出")) return -100;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        // 没 proc 就完全不是这个技能的场合
        if (!有闪飒预备()) return -1;

        // ══════════════════════════════════════════════════════════════════
        //  ★ **先定目标，再算「proc 快过期」的优先级**（表外审计 P1-2 修复）★
        //
        //  [!] 原来这里是一个**提前 return 12**：
        //        if (剩余 < 读条毫秒 * 2 + 1500) return 12;
        //      而 `本帧目标` 是**后面**才赋值的 ⇒ `Build` 里
        //        `if (目标 == null) return;` 直接返回空槽 ✗
        //      ⇒ 结果不是"优先打闪飒"，而是**把这一发闪飒整个吞掉**
        //        （Check 返回正数 = 框架认为这一拍有东西要放，
        //          Build 却什么都没塞 ⇒ 这一拍空转，proc 白白浪费）。
        //
        //  [!] 参考（`闪飒.txt`）的顺序是**先取 CurrentTarget + 判射程**，
        //      再查 `HasAura(3879)` —— 永远不会出现"判定通过但没目标"。
        //
        //  [!] 修法：把 proc 判定**挪到目标选好之后**，并且只影响**返回值**
        //      （12 = 高优先，10 = 普通），不再提前 return。
        //      ⇒ 语义变成"proc 快过期 ⇒ 这一发优先级更高"，
        //        而不是"proc 快过期 ⇒ 什么都不放"。
        // ══════════════════════════════════════════════════════════════════

        // 闪飒要目标（Range=25）
        if (HealTargetHelper.当前目标() == null) return -1;

        // ★ 闪飒是 **AOE**（数据：CastType=2 / Range=25 / **EffectRange=5**）★
        //   [!] 原来固定只打"当前目标" ⇒ 站在怪堆边上时只打到 1 只 ✗
        //   [!] 现在先问一次"以谁为落点能覆盖最多"（半径用它自己的 5 米），
        //       问到就换过去；问不到就退回当前目标（**不因此放弃这一发**）——
        //       闪飒是 proc 限时技能，宁可打 1 只也不能不打。
        var 最优 = 智能选目标.圆形最优(5f, 至少几个: 2);
        if (最优 != null && 最优.对象有效() && 技能数据.打得到(最优, 技能数据.取有效射程(技能)))
        {
            本帧目标 = 最优;
        }
        else
        {
            本帧目标 = HealTargetHelper.当前目标();
        }
        if (本帧目标 == null) return -1;

        // 视线/射程（和别的输出技同一套判断）
        if (!技能数据.打得到(本帧目标, 技能数据.取有效射程(技能))) return -6;

        // ── proc 快过期 ⇒ **提高优先级**（不再提前 return，见上面的说明）──
        //   [!] 判据：`proc剩余 < 读条 × 2000 + 1500ms`
        //       （参考口径；乘 2 是给服务器延迟/动画锁留的余量）。
        //       proc 读不到（<= 0）时**不拦** —— 保守方向取"放行"，
        //       否则一个 API 抽风就把整个神速窗口的输出吞掉。
        var proc快过期 = false;
        try
        {
            var 剩余 = (int)CharacterExt.我的Buff剩余毫秒安全(AuraIds.闪飒预备);
            if (剩余 <= 0) 剩余 = (int)CharacterExt.我的Buff剩余毫秒安全(AuraIds.闪飒预备2);

            if (剩余 > 0)
            {
                var 读条毫秒 = (int)(SpellUtil.读条时间秒(SpellIds.取("闪灼")) * 1000f);
                if (读条毫秒 <= 0) 读条毫秒 = 1500;

                if (剩余 < 读条毫秒 * 2 + 1500) proc快过期 = true;
            }
        }
        catch { }

        // 二次确认：按 开发约定 E 节，不拿没吃透的 API 当**唯一**依据，
        // 但也不让它把已经确定该放的技能挡掉 —— 挡掉时留下证据。
        if (!SpellUtil.可用(技能))
        {
            if (Environment.TickCount64 - 上次诊断 > 5000)
            {
                上次诊断 = TimeHelper.Now();
                LogHelper.Info("[HealerACR] 闪飒预备在身，但 可用(闪飒) = false -> 仍然放");
                // 追一层：直接问游戏自己"到底缺什么"（内部有 10 秒节流）
                技能诊断.报告一次(技能, HealTargetHelper.当前目标());
            }
        }

        // ★ proc 快过期 ⇒ **提高优先级**（12 而不是 8）——
        //   这一发打出去比"下一发"重要，因为它马上就没了。
        //   [!] 注意语义：这是"**更该放**"，不是"别放"。
        //       （原来写成提前 `return 12` 且没定目标 ⇒ Build 空槽 ⇒ proc 被吞掉）
        return proc快过期 ? 12 : 8;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell == null) return;

        var 目标 = 本帧目标;
        if (目标 == null || !目标.对象有效()) return;

        slot.Add(new Spell(spell.Id, 目标));
    }
}

/// <summary>
/// 白魔「神爱抚」(37011, Lv100) —— **群体盾 + HoT**。
///
/// ★ 2026-10-04 新增：原来它只在 `治疗候选` 表里登记了一条，**没有任何 resolver** ✗
///   ⇒ 满级那层（群盾 400 + HoT 200/15s）实际上永远交不出去 ✗
///   （对照实现里它是独立解析器，优先级仅次于节制 ✓）
///
/// 判据（保守版）：
///   · 20 米内低于「群奶阈值」的人数 ≥ 群奶人数 ⇒ 交 ✓
///   · 或者"大伤害要来"（时间轴 / 读条预判）⇒ 交 ✓
///   · 木桩模式让路 ✓
/// </summary>
public class WHM_Serenity : ISlotResolver
{
    private static uint 技能 => SpellIds.取("神爱抚");

    public int Check()
    {
        if (HealTargetHelper.木桩模式) return -300;
        if (!HealQt.GetQt("奶人")) return -100;
        if (!HealQt.GetQt("群奶")) return -101;
        if (技能 == 0) return -102;
        if (!SpellUtil.已解锁(技能)) return -2;

        var 血线 = 治疗阈值表.取(技能, HealSettings.Instance.群体治疗阈值);
        var 人够多 = HealTargetHelper.低于阈值人数(血线, 20f)
                     >= HealTargetHelper.群奶人数要求(HealSettings.Instance.群奶最少人数);
        var 要来 = TimelineManager.未来有减伤(4.0) || 减伤Helper.即将来大伤害();

        if (!人够多 && !要来) return -1;

        return SpellUtil.可用(技能) ? 12 : -1;
    }

    public void Build(Slot slot)
    {
        var spell = SpellUtil.当前形态(技能);
        if (spell != null) slot.Add(spell);   // 以自身为中心，不需要目标/坐标
    }
}
