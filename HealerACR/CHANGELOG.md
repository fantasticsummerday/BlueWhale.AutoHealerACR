# 更新记录

## 0.1.0（测试版）—— 首次编译通过

**编译环境**：`.NET SDK 10.0.401` + `AEAssist.NET 1.2.16`（myget 源）
**结果**：**0 错误 0 警告**
**产物**：`release/HealerACR-0.1.0.zip`（48 KB）

```
HealerACR-0.1.0/
  HealerACR.dll            56 KB   ← 入口在这
  HealerACR.pdb            32 KB
  安装说明.txt                     ← 给用户的
  Timelines/
    valigarmanda.txt               ← cactbot 时间轴样例
    说明.txt
```

### 这个版本有什么

- 四个奶妈（白魔 / 学者 / 占星 / 贤者），1-100 级
- 一套通用治疗/输出逻辑 + 每个职业一张技能表
- 职业资源：白魔百合、学者小仙女+炽天使、占星卡牌+地星、贤者蛇胆+毒刺
- 减伤：boss 读条判断 **+** cactbot 时间轴双路
- 时间轴：解析 `.txt`、模拟 jump（时间偏移 + 分支判定）、未来预报
- 时间轴驱动的攒资源 / 预铺盾 / 拉炽天使 / 地星提前 10 秒
- QT 面板 + 阈值滑条页

---

## ⚠️ 编译时挖出来的两个致命问题

这两个如果不解决，**整个 ACR 一行都跑不起来**。记在这里，免得下次再踩。

### 1. `SpellsDefine` 里没有任何职业技能

`AEAssist.CombatRoutine.SpellsDefine` 只有 **40 个成员**，全是通用技能：

```
Sprint / SecondWind / Bloodbath / TrueNorth / ArmsLength / Feint /
HeadGraze / FootGraze / LegGraze / Peloton / LegSweep / Potion /
Surecast / Addle / Swiftcast / LucidDreaming / Esuna / Rescue / Repose /
Rampart / Provoke / Reprisal / Shirk / Interject / LowBlow / Mug /
Ten / Chi / Jin + 多变迷宫那一组
```

`SpellsDefine.Cure`、`SpellsDefine.Dosis`、`SpellsDefine.AfflatusSolace`…
**一个都不存在**。实测在整个 AEAssist.NET 元数据里 grep `Dosis|Prognosis|Medica|Adloquium|EarthlyStar`
返回 **0 条**。

**修法**：技能 ID 改成运行时按名字解析 —— `Common/SpellIds.cs`：

```csharp
SpellIds.取("Cure II", "治疗II")     // 先试英文名，再试中文名
```

内部是 `MemApiSpell.GetId(名字)`，查到就缓存。查不到会打日志，
可以在设置 json 的 `技能Id覆盖` 里手工补。

### 2. `AurasDefine` 只剩团辅类 buff

`AEAssist.CombatRoutine.AurasDefine` 的成员全在这：

```
BattleVoice / IronWill / Grit / Brotherhood / Defiance / TrueNorth /
TechnicalFinish / RoyalGuard / Embolden / BattleLitany / ArcaneCircle /
SearingLight / ChainStratagem / DeathsDesign / SoulReaver / …  Mug
```

**这不是"buff 常量表"，是"团辅技能表"**。
治疗要用的 `Swiftcast` / `Raise` / `LivingDead` / `Holmgang` / `Dia` / `Bio`
**全都不在里面**（顺带一提：2.0 版的 AurasDefine 是有这些的，3.0 换掉了）。

**修法**：`Common/AuraIds.cs` 硬编码状态 ID，并且**每个值都能在设置里覆盖**：

```json
"BuffId覆盖": { "即刻": 167, "死斗": 409, "白魔Dot": 1871 }
```

**写成 0 = 关掉这个判断**，对应功能自动降级，不会崩 —— 这是给"拿不准的 ID"留的退路。

---

## 其他修掉的编译错误

| 报错 | 真正的原因 | 修法 |
|---|---|---|
| `未能找到 ImGuiNET` | AEAssist 3.0 用的是 **Dalamud 自己的 ImGui 绑定**，不是 ImGui.NET | `using Dalamud.Bindings.ImGui;` |
| `"Jobs" 是命名空间，但此处被当做类型` | **我自己的 `HealerACR.Jobs` 命名空间把 `AEAssist.Jobs` 枚举遮住了** | 命名空间改名 `HealerACR.Rotations`，目录一起改 |
| `未能找到类型 Jobs` | `JobSpellTable.cs` 第一行直接是 `namespace`，没有 using 块 | 补 `using AEAssist.CombatRoutine;` |
| `uint 未包含 GetSpell 的定义` | `GetSpell` 是 `AEAssist.Helper.SpellHelper` 的，缺 `using AEAssist.Helper;` | 加进 GlobalUsings |
| `运算符 ? 无法应用于 CardType` | `CardType` 是**枚举（值类型）**，不能 `?.` | 去掉问号 |
| `参数 1 无法从 string 转换为 char` | `IBattleChara.Name` 是 **SeString**，不是 string | `.ToString()` |
| `当前上下文中不存在名称 AI` | `AI` 在 `AEAssist.CombatRoutine.Module` | 加进 GlobalUsings |
| `InputText 参数 3 无法从 uint 转换为 ImGuiInputTextFlags` | Dalamud 的新 ImGui 绑定签名变了 | 去掉那个输入框，改成改 json |

**新增文件**：`GlobalUsings.cs` —— 把 `AEAssist` / `AEAssist.CombatRoutine` /
`AEAssist.CombatRoutine.Module` / `AEAssist.Extension` / `AEAssist.Helper` /
`Dalamud.Bindings.ImGui` / `Dalamud.Game.ClientState.Objects.Types` 全注册成全局，
省得每个文件漏一个就报一片错。

---

## 还没有验证的（重要）

**编译通过 ≠ 能用。** 这版**运行时行为一行都没跑过**（本机没装游戏）。
需要在游戏里核对：

1. 日志里有没有 `[HealerACR] 技能名解析失败：XXX`
   —— 有的话说明那个名字查不到，要填 `技能Id覆盖`
2. `AuraIds` 里那几个带 ⚠️ 的数字对不对（尤其假死四件套和四个 DoT）
3. 时间轴分支判定能不能及时认出 storm / ice 那种双分支
4. 卡牌近战/远程判断（靠 `CardType.ToString()` + 关键词名单）

## 怎么编译

```powershell
# 依赖在 myget 上，HealerACR/NuGet.config 已经配好
dotnet build HealerACR\HealerACR.csproj -c Release -p:AEAssistAcrDir="D:\某处\Output\ACR"
```

产物落在 `<AEAssistAcrDir>\HealerACR\`，**文件夹名必须和 dll 同名**。
