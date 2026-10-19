<#
.SYNOPSIS
    Revolution 框架本体的「增量」同步脚本（默认只预演，不落盘）。

.DESCRIPTION
    把 Revolution 仓库 package 分支上「相对上一次安装有变化」的文件，增量覆盖到
    <Unity工程>/Assets/Revolution —— 只覆盖变化文件，**不删除**本地文件、**不镜像整目录**。

    为什么要这样做（血泪教训）：
      Assets/Revolution 里混着「框架源」和「本项目生成物」。生成物同名但内容不同，
      一旦整目录覆盖就会立刻编译失败（2026-10-19 就踩过：RevResPath.cs 被换成框架默认的
      7 个常量，本项目 56 个常量全丢 → 8 处 CS0117）。

    幂等：先比内容（SHA256），一致就跳过；重复执行不会产生任何改动。

.PARAMETER Apply
    真正把文件写进工程。不写这个参数 = DryRun（只打印计划）。

.PARAMETER Baseline
    基线提交（package 分支上的某个 commit）。默认读
    <Unity工程>/Assets/Revolution/PACKAGE_VERSION.txt 里的 commit 字段。

.PARAMETER PackageRef
    要同步到的目标引用，默认 origin/package。

.PARAMETER FrameworkRepo
    Revolution 仓库克隆路径。默认 <本仓库上一级>\RevolutionFramework。

.PARAMETER UnityProject
    Unity 工程路径。默认 <本仓库>\RevGame_Client\UnityProject\RevGame.Unity。

.PARAMETER NoFetch
    跳过 git fetch（离线 / 已手动 fetch 时用）。

.PARAMETER AllowDelete
    允许删除「上游已删除」的文件。默认不删，只列出来让人确认。

.EXAMPLE
    # 1) 预演：看看这次升级会覆盖哪些文件（什么都不改）
    .\tools\sync-framework.ps1

.EXAMPLE
    # 2) 确认无误后真正落盘
    .\tools\sync-framework.ps1 -Apply

.EXAMPLE
    # 3) 基线文件丢了，手动指定上次安装的提交
    .\tools\sync-framework.ps1 -Baseline ae5c3c0
#>

[CmdletBinding()]
param(
    [switch]$Apply,
    [string]$Baseline = "",
    [string]$PackageRef = "origin/package",
    [string]$FrameworkRepo = "",
    [string]$UnityProject = "",
    [switch]$NoFetch,
    [switch]$AllowDelete
)

$ErrorActionPreference = "Stop"

# ============================================================
#  保护清单：本项目生成的产物，永远不覆盖（路径相对 Assets/Revolution）
#  以后新增生成物，往这里加一行即可。
# ============================================================
$Protect = @(
    "Generation/RevResPath.cs",
    "Runtime/RevSoundSystem/Generated/RevSoundPath.cs",
    "Resources/ResourceSystem/ResMap.txt",
    "PACKAGE_VERSION.txt"
)

function Write-Step([string]$Text)
{
    Write-Host ""
    Write-Host ("== " + $Text) -ForegroundColor Cyan
}

function Get-Sha256([string]$Path)
{
    if (-not (Test-Path -LiteralPath $Path))
    {
        return ""
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Show-List([string[]]$Items, [string]$Prefix, [int]$Max = 15)
{
    if (-not $Items -or $Items.Count -eq 0)
    {
        return
    }
    $i = 0
    foreach ($it in $Items)
    {
        $i++
        if ($i -gt $Max)
        {
            Write-Host ("      … 还有 " + ($Items.Count - $Max) + " 个")
            break
        }
        Write-Host ("      " + $Prefix + " " + $it)
    }
}

# ------------------------------------------------------------
#  0. 解析路径
# ------------------------------------------------------------
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $FrameworkRepo)
{
    $FrameworkRepo = Join-Path (Split-Path -Parent $repoRoot) "RevolutionFramework"
}
if (-not $UnityProject)
{
    $UnityProject = Join-Path $repoRoot "RevGame_Client\UnityProject\RevGame.Unity"
}
$frameworkRoot = Join-Path $UnityProject "Assets\Revolution"
$anchorFile = Join-Path $frameworkRoot "PACKAGE_VERSION.txt"

Write-Step "路径检查"
Write-Host ("  Revolution 仓库 : " + $FrameworkRepo)
Write-Host ("  Unity 工程      : " + $UnityProject)
Write-Host ("  目标目录        : " + $frameworkRoot)

if (-not (Test-Path -LiteralPath (Join-Path $FrameworkRepo ".git")))
{
    throw ("找不到 Revolution 仓库（缺 .git）：" + $FrameworkRepo + "；请用 -FrameworkRepo 指定。")
}
if (-not (Test-Path -LiteralPath $frameworkRoot))
{
    throw ("找不到目标目录：" + $frameworkRoot + "；请用 -UnityProject 指定 Unity 工程根。")
}

# ------------------------------------------------------------
#  1. 确定基线
# ------------------------------------------------------------
Write-Step "读取基线"
if (-not $Baseline)
{
    if (-not (Test-Path -LiteralPath $anchorFile))
    {
        throw ("基线未知：没传 -Baseline，也找不到 " + $anchorFile + "。请用 -Baseline <提交> 指定上次安装的 package 提交。")
    }
    $anchorText = Get-Content -LiteralPath $anchorFile -Raw -Encoding UTF8
    $m = [regex]::Match($anchorText, "(?m)^\s*commit\s*=\s*([0-9a-fA-F]{7,40})\s*$")
    if (-not $m.Success)
    {
        throw ("基线文件里找不到 commit 字段：" + $anchorFile)
    }
    $Baseline = $m.Groups[1].Value
    Write-Host ("  来自 PACKAGE_VERSION.txt : " + $Baseline)
}
else
{
    Write-Host ("  来自命令行 -Baseline     : " + $Baseline)
}

# ------------------------------------------------------------
#  2. fetch + 解析目标
# ------------------------------------------------------------
if (-not $NoFetch)
{
    Write-Step "git fetch origin package"
    & git -C $FrameworkRepo fetch origin package --quiet
    if ($LASTEXITCODE -ne 0)
    {
        throw ("git fetch 失败（exit=" + $LASTEXITCODE + "）。网络抽风时可稍后重试，或加 -NoFetch 跳过。")
    }
    Write-Host "  fetch 完成"
}

$baselineSha = (& git -C $FrameworkRepo rev-parse $Baseline).Trim()
if ($LASTEXITCODE -ne 0 -or -not $baselineSha)
{
    throw ("无法解析基线提交：" + $Baseline + "（可能还没 fetch 到）")
}
$targetSha = (& git -C $FrameworkRepo rev-parse $PackageRef).Trim()
if ($LASTEXITCODE -ne 0 -or -not $targetSha)
{
    throw ("无法解析目标引用：" + $PackageRef)
}

Write-Step "版本对比"
Write-Host ("  基线（已安装） : " + $baselineSha.Substring(0, 7) + "    " + $Baseline)
Write-Host ("  目标（同步到） : " + $targetSha.Substring(0, 7) + "    " + $PackageRef)

if ($baselineSha -eq $targetSha)
{
    Write-Host ""
    Write-Host "已是最新，无需同步 ✓" -ForegroundColor Green
    exit 0
}

# ------------------------------------------------------------
#  3. 用临时 worktree 把目标版本铺开（比逐个 git show 更稳，二进制安全）
# ------------------------------------------------------------
$tmpRoot = Join-Path $env:TEMP ("revpkg_" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$worktreeAdded = $false
$exitCode = 0

try
{
    Write-Step "准备目标版本文件（git worktree，只读临时目录）"
    & git -C $FrameworkRepo worktree add --detach --quiet $tmpRoot $targetSha
    if ($LASTEXITCODE -ne 0)
    {
        throw ("git worktree add 失败（exit=" + $LASTEXITCODE + "）")
    }
    $worktreeAdded = $true
    Write-Host ("  临时目录 : " + $tmpRoot)

    # ---------------- 4. 计算计划 ----------------
    Write-Step "计算差异（git diff --name-status）"
    $diffLines = & git -C $FrameworkRepo diff --name-status "$baselineSha..$targetSha"
    if ($LASTEXITCODE -ne 0)
    {
        throw ("git diff 失败（exit=" + $LASTEXITCODE + "）")
    }

    $planAdd = New-Object System.Collections.Generic.List[string]
    $planUpdate = New-Object System.Collections.Generic.List[string]
    $planSame = New-Object System.Collections.Generic.List[string]
    $planProtected = New-Object System.Collections.Generic.List[string]
    $planDelete = New-Object System.Collections.Generic.List[string]

    foreach ($line in $diffLines)
    {
        if (-not $line -or -not $line.Trim())
        {
            continue
        }
        $cols = $line -split "`t"
        $status = $cols[0]
        $rel = ""

        if ($status.StartsWith("R") -or $status.StartsWith("C"))
        {
            if ($cols.Count -lt 3)
            {
                continue
            }
            $planDelete.Add($cols[1])
            $rel = $cols[2]
        }
        elseif ($cols.Count -ge 2)
        {
            $rel = $cols[1]
        }
        else
        {
            continue
        }

        if ($Protect -contains $rel)
        {
            $planProtected.Add($rel)
            continue
        }

        if ($status.StartsWith("D"))
        {
            $planDelete.Add($rel)
            continue
        }

        $src = Join-Path $tmpRoot $rel
        $dst = Join-Path $frameworkRoot $rel
        $dstExists = Test-Path -LiteralPath $dst
        if ($dstExists -and ((Get-Sha256 $src) -eq (Get-Sha256 $dst)))
        {
            $planSame.Add($rel)
        }
        elseif ($dstExists)
        {
            $planUpdate.Add($rel)
        }
        else
        {
            $planAdd.Add($rel)
        }
    }

    # ---------------- 5. 报告 ----------------
    $modeText = "DryRun（预演，不落盘）"
    if ($Apply)
    {
        $modeText = "Apply（真正写入）"
    }
    Write-Step ("同步计划 · " + $modeText)

    if ($planAdd.Count -gt 0)
    {
        Write-Host ("  新增 " + $planAdd.Count + " 个文件：") -ForegroundColor Green
        Show-List $planAdd "+"
    }
    if ($planUpdate.Count -gt 0)
    {
        Write-Host ("  覆盖 " + $planUpdate.Count + " 个文件：") -ForegroundColor Yellow
        Show-List $planUpdate "~"
    }
    if ($planProtected.Count -gt 0)
    {
        Write-Host ("  保护跳过 " + $planProtected.Count + " 个（本项目生成物，永不覆盖）：") -ForegroundColor Magenta
        Show-List $planProtected "!"
    }
    if ($planSame.Count -gt 0)
    {
        Write-Host ("  内容相同、跳过 " + $planSame.Count + " 个")
    }
    if ($planDelete.Count -gt 0)
    {
        Write-Host ("  上游已删除 " + $planDelete.Count + " 个（默认不动，需 -AllowDelete 才删）：") -ForegroundColor Red
        Show-List $planDelete "-"
    }

    Write-Host ""
    Write-Host ("  合计：新增 " + $planAdd.Count + " / 覆盖 " + $planUpdate.Count + " / 未变 " + $planSame.Count + " / 保护 " + $planProtected.Count + " / 待确认删除 " + $planDelete.Count)

    # ---------------- 6. 落盘 ----------------
    if ($Apply)
    {
        Write-Step "写入工程"
        $written = 0
        foreach ($rel in ($planAdd + $planUpdate))
        {
            $src = Join-Path $tmpRoot $rel
            $dst = Join-Path $frameworkRoot $rel
            $dstDir = Split-Path -Parent $dst
            if (-not (Test-Path -LiteralPath $dstDir))
            {
                New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
            }
            Copy-Item -LiteralPath $src -Destination $dst -Force
            $written++
        }
        Write-Host ("  已写入 " + $written + " 个文件")

        $deleted = 0
        if ($AllowDelete)
        {
            foreach ($rel in $planDelete)
            {
                $dst = Join-Path $frameworkRoot $rel
                if (Test-Path -LiteralPath $dst)
                {
                    Remove-Item -LiteralPath $dst -Force
                    $deleted++
                }
            }
            if ($deleted -gt 0)
            {
                Write-Host ("  已删除 " + $deleted + " 个上游移除的文件")
            }
        }

        # ---------------- 7. 更新版本锚点 ----------------
        Write-Step "更新版本锚点 PACKAGE_VERSION.txt"
        $now = (Get-Date).ToString("yyyy-MM-dd HH:mm")
        $anchorContent = @"
# ============================================================
#  Revolution 框架本体 · 安装基线（Package Version Anchor）
# ============================================================
#
#  这个文件记录「Assets/Revolution 当前对应 Revolution 仓库的哪个 package 提交」。
#  作用：tools/sync-framework.ps1 用它当基线做**增量**更新 —— 只覆盖有变化的文件，
#        绝不整目录镜像，从而避免把本项目生成的数据覆盖掉。
#
#  ⚠️ 以下文件永远不要被同步覆盖（脚本已内置保护清单）：
#     · Generation/RevResPath.cs                          本项目资源目录常量
#     · Runtime/RevSoundSystem/Generated/RevSoundPath.cs  本项目音效目录常量
#     · Resources/ResourceSystem/ResMap.txt               本项目资源映射表
#     · PACKAGE_VERSION.txt（本文件）
# ============================================================

commit      = $targetSha
short       = $($targetSha.Substring(0, 7))
date        = $now
source      = $PackageRef（Revolution 仓库 package 分支）
previous    = $baselineSha
updated_by  = tools/sync-framework.ps1
"@
        [IO.File]::WriteAllText($anchorFile, $anchorContent + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host ("  已更新：" + $anchorFile)
        Write-Host ""
        Write-Host "下一步：让 Unity 重新编译，确认 Console 0 error；" -ForegroundColor Green
        Write-Host "        并抽查 Generation/RevResPath.cs 常量数未变（脚本不会碰它）。" -ForegroundColor Green
    }
    else
    {
        Write-Host ""
        Write-Host "这是预演，未改动任何文件。确认无误后执行：" -ForegroundColor Green
        Write-Host "    .\tools\sync-framework.ps1 -Apply" -ForegroundColor Green
    }
}
catch
{
    Write-Host ""
    Write-Host ("✗ 失败：" + $_.Exception.Message) -ForegroundColor Red
    $exitCode = 1
}
finally
{
    # 清理临时 worktree：临时目录常被杀软/索引器占用，**任何清理失败都不能影响退出码**
    $ErrorActionPreference = "SilentlyContinue"

    if ($worktreeAdded)
    {
        try
        {
            & git -C $FrameworkRepo worktree remove --force $tmpRoot 2>&1 | Out-Null
        }
        catch
        {
            # 忽略：下面还有兜底删除 + prune
        }
    }

    if (Test-Path -LiteralPath $tmpRoot)
    {
        for ($i = 0; $i -lt 3 -and (Test-Path -LiteralPath $tmpRoot); $i++)
        {
            Remove-Item -LiteralPath $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $tmpRoot)
            {
                Start-Sleep -Milliseconds 500
            }
        }
    }

    # 清掉可能残留的 worktree 注册（目录没删干净时 git 会一直记着它）
    try
    {
        & git -C $FrameworkRepo worktree prune 2>&1 | Out-Null
    }
    catch
    {
        # 忽略
    }
}

exit $exitCode
