using AEAssist;
using AEAssist.Extension;

namespace HealerACR.Common;

/// <summary>
/// 职业 ID 表 —— 对照 鍚岀被 ACR 的 `鍚岀被 ACRTargetHelper.IsHealerJob`。
///
/// 它的实现里是一串硬编码常量：`常量(24, 28, 33, 40, 1)`
/// —— 正是四个奶妈的职业 ID。
///
/// **为什么要单独一张表**：
///   复活优先级、团队减伤分配、资源协调都要判"这是不是奶妈/坦克"，
///   把 ID 散在各处的话，将来加职业或者改判定会漏。
/// </summary>
public static class 职业表
{
    // ── 治疗 ──
    public const uint 白魔 = 24;
    public const uint 学者 = 28;
    public const uint 占星 = 33;
    public const uint 贤者 = 40;

    public static readonly uint[] 奶妈 = { 白魔, 学者, 占星, 贤者 };

    // ── 坦克 ──
    public const uint 骑士 = 19;
    public const uint 战士 = 21;
    public const uint 暗黑骑士 = 32;
    public const uint 绝枪战士 = 37;
    // 20 是"剑术师"（骑士的前置职业），低等级会遇到

    public static readonly uint[] 坦克 = { 19, 20, 21, 32, 37 };

    // ── 近战（发卡/龙肠之类的近战优先判断会用）──
    public static readonly uint[] 近战 =
    {
        20, 22, 30, 34, 19, 21, 32, 37, 39, 41,   // 剑术/斧术/枪术/格斗 + 四个坦克 + 武僧/龙骑
    };

    // ── 远程物理 ──
    public static readonly uint[] 远程物理 = { 23, 31, 38 };   // 弓术/机工/舞者

    // ── 法系 ──
    public static readonly uint[] 法系 = { 25, 27, 35, 36, 42 };  // 咒术/秘术/召喚/黑魔/绘灵

    /// <summary>这个玩家是不是治疗职业</summary>
    public static bool 是奶妈(IBattleChara? c)
    {
        var id = 取职业(c);
        return id != 0 && Array.IndexOf(奶妈, id) >= 0;
    }

    /// <summary>这个玩家是不是坦克</summary>
    public static bool 是坦克(IBattleChara? c)
    {
        var id = 取职业(c);
        return id != 0 && Array.IndexOf(坦克, id) >= 0;
    }

    public static bool 是近战(IBattleChara? c)
    {
        var id = 取职业(c);
        return id != 0 && Array.IndexOf(近战, id) >= 0;
    }

    public static bool 是远程(IBattleChara? c)
    {
        var id = 取职业(c);
        return id != 0 && Array.IndexOf(远程物理, id) >= 0;
    }

    public static bool 是法系(IBattleChara? c)
    {
        var id = 取职业(c);
        return id != 0 && Array.IndexOf(法系, id) >= 0;
    }

    /// <summary>取职业 ID（拿不到返回 0）</summary>
    public static uint 取职业(IBattleChara? c)
    {
        if (c == null) return 0;

        try { return c.ClassJob.RowId; }
        catch { return 0; }
    }

    /// <summary>我自己是不是奶妈（用于自检）</summary>
    public static bool 我是奶妈()
    {
        try { return Array.IndexOf(奶妈, Core.Me.ClassJob.RowId) >= 0; }
        catch { return false; }
    }
}
