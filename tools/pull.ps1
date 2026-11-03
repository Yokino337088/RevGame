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

function Git([string[]]$Arguments) {
    $output = & git @Arguments 2>&1
    return @{ Code = $LASTEXITCODE; Output = ($output | Out-String).Trim() }
}

$manifest = 'RevGame_Client/美术/第三方素材/字体/字体精简清单.json'

$dirty = (Git @('status', '--porcelain')).Output
$stashed = $false
if ($dirty) {
    Write-Host '[1/5] 本地有改动，先暂存（含未跟踪文件）…' -ForegroundColor Cyan
    $r = Git @('stash', 'push', '-u', '-m', "pull.ps1 自动暂存 $(Get-Date -Format 'yyyyMMdd-HHmmss')")
    if ($r.Code -ne 0) { throw "暂存失败：$($r.Output)" }
    $stashed = $true
}
else {
    Write-Host '[1/5] 工作区干净，跳过暂存' -ForegroundColor DarkGray
}

Write-Host "[2/5] 拉取 $Remote/$Branch …" -ForegroundColor Cyan
$pull = Git @('pull', '--tags', $Remote, $Branch)
Write-Host $pull.Output
if ($pull.Code -ne 0) {
    if ($stashed) { Write-Host '  拉取失败，正在把改动放回去…' -ForegroundColor Yellow; Git @('stash', 'pop') | Out-Null }
    throw '拉取失败，工作区已恢复'
}

if ($stashed) {
    Write-Host '[3/5] 恢复本地改动 …' -ForegroundColor Cyan
    $pop = Git @('stash', 'pop')
    if ($pop.Code -ne 0) {
        # 生成物冲突时不手工合并：取远端版本，稍后由脚本重新生成
        Write-Host '  清单等生成物冲突，取远端版本后重新生成' -ForegroundColor Yellow
        Git @('reset', '-q', '--', $manifest) | Out-Null
        Git @('checkout', '--', $manifest) | Out-Null
        if (-not (Git @('diff', '--name-only', '--diff-filter=U')).Output) {
            Git @('stash', 'drop') | Out-Null
        }
        Write-Host '  已解决，其余改动保留在 stash 中（git stash list 可查看）' -ForegroundColor Yellow
    }
    else {
        Write-Host $pop.Output
    }
}
else {
    Write-Host '[3/5] 无需恢复' -ForegroundColor DarkGray
}

Write-Host '[4/5] 重新生成字体精简清单 …' -ForegroundColor Cyan
$python = (Get-Command py -ErrorAction SilentlyContinue)
if ($python) {
    $font = & py -3.13 tools\font-subset.py 2>&1 | Out-String
    Write-Host "  $($font.Trim())"
}
else {
    Write-Host '  未找到 py 启动器，跳过；构建字库前请手动运行 python tools/font-subset.py' -ForegroundColor Yellow
}

Write-Host '[5/5] 当前状态' -ForegroundColor Cyan
$head = (Git @('log', '-1', '--format=%h %s')).Output
$counts = (Git @('rev-list', '--left-right', '--count', "$Remote/$Branch...HEAD")).Output
Write-Host "  HEAD     : $head"
Write-Host "  领先/落后: $counts"
$status = (Git @('-c', 'core.quotepath=off', 'status', '--short')).Output
if ($status) { Write-Host "  工作区：`n$status" } else { Write-Host '  工作区：干净' -ForegroundColor Green }
$stashes = (Git @('stash', 'list')).Output
if ($stashes) { Write-Host "  仍有暂存（确认后 git stash drop）：`n$stashes" -ForegroundColor Yellow }
