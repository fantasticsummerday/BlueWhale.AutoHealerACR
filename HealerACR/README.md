# HealerACR —— 日随用四奶妈 ACR 骨架

按 **AEAssist 3.0（卫月 / Dalamud 版）** 的 ACR 开发方式
（`IRotationEntry` + `ISlotResolver` + `SlotResolverData/SlotMode`）搭的骨架，
**一份工程同时适配白魔 / 学者 / 占星 / 贤者**，等级 **1 → 100**。

思路：四个奶妈的**行为逻辑是同一套**，差异只在**技能 ID 和职业资源**。
所以技能差异收进 `JobSpellTable`，职业资源收进 `JobApiHelper`，
治疗 / 输出 / 减伤 / 时间轴逻辑写成与职业无关的模块。

---

## 一、目录结构

```
HealerACR/
├── HealerACR.csproj          编译配置（改一个路径就能用）
├── NuGet.config              ★ AEAssist.NET 只在 myget 上，必须有这个文件
├── Common/
│   ├── JobSpellTable.cs      ★ 抽象技能表：四个职业的技能差异全在这
│   ├── JobApiHelper.cs       ★ 职业资源：蛇胆 / 以太 / 百合 / 手牌
│   ├── SpellUtil.cs          取技能 / 等级变换 / 血量比例
│   ├── AuraIds.cs            所有 buff id 的集中地
│   ├── MitigationHelper.cs   "boss 马上要打大伤害了"（看读条）
│   ├── HealTargetHelper.cs   "这个技能给谁"
│   ├── HealSettings.cs       共用设置（阈值 / 时间轴 / 卡牌关键词）
│   ├── HealQt.cs             QT 开关统一入口
│   └── HealerEntryBase.cs    入口基类 + 事件回调 + 阈值滑条面板
├── Resolvers/
│   ├── Res_Raise.cs          复活
│   ├── Res_Esuna.cs          驱散
│   ├── Res_Heal.cs           紧急单奶 / 群奶 / 单奶 / 单体盾预铺
│   ├── Res_Mitigation.cs     团队减伤 / 个人减伤 / 群体护盾
│   ├── Res_Damage.cs         DoT / AOE / 兜底单体
│   └── Res_LucidDreaming.cs  醒梦
├── Timeline/                 ★ cactbot 时间轴
│   ├── CactbotTimelineParser.cs  解析 .txt（含 jump / label）
│   ├── TimelineRunner.cs         运行时：游标 + 时间偏移 + 分支判定
│   └── TimelineManager.cs        按副本加载 / 对外查询
├── Timelines/                ★ 把 cactbot 的时间轴 .txt 丢这里
│   ├── 说明.txt
│   └── valigarmanda.txt      （样例）
├── Opener/
│   └── 预铺起手.cs           倒计时预铺
└── Jobs/
    ├── WhiteMageACR.cs       百合 / 神速魔 / 苦难之心
    ├── ScholarACR.cs         小仙女 / 炽天使 / 以太超流
    ├── AstrologianACR.cs     卡牌分配 / 地星 / 大小阿卡纳 / 光速
    └── SageACR.cs            均衡DoT / 心关 / 根素 / 箭毒
```

---

## 二、编译 & 部署

1. 改 `HealerACR.csproj`：

   ```xml
   <AEAssistAcrDir>C:\你的AEAssist目录\Output\ACR</AEAssistAcrDir>
   ```

2. 依赖走 NuGet（`AEAssist.NET` 不在 nuget.org 上，靠同目录 `NuGet.config` 指向 myget）：

   ```xml
   <PackageReference Include="AEAssist.NET" Version="1.2.*" ExcludeAssets="runtime" />
   ```

3. `dotnet build -c Release` → 产物落在 `...\ACR\HealerACR\`，
   **dll 必须和所在文件夹同名**（文档里点名的坑）。
4. 时间轴放 `...\ACR\HealerACR\Timelines\`（跟着 dll 走）。
5. 进游戏点「重新加载 dll」（需先开开发者模式）。

---

## 三、决策队列

框架从上往下扫，**第一个 `Check() >= 0` 的说了算**，所以顺序 = 优先级。

### GCD 队列

| 顺序 | Resolver | 触发条件 |
|---|---|---|
| 1 | `Res_Raise` | 有人躺着 + 有即刻 |
| 2 | `Res_Esuna` | 有人带可驱散 debuff |
| 3 | 职业特色治疗 | 白魔 欢愉 / 安慰（百合免蓝）；贤者 群盾预铺 |
| 4 | `Res_HealAoEGcd` | 低于群奶血线的人 ≥「群奶最少人数」 |
| 5 | `Res_HealSingleGcd` | 有人低于单奶血线 |
| 6 | `Res_HealShield` | 坦克掉血 / boss 要打大伤害（学者、贤者） |
| 7 | 职业特色输出 | 白魔 苦难之心；贤者 箭毒（临近大伤害都不打） |
| 8 | `Res_Dot` | 目标身上没有我的 DoT |
| 9 | `Res_AoEDamage` | 周围敌人 ≥ 3 |
| 10 | `Res_BaseDamage` | 兜底，**必须最后** |

### OffGcd 队列

| 顺序 | Resolver | 触发条件 |
|---|---|---|
| 1 | `Res_HealEmergency` | 有人低于紧急血线 → 交大加 |
| 2 | 炽天使技能 | 学者：慰藉（群盾） |
| 3 | 小仙女技能 | 学者：祝福（免费群奶） |
| 4 | `Res_HealAoEAbility` | 团队掉血 → 群奶能力技（大伤害要来时**留着**） |
| 5 | 豆子 | 以太超流 / 根素（读内存，不溢出） |
| 6 | 职业专属 | 妖精契约 / 炽天使 / 卡牌 / 地星 / 光速 / 心关 / 神速魔 |
| 7 | `Res_SelfMitigation` | 自己血少 或 要挨打 → 个人减伤 |
| 8 | `Res_TeamMitigation` | **时间轴说了** 或 boss 读条 → 团队减伤 |
| 9 | `Res_LucidDreaming` | 蓝量低于阈值 |

---

## 四、职业特色资源

### 白魔 —— 百合系统

| 资源 | API | 用法 |
|---|---|---|
| 百合 0-3 | `JobApi_WhiteMage.Lily` | 花 1 颗放 **安慰**（单奶）/ **欢愉**（群奶），**不耗蓝** |
| 血百合 0-3 | `JobApi_WhiteMage.BloodLily` | 满 3 颗放 **苦难之心** —— 但临近大伤害时留着 |

### 学者 —— 小仙女 + 炽天使

| 资源 | API | 用法 |
|---|---|---|
| 以太 0-3 | `JobApi_Scholar.Aetherflow` | ≥2 不补；大伤害要来时也不补（留着救急） |
| 妖精能量 0-100 | `JobApi_Scholar.FairyGauge` | ≥20 挂 **妖精契约** |
| 宠物 | `JobApi_Scholar.HasPet` | 不在场就召唤 |
| 炽天使 | `JobApi_Scholar.SeraphTimer` | >0 表示在场；在场时自动放**慰藉**（群盾，两次） |

**炽天使**：时间轴预报 3 秒内有大伤害、或者团队已经在掉血时才拉出来 ——
因为炽天使的价值就是那两个群盾。

### 占星 —— 卡牌 + 地星

| 资源 | API | 用法 |
|---|---|---|
| 手牌 | `JobApi_Astrologian.DrawnCards` | <2 抽卡；≥1 出卡 |
| 大阿卡纳 | `JobApi_Astrologian.DrawnCrownCard` | 手牌转小阿卡纳；领主/贵妇自动选目标 |

**卡牌按类型分配**：`CardType` 的成员名没出现在 AEAssist.NET 的公开元数据里，
所以走运行时 `ToString()` + 设置里的「近战卡关键词」名单判断：

- 是近战卡 → 给近战 DPS
- 否则 → 给远程 DPS
- 都没有 → 退给任意 DPS → 坦克

默认名单是 7.x 的近战三张：`Balance, Bole, Arrow`（也带中文名）。

**地星（EarthlyStar）**：放下去 10 秒后自动炸，所以是
`TimelineManager.未来有减伤(10.0)` —— 时间轴预报 10 秒后有伤害才放，
正好赶上。没有时间轴时不会乱扔。

### 贤者 —— 蛇胆 / 毒刺 / 均衡

| 资源 | API | 用法 |
|---|---|---|
| 蛇胆 0-3 | `JobApi_Sage.Addersgall` | **根素**只在 0 颗时补 |
| 毒刺 0-3 | `JobApi_Sage.Addersting` | 快溢出（≥2）或打 AOE 时喂给**箭毒** —— 临近大伤害时留着 |
| 均衡 | `JobApi_Sage.Eukrasia` | 开盾 / 上 DoT 前先确认 |

---

## 五、减伤与预铺

两条路，谁先满足算谁：

1. **cactbot 时间轴**（准，提前量足）—— 见下一节
2. **看 boss 读条**（兜底，任何副本都能用）

```csharp
TargetHelper.targetCastingIsBossAOE(IBattleChara, int timeLeft)
TargetHelper.targetCastingIsDeathSentenceWithTime(IBattleChara, int timeLeft)
```

| 职业 | 团队减伤 | 个人减伤 |
|---|---|---|
| 白魔 | 节制（80） | 神祝祷（66） |
| 学者 | 野战阵（50） | — |
| 占星 | 中立学派（90） | 亢奋（86） |
| 贤者 | 坚角清汁（50） | — |

`Res_SelfMitigation` 现在**四个职业都进队列了**（自己血少或要挨打时保命）。

---

## 六、时间轴（cactbot）

### 怎么用

1. 从 [cactbot 仓库](https://github.com/OverlayPlugin/cactbot/tree/main/ui/raidboss/data)
   拷副本的 `.txt`。哪些副本有，用
   [coverage 页面](https://overlayplugin.github.io/cactbot/util/coverage/coverage.html?lang=cn) 查。
2. 丢进 `HealerACR\Timelines\`（递归扫子目录）。
3. 重载 ACR，进本自动匹配 —— 靠文件头 `# ZoneId: 1195`，别删。

### 核心机制：时间偏移（这就是 jump 模拟）

cactbot 用 `jump` / `label` 表达多阶段。线性排的话，一旦走了另一条分支，
后面所有条目的时间会差几百秒。所以 Runner 维护一个偏移量：

```
文件时间 = 实际时间 + offset

发生 jump "L"（在文件时间 T，目标是 label 时间 L）时：
    offset += L - T
```

**用真实文件验证过**：valigarmanda 在 108.4s 跳到 label 508.4，
offset 变成 400；之后文件里 519.8 的 `"Skyruin (storm phase)"`
实际触发时间就是 `519.8 - 400 = 119.8s` ——
正好对上文件里那条 `119.8 "Skyruin (storm/ice phase?)"`。算法是对的。

### 分支怎么选

同一时刻有多条带不同技能 ID 的 jump（storm / ice 两条分支）时：

1. 运行时盯着 `BattleData.EnemyCastSpellHistory`，**看到哪个分支的判定技能出现就走哪条**
2. 只有一条 jump 且不需要判定的 → 直接跳
3. 等太久（8 秒）→ 退回第一条并写日志

用"见过哪些技能"而不是时间戳来判断，是为了不依赖 `StartCastTime` 的单位。

### 行类型

拿真实文件核过：cactbot 的 `StartsUsing` 大多是同步锚点（名字是 `--sync--`），
**真正记录伤害的是 `Ability` 行**（同一份文件 101 条 vs 9 条）。
所以两种都看，再靠 `MemApiSpell.IsBossAoe(技能ID)` 自动滤出需要减伤的那些 ——
**不需要手工维护名单**。认不准时用设置里的「时间轴额外技能Id」补。

### 时间轴驱动的更多东西

不只是减伤，`TimelineManager` 还对外提供了两个查询：

```csharp
TimelineManager.未来有减伤(10.0)   // 10 秒后附近有没有伤害
TimelineManager.距下次减伤()       // 还有几秒
```

用它们做的事：

| 用途 | 谁在用 |
|---|---|
| **地星提前 10 秒预铺** | `AST_EarthlyStar` |
| **拉炽天使** | `SCH_Seraph`（3 秒内有大伤害才拉） |
| **预铺群盾 / 单体盾** | `Res_GroupShield` / `Res_HealShield` |
| **攒资源** | `WHM_AfflatusMisery` / `SGE_Toxikon`（8 秒内有大伤害就先别输出） |
| **攥住群奶能力技** | `Res_HealAoEAbility`（5 秒内有大伤害且掉血不严重就留着） |
| **留以太 / 团辅** | `SCH_Aetherflow` / `SCH_ChainStratagem` / `AST_Divination` |

攒资源这一块可以在设置里用「时间轴攒资源」总开关关掉（默认开）。

### 顺手一提：官方也有转换器

```csharp
// namespace AEAssist.TriggerlineEditor
public class CactbotTimeline
{
    public static HighEndTriggerline Totimeline(string timeline);
}
```

走这条路要配合 `Rotation.AddTriggerlineUpgradeFromData(Action<TriggerLine>)`
操作 `TreeRoot` 内部结构。骨架没走，是因为它对日随太重。

---

## 七、起手（`Opener/预铺起手.cs`）

`Sequence` 留空（日随不需要固定循环），真正有用的是 `InitCountDown`：

- **开怪前 15 秒**：给主坦预铺单体盾
- **开怪前 3 秒**：坦克血量不满就补一口
- 爆发药留了注释位

---

## 八、阈值面板

```
通用 | 阈值
       紧急单奶 / 单体治疗 / 群体治疗 / 群奶最少人数
       醒梦蓝量 / 不挂 DoT 血线
       自动减伤 / 允许硬读复活 / 拉人喊话
       ── cactbot 时间轴 ──
       状态：valigarmanda.txt · 116 条（含分支）
       启用时间轴 / 提前秒数 / 额外技能Id
       [保存到 json]
```

「时间轴攒资源」也在这一页。

---

## 九、等级 1-100 怎么覆盖

`MinLevel = 1` / `MaxLevel = 100`，**不写任何 `if (level >= xx)`**：

1. **输出技能**：只写最初形态，运行时 `SpellUtil.当前形态()`（`CheckActionChange`）升级
2. **治疗技能**：`SpellUtil.取已解锁(CureII, Cure)` 从高到低挑
3. **每个 `Check()` 都有 `IsUnlock()`**，没学会就跳过
4. **职业资源自动降级**：1 级时 `Lily = 0`，百合 resolver 自然不触发

| 职业 | 1 级有什么 | 关键解锁 |
|---|---|---|
| 白魔 | Stone | 2 Cure / 4 Aero / 12 复活 / 30 CureII / 45 Holy / 50 天赐 / 52 安慰 / 56 神谕 / 74 苦难之心 / 76 欢愉 / 80 节制 |
| 学者 | Ruin | 2 Bio / 4 医术 / 30 鼓舞 / 50 野战阵 / 52 不屈 / 62 深谋 / 70 妖精契约 / 76 祝福 / 80 炽天使+慰藉 |
| 占星 | Malefic（**30 级转职**） | 15 先天禀赋 / 26 吉星II / 40 阳星相位 / 45 天星冲日 / 62 地星 / 70 小阿卡纳 / 90 中立学派 |
| 贤者 | Dosis（**70 级转职**） | 50 坚角 / 52 消化+灵橡 / 62 白牛 / 66 箭毒 / 70 海马 / 74 根素 |

> ⚠️ `MinLevel = 1` 覆盖的是白魔和学者；
> **占星 30 级、贤者 70 级才能转职**，实际可用区间是 30-100 / 70-100。

---

## 十、日随向的取舍

- **不留牌、不做死循环起手**：只做开怪倒计时预铺。
- **不硬读复活**。
- **阈值偏保守**：紧急 35% / 单奶 65% / 群奶 70%、2 人起。
- **排除"假死"**：死斗、超火流星、死而不僵、行尸走肉一律不治。
- **减伤不猜**：时间轴说了才铺，或者 boss 明确读条才铺。
- **自动减伤默认关**：日随里 T 的减伤够用，想开在「阈值」页勾上。

---

## 十一、依赖的 API（对着 AEAssist.NET 1.2.16 的公共元数据核过）

```csharp
// 入口
public interface IRotationEntry : IDisposable {
    string AuthorName { get; set; }
    Rotation Build(string settingFolder);
    IRotationUI GetRotationUI();
    void OnDrawSetting();
}
public class Rotation {
    public Rotation(List<SlotResolverData> slotResolvers);
    public Jobs TargetJob { get; set; } public AcrType AcrType { get; set; }
    public int MinLevel { get; set; }   public int MaxLevel { get; set; }
    public string Description { get; set; }
    public Rotation AddOpener(Func<uint, IOpener> opener);
    public Rotation SetRotationEventHandler(IRotationEventHandler handler);
}

// 决策
public interface ISlotResolver { int Check(); void Build(Slot slot); }
public class SlotResolverData { public SlotResolverData(ISlotResolver, SlotMode); }
public enum SlotMode { Always = 0, Gcd = 1, OffGcd = 2 }
public class Slot { public Slot Add(Spell spell); }

// 事件（OnBattleUpdate 是时间轴的推进源）
public interface IRotationEventHandler {
    Task OnPreCombat(); void OnResetBattle(); Task OnNoTarget();
    void OnSpellCastSuccess(Slot, Spell); void AfterSpell(Slot, Spell);
    void OnBattleUpdate(int currTimeInMs);
    void OnEnterRotation(); void OnExitRotation(); void OnTerritoryChanged();
}

// 技能 / 内存
public class Spell {
    public Spell(uint id, SpellTargetType);
    public Spell(uint id, IBattleChara);
    public Spell(uint id, Vector3 pos);      // ← 地星用这个
    public uint Id;
}
public class MemApiSpell {
    public uint CheckActionChange(uint spellId);
    public bool IsUnlock(uint actionId);
    public static bool IsBossAoe(uint spellId);   // ← 时间轴自动识别减伤
    public Vector3 MapCenter(Vector3 fallback);
}
public class MemApiZoneInfo { public uint GetCurrTerrId(); public bool IsHighEnd(); }
public static class Core { public static IPlayerCharacter Me { get; } public static T Resolve<T>(); }

// 战斗数据（时间轴 + 分支判定）
public class BattleData {
    public Queue<Slot> HighPrioritySlots_GCD, HighPrioritySlots_OffGCD;
    public Dictionary<(ulong, uint), EnemyCastSpellCondParams> EnemyCastSpellHistory;  // ← 分支判定
    public Slot NextSlot { get; set; }
    public long BattleStartTime { get; set; }
    public long CurrBattleTimeInMs { get; set; }
}
public class EnemyCastSpellCondParams { public uint SpellId; public string SpellName; public long StartCastTime; }

// 职业资源
JobApi_Sage        : AddersgallTimer / Addersgall / Addersting / Eukrasia
JobApi_Scholar     : Aetherflow / SeraphTimer / FairyGauge / HasPet / Dissipation
JobApi_WhiteMage   : Lily / BloodLily / LilyTimer
JobApi_Astrologian : DrawnCards / DrawnCrownCard / ActiveDraw

// 队伍 / 目标
public static class PartyHelper {
    public static List<IBattleChara> CastableAlliesWithin30, DeadAllies, CastableTanks,
                                     CastableDps, CastableMelees, CastableRangeds;
}
public static class TargetHelper {
    public static int GetNearbyEnemyCount(IBattleChara, int, int);
    public static bool targetCastingIsBossAOE(IBattleChara, int timeLeft);
    public static bool targetCastingIsDeathSentenceWithTime(IBattleChara, int timeLeft);
}
public static class GCDHelper { public static int GetGCDCooldown(); public static bool CanUseGCD(); public static bool CanUseOffGcd(); }

// UI
public class JobViewWindow : IRotationUI {
    public JobViewWindow(JobViewSave save, Action save2, string name);
    public void AddTab(string tabName, Action<JobViewWindow> draw);
    public void AddQt(string name, bool defaultValue);
    public bool GetQt(string qtName);
    public bool SetQt(string qtName, bool qtValue);
}
```

### 没法从元数据确认、留了单点替换位置的

| # | 位置 | 说明 |
|---|---|---|
| 1 | `AuraIds.cs` | buff id 走 `AurasDefine.XXX`，对不上就改这一个文件 |
| 2 | 四个 `XxxSpellTable` | `SpellsDefine.XXX` 技能名，用 SeeSpell / SeeBuff 核对 |
| 3 | `AurasDefine.Dia` / `Bio` / `Combust` / `EukrasianDosis` | 四个 DoT 的 buff 名最容易对不上 |
| 4 | `SpellsDefine.SummonEos` / `SummonSelene` | 学者召唤小仙女的技能名 |
| 5 | `CardType` 的运行时名字 | 决定近战/远程卡怎么分，名字对不上就改设置里的「近战卡关键词」 |
| 6 | `MemApiSpell.IsBossAoe` 的判定粒度 | 决定哪些时间轴条目算大伤害，不准就用「时间轴额外技能Id」补 |

---

## 十二、参考来源

- 你给的《AEAssist3.0 ACR开发》文档 —— `SlotResolver` 三种队列、`Slot`/`SlotSequence`、API 四分类。
- 你给的 [cactbot coverage](https://overlayplugin.github.io/cactbot/util/coverage/coverage.html?lang=cn) ——
  时间轴数据来源，格式见 [TimelineGuide.md](https://github.com/OverlayPlugin/cactbot/blob/main/docs/TimelineGuide.md)。
- `LittleNightmare/LittleNightmareACR`（AEAssist 3.0 写法）
- `Airexplosion/Zanhikari_Sage_AEAssist2.0`（日随贤者）
- `EFrostBlade/Frost_acr`
- `AEAssist.NET` NuGet 包导出的公共元数据（731 个类型）

---

## 十三、TODO

已完成：

- [x] 职业特色资源循环：白魔百合技、学者小仙女、占星抽卡、贤者蛇胆/毒刺
- [x] 读豆子数（`JobApi_Sage` / `JobApi_Scholar`），不再溢出
- [x] 减伤与预铺（读条判断 + cactbot 时间轴双路）
- [x] 群盾：贤者均衡预后、学者慰藉
- [x] 起手序列（倒计时预铺）
- [x] `Res_HealShield` 预铺盾
- [x] QT 面板阈值滑条
- [x] cactbot 时间轴支持
- [x] **时间轴模拟 jump**（时间偏移 + 分支判定）
- [x] **占星按 CardType 分配卡牌**（近战卡给近战）
- [x] **地星提前 10 秒预铺**
- [x] **学者炽天使 / 占星大阿卡纳**
- [x] **`Res_SelfMitigation` 进队列**
- [x] **时间轴驱动的更多行为**（提前抬血 / 预铺盾 / 攒资源）

还没做：

- [ ] cactbot 的 `window` 语义只用了粗略版本，没做完整的 sync 纠偏
- [ ] 时间轴目前只驱动"要不要交技能"，没有驱动"起身走位 / 站桩"
- [ ] 贤者更多蛇胆用法（坚角清汁 + 泛输血组合）
- [ ] 阈值面板做成分页 + 主题色跟随
