#!/usr/bin/env bash
# Build Jellyfin.Plugin.AIRecommendations (Release) — Linux/macOS
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_FILE="$SCRIPT_DIR/Jellyfin.Plugin.AIRecommendations.csproj"
OUTPUT_DIR="$SCRIPT_DIR/bin/Release/net9.0"
DLL_NAME="Jellyfin.Plugin.AIRecommendations.dll"
ZIP_NAME="Jellyfin.Plugin.AIRecommendations.zip"

echo "=== AI Recommendations Plugin Build ==="

if ! command -v dotnet &> /dev/null; then
    echo "ERROR: .NET SDK not found. Install .NET 9 SDK."
    exit 1
fi

echo "dotnet version: $(dotnet --version)"

dotnet restore "$PROJECT_FILE"
dotnet build "$PROJECT_FILE" -c Release --no-restore

DLL_PATH="$OUTPUT_DIR/$DLL_NAME"
if [ ! -f "$DLL_PATH" ]; then
    echo "ERROR: Build output not found at $DLL_PATH"
    exit 1
fi

ZIP_PATH="$OUTPUT_DIR/$ZIP_NAME"

# Generate meta.json (required by Jellyfin 10.10+ to read plugin metadata)
SEMVER=$(tr -d ' \r\n' < "$SCRIPT_DIR/VERSION.txt")
TARGET_ABI=$(jq -r --arg v "${SEMVER}.0" '.[0].versions[] | select(.version == $v) | .targetAbi // "10.11.0.0"' "$SCRIPT_DIR/manifest.json")
CHANGELOG=$(jq -r --arg v "${SEMVER}.0" '.[0].versions[] | select(.version == $v) | .changelog // "Build '"$SEMVER"'"' "$SCRIPT_DIR/manifest.json")
TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

cat > "$OUTPUT_DIR/meta.json" << EOF
{
  "category": "General",
  "changelog": "$CHANGELOG",
  "description": "Per-user AI movie and TV recommendations synced to virtual libraries on all Jellyfin clients",
  "guid": "7c4a9e2b-3f1d-4a8c-b6e5-2d9f8a1c0b3e",
  "imageUrl": null,
  "name": "AI Recommendations",
  "overview": "LLM-powered recommendations via OpenAI, OpenRouter, or Ollama",
  "owner": "aG00Dtime",
  "targetAbi": "$TARGET_ABI",
  "timestamp": "$TIMESTAMP",
  "version": "${SEMVER}.0"
}
EOF

rm -f "$ZIP_PATH"
(cd "$OUTPUT_DIR" && zip -j "$ZIP_NAME" "$DLL_NAME" meta.json)

"$SCRIPT_DIR/scripts/sync-manifest-checksum.sh" "$ZIP_PATH"

CHECKSUM=$(md5sum "$ZIP_PATH" | awk '{print $1}')
echo ""
echo "Build successful!"
echo "  DLL: $DLL_PATH"
echo "  ZIP: $ZIP_PATH"
echo "  MD5: $CHECKSUM"
