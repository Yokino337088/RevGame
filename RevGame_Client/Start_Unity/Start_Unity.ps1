# Windows PowerShell 5.1启动适配：只持久化Unity.exe的位置，工程路径从脚本目录推导，仓库搬迁仍可用。
[CmdletBinding()]
param(
    [switch]$Configure,
    [switch]$CheckOnly,
    [string]$EditorPath,
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'

# 将Unity安装目录、Editor目录或Unity.exe统一为实际可执行文件；失败时不覆盖原配置。
function Resolve-UnityEditor([string]$InputPath)
{
    if ([string]::IsNullOrWhiteSpace($InputPath))
    {
        throw 'Unity路径不能为空。'
    }
    $candidate = [Environment]::ExpandEnvironmentVariables($InputPath.Trim().Trim('"'))
    if (Test-Path -LiteralPath $candidate -PathType Container)
    {
        $direct = Join-Path $candidate 'Unity.exe'
        $nested = Join-Path $candidate 'Editor/Unity.exe'
        if (Test-Path -LiteralPath $direct -PathType Leaf)
        {
            $candidate = $direct
        }
        elseif (Test-Path -LiteralPath $nested -PathType Leaf)
        {
            $candidate = $nested
        }
        else
        {
            throw '这个目录中没有Unity.exe，请选择Unity安装目录或Editor目录。'
        }
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf) -or [IO.Path]::GetFileName($candidate) -ine 'Unity.exe')
    {
        throw '请提供存在的Unity.exe，或包含它的安装目录。'
    }
    return (Get-Item -LiteralPath $candidate).FullName
}

# JSON先写临时文件再替换，避免配置写到一半留下不可读文件；本机绝对路径仅存忽略的本地JSON。
function Save-UnityConfig([string]$Path, [string]$Executable)
{
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container))
    {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $temporary = $Path + '.tmp'
    $json = [ordered]@{ schemaVersion = 1; unityEditorPath = $Executable } | ConvertTo-Json
    [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

try
{
    $project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../UnityProject/RevGame.Unity'))
    if (-not (Test-Path -LiteralPath (Join-Path $project 'Assets') -PathType Container) -or -not (Test-Path -LiteralPath (Join-Path $project 'ProjectSettings') -PathType Container))
    {
        throw '未找到相邻的RevGame.Unity工程，请保持Start_Unity与UnityProject在同一目录。'
    }
    if ([string]::IsNullOrWhiteSpace($ConfigPath))
    {
        $ConfigPath = Join-Path $PSScriptRoot 'unity-launcher.local.json'
    }
    $ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
    $executable = $null
    if (-not [string]::IsNullOrWhiteSpace($EditorPath))
    {
        $executable = Resolve-UnityEditor $EditorPath
        Save-UnityConfig $ConfigPath $executable
    }
    elseif (-not $Configure -and (Test-Path -LiteralPath $ConfigPath -PathType Leaf))
    {
        try
        {
            $saved = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($saved.schemaVersion -ne 1)
            {
                throw '配置版本无效。'
            }
            $executable = Resolve-UnityEditor $saved.unityEditorPath
        }
        catch
        {
            if ($CheckOnly)
            {
                throw
            }
            Write-Host ('保存的Unity路径不可用：' + $_.Exception.Message) -ForegroundColor Yellow
        }
    }
    if ($null -eq $executable)
    {
        if ($CheckOnly)
        {
            throw '还没有有效Unity路径，请首次双击配置，或使用-EditorPath指定路径。'
        }
        Write-Host '首次配置：输入Unity.exe完整路径，或Unity安装目录。无需打开Unity Hub。'
        Write-Host '直接按回车取消；以后需要修改时运行 Start_Unity.cmd -Configure。'
        while ($null -eq $executable)
        {
            $answer = Read-Host 'Unity引擎路径'
            if ([string]::IsNullOrWhiteSpace($answer))
            {
                exit 0
            }
            try
            {
                $executable = Resolve-UnityEditor $answer
            }
            catch
            {
                Write-Host $_.Exception.Message -ForegroundColor Yellow
            }
        }
        Save-UnityConfig $ConfigPath $executable
    }
    if ($CheckOnly)
    {
        [ordered]@{ success = $true; editor = $executable; project = $project; config = $ConfigPath } | ConvertTo-Json
        exit 0
    }
    # 已打开相同工程就不重复启动，避免工程锁冲突；无法读进程命令行时交由Unity自己的锁处理。
    $existing = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object {
        $_.CommandLine -and $_.CommandLine.Replace('/', '\').IndexOf($project.Replace('/', '\'), [StringComparison]::OrdinalIgnoreCase) -ge 0
    } | Select-Object -First 1
    if ($null -ne $existing)
    {
        Write-Host ('RevGame工程已经打开，Unity进程号：' + $existing.ProcessId)
        exit 0
    }
    # Windows文件路径不能包含双引号；这里显式给工程路径加引号，使中文/空格路径按一个参数传给Unity。
    Start-Process -FilePath $executable -ArgumentList ('-projectPath "' + $project + '"') -WorkingDirectory $project -WindowStyle Hidden | Out-Null
    Write-Host '已启动RevGame Unity工程。'
    exit 0
}
catch
{
    Write-Host ('启动失败：' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
