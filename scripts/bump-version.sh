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

timestamp="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
sed -i.bak -E "s/\"version\": \"[^\"]+\"/\"version\": \"${new_version_four}\"/" "$MANIFEST"
sed -i.bak -E "s|\"sourceUrl\": \"[^\"]+\"|\"sourceUrl\": \"https://github.com/aG00Dtime/Jellyfin.Plugin.AIRecommendations/releases/download/v${new_version}/Jellyfin.Plugin.AIRecommendations.zip\"|" "$MANIFEST"
sed -i.bak -E "s/\"timestamp\": \"[^\"]+\"/\"timestamp\": \"${timestamp}\"/" "$MANIFEST"
sed -i.bak -E "s/\"changelog\": \"[^\"]+\"/\"changelog\": \"Build ${new_version}\"/" "$MANIFEST"
rm -f "${MANIFEST}.bak"

echo "Version bumped to ${new_version} (${new_version_four})"
