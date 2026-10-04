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

    // ══════════════════════════════════════════════════════════════════
    //  ★ **坦克死刑预判**（表 #38）★
    //
    //  [!] 参考实现里这是一个**独立于"大伤害"之外**的函数
    //      （`HealerEnemyTargetHelper.HasBossCastingTankbusterWithin(int)`，IL 直读）：
    //
    //        · 遍历 `TargetMgr.Enemys`
    //        · 只取 `TargetHelper.IsBoss(敌)` 的
    //        · 距离 **≤30 米**（远了不关我的事）
    //        · `IsTargetCastingActionSoon()` 且 `Helper.IsTankDeathSentence(CastActionId)`
    //        · 有就返回 true
    //
    //  [!] 为什么单独拎出来（而不是继续复用 `即将来大伤害()`）：
    //      `即将来大伤害()` 查的是 **BossAOE ∪ DeathSentence** 两类，
    //      语义是"**全队**要吃一发大的" —— 减伤、群盾那些用它最合适。
    //      而**死刑是打坦克一个人的**，它的正确应对是**给坦克单体预铺**
    //      （单盾 / 输血 / 绿帽），不是"全队减伤"。
    //      ⇒ 两个决策要用**各自的判据**，混用会让"坦克死刑"被当成"AOE"
    //        去触发团减（浪费），或者反过来该预铺的时候没预铺 ✗
    //
    //  [!] 默认提前量给 **5000ms**（比 AOE 的 2500 长）：
    //      单体盾有读条、输血是能力技要在窗口里插 —— 预铺必须比 AOE 更早。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>坦克死刑的默认预判窗口（毫秒）—— 比 AOE 的提前量更长</summary>
    public const int 死刑预判毫秒 = 5000;

    /// <summary>boss 距离多远之内才关心（参考口径 30 米）</summary>
    private const float 死刑关心距离 = 30f;

    /// <summary>
    /// **30 米内有没有 boss 正在读一发坦克死刑**（`timeLeftMs` 毫秒内落地）。
    ///
    /// [!] 判据四条（照参考的 IL）：是 boss + ≤30 米 + 很快放出来 + 那一下是死刑。
    /// </summary>
    public static bool boss要打坦克死刑(int 提前毫秒 = 死刑预判毫秒)
    {
        return 正在读死刑的boss(提前毫秒) != null;
    }

    /// <summary>正在读坦克死刑的那个 boss（拿来当"给谁预铺"的参考；没有返回 null）</summary>
    public static IBattleChara? 正在读死刑的boss(int 提前毫秒 = 死刑预判毫秒)
    {
        try
        {
            foreach (var 敌人 in Data.AllHostileTargets)
            {
                if (敌人 == null || !敌人.对象有效()) continue;
                if (敌人.CurrentHp <= 0) continue;
                if (!是Boss(敌人)) continue;

                try { if (敌人.Distance(Core.Me!) > 死刑关心距离) continue; } catch { }

                if (TargetHelper.targetCastingIsDeathSentenceWithTime(敌人, 提前毫秒)) return 敌人;
            }
        }
        catch { }

        return null;
    }
}
