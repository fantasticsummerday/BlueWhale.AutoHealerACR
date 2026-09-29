using AEAssist.CombatRoutine;

namespace HealerACR.Common;

/// <summary>
/// 职业技能表：把"四个奶妈的差异"全部收敛到这一个抽象里。
/// 技能 ID 全部走 <see cref="SpellIds.取"/>（内置表，中文名，已用 Lumina dump 核对）。
/// </summary>
public abstract class JobSpellTable
{
    public abstract Jobs Job { get; }
    public abstract string 职业名 { get; }

    // ---------------- 输出 ----------------

    public abstract uint 基础输出 { get; }
    public abstract uint 群体输出 { get; }

    /// <summary>DoT 技能（写当前可用的最高级，用 取已解锁 链）</summary>
    public abstract uint Dot技能 { get; }

    /// <summary>DoT 的 buff id（满级那个）</summary>
    public abstract uint DotBuff { get; }

    /// <summary>
    /// **所有等级段**的 DoT buff id。
    ///
    /// ⚠️ 踩过的坑：DoT 升级会换 buff id
    ///    （占星 烧灼838 → 炽灼843 → 焚灼1881，学者 毒菌179 → 猛毒菌189 → 蛊毒法1895）。
    ///    只检查一个的话，满级用的是最高级 DoT，buff 对不上 → 永远认为"没上 DoT" → 无限补。
    ///    所以这里把所有等级段都列出来，只要身上有其中任意一个就算"已上 DoT"。
    /// </summary>
    public virtual uint[] 所有DotBuff => DotBuff != 0 ? new[] { DotBuff } : Array.Empty<uint>();

    /// <summary>要卡 CD 打的输出能力技（学者的能量吸收这种），空数组表示没有</summary>
    public virtual uint[] 输出能力技 => Array.Empty<uint>();

    /// <summary>
    /// 现在是不是处于"基础输出被游戏替换掉"的状态。
    /// 典型例子：白魔神速咏唱期间闪灼被替换成闪飒，硬放闪灼会直接失败。
    /// </summary>
    public virtual bool 有特殊输出形态 => false;

    // ---------------- 治疗 ----------------

    public abstract uint 单体治疗GCD { get; }
    public abstract uint 群体治疗GCD { get; }
    public abstract uint 紧急单奶 { get; }
    public abstract uint 群体治疗能力技 { get; }

    /// <summary>群体治疗能力技是不是"输出型"（白魔法令）—— 这种卡 CD 打，不等掉血</summary>
    public virtual bool 群体治疗能力技是输出型 => false;

    public virtual uint 单体盾 { get; } = 0;
    public virtual uint 群体盾 { get; } = 0;
    public virtual uint 护盾前置 { get; } = 0;

    // ---------------- 减伤 ----------------

    public virtual uint 团队减伤 { get; } = 0;
    public virtual uint 个人减伤 { get; } = 0;

    // ---------------- 通用 ----------------

    public abstract uint 复活 { get; }
    public abstract uint 驱散 { get; }
    public abstract uint 醒梦 { get; }

    /// <summary>
    /// 放 AOE 至少要有几个敌人。
    ///
    /// ══════════════════════════════════════════════════════════════════
    ///  ⚠️ 从 3 降到 2（对照同类 ACR 的实测值）
    ///
    ///    原来写死 3，四个职业全部继承、没人覆写。问题是**日随一波小怪
    ///    常常只有 2 只** —— 按 3 判就一直在打单体，AOE 永远不触发。
    ///
    ///    对照值（从同类 ACR 的 IL 直读）：
    ///      · 白魔/占星系 = 82 级前 2，82 级后 3
    ///      · 学者/贤者系 = 54 级前 1，54 级后 2
    ///      · 占星系      = 2
    ///    共同点是**低等级更宽松**（低等级 AOE 威力占比更高、怪也更容易成堆）。
    ///    这里取 2 作为统一值 —— 比"按等级分档"简单，且对日随足够。
    ///
    ///    代价：只有 2 个敌人时用 AOE，纯单体收益可能略低一点点。
    ///    但"两只怪还在打单体"是更常见、更明显的损失。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public virtual int AOE最少敌人数 => 2;

    /// <summary>
    /// AOE 技能的**伤害范围**（米）。各职业不一样：
    ///   白魔 神圣/豪圣 = 8、学者 破阵法/裂阵法 = 5、占星 重力/中重力 = 5、贤者 失衡 = 5
    /// 对照官方文档的 CheckNeedUseAOE 第二个参数。
    /// </summary>
    public virtual int AOE伤害范围 => 5;

    // ---------------- 资源管理 ----------------

    /// <summary>现在手上的治疗资源够不够交"能力技群奶"</summary>
    public virtual bool 治疗资源充足 => true;

    /// <summary>脱战时要提前准备的战斗资源（技能 ID），空数组 = 不需要</summary>
    public virtual uint[] 脱战准备技能 => Array.Empty<uint>();

    /// <summary>
    /// 移动中用的**瞬发填充技**，0 表示这个职业没有（那就干脆不放，别硬读条）。
    /// 四奶里只有学者的"毁坏"是纯瞬发填充。
    /// </summary>
    public virtual uint 移动填充技 => 0;

    // ---------------- 便捷判断 ----------------

    public bool 已解锁(uint id) => SpellUtil.已解锁(id);
    public bool 可用(uint id) => SpellUtil.可用(id);
}
