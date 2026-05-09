#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

echo "[GOI] Compiling..."
msbuild TrinyTokGOI.csproj /p:Configuration=Release

echo "[GOI] Done: bin/Release/TrinyTokGOI.dll"
