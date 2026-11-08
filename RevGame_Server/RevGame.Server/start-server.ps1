# 开发态单进程启动。PvP对外 Fantasy KCP / UDP 20000，内部 TCP 11002 仅绑定回环地址。
$ErrorActionPreference = 'Stop'
$serverProject = Join-Path $PSScriptRoot 'Server/Main/Main.csproj'
$outputDirectory = Join-Path $PSScriptRoot 'Server/Main/bin/Debug/net8.0'
$requiredFiles = @('Main.dll', 'Main.deps.json', 'Main.runtimeconfig.json', 'Entity.dll', 'Hotfix.dll', 'Fantasy-Net.dll', 'Fantasy.config')
Push-Location $PSScriptRoot
try
{
    # 启动沿用已有 Debug 产物。修改源码后可手动执行 dotnet build 更新产物。
    if ($requiredFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $outputDirectory $_) -PathType Leaf) })
    {
        Write-Host '缺少服务端编译产物，正在构建。'
        dotnet build $serverProject --configuration Debug
        if ($LASTEXITCODE -ne 0)
        {
            throw '围棋中转服务器构建失败，请检查上方日志。'
        }
    }

    dotnet (Join-Path $outputDirectory 'Main.dll') --m Develop
    if ($LASTEXITCODE -ne 0)
    {
        throw '围棋中转服务器非正常退出，请检查上方日志。'
    }
}
finally
{
    Pop-Location
}
