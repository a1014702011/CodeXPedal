#!/usr/bin/env bash
# [AI修改] 只打包独立 SwiftPM 客户端和自有图标，不复制旧仪表盘资源。
set -euo pipefail
ROOT_DIR="${1:?请传入工程根目录}"
TARGET_TRIPLE="${2:?请传入目标架构}"
SIGN_IDENTITY="${3:--}"
[[ "$(uname -s)" == Darwin ]] || { echo '必须在 Mac 上构建原生客户端。' >&2; exit 2; }
case "$TARGET_TRIPLE" in
  arm64-apple-macos13.0|x86_64-apple-macos13.0) ;;
  *) echo '不支持的目标架构，请使用 arm64 或 x86_64 的 macOS 13.0 目标。' >&2; exit 2 ;;
esac
# [AI修改] Rust与Swift必须使用同一架构；只链接静态库，不依赖开发机动态路径。
RUST_TARGET=x86_64-apple-darwin
[[ "$TARGET_TRIPLE" == arm64-* ]] && RUST_TARGET=aarch64-apple-darwin
export CARGO_TARGET_DIR="${CARGO_TARGET_DIR:-$ROOT_DIR/build/rust}"
sed 's/\r$//' "$ROOT_DIR/shared/scripts/build-macos.sh" | bash -s -- "$ROOT_DIR" "$RUST_TARGET"
export CODEXPEDAL_CORE_LIB_DIR="$CARGO_TARGET_DIR/$RUST_TARGET/release"
SDK_PATH="$(xcrun --sdk macosx --show-sdk-path)"
swift build --package-path "$ROOT_DIR/macos" -c release --triple "$TARGET_TRIPLE" --sdk "$SDK_PATH"
BIN_DIR="$(swift build --package-path "$ROOT_DIR/macos" -c release --triple "$TARGET_TRIPLE" --sdk "$SDK_PATH" --show-bin-path)"
APP_DIR="$ROOT_DIR/build/CodeXPedal.app"
# [AI修改] 目标固定在本工程 build 下，避免复用旧目录残留面板或配色资源。
[[ "$APP_DIR" == "$ROOT_DIR/build/CodeXPedal.app" && -f "$ROOT_DIR/macos/Package.swift" ]] || exit 2
if [[ -d "$APP_DIR" ]]; then
  BACKUP_DIR="$ROOT_DIR/build/previous-CodeXPedal-$(date +%Y%m%d-%H%M%S)-$$.app"
  mv "$APP_DIR" "$BACKUP_DIR"
fi
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
cp "$BIN_DIR/CodeXPedal" "$APP_DIR/Contents/MacOS/CodeXPedal"
cp "$ROOT_DIR/Resources/Info.plist" "$APP_DIR/Contents/Info.plist"
cp "$ROOT_DIR/Resources/CodeXPedal.icns" "$APP_DIR/Contents/Resources/CodeXPedal.icns"
# [AI修改] 核心静态链接后仍须保留项目和第三方版权声明。
cp "$ROOT_DIR/LICENSE" "$APP_DIR/Contents/Resources/LICENSE"
cp -R "$ROOT_DIR/shared/licenses" "$APP_DIR/Contents/Resources/licenses"
plutil -lint "$APP_DIR/Contents/Info.plist"
if [[ "$SIGN_IDENTITY" == - ]]; then
  codesign --force --sign - "$APP_DIR"
else
  codesign --force --options runtime --timestamp --sign "$SIGN_IDENTITY" "$APP_DIR"
fi
codesign --verify --deep --strict "$APP_DIR"
file "$APP_DIR/Contents/MacOS/CodeXPedal"
echo "已构建：$APP_DIR（仅本地构建，不代表已完成真机验收或公证）"
