param(
    [ValidateRange(1, 65535)][int]$OuterPort,
    [ValidateRange(1, 65535)][int]$InnerBasePort
)

$ErrorActionPreference = 'Stop'
$serverProject = Join-Path $PSScriptRoot 'Server/Main/Main.csproj'
$sourceConfig = Join-Path $PSScriptRoot 'Server/Entity/Fantasy.config'
$builtDirectory = Join-Path $PSScriptRoot 'Server/Main/bin/Debug/net8.0'

function Get-PortOwner($endpoint)
{
    # 只在失败时查 PID，查询不可用时仍报告原始绑定错误。
    try
    {
        $owners = if ($endpoint.Protocol -eq 'Tcp')
        {
            Get-NetTCPConnection -State Listen -LocalPort $endpoint.Port -ErrorAction SilentlyContinue
        }
        else
        {
            Get-NetUDPEndpoint -LocalPort $endpoint.Port -ErrorAction SilentlyContinue
        }
        $owners | Where-Object {
            $_.LocalAddress -in @($endpoint.Address, '0.0.0.0', '::') -or $endpoint.Address -in @('0.0.0.0', '::')
        } | ForEach-Object {
            $process = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
            "PID $($_.OwningProcess) $($process.ProcessName) [$($_.LocalAddress):$($_.LocalPort)]"
        }
    }
    catch
    {
        # 端口探测仍可独立工作，例如在没有 Windows CIM 命令的环境。
    }
}

function Test-ListenPorts($endpoints)
{
    $sockets = [Collections.Generic.List[Net.Sockets.Socket]]::new()
    try
    {
        foreach ($endpoint in $endpoints)
        {
            $address = [Net.IPAddress]::Parse($endpoint.Address)
            $isTcp = $endpoint.Protocol -eq 'Tcp'
            $socketType = if ($isTcp) { 'Stream' } else { 'Dgram' }
            $socket = [Net.Sockets.Socket]::new($address.AddressFamily, $socketType, $endpoint.Protocol)
            $sockets.Add($socket)
            try
            {
                $socket.SetSocketOption([Net.Sockets.SocketOptionLevel]::Socket, [Net.Sockets.SocketOptionName]::ReuseAddress, $false)
                if ($address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6)
                {
                    $socket.DualMode = $true
                }
                $socket.Bind([Net.IPEndPoint]::new($address, $endpoint.Port))
                if ($isTcp)
                {
                    $socket.Listen(1)
                }
            }
            catch
            {
                $owners = @(Get-PortOwner $endpoint)
                $ownerText = if ($owners.Count) { $owners -join ', ' } else { '未查询到监听进程，请核对系统保留端口或重复监听。' }
                $option = if ($endpoint.Role -eq '内网') { '-InnerBasePort' } else { '-OuterPort' }
                throw ("Scene {0} {1} 无法绑定 {2} {3}:{4}。{5}{6}{5}{7}{5}可使用已有实例，或用 {8} 指定空闲端口。" -f
                    $endpoint.Scene, $endpoint.Role, $endpoint.Protocol.ToUpperInvariant(), $endpoint.Address, $endpoint.Port,
                    [Environment]::NewLine, $ownerText, $_.Exception.GetBaseException().Message, $option)
            }
        }
    }
    finally
    {
        # 同时持有所有探测套接字，才能发现同一份配置里的重复监听。正式监听由 Fantasy 创建。
        foreach ($socket in $sockets)
        {
            $socket.Dispose()
        }
    }
}

$exitCode = 1
$instance = $null
Push-Location $PSScriptRoot
try
{
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue))
    {
        throw '未检测到 dotnet。源码启动请安装 .NET 8 SDK，运行已构建版本请安装 .NET 8 ASP.NET Core Runtime。'
    }
    $runtimes = @(dotnet --list-runtimes)
    foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'))
    {
        if (-not ($runtimes -match ('^' + [regex]::Escape($framework) + ' 8\.')))
        {
            throw "缺少 $framework 8.x。请安装 .NET 8 ASP.NET Core Runtime 或 .NET 8 SDK。"
        }
    }
    $sdks = @(dotnet --list-sdks | Where-Object { $_ -match '^\d+\.' -and [int]($_ -split '\.')[0] -ge 8 })
    $build = $sdks.Count -gt 0 -and (Test-Path -LiteralPath $serverProject -PathType Leaf)
    $builtConfig = Join-Path $builtDirectory 'Fantasy.config'
    $configPath = if ($build) { $sourceConfig } else { $builtConfig }
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf))
    {
        throw '缺少 Fantasy.config。请安装 .NET 8 SDK 构建源码，或复制完整且同版本的 Debug/net8.0 产物。'
    }
    if (-not $build -and (Test-Path -LiteralPath $sourceConfig -PathType Leaf) -and
        (Get-FileHash -LiteralPath $builtConfig).Hash -ne (Get-FileHash -LiteralPath $sourceConfig).Hash)
    {
        throw '编译产物中的 Fantasy.config 与当前源码不同。请安装 .NET 8 SDK 重新构建，或复制同版本的完整产物。'
    }

    $config = [xml]::new()
    $config.Load($configPath)
    $root = $config.DocumentElement
    $localServer = $root.server
    $machines = @($localServer.machines.machine)
    $processes = @($localServer.processes.process)
    $scenes = @($localServer.scenes.scene)
    if ($root.controlCenter.enabled -eq 'true' -or $machines.Count -ne 1 -or $processes.Count -ne 1 -or -not $scenes[0])
    {
        throw '此开发脚本需要本地单机、单进程的 Fantasy 配置。'
    }
    $machine = $machines[0]
    $outerScenes = @($scenes | Where-Object { [int]$_.outerPort -gt 0 })
    if ($OuterPort)
    {
        if ($outerScenes.Count -ne 1)
        {
            throw '-OuterPort 需要恰好一个对外 Scene。'
        }
        $outerScenes[0].SetAttribute('outerPort', "$OuterPort")
    }
    if ($InnerBasePort)
    {
        if ($InnerBasePort + $scenes.Count - 1 -gt 65535)
        {
            throw "内网起始端口过大，必须为 $($scenes.Count) 个 Scene 留出连续端口。"
        }
        for ($i = 0; $i -lt $scenes.Count; $i++)
        {
            $scenes[$i].SetAttribute('innerPort', [string]($InnerBasePort + $i))
        }
    }

    $endpoints = foreach ($scene in $scenes)
    {
        $innerProtocol = if ($root.network.inner -eq 'KCP') { 'Udp' } else { 'Tcp' }
        [pscustomobject]@{ Scene = $scene.id; Role = '内网'; Address = $machine.innerBindIP; Port = [int]$scene.innerPort; Protocol = $innerProtocol }
        if ([int]$scene.outerPort -gt 0)
        {
            $outerProtocol = if ($scene.networkProtocol -eq 'KCP') { 'Udp' } else { 'Tcp' }
            [pscustomobject]@{ Scene = $scene.id; Role = '外网'; Address = $machine.outerBindIP; Port = [int]$scene.outerPort; Protocol = $outerProtocol }
        }
    }
    Test-ListenPorts $endpoints

    # Fantasy 从 Main.dll 旁读取配置。独立目录隔离正在运行的 DLL 和本次端口覆盖。
    $tempRoot = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).ProviderPath
    $instance = Join-Path $tempRoot ('RevGameServer-' + [guid]::NewGuid().ToString('N'))
    $runDirectory = Join-Path $instance 'run'
    New-Item -ItemType Directory -Path $runDirectory | Out-Null
    if ($build)
    {
        Write-Host '正在从当前源码构建 RevGame 服务端。'
        dotnet build $serverProject --configuration Debug --artifacts-path (Join-Path $instance 'artifacts') --output $runDirectory
        if ($LASTEXITCODE -ne 0)
        {
            throw "构建失败，dotnet 退出码 $LASTEXITCODE。"
        }
    }
    else
    {
        Write-Host '使用已有编译产物。源码更新后需要在装有 SDK 的机器上重新构建。'
        Get-ChildItem -LiteralPath $builtDirectory | Copy-Item -Destination $runDirectory -Recurse
    }
    foreach ($file in @('Main.dll', 'Main.deps.json', 'Main.runtimeconfig.json', 'Entity.dll', 'Hotfix.dll', 'Fantasy-Net.dll'))
    {
        if (-not (Test-Path -LiteralPath (Join-Path $runDirectory $file) -PathType Leaf))
        {
            throw "缺少运行文件 $file，请重新构建或复制完整产物。"
        }
    }
    $config.Save((Join-Path $runDirectory 'Fantasy.config'))
    Test-ListenPorts $endpoints
    foreach ($endpoint in $endpoints)
    {
        Write-Host "Scene $($endpoint.Scene) $($endpoint.Role) $($endpoint.Protocol.ToUpperInvariant()) $($endpoint.Address):$($endpoint.Port)"
    }
    if ($outerScenes.Count -eq 1)
    {
        Write-Host "本机客户端填写 127.0.0.1:$($outerScenes[0].outerPort)。其它设备使用服务端所在机器的实际 IP 和同一端口。"
    }
    Write-Host '停止服务请按 Ctrl+C。'
    dotnet (Join-Path $runDirectory 'Main.dll') --m Develop
    $exitCode = $LASTEXITCODE
}
catch
{
    [Console]::Error.WriteLine($_.Exception.Message)
}
finally
{
    # 只删除本次创建的专用目录，保留原启动退出码。
    if ($instance -and (Test-Path -LiteralPath $instance))
    {
        $resolved = (Resolve-Path -LiteralPath $instance).ProviderPath
        if ((Split-Path -Parent $resolved) -eq $tempRoot -and $resolved -eq $instance)
        {
            Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Pop-Location
}
exit $exitCode
