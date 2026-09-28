# ActionEffect Hook 逆向报告

这份文档记录**技能效果追踪（ActionEffectTracker）**的完整逆向过程。
结论先写：**链路已挖通，但最后一步的签名用途还需要实证，我没有硬上。**

---

## 一、Hook 链路（三层）

```
第一层：FFXIVClientStructs
    FFXIV.Client.Game.Character.ActionEffectHandler
        ↑ 游戏侧的效果处理器（结构体）

第二层：AEAssist 内部
    AEAssist.ACT.ActionHook
        .ctor()
        static Void Init()      -> 调 Enable
        static Void Dispose()   -> 调 Dispose
        .cctor 里有签名串: "E8 ?? ?? ?? ?? B0 01 EB B6 ?? ?? ?? ?? ?? ?? ??"

    AEAssist 的内部方法（名字被混淆了）
        调: ActionEffect.add_ActionEffectEvent
            ISigScanner.ScanText × 4 个签名
            ActionHook.Init

第三层：可用的公开 API
    AEAssist.Helper.CompSig
        .ctor(String Signature, String SignatureCN = null)   ← 公开构造
        method T GetDelegate()                                ← 拿原始委托
        method Hook<T> GetHook(T detour)                      ← 挂 hook
        method IntPtr ScanText() / T* ScanText()
        method IntPtr GetStatic(int offset = 0)
```

**关键结论**：**AEAssist 自己已经 hook 好了 `ActionEffect`**，并把它包装成了
`ActionEffect.add_ActionEffectEvent` 这个 .NET 事件 —— **不需要自己扫内存签名**。

---

## 二、已经确认的 API

| API | 归属 | 状态 |
|---|---|---|
| `ActionEffect.add_ActionEffectEvent` | AEAssist 内部调用 | ✅ 存在，但没导出成公开 API |
| `ActionHook.Init()` / `Dispose()` | `AEAssist.ACT.ActionHook` | ✅ 公开，但**只开关、不给事件** |
| `ActionEffectHandler` | FFXIVClientStructs | ✅ 存在，成员待细查 |
| `CompSig.GetHook<T>(T detour)` | `AEAssist.Helper.CompSig` | ✅ 公开可用 |
| `CompSig..ctor(签名, 中文签名)` | 同上 | ✅ 公开可用 |
| `ISigScanner.ScanText` | Dalamud | ⚠️ ACR 拿不到 Dalamud 注入的服务 |

---

## 三、鍚岀被 ACR 是怎么做的

```
鍚岀被 ACR.BLM_7.ActionEffectTracker
    Enable       -> Enable
    Disable      -> Reset / Disable / Dispose
    Reset        -> Monitor.Enter / Clear / Monitor.Exit
    TryConfirmAction -> MatchesAction
    MatchesAction    -> MemApiSpell.CheckActionChange
    ReceiveActionEffectDetour
        常量(0, 32, 1)
        调: ReceiveActionEffectDelegate.Invoke
            DateTimeOffset.ToUnixTimeMilliseconds
            Monitor.Enter / Enqueue / Dequeue / Monitor.Exit
```

**注意两点**：
1. `ReceiveActionEffectDelegate` **在 AEAssist 里查不到** → 是 **鍚岀被 ACR 自己声明的 delegate**
2. `ActionEffectTracker` 类里**没有任何签名串** → hook 是在**别处**挂的（基类或外部初始化）

它用了 `Monitor`（锁）+ `Queue`（队列）+ `DateTimeOffset`（时间戳）——
说明它是**多线程安全的**：游戏回调线程入队、战斗线程出队确认。

---

## 四、还差什么（诚实说明）

### 缺 1：签名串的实际用途

我从 `ActionHook..cctor` 扒到了：
```
E8 ?? ?? ?? ?? B0 01 EB B6 ?? ?? ?? ?? ?? ?? ??
```

**但不确定**它是给 `ActionHook` 还是给 `ActionEffect` 用的 —— 
`AEAssist.dll` 里那个混淆方法一口气用了 **4 个签名**，我只定位到其中一个。

### 缺 2：delegate 的完整签名

要挂 hook 得先声明一个和游戏函数**完全一致**的委托。从
`ReceiveActionEffectDetour` 的参数看，至少要有 `sourceId` 和 `actionId`，
但完整参数表（有没有 target、effect 数组、指针类型）还需要从 FFXIVClientStructs
的 `ActionEffectHandler` 里读出来。

### 缺 3：实例来源

`CompSig` 可以 `new`，但 `ActionHook` 是 AEAssist 内部初始化的 —— 
如果走 `CompSig` 路线就绕开它，得自己管生命周期（Dispose 时 Unhook）。

---

## 五、如果要做，建议的路线

**路线 A（推荐，风险低）**：等/找 AEAssist 公开 `ActionEffectEvent`

既然它内部已经 `add_ActionEffectEvent` 了，很可能某个版本会导出成公开事件。
也可以在 AEAssist 的 ACR 群里问作者公开入口。

**路线 B（中等风险）**：用 `CompSig` + 完整签名

```csharp
// 1) 声明和游戏函数一致的委托
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void ReceiveActionEffectDelegate(
    uint sourceId, IntPtr source, uint targetId, IntPtr target,
    IntPtr effectArray, IntPtr effectFlags);

// 2) 用签名扫地址 + 挂 hook
var sig = new CompSig("完整签名串");
var hook = sig.GetHook<ReceiveActionEffectDelegate>(我的回调);
// hook.Enable() / hook.Disable() 由自己管
```

**前提**：签名串必须**逐字节确认**（从 FFXIVClientStructs 的 sig 定义里拿更稳）。

**路线 C（高风险）**：直接 hook `ActionHook` 的 4 个签名

不推荐 —— 那是 AEAssist 的内部实现，它自己也依赖那些签名，改了会互相干扰。

---

## 六、我的判断

**收益**：确认"技能是否真的命中"，比 `RecentlyUsed`（AEAssist 记录）/`GetCharges`（充能数）更硬。

**风险**：替换游戏回调级别的操作。搞错了不是"技能不放"这种小故障，而是**整个 ACR 崩或者游戏行为异常**。

**现状**：我已经用 `RecentlyUsed` + `GetCharges` 拿到了 90% 的效果 ——
- `RecentlyUsed` 能判断"技能放过没有"（AEAssist 在成功释放后记录）
- `GetCharges` 能读真实充能层数

**ActionEffectTracker 的独特价值**在于"**命中确认**"（放出去了 ≠ 打中了），
这个在日随场景里用得不多，在高难里才有意义。

**所以**：链路留着，等签名确认清楚再做。**我宁愿留一份准确的报告，也不塞一个可能崩的实现进来。**
