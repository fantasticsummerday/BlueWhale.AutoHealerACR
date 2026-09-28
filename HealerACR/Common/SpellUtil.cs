using AEAssist;
using AEAssist.CombatRoutine;
using AEAssist.Extension;
using AEAssist.MemoryApi;

namespace HealerACR.Common;

/// <summary>
/// 技能表要用的几个工具。
///
/// 单独放一层的原因：AEAssist 不同版本里"取技能实体""自动换成当前等级的技能"
/// 这两件事的 API 名字变过，集中在这里，换版本只改这个文件。
/// </summary>
public static class SpellUtil
{
    /// <summary>
    /// 取技能实体。所有 xxx.GetSpell() 最终都走这里。
    /// </summary>
    public static Spell? Get(uint id)
    {
        if (id == 0) return null;
        try
        {
            return id.GetSpell();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>这个技能当前等级学会了吗</summary>
    public static bool 已解锁(uint id)
    {
        if (id == 0) return false;
        try
        {
            return id.GetSpell().IsUnlock();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>现在能用吗（含施法条件检查）</summary>
    public static bool 可用(uint id)
    {
        if (id == 0) return false;

        var s = Get(id);
        if (s == null) return false;

        // ==================== 这一段改动前请先读完 ====================
        //
        // 官方文档原文（完整版，注意【】里的限定词）：
        //
        //     技能是否可用 IsReady
        //
        //     注意，考虑到 Buff 延迟，【部分有 Buff 才亮的技能】，可能需要你自行
        //     比对本地有没有释放获取 Buff 的技能。【此时】你可以通过 IsUnlock
        //     检查是否学习/等级够不够，接着使用 xxx.GetSpell().Cooldown 来检测
        //     技能 CD，再检测本地有没有释放过获取对应 Buff 的技能
        //
        // → **主判断始终是 IsReady**。「IsUnlock + Cooldown」是【只在 Buff 延迟
        //    导致技能不亮时】才用的**补充手段**，不是通用替代方案。
        //
        // 我在这一点上翻车两次，别再犯：
        //   0.6.7  换成 s.Cooldown > TimeSpan.Zero —— 语义没验证，恒真
        //          → 所有技能不可用
        //   0.7.3  加 !s.IsUnlock()               —— 对召唤技/爆发技返回 false
        //          → 小仙女不召唤、爆发不打
        //
        // 实测证据：0.5.5 的实机日志里有 "CastSpell success: 17215 朝日召唤"，
        // 那一版用的就是这个判断。**实测优先于文档的一般性建议。**
        //
        // 真要做"Buff 延迟"的补充判断，应该**针对具体技能**在它自己的 resolver 里做，
        // 而不是在这个通用入口上一刀切。
        // =============================================================
        return s.IsReadyWithCanCast();
    }

    /// <summary>
    /// 等级变换：把"最初形态"的技能换成当前等级该用的那个。
    /// 白魔 Stone(1) → StoneII → Glare → GlareIII，学者 Ruin → Broil IV …
    ///
    /// ⚠️ 如果你的 AEAssist 版本里 MemApiSpell 没有 CheckActionChange，
    ///    把这里改成 return Get(id); 就行（代价是低等级技能不会自动升级）。
    /// </summary>
    public static Spell? 当前形态(uint id)
    {
        if (id == 0) return null;
        try
        {
            return Core.Resolve<MemApiSpell>().CheckActionChange(id).GetSpell();
        }
        catch
        {
            return Get(id);
        }
    }

    /// <summary>从高到低挑第一个已解锁的技能（等级降级用），都没解锁返回 0</summary>
    public static uint 取已解锁(params uint[] ids)
    {
        foreach (var id in ids)
        {
            if (已解锁(id)) return id;
        }

        return 0;
    }
}

/// <summary>
/// 血量 / 蓝量的统一读法。
/// 直接用 Dalamud 原生属性算，不依赖任何 AEAssist 版本特有的扩展方法。
/// </summary>
