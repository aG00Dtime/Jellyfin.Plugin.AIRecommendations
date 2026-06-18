#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ZIP_PATH="${1:-$ROOT/bin/Release/net9.0/Jellyfin.Plugin.AIRecommendations.zip}"
MANIFEST="$ROOT/manifest.json"
VERSION_FILE="$ROOT/VERSION.txt"

if [ ! -f "$ZIP_PATH" ]; then
  echo "Zip not found: $ZIP_PATH (run ./build.sh first)" >&2
  exit 1
fi

semver="$(tr -d ' \r\n' < "$VERSION_FILE")"
version_four="${semver}.0"
checksum="$(md5sum "$ZIP_PATH" | awk '{print $1}')"
timestamp="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

jq --arg version "$version_four" \
   --arg checksum "$checksum" \
   --arg ts "$timestamp" \
   '(.[0].versions[] | select(.version == $version) | .checksum) = $checksum |
    (.[0].versions[] | select(.version == $version) | .timestamp) = $ts' \
   "$MANIFEST" > "${MANIFEST}.tmp" && mv "${MANIFEST}.tmp" "$MANIFEST"

echo "manifest.json checksum for ${version_four} -> ${checksum}"
