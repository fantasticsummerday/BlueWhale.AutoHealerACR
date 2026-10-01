using AEAssist;
using AEAssist.Extension;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// "boss 马上要打大伤害了" 的判断 —— 减伤和预铺盾的唯一触发依据。
///
/// 用的是 AEAssist 现成的两个方法（已确认存在）：
///   TargetHelper.targetCastingIsBossAOE(IBattleChara, Int32 timeLeft)
///   TargetHelper.targetCastingIsDeathSentenceWithTime(IBattleChara, Int32 timeLeft)
/// 时间参数是"还剩多少毫秒内会打出来"，所以给一个人为的提前量就能做到预铺。
/// </summary>
public static class 减伤Helper
{
    /// <summary>默认提前量：读条还剩 2.5 秒内就算"马上要来了"</summary>
    public const int 默认提前毫秒 = 2500;

    /// <summary>当前目标是不是正在读一个会打全队的大伤害</summary>
    public static bool 即将来大伤害(int 提前毫秒 = 默认提前毫秒)
    {
        return 正在放大伤害的敌人(提前毫秒) != null;
    }

    /// <summary>正在读大伤害的那个敌人（拿来当减伤技能的参考目标）</summary>
    /// <summary>
    /// 找出"正在憋大招"的敌人。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ⚠️ 修正（参考同类 ACR 的 TargetHelper.CheckAllNearbyEnemiesSkills）
    ///
    ///  原来只查两个单位：当前目标、当前目标正在打的人。
    ///  这有个致命盲区 —— **Boss 在读大伤害条、但你选中了别的东西 →
    ///  完全看不见 → 减伤不触发 → 队友被 AOE 打穿**。
    ///
    ///  现在改成**扫所有敌对目标**（AEAssist.Data.AllHostileTargets）。
    ///  这个 API 我找了好几轮 —— 它藏在 AEAssist.Data 静态类里，
    ///  之前翻 TargetHelper / GameObjectExtension 都找不到。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static IBattleChara? 正在放大伤害的敌人(int 提前毫秒 = 默认提前毫秒)
    {
        // ── ① 扫所有敌对目标（主路径）──
        try
        {
            foreach (var 敌人 in Data.AllHostileTargets)
            {
                if (敌人 == null || 敌人.CurrentHp <= 0) continue;

                if (是危险读条(敌人, 提前毫秒)) return 敌人;
            }
        }
        catch { }

        // ── ② 兜底：当前目标 / 目标的目标（万一拿不到敌对列表）──
        var 目标 = HealTargetHelper.当前目标();
        if (目标 != null && 是危险读条(目标, 提前毫秒)) return 目标;

        var 目标的目标 = 目标?.GetCurrTarget();
        if (目标的目标 != null && 是危险读条(目标的目标, 提前毫秒)) return 目标的目标;

        return null;
    }

    /// <summary>当前是不是高难本（AEAssist 自己的判断，比数人数准）</summary>
    public static bool 是高难本()
    {
        try { return Data.IsInHighEndDuty; }
        catch { return false; }
    }

    /// <summary>玩家当前等级</summary>
    public static int 玩家等级()
    {
        try { return Data.PlayerCurrentLevel; }
        catch { return 100; }
    }

    /// <summary>这个敌人身上有没有"即将落下来的大伤害"</summary>
    public static bool 是危险读条(IBattleChara 敌人, int 提前毫秒 = 默认提前毫秒)
    {
        // ★ 入口判有效性：参数是游戏对象，读它的属性会因【已释放对象】而
        //   触发原生访问违例（穿 catch / 无转储 / 进程直接没）。
        //   本项目 12 次崩溃全部是这一类 —— 不假设调用方判过。
        if (敌人 == null || !敌人.对象有效()) return false;
        if (敌人.CurrentHp <= 0) return false;

        // Boss 的 AOE 读条
        if (TargetHelper.targetCastingIsBossAOE(敌人, 提前毫秒)) return true;

        // 死刑（对坦克的单体大伤害）
        if (TargetHelper.targetCastingIsDeathSentenceWithTime(敌人, 提前毫秒)) return true;

        return false;
    }

    /// <summary>目标值不值得交减伤（boss 或精英怪）</summary>
    public static bool 是Boss(IBattleChara 敌人)
    {
        // ★ **交给 AEAssist 之前先验有效性** —— `TargetHelper.IsBoss` 会读
        //   这个对象的内存，而它**不知道我们的哨兵约定**（已释放对象 = 0x12345679，
        //   不是 null）。把这种对象交出去 ==> 原生访问违例发生**在它内部**，
        //   我们的 try/catch 和全局异常钩子**都在它外面** ==> 进程直接没。
        //   （实测：连续 12 轮排查里钩子一次都没触发，就是因为崩在外部库里。）
        if (敌人 == null || !敌人.对象有效()) return false;
        return TargetHelper.IsBoss(敌人);
    }
}
