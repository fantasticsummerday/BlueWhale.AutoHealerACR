using AEAssist;
using AEAssist.CombatRoutine.Module;
using AEAssist.Extension;
using AEAssist.Helper;
using HealerACR.Common;

namespace HealerACR.Resolvers;

/// <summary>
/// **移动中开即刻咏唱** —— 让读条技能在移动中放得出来。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么需要它（用户实测："全程移动 ai 不奶了"）★
///
///    `SpellUtil.移动中能放()` 的判据是：
///        ① 没在移动/空中 -> 放行
///        ② 正在读条     -> 放行（不能打断自己）
///        ③ **有即刻类 buff -> 放行**   ← 只看"已经开着"
///        ④ 看技能本身有无读条 -> 瞬发才放行
///
///    ⇒ 它只**尊重**即刻 buff，**没有任何地方主动开它**。
///
///    全项目只有两处用了即刻（`Res_PotionTail` 爆发药、`Res_Raise` 拉人），
///    **治疗 / 盾没有这条路** —— 于是移动中：
///        · GCD 治疗（医术/救疗/鼓舞…）全被挡住
///        · oGCD 治疗有 CD 或吃资源（以太超流 / 小仙女）
///        · 资源一空 => **一口都放不出来**
///
///  ★ 为什么补在本地层、而不是只加 AI 候选 ★
///
///    AI 候选只在"AI 启用 + 建议被采纳"时生效。
///    AI 关掉 / 未就绪 / 熔断时（`该走原版逻辑`）**本地必须自己会做** ——
///    这是整个项目的降级原则。
///
///  ★ 时机与代价 ★
///
///    即刻咏唱：能力技，CD **60 秒**，buff 持续 **10 秒**（官表 7561）。
///    所以只在"**真的有东西需要它**"时才开 —— 详见 `Check()`。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public class Res_移动开即刻 : ISlotResolver
{
    private readonly JobSpellTable _t;

    public Res_移动开即刻(JobSpellTable table) => _t = table;

    /// <summary>最近一次没开的原因（诊断用）</summary>
    public static string 上次原因 { get; private set; } = "";

    public int Check()
    {
        try
        {
            // ── ① 开关 / 木桩 ──
            if (HealTargetHelper.木桩模式) return -300;

            // ── ② 必须"移动中或空中" ──
            //     [!] 不移动时不需要即刻（读条本来就能放），
            //         开它是纯浪费 60 秒 CD。
            var 受限 = SpellUtil.在移动() || 空中检测.在空中;
            if (!受限) { 上次原因 = "没在移动"; return -1; }

            // ── ③ 已经有即刻 buff -> 不用再开 ──
            if (Core.Me.HasAura(AuraIds.即刻)) { 上次原因 = "已有即刻buff"; return -2; }

            // ── ④ 即刻可用吗 ──
            var id = SpellIds.取("即刻咏唱");
            if (id == 0) { 上次原因 = "没配即刻ID"; return -102; }
            if (!SpellUtil.已解锁(id)) { 上次原因 = "未解锁"; return -3; }
            if (!SpellUtil.可用(id)) { 上次原因 = "CD中"; return -4; }

            // ── ⑤ **真的有东西需要它吗** ★ 核心判据 ★ ──
            //
            //  [!] 不判这一条的话，只要在移动就会开即刻 ——
            //      结果"走两步就烧掉 60 秒 CD"，真需要救命时没得用。
            //
            //  判据（任一成立就值得开）：
            //    · **有人血线告急**（缺口够大）—— 移动中救命的唯一手段
            //    · 减伤说**马上要挨大伤害**，而盾/治疗是读条的
            if (!有需要()) return -5;

            上次原因 = "可以开";
            return 8;      // 高于普通输出/填充，低于真正的救命（那些是 10+ / 20+）
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>现在有没有"必须靠即刻才能做"的事</summary>
    private static bool 有需要()
    {
        try
        {
            // ① 有人血线告急？（用紧急阈值，不是普通治疗阈值）
            var 阈值 = HealSettings.Instance.紧急单奶阈值;
            if (HealTargetHelper.低于阈值人数(阈值, 30f) > 0) return true;

            // ② 马上要挨大伤害，而盾是读条的？
            //    [!] 只在"坦克压力上来"或时间轴报了减伤时才认 ——
            //        否则每次跑位都会开即刻。
            if (减伤Helper.即将来大伤害()) return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    public void Build(Slot slot)
    {
        try
        {
            var id = SpellIds.取("即刻咏唱");
            if (id == 0) return;

            var spell = SpellUtil.Get(id);
            if (spell == null) return;

            slot.Add(spell);
            Ai调试入口.记($"[即刻] 移动中开即刻（{上次原因}）—— 让读条技能放得出来");
        }
        catch { }
    }
}

/// <summary>
/// 让本地层也能往 AI 日志里写一行（AI 没挂载时静默）。
///
/// [!] 为什么需要：`HealerACR` 编译期看不到 `BlueWhale` 命名空间
///     （两个程序集，BlueWhale 引用 HealerACR，反过来不行），
///     所以走反射 —— 和 `内存库钩子` / `阈值钩子` 是同一个套路。
/// </summary>
internal static class Ai调试入口
{
    private static Action<string>? _记;

    public static void 记(string 文本)
    {
        try
        {
            if (_记 == null)
            {
                foreach (var 程序集 in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? 类型 = null;
                    try { 类型 = 程序集.GetType("BlueWhale.AutoHealerACR.Ai调试", false); }
                    catch { }
                    if (类型 == null) continue;

                    var 方法 = 类型.GetMethod("日志",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (方法 == null) continue;

                    _记 = (Action<string>?)Delegate.CreateDelegate(typeof(Action<string>), 方法, false);
                    break;
                }
            }

            _记?.Invoke(文本);
        }
        catch { }
    }
}
