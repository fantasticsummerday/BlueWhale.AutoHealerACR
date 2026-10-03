using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.CombatRoutine.Module;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// **低蓝闸门装饰器** —— 一处判断覆盖整条输出/爆发链。
///
/// ══════════════════════════════════════════════════════════════════
///  ★ 为什么做这个（取自对照实现 B 的 `Scholar_OffGcdGate`）★
///
///  ── 我们原来的问题 ──
///    `蓝量.低蓝停手()` 这个检查**散在 10 处**，每个输出类 resolver
///    各写各的。漏一处，那条路就会在低蓝时继续烧蓝。
///
///    而且散着写没法保证一致：有的返回 -9、有的返回 -4、有的 -5，
///    以后再加输出技能，很容易忘了加这一句。
///
///  ── 参考实现 B 的做法（IL 直证）──
///    `Scholar_OffGcdGate` 是个装饰器，把**全部 25 个 oGCD** 包一遍：
///        Check() => IsLowMpStopActive() ? -1 : _inner.Check();
///    一处判断覆盖全部 —— 这是"横切关注点"该有的写法。
///
///  ══════════════════════════════════════════════════════════════════
///  ⚠️⚠️ **绝对不要把这个装饰器套到治疗上** ⚠️⚠️
///
///    我们**只套"类型名表明是纯输出/纯爆发"的 resolver**
///    （`Res_Damage` / `Res_Offense` / `爆发轴`）。
///
///    原因：**低蓝时治疗该不该停，是另一个决策** ——
///    原来的行为是"低蓝只停输出，治疗照常"（所有带这个判断的
///    调用点都只在输出侧）。如果这里一刀切套到治疗 resolver 上，
///    就会凭空改变治疗行为 —— 那正是开发约定 ⑦
///    「加了不该加的前置条件」那类错误，而且这次会直接害死人。
///
///    所以 `套上()` 里的名字过滤**是安全机制，不是优化** ——
///    改它之前先想清楚"低蓝时治疗要不要停"。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public sealed class Res_低蓝闸门 : ISlotResolver
{
    private readonly ISlotResolver _内层;

    public Res_低蓝闸门(ISlotResolver 内层) => _内层 = 内层;

    /// <summary>
    /// 低蓝 → 直接让路（返回负数，不参与仲裁）。
    ///
    /// ⚠️ 返回值和原来各处用的 -9 保持一致 —— 它只是个负数，
    ///    框架只判 `>= 0`，具体数值不影响行为（只影响调试日志）。
    /// </summary>
    public int Check()
    {
        try
        {
            if (蓝量.低蓝停手()) return -9;
        }
        catch { }

        return _内层.Check();
    }

    public void Build(Slot slot) => _内层.Build(slot);

    /// <summary>
    /// 这个 resolver 是不是"纯输出 / 纯爆发"（可以套低蓝闸门）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 判据：**看它 Check() 里用没用技能表的"输出栏位"** ★
    ///
    ///  ── 为什么不用类型名 ──
    ///    第一版我按名字过滤（含 `Damage`/`Offense`），实测**只套到 2 个** ——
    ///    真正的输出类一个都没套上：
    ///      `Res_Dot` / `Res_OffensiveAbility` / `SGE_Dot` /
    ///      `WHM_AflatusMisery` / `SGE_Phlegma` / `Res_MoveGcd` …
    ///    一个都没套上，等于这个装饰器白做。
    ///    名字是随手起的，靠它分类不可靠。
    ///
    ///  ── 现在为什么可靠 ──
    ///    技能表 `JobSpellTable` 里，**输出栏位和治疗栏位是分开的**：
    ///      输出：`基础输出` / `群体输出` / `Dot技能` / `移动填充技` / `输出能力技`
    ///      治疗：`单体治疗GCD` / `群体治疗GCD` / `紧急单奶` / `单体盾` / …
    ///    一个 resolver 只要碰了输出栏位，它就是输出类 ——
    ///    这是**语义判据**，改名也打不破。
    ///
    ///  ⚠️ 实现方式：读 `Check()` 的 IL 字节，找有没有引用那几个属性。
    ///    反射读 IL 是安全的（只读元数据，不执行）；任何异常都当成"不是输出"，
    ///    也就是**不套闸门** —— 保守方向正确（宁可不套，不可乱套）。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    private static bool 是纯输出(ISlotResolver r)
    {
        try
        {
            var 方法 = r.GetType().GetMethod("Check", Type.EmptyTypes);
            if (方法 == null) return false;

            var 体 = 方法.GetMethodBody();
            if (体 == null) return false;   // 没有 IL（比如抽象）→ 不套

            var il = 体.GetILAsByteArray();
            if (il == null || il.Length == 0) return false;

            // 找出这个 resolver 持有的 `JobSpellTable` 字段
            var 表类型 = typeof(JobSpellTable);
            var 表字段 = r.GetType()
                .GetFields(System.Reflection.BindingFlags.Instance
                           | System.Reflection.BindingFlags.NonPublic
                           | System.Reflection.BindingFlags.Public)
                .Where(f => 表类型.IsAssignableFrom(f.FieldType))
                .ToList();

            if (表字段.Count == 0) return false;   // 不持有技能表 → 判不了 → 不套

            // 输出栏位的属性（含中文名，用 GetProperty 的动态方式匹配）
            var 输出栏位 = new[]
            {
                "基础输出", "群体输出", "Dot技能", "移动填充技", "输出能力技",
            };

            foreach (var 属性名 in 输出栏位)
            {
                var pi = 表类型.GetProperty(属性名);
                if (pi == null) continue;

                var getter = pi.GetGetMethod(true);
                if (getter == null) continue;

                // IL 里 `callvirt get_X` 的操作数是 **MethodDef/ MemberRef token**
                // → 在 `callvirt`(0x6F) / `call`(0x28) 之后 4 字节
                if (IL里调用了(il, getter.MetadataToken)) return true;
            }

            return false;
        }
        catch
        {
            return false;   // 判不了 → 不套（保守：不乱加前置条件）
        }
    }

    /// <summary>
    /// 扫一遍 IL，看有没有 `call`/`callvirt` 到指定的方法 token。
    ///
    /// ⚠️ 这是个**粗扫**（不解析完整指令流），可能把操作数里的巧合字节当成命中。
    ///    但方向是安全的：
    ///      · 误判成"是输出" → 多套一个低蓝闸门 → 那个 resolver 低蓝时让路
    ///      · 误判成"不是输出" → 不套 → 退回原来的行为
    ///    两者都不会让治疗停摆（治疗 resolver 本来就不引用输出栏位）。
    /// </summary>
    private static bool IL里调用了(byte[] il, int token)
    {
        try
        {
            var t = BitConverter.GetBytes(token);
            for (var i = 0; i + 5 <= il.Length; i++)
            {
                // call = 0x28, callvirt = 0x6F
                if (il[i] != 0x28 && il[i] != 0x6F) continue;

                if (il[i + 1] == t[0] && il[i + 2] == t[1]
                    && il[i + 3] == t[2] && il[i + 4] == t[3]) return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 给整条队列套上低蓝闸门（**只套纯输出/纯爆发**，见上面的警告）。
    ///
    /// 用法：在 `构建决策队列_职业专属()` 的返回值上过一道。
    /// </summary>
    public static List<SlotResolverData> 套上(List<SlotResolverData> 队列)
    {
        try
        {
            if (队列 == null) return 队列!;

            foreach (var d in 队列)
            {
                if (d?.SlotResolver == null) continue;
                if (!是纯输出(d.SlotResolver)) continue;

                // 已经套过就别套第二层（重复套虽然无害，但会让日志难读）
                if (d.SlotResolver is Res_低蓝闸门) continue;

                d.SlotResolver = new Res_低蓝闸门(d.SlotResolver);
            }
        }
        catch { }

        return 队列!;
    }
}
