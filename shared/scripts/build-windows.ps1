param([string]$TargetDirectory)
# [AI修改] 静态链接MSVC运行库，用户无需另装Rust或VC运行库。
$ErrorActionPreference = 'Stop'
$sharedRoot = Split-Path -Parent $PSScriptRoot
if (-not $TargetDirectory) { $TargetDirectory = Join-Path $sharedRoot 'target' }
$env:CARGO_TARGET_DIR = $TargetDirectory
$previousFlags = $env:RUSTFLAGS
try {
    $env:RUSTFLAGS = (($previousFlags + ' -C target-feature=+crt-static').Trim())
    cargo build --manifest-path (Join-Path $sharedRoot 'Cargo.toml') --locked --release --target x86_64-pc-windows-msvc
    if ($LASTEXITCODE -ne 0) { throw '共享核心构建失败。' }
    $nativeDirectory = Join-Path $sharedRoot 'native\windows-x64'
    New-Item -ItemType Directory -Path $nativeDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $TargetDirectory 'x86_64-pc-windows-msvc\release\codexpedal_core.dll') -Destination $nativeDirectory
} finally { $env:RUSTFLAGS = $previousFlags }
