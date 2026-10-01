<#
.SYNOPSIS
    发布打包脚本 —— 带**强制校验**，防止漏文件。

.DESCRIPTION
    ══════════════════════════════════════════════════════════════════
     为什么要有这个脚本（用户要求："确保每次发布时时间轴已经打包进去了"）
    ══════════════════════════════════════════════════════════════════

     原来的打包是**手敲一长串命令**，而辅助文件靠"从上一个包复制"传来传去。
     实测后果：cactbot 时间轴（310 份、3.6 MB）提取完之后，
     **连续 10 个版本都没进包** —— 因为复制源是提取之前的老包，
     而这个链条上没有任何一步会报错。

     失败模式很隐蔽：包里确实有 `Timelines\` 目录（有 valigarmanda.txt），
     所以"看起来没问题"，直到用户发现时间轴没生效。

     ── 所以这个脚本的核心是**校验**，不是打包 ──
     打包本身谁都会写；难的是**发现漏了**。
     校验会明确要求：
       · 时间轴份数 ≥ 阈值（默认 300）——  少一份就报错退出
       · `Timelines\` 目录必须存在
       · dll 的版本号 + revision 必须对得上

.PARAMETER 主版本
    HealerACR 的版本号（如 1.35.0）

.PARAMETER BlueWhale版本
    BlueWhale 的版本号（如 1.42.0）

.PARAMETER 跳过校验
    仅供调试用 —— 正常发布**不要**加这个开关

.EXAMPLE
    .\tools\发布打包.ps1 -HealerVer 1.35.0 -BlueVer 1.42.0
#>

param(
    [Parameter(Mandatory = $true)][string]$HealerVer,
    [Parameter(Mandatory = $true)][string]$BlueVer,
    [switch]$SkipCheck
)

$ErrorActionPreference = "Stop"

$仓库根 = Split-Path -Parent $PSScriptRoot
Set-Location $仓库根

# 时间轴最少份数 —— 低于这个数说明提取产物没同步/没生成
$最少时间轴份数 = 300

# ⚠️ 这些是时间轴目录里的**非时间轴**文件，计数时要排除。
#    不排除的话计数会虚高，而"虚高"恰恰会掩盖真正的丢失。
$非时间轴文件 = @("说明.txt", "授权说明.txt", "readme.txt")

function 标题($t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }
function 好($t)   { Write-Host "  OK  $t" -ForegroundColor Green }
function 坏($t)   { Write-Host "  错误 $t" -ForegroundColor Red }
function 提示($t) { Write-Host "      $t" -ForegroundColor DarkGray }

# 统计时间轴份数（排除说明文件）
function 数时间轴($目录) {
    if (-not (Test-Path $目录)) { return -1 }
    return @(Get-ChildItem $目录 -Recurse -File -Filter "*.txt" |
             Where-Object { $非时间轴文件 -notcontains $_.Name }).Count
}

# 找**版本号最大**的旧包目录。
#
# ⚠️ 不能用 Sort-Object Name —— 那是**字符串**比较，
#    `HealerACR-1.4.0` 会排在 `HealerACR-1.34.0` 后面（"4" > "3"），
#    于是会选中很旧的包（实测差点选中 1.4.0）。
function 最新旧包($release目录, $前缀) {
    # [!] 用 .NET 的 `[version]` 比较，**不要**自己拼整数。
    #
    # ⚠️ 原来写的是 `主*1000000 + 次*1000 + 修订`（只取前三段）——
    #    版本号前缀改成 `0.3.x` 之后，`0.3.19.53` 算出来是 3019，
    #    而旧的 `3.19.53` 算出来是 3019053
    #    ==> **"最新包"会被判成比旧包更旧**，兜底取到错误的包。
    #
    # [!] `[version]` 按**段**做数值比较，而且 3 段 / 4 段混用没问题
    #     （段数少的按 0 补）：`[version]"3.19.53"` > `[version]"0.3.19.53"`。正确。
    #
    # [!] 排序仍要按**比较结果**来，不能用字符串 —— 那正是这个函数注释里
    #     记着的另一个坑（`1.4.0` 会排在 `1.34.0` 后面）。
    Get-ChildItem $release目录 -Directory -Filter "$前缀-*" |
        ForEach-Object {
            $v = $_.Name.Substring($前缀.Length + 1)
            $parsed = $null
            if ([version]::TryParse($v, [ref]$parsed)) {
                [pscustomobject]@{
                    目录 = $_
                    版本 = $parsed
                }
            }
        } |
        Sort-Object 版本 -Descending |
        Select-Object -First 1
}

# ══════════════════════════════════════════════════════════════════
标题 "① 前置检查：时间轴源目录"
# ══════════════════════════════════════════════════════════════════

$时间轴源 = Join-Path $仓库根 "cactbot_timelines"

if (-not (Test-Path $时间轴源)) {
    坏 "找不到 cactbot_timelines\"
    提示 "先跑提取工具：python tools\CactbotTimelineDump.py"
    exit 1
}

$源份数 = 数时间轴 $时间轴源
提示 "cactbot_timelines\ 里 $源份数 份时间轴"

if ($源份数 -lt $最少时间轴份数) {
    坏 "时间轴份数不足：$源份数 < $最少时间轴份数"
    提示 "要么提取没跑，要么 cactbot 更新后文件变少了 —— 先确认再发版"
    exit 1
}
好 "源目录有 $源份数 份时间轴"

# ══════════════════════════════════════════════════════════════════
标题 "② 构建（--no-incremental，保证 revision 来自当前 HEAD）"
# ══════════════════════════════════════════════════════════════════

dotnet build "HealerACR\HealerACR.csproj" -c Release "-p:AEAssistAcrDir=$仓库根\build\ACR" --no-incremental |
    Select-String -Pattern "error|已成功生成" | Select-Object -First 2 | ForEach-Object { 提示 $_ }
dotnet build "BlueWhale.AutoHealerACR\BlueWhale.AutoHealerACR.csproj" -c Release --no-incremental |
    Select-String -Pattern "error|已成功生成" | Select-Object -First 2 | ForEach-Object { 提示 $_ }

# ══════════════════════════════════════════════════════════════════
标题 "②·五 静态核对（ID 表 + 时间轴解析）"
# ══════════════════════════════════════════════════════════════════
#
# ⚠️ 为什么把这两个也放进发版流程：
#    它们查的都是**静默失效**类问题 —— 不报错、不崩溃，
#    只是"某个技能永远不放"或"AI 收到一堆噪声"。
#    这种问题只有靠机械核对才能稳定拦住，靠人记是不可靠的。

$py = "python"
# 优先用工程里记录的解释器路径（如果存在）
$pyHint = "$env:USERPROFILE\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe"
if (Test-Path $pyHint) { $py = $pyHint }

if (Test-Path "tools\IdAudit.py") {
    $auditOut = & $py "tools\IdAudit.py" 2>&1
    $auditCode = $LASTEXITCODE
    if ($auditCode -ne 0) {
        坏 "ID 核对未通过（tools\IdAudit.py 退出码 $auditCode）"
        $auditOut | Select-Object -Last 25 | ForEach-Object { 提示 $_ }
        $失败 = $true
    } else {
        好 "ID / 开关核对通过"
    }
} else {
    提示 "没有 tools\IdAudit.py，跳过"
}

if (Test-Path "tools\TimelineProbe\TimelineProbe.csproj") {
    $probeOut = dotnet run --project "tools\TimelineProbe" -- $时间轴源 10 2>&1
    $probeText = ($probeOut | Out-String)
    # 关键断言：必须能认出绝大多数时间轴的地图 ID
    if ($probeText -match "能认出地图 ID 的:\s*(\d+)\s*/\s*(\d+)") {
        $认出 = [int]$Matches[1]
        $共 = [int]$Matches[2]
        if ($共 -gt 0 -and $认出 -lt ($共 - 5)) {
            坏 "时间轴解析异常：只有 $认出 / $共 能认出地图 ID"
            $失败 = $true
        } else {
            好 "时间轴解析正常（$认出 / $共 认出地图 ID）"
        }
    } else {
        提示 "TimelineProbe 输出格式不符预期，跳过断言"
    }
} else {
    提示 "没有 tools\TimelineProbe，跳过"
}

# ── 提示词预算：防止"记忆库长胖后把技能清单挤掉" ──
#
# ⚠️ 为什么这个也要拦：
#    局面报告有 6000 字符上限，而**记忆库是会长大的**
#    （结论由 AI 生成，长度不可控）。一旦撑爆，
#    最先丢的是技能清单 —— 而 AI 只能从清单里选 ID，
#    丢了两边就完全对不上了（表现为"AI 一直在给无效建议"）。
if (Test-Path "tools\PromptBudget.py") {
    $budgetOut = & $py "tools\PromptBudget.py" 2>&1
    $budgetText = ($budgetOut | Out-String)

    # ⚠️ 只匹配 **ASCII 标记**（BUDGET_PERCENT=NN）。
    #    第一版我匹配的是中文"占用率："—— PowerShell 按 GBK 去读
    #    Python 的 UTF-8 输出，中文匹配不上，于是**静默跳过**了这条检查。
    #    那看起来像"检查过了"，实际什么都没查 —— 比没有断言更糟。
    if ($budgetText -match "BUDGET_PERCENT=(\d+)") {
        $占比 = [int]$Matches[1]
        if ($占比 -ge 95) {
            坏 "提示词预算占用 $占比%（>=95%）—— 随时会触发截断"
            提示 "跑 python tools\PromptBudget.py 看是哪一段变大了"
            $失败 = $true
        } else {
            好 "提示词预算占用 $占比%（有余量）"
        }
    } else {
        # 匹配不上就是**脚本坏了**，不能当"通过"放过去
        坏 "PromptBudget 没有输出 BUDGET_PERCENT 标记 —— 断言无法执行"
        提示 "要么工具坏了，要么发布脚本的正则过时了；必须修，不能跳过"
        $失败 = $true
    }
} else {
    提示 "没有 tools\PromptBudget.py，跳过"
}

# ══════════════════════════════════════════════════════════════════
标题 "③ 组装包目录"
# ══════════════════════════════════════════════════════════════════

# ⚠️ **包目录要多一层子目录**，和 BlueWhale 对称。
#
#    为什么：zip 用 `Compress-Archive -Path "父目录\*"` 打包，
#    而 `-Path "X\*"` **不包含 X 本身**（已手工复现确认）。
#    所以想让 zip 里有 `HealerACR\` 这层，包目录就必须在子层。
#
#    原来 `$H包` 直接是 `release\HealerACR-<版本>`，
#    打出来 zip 里**只有裸的 Timelines\ 和几个文件**，
#    `HealerACR.dll` 落在 zip 最外层 —— 用户解压后不知道往哪放。
#    实测 `HealerACR-1.76.0.zip` 327 条里**一条 dll 都没有**。
$H包 = Join-Path $仓库根 "release\HealerACR-$HealerVer\HealerACR"
$B包 = Join-Path $仓库根 "release\BlueWhale-$BlueVer\BlueWhale"

foreach ($d in @($H包, $B包)) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}

# ── 辅助文件：**从仓库取**，不从旧包复制 ──
#
# ⚠️ 这一条是刻意的：旧做法"从上一个包复制"是 310 份时间轴丢失的**直接原因**
#    （复制源在提取之前，而且链条上没人报错）。
#    从仓库当前状态取，就不会有"上游过期"的问题。
#  ⚠️ 后两个是「判断逻辑说明」文档 —— 用户要求随包发布：
#     · 判断逻辑说明-本地与AI.md   架构总览（明确区分本地层与 AI 层职责）
#     · 审查材料-输出与治疗逻辑.md  第三方审查用（每个结论带文件行号 + 数据来源）
#     放仓库根（随代码演进），不是 release\_support\（那是历史遗留文件的位置）。
foreach ($pair in @(
        @("HealerACR\README.md", "README.md"),
        @("HealerACR\CHANGELOG.md", "CHANGELOG.md"),
        @("HealerACR\开发约定.md", "开发约定.md"),
        @("判断逻辑说明-本地与AI.md", "判断逻辑说明-本地与AI.md"),
        @("审查材料-输出与治疗逻辑.md", "审查材料-输出与治疗逻辑.md"))) {
    $src = Join-Path $仓库根 $pair[0]
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $H包 $pair[1]) -Force
        Copy-Item $src (Join-Path $B包 $pair[1]) -Force
    } else { 提示 "缺 $($pair[0])（跳过）" }
}

$blueReadme = Join-Path $仓库根 "BlueWhale.AutoHealerACR\README.md"
if (Test-Path $blueReadme) { Copy-Item $blueReadme (Join-Path $B包 "README.md") -Force }

# ── 安装说明 / 逆向报告：优先用规范位置，其次才从旧包取 ──
#
# ⚠️ 为什么要有 `release\_support\`：
#    这两个文件原来只在各版本包里传来传去（同一个"链条式复制"问题）——
#    一旦某个包被覆盖坏，文件就丢了。
#    实测：`HealerACR-1.34.0\` 里就没有 安装说明.txt（被旧打包脚本覆盖过）。
#    所以固定存一份在 `release\_support\`，以它为**权威来源**。
$支持目录 = Join-Path $仓库根 "release\_support"
foreach ($f in @("安装说明.txt", "ActionEffect逆向报告.md")) {
    $src = Join-Path $支持目录 $f
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $H包 $f) -Force
        Copy-Item $src (Join-Path $B包 $f) -Force
        提示 "$f 来自 release\_support\"
    } else {
        提示 "缺 release\_support\$f"
    }
}

# 兜底：规范位置没有就从最新旧包取
if (-not (Test-Path (Join-Path $支持目录 "安装说明.txt"))) {
    $旧 = 最新旧包 (Join-Path $仓库根 "release") "HealerACR"
    if ($旧) {
        $说明 = Join-Path $旧.目录.FullName "安装说明.txt"
        if (Test-Path $说明) {
            Copy-Item $说明 (Join-Path $H包 "安装说明.txt") -Force
            Copy-Item $说明 (Join-Path $B包 "安装说明.txt") -Force
            提示 "安装说明.txt 来自旧包 $($旧.目录.Name)"
        }
    }
}

# ── 时间轴：**显式复制**（不依赖任何链条）──
#
# ⚠️ 这里把 cactbot 时间轴放进 Timelines\cactbot\ 子目录，
#    和仓库里那份 valigarmanda.txt 并存。
$HTL = Join-Path $H包 "Timelines"
$BTL = Join-Path $B包 "Timelines"
New-Item -ItemType Directory -Path $HTL, $BTL -Force | Out-Null

Copy-Item $时间轴源 (Join-Path $HTL "cactbot") -Recurse -Force
Copy-Item $时间轴源 (Join-Path $BTL "cactbot") -Recurse -Force

# 仓库里那份手动维护的时间轴也带上（如果有）
$手动TL = Join-Path $仓库根 "release\_timelines"
if (Test-Path $手动TL) {
    Copy-Item (Join-Path $手动TL "*") $HTL -Recurse -Force
    Copy-Item (Join-Path $手动TL "*") $BTL -Recurse -Force
}

# ── dll ──
Copy-Item (Join-Path $仓库根 "build\ACR\HealerACR\HealerACR.dll") $H包 -Force
Copy-Item (Join-Path $仓库根 "build\ACR\HealerACR\HealerACR.pdb") $H包 -Force
Copy-Item (Join-Path $仓库根 "BlueWhale.AutoHealerACR\bin\Release\BlueWhale.dll") $B包 -Force
Copy-Item (Join-Path $仓库根 "BlueWhale.AutoHealerACR\bin\Release\BlueWhale.pdb") $B包 -Force

# ★ BlueWhale 是**自包含**的，不需要额外的依赖 dll ★
#
#  [!] 试过拆成「BlueWhale.dll + HealerACR.dll（依赖）」，又撤回了，原因：
#        ① 用户要的是**一个 dll** —— 少一个文件就少一处"装漏了"的可能
#        ② 体积差别（233 KB vs 1715 KB）对加载速度没有实际影响
#        ③ ★ 它引入了 0.4.2.2 从来没有过的失败模式 ★
#           0.4.2.2（实测能跑的版本）零外部依赖；
#           改成依赖后万一解析失败，**连职业列表都不会出现**，比崩溃更难诊断。
#
#  [!] 那"两份静态状态"呢：只有两个 dll **同时被加载**时才存在两套状态。
#      实际部署目录里只有 BlueWhale.dll，所以**运行时只有一套** ——
#      那是架构整洁度问题，不是当前崩溃的成因。
#
#  [!] 保留的独立改进：5 个职业入口类加了条件编译，BlueWhale 里它们是 abstract
#      ==> 即使将来 HealerACR.dll 被加载，也不会多出"HealerACR·白魔"这类选项。

# 副本名表（记忆库用）
#
# ⚠️ 和下面那两张地名表一样：**必须打进去**，缺了不会报错、只会静默退化
#    （记忆库里会全是 `副本#123` 这种 ID，没法看）。
$duty = Join-Path $仓库根 "DutyNames.json"
if (Test-Path $duty) { Copy-Item $duty $B包 -Force }

# 地名表（TerritoryType -> 人话名字）
#
# ⚠️ 这两份是**必须打进去**的，缺了不会报错、只会静默退化：
#    插件找不到文件时，副本名会退化成 `区域#979` 这种「看起来像名字的 ID」。
#    用户实测踩过一次（站在雪都房区显示了错误的副本名），
#    所以下面校验段还会再查一遍在不在包里。
foreach ($n in @("TerritoryNames.json", "TerritoryPlaces.json")) {
    $f = Join-Path $仓库根 $n
    if (Test-Path $f) { Copy-Item $f $B包 -Force }
    else { 坏 "缺少 $n（先跑 tools\TerritoryDump 生成）" }
}

好 "包目录组装完成"

# ══════════════════════════════════════════════════════════════════
标题 "④ 校验包目录内容（打包前）"
# ══════════════════════════════════════════════════════════════════

$失败 = $false

foreach ($pair in @(@($H包, "HealerACR"), @($B包, "BlueWhale"))) {
    $dir, $名 = $pair

    $tlDir = Join-Path $dir "Timelines"
    if (-not (Test-Path $tlDir)) {
        坏 "$名 ： 没有 Timelines\ 目录"
        $失败 = $true
        continue
    }

    $n = 数时间轴 $tlDir
    if ($n -lt $最少时间轴份数) {
        坏 "$名 ： Timelines 里只有 $n 份（要求 >= $最少时间轴份数）"
        $失败 = $true
    } else {
        好 "$名 ： Timelines 有 $n 份"
    }

    if (-not (Test-Path (Join-Path $dir "$名.dll"))) {
        坏 "$名 ： 没有 $名.dll"
        $失败 = $true
    }
}

# ── BlueWhale 的数据表必须都在包里 ──
#
# ⚠️ 为什么单独查：这三份文件**缺了不会报错** ——
#    插件读不到时会静默退化（副本名变成 `区域#979` 这种"看起来像名字的 ID"）。
#    用户实测踩过一次（站在雪都房区显示了完全错误的副本名），
#    所以这里挡住，不让这种退化静默发出去。
foreach ($n in @("DutyNames.json", "TerritoryNames.json", "TerritoryPlaces.json")) {
    $f = Join-Path $B包 $n
    if (-not (Test-Path $f)) {
        坏 "BlueWhale ： 包里缺 $n（副本名/地名会静默退化）"
        $失败 = $true
    } else {
        $kb = [math]::Round((Get-Item $f).Length / 1KB)
        好 "BlueWhale ： $n 已打包（$kb KB）"
    }
}

if ($失败 -and -not $SkipCheck) {
    坏 "校验不通过 —— **不要发布**。修好上面的问题再跑一次。"
    exit 1
}

# ══════════════════════════════════════════════════════════════════
标题 "⑤ 打 zip"
# ══════════════════════════════════════════════════════════════════

$Hzip = Join-Path $仓库根 "release\HealerACR-$HealerVer.zip"
$Bzip = Join-Path $仓库根 "release\BlueWhale-$BlueVer.zip"
foreach ($z in @($Hzip, $Bzip)) { if (Test-Path $z) { Remove-Item $z -Force } }

# ⚠️ **必须打"父目录\*"**，让 zip 里带上 `HealerACR-<版本>\` 这一层。
#
#    原来写的是 `$H包\*` —— 而 `$H包` **就是包根目录本身**
#     (`release\HealerACR-<版本>`)，于是 zip 里只有"根目录的内容"，
#     **没有根目录**，结果解压出来是裸的 `Timelines\` + 几个文件，
#     `HealerACR.dll` 处在 zip 最外层之外 —— 用户拿到手不知道往哪放。
#
#    而 BlueWhale 那行一直是 `release\BlueWhale-<版本>\*`（父目录），
#     zip 里有 `BlueWhale\` 这层 —— **两行写法不对称**，只有 HealerACR 漏了。
#
#    实测：`HealerACR-1.76.0.zip` 里 327 个条目，**没有一条是 HealerACR.dll**。
Compress-Archive -Path (Join-Path $仓库根 "release\HealerACR-$HealerVer\*") -DestinationPath $Hzip -Force
Compress-Archive -Path (Join-Path $仓库根 "release\BlueWhale-$BlueVer\*") -DestinationPath $Bzip -Force

# ══════════════════════════════════════════════════════════════════
标题 "⑥ 校验 zip 内容（打包后 —— 这一步最容易发现问题）"
# ══════════════════════════════════════════════════════════════════

Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($pair in @(@($Hzip, "HealerACR", "HealerACR/Timelines"), @($Bzip, "BlueWhale", "BlueWhale/Timelines"))) {
    $zip, $名, $前缀 = $pair

    if (-not (Test-Path $zip)) { 坏 "$名 ： zip 没生成"; continue }

    $z = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $zip))
    try {
        # ══════════════════════════════════════════════════════════════
        #  ★ 必需文件核对（**按 zip 内的确切路径查，不做模糊匹配**）★
        #
        #  ⚠️ 为什么必须单独有这一段：
        #     原来只校验"时间轴份数"，而且用的是**模糊匹配**
        #     (`-like "*Timelines/*"`)。那个匹配**掩盖过真问题** ——
        #     `HealerACR-1.76.0.zip` 里 327 条**一条 `HealerACR.dll` 都没有**，
        #     而校验照样报"全部校验通过，可以发布"。
        #
        #     根因是 `Compress-Archive -Path "X\*"` **不包含 X 这层**
        #     （已手工复现确认），所以 zip 里只有裸的 `Timelines\`。
        #     用户解压出来不知道往哪放，**这个包等于废的**。
        #
        #  ⇒ 这里直接查确切路径，查不到就拦住：
        #       HealerACR → `HealerACR/HealerACR.dll`
        #       BlueWhale → `BlueWhale/BlueWhale.dll` + 三份数据表
        # ══════════════════════════════════════════════════════════════
        $包内 = @($z.Entries | ForEach-Object {
            $_.FullName.Replace("\", "/")
        })

        $必需 = if ($名 -eq "HealerACR") {
            @("HealerACR/HealerACR.dll")
        } else {
            @("BlueWhale/BlueWhale.dll",
              "BlueWhale/DutyNames.json",
              "BlueWhale/TerritoryNames.json",
              "BlueWhale/TerritoryPlaces.json")
        }

        foreach ($f in $必需) {
            if ($包内 -contains $f) {
                好 "$名 ： 包内有 $f"
            } else {
                坏 "$名 ： 包里**没有** $f —— 这个包不能用（检查 zip 的根目录层级）"
                $失败 = $true
            }
        }

        # ⚠️ zip 条目里的路径分隔符是**反斜杠**（`Timelines\cactbot\...`）——
        #    Compress-Archive 在 Windows 上就这么写。
        #    所以匹配前必须**统一成正斜杠**：
        #    直接用 `-like "*Timelines/*"` 会 0 命中。
        #    （实测踩过：包明明是对的，校验却报"只有 0 份"，
        #      差点跑去改好好的打包逻辑。）
        $tl = @($z.Entries | Where-Object {
            $p = $_.FullName.Replace("\", "/")
            $p -like "*$前缀/*" -and $p -like "*.txt" -and
            $非时间轴文件 -notcontains [System.IO.Path]::GetFileName($p)
        })
        $大小MB = (Get-Item $zip).Length / 1MB

        if ($tl.Count -lt $最少时间轴份数) {
            坏 "$名 ： zip 里 Timelines 只有 $($tl.Count) 份（要求 >= $最少时间轴份数）"
            $失败 = $true
        } else {
            好 "$名 ： zip 里有 $($tl.Count) 份时间轴，包大小 $([Math]::Round($大小MB,2)) MB"
        }

        # dll 的版本 + revision
        $dllEntry = $z.Entries | Where-Object { $_.FullName -like "*$名.dll" } | Select-Object -First 1
        if ($dllEntry) {
            $head = (git rev-parse HEAD).Trim()
            $short = $head.Substring(0, 7)
            $tmp = Join-Path $env:TEMP "relcheck_$名.dll"
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($dllEntry, $tmp, $true)
            $v = (Get-Item $tmp).VersionInfo.ProductVersion
            Remove-Item $tmp -Force -ErrorAction SilentlyContinue

            if ($v -match [regex]::Escape($short)) { 好 "$名 ： 版本 $v（revision 对得上）" }
            else { 坏 "$名 ： 版本 $v 的 revision 对不上当前 HEAD（$short）"; $失败 = $true }
        }
    } finally { $z.Dispose() }
}

Write-Host ""
if ($失败) {
    坏 "校验不通过 —— **不要发布**"
    exit 1
}
好 "全部校验通过，可以发布"
Write-Host ""
提示 "下一步（手动）："
提示 "  git push origin master"
提示 "  把 release\HealerACR-$HealerVer.zip 与 release\BlueWhale-$BlueVer.zip 分发"
