param(
  [string]$In,
  [int]$Start,
  [int]$End,
  [string]$OutPath
)
$ErrorActionPreference = 'Stop'
$lines = Get-Content $In
$slice = $lines[($Start-1)..($End-1)]
$dropPatterns = @(
  ':\s*endfinally\s*$',
  ':\s*constrained\.',
  ':\s*nop\s*$',
  ':\s*leave(\.s)?\s',
  'DefaultInterpolatedStringHandler::',
  ':\s*ldloca\.s\s',
  'System\.IDisposable::Dispose'
)
$acc = [System.Collections.Generic.List[string]]::new()
foreach ($l in $slice) {
  $skip = $false
  foreach ($p in $dropPatterns) { if ($l -match $p) { $skip = $true; break } }
  if ($skip) { continue }
  $s = $l
  $s = $s.Replace('鍚岀被 ACR.Scholar.', 'S.')
  $s = $s.Replace('鍚岀被 ACR3._0.', 'A3.')
  $s = $s.Replace('鍚岀被 ACR.', 'A.')
  $s = $s.Replace('鍚岀被 ', '')
  $s = $s.Replace('System.Collections.Generic.', 'G.')
  $s = $s.Replace('System.Runtime.CompilerServices.', 'CSI.')
  $s = $s.Replace('Dalamud.Game.ClientState.Objects.Types.', 'D.')
  $s = $s.Replace('Dalamud.Game.ClientState.Objects.SubKinds.', 'DS.')
  $s = $s.Replace('Dalamud.Game.ClientState.', 'DC.')
  $s = $s.Replace('AEAssist.CombatRoutine.Module.', 'M.')
  $s = $s.Replace('AEAssist.CombatRoutine.', 'R.')
  $s = $s.Replace('AEAssist.Helper.', 'H.')
  $s = $s.Replace('AEAssist.DataBinding.', 'DB.')
  $s = $s.Replace('AEAssist.IO.', 'IO.')
  $s = $s.Replace('AEAssist.', 'A.')
  $s = $s.Replace('Dalamud.Bindings.ImGui.', 'IM.')
  $s = $s.Replace('System.Numerics.', 'N.')
  $s = $s -replace '^\s*IL_([0-9a-f]{4}):\s*', 'IL_$1: '
  $s = $s -replace '\s+$', ''
  $acc.Add($s)
}
Set-Content -Path $OutPath -Value $acc -Encoding UTF8
"OK $OutPath : in=$($slice.Count) out=$($acc.Count)"
