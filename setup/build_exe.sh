#!/usr/bin/env bash
# Compile UnityMCPSetup.cs -> UnityMCPSetup.exe using the .NET Framework C# compiler that ships
# with Windows (no installs needed). Staged on C: to avoid UNC-cwd issues during compilation.
set -euo pipefail

CSC="/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
WINROOT="/mnt/c/Users/mjmob/unity-mcp/setup"
SRC="$(cd "$(dirname "$0")" && pwd)/UnityMCPSetup.cs"

mkdir -p "$WINROOT"
cp "$SRC" "$WINROOT/UnityMCPSetup.cs"

# Compile with cwd on C: so csc doesn't inherit a WSL/UNC working directory.
cd "$WINROOT"
"$CSC" -nologo -optimize+ -out:"UnityMCPSetup.exe" "UnityMCPSetup.cs"

# Mirror the built exe back into the repo for convenience.
cp "$WINROOT/UnityMCPSetup.exe" "$(dirname "$SRC")/UnityMCPSetup.exe"
echo "Built: C:\\Users\\mjmob\\unity-mcp\\setup\\UnityMCPSetup.exe"
