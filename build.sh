#!/usr/bin/env bash
# Build Jellyfin.Plugin.AIRecommendations (Release) — Linux/macOS
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_FILE="$SCRIPT_DIR/Jellyfin.Plugin.AIRecommendations.csproj"
OUTPUT_DIR="$SCRIPT_DIR/bin/Release/net8.0"
DLL_NAME="Jellyfin.Plugin.AIRecommendations.dll"
ZIP_NAME="Jellyfin.Plugin.AIRecommendations.zip"

echo "=== AI Recommendations Plugin Build ==="

if ! command -v dotnet &> /dev/null; then
    echo "ERROR: .NET SDK not found. Install .NET 8 SDK."
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
rm -f "$ZIP_PATH"
(cd "$OUTPUT_DIR" && zip -j "$ZIP_NAME" "$DLL_NAME")

CHECKSUM=$(md5sum "$ZIP_PATH" | awk '{print $1}')
echo ""
echo "Build successful!"
echo "  DLL: $DLL_PATH"
echo "  ZIP: $ZIP_PATH"
echo "  MD5: $CHECKSUM"
