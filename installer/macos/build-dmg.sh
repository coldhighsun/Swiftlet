#!/usr/bin/env bash
# Wraps a published osx-* build into Swiftlet.app and packs it into a disk image. Runs on macOS only.
#
#   build-dmg.sh <publish directory> <version> <output .dmg>
#
# The bundle is only ad-hoc signed and not notarized, so macOS asks the user to allow it on the first start.
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <publish directory> <version> <output .dmg>" >&2
  exit 2
fi

publish_dir=$1
version=$2
output=$3

script_dir=$(cd "$(dirname "$0")" && pwd)
repo_root=$(cd "$script_dir/../.." && pwd)

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

staging="$work/dmg"
app="$staging/Swiftlet.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

cp -R "$publish_dir/." "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/Swiftlet"
cp "$repo_root/LICENSE" "$repo_root/THIRD-PARTY-NOTICES.md" "$app/Contents/Resources/"

# CFBundleVersion allows numbers only, so a pre-release suffix is dropped.
sed "s/@VERSION@/${version%%-*}/g" "$script_dir/Info.plist" > "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist"

iconset="$work/Swiftlet.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$script_dir/icon-1024.png" --out "$iconset/icon_${size}x${size}.png" > /dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$script_dir/icon-1024.png" --out "$iconset/icon_${size}x${size}@2x.png" > /dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/Swiftlet.icns"

# Apple silicon runs signed code only; "-" is the ad-hoc identity.
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict --verbose=2 "$app"

ln -s /Applications "$staging/Applications"
mkdir -p "$(dirname "$output")"

# hdiutil now and then fails with "Resource busy" on CI machines.
for attempt in 1 2 3; do
  if hdiutil create -volname Swiftlet -srcfolder "$staging" -ov -format UDZO "$output"; then
    exit 0
  fi
  echo "hdiutil failed (attempt $attempt)" >&2
  sleep 5
done
exit 1
