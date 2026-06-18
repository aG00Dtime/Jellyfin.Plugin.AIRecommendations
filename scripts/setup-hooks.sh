#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

git config core.hooksPath .githooks
echo "Git hooks path set to .githooks"
echo ""
echo "  pre-commit  verify VERSION.txt matches csproj + manifest"
echo "  pre-push    bump patch version and commit before push"
echo ""
echo "Skip auto-bump: SKIP_VERSION_BUMP=1 git push"
