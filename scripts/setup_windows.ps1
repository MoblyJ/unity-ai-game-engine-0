# setup_windows.ps1 — stage the MCP server on the Windows filesystem, install its Python deps
# into Windows Python, and print the exact command to register it with Claude Code (WSL).
#
# The server MUST run on Windows (so 127.0.0.1 reaches Unity) AND from a real C:\ path — a Windows
# process launched from inside the WSL share can't reliably resolve \\wsl.localhost\... UNC paths.
#
# Run from Windows PowerShell:
#   powershell -ExecutionPolicy Bypass -File scripts\setup_windows.ps1
# or from WSL:
#   powershell.exe -ExecutionPolicy Bypass -File scripts/setup_windows.ps1

$ErrorActionPreference = "Stop"

# Source (this repo — a UNC/WSL path when run from WSL) and the local Windows destination.
$srcServer = Join-Path (Split-Path -Parent $PSScriptRoot) "server"
$destRoot = Join-Path $env:USERPROFILE "unity-mcp"
$destServer = Join-Path $destRoot "server"

Write-Host "Staging server -> $destServer"
New-Item -ItemType Directory -Force -Path $destRoot | Out-Null
Copy-Item -Recurse -Force -Path $srcServer -Destination $destRoot

Write-Host "Installing Python deps into Windows python.exe ..."
python.exe -m pip install --user -r (Join-Path $destServer "requirements.txt")

Write-Host ""
Write-Host "Ready. Register the MCP server with Claude Code (run this in WSL):" -ForegroundColor Green
Write-Host ""
Write-Host "  claude mcp add unity-editor -- python.exe `"$destServer\unity_editor_mcp.py`"" -ForegroundColor Cyan
Write-Host ""
Write-Host "Re-run this script after editing anything under server/ to re-stage it."
Write-Host "Then in Unity: Window > Agent Bridge > Start.  Default port 8765."
