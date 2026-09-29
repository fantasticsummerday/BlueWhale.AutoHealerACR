using AEAssist.Extension;
using AEAssist.MemoryApi;
using AEAssist.Helper;

namespace HealerACR.Common;

/// <summary>
/// 技能数据查询 —— 把 Lumina 的游戏数据表接进来。
///
/// ══════════════════════════════════════════════════════════════════
///  为什么需要它（"自动识别地面技能"）：
///
///  地面放置技能（罩子/地星/庇护所/礼仪之铃）需要给**坐标**而不是目标，
///  而现在这个判断是靠 `JobSpellTable` 里配了哪些技能 ——
///  **每加一个地面技能都要记得去改表，很容易漏**。
///
///  我 dump 了 Lumina 的 Action 表，发现 `CastType` 能可靠区分：
///      7 = 地面放置（罩子 188 / 庇护所 3569 / 地星 7439 / 礼仪之铃 25862 全是 7）
///      2 = 普通（目标或自身为中心的 AOE）
///      1 = 单体
///
///  所以可以直接问游戏数据，不用手工维护表。
///
///  ⚠️ 但要注意：`CastType` **区分不了直线和圆形**（神圣/箭毒/失衡都是 2），
///     所以形状判断还是得靠 `智能选目标` 里的人工标注。
/// ══════════════════════════════════════════════════════════════════
/// </summary>
public static class 技能数据
{
    /// <summary>地面放置型技能的 CastType 值</summary>
    public const int 地面放置 = 7;

    /// <summary>
    /// 取技能的 CastType（拿不到返回 0）。
    ///
    /// 走的是 AEAssist 的 `LuminaHelper.GetExcelRow` ——
    /// 它封装了 Dalamud 的 DataManager，ACR 里能直接用。
    /// </summary>
    public static int 取CastType(uint 技能Id)
    {
        if (技能Id == 0) return 0;

        try
        {
            var 行 = LuminaHelper.GetExcelRow<Lumina.Excel.Sheets.Action>(技能Id);
            if (行 == null) return 0;

            return (int)行.Value.CastType;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>这个技能是不是"要放在地上的"（需要坐标参数）</summary>
    public static bool 是地面技能(uint 技能Id)
    {
        return 取CastType(技能Id) == 地面放置;
    }

    /// <summary>技能射程（米），拿不到返回 0</summary>
    public static int 取射程(uint 技能Id)
    {
        try
        {
            var 行 = LuminaHelper.GetExcelRow<Lumina.Excel.Sheets.Action>(技能Id);
            if (行 == null) return 0;
            return 行.Value.Range;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>技能效果范围（米），拿不到返回 0</summary>
    public static int 取效果范围(uint 技能Id)
    {
        try
        {
            var 行 = LuminaHelper.GetExcelRow<Lumina.Excel.Sheets.Action>(技能Id);
            if (行 == null) return 0;
            return 行.Value.EffectRange;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>技能名（本地化），拿不到返回空</summary>
    public static string 取名(uint 技能Id)
    {
        try
        {
            var 行 = LuminaHelper.GetExcelRow<Lumina.Excel.Sheets.Action>(技能Id);
            if (行 == null) return "";
            return 行.Value.Name.ToString();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 这个技能是不是"以自己为中心"（Range = 0 且不是地面技能）。
    ///
    /// 用途：自身为中心的 AOE（神圣/失衡）**不需要指定目标**，
    /// 而指向型的（箭毒/苦难之心）需要。
    /// </summary>
    public static bool 是自身中心技能(uint 技能Id)
    {
        try
        {
            if (是地面技能(技能Id)) return false;

            var 行 = LuminaHelper.GetExcelRow<Lumina.Excel.Sheets.Action>(技能Id);
            if (行 == null) return false;

            return 行.Value.Range == 0 && 行.Value.EffectRange > 0;
        }
        catch
        {
            return false;
        }
    }

    // ==================== 视线检查 ====================

    /// <summary>
    /// 从我这里看，目标是不是被墙/柱子挡住了。
    ///
    /// **参考同类 ACR 的 `IsTargetVisibleOrInRange`** ——
    /// 它在判断"这个技能能不能打到"时会检查视线，
    /// 而只看距离是不够的：**目标可能在墙后面，距离够但打不到**。
    ///
    /// 用的是 AEAssist 自带的实现：
    ///     GameObjectExtension.ToStruct(IGameObject) → GameObject*
    ///     MemApiSpell.LineOfSightChecker.IsBlocked(GameObject*, GameObject*)
    ///
    /// ⚠️ 用指针，所以这个文件必须允许 unsafe（csproj 里已开）。
    ///    整个方法包在 try 里 —— 拿不到指针就返回 false（当作"没被挡"），
    ///    因为"误判为被挡"会导致技能永远不放，比误判为没挡更糟。
    /// </summary>
    public static bool 视线被挡(IGameObject? 目标)
    {
        if (目标 == null) return false;

        try
        {
            unsafe
            {
                var 我 = AEAssist.Core.Me.ToStruct();
                var 它 = 目标.ToStruct();

                if (我 == null || 它 == null) return false;

                return MemApiSpell.LineOfSightChecker.IsBlocked(我, 它);
            }
        }
        catch
        {
            return false;   // 判断不了就当没挡
        }
    }

    /// <summary>
    /// 这个目标"看得见而且在射程内"。
    ///
    /// 比单纯判距离多一层视线检查 ——
    /// 副本里柱子、墙角后面很常见，光看距离会一直对着墙放技能。
    /// </summary>
    public static bool 可攻击(IBattleChara? 目标, float 射程 = 25f)
    {
        if (目标 == null || 目标.CurrentHp <= 0) return false;

        try
        {
            var 距离 = AEAssist.Extension.GameObjectExtension.InRange(AEAssist.Core.Me, 目标, (int)射程);
            if (!距离) return false;

            return !视线被挡(目标);
        }
        catch
        {
            return true;   // 判断不了就放行
        }
    }

    /// <summary>
    /// 技能层面的"打得到吗" —— 供各输出 resolver 的 Check 复用。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ★ 为什么把它接进 resolver（原先写了却没人调用）★
    ///
    ///    `可攻击()` / `视线被挡()` 早就写好了，但一直没接入任何 resolver，
    ///    等于白写。不接的代价：**对着柱子/墙角空放技能** ——
    ///    距离判断是过了，但技能打不出去（或者打空），
    ///    表现出来就是"输出循环卡住不动"。
    ///
    ///  ★ 为什么默认射程是 25 而不是技能真实射程 ★
    ///
    ///    奶妈的输出技能射程基本都是 25 米（`/ac` 的施法距离），
    ///    而**拿不到真实射程时不能编一个** —— 所以参数留了口子，
    ///    调用方有准确射程就传，没有就用 25。
    ///
    ///  ★ 误判方向是刻意选的 ★
    ///
    ///    `视线被挡()` 在拿不到指针 / 异常时返回 false（= 没被挡），
    ///    `可攻击()` 在异常时返回 true（= 放行）。
    ///    **两个兜底都偏向"放行"** —— 因为"误判为被挡"会让技能永远不放
    ///    （输出完全停摆），比"偶尔对着墙放一次"严重得多。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static bool 打得到(IBattleChara? 目标, float 射程 = 25f) => 可攻击(目标, 射程);

    /// <summary>诊断输出（调试面板用）</summary>
    public static string 描述(uint 技能Id)
    {
        if (技能Id == 0) return "（无技能）";

        try
        {
            var 行 = LuminaHelper.GetExcelRow<Lumina.Excel.Sheets.Action>(技能Id);
            if (行 == null) return $"id={技能Id} 查不到数据";

            var a = 行.Value;
            var 形状 = (int)a.CastType switch
            {
                1 => "单体",
                2 => "圆形",
                3 => "扇形",
                4 => "自身圆形",
                7 => "地面放置",
                _ => $"未知({(int)a.CastType})",
            };

            return $"{a.Name} 形状={形状} 射程={a.Range} 效果={a.EffectRange}";
        }
        catch (Exception e)
        {
            return $"读取失败: {e.Message}";
        }
    }
}
