#!/bin/bash
# Universal standalone macOS package. No Apple developer certificate required.
set -euo pipefail

repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
version="${BITCHAT_MAC_VERSION:-0.1.9}"
build_dir="${BITCHAT_MAC_BUILD_DIR:-${TMPDIR:-/tmp}/bitchat-macos-build}"
output_dir="${BITCHAT_MAC_OUTPUT_DIR:-${TMPDIR:-/tmp}/bitchat-macos-dist}"
mkdir -p "$output_dir" "$build_dir"
git -C "$repo_dir" diff --quiet HEAD -- || { echo 'Commit source changes before creating a release.' >&2; exit 1; }
python3 "$repo_dir/scripts/audit_release.py" source --repo "$repo_dir" --report "$output_dir/SECURITY-source.json"
# Xcode synchronized groups can otherwise pick up untracked files. Compile a
# fresh archive of committed files, using fresh products and no user profile.
scratch="$(mktemp -d "$build_dir/release.XXXXXX")"
trap 'rm -rf "$scratch"' EXIT
snapshot_dir="$scratch/source"
mkdir -p "$snapshot_dir" "$scratch/stage"
git -C "$repo_dir" archive HEAD | tar -x -C "$snapshot_dir"

xcodebuild -project "$snapshot_dir/bitchat.xcodeproj" \
  -scheme 'bitchat (macOS)' -configuration Release \
  -derivedDataPath "$scratch/DerivedData" -destination 'generic/platform=macOS' \
  CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO \
  ARCHS='arm64 x86_64' ONLY_ACTIVE_ARCH=NO \
  MARKETING_VERSION="$version" CURRENT_PROJECT_VERSION=19 build

stage_dir="$scratch/stage"
app_path="$stage_dir/BitChat.app"
ditto --noextattr --norsrc "$scratch/DerivedData/Build/Products/Release/bitchat.app" "$app_path"
binary="$app_path/Contents/MacOS/bitchat"
lipo "$binary" -verify_arch arm64
lipo "$binary" -verify_arch x86_64

# Apple Silicon requires an ad-hoc code seal. This uses no certificate,
# supplies no Developer ID identity and does not notarize the application.
codesign --force --sign - --timestamp=none "$app_path"
codesign --verify --deep --strict "$app_path"
python3 "$repo_dir/scripts/audit_release.py" macos --repo "$snapshot_dir" --root "$app_path" --report "$output_dir/SECURITY-macOS.json"

cp "$repo_dir/docs/INSTALLING-MACOS.md" "$stage_dir/LIRE-MOI.md"
{
  printf 'BitChat macOS %s\n' "$version"
  printf 'Source: %s\n' "$(git -C "$repo_dir" rev-parse HEAD)"
  printf 'Architectures: %s\n' "$(lipo -archs "$binary")"
  printf 'Deployment target: macOS 13.0\n'
  printf 'Signing: ad-hoc only, no Developer ID, not notarized\n'
  xcodebuild -version
} > "$stage_dir/BUILD-INFO.txt"

ditto -c -k --noextattr --norsrc --keepParent "$app_path" \
  "$output_dir/BitChat-macOS-universal-$version.zip"
ln -s /Applications "$stage_dir/Applications"
hdiutil create -volname "BitChat $version" -srcfolder "$stage_dir" \
  -ov -format UDZO "$output_dir/BitChat-macOS-universal-$version.dmg"
cp "$stage_dir/BUILD-INFO.txt" "$output_dir/BUILD-INFO-macOS.txt"
(
  cd "$output_dir"
  shasum -a 256 "BitChat-macOS-universal-$version.dmg" \
    "BitChat-macOS-universal-$version.zip" > SHA256-macOS.txt
)
printf '\nPackages ready in %s\n' "$output_dir"
