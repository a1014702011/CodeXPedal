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
# [AI修改] DMG根目录必须包含完整.app，而不是把Contents散放到卷根；实际挂载验包。
DMG_STAGE="$(mktemp -d "$ROOT_DIR/build/dmg-content-$ARCH.XXXXXX")"
ditto "$APP_DIR" "$DMG_STAGE/CodeXPedal.app"
ln -s /Applications "$DMG_STAGE/Applications"
hdiutil create -volname CodeXPedal -srcfolder "$DMG_STAGE" -ov -format UDZO "$ROOT_DIR/dist/$NAME.dmg"
hdiutil verify "$ROOT_DIR/dist/$NAME.dmg"
MOUNT_DIR="$(mktemp -d "$ROOT_DIR/build/dmg-check-$ARCH.XXXXXX")"
MOUNTED=0
cleanup() { if [[ "$MOUNTED" == 1 ]]; then hdiutil detach "$MOUNT_DIR" >/dev/null; fi; }
trap cleanup EXIT
hdiutil attach "$ROOT_DIR/dist/$NAME.dmg" -readonly -nobrowse -mountpoint "$MOUNT_DIR" >/dev/null
MOUNTED=1
test -f "$MOUNT_DIR/CodeXPedal.app/Contents/Resources/licenses/serde_json-1.0.154-LICENSE-MIT"
plutil -lint "$MOUNT_DIR/CodeXPedal.app/Contents/Info.plist"
codesign --verify --deep --strict "$MOUNT_DIR/CodeXPedal.app"
file "$MOUNT_DIR/CodeXPedal.app/Contents/MacOS/CodeXPedal" | grep -q "$ARCH"
hdiutil detach "$MOUNT_DIR" >/dev/null
MOUNTED=0
(cd "$ROOT_DIR/dist" && shasum -a 256 "$NAME.zip" "$NAME.dmg" > "$NAME.sha256")
