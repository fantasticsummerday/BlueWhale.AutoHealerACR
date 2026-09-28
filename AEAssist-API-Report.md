# AEAssist public C# API — findings for third-party ACR development

## 0. CRITICAL: the requested repo does not contain the requested API

**`Harmony-P/AEAssist` (`main`, HEAD `11337c61a486c729dcd3968d5c6ef991324cc554`, committed 2022-07-23T03:24:45Z, description "CombatRoutine for rb (ff14)", MIT, branches: `main` + `MNK_WIP`) is a 2022 RebornBuddy-era snapshot.** It contains 1092 blobs / **657 `.cs` files**; all 657 were downloaded and searched in full.

Content search over every `.cs` file in the repo (exact case-sensitive substring counts):

| Identifier | matches | Identifier | matches |
|---|---|---|---|
| `IRotationEntry` | **0** | `IRotationEventHandler` | **0** |
| `ISlotResolver` | **0** | `SlotSequence` / `ISlotSequence` | **0 / 0** |
| `class Slot` | **0** | `WaitServerAcq` | **0** |
| `PartyHelper` | **0** | `ItemHelper` | **0** |
| `CheckCurrJobPotion` | **0** | `AddSlot` | **0** |
| `MemApi` | **0** | `BattleCharacterExtend` / `CharacterExtend` | **0 / 0** |
| `BardSettingUI` | **0** | `IJobSetting` | **0** |
| `RotationAttribute` | 1 (only a log string, no such class) | `SpellTargetType` | present (see §6) |

**Conclusion: items 1–5, 8–11, 13–15 (as named) do not exist in `Harmony-P/AEAssist`.** The API described in the task is the **modern Dalamud AEAssist**, which is not open source; it is shipped as the NuGet package **`AEAssist.NET`**. I recovered its exact public surface (§7 below) by metadata-dumping `lib/net10.0/AEAssist.dll` (731 exported types).

Provenance of the modern API surface:

```xml
<!-- repo-relative: temp.csproj  @ aeassist-acr/ACR_template (branch master) -->
<PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>
<ItemGroup>
    <PackageReference Include="AEAssist.NET" Version="*" ExcludeAssets="runtime" />
</ItemGroup>
```

```xml
<!-- repo-relative: .nuget/NuGet.config  @ aeassist-acr/ACR_template -->
<configuration>
    <packageSources>
        <add key="aeassist" value="https://www.myget.org/F/aeassist/api/v3/index.json" />
    </packageSources>
</configuration>
```

`AEAssist.NET` versions on that feed: `1.2.12, 1.2.13, 1.2.14, 1.2.15, 1.2.16` (it is **not** on nuget.org — 404). The 1.2.16 package ships `AEAssist.dll` plus `Dalamud.dll`, `FFXIVClientStructs.dll`, `ECommons.dll`, `Lumina*.dll`, `ImGui/ImPlot/ImGuizmo` bindings, `CSCore.dll`.

Notation for §7: declarations are rendered in C# from IL metadata (CLI names `String`/`Void`/`Int32`/`Single`/`Boolean` → `string`/`void`/`int`/`float`/`bool`); member names, parameter names, return types, optionality/defaults and declaration order are exactly as in the assembly metadata. Members whose declaring base shows as `wKCkaOJMC9e6cW3xjE0` are obfuscated non-exported types.

---

## 1. `IRotationEntry`

**In `Harmony-P/AEAssist`: NOT FOUND** (0 matches). Its equivalent is `IRotation`:

```csharp
// file: AEAssist/Rotations/Core/IRotation.cs  (repo-relative)
using AEAssist.Define;
using System.Threading.Tasks;

namespace AEAssist.Rotations.Core
{
    public interface IRotation
    {
        /// <summary>
        ///     init after job switch
        /// </summary>
        void Init();

        Task<bool> PreCombatBuff();
        /// <summary>
        /// If there is no target to attack, but it is not Stopped.
        /// </summary>
        /// <returns></returns>
        Task<bool> NoTarget();
        SpellEntity GetBaseGCDSpell();
    }
}
```

**In `AEAssist.NET` 1.2.16 — `AEAssist.CombatRoutine.IRotationEntry`:**

```csharp
namespace AEAssist.CombatRoutine
{
    public interface IRotationEntry : IDisposable
    {
        string AuthorName { get; set; }
        Rotation Build(string settingFolder);
        IRotationUI GetRotationUI();
        void OnDrawSetting();
    }
}
```

## 2. Abstract base class for ACR entries

**In `Harmony-P/AEAssist`: NOT FOUND as an abstract class.** The only base is the concrete `DefaultRotation` used as a fallback (rotations implement `IRotation` directly):

```csharp
// file: AEAssist/Rotations/Core/DefaultRotation.cs
namespace AEAssist.Rotations.Core
{
    public class DefaultRotation : IRotation
    {
        public void Init() { }
        public Task<bool> PreCombatBuff() { return Task.FromResult(false); }
        public SpellEntity GetBaseGCDSpell() { return null; }
        public Task<bool> NoTarget() { return Task.FromResult(false); }
    }
}
```

An ACR/rotation in this repo is declared like this (no base class):

```csharp
// file: AEAssist/AI/Bard/BardRotation.cs
namespace AEAssist.AI.Bard
{
    [Job(ClassJobType.Bard)]
    public class BardRotation : IRotation
    {
        public void Init() { ... }
        public async Task<bool> PreCombatBuff() { ... }
        public Task<bool> NoTarget() { return Task.FromResult(false); }
        public SpellEntity GetBaseGCDSpell() { return BardSpellHelper.GetBaseGCD(); }
    }
}
```

**In `AEAssist.NET` 1.2.16: there is NO exported abstract `RotationEntry`/`BaseRotationEntry` class.** An ACR entry is a class implementing `IRotationEntry` (plus `IRotationEventHandler` etc.) whose `Build` returns a `Rotation` descriptor. The descriptor is `AEAssist.CombatRoutine.Rotation`:

```csharp
namespace AEAssist.CombatRoutine
{
    public class Rotation
    {
        public Rotation(List<SlotResolverData> slotResolvers);
        public Jobs TargetJob { get; set; }
        public AcrType AcrType { get; set; }
        public int MinLevel { get; set; }
        public int MaxLevel { get; set; }
        public string Description { get; set; }
        public IRotationEntry RotationEntry { get; set; }
        public Rotation AddOpener(Func<uint, IOpener> opener);
        public Rotation SetRotationEventHandler(IRotationEventHandler rotationEventHandler);
        public Rotation AddTargetResolver(ITargetResolver[] targetResolvers);
        public Rotation AddHotkeyEventHandlers(IHotkeyEventHandler[] hotkeyEventHandlers);
        public Rotation AddSlotSequences(ISlotSequence[] slotSequences);
        public Rotation AddTriggerCondition(ITriggerCond[] conds);
        public Rotation AddTriggerAction(ITriggerAction[] actions);
        public Rotation AddCanUseHighPrioritySlotCheck(Func<SlotMode, Slot, int> check);
        public Rotation AddTriggerlineUpgradeFromStr(Func<string, string> upgrade);
        public Rotation AddTriggerlineUpgradeFromData(Action<TriggerLine> upgrade);
        public Rotation AddCanPauseACRCheck(Func<int> canPauseCheck);
        public Rotation SetACRAutoUpdateTimeline(List<string> idList);
    }

    public enum AcrType { Both = 0, Normal = 1, HighEnd = 2, PVP = 3 }
}
```

Also present: `AEAssist.CombatRoutine.CombatRoutine2 : IAEPlugin, IDisposable` (host-side ACR plugin: `static AssemblyLoadContext LoadContext`, `OnLoad(AssemblyLoadContext loadContext)`, `CheckACRUpdate()`, `BuildPlugin()`, `Update()`, `Dispose()`, …).

## 3. SlotResolver interface

**In `Harmony-P/AEAssist`: NOT FOUND.** The equivalent is the priority-queue pair:

```csharp
// file: AEAssist/AI/AIMgrs.cs
namespace AEAssist.AI
{
    public interface IAIHandler
    {
        int Check(SpellEntity lastSpell);
        Task<SpellEntity> Run();
    }

    public interface IAIPriorityQueue
    {
        List<IAIHandler> GCDQueue { get; }
        List<IAIHandler> AbilityQueue { get; }
        Task<bool> UsePotion();
    }
}
```

```csharp
// file: AEAssist/AI/AISpellQueueMgr.cs
namespace AEAssist.AI
{
    public interface IAISpellQueue
    {
        List<IAISpellQueueSlot> SlotQueue { get; }
    }

    public interface IAISpellQueueSlot
    {
        int Check(int index);
        void Fill(SpellQueueSlot slot);
    }
}
```

**In `AEAssist.NET` 1.2.16 — `AEAssist.CombatRoutine.Module.ISlotResolver`:**

```csharp
namespace AEAssist.CombatRoutine.Module
{
    public interface ISlotResolver
    {
        int Check();
        void Build(Slot slot);
    }

    public class SlotResolverData
    {
        public SlotResolverData(ISlotResolver slotResolver, SlotMode mode);
        public ISlotResolver SlotResolver { get; set; }
        public SlotMode SlotMode { get; set; }
    }

    public enum SlotMode { Always = 0, Gcd = 1, OffGcd = 2 }
}
```

## 4. `Slot` class

**In `Harmony-P/AEAssist`: NOT FOUND** (`class Slot` = 0). The equivalent container is `SpellQueueSlot` (§5).

**In `AEAssist.NET` 1.2.16 — `AEAssist.CombatRoutine.Module.Slot`:**

```csharp
namespace AEAssist.CombatRoutine.Module
{
    public class Slot
    {
        public Slot(int maxDura = 600);
        public Slot(Spell spell, int maxDura);

        public List<SlotAction> Actions;
        public long maxDuration;
        public bool Wait2NextGcd;

        public Slot Insert(SlotAction slotAction, int index = 0);
        public Slot Add(SlotAction slotAction);
        public Slot Add(Spell spell);
        public Slot Add2NdWindowAbility(Spell spell);
        public Slot AddDelaySpell(int delay, Spell spell);
        public void AppendSequence(ISlotSequence slotSequence, bool wait2nextGcd = true);
        public override string ToString();
        public string ToStringSameline();
    }

    public class SlotAction
    {
        public SlotAction(WaitType wait, int timeInMs, Spell spell);
        public SlotAction(Spell spell);
        public int MaxDuration;
        public Spell Spell;
        public int TimeInMs;
        public WaitType Wait;
        public Task<bool> Run(Slot slot);
        public override string ToString();
    }

    public enum SlotAction.WaitType { None = 0, WaitInMs = 1, WaitForSndHalfWindow = 2 }
}
```

Old-repo equivalent, for reference (this is the closest thing to `Slot` + `SlotSequence` there):

```csharp
// file: AEAssist/AI/SpellQueueData.cs
public class SpellQueueSlot : Entity
{
    // spellId == 0, mean wait {AnimationLockMs}
    public Queue<(uint spellId, SpellTargetType SpellTargetType)> Abilitys =
        new Queue<(uint spellId, SpellTargetType SpellTargetType)>();
    public int AnimationLockMs = 0;
    private uint GCDSpellId;
    public SpellTargetType SpellTargetType;
    public BattleCharacter BattleCharacter;
    public bool UsePotion;
    public int Index;
    public bool GCDQueueMode;
    public Queue<(uint spellId, SpellTargetType targetType)> GCDQueues;
    public int GCDQueueIndex;
    public Func<int> BreakCond;

    public void SetGCD(uint spellId, SpellTargetType targetType);
    public void SetGCD(uint spellId, BattleCharacter target);
    public void SetGCDQueue(params (uint spellId, SpellTargetType targetType)[] queues);
    public void SetGCDQueueFromList(List<(uint spellId, SpellTargetType targetType)> queues);
    public void EnqueueGCD((uint spellId, SpellTargetType targetType) value);
    public void SetBreakCondition(Func<int> Check);
    public void ClearGCD();
    public void EnqueueAbility((uint spellId, SpellTargetType spellTargetType) va);
    public uint GetGCDSpell();
    protected override void OnDestroy();
}
```

## 5. `SlotSequence`

**In `Harmony-P/AEAssist`: NOT FOUND** (`SlotSequence`/`ISlotSequence` = 0/0). Equivalent queue container:

```csharp
// file: AEAssist/AI/SpellQueueData.cs
public class SpellQueueData : IBattleData
{
    public Queue<SpellQueueSlot> Queue = new Queue<SpellQueueSlot>();
    public void Add(SpellQueueSlot slot);
    public void Clear();
    public async Task<bool> ApplySlot();
}
```

**In `AEAssist.NET` 1.2.16: no `class SlotSequence` exists; the type is `ISlotSequence`:**

```csharp
namespace AEAssist.CombatRoutine.Module
{
    public interface ISlotSequence
    {
        List<Action<Slot>> Sequence { get; }
        int StartCheck();
        int StopCheck(int index);
    }
}
```

(Consumed by `Slot.AppendSequence(ISlotSequence slotSequence, bool wait2nextGcd = true)` and `Rotation.AddSlotSequences(ISlotSequence[] slotSequences)`; `AEAssist.CombatRoutine.Module.Opener.IOpener : ISlotSequence, IScript`.)

## 6. `Spell` and `SpellTargetType`

**In `Harmony-P/AEAssist`: `class Spell` = 0, but a `SpellTargetType` enum does exist** (file `AEAssist/Define/SpellEntity.cs`). The `Spell` analogue is `SpellEntity`:

```csharp
// file: AEAssist/Define/SpellEntity.cs
namespace AEAssist.Define
{
    public enum SpellTargetType
    {
        Self = -1,
        CurrTarget = 0,
        PM1 = 1,
        PM2,
        PM3,
        PM4,
        PM5,
        PM6,
        PM7,
        PM8,
        SpecifyTarget = 9,
        Location = 10,
    }

    public class SpellEntity : Entity
    {
        public static SpellEntity Default = new SpellEntity();
        public SpellData SpellData;
        public SpellTargetType SpellTargetType;

        public SpellEntity();
        public SpellEntity(uint id);
        public SpellEntity(uint id, SpellTargetType targetIndex) : this(id);
        public SpellEntity(uint id, BattleCharacter target) : this(id);

        public uint Id => SpellData?.Id ?? 0;
        public TimeSpan Cooldown => SpellData.Cooldown;
        public TimeSpan AdjustedCooldown => SpellData.AdjustedCooldown;

        public static SpellEntity Create();
        public static SpellEntity Create(uint spellId);
        protected override void OnDestroy();
        public async Task<bool> DoAction();
        public BattleCharacter GetTarget();
        public async Task<bool> DoGCD();
        public async Task<bool> DoAbility();
        public async Task<bool> DoAbilityAndWait(int retrytime = 1000);
        public bool RecentlyUsed(int span = 1000);
        public int CanCastGCD();
        public bool CanCastAbility();
        public bool IsHighPriority();
    }
}
```

**In `AEAssist.NET` 1.2.16 — `AEAssist.CombatRoutine.Spell`** (note `WaitServerAcq` **is** present):

```csharp
namespace AEAssist.CombatRoutine
{
    public class Spell
    {
        public Spell(uint id, SpellTargetType targetIndex);
        public Spell(uint id, IBattleChara target);
        public Spell(uint id, Func<IBattleChara> getSpecifyTargetFunc);
        public Spell(uint id, Vector3 pos);
        public Spell(uint itemId, bool isHq);

        public static Spell Idle;
        public SpellTargetType SpellTargetType;
        public SpellCategory SpellCategory;
        public uint Id;
        public bool Hq;
        public Vector3 Pos;
        public bool UsePos;
        public bool WaitServerAcq;
        public bool DontUseGcdOpt;

        public IBattleChara SpecifyTarget { get; set; }
        public Func<IBattleChara> GetDynamicsTarget { get; set; }
        public TimeSpan RecastTime { get; }
        public float RecastTimeElapsed { get; }
        public TimeSpan Cooldown { get; }
        public string Name { get; }
        public string LocalizedName { get; }
        public float Charges { get; }
        public int MaxCharges { get; }
        public TimeSpan CastTime { get; }
        public TimeSpan AdjustedCastTime { get; }
        public ulong MPNeed { get; }
        public float ActionRange { get; }

        public Spell DontUseGcd();
        public static Spell Create(SpellCategory category, uint spellId, SpellTargetType spellTargetType);
        public static Spell CreateLimitBreak(IBattleChara target);
        public static Spell CreatePotion();
        public static Spell CreateSprint();
        public static Spell CreateDance();
    }

    public enum SpellCategory { Default = 0, LimitBreak = 1, Potion = 2, Sprint = 3, Dance = 4, Item = 5 }

    public enum SpellTargetType
    {
        Target = 0,
        Self = 1,
        TargetTarget = 2,
        Pm1 = 3, Pm2 = 4, Pm3 = 5, Pm4 = 6,
        Pm5 = 7, Pm6 = 8, Pm7 = 9, Pm8 = 10,
        SpecifyTarget = 100,
        Location = 101,
        DynamicTarget = 102,
        MapCenter = 103,
    }

    public enum SpellType { None = 0, RealGcd = 1, GeneralGcd = 2, Ability = 3 }
    public enum SpellActionType { Request = 0, Effect = 1, CancelCast = 2, ActionRejected = 3 }
}
```

`AEAssist.CombatRoutine.SpellsDefine` is a static class of `static uint` spell-id fields (`Sprint`, `SecondWind`, `Bloodbath`, `TrueNorth`, `ArmsLength`, `Feint`, `HeadGraze`, `FootGraze`, `LegGraze`, `Peloton`, `LegSweep`, `Potion`, `Surecast`, `Addle`, `Swiftcast`, `LucidDreaming`, `Esuna`, `Rescue`, `Repose`, `Rampart`, `Provoke`, `Reprisal`, `Shirk`, `Interject`, `LowBlow`, `Mug`, `Ten`, `Chi`, `Jin`, `TenCombo`, `ChiCombo`, `JinCombo`, …) plus `GetTankStateOpenId/GetTankStateAuraId/GetTankStateCloseId(Jobs)`.

## 7. Settings base class / interface for ACRs

**In `Harmony-P/AEAssist` — the base is the interface `IBaseSetting`** (file `AEAssist/Setting/BaseSetting.cs`, namespace `AEAssist`):

```csharp
namespace AEAssist
{
    public interface IBaseSetting
    {
        void Reset();
        void OnLoad();
    }
}
```

```csharp
// file: AEAssist/Setting/SettingAttribute.cs
namespace AEAssist
{
    public class SettingAttribute : Attribute { }
}
```

`BardSettings` (the class named in the doc) implements `IBaseSetting`; `BardSettingUI` does **not** exist (0 matches) — the WPF view is `AEAssist.View.BardSettingView`:

```csharp
// file: AEAssist/Setting/Setting/BardSettings.cs
using AEAssist.AI.Bard;
using PropertyChanged;

namespace AEAssist
{
    [AddINotifyPropertyChangedInterface]
    public class BardSettings : IBaseSetting
    {
        public BardSettings() { Reset(); }
        public double RestHealthPercent { get; set; }
        public int ApexArrowValue { get; set; }
        public SongStrategyEnum CurrentSongPlaylist { get; set; }
        public int Songs_WM_TimeLeftForSwitch { get; set; }
        public int Songs_MB_TimeLeftForSwitch { get; set; }
        public int Songs_AP_TimeLeftForSwitch { get; set; }
        public bool UsePeloton { get; set; }
        public bool BuffsDelay2GCD { get; set; }
        public int TTK_IronJaws { get; set; }
        public int Dot_TimeLeft { get; set; } = 2500;
        public bool EarlyEmpyrealArrow { get; set; } = true;
        public bool EarlyDecisionMode { get; set; }
        public bool ApexWaitBuffs { get; set; }
        public void Reset() { ... }
        public void OnLoad() { }
    }
}
```

```csharp
// file: AEAssist/View/BardSettingView.xaml.cs
namespace AEAssist.View
{
    public partial class BardSettingView : UserControl
    {
        public BardSettingView() { InitializeComponent(); }
    }
}
```

What a custom settings class must override/implement: **`Reset()` and `OnLoad()`** — both are required by `IBaseSetting` (no default implementation). Loading/saving is automatic:

```csharp
// file: AEAssist/Setting/SettingMgr.cs  (excerpt — exact signatures)
public const string SettingPath = @"Settings\AEAssists";
public static SettingMgr Instance = new SettingMgr();
public void AutoSave();
public void InitSetting();
public void Reset();
public static T GetSetting<T>() where T : class, IBaseSetting, new();
public void Save();
```

Settings are discovered by `GetType().Assembly.GetTypes()` filtered on `IBaseSetting`, serialized with Newtonsoft.Json to `Settings/AEAssists/<TypeName>.json`, and instantiated with `Activator.CreateInstance` (hence the `new()` constraint).

**In `AEAssist.NET` 1.2.16:** there is **no public settings base class or interface to derive from** (NOT FOUND). Settings are plain classes retrieved by:

```csharp
namespace AEAssist.CombatRoutine
{
    public class SettingMgr
    {
        public static string SettingPath;
        public static SettingMgr Instance;
        public static T GetSetting<T>();
        public void Save();
    }
}
```

The base class that AEAssist's own settings use is an **obfuscated, non-exported** type (`wKCkaOJMC9e6cW3xjE0`), e.g. `class GeneralSettings : wKCkaOJMC9e6cW3xjE0`, `class RotationSetting : wKCkaOJMC9e6cW3xjE0` — it cannot be referenced by name from a third-party ACR. Settings **UIs** implement:

```csharp
namespace AEAssist.CombatRoutine.View
{
    public interface ISettingUI
    {
        string Name { get; }
        void Draw();
    }

    public interface ISettingUIWithIndex : ISettingUI
    {
        int Index { get; }
    }
}
```

## 8. `IRotationEventHandler`

**In `Harmony-P/AEAssist`: NOT FOUND** (0 matches). Equivalent interfaces: `ISpellEvent` / `SpellEventAttribute` (`AEAssist/AI/SpellEvent.cs`), `ITriggerActionHandler`, `ITriggerCondHandler` (`AEAssist/TriggerSystem/*`).

**In `AEAssist.NET` 1.2.16 — `AEAssist.CombatRoutine.IRotationEventHandler`** (note `BeforeSpell` is *not* abstract — it has a default implementation, and `OnSpellCastSuccess(Slot, Spell)` takes the §4/§6 types):

```csharp
namespace AEAssist.CombatRoutine
{
    public interface IRotationEventHandler
    {
        Task OnPreCombat();
        void OnResetBattle();
        Task OnNoTarget();
        void OnSpellCastSuccess(Slot slot, Spell spell);
        void BeforeSpell(Slot slot, Spell spell);   // non-abstract (default implementation)
        void AfterSpell(Slot slot, Spell spell);
        void OnBattleUpdate(int currTimeInMs);
        void OnEnterRotation();
        void OnExitRotation();
        void OnTerritoryChanged();
    }
}
```

Sibling interfaces: `IRotationUI { void Update(); bool IsCustomMain(); void OnDrawUI(); }`, `ITargetResolver { bool ResolveTarget(ref IBattleChara agent); }`, `ITriggerHandler { string ListeningActionName { get; } void Handle(string args); }`, `IHotkeyEventHandler { void Run(HotkeyConfig config); }`.

## 9. How an ACR registers with the framework

**In `Harmony-P/AEAssist`:** entry point is `AEAssist.Entry` (a plain class, not an attribute-based plugin), loaded by the RebornBuddy `CombatRoutine` shim.

```csharp
// file: AEAssist/Rotations/JobAttribute.cs   <- the "registration attribute"; note the class name is JobAttribute, not RotationAttribute
using ff14bot.Enums;
using System;

namespace AEAssist
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class JobAttribute : Attribute
    {
        public ClassJobType ClassJobType;
        public JobAttribute(ClassJobType classJobType) { ClassJobType = classJobType; }
    }
}
```

```csharp
// file: AEAssist/Rotations/Core/RotationManager.cs  (excerpt — the discovery loop, verbatim)
public void Init()
{
    AllRotations.Clear();
    var baseType = typeof(IRotation);
    foreach (var type in GetType().Assembly.GetTypes())
    {
        if (type.IsAbstract || type.IsInterface)
            continue;
        if (!baseType.IsAssignableFrom(type))
            continue;
        if (type == typeof(DefaultRotation))
            continue;
        var attrs = type.GetCustomAttributes(typeof(JobAttribute), false);
        if (attrs.Length == 0)
        {
            LogHelper.Error($"Rotation class [{type}] need RotationAttribute");
            continue;
        }

        var attr = attrs[0] as JobAttribute;
        AllRotations[attr.ClassJobType] = Activator.CreateInstance(type) as IRotation;
        LogHelper.Debug("Load Rotation: " + attr.ClassJobType);
    }
}
```

**This scans only `GetType().Assembly` (the AEAssist assembly itself) — there is no external/third-party ACR DLL discovery in this repo.** The loader only loads `Routines\AEAssist\AEAssist.dll`:

```csharp
// file: AEAssistLoader/AEAssistLoader.cs  (excerpt — verbatim)
public class AEAssistLoader : CombatRoutine
{
    private const string ProjectName = "AEAssist";
    private const string ProjectAssemblyName = "AEAssist.dll";
    private static readonly string ProjectAssembly = Path.Combine(Environment.CurrentDirectory,
        $@"Routines\{ProjectName}\{ProjectAssemblyName}");
    public static readonly HashSet<string> ExternelDlls = new HashSet<string> { "MongoDB.Bson" };
    // ...
    private void LoadAsm()
    {
        RedirectAssembly();
        var path = ProjectAssembly;
        var asm = LoadAssembly(path);
        var entryType = asm.GetType("AEAssist.Entry");
        Entry = Activator.CreateInstance(entryType);
        // wires "RestBehavior"/"PreCombatBuffBehavior"/"PullBehavior"/"HealBehavior"/
        // "CombatBuffBehavior"/"CombatBehavior"/"PullBuffBehavior" properties and the
        // "Initialize"/"Pulse"/"Shutdown"/"OnButtonPress" methods by reflection
    }
}
```

Its public behaviour members: `Name`, `PullRange`, `WantButton`, `Class`, `RestBehavior`, `PreCombatBuffBehavior`, `PullBehavior`, `HealBehavior`, `CombatBuffBehavior`, `CombatBehavior`, `PullBuffBehavior`, `Initialize()`, `OnButtonPress()`, `Pulse()`, `ShutDown()`, `static RedirectAssembly()`.

**In `AEAssist.NET` 1.2.16 (the real third-party ACR mechanism): there is NO `[Rotation]` / `RotationAttribute` class** — the only exported attribute types are `AutofacAttribute`, `HotkeyEventAttribute`, `IgnoreTriggerlineCheckAttribute`, `GameFunctionAttribute`, `LabelNameAttribute`, `NotDisplayAttribute`. Registration is by **contract + folder convention**:

- A third-party ACR is a DLL whose entry class implements `IRotationEntry` and returns a fully configured `Rotation` from `Build(string settingFolder)` (see §2 for the fluent `Rotation.Add*` registration API: openers, event handler, target resolvers, hotkey handlers, slot sequences, trigger conds/actions, high-priority-slot check, ACR auto-update timelines).
- Classes are found by type-shape, not by attribute: `AEAssist.EventSystem` exposes `void Register(Assembly assembly)`, `HashSet<Type> GetTypesByAttr(Assembly assembly, Type attrType)`, `HashSet<Type> GetTypesByBaseType(Assembly assembly, Type baseType)`.
- ACRs live in folders and are loaded into their own `AssemblyLoadContext`:

```csharp
namespace AEAssist.CombatRoutine
{
    public class RotationManager
    {
        public static RotationManager Instance;
        public Dictionary<Jobs, List<Rotation>> Job2Rotations;
        public static Dictionary<Jobs, Jobs> JobsMap;
        public static bool Reloading;
        public Dictionary<string, List<Rotation>> Folder2Rotations { get; set; }   // <- one key per ACR folder
        public void Init();
        public void Update();
        public void Dispose();
    }

    public class ExAssemblyLoader : AssemblyLoadContext
    {
        public ExAssemblyLoader(AssemblyLoadContext parentLoadContext, string name);
        public void AddAssemblyDependencyResolver(string dllFullName);
        public Assembly LoadAssembly(string dllFullName);
    }

    public class CombatRoutine2 : IAEPlugin, IDisposable
    {
        public static AssemblyLoadContext LoadContext;
        public void OnLoad(AssemblyLoadContext loadContext);
        public void CheckACRUpdate();
        public PluginSetting BuildPlugin();
        // + OnEvent/OnPluginUI/AfterVerified/OnExternalUI/Update/Dispose/DrawOverlay/DrawSetting/...
    }
}
```

- Distribution/packaging metadata is `upload_config.yaml` in the ACR repo (fields below) and maps onto `AEAssist.CloudACR.ACR`:

```yaml
# repo-relative: upload_config.yaml  @ aeassist-acr/ACR_template (branch master)
Remark: 这是一个ACR介绍
SupportJobs:
  - 白魔法师
  - 黑魔法师
SupportUrl: 改成一个HTTP://或者HTTPS://开头的链接
ReportUrl: 改成一个HTTP://或者HTTPS://开头的链接
EnVersion: false
```

```csharp
namespace AEAssist.CloudACR
{
    public class ACR
    {
        public string Author { get; set; }
        public string KeyMd5 { get; set; }
        public string Remark { get; set; }
        public List<uint> SupportJobs { get; set; }
        public byte[] OriginData { get; set; }
        public long LastUpdateTime { get; set; }
        public string SupportUrl { get; set; }
        public string ReportUrl { get; set; }
        public bool EnVersion { get; set; }
    }

    public static class CloudACRHelper
    {
        public static string URL_AddOrUpdate;
        public static string URL_Download;
        public static string URL_ListACR;
        public static string BaseUrl { get; }
        public static Task<string> AddOrUpdate(ACR acr);
        public static Task<ACRLinkData> Download(string author);
        public static Task<List<ACR>> ListACR();
        public static void Delete(string acrName, Action callback);
    }
}
```

The ACR template repo (`aeassist-acr/ACR_template`) contains only `temp.csproj`, `.nuget/NuGet.config`, `upload_config.yaml`, `.gitignore` and two GitHub workflows (`auto-merge-pr.yml`, `release-on-merge.yml` delegating to `aeassist-acr/workflows_center`).

## 10. `AI` class (`AI.Instance`), BattleData, TriggerlineData, GCD control

**In `Harmony-P/AEAssist`:** the AI root is `AIRoot` (`AEAssist/AI/AIRoot.cs`), with `public static readonly AIRoot Instance = new AIRoot();`:

```csharp
// file: AEAssist/AI/AIRoot.cs  (public members only)
namespace AEAssist.AI
{
    public interface IBattleData { }

    public class AIRoot
    {
        public static readonly AIRoot Instance;
        public bool Stop { get; set; }          // proxies AEAssist.DataBinding.Instance.Stop
        public bool Move { get; set; }          // proxies DataBinding.Instance.Move
        public bool CloseBurst { get; set; }    // proxies !DataBinding.Instance.Burst
        public bool IsRunning => Core.Me.InCombat;
        public void Init();
        public static T GetBattleData<T>() where T : class, IBattleData;
        public void Clear();
        public void ForceClear();
        public async Task<bool> Update();
        public void RecordGCD(SpellEntity ret);
        public void RecordAbility(SpellEntity ret);
        public bool Is2ndAbilityTime(float time = 0.5f);   // single/double-weave window control
        public double GetGCDDuration();
        public bool CanUseGCD();
        public void MuteAbilityTime();
    }
}
```

Battle data (`AEAssist/AI/BattleData.cs`) — this is where force-use-spell / next-slot / triggerline data live:

```csharp
// file: AEAssist/AI/BattleData.cs  (public members only)
namespace AEAssist.AI
{
    public class BattleData : IBattleData
    {
        public int OpenerIndex;
        public BattleData();
        public long BattleStartTime { get; set; }
        public long CurrBattleTimeInMs { get; private set; }
        public void Update(long currTime);
        // BaseSpellControl
        public SpellEntity lastAbilitySpell;
        public long lastCastTime;
        public int lastGCDIndex;
        public SpellEntity lastGCDSpell;
        public int maxAbilityTimes;
        public bool LimitAbility;
        public HashSet<uint> LockSpellId;
        public void ResetMaxAbilityTimes();
        // NextSpell
        public SpellEntity NextAbilitySpellId { get; set; }
        public bool NextAbilityUsePotion;
        public SpellEntity NextGcdSpellId { get; set; }
        public SpellQueueSlot NextSpellSlot { get; set; }
        public IAISpellQueue CurrApply { get; set; }
        public int ApplyIndex;
        public long GCDRetryEndTime;
        public long AbilityRetryEndTime;
        public long SlotRetryEndTime;
        // TriggerLine
        public void SetExecuted(string triggerId);
        public long GetExecutedTriggersTime(string id);
        public long GetExecutedTriggersTime_And(List<string> ids);
        public long GetExecutedTriggersTime_Or(List<string> ids);
        public void RecordCondHitTime(ITriggerCond cond);
        public bool GetCondHitTime(ITriggerCond cond, out long time);
    }
}
```

Triggerline access in this repo is via `AEAssist.DataBinding.Instance.CurrTriggerLine` / `TriggerLineName` / `ChangeTriggerLine(TriggerLine line)` (file `AEAssist/DataBinding/DataBinding.cs`), `TriggerSystemMgr.Instance.HandleTriggers(v)`, and `TriggerLineSwitchHelper` (`AEAssist/Helper/TriggerLineSwitchHelper.cs`).

**In `AEAssist.NET` 1.2.16 — `AEAssist.CombatRoutine.Module.AI` (this is the `AI.Instance` the task means):**

```csharp
namespace AEAssist.CombatRoutine.Module
{
    public class AI
    {
        public AI();
        public static AI Instance;
        public bool LockPos;
        public bool Remotecontrol;
        public string PartyRole;
        public List<string> PartyRoleList;
        public List<string> AllianceRoleList;
        public List<string> AlliancePartyPrefixes;

        public BattleData BattleData { get; set; }
        public TriggerlineData TriggerlineData { get; set; }
        public TriggerlineAssistData TriggerlineAssistData { get; set; }
        public List<SlotResolverData> CustomSlotResolver { get; set; }

        public List<string> GetPartyRolesForAlliance(string prefix);
        public string GetAlliancePrefix(string role);
        public bool IsAllianceRole(string role);
        public void CheckAutoInterrupt(IBattleChara battleCharacter);
        public int ExposeVarsGetValueOrDefault(string v);
        public void ExposeVarsSet(string v, int oldValue);
        public void Update();
        public void Clear();
        public void RegisterCustomSlotResolver(SlotResolverData slotResolver);
        public void UnRegisterCustomSlotResolver(SlotResolverData slotResolver);
    }
}
```

Slot queueing / force-use-spell / highest-priority slots are on `BattleData` (there is **no `AI.Instance.AddSlot`/`AddNextSlot`**; 0 matches for `AddSlot`):

```csharp
namespace AEAssist.CombatRoutine.Module
{
    public class BattleData
    {
        public Dictionary<int, long> TargetIconTime;
        public Dictionary<int, List<(ulong, long)>> TargetIcon2ObjectIdAndTime;
        public Dictionary<(ulong, uint), EnemyCastSpellCondParams> EnemyCastSpellHistory;
        public Dictionary<ulong, long> TetherObjectId2Time;
        public Dictionary<uint, long> ReceiveAbilityTime;
        public Dictionary<uint, List<(ulong, long)>> ReceiveAbility2ObjectIdAndTime;
        public HashSet<uint> LockSpells;
        public Queue<Slot> HighPrioritySlots_GCD;      // <- highest-priority slot queues
        public Queue<Slot> HighPrioritySlots_OffGCD;
        public int CurrGcdAbilityCount { get; set; }
        public uint LockedBossId { get; set; }
        public Slot NextSlot { get; set; }             // <- the "next slot"
        public long BattleStartTime { get; set; }
        public long CurrBattleTimeInMs { get; set; }
        public string CurrBattleTimeInSec { get; }
        public long FirstDeadTime { get; set; }

        public void AddCastSpellHistory(ulong objectId, uint spellId, EnemyCastSpellCondParams condParams);
        public void AddTargetIcon(int targetIcon, ulong objectId);
        public List<ulong> GetTargetIconObjects(int targetIcon, int recentTimeInMs);
        public void AddTether(IGameObject entity, IGameObject tar);
        public bool IsTarTether(ulong objectId, int recentTimeInMs);
        public void AddReceiveAbility(uint ReceiveAbility, ulong objectId);
        public List<ulong> GetReceiveAbilityObjects(uint ReceiveAbility, int recentTimeInMs);
        public IBattleChara GetCurrLockedTarget();
        public void AddSpell2NextSlot(SlotAction action);   // <- force a spell into the next slot
        public void AddSpell2NextSlot(Spell spell);
        public bool PopSequenceStack();
        public void PopNextSlot();
        public void SetCurrSlot(Slot slot);
        public bool PopCurrSlot();
        public void Update(long currTime);
    }
}
```

GCD / weave control and casting:

```csharp
namespace AEAssist.Helper
{
    public static class GCDHelper
    {
        public static bool Is2ndAbilityTime(float timeInMs = 1000);   // single-weave / 2nd-ability window
        public static int GetGCDDuration();
        public static int GetElapsedGCD();
        public static int GetGCDCooldown();
        public static bool CanUseGCD();
        public static bool CanUseOffGcd();
    }

    public static class SpellHelper
    {
        public static void PrintSpell();
        public static bool IsPlayerOccupied();
        public static IBattleChara GetTarget(SpellTargetType spellTargetType);
        public static IBattleChara GetTarget(Spell spell);
        public static Task<bool> Cast(Spell spell);
        public static Task<bool> CastInSeq(Spell spell);
        public static Task<bool> Cast(Spell spell, Slot slot);
        public static Spell GetSpell(uint id);
        public static Spell GetSpell(uint id, SpellTargetType targetType);
        // ...
    }

    public static class SpellExtension
    {
        public static bool IsLevelEnough(uint spellId);
        public static bool IsAbility(Spell spell);
        public static bool IsAbility(uint spellId);
        public static bool IsUnlock(uint spellId);
        public static bool IsUnlock(Spell spell);
        public static bool IsUnlockWithCDCheck(uint spellId);
        public static bool IsReadyWithCanCast(Spell spell);
        public static bool IsMaxChargeReady(uint spellId, float delta = 0.5f);
        public static bool CoolDownInGCDs(uint spellId, int count);
        public static bool AbilityCoolDownInNextXgcDsWindow(Spell spellData, int count);
        public static bool AbilityCoolDownInNextXgcDsWindow(uint spellId, int count);
        public static int AbilityComesInNextXgcDsWindow(Spell spellData);
        public static int AbilityComesInNextXgcDsWindow(uint spellId);
        public static bool RecentlyUsed(uint spellId, int time = 1200);
        public static bool IsDeathSentence(uint spellId);
        public static Task WaitFor2NdAbilityWindow(float timeScale = 0.65f);
    }
}
```

Slot execution helpers: `AEAssist.CombatRoutine.Module.AILoop.PVE_RunSlotHelper` (`CalSlot`, `CheckNextSlot`, `CheckNext`, `Run`, `HandleSlot`, `HandleSlotSequence`) and `PVP_RunSlotHelper.CalSlot`; loop strategy interface `IAILoop { bool Check(); void Update(AI battleMgr); void Clear(AI battleMgr); }` with `AILoop_Normal`, `AILoop_PVP`, `AILoop_Simulate`.

`TriggerlineData` (namespace `AEAssist.CombatRoutine.Module`, `IDisposable, ITriggerRuntime`) — key public members: `Dictionary<string,int> Variable`, `RunningState TriggerlineState`, `TriggerLine CurrTriggerLine { get; set; }`, `TriggerlineAssistData CurrentAssistData { get; }`, `bool Start { get; set; }`, `ScriptEnv ScriptEnv { get; }`, `Task SetTriggerline(TriggerLine triggerLine)`, `void StartTriggerline()`, `void CalTriggerLine()`, `void Clear(string src = "")`, `bool CanRunning()`, `Task<bool> WaitCond(TreeActionBase treeNode)`, plus ~25 `CreateXxxParams(...)` factory methods (`CreateEnemyCastSpellParams`, `CreateAddStatusParams`, `CreateTetherParams`, `CreateAfterSpellParams`, …). Companion: `TriggerlineAssistData : ITriggerRuntime` (`Task LoadTriggerAssist(string name = "")`, `void RunTriggerlineAssist()`, `ClearWait()`, `HitCond(string)`, …). Enum `TriggerlineData.RunningState { Idle = 0, Running = 1, Over = 2 }`.

## 11. `PartyHelper`

**In `Harmony-P/AEAssist`: NOT FOUND** (0 matches). The equivalent is `GroupHelper`:

```csharp
// file: AEAssist/Helper/GroupHelper.cs
namespace AEAssist.Helper
{
    public static class GroupHelper
    {
        public static readonly List<Character> CastableParty = new List<Character>();
        public static readonly List<Character> DeadAllies = new List<Character>();
        public static readonly List<Character> CastableTanks = new List<Character>();
        public static readonly List<Character> CastableHealers = new List<Character>();
        public static readonly List<Character> CastableDps = new List<Character>();
        public static readonly List<Character> CastableAlliesWithin30 = new List<Character>();
        public static readonly List<Character> CastableAlliesWithin25 = new List<Character>();
        public static readonly List<Character> CastableAlliesWithin20 = new List<Character>();
        public static readonly List<Character> CastableAlliesWithin15 = new List<Character>();
        public static readonly List<Character> CastableAlliesWithin12 = new List<Character>();
        public static readonly List<Character> CastableAlliesWithin10 = new List<Character>();

        public static bool InParty => PartyManager.IsInParty;
        public static bool PartyInCombat => Core.Me.InCombat;
        public static bool InActiveDuty => DutyManager.InInstance && DutyHelper.State() == DutyHelper.States.InProgress;
        public static bool InGcInstance => RaptureAtkUnitManager.Controls.Any(r => r.Name == "GcArmyOrder");
        public static bool OnPvpMap => Core.Me.OnPvpMap();

        public static void UpdateAllies(Action extensions = null);
    }
}
```

**In `AEAssist.NET` 1.2.16 — `AEAssist.Helper.PartyHelper`** (note: `IBattleChara`, not `Character`; includes 3-yalm and melee/ranged lists):

```csharp
namespace AEAssist.Helper
{
    public static class PartyHelper
    {
        public static List<IBattleChara> DeadAllies;
        public static List<IBattleChara> Party;
        public static List<IBattleChara> CastableParty;
        public static List<IBattleChara> CastableTanks;
        public static List<IBattleChara> CastableHealers;
        public static List<IBattleChara> CastableDps;
        public static List<IBattleChara> CastableMainTanks;
        public static List<IBattleChara> CastableAlliesWithin30;
        public static List<IBattleChara> CastableAlliesWithin3;
        public static List<IBattleChara> CastableAlliesWithin25;
        public static List<IBattleChara> CastableAlliesWithin20;
        public static List<IBattleChara> CastableAlliesWithin15;
        public static List<IBattleChara> CastableAlliesWithin10;
        public static List<IBattleChara> CastableMelees;
        public static List<IBattleChara> CastableRangeds;

        public static void AddMainTanks(IBattleChara ally);
        public static void ClearCastable();
        public static void UpdateAllies(Action extensions = null);
        public static IBattleChara GetAnotherTank(IBattleChara tank1);
    }
}
```

Also `AEAssist.MemoryApi.MemApiParty`.

## 12. `TargetHelper.CheckNeedUseAOE`

**In `Harmony-P/AEAssist`:** exists as three overloads (file `AEAssist/Helper/TargetHelper.cs`), plus helpers:

```csharp
namespace AEAssist.Helper
{
    public static class TargetHelper
    {
        public static bool CheckNeedUseAOE(int targetRange, int damageRange, int needCount = 3);
        public static bool CheckNeedUseAOE(GameObject target, int targetRange, int damageRange, int needCount = 3);
        public static bool CheckNeedUseAOEByMe(int targetRange, int damageRange, int needCount = 3);
        public static bool CheckNeedUseAOETest(int targetRange, int damageRange, int needCount = 3);
        public static bool CheckNeedUseAOETest(GameObject target, int targetRange, int damageRange, int needCount = 3);

        public static int GetNearbyEnemyCount(GameObject target, int targetRange, int damageRange);
        public static int GetNearbyEnemyCountTest(GameObject target, int targetRange, int damageRange);
        public static float GetTargetDistanceFromMeTest(GameObject target, GameObject origin);

        // extension methods on GameObject, declared in the same class:
        public static bool ValidAttackUnit(this GameObject unit);
        public static bool CanAttackUnit(this GameObject unit);
        public static bool ValidUnit(this GameObject unit);
        public static bool NotInvulnerable(this GameObject unit);
        public static bool CanAttackTargetInRange(this GameObject unit, GameObject target, int range = 3);
        public static bool ValidPartyTarget(this GameObject unit);
    }
}
```

(All `CheckNeedUseAOE*` return `false` when `AEAssist.DataBinding.Instance.UseAOE` is false; counts come from `TargetMgr.Instance.EnemysIn25`.)

**In `AEAssist.NET` 1.2.16: `CheckNeedUseAOE` is NOT FOUND** (0 matches in the exported surface — it lives in AEAssist's built-in, closed-source ACR code). The public `AEAssist.Helper.TargetHelper` is:

```csharp
namespace AEAssist.Helper
{
    public static class TargetHelper
    {
        public static int GetNearbyEnemyCount(IBattleChara target, int spellCastRange, int damageRange);
        public static int GetNearbyEnemyCount(int range);
        public static bool IsBoss(IBattleChara target);
        public static float GetTargetDistanceFromMeTest2D(IBattleChara target, IBattleChara origin);
        public static int GetEnemyCountInsideSector(IBattleChara me, IBattleChara target, float sectorRadius, float sectorAngle);
        public static int GetEnemyCountInsideRect(IBattleChara me, IBattleChara target, float length, float width);
        public static bool targetCastingIsBossAOE(IBattleChara target, int timeLeft);
        public static bool targetCastingIsDeathSentence(IBattleChara target);
        public static bool targetCastingIsDeathSentenceWithTime(IBattleChara target, int timeLeft);
        public static bool TargetIsInvincible(IBattleChara target);
        public static bool TargetHasMagicStatus(IBattleChara target);
        public static bool TargetHasPhysicalStatus(IBattleChara target);
        public static bool MeHasImmobilizeDebuff();
        public static bool MeHasSkillSilenceDebuff();
        public static IBattleChara GetMostCanTargetObjects(uint ActionID);
        public static IBattleChara GetMostCanTargetObjects(uint ActionID, int Count);
        public static IBattleChara GetMostCanTargetObjects(uint ActionID, int Count, float Angle);
    }
}
```

AOE counting in this repo is done with `SpellHelper.GetMostCanTargetObjects` / `GCDHelper` + `MemApiSpell`.

## 13. Player/character extension class (`BattleCharacterExtend` / `Core.Me`)

**In `Harmony-P/AEAssist`: `BattleCharacterExtend`/`CharacterExtend` NOT FOUND** (0 matches). Extensions are split across `AuraHelper` and `SpellHelper`, and the player is RebornBuddy's `ff14bot.Core.Me`:

```csharp
// file: AEAssist/Helper/AuraHelper.cs
namespace AEAssist.Helper
{
    public static class AuraHelper
    {
        public static bool ContainAura(this Character character, uint id, int timeLeft = 0);
        public static int GetAuraStack(this Character character, uint id);
        public static bool HasMyAuraWithTimeleft(this Character character, uint id, int timeLeft = 0);
        public static bool ContainsMyInEndAura(this Character character, uint id, int timeLeft = 0);
        public static bool HasAnyAura(this GameObject unit, List<uint> auras, int msLeft = 0);
    }
}
```

```csharp
// file: AEAssist/Helper/SpellHelper.cs  (public static class SpellHelper — extension methods marked "this")
public static SpellEntity GetSpellEntity(this uint id);
public static async Task<bool> CastGCD(SpellData spell, GameObject target);
public static int CanCastGCD(SpellData spell, GameObject target);
public static async Task<bool> CastAbility(SpellData spell, GameObject target, int waitTime = 0);
public static bool IsUnlock(this SpellData spellData);
public static bool IsUnlock(this uint spellId);
public static bool IsUnlock(this SpellEntity spellId);
public static bool IsReady(this SpellData spellData);
public static bool IsReady(this uint spellId);
public static bool IsReady(this SpellEntity spell);
public static bool IsMaxChargeReady(this SpellData spellData, float delta = 0.5f);
public static bool IsMaxChargeReady(this uint spellId, float delta = 0.5f);
public static bool IsMaxChargeReady(this SpellEntity spellId, float delta = 0.5f);
public static bool CoolDownInGCDs(this SpellData spellData, int count);
public static bool CoolDownInGCDs(this uint spellId, int count);
public static bool AbilityCoolDownInNextXGCDsWindow(this SpellData spellData, int count);
public static bool AbilityCoolDownInNextXGCDsWindow(this uint spellId, int count);
public static int AbilityComesInNextXGCDsWindow(this SpellData spellData);
public static int AbilityComesInNextXGCDsWindow(this uint spellId);
public static Task<bool> DoGCD(this uint spellId);
public static Task<bool> DoAbility(this uint spellId);
public static bool RecentlyUsed(this uint spellId);
public static uint GetInterruptSpell(ClassJobType job);
public static uint GetLastComboSpell();
```

HP/MP/level/cooldown/TTK in this repo come from RebornBuddy itself (`Core.Me.CurrentHealthPercent/CurrentManaPercent/ClassLevel/HasAura/HasMyAura`, `DataManager.GetSpellData(id).Cooldown`) plus:

```csharp
// file: AEAssist/Helper/TTKHelper.cs
namespace AEAssist.Helper
{
    public static class TTKHelper
    {
        public static bool IsTargetTTK(Character target, bool ignoreBossCheck = false);
        public static bool IsTargetTTK(Character target, int timeInSec, bool ignoreBossCheck);
        public static bool CheckFinalBurst(Character target);
    }
}
```

(`TimeToKill` as an identifier: 0 matches in the repo; TTK settings are `GeneralSettings.TimeToKill_TimeInSec`, `OpenTTK`, `AutoFinalBurst`, `AutoFinalBurstCheckTime`.)

**In `AEAssist.NET` 1.2.16 — `AEAssist.Extension.GameObjectExtension` (this is the `BattleCharacterExtend` equivalent) and `LocalPlayerExtension`:**

```csharp
namespace AEAssist.Extension
{
    public static class GameObjectExtension
    {
        public static bool HasAura(IBattleChara battleChara, uint auraId, int timeLeft = 0);
        public static bool HasLocalPlayerAura(IBattleChara battleChara, uint auraId);
        public static int GetAuraStack(IBattleChara characterAgent, uint id);
        public static bool HasCanDispel(IBattleChara characterAgent);
        public static bool HasMyAuraWithTimeleft(IBattleChara characterAgent, uint id, int timeLeft = 0);
        public static bool ContainsMyInEndAura(IBattleChara characterAgent, uint id, int timeLeft = 0);
        public static bool HasAnyAura(IBattleChara characterAgent, List<uint> auras, int msLeft = 0);
        public static uint HitAnyAura(IBattleChara characterAgent, List<uint> auras, int msLeft = 0);
        public static IBattleChara GetCurrTarget(IBattleChara unit);
        public static IBattleChara GetCurrTargetsTarget(IBattleChara unit);
        public static bool TryGetCurrTarget(IBattleChara unit, ref IBattleChara battleChara);
        public static bool ValidAttackUnit(IBattleChara unit);
        public static bool CanAttackUnit(IBattleChara unit);
        public static bool CanAttack(IBattleChara battleChara);
        public static bool ValidUnit(IBattleChara unit);
        public static bool InRange(IBattleChara unit, IBattleChara target, int range = 3);
        public static bool IsBoss(IBattleChara target);
        public static bool IsDummy(IBattleChara battleChara);
        public static bool IsPlayerCamp(IBattleChara source);
        public static bool IsDead(IBattleChara battleChara);
        public static bool InCombat(ICharacter character);
        public static Jobs CurrentJob(ICharacter character);
        public static bool IsTank(ICharacter c);
        public static bool IsDps(ICharacter c);
        public static bool IsHealer(ICharacter c);
        public static bool IsMelee(ICharacter c);
        public static bool IsCaster(ICharacter c);
        public static bool IsRanged(ICharacter c);
        public static float CurrentHpPercent(ICharacter c);
        public static float CurrentMpPercent(ICharacter c);
        public static float CurrentGpPercent(ICharacter c);
        public static float CurrentCpPercent(ICharacter c);
        public static bool IsInParty(ICharacter c);
        public static byte GetCharacterAnimationState(ICharacter c, bool second = true);
        public static uint GetWeaponId(ICharacter c);
        public static bool HasPositional(IGameObject gameObject);
        public static bool IsMe(IGameObject gameObject);
        public static bool IsLocalPlayer(IGameObject gameObject);
        public static bool IsInCombat(IGameObject obj);
        public static Relationship GetRelationshipWithLocalPlayer(IGameObject gameObject);
        public static bool InActionRange(IGameObject gameObject, float range = 30f);
        public static Vector3 GetEyePostion(IGameObject gameObject);
        public static void BecomeTargetOfLocalPlayer(IGameObject gameObject);
        public static float Distance(IGameObject source, IGameObject target, DistanceMode mode = DistanceMode.<7>);
        public static bool IsPvP(IGameObject gameObject);
        public static T ToGameObject<T>(uint objectId);
        public static SectorShape BehindShape(IGameObject gameObject);
        public static bool InBehind(IGameObject gameObject, Vector3 pos, bool checkPositional = true);
        public static bool IsBehindTarget(IGameObject source, IGameObject target);
        public static float DistanceToPlayer(IGameObject obj);
        public static IEnumerable<T> GetObjectInRadius<T>(IEnumerable<T> objects, float radius);
        public static uint GetNamePlateIcon(IGameObject obj);
        public static EventHandlerContent GetEventType(IGameObject obj);
        public static bool IsEnemy(IGameObject obj);
        public static uint FateId(IGameObject obj);
        public static uint EventId(IGameObject obj);
        public static bool EventValid(IGameObject obj);
        public static bool IsInEnemiesList(IGameObject obj);
        public static GameObject* ToStruct(IGameObject obj);
        public static bool TargetInteract(IGameObject gameObject);
        public static string ToLogString(IGameObject obj);
        public static bool InCutSceneState(IPlayerCharacter localPlayer);
    }

    public static class LocalPlayerExtension
    {
        public static uint GetItemCount(IPlayerCharacter localPlayer, uint itemId, bool isHq);
        public static bool UseItem(IPlayerCharacter localPlayer, uint itemId, bool isHq);
        public static TimeSpan GetItemCoolDown(IPlayerCharacter localPlayer, uint itemId);
        public static LimitBreakController LimitBreakController(IPlayerCharacter localPlayer);
        public static byte LimitBreakBarCount(IPlayerCharacter localPlayer);
        public static uint LimitBreakBarValue(IPlayerCharacter localPlayer);
        public static ushort LimitBreakCurrentValue(IPlayerCharacter localPlayer);
        public static void SetPos(IPlayerCharacter lp, Vector3 pos);
        public static IBattleChara GetHighestEnmityGameObjec(IPlayerCharacter lp);
        public static IBattleChara GetHighestEnmityTank(IPlayerCharacter lp);
        public static bool IsMoving(IPlayerCharacter lp);
        public static bool IsMounted(ICharacter chr);
        public static bool IsFlight(ICharacter chr);
        public static bool IsQuestComplete(IPlayerCharacter localPlayer, uint questId);
    }
}
```

`Core.Me`:

```csharp
namespace AEAssist
{
    public static class Core
    {
        public static bool Inited { get; set; }
        public static IPlayerCharacter Me { get; }
        public static T Resolve<T>();
    }
}
```

`Spell` cooldown/readiness accessors: `Spell.Cooldown`, `Spell.RecastTime`, `Spell.RecastTimeElapsed`, `Spell.Charges`, `Spell.MaxCharges`, `Spell.CastTime`, `Spell.AdjustedCastTime` (§6); readiness predicates: `SpellExtension.IsReadyWithCanCast`, `IsUnlock`, `IsUnlockWithCDCheck`, `IsMaxChargeReady`, `CoolDownInGCDs`, `RecentlyUsed`. Time-to-kill: `TTKHelper.IsTargetTTK(IBattleChara target, bool ignoreBossCheck = false)` / `IsTargetTTK(IBattleChara target, int time, bool ignoreBossCheck)`.

## 14. `ItemHelper.CheckCurrJobPotion()` and potion helpers

**In `Harmony-P/AEAssist`: NOT FOUND** (0 matches). The equivalent is the `internal class PotionHelper` (file `AEAssist/Helper/PotionHelper.cs` — note it is `internal`, so not usable from another assembly):

```csharp
namespace AEAssist.Helper
{
    internal class PotionHelper
    {
        public static List<PotionData> DexPotions { get; set; }
        public static List<PotionData> StrPotions { get; set; }
        public static List<PotionData> MindPotions { get; set; }
        public static List<PotionData> IntPotions { get; set; }

        public static void Init();
        public static async Task<bool> UsePotion(int potionRawId);
        public static async Task<bool> ForceUsePotion(int potionRawId);
        internal static bool CheckPotion(int potionRawId);
        public static int CheckNum(int potionId);
    }
}
```

```csharp
// file: AEAssist/DataBinding/PotionData.cs
namespace AEAssist
{
    public class PotionData
    {
        public int ID { get; set; }
        public string Name { get; set; }
    }
}
```

(Potion IDs are configured as `GeneralSettings.DexPotionId` etc.; `UsePotion` is called from `BardAbility_UsePotion`, `IAIPriorityQueue.UsePotion()`, `CountDownHandler.AddPotionAction`.)

**In `AEAssist.NET` 1.2.16 — `AEAssist.Helper.ItemHelper` (exact):**

```csharp
namespace AEAssist.Helper
{
    public class ItemHelper
    {
        public ItemHelper();
        public static void Init();
        public static Task<bool> ForceUsePotion(uint potionRawId, bool isHq);
        public static Task<bool> UseItem(uint itemId, bool isHq);
        public static bool CheckPotion(uint potionRawId, bool isHq);
        public static bool CheckCurrJobPotion(bool isHq = true);   // <- requested signature
        public static uint GetEmptyInventorySlotCount();
    }

    public class PotionData
    {
        public string CnName;
        public uint ID;
        public string Name;
    }
}
```

Related: `Spell.CreatePotion()`, `SpellCategory.Potion`, `AEAssist.CombatRoutine.PotionSetting`, `AEAssist.CombatRoutine.PotionType`.

## 15. `MemApiXXX` and `XXXXHelper` class names

**In `Harmony-P/AEAssist`: `MemApi*` = 0 matches** — no such classes exist.

**In `AEAssist.NET` 1.2.16 — namespaces `AEAssist.MemoryApi` and `AEAssist.API.MemoryApi` (complete list of exported `MemApi*` types):**

`AEAssist.MemoryApi.MemApiAddon`, `MemApiBuff`, `MemApiChatMessage`, `MemApiCondition`, `MemApiCountdown`, `MemApiDuty`, `MemApiDutyFinder`, `MemApiEnvControl`, `MemApiFunctionPointer`, `MemApiHack`, `MemApiHotkey` (+ nested `MessageProcDelegate`), `MemApiIcon`, `MemApiMacro`, `MemApiMap`, `MemApiMapEffect`, `MemApiMarker`, `MemApiMove`, `MemApiMove2` (+ nested `Delegate`, `Delegate.MoveToDelegate`), `MemApiMove3`, `MemApiMoveControl`, `MemApiNotification`, `MemApiParty`, `MemApiSendMessage`, `MemApiSpell` (+ nested `LineOfSightChecker`), `MemApiSpellCastFail`, `MemApiSpellCastInfo`, `MemApiSpellCastSuccess` (+ nested `Castinfo`, `GcdInfo`), `MemApiTarget`, `MemApiTeleport`, `MemApiTether`, `MemApiTrust`, `MemApiZoneInfo`.
`AEAssist.API.MemoryApi.MemApiContentFinderCondition`, `AEAssist.API.MemoryApi.MemApiFishing` (+ nested `QuadtreeWrapper`, `Triangle`).

**Helpers in `Harmony-P/AEAssist`** (top-level class names, from `AEAssist/Helper/*.cs`; `AEAssist/View/UIHelper.cs`; `AEAssistLoader/MongoHelper.cs`; `AETriggers/…`), with visibility:

| File (repo-relative) | Declaration |
|---|---|
| `AEAssist/Helper/AuraHelper.cs` | `public static class AuraHelper` |
| `AEAssist/Helper/DataHelper.cs` | `internal static class DataHelper` |
| `AEAssist/Helper/DotBlacklistHelper.cs` | `public static class DotBlacklistHelper` |
| `AEAssist/Helper/DutyHelper.cs` | `public static class DutyHelper` (+ `public enum States`) |
| `AEAssist/Helper/GroupHelper.cs` | `public static class GroupHelper` |
| `AEAssist/Helper/GUIHelper.cs` | `public static class GUIHelper` |
| `AEAssist/Helper/LanguageHelper.cs` | `public static class LanguageHelper` |
| `AEAssist/Helper/LogHelper.cs` | `public static class LogHelper` |
| `AEAssist/Helper/PotionHelper.cs` | `internal class PotionHelper` |
| `AEAssist/Helper/RandomHelper.cs` | `public static class RandomHelper` |
| `AEAssist/Helper/SpellHelper.cs` | `public static class SpellHelper` |
| `AEAssist/Helper/SpellHistoryHelper.cs` | `public static class SpellHistoryHelper` |
| `AEAssist/Helper/TargetHelper.cs` | `public static class TargetHelper` |
| `AEAssist/Helper/TimeHelper.cs` | `public static class TimeHelper` |
| `AEAssist/Helper/TriggerLineSwitchHelper.cs` | `public static class TriggerLineSwitchHelper` |
| `AEAssist/Helper/TTKHelper.cs` | `public static class TTKHelper` |
| `AEAssist/Helper/WorldHelper.cs` | `public static class WorldHelper` |
| `AEAssist/View/UIHelper.cs` | `public static class UIHelper` |
| `AEAssist/AI/GeneralAI/PhysicsRangeDPSHelper.cs` | `public static class PhysicsRangeDPSHelper` |
| `AEAssist/AI/<Job>/*SpellHelper.cs` | `public static class AstSpellHelper`, `BardSpellHelper`, `BlackMageSpellHelper`, `DancerSpellHelper`, `DarkKnight_SpellHelper`, `Dragoon_SpellHelper`, `GunBreakerSpellHelper`, `MCHSpellHelper`, `MonkSpellHelper`, `NinjaSpellHelper`, `Paladin_SpellHelper`, `ReaperSpellHelper`, `RedMage_SpellHelper`, `SageSpellHelper`, `SamuraiSpellHelper`, `Scholar_SpellHelper`, `SMN_SpellHelper`, `Warrior_SpellHelper`, `WhiteMageSpellHelper` |
| `AEAssistLoader/MongoHelper.cs`, `AETriggers/LogHelper.cs`, `AETriggers/TriggerModel/MongoHelper.cs`, `AETriggers/TriggerModel/TriggerHelper.cs` | same names in their own assemblies |

**Helpers in `AEAssist.NET` 1.2.16** (`AEAssist.Helper`, complete exported list): `AddressHelper`, `AutoHelper` (+`TriangleCoordinateSystem`), `AutoRecommnadEquipHelper`, `ChatHelper`, `CompSig`, `CondParamHelper`, `CraftHelper`, `DotBlacklistHelper`, `ExecuteCommandHelper`, `GCDHelper`, `HaJimiHelper`, `HotkeyHelper`, `ItemHelper`, `JobHelper`, `KeyHelper`, `LogHelper`, `LuminaHelper`, `MacIdHelper`, `MacroHelper`, `MathHelper`, `MeleePosHelper2`, `MoveHelper`, `MultiHelper`, `NetworkHelper` (+`EventStartPackt`), `NumericHelper`, `PartyHelper`, `RandomHelper`, `RemoteControlHelper`, `SpellExtension`, `SpellHelper`, `SpellHistoryHelper`, `SpiritbondHelper`, `SplHelper`, `StringHelper`, `TargetHelper`, `TimeHelper`, `TriggerlineAssistHelper`, `TriggerLineHelper`, `TTKHelper`, `VnavHelper`, `WeatherHelper`, `WindowHelper`, `ZIPHelper`. Also `AEAssist.Helper.GamePacketOpcodes`, `AEAssist.Helper.ExecuteCommandFlag`, `AEAssist.Helper.ExecuteCommandComplexFlag`.

---

## Practical recommendation for the parent

1. **`Harmony-P/AEAssist` is the wrong target for the stated goal** — it is a 2022 RebornBuddy combat routine; its rotation API is `IRotation` + `[Job(ClassJobType)]` and it can only load rotations from its own assembly (`GetType().Assembly.GetTypes()`), so a third-party ACR DLL cannot plug into it as described.
2. **Write the ACR against `AEAssist.NET`** (modern Dalamud AEAssist): `dotnet` project targeting `net10.0-windows`, `PackageReference Include="AEAssist.NET" Version="*" ExcludeAssets="runtime"`, NuGet source `https://www.myget.org/F/aeassist/api/v3/index.json`, plus `upload_config.yaml` for publishing. Baseline the project on `aeassist-acr/ACR_template` (branch `master`).
3. Exact surface for items 1–15 as requested is in §1–§15 above; the entry contract is `IRotationEntry.Build(string settingFolder)` returning a configured `Rotation`, with `ISlotResolver.Check()/Build(Slot)` populating `Slot` objects and `IRotationEventHandler` receiving `OnSpellCastSuccess/AfterSpell(Slot, Spell)`.

*Method: repo tree via GitHub trees API, all 657 `.cs` files fetched and searched locally; modern API via metadata dump (System.Reflection.MetadataLoadContext) of `AEAssist.dll` from `AEAssist.NET.1.2.16.nupkg` obtained from the AEAssist MyGet feed.*
