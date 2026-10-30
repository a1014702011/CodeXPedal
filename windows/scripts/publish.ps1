[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DotnetRoot = $env:DOTNET_ROOT,
    [string]$OutputRoot
)

# [AI修改] 生成携带 .NET 桌面运行时、可直接双击启动的 x64 便携包。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $DotnetRoot) {
    $dotnetCommand = Get-Command dotnet -ErrorAction Stop
    $DotnetRoot = Split-Path -Parent $dotnetCommand.Source
}

$dotnet = Join-Path $DotnetRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "指定目录中没有 dotnet.exe：$DotnetRoot"
}

$windowsRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = Split-Path -Parent $windowsRoot
$appProject = Join-Path $windowsRoot 'src\CodeXPedal.Windows.App\CodeXPedal.Windows.App.csproj'
if (-not $OutputRoot) {
    $OutputRoot = Join-Path $windowsRoot 'dist'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$packageRoot = Join-Path $OutputRoot 'CodeXPedal-windows-x64-portable'
$appRoot = Join-Path $packageRoot 'app'
$runtimeRoot = Join-Path $packageRoot 'runtime'
$zipPath = Join-Path $OutputRoot 'CodeXPedal-windows-x64-portable.zip'

$coreRuntime = Get-ChildItem -LiteralPath (Join-Path $DotnetRoot 'shared\Microsoft.NETCore.App') -Directory |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
$desktopRuntime = Get-ChildItem -LiteralPath (Join-Path $DotnetRoot 'shared\Microsoft.WindowsDesktop.App') -Directory |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
$hostFxr = Get-ChildItem -LiteralPath (Join-Path $DotnetRoot 'host\fxr') -Directory |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1

if (-not $coreRuntime -or -not $desktopRuntime -or -not $hostFxr) {
    throw '指定 .NET 目录缺少 Core、Windows Desktop Runtime 或 hostfxr。'
}

# [AI修改] 只清理本脚本固定命名的发布目录和压缩包，避免误删其他产物。
if (-not ([IO.Path]::GetFullPath($packageRoot)).StartsWith($OutputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '发布目录越出指定输出目录，已停止。'
}
if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null

& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -DotnetRoot $DotnetRoot

# [AI修改] 不指定 RID，避免重复下载本地 SDK 已携带的运行时包；PlatformTarget 仍保证 x64 apphost。
& $dotnet restore $appProject -p:Platform=x64 -m
if ($LASTEXITCODE -ne 0) { throw 'Windows 发布还原失败。' }

& $dotnet publish $appProject -c $Configuration --self-contained false `
    -p:Platform=x64 -p:AppHostDotNetSearch=AppRelative -p:AppHostRelativeDotNet=..\runtime `
    -p:DebugType=None -p:DebugSymbols=false `
    --no-restore -m -o $appRoot
if ($LASTEXITCODE -ne 0) { throw 'Windows 发布失败。' }

# [AI修改] 只复制运行应用所需的 hostfxr 与两个共享框架，不携带完整 SDK。
Copy-Item -LiteralPath $dotnet -Destination $runtimeRoot
Copy-Item -LiteralPath (Join-Path $DotnetRoot 'host') -Destination $runtimeRoot -Recurse
New-Item -ItemType Directory -Path (Join-Path $runtimeRoot 'shared\Microsoft.NETCore.App') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $runtimeRoot 'shared\Microsoft.WindowsDesktop.App') -Force | Out-Null
Copy-Item -LiteralPath $coreRuntime.FullName -Destination (Join-Path $runtimeRoot 'shared\Microsoft.NETCore.App') -Recurse
Copy-Item -LiteralPath $desktopRuntime.FullName -Destination (Join-Path $runtimeRoot 'shared\Microsoft.WindowsDesktop.App') -Recurse

foreach ($notice in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    $noticePath = Join-Path $DotnetRoot $notice
    if (Test-Path -LiteralPath $noticePath -PathType Leaf) {
        Copy-Item -LiteralPath $noticePath -Destination $runtimeRoot
    }
}

# [AI修改] 在包根目录附带运行说明和项目许可证，便于离线查看边界与归属。
Copy-Item -LiteralPath (Join-Path $windowsRoot 'README.md') -Destination (Join-Path $packageRoot 'README.md')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $packageRoot 'LICENSE')
# [AI修改] 随共享原生库分发实际编译依赖的MIT版权，不能只附本项目许可。
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'shared\licenses') -Destination (Join-Path $packageRoot 'licenses') -Recurse

Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Windows 便携包已生成：$zipPath"
