[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DotnetRoot = $env:DOTNET_ROOT
)

# [AI修改] 使用 MSBuild 内部并行构建 Windows 客户端，并运行离线自测。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$windowsRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $windowsRoot 'CodeXPedal.Windows.sln'
$testProject = Join-Path $windowsRoot 'tests\CodeXPedal.Windows.Tests\CodeXPedal.Windows.Tests.csproj'

# [AI修改] 先生成真实共享DLL，再编译两个宿主，不能只测试托管替身。
& (Join-Path (Split-Path -Parent $windowsRoot) 'shared\scripts\build-windows.ps1') -TargetDirectory $env:CARGO_TARGET_DIR

if ($DotnetRoot) {
    $dotnet = Join-Path $DotnetRoot 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
        throw "指定目录中没有 dotnet.exe：$DotnetRoot"
    }
} else {
    $dotnetCommand = Get-Command dotnet -ErrorAction Stop
    $dotnet = $dotnetCommand.Source
}

& $dotnet restore $solution -p:Platform=x64 -m
if ($LASTEXITCODE -ne 0) { throw 'Windows 解决方案还原失败。' }

& $dotnet build $solution -c $Configuration -p:Platform=x64 --no-restore -m
if ($LASTEXITCODE -ne 0) { throw 'Windows 解决方案构建失败。' }

& $dotnet run --project $testProject -c $Configuration -p:Platform=x64 --no-build
if ($LASTEXITCODE -ne 0) { throw 'Windows 自测失败。' }

Write-Host "Windows 客户端构建和自测通过：$Configuration x64"
