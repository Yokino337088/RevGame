# 开发态单进程启动。对外 TCP 20000，内部 TCP 11002 仅绑定回环地址。
$ErrorActionPreference = 'Stop'
$serverProject = Join-Path $PSScriptRoot 'Server/Main/Main.csproj'
Push-Location $PSScriptRoot
try
{
    dotnet run --project $serverProject --no-launch-profile -- --m Develop
    if ($LASTEXITCODE -ne 0)
    {
        throw '围棋中转服务器非正常退出，请检查上方日志。'
    }
}
finally
{
    Pop-Location
}
