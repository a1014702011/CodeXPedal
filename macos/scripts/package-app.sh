#!/usr/bin/env bash
# [AI修改] 从已验签应用生成候选ZIP/DMG与真实哈希，不冒充Apple公证。
set -euo pipefail
ROOT_DIR="${1:?工程根目录}"
APP_DIR="$ROOT_DIR/build/CodeXPedal.app"
VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$APP_DIR/Contents/Info.plist")"
ARCH="$(uname -m)"
mkdir -p "$ROOT_DIR/dist"
NAME="CodeXPedal-$VERSION-macos-$ARCH"
codesign --verify --deep --strict "$APP_DIR"
file "$APP_DIR/Contents/MacOS/CodeXPedal" | grep -q "$ARCH"
ditto -c -k --sequesterRsrc --keepParent "$APP_DIR" "$ROOT_DIR/dist/$NAME.zip"
hdiutil create -volname CodeXPedal -srcfolder "$APP_DIR" -ov -format UDZO "$ROOT_DIR/dist/$NAME.dmg"
hdiutil verify "$ROOT_DIR/dist/$NAME.dmg"
(cd "$ROOT_DIR/dist" && shasum -a 256 "$NAME.zip" "$NAME.dmg" > "$NAME.sha256")
