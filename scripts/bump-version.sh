#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PART="${1:-patch}"

VERSION_FILE="$ROOT/VERSION.txt"
CSPROJ="$ROOT/Jellyfin.Plugin.AIRecommendations.csproj"
MANIFEST="$ROOT/manifest.json"

current="$(tr -d ' \r\n' < "$VERSION_FILE")"
IFS='.' read -r major minor patch <<< "$current"

case "$PART" in
  major) major=$((major + 1)); minor=0; patch=0 ;;
  minor) minor=$((minor + 1)); patch=0 ;;
  patch) patch=$((patch + 1)) ;;
  *) echo "Usage: $0 [patch|minor|major]" >&2; exit 1 ;;
esac

new_version="${major}.${minor}.${patch}"
new_version_four="${new_version}.0"

printf '%s' "$new_version" > "$VERSION_FILE"

sed -i.bak -E "s/<AssemblyVersion>[0-9.]+<\/AssemblyVersion>/<AssemblyVersion>${new_version_four}<\/AssemblyVersion>/" "$CSPROJ"
sed -i.bak -E "s/<FileVersion>[0-9.]+<\/FileVersion>/<FileVersion>${new_version_four}<\/FileVersion>/" "$CSPROJ"
rm -f "${CSPROJ}.bak"

target_abi="$(grep -oE 'Jellyfin\.Controller" Version="[0-9]+\.[0-9]+\.[0-9]+' "$CSPROJ" | grep -oE '[0-9]+\.[0-9]+\.[0-9]+').0"
timestamp="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

# Prepend a new version entry to the "versions" array — every prior entry is
# release history and must stay untouched. (A field-by-field sed replace here
# previously clobbered every historical entry with the new version's values;
# see git history if this needs re-deriving.) CI fills in the real checksum
# once it builds the zip.
python3 - "$MANIFEST" "$new_version_four" "$new_version" "$target_abi" "$timestamp" <<'PYEOF'
import json, sys

manifest_path, version_four, version, target_abi, timestamp = sys.argv[1:6]

with open(manifest_path, "r", encoding="utf-8") as f:
    data = json.load(f)

data[0]["versions"].insert(0, {
    "version": version_four,
    "changelog": f"Build {version}",
    "targetAbi": target_abi,
    "sourceUrl": f"https://github.com/aG00Dtime/Jellyfin.Plugin.AIRecommendations/releases/download/v{version}/Jellyfin.Plugin.AIRecommendations.zip",
    "checksum": "",
    "timestamp": timestamp,
})

with open(manifest_path, "w", encoding="utf-8") as f:
    json.dump(data, f, indent=2, ensure_ascii=False)
    f.write("\n")
PYEOF

echo "Version bumped to ${new_version} (${new_version_four})"
