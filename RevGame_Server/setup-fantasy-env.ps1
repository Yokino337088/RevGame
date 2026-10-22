<#
.SYNOPSIS
    RevGame 服务端 —— Fantasy 开发环境一键检查 / 部署脚本。

.DESCRIPTION
    按 Fantasy 官方要求（https://github.com/qq362946/Fantasy）检查本机是否具备开发环境：
      .NET 8 SDK、git、Fantasy.Cli 全局工具、Unity 编辑器、MongoDB（可选）、协议与工具目录。

    默认行为 = 「检查 + 缺什么补什么」：
      · 缺 .NET 8 SDK   → 优先 winget 安装，没有 winget 则用官方 dotnet-install.ps1 装到用户目录
      · 缺 Fantasy.Cli  → dotnet tool install -g Fantasy.Cli
      · 缺 Tools 目录    → fantasy add -t fantasynet / -t fantasyunity 重新提取
      · 缺 Unity / Mongo → 只报告并给出下载地址（体积大、需要 Unity Hub / 安装器，不自动装）

    脚本是幂等的：环境齐全时再跑一遍不会做任何事，只输出全部 OK。

    ★ 为什么不静默失败：每一项检查都会落到 OK / WARN / FAIL 三态。
      WARN = 可以继续但会影响体验（比如 Unity 版本不一致）；FAIL = 阻塞开发，退出码为 1。

.PARAMETER CheckOnly
    只检查、不安装任何东西。适合 CI 或"先看看我缺什么"。

.PARAMETER Force
    即使已装 Fantasy.Cli 也强制升级到最新版（dotnet tool update -g）。

.PARAMETER SkipRestore
    跳过服务端解决方案的 NuGet 还原验证（离线环境或只想看工具链时用）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup-fantasy-env.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup-fantasy-env.ps1 -CheckOnly
#>
#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [switch]$Force,
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
# 关掉进度条：Invoke-WebRequest 在 PS 5.1 下画进度条会慢十倍
$ProgressPreference = 'SilentlyContinue'

$script:Root = $PSScriptRoot
$script:FantasyProjectDir = Join-Path $script:Root 'RevGame.Server'
$script:SolutionFile = Join-Path $script:FantasyProjectDir 'Server\Server.sln'
$script:ToolsDir = Join-Path $script:FantasyProjectDir 'Tools'
$script:DotnetToolsPath = Join-Path $env:USERPROFILE '.dotnet\tools'
$script:Results = New-Object 'System.Collections.Generic.List[object]'

# Fantasy CLI 首次运行会弹「请选择语言」的交互菜单；在脚本/CI 这种非交互终端里，
# 它会直接抛 NotSupportedException 并退出。官方留了环境变量入口，设上就跳过询问。
$env:FANTASY_CLI_LANG = 'Chinese'

# ================================================================ 输出小工具
function Write-Step {
    param([string]$Text)
    Write-Host ""
    Write-Host "== $Text ==" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Text)
    Write-Host "   [OK]   $Text" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Text)
    Write-Host "   [WARN] $Text" -ForegroundColor Yellow
}

function Write-Fail {
    param([string]$Text)
    Write-Host "   [FAIL] $Text" -ForegroundColor Red
}

# 剥掉 ANSI 颜色转义：fantasy CLI 的输出带 ESC[38;5;2m 这类控制码，
# 直接转述会在终端里显示成 \u001b[... 的乱码字符
function Clear-Ansi {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) {
        return ''
    }
    $esc = [regex]::Escape([string][char]27)
    return ($Text -replace "$esc\[[0-9;]*[A-Za-z]", '')
}

function Write-Info {
    param([string]$Text)
    Write-Host "          $(Clear-Ansi $Text)" -ForegroundColor DarkGray
}

# 记录一条检查结论，最后汇总用；Status 只有 OK / WARN / FAIL 三种
function Add-Result {
    param(
        [string]$Name,
        [ValidateSet('OK', 'WARN', 'FAIL')][string]$Status,
        [string]$Detail
    )
    $script:Results.Add([pscustomobject]@{ Name = $Name; Status = $Status; Detail = $Detail })
}

# 在 PATH 前面插一个目录（本次会话有效），用于刚装完工具后能立刻调用
function Add-ToSessionPath {
    param([string]$Directory)
    if ((Test-Path $Directory) -and ($env:PATH -notlike "*$Directory*")) {
        $env:PATH = "$Directory;$env:PATH"
        Write-Info "已临时加入本次会话 PATH：$Directory"
    }
}

# ================================================================ 检查项：.NET 8 SDK
function Get-DotnetSdkVersions {
    $exe = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $exe) {
        return @()
    }
    # 输出形如 "8.0.426 [C:\Program Files\dotnet\sdk]"，取版本号部分
    return @(& dotnet --list-sdks 2>$null | ForEach-Object {
        if ($_ -match '^\s*(\d+)\.(\d+)\.') { "$($Matches[1]).$($Matches[2])" }
    })
}

function Install-DotnetSdk8 {
    # 首选 winget：系统级安装，PATH 自动就绪，最省事
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($null -ne $winget) {
        Write-Info "使用 winget 安装 Microsoft.DotNet.SDK.8 ..."
        & winget install --id Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements --silent 2>&1 | ForEach-Object { Write-Info $_ }
        if ($LASTEXITCODE -eq 0) {
            return $true
        }
        Write-Warn "winget 安装失败（退出码 $LASTEXITCODE），改用官方 dotnet-install 脚本"
    }

    # 兜底：官方脚本装到用户目录。不需要管理员权限，但 PATH 要自己加
    $installDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
    $scriptFile = Join-Path $env:TEMP 'dotnet-install.ps1'
    try {
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $scriptFile -UseBasicParsing
        & powershell -NoProfile -ExecutionPolicy Bypass -File $scriptFile -Channel 8.0 -InstallDir $installDir
        Add-ToSessionPath $installDir
        return (Test-Path (Join-Path $installDir 'dotnet.exe'))
    }
    catch {
        Write-Warn "下载 dotnet-install 脚本失败：$($_.Exception.Message)"
        return $false
    }
}

function Test-DotnetSdk {
    Write-Step '1/6 检查 .NET SDK（Fantasy 要求 8.0+）'
    $sdks = Get-DotnetSdkVersions
    if ($sdks.Count -eq 0) {
        Write-Fail '未找到 dotnet 命令'
        if ($CheckOnly) {
            Add-Result '.NET SDK' 'FAIL' '未安装，请安装 .NET 8 SDK'
            return
        }
        if (Install-DotnetSdk8) {
            $sdks = Get-DotnetSdkVersions
            Write-Ok "已安装 .NET SDK：$($sdks -join ', ')"
            Add-Result '.NET SDK' 'OK' "本次安装，版本 $($sdks -join ', ')"
        }
        else {
            Write-Fail '自动安装 .NET 8 SDK 失败，请手动安装：https://dotnet.microsoft.com/download/dotnet/8.0'
            Add-Result '.NET SDK' 'FAIL' '自动安装失败'
        }
        return
    }

    Write-Ok "已安装 SDK 主版本：$($sdks -join ', ')"
    if ($sdks -contains '8.0') {
        Add-Result '.NET SDK' 'OK' ($sdks -join ', ')
        return
    }

    # 只有 9/10 也能编译（SDK 会自动还原 net8.0 targeting pack），但跑服务端需要 8 运行时
    Write-Warn '未发现 8.x SDK；服务端目标是 net8.0，建议补装 8.0 SDK（含 8.0 运行时）'
    if (-not $CheckOnly) {
        if (Install-DotnetSdk8) {
            $sdks = Get-DotnetSdkVersions
            Write-Ok "已补装 .NET 8 SDK，当前：$($sdks -join ', ')"
            Add-Result '.NET SDK' 'OK' "本次补装 8.0，当前 $($sdks -join ', ')"
            return
        }
    }
    Add-Result '.NET SDK' 'WARN' '缺少 8.x SDK，运行服务端会失败'
}

# ================================================================ 检查项：git
function Test-Git {
    Write-Step '2/6 检查 git'
    if ($null -ne (Get-Command git -ErrorAction SilentlyContinue)) {
        $version = (& git --version 2>$null)
        Write-Ok $version
        Add-Result 'git' 'OK' $version
        return
    }
    Write-Fail '未找到 git —— UPM 拉 Fantasy.Unity 包、克隆示例工程都需要它'
    Write-Info '下载：https://git-scm.com/download/win'
    Add-Result 'git' 'FAIL' '未安装'
}

# ================================================================ 检查项：Fantasy.Cli
# 读取 fantasy 版本号。CLI 会先打 ASCII banner，所以不能直接用输出原串，
# 从里面挑第一个形如 2026.1.2003 的版本串；读不到就返回空串，由调用方降级处理。
function Get-FantasyCliVersion {
    try {
        $raw = & fantasy --version 2>&1 | Out-String
    }
    catch {
        return ''
    }
    if ($raw -match '(\d{4}\.\d+\.\d+)') {
        return $Matches[1]
    }
    return ''
}

function Test-FantasyCli {
    Write-Step '3/6 检查 Fantasy.Cli 全局工具'
    # 刚装完工具时当前进程的 PATH 可能还没刷新，先补一次
    Add-ToSessionPath $script:DotnetToolsPath

    $cmd = Get-Command fantasy -ErrorAction SilentlyContinue
    if ($null -ne $cmd) {
        $version = Get-FantasyCliVersion
        if ($Force) {
            Write-Info '指定了 -Force，尝试升级 Fantasy.Cli ...'
            & dotnet tool update -g Fantasy.Cli 2>&1 | ForEach-Object { Write-Info $_ }
            $version = Get-FantasyCliVersion
        }
        if ([string]::IsNullOrEmpty($version)) {
            Write-Ok 'fantasy 已安装（版本号读取失败，命令本身可用）'
            Add-Result 'Fantasy.Cli' 'OK' '版本未知'
            return
        }
        Write-Ok "fantasy 已就绪：$version"
        Add-Result 'Fantasy.Cli' 'OK' "$version"
        return
    }

    if ($CheckOnly) {
        Write-Warn '未安装 Fantasy.Cli（只检查模式，不安装）'
        Add-Result 'Fantasy.Cli' 'WARN' '未安装'
        return
    }

    if ($null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Fail '没有 dotnet，无法安装 Fantasy.Cli'
        Add-Result 'Fantasy.Cli' 'FAIL' '缺少 dotnet'
        return
    }

    Write-Info '执行：dotnet tool install -g Fantasy.Cli ...'
    $output = & dotnet tool install -g Fantasy.Cli 2>&1
    $output | ForEach-Object { Write-Info $_ }
    Add-ToSessionPath $script:DotnetToolsPath

    if ($null -ne (Get-Command fantasy -ErrorAction SilentlyContinue)) {
        $version = Get-FantasyCliVersion
        Write-Ok "Fantasy.Cli 安装成功：$version"
        Add-Result 'Fantasy.Cli' 'OK' "本次安装，版本 $version"
    }
    else {
        Write-Fail '安装命令已执行但找不到 fantasy，请重开终端或手动把 %USERPROFILE%\.dotnet\tools 加入 PATH'
        Add-Result 'Fantasy.Cli' 'FAIL' '安装后不可用'
    }
}

# ================================================================ 检查项：Unity 编辑器
function Test-Unity {
    Write-Step '4/6 检查 Unity 编辑器（客户端需要，服务端可选）'
    $required = '2022.3.15f1c1'
    $versionFile = Join-Path $script:Root '..\RevGame_Client\UnityProject\RevGame.Unity\ProjectSettings\ProjectVersion.txt'
    if (Test-Path $versionFile) {
        $line = Select-String -Path $versionFile -Pattern 'm_EditorVersion:' | Select-Object -First 1
        if ($null -ne $line) {
            $required = ($line.Line -replace 'm_EditorVersion:\s*', '').Trim()
        }
    }

    # Unity Hub 的两处默认安装位置都扫；按版本号精确匹配工程要求
    $candidates = @()
    foreach ($base in @("$env:ProgramFiles\Unity\Hub\Editor", 'G:\Unity\Editor')) {
        if (Test-Path $base) {
            $candidates += Get-ChildItem $base -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name
        }
    }

    if ($candidates -contains $required) {
        Write-Ok "已安装工程所需版本：$required"
        Add-Result 'Unity 编辑器' 'OK' $required
        return
    }

    if ($candidates.Count -gt 0) {
        Write-Warn "未找到 $required，本机现有：$($candidates -join ', ')"
        Write-Info '用其它版本打开会改写 ProjectVersion.txt / 包锁定，团队协同会脏'
        Add-Result 'Unity 编辑器' 'WARN' "缺少 $required"
        return
    }

    Write-Warn "未检测到 Unity 编辑器（需要 $required）"
    Write-Info '通过 Unity Hub 安装：https://unity.com/download （服务端开发可先跳过）'
    Add-Result 'Unity 编辑器' 'WARN' '未检测到'
}

# ================================================================ 检查项：MongoDB（可选）
function Test-Mongo {
    Write-Step '5/6 检查 MongoDB（Fantasy 数据库，可选）'
    $service = Get-Service -Name '*mongo*' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $service) {
        Write-Ok "服务 $($service.Name)：$($service.Status)"
        Add-Result 'MongoDB' 'OK' "$($service.Name) / $($service.Status)"
        return
    }
    if ($null -ne (Get-Command mongod -ErrorAction SilentlyContinue)) {
        Write-Ok 'mongod 已安装（未注册为服务）'
        Add-Result 'MongoDB' 'OK' 'mongod 可用'
        return
    }
    Write-Warn '未检测到 MongoDB。Fantasy.config 默认模板使用 MongoDB 作 World 数据库，'
    Write-Info '接数据库前需要装（4.0+）：https://www.mongodb.com/try/download/community'
    Add-Result 'MongoDB' 'WARN' '未安装（用到数据库时必需）'
}

# ================================================================ 检查项：工程目录与 NuGet 还原
function Test-Project {
    Write-Step '6/6 检查服务端工程与工具目录'

    if (-not (Test-Path $script:SolutionFile)) {
        Write-Fail "找不到解决方案：$script:SolutionFile"
        Add-Result '服务端工程' 'FAIL' 'Server.sln 缺失'
        return
    }
    Write-Ok 'Server.sln 存在'

    $requiredTools = @('NetworkProtocol', 'ProtocolExportTool', 'NLog')
    $missing = @($requiredTools | Where-Object { -not (Test-Path (Join-Path $script:ToolsDir $_)) })
    if ($missing.Count -eq 0) {
        Write-Ok "工具目录齐全：$($requiredTools -join ', ')"
    }
    else {
        Write-Warn "缺少工具目录：$($missing -join ', ')"
        if (-not $CheckOnly) {
            foreach ($tool in $missing) {
                Write-Info "执行：fantasy add -t $($tool.ToLower()) -p ./RevGame.Server"
                & fantasy add -t $tool.ToLower() -p $script:FantasyProjectDir 2>&1 | ForEach-Object { Write-Info $_ }
            }
            $missing = @($requiredTools | Where-Object { -not (Test-Path (Join-Path $script:ToolsDir $_)) })
        }
        if ($missing.Count -eq 0) {
            Write-Ok '工具目录已补齐'
        }
        else {
            Write-Warn "仍缺少：$($missing -join ', ')"
        }
    }

    # 源码镜像：NuGet 包只带 dll，没有 pdb / SourceLink，读实现要靠这份源码
    $sourceMirror = Join-Path $script:ToolsDir 'Fantasy.Net'
    if (Test-Path $sourceMirror) {
        Write-Ok 'Fantasy.Net 源码镜像存在（本地可读框架实现）'
    }
    else {
        Write-Warn '缺少 Fantasy.Net 源码镜像，本地无法阅读框架实现'
        Write-Info '恢复命令：fantasy add -t fantasynet -p ./RevGame.Server'
    }

    if ($SkipRestore) {
        Write-Info '按参数跳过 NuGet 还原验证'
        Add-Result '服务端工程' 'OK' '已跳过还原'
        return
    }

    Write-Info '验证 NuGet 还原与编译（dotnet build）...'
    $buildOutput = & dotnet build $script:SolutionFile --nologo 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Ok '编译通过（0 错误）'
        Add-Result '服务端工程' 'OK' 'build 通过'
    }
    else {
        $buildOutput | Select-Object -Last 15 | ForEach-Object { Write-Info $_ }
        Write-Fail '编译失败，请查看上面的错误'
        Add-Result '服务端工程' 'FAIL' 'build 失败'
    }
}

# ================================================================ 主流程
Write-Host ''
Write-Host '============================================================' -ForegroundColor White
Write-Host ' RevGame 服务端 · Fantasy 开发环境一键部署' -ForegroundColor White
Write-Host " 模式：$(if ($CheckOnly) { '只检查' } else { '检查并自动补齐' })" -ForegroundColor White
Write-Host " 目录：$script:Root" -ForegroundColor White
Write-Host '============================================================' -ForegroundColor White

Test-DotnetSdk
Test-Git
Test-FantasyCli
Test-Unity
Test-Mongo
Test-Project

# ---------------- 汇总 ----------------
Write-Step '汇总'
$script:Results | ForEach-Object {
    $text = "{0,-12} {1,-5} {2}" -f $_.Name, $_.Status, $_.Detail
    switch ($_.Status) {
        'OK' { Write-Ok $text }
        'WARN' { Write-Warn $text }
        default { Write-Fail $text }
    }
}

$failed = @($script:Results | Where-Object { $_.Status -eq 'FAIL' }).Count
$warned = @($script:Results | Where-Object { $_.Status -eq 'WARN' }).Count

Write-Host ''
if ($failed -gt 0) {
    Write-Host "结论：有 $failed 项阻塞项、$warned 项提醒 —— 请按上面的提示处理。" -ForegroundColor Red
}
elseif ($warned -gt 0) {
    Write-Host "结论：核心环境可用，另有 $warned 项提醒（不影响服务端编译）。" -ForegroundColor Yellow
}
else {
    Write-Host '结论：Fantasy 开发环境完整，可以直接开工。' -ForegroundColor Green
}

Write-Host ''
Write-Host '常用命令：' -ForegroundColor White
Write-Host '  cd RevGame_Server/RevGame.Server' -ForegroundColor DarkGray
Write-Host '  dotnet build Server/Server.sln                        # 编译' -ForegroundColor DarkGray
Write-Host '  dotnet run --project Server/Main/Main.csproj          # 启动（launchSettings 带 --m Develop）' -ForegroundColor DarkGray
Write-Host '  Tools/ProtocolExportTool/Run.bat                      # 改完 .proto 后导出协议' -ForegroundColor DarkGray
Write-Host ''

if ($failed -gt 0) {
    exit 1
}
exit 0
