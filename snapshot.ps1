<#
================================================================================
  Source Snapshot Tool -- lightweight version control
================================================================================

  Why not git:
    Not installed on this machine, and installing it pulls tens of MB.
    What this project actually needs is simple: be able to roll back.

  Usage:
    .\snapshot.ps1 save "added scholar logic"    create a snapshot
    .\snapshot.ps1 list                          list all snapshots
    .\snapshot.ps1 restore 3                     restore snapshot 3
    .\snapshot.ps1 diff 3                        diff current vs snapshot 3

  What is stored:
    Source code only (*.cs *.csproj *.sln *.md *.txt *.json *.ps1 *.js *.tsv)
    Excluded: bin obj release localnuget ref .snapshots build

  Where:
    .snapshots\<index>_<timestamp>\

================================================================================
#>

param(
    [Parameter(Position = 0)]
    [ValidateSet('save', 'list', 'restore', 'diff', 'help')]
    [string]$Action = 'help',

    [Parameter(Position = 1)]
    [string]$Value = ''
)

$ErrorActionPreference = 'Stop'

$Root      = $PSScriptRoot
$SnapRoot  = Join-Path $Root '.snapshots'
$Patterns  = @('*.cs', '*.csproj', '*.sln', '*.md', '*.txt', '*.json', '*.ps1', '*.js', '*.tsv')
$ExcludeRe = '\\(bin|obj|release|localnuget|ref|\.snapshots|\.git|node_modules|build)\\'

function Get-SourceFiles {
    $list = @()
    foreach ($p in $Patterns) {
        $list += Get-ChildItem $Root -Filter $p -Recurse -File -ErrorAction SilentlyContinue |
                 Where-Object { $_.FullName -notmatch $ExcludeRe }
    }
    return $list
}

function Do-Save {
    param([string]$Note)

    if (-not (Test-Path $SnapRoot)) { New-Item -ItemType Directory -Path $SnapRoot | Out-Null }

    $existing = @(Get-ChildItem $SnapRoot -Directory -ErrorAction SilentlyContinue)
    $index    = $existing.Count + 1
    $stamp    = Get-Date -Format 'yyyyMMdd_HHmmss'
    $dir      = Join-Path $SnapRoot ("{0:D3}_{1}" -f $index, $stamp)

    New-Item -ItemType Directory -Path $dir | Out-Null

    $files = Get-SourceFiles
    $manifest = @()

    foreach ($f in $files) {
        $rel = $f.FullName.Substring($Root.Length).TrimStart('\')
        $dst = Join-Path $dir $rel
        $dstDir = Split-Path $dst -Parent
        if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
        Copy-Item $f.FullName $dst -Force
        $manifest += $rel
    }

    $noteFile = Join-Path $dir '_note.txt'
    $lines = @(
        "index: $index",
        "time: $stamp",
        "note: $Note",
        "count: $($manifest.Count)",
        ""
    ) + $manifest
    $lines | Set-Content $noteFile -Encoding UTF8

    Write-Host "  [OK] snapshot $index saved ($($manifest.Count) files)" -ForegroundColor Green
    Write-Host "       $dir"
    Write-Host "       note: $Note"
}

function Do-List {
    if (-not (Test-Path $SnapRoot)) { Write-Host "  no snapshots yet"; return }

    $all = @(Get-ChildItem $SnapRoot -Directory | Sort-Object Name)
    if ($all.Count -eq 0) { Write-Host "  no snapshots yet"; return }

    Write-Host ""
    Write-Host "  IDX   TIME              FILES  NOTE"
    Write-Host "  ----  ----------------  -----  ----"

    foreach ($d in $all) {
        $noteFile = Join-Path $d.FullName '_note.txt'
        $note = ''
        $cnt  = 0
        if (Test-Path $noteFile) {
            $content = Get-Content $noteFile -Encoding UTF8
            $note = ($content | Where-Object { $_ -match '^note: ' })  -replace '^note: ', ''
            $c    = ($content | Where-Object { $_ -match '^count: ' }) -replace '^count: ', ''
            if ($c) { $cnt = [int]$c }
        }
        Write-Host ("  {0,-4}  {1,-16}  {2,-5}  {3}" -f $d.Name.Substring(0,3), $d.Name.Substring(4), $cnt, $note)
    }
    Write-Host ""
}

function Find-Snapshot {
    param([string]$Text)

    $n = 0
    if (-not [int]::TryParse($Text, [ref]$n)) { return $null }
    return Get-ChildItem $SnapRoot -Directory | Where-Object { $_.Name -like ("{0:D3}_*" -f $n) } | Select-Object -First 1
}

function Do-Restore {
    param([string]$Text)

    if (-not $Text) { Write-Host "  usage: .\snapshot.ps1 restore <index>" -ForegroundColor Yellow; return }

    $target = Find-Snapshot $Text
    if (-not $target) { Write-Host "  snapshot $Text not found" -ForegroundColor Red; return }

    $noteFile = Join-Path $target.FullName '_note.txt'
    if (Test-Path $noteFile) {
        $note = (Get-Content $noteFile -Encoding UTF8 | Where-Object { $_ -match '^note: ' }) -replace '^note: ', ''
        Write-Host ""
        Write-Host "  restoring: $($target.Name)" -ForegroundColor Yellow
        Write-Host "  note: $note"
    }

    Write-Host ""
    Write-Host "  saving current state first (so you can come back)..." -ForegroundColor Cyan
    Do-Save "auto backup (before restoring $Text)"

    $n = 0
    Get-ChildItem $target.FullName -Recurse -File | Where-Object { $_.Name -ne '_note.txt' } | ForEach-Object {
        $rel = $_.FullName.Substring($target.FullName.Length).TrimStart('\')
        $dst = Join-Path $Root $rel
        $dstDir = Split-Path $dst -Parent
        if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
        Copy-Item $_.FullName $dst -Force
        $n++
    }

    Write-Host ""
    Write-Host "  [OK] restored $n files" -ForegroundColor Green
}

function Do-Diff {
    param([string]$Text)

    if (-not $Text) { Write-Host "  usage: .\snapshot.ps1 diff <index>" -ForegroundColor Yellow; return }

    $target = Find-Snapshot $Text
    if (-not $target) { Write-Host "  snapshot $Text not found" -ForegroundColor Red; return }

    Write-Host ""
    Write-Host "  diff vs snapshot $Text :"
    $script:changed = 0
    $seen = @()

    Get-ChildItem $target.FullName -Recurse -File | Where-Object { $_.Name -ne '_note.txt' } | ForEach-Object {
        $rel = $_.FullName.Substring($target.FullName.Length).TrimStart('\')
        $seen += $rel
        $cur = Join-Path $Root $rel

        if (-not (Test-Path $cur)) {
            Write-Host "    [deleted] $rel" -ForegroundColor Red
            $script:changed++
        } else {
            $a = (Get-FileHash $_.FullName -Algorithm MD5).Hash
            $b = (Get-FileHash $cur -Algorithm MD5).Hash
            if ($a -ne $b) {
                Write-Host "    [changed] $rel" -ForegroundColor Yellow
                $script:changed++
            }
        }
    }

    foreach ($f in Get-SourceFiles) {
        $rel = $f.FullName.Substring($Root.Length).TrimStart('\')
        if ($seen -notcontains $rel) {
            Write-Host "    [added]   $rel" -ForegroundColor Green
            $script:changed++
        }
    }

    if ($script:changed -eq 0) { Write-Host "    (no differences)" }
    Write-Host ""
}

switch ($Action) {
    'save'    { Do-Save $Value }
    'list'    { Do-List }
    'restore' { Do-Restore $Value }
    'diff'    { Do-Diff $Value }
    default {
        Write-Host @"

  Source Snapshot Tool

  Usage:
    .\snapshot.ps1 save "note"      create a snapshot
    .\snapshot.ps1 list             list snapshots
    .\snapshot.ps1 restore 3        restore snapshot 3
    .\snapshot.ps1 diff 3           diff current vs snapshot 3

  Stored in .snapshots\ -- source code only, no build output.

"@
    }
}
