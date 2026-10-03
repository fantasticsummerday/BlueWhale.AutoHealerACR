// 全局 using —— 这几个命名空间几乎每个文件都要用，集中在这里，省得漏。
//
// 说明：
//   Dalamud.Bindings.ImGui                 → ImGui.SliderFloat 等
//     （AEAssist 3.0 用 Dalamud 自己的 ImGui 绑定，**不是** ImGui.NET，
//       命名空间也不是 ImGuiNET）
//   Dalamud.Game.ClientState.Objects.Types → IBattleChara / IPlayerCharacter
//   AEAssist.Helper                        → SpellHelper.GetSpell / SpellExtension 的扩展方法
//                                            / GCDHelper / PartyHelper / TargetHelper / LogHelper
//   AEAssist.CombatRoutine.Module          → ISlotResolver / Slot / SlotMode / SlotResolverData / AI
//   AEAssist.CombatRoutine                 → IRotationEntry / Rotation / Spell / Jobs / AcrType

global using Dalamud.Game.ClientState.Objects.Types;
global using Dalamud.Game.ClientState.JobGauge.Enums;
global using Dalamud.Bindings.ImGui;

global using AEAssist;
global using AEAssist.CombatRoutine;
global using AEAssist.CombatRoutine.Module;
global using AEAssist.Extension;
global using AEAssist.Helper;
