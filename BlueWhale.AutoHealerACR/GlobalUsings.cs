// ══════════════════════════════════════════════════════════════════════════
//  ★ 本文件是「引用 HealerACR」改造的产物 ★
//
//  [!] 为什么需要它：
//      原来 `BlueWhale.AutoHealerACR.csproj` 用
//          &lt;Compile Include="..\HealerACR\**\*.cs" /&gt;
//      把 HealerACR 的源码**连同它的 `GlobalUsings.cs` 一起**编译了进来 ——
//      所以 BlueWhale 的代码能直接用 `IBattleChara` / `Rotation` / `ISlotResolver`
//      这些名字，**而自己一行 using 都没写**。
//
//      改成 `ProjectReference` 之后，那些全局 using **不再跨界生效**，
//      38 个编译错误暴露了这个**一直存在的隐式依赖**。
//      ==> 这个文件就是把它**显式化**。
//
//  [!] 内容与 `HealerACR\GlobalUsings.cs` 保持一致 ——
//      共享源码（`HealerACR\**\*.cs` 里那些）在两个程序集里编译时，
//      都需要同一组 using，否则会因为"在 A 里能编、在 B 里编不过"而分裂。
// ══════════════════════════════════════════════════════════════════════════

// Dalamud.Bindings.ImGui                 → ImGui.SliderFloat 等
//   （AEAssist 3.0 用 Dalamud 自己的 ImGui 绑定，**不是** ImGui.NET，
//     命名空间也不是 ImGuiNET）
// Dalamud.Game.ClientState.Objects.Types → IBattleChara / IPlayerCharacter
// AEAssist.Helper                        → SpellHelper.GetSpell / SpellExtension 的扩展方法
//                                          / GCDHelper / PartyHelper / TargetHelper / L
// AEAssist.CombatRoutine.Module          → ISlotResolver / Slot / SlotMode / SlotResolver
// AEAssist.CombatRoutine                 → IRotationEntry / Rotation / Spell / Jobs / ACR
global using Dalamud.Game.ClientState.Objects.Types;
global using Dalamud.Bindings.ImGui;

global using AEAssist;
global using AEAssist.CombatRoutine;
global using AEAssist.CombatRoutine.Module;
global using AEAssist.Extension;
global using AEAssist.Helper;
