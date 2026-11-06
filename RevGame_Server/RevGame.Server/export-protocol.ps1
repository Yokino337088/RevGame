# 所有路径相对脚本位置，仓库换盘符或其他开发者克隆后无需修改协议工具配置。
$ErrorActionPreference = 'Stop'
$protocolToolDirectory = Join-Path $PSScriptRoot 'Tools/ProtocolExportTool'
Push-Location $protocolToolDirectory
try
{
    dotnet Fantasy.ProtocolExportTool.dll export --silent
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Fantasy 协议导出失败。'
    }
}
finally
{
    Pop-Location
}
