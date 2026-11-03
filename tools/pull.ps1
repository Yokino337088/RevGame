<#
.SYNOPSIS
    安全拉取 RevGame 仓库：自动暂存本地改动、拉取、恢复，并重新生成字体清单。

.DESCRIPTION
    为什么需要这个脚本：
    `字体精简清单.json` 是 tools/font-subset.py 的产物，里面记录每个源文件的 sha256。
    只要本地跑过导表、Unity 存过场景，清单哈希就会变，于是 `git pull` 会报
    “Your local changes ... would be overwritten by merge” 并中止。

    本脚本按固定顺序处理，不丢任何本地改动：
      1. 有改动就先 stash（含未跟踪文件）
      2. pull --tags
      3. stash pop 恢复
      4. 清单如果冲突，取远端版本后重新生成（不手工合并生成物）
      5. 报告当前状态

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\pull.ps1
#>
param(
    [string]$Remote = "origin",
    [string]$Branch = "main"
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

# 调用外部命令并原样拿到输出与退出码。
# 两个坑：1) 函数名不能叫 Git，PowerShell 不区分大小写，会和 git.exe 撞名递归；
#         2) git 会把正常提示（如 From https://…）写到 stderr，PS 5.1 在 Stop 模式下会当成异常抛出，
#            所以调用期间临时放宽错误处理，只看退出码。
function Invoke-Git([string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git.exe @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    return [pscustomobject]@{
        Code   = $code
        Output = (($output | ForEach-Object { "$_" }) -join "`n").Trim()
    }
}

$manifest = 'RevGame_Client/美术/第三方素材/字体/字体精简清单.json'

$status = Invoke-Git @('status', '--porcelain')
$stashed = $false
if ($status.Output) {
    Write-Host '[1/5] 本地有改动，先暂存（含未跟踪文件）…' -ForegroundColor Cyan
    $stash = Invoke-Git @('stash', 'push', '-u', '-m', "pull.ps1 自动暂存 $(Get-Date -Format 'yyyyMMdd-HHmmss')")
    if ($stash.Code -ne 0) { throw "暂存失败：$($stash.Output)" }
    $stashed = $true
}
else {
    Write-Host '[1/5] 工作区干净，跳过暂存' -ForegroundColor DarkGray
}

Write-Host "[2/5] 拉取 $Remote/$Branch …" -ForegroundColor Cyan
$pull = Invoke-Git @('pull', '--tags', $Remote, $Branch)
Write-Host "  $($pull.Output)"
if ($pull.Code -ne 0) {
    if ($stashed) {
        Write-Host '  拉取失败，正在把改动放回去…' -ForegroundColor Yellow
        Invoke-Git @('stash', 'pop') | Out-Null
    }
    throw '拉取失败，工作区已恢复'
}

if ($stashed) {
    Write-Host '[3/5] 恢复本地改动 …' -ForegroundColor Cyan
    $pop = Invoke-Git @('stash', 'pop')
    if ($pop.Code -ne 0) {
        # 生成物冲突不手工合并：取远端版本，稍后由本脚本重新生成
        Write-Host '  清单等生成物冲突，取远端版本后重新生成' -ForegroundColor Yellow
        Invoke-Git @('reset', '-q', '--', $manifest) | Out-Null
        Invoke-Git @('checkout', '--', $manifest) | Out-Null
        $unmerged = Invoke-Git @('diff', '--name-only', '--diff-filter=U')
        if (-not $unmerged.Output) {
            Invoke-Git @('stash', 'drop') | Out-Null
            Write-Host '  已解决，暂存已清空' -ForegroundColor Yellow
        }
        else {
            Write-Host "  仍有冲突，请手工处理：`n$($unmerged.Output)" -ForegroundColor Red
        }
    }
    else {
        Write-Host "  $($pop.Output)"
    }
}
else {
    Write-Host '[3/5] 无需恢复' -ForegroundColor DarkGray
}

Write-Host '[4/5] 重新生成字体精简清单 …' -ForegroundColor Cyan
if (Get-Command py -ErrorAction SilentlyContinue) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $font = & py -3.13 tools\font-subset.py 2>&1 | Out-String } finally { $ErrorActionPreference = $previous }
    Write-Host "  $($font.Trim())"
}
else {
    Write-Host '  未找到 py 启动器，跳过；构建字库前请手动运行 python tools/font-subset.py' -ForegroundColor Yellow
}

Write-Host '[5/5] 当前状态' -ForegroundColor Cyan
Write-Host "  HEAD      : $((Invoke-Git @('log', '-1', '--format=%h %s')).Output)"
Write-Host "  领先/落后 : $((Invoke-Git @('rev-list', '--left-right', '--count', "$Remote/$Branch...HEAD")).Output)"
$now = Invoke-Git @('-c', 'core.quotepath=off', 'status', '--short')
if ($now.Output) { Write-Host "  工作区：`n$($now.Output)" } else { Write-Host '  工作区：干净' -ForegroundColor Green }
$stashList = Invoke-Git @('stash', 'list')
if ($stashList.Output) { Write-Host "  仍有暂存（确认后 git stash drop）：`n$($stashList.Output)" -ForegroundColor Yellow }
