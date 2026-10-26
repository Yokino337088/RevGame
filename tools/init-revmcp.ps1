<#
.SYNOPSIS
    RevMCP 一键初始化：找 Python → 校验目录 → 真实 MCP 握手 → 生成客户端配置。

.DESCRIPTION
    为什么用 PowerShell 而不是 Python 脚本：
        RevMCP 本身要求机器上有一个 3.10+ 的 Python，而"机器上没有 Python"恰恰是
        最需要这份脚本的时候。用一个需要 Python 的脚本来检查 Python 是否可用，是死循环。

    脚本不做 pip 安装：RevMCP 是纯标准库实现，不需要任何第三方包。所谓"初始化依赖"其实是。
        1. 本机有一个 ≥3.10 的 Python（并拿到它的绝对路径）；
        2. Revolution.RevMCP 与 Revolution.RevCLI 两个目录同级存在；
        3. 客户端配置里的路径写对。

    第 3 点最容易错：反斜杠写错时客户端只会静默显示"连不上"。所以本脚本不靠
    "文件存在"就宣布成功，而是用将来客户端要执行的那条命令真拉起一次服务，
    走 initialize + tools/list，拿到工具列表才算通过。

.PARAMETER Write
    把条目合并进本机已安装的客户端配置（自动备份原文件）。不加这个参数只生成文件。

.PARAMETER Project
    Unity 工程路径。默认 <仓库>\RevGame_Client\UnityProject\RevGame.Unity。

.PARAMETER Out
    配置输出目录。默认 <仓库>\tools\revmcp-configs。

.PARAMETER SkipUnity
    跳过 Unity 连通性探测（机器上没打开编辑器、或 CI 环境时用）。

.EXAMPLE
    .\tools\init-revmcp.ps1

.EXAMPLE
    .\tools\init-revmcp.ps1 -Write
#>

[CmdletBinding()]
param(
    [switch]$Write,
    [string]$Project = "",
    [string]$Out = "",
    [switch]$SkipUnity
)

$ErrorActionPreference = 'Stop'

# 输出被重定向时用 UTF-8（管道/CI 才不会乱码）；双击窗口里保持控制台原编码，
# 否则在 GBK 控制台下强制 UTF-8 反而会显示成乱码。
if ([Console]::IsOutputRedirected) {
    try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
}

$script:Failures = New-Object System.Collections.Generic.List[string]
$ServerName = 'revolution'
$MinPython = [version]'3.10'

function Write-Ok   { param([string]$m) Write-Host "[OK] $m" }
function Write-Info { param([string]$m) Write-Host "[--] $m" }
function Write-Warn { param([string]$m) Write-Host "[!!] $m" -ForegroundColor Yellow }
function Write-Bad  { param([string]$m, [string]$fix = "")
    Write-Host "[XX] $m" -ForegroundColor Red
    $script:Failures.Add($m)
    if ($fix) { Write-Host "     → 处理：$fix" }
}

# 统一按 UTF-8 无 BOM 落盘：JSON/TOML 配置带 BOM 有客户端解析不了。
function Write-Utf8NoBom {
    param([string]$Path, [string]$Text)
    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}

function Read-TextAuto {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return "" }
    return [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
}

function Get-JsonString {
    # JSON 里统一用正斜杠 + 转义引号，绕开 Windows 反斜杠的转义地狱。
    param([string]$Value)
    $v = $Value -replace '\\', '/'
    $v = $v -replace '"', '\"'
    return '"' + $v + '"'
}

# ---------------------------------------------------------------- 1. 找 Python

function Invoke-PythonProbe {
    <#
      手工拼 Arguments 字符串而不用 -c 直接调用：
      PowerShell 把数组传给原生程序时只做空格拼接、不加引号，
      -c 后面的代码含空格就会被拆成多个参数。这里自己控制引号，结果确定。
    #>
    param([string]$Exe, [string[]]$PrefixArgs, [string]$Code)

    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    # 直接拼字符串，不用数组 + -join：数组元素被拆开会让 -c 的代码多出空格，
    # Python 会把它当成缩进块并报 IndentationError（这个问题实际踩过）。
    $prefix = ''
    if ($PrefixArgs) { $prefix = ($PrefixArgs -join ' ') + ' ' }
    $psi.Arguments = $prefix + '-c "' + $Code + '"'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    try {
        $proc = [Diagnostics.Process]::Start($psi)
        $stdout = $proc.StandardOutput.ReadToEnd()
        $proc.StandardError.ReadToEnd() | Out-Null
        $proc.WaitForExit(20000) | Out-Null
        if ($proc.ExitCode -ne 0) { return $null }
        return $stdout
    } catch {
        return $null
    }
}

function Resolve-Python {
    <#
      逐个尝试 py 启动器 / python / python3。
      必须拿到 sys.executable 而不是命令名，因为客户端配置里要写绝对路径。
      Windows 自带一个 WindowsApps\python.exe 的"应用执行别名"，它只会弹出应用商店，
      不能真正执行，必须排除。
    #>
    $candidates = New-Object System.Collections.Generic.List[object]
    if (Get-Command py -ErrorAction SilentlyContinue) {
        $candidates.Add(@{ Exe = 'py'; Prefix = @('-3') })
    }
    foreach ($name in @('python', 'python3')) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd -and $cmd.Source) { $candidates.Add(@{ Exe = $cmd.Source; Prefix = @() }) }
    }

    $notes = New-Object System.Collections.Generic.List[string]
    $probeCode = "import sys;print('%d.%d.%d' % sys.version_info[:3]);print(sys.executable)"
    foreach ($candidate in $candidates) {
        $raw = Invoke-PythonProbe -Exe $candidate.Exe -PrefixArgs $candidate.Prefix -Code $probeCode
        if (-not $raw) { continue }
        $lines = @($raw -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
        if ($lines.Count -lt 2) { continue }

        $version = $null
        try { $version = [version]$lines[0] } catch { continue }
        $exe = $lines[1]

        if ($exe -like '*\WindowsApps\*') {
            $notes.Add("跳过应用商店占位程序：$exe")
            continue
        }
        if (-not (Test-Path -LiteralPath $exe)) {
            $notes.Add("路径不存在：$exe")
            continue
        }
        if ($version -lt $MinPython) {
            $notes.Add("版本过低（$version）：$exe")
            continue
        }
        foreach ($note in $notes) { Write-Info $note }
        return @{ Exe = $exe; Version = $version }
    }
    foreach ($note in $notes) { Write-Info $note }
    return $null
}

# ---------------------------------------------------------------- 4. 握手

function Test-McpHandshake {
    <#
      按客户端将来执行的方式真正拉起一次服务，发 initialize + tools/list。
      这是"配置能连上"的唯一硬证据：文件存在、JSON 合法都不代表能握手。
    #>
    param([string]$Exe, [string]$RevMcp, [string]$UnityProject, [int]$TimeoutSeconds = 40)

    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = '"' + $RevMcp + '" --project "' + $UnityProject + '" serve'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [Text.Encoding]::UTF8

    try { $proc = [Diagnostics.Process]::Start($psi) }
    catch {
        Write-Bad "无法启动 RevMCP 进程：$($_.Exception.Message)" "检查 Python 路径是否可执行"
        return $false
    }

    # stderr 必须一直有人读：服务若写满管道缓冲区会阻塞，握手就会假超时。
    $errTask = $proc.StandardError.ReadToEndAsync()
    try {
        $requests = @(
            '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"init-revmcp","version":"1.0"}}}',
            '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}',
            '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
        )
        foreach ($line in $requests) {
            $proc.StandardInput.WriteLine($line)
            $proc.StandardInput.Flush()
        }

        $answers = @{}
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $task = $proc.StandardOutput.ReadLineAsync()
        while ((Get-Date) -lt $deadline -and $answers.Count -lt 2) {
            $remain = [int][Math]::Max(0, ($deadline - (Get-Date)).TotalMilliseconds)
            if ($remain -le 0) { break }
            if (-not $task.Wait([Math]::Min($remain, 500))) { continue }
            $line = $task.Result
            if ($null -eq $line) { break }
            try {
                $message = $line | ConvertFrom-Json -ErrorAction Stop
                if ($message.id -eq 1 -or $message.id -eq 2) { $answers[[int]$message.id] = $message }
            } catch { }
            $task = $proc.StandardOutput.ReadLineAsync()
        }
    } finally {
        try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
        try { $proc.WaitForExit(3000) | Out-Null } catch { }
    }

    $init = $answers[1]
    if (-not $init -or -not $init.result) {
        $detail = '无响应'
        if ($init -and $init.error) { $detail = "$($init.error.message)" }
        try {
            $stderr = $errTask.Result
            if ($stderr -and $stderr.Trim()) {
                $firstLine = ($stderr.Trim() -split "`r?`n")[0]
                $detail = "$detail / stderr: $firstLine"
            }
        } catch { }
        Write-Bad "MCP 握手失败：$detail" "手动执行 `"$Exe`" `"$RevMcp`" --project `"$UnityProject`" serve 看报错"
        return $false
    }

    $tools = @()
    if ($answers[2] -and $answers[2].result -and $answers[2].result.tools) { $tools = @($answers[2].result.tools) }
    if ($tools.Count -eq 0) {
        Write-Bad "MCP 握手成功但 tools/list 为空" "确认 Revolution.RevMCP 目录完整"
        return $false
    }
    $names = ($tools | Select-Object -First 5 | ForEach-Object { $_.name }) -join ', '
    Write-Ok ("MCP 握手通过：{0} v{1}，{2} 个工具（{3} …）" -f $init.result.serverInfo.name, $init.result.serverInfo.version, $tools.Count, $names)
    return $true
}

# ---------------------------------------------------------------- 5. 配置

function New-ClientConfigs {
    param([string]$OutDir, [string]$PythonExe, [string]$RevMcp, [string]$UnityProject)

    $script = (Get-JsonString $RevMcp)
    $proj = (Get-JsonString $UnityProject)
    $py = (Get-JsonString $PythonExe)

    $generic = @"
{
  "mcpServers": {
    "$ServerName": {
      "command": $py,
      "args": [
        $script,
        "--project",
        $proj,
        "serve"
      ]
    }
  }
}
"@

    # VS Code 的 .vscode/mcp.json 键名是 servers 且必须写 type，单独出一份免得用户来回改。
    $vscode = @"
{
  "servers": {
    "$ServerName": {
      "type": "stdio",
      "command": $py,
      "args": [
        $script,
        "--project",
        $proj,
        "serve"
      ]
    }
  }
}
"@

    # TOML 用单引号字面量：反斜杠原样保留，不需要转义。
    $toml = @"
[mcp_servers.$ServerName]
command = '$PythonExe'
args = ['$RevMcp', '--project', '$UnityProject', 'serve']
startup_timeout_sec = 60
tool_timeout_sec = 120
"@

    Write-Utf8NoBom (Join-Path $OutDir 'mcp-generic.json') $generic
    Write-Utf8NoBom (Join-Path $OutDir 'vscode-mcp.json') $vscode
    Write-Utf8NoBom (Join-Path $OutDir 'codex-config.toml') $toml
    Write-Ok "已生成 3 份客户端配置 → $OutDir"
    return @{ Generic = $generic; Toml = $toml }
}

function Backup-File {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $target = "$Path.bak-$stamp"
    $serial = 1
    # 同一秒内连跑两次不能互相覆盖备份，否则第一份原始内容就丢了。
    while (Test-Path -LiteralPath $target) { $target = "$Path.bak-$stamp-$serial"; $serial++ }
    Copy-Item -LiteralPath $Path -Destination $target
    return $target
}

function Merge-JsonConfig {
    param([string]$Path, [string]$EntryJson)

    $text = Read-TextAuto $Path
    $obj = $null
    if ($text.Trim()) {
        try { $obj = $text | ConvertFrom-Json -ErrorAction Stop }
        catch { Write-Warn "跳过 $Path：不是合法 JSON（$($_.Exception.Message)），请手动合并"; return }
        if ($null -eq $obj -or $obj -is [array]) { Write-Warn "跳过 $Path：顶层不是对象，请手动合并"; return }
    }
    if (-not $obj) { $obj = New-Object psobject }

    $backup = Backup-File $Path
    if (-not $obj.PSObject.Properties['mcpServers']) {
        $obj | Add-Member -MemberType NoteProperty -Name 'mcpServers' -Value (New-Object psobject)
    }
    $entry = $EntryJson | ConvertFrom-Json
    $existed = [bool]$obj.mcpServers.PSObject.Properties[$ServerName]
    $obj.mcpServers | Add-Member -MemberType NoteProperty -Name $ServerName -Value $entry -Force

    Write-Utf8NoBom $Path (($obj | ConvertTo-Json -Depth 12) + "`n")
    $note = if ($existed) { '已覆盖' } else { '已新增' }
    $suffix = if ($backup) { "，原文件备份 $(Split-Path -Leaf $backup)" } else { '（新建文件）' }
    Write-Ok "$note $ServerName 条目：$Path$suffix"
}

function Merge-TomlConfig {
    param([string]$Path, [string]$TomlBlock)

    $text = Read-TextAuto $Path
    if ($text -match "(?m)^\[mcp_servers\.$ServerName\]") {
        Write-Info "$Path 已有 [mcp_servers.$ServerName]，保持不变（如需更新请手动改）"
        return
    }
    $backup = Backup-File $Path
    if ($text -and -not $text.EndsWith("`n")) { $text += "`n" }
    $merged = $text + "`n" + $TomlBlock
    Write-Utf8NoBom $Path $merged
    $suffix = if ($backup) { "，原文件备份 $(Split-Path -Leaf $backup)" } else { '（新建文件）' }
    Write-Ok "已追加 [mcp_servers.$ServerName]：$Path$suffix"
}

function Add-ToInstalledClients {
    param([string]$GenericJson, [string]$TomlBlock)

    $home2 = [Environment]::GetFolderPath('UserProfile')
    $appData = [Environment]::GetFolderPath('ApplicationData')
    $jsonTargets = @(
        @{ Label = 'CodeBuddy / Claude Desktop / Cursor 类'; Path = (Join-Path $home2 '.codebuddy\mcp.json') },
        @{ Label = 'Cursor'; Path = (Join-Path $home2 '.cursor\mcp.json') },
        @{ Label = 'Claude Desktop'; Path = (Join-Path $appData 'Claude\claude_desktop_config.json') }
    )
    $touched = $false
    foreach ($target in $jsonTargets) {
        # 只处理本机真实存在的客户端，不凭空创建别人软件的配置目录。
        if (-not (Test-Path -LiteralPath $target.Path)) {
            Write-Info "$($target.Label)：未安装（$($target.Path) 不存在），已跳过"
            continue
        }
        Merge-JsonConfig -Path $target.Path -EntryJson $GenericJson
        $touched = $true
    }

    $codex = Join-Path $home2 '.codex\config.toml'
    if (Test-Path -LiteralPath $codex) {
        Merge-TomlConfig -Path $codex -TomlBlock $TomlBlock
        $touched = $true
    } else {
        Write-Info "Codex：未安装（$codex 不存在），已跳过"
    }

    if (-not $touched) { Write-Warn '没有检测到任何已安装的客户端；请用生成的 JSON 手动贴进你的客户端' }
}

function Test-UnityBridge {
    param([string]$PythonExe, [string]$RevMcp, [string]$UnityProject, [int]$TimeoutSeconds = 30)

    # Unity 没开时 discover 必然失败，这是正常使用状态，只提示不判失败。
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $PythonExe
    $psi.Arguments = '"' + $RevMcp + '" --project "' + $UnityProject + '" discover'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [Text.Encoding]::UTF8

    try {
        $proc = [Diagnostics.Process]::Start($psi)
        $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
        $stderrTask = $proc.StandardError.ReadToEndAsync()
        if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
            try { $proc.Kill() } catch { }
            Write-Warn 'Unity 探测超时（工程可能正在编译）；不影响配置生成'
            return
        }
        $out = $stdoutTask.Result
    } catch {
        Write-Warn "Unity 探测启动失败：$($_.Exception.Message)"
        return
    }

    try { $data = $out | ConvertFrom-Json -ErrorAction Stop }
    catch { Write-Warn 'Unity 探测没有返回 JSON；先忽略，等 Unity 打开后再验证'; return }

    if ($data.success) {
        $count = 0
        if ($data.actions) { $count = @($data.actions).Count }
        Write-Ok "已连上当前 Unity 工程，发现 $count 个真实能力（编辑器已打开且已编译）"
    } else {
        Write-Warn ("Unity 当前未就绪：{0}" -f "$($data.message)".Substring(0, [Math]::Min(120, "$($data.message)".Length)))
        Write-Info '这是正常状态——配置不需要 Unity 开着；等编辑器打开并编译完成后再验证即可'
    }
}

function Write-Guide {
    param([string]$PythonExe, [string]$RevMcp, [string]$UnityProject, [string]$OutDir, [bool]$HandshakeOk)

    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host '如何在你的 MCP 客户端里启用 Revolution'
    Write-Host ('=' * 78)
    Write-Host @"

核心只有一条命令（所有客户端填的都是它）：

    $PythonExe "$RevMcp" --project "$UnityProject" serve

客户端配置片段已生成到：
    $OutDir
        mcp-generic.json    → 绝大多数客户端（mcpServers 结构的 JSON）
        vscode-mcp.json     → VS Code 的 .vscode/mcp.json（servers + type: stdio）
        codex-config.toml   → OpenAI Codex 的 ~/.codex/config.toml

各客户端放哪里：

    客户端              配置文件位置                              用哪份片段
    ----------------    --------------------------------------    --------------
    CodeBuddy           ~/.codebuddy/mcp.json                     mcp-generic.json
    Cursor              ~/.cursor/mcp.json                        mcp-generic.json
    Claude Desktop      %APPDATA%\Claude\claude_desktop_config.json mcp-generic.json
    VS Code             <工程>/.vscode/mcp.json                    vscode-mcp.json
    Codex               ~/.codex/config.toml                      codex-config.toml
    其它 stdio 客户端   按其文档放进 servers / mcpServers 数组      mcp-generic.json

    说明：想省事就重跑 `.\tools\init-revmcp.ps1 -Write`，
          脚本会把条目合并进本机已安装的客户端，并自动备份原文件。

配置好之后怎么确认真的通了：

    1) 打开 Unity 工程 $(Split-Path -Leaf $UnityProject)，等右下角编译转圈结束；
    2) 命令行验证（不需要经过任何客户端）：
           $PythonExe "$RevMcp" --project "$UnityProject" discover
       返回 success=true 且 actions 里有真实条目，就说明桥是通的；
    3) 回到客户端重启 / 重新加载 MCP，让助手先调 rev_discover，
       再按返回的真实 action 名字调用，不要在提示里凭想象编能力名。

常见故障：

    · 客户端显示"已连接但没有工具"
        → 多半是 Python 路径写错。用本脚本生成的值，别手打反斜杠。
    · discover 报找不到工程 / 连接超时
        → Unity 没打开，或者工程还在编译；这不是 RevMCP 坏了。
    · 改了配置文件没生效
        → 客户端需要重启或手动 Reload；CodeBuddy / Cursor 可在设置里点刷新。
    · 担心误改工程
        → 写操作（编辑器改资产、GM 改数值）只有显式 confirm 才会执行；
          正式发行包里默认没有运行时测试接口。

"@
    # 这里只提示，不再调用 Write-Bad：真正的失败项已在前面各自登记过，
    # 重复记一次会让末尾的"需要处理"清单出现自指条目。
    if ($HandshakeOk) { Write-Ok '本机环境已经可用，只差把配置贴进客户端。' }
    else { Write-Host '[XX] 环境检查未通过，先修上面标 [XX] 的项，再重跑本脚本。' -ForegroundColor Red }
}

# ---------------------------------------------------------------- main

$repoRoot = Split-Path -Parent $PSScriptRoot
$unityRoot = Join-Path $repoRoot 'RevGame_Client\UnityProject'
$unityProject = if ($Project) { (Resolve-Path -LiteralPath $Project).Path } else { Join-Path $unityRoot 'RevGame.Unity' }
$revMcpDir = Join-Path $unityRoot 'Revolution.RevMCP'
$automationDir = Join-Path $unityRoot 'Revolution.RevCLI'
$revMcp = Join-Path $revMcpDir 'rev_mcp.py'
$outDir = if ($Out) { $Out } else { Join-Path $PSScriptRoot 'revmcp-configs' }

Write-Host 'RevMCP 一键初始化'
Write-Host "仓库根：$repoRoot"
Write-Host "Unity 工程：$unityProject"
Write-Host ('-' * 78)

Write-Host "`n[1/5] Python 运行环境"
$python = Resolve-Python
if ($python) {
    Write-Ok ("Python {0}（{1}）" -f $python.Version, $python.Exe)
    if ($python.Exe -like '* *') { Write-Info 'Python 路径含空格，配置里已作为完整字符串写入，无需手动加引号' }
} else {
    Write-Bad '没有找到可用的 Python 3.10+' '安装 Python 3.10+ 并勾选 "Add python.exe to PATH"：https://www.python.org/downloads/windows/'
}

Write-Host "`n[2/5] 目录与依赖文件"
$required = @(
    @{ Path = $revMcp; Label = 'RevMCP 入口' },
    @{ Path = (Join-Path $revMcpDir 'revmcp\server.py'); Label = 'MCP 服务实现' },
    @{ Path = (Join-Path $automationDir 'revautomation\client.py'); Label = 'Revolution.RevCLI 客户端（RevMCP 依赖它）' },
    @{ Path = (Join-Path $unityProject 'Assets'); Label = 'Unity 工程' }
)
$layoutOk = $true
foreach ($item in $required) {
    if (Test-Path -LiteralPath $item.Path) { Write-Ok "$($item.Label)：$($item.Path)" }
    else {
        $layoutOk = $false
        Write-Bad "$($item.Label) 缺失：$($item.Path)" '确认目录结构完整（Revolution.RevMCP 与 Revolution.RevCLI 必须同级）'
    }
}

Write-Host "`n[3/5] MCP 协议握手（真实拉起服务）"
$handshakeOk = $false
if ($python -and $layoutOk) {
    $handshakeOk = Test-McpHandshake -Exe $python.Exe -RevMcp $revMcp -UnityProject $unityProject
} else {
    Write-Info '前置检查未通过，跳过握手'
}

Write-Host "`n[4/5] 生成客户端配置"
if ($python -and $layoutOk) {
    $configs = New-ClientConfigs -OutDir $outDir -PythonExe $python.Exe -RevMcp $revMcp -UnityProject $unityProject
    if ($Write) { Add-ToInstalledClients -GenericJson $configs.Generic -TomlBlock $configs.Toml }
    else { Write-Info '未加 -Write：只生成文件，不改你机器上已有客户端配置' }
} else {
    # Python 不可用时不生成配置：把一条根本跑不起来的命令写进客户端配置，比不写更坑人。
    Write-Warn '前置检查未通过，已跳过配置生成（避免写出跑不起来的命令）'
}

Write-Host "`n[5/5] Unity 连通性探测"
if (-not $SkipUnity -and $python -and $layoutOk) {
    Test-UnityBridge -PythonExe $python.Exe -RevMcp $revMcp -UnityProject $unityProject
} else {
    Write-Info '已跳过 Unity 探测'
}

Write-Guide -PythonExe ($(if ($python) { $python.Exe } else { '<python.exe 的绝对路径>' })) -RevMcp $revMcp -UnityProject $unityProject -OutDir $outDir -HandshakeOk $handshakeOk

Write-Host ('-' * 78)
if ($script:Failures.Count -gt 0) {
    Write-Host "[XX] $($script:Failures.Count) 项需要处理：" -ForegroundColor Red
    foreach ($item in $script:Failures) { Write-Host "      - $item" }
    exit 1
}
Write-Ok '初始化完成，环境可用。'
exit 0
