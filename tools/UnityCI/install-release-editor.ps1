param(
    [Parameter(Mandatory)]
    [ValidateSet('windows','android')]
    [string]$Target
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# 官方 Windows 安装清单对应 RevGame 固定的 Unity 中国版修订。
$archive = 'https://download.unitychina.cn/download_unity/18739dc7bca8'
# 只在本轮托管 runner 的临时目录中安装，使用短路径避免 IL2CPP/NDK 的路径限制。
$root = Join-Path $env:RUNNER_TEMP 'rev-unity-c1'
# 逐个处理下载，验证并安装后立即回收安装介质。
$scratch = Join-Path $env:RUNNER_TEMP 'rev-unity-download'
if ((Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $scratch)) {
    throw '为本轮选择全新的 Unity 安装目录。'
}
$null = New-Item -ItemType Directory -Path $scratch

# 使用官网的明确版本 URL，失败交给调用端处理。
function Get-UnityFile([string]$Url, [string]$Destination) {
    & curl.exe --silent --show-error --fail --location --retry 3 --output $Destination $Url
    if ($LASTEXITCODE -ne 0) {
        throw "下载失败 $([IO.Path]::GetFileName($Destination))"
    }
}

# 对照 Unity 下载清单的 MD5，随后用官方 Windows 无人值守参数安装。
function Install-UnityPart([string]$RelativePath, [string]$Md5) {
    $installer = Join-Path $scratch ([IO.Path]::GetFileName($RelativePath))
    try {
        Get-UnityFile "$archive/$RelativePath" $installer
        if ((Get-FileHash -LiteralPath $installer -Algorithm MD5).Hash -ne $Md5) {
            throw "Unity 安装文件与官方清单不同 $RelativePath"
        }
        $process = Start-Process -FilePath $installer -ArgumentList @('/S', "/D=$root") -WindowStyle Hidden -PassThru -Wait
        if ($process.ExitCode -ne 0) {
            throw "Unity 安装失败 $RelativePath，退出码 $($process.ExitCode)"
        }
    } finally {
        if (Test-Path -LiteralPath $installer) {
            Remove-Item -LiteralPath $installer
        }
    }
}

# Unity 模块可能预建空工具目录。只移除空目录，再把本次安装的工具原地就位。
function Move-ToolFolder([string]$Source, [string]$Destination) {
    $from = (Resolve-Path -LiteralPath $Source).Path
    $absolute = [IO.Path]::GetFullPath($Destination)
    if ((-not $from.StartsWith($env:RUNNER_TEMP.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) -or
        (-not $absolute.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase))) {
        throw 'Android 工具移动路径越过本轮安装目录。'
    }
    if (Test-Path -LiteralPath $absolute) {
        if (@(Get-ChildItem -LiteralPath $absolute -Force).Count -ne 0) {
            throw "工具安装位置非空 $absolute"
        }
        Remove-Item -LiteralPath $absolute
    }
    Move-Item -LiteralPath $from -Destination $absolute
}

Install-UnityPart 'Windows64EditorInstaller/UnitySetup64.exe' '7c1b8791e6db42e7b9687384c3076a57'
if ($Target -eq 'windows') {
    Install-UnityPart 'TargetSupportInstaller/UnitySetup-Windows-IL2CPP-Support-for-Editor-2022.3.15f1c1.exe' '6b167463842cfbe484a519b9441ab6cc'
    $player = Join-Path $root 'Editor/Data/PlaybackEngines/WindowsStandaloneSupport/Variations/win64_player_nondevelopment_il2cpp'
    if (-not (Test-Path -LiteralPath $player)) {
        throw 'Windows x64 IL2CPP 模块安装未完成。'
    }
} else {
    Install-UnityPart 'TargetSupportInstaller/UnitySetup-Android-Support-for-Editor-2022.3.15f1c1.exe' '3835734b614019f34e1ac8002998b4ca'
    $module = Join-Path $root 'Editor/Data/PlaybackEngines/AndroidPlayer'
    $sdk = Join-Path $module 'SDK'
    $hostSdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { $env:ANDROID_SDK_ROOT }
    if (-not $hostSdk -or -not (Test-Path -LiteralPath (Join-Path $hostSdk 'licenses'))) {
        throw '托管 runner 应提供已授权的 Android SDK。'
    }
    $manager = Get-ChildItem -LiteralPath (Join-Path $hostSdk 'cmdline-tools') -Recurse -Filter 'sdkmanager.bat' -File |
        Sort-Object @{Expression={ if ($_.Directory.Parent.Name -eq 'latest') { 0 } else { 1 } }}, FullName | Select-Object -First 1
    if (-not $manager) {
        throw '托管 runner 缺少 Android SDK Manager。'
    }
    $null = New-Item -Path $sdk -ItemType Directory -Force
    Copy-Item -LiteralPath (Join-Path $hostSdk 'licenses') -Destination $sdk -Recurse
    $priorJava = $env:JAVA_HOME
    try {
        if ($env:JAVA_HOME_17_X64) { $env:JAVA_HOME = $env:JAVA_HOME_17_X64 }
        & $manager.FullName "--sdk_root=$sdk" --install 'platforms;android-33' 'build-tools;34.0.0' 'platform-tools' 'cmdline-tools;6.0' 'ndk;23.1.7779620'
        if ($LASTEXITCODE -ne 0) {
            throw '安装 RevGame 已验的 Android SDK/NDK 组合失败。'
        }
    } finally {
        $env:JAVA_HOME = $priorJava
    }
    Move-ToolFolder (Join-Path $sdk 'ndk/23.1.7779620') (Join-Path $module 'NDK')

    # Unity 的 Gradle 7.2 使用与此 c1 编辑器匹配的 OpenJDK 11。
    $jdkUrl = 'https://download.unitychina.cn/download_unity/open-jdk/open-jdk-win-x64/jdk11.0.14.1-1_85218201fea144521d643808d167605d6d46cd4fe44ee4001991a3a4b76fdd64.zip'
    $zip = Join-Path $scratch 'openjdk.zip'
    $extract = Join-Path $scratch 'jdk'
    try {
        Get-UnityFile $jdkUrl $zip
        Expand-Archive -LiteralPath $zip -DestinationPath $extract
        if (Test-Path -LiteralPath (Join-Path $extract 'bin/java.exe')) {
            $jdk = $extract
        } else {
            $jdk = (Get-ChildItem -LiteralPath $extract -Directory |
                Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bin/java.exe') } |
                Select-Object -First 1).FullName
        }
        if (-not $jdk -or -not (Select-String -LiteralPath (Join-Path $jdk 'release') -Pattern 'JAVA_VERSION="11.0.14.1"' -SimpleMatch)) {
            throw 'Unity 内置 OpenJDK 版本校验失败。'
        }
        Move-ToolFolder $jdk (Join-Path $module 'OpenJDK')
    } finally {
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
        if ((Test-Path -LiteralPath $extract) -and @((Get-ChildItem -LiteralPath $extract -Force)).Count -eq 0) {
            Remove-Item -LiteralPath $extract
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $sdk 'platforms/android-33/android.jar')) -or
        -not (Test-Path -LiteralPath (Join-Path $module 'NDK/source.properties')) -or
        -not (Test-Path -LiteralPath (Join-Path $module 'Tools/gradle/lib/gradle-launcher-7.2.jar'))) {
        throw 'Android 的 SDK、NDK 或 Unity 内置 Gradle 安装未完成。'
    }
}

$editor = Join-Path $root 'Editor/Unity.exe'
if (-not (Test-Path -LiteralPath $editor)) {
    throw 'Unity.exe 安装未完成。'
}
if (@(Get-ChildItem -LiteralPath $scratch -Force).Count -eq 0) {
    Remove-Item -LiteralPath $scratch
}
"REVGAME_CLOUD_EDITOR=$editor" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
"REVGAME_RELEASE_OUTPUT=$(Join-Path $env:RUNNER_TEMP "revgame-$Target")" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
