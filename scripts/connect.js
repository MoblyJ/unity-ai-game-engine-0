'use strict';
/*
 * connect.js — auto-connect the Unity Editor MCP server to the Claude Code host,
 * and stage the Windows setup exe into the user's WSL home.
 *
 * Used by:
 *   - scripts/postinstall.js  (runs automatically on `npm install`)
 *   - bin/cli.js connect      (re-run manually any time)
 *
 * What it does (all best-effort — never throws, so it can't break `npm install`):
 *   1. Copies setup/UnityMCPSetup.exe -> ~/UnityMCPSetup.exe  (copy to Windows + run for the Unity side).
 *   2. Stages the MCP server to  %USERPROFILE%\unity-mcp\server  (so 127.0.0.1 reaches Unity — same
 *      machine as the Editor). Falls back to the repo path over \\wsl.localhost if that fails.
 *   3. Installs the server's Windows-Python deps (mcp + pydantic) if missing.
 *   4. Registers it with Claude Code:  claude mcp add -s user unity-editor -- python.exe <server>
 *
 * Opt out entirely with  UNITY_MCP_NO_POSTINSTALL=1.
 * Override the MCP scope with  UNITY_MCP_SCOPE=local|project|user  (default: user).
 */
const fs = require('fs');
const path = require('path');
const os = require('os');
const { execSync } = require('child_process');

const ROOT = path.resolve(__dirname, '..');
const EXE = path.join(ROOT, 'setup', 'UnityMCPSetup.exe');
const SERVER_SRC = path.join(ROOT, 'server');
const BRIDGE_SRC = path.join(ROOT, 'unity', 'Assets', 'AgentBridge');
const CONFIG = path.join(os.homedir(), '.unity-mcp-bridge.json'); // remembers linked Unity projects

const C = {
  g: (s) => `\x1b[32m${s}\x1b[0m`,
  y: (s) => `\x1b[33m${s}\x1b[0m`,
  r: (s) => `\x1b[31m${s}\x1b[0m`,
  c: (s) => `\x1b[36m${s}\x1b[0m`,
  b: (s) => `\x1b[1m${s}\x1b[0m`,
};
const ok = (s) => console.log('  ' + C.g('✓ ') + s);
const warn = (s) => console.log('  ' + C.y('! ') + s);
const info = (s) => console.log('    ' + s);

function sh(cmd) {
  try {
    return execSync(cmd, { stdio: ['ignore', 'pipe', 'pipe'] }).toString().trim();
  } catch (e) {
    return null;
  }
}
function isWSL() {
  try {
    return /microsoft/i.test(fs.readFileSync('/proc/version', 'utf8'));
  } catch {
    return false;
  }
}
function wslToWin(p) {
  return sh(`wslpath -w ${JSON.stringify(p)}`);
}
function winToWsl(p) {
  return sh(`wslpath -u ${JSON.stringify(p)}`);
}

// 1) Drop the Windows setup exe into ~/ for the user to copy to Windows.
function stageExeToHome() {
  if (!fs.existsSync(EXE)) {
    warn('setup exe not found in package (setup/UnityMCPSetup.exe) — skipping exe copy.');
    return null;
  }
  const dest = path.join(os.homedir(), 'UnityMCPSetup.exe');
  fs.copyFileSync(EXE, dest);
  ok('Setup exe placed at ' + C.c(dest));
  info('Copy it to Windows and double-click to wire up the Unity side, e.g.:');
  const winUser = sh('cmd.exe /c "echo %USERNAME%"');
  const target = winUser ? `/mnt/c/Users/${winUser}/Desktop/` : '/mnt/c/Users/<you>/Desktop/';
  info(C.c(`cp ~/UnityMCPSetup.exe ${target}`));
  return dest;
}

// 2) Stage the MCP server next to Unity (on Windows). Returns the Windows path to the entry script.
function stageServerToWindows() {
  const profWin = sh('cmd.exe /c "echo %USERPROFILE%"'); // e.g. C:\Users\mjmob
  if (!profWin || !/^[A-Za-z]:\\/.test(profWin)) return null;
  const profWsl = winToWsl(profWin);
  if (!profWsl || !fs.existsSync(profWsl)) return null;

  const destWsl = path.join(profWsl, 'unity-mcp', 'server');
  fs.mkdirSync(destWsl, { recursive: true });
  for (const name of fs.readdirSync(SERVER_SRC)) {
    if (name === '__pycache__') continue;
    const s = path.join(SERVER_SRC, name);
    if (fs.statSync(s).isFile()) fs.copyFileSync(s, path.join(destWsl, name));
  }
  const destWin = profWin.replace(/\\+$/, '') + '\\unity-mcp\\server';
  ok('MCP server staged → ' + C.c(destWin));
  return { destWin, script: destWin + '\\unity_editor_mcp.py', reqWin: destWin + '\\requirements.txt' };
}

// 3) Ensure Windows-Python has mcp + pydantic (best-effort, skipped if already importable).
function ensureDeps(reqWin) {
  const already = sh('python.exe -c "import mcp, pydantic; print(1)"');
  if (already === '1') {
    ok('Windows-Python deps already present (mcp + pydantic).');
    return;
  }
  info('Installing Windows-Python deps (mcp + pydantic) — one-time, may take a minute...');
  const out = sh(`python.exe -m pip install --user -r ${JSON.stringify(reqWin)}`);
  if (out === null) warn('Could not install deps automatically — the setup exe will do it on Windows.');
  else ok('Windows-Python deps installed.');
}

// --- linked-project config (so `npm update` also refreshes the in-project C# bridge) ---
function readConfig() {
  try {
    return JSON.parse(fs.readFileSync(CONFIG, 'utf8'));
  } catch {
    return { projects: [] };
  }
}
function writeConfig(cfg) {
  try {
    fs.writeFileSync(CONFIG, JSON.stringify(cfg, null, 2) + '\n');
  } catch (e) {
    warn('could not save ' + CONFIG + ': ' + e.message);
  }
}
function copyDir(src, dest) {
  fs.mkdirSync(dest, { recursive: true });
  for (const name of fs.readdirSync(src)) {
    const s = path.join(src, name);
    const d = path.join(dest, name);
    if (fs.statSync(s).isDirectory()) copyDir(s, d);
    else fs.copyFileSync(s, d);
  }
}
// Copy unity/Assets/AgentBridge into a Unity project (accepts a Windows or WSL path).
function refreshBridge(projectPath) {
  if (!fs.existsSync(BRIDGE_SRC)) return false;
  let projWsl = projectPath;
  if (/^[A-Za-z]:\\/.test(projectPath)) projWsl = winToWsl(projectPath); // Windows path -> /mnt/...
  if (!projWsl) return false;
  const assets = path.join(projWsl, 'Assets');
  if (!fs.existsSync(assets)) {
    warn('linked project has no Assets folder (skipped): ' + projectPath);
    return false;
  }
  copyDir(BRIDGE_SRC, path.join(assets, 'AgentBridge'));
  return true;
}
// Called on every connect/update: refresh the bridge in all linked projects.
function refreshLinkedBridges() {
  const cfg = readConfig();
  const projects = (cfg.projects || []).filter(Boolean);
  if (!projects.length) return;
  for (const p of projects) {
    if (refreshBridge(p)) ok('Refreshed Unity bridge in ' + C.c(p) + ' (restart Unity to load it).');
  }
}
// `unity-mcp-bridge link <project>` — remember a Unity project + copy the bridge in now.
function link(projectPath) {
  console.log(C.b('\n  unity-mcp-bridge — link a Unity project\n'));
  if (!projectPath) {
    warn('Usage: unity-mcp-bridge link "C:\\\\Users\\\\you\\\\MyGame"  (the folder containing Assets)');
    return;
  }
  const cfg = readConfig();
  cfg.projects = cfg.projects || [];
  if (!cfg.projects.includes(projectPath)) cfg.projects.push(projectPath);
  writeConfig(cfg);
  ok('Linked ' + C.c(projectPath));
  if (refreshBridge(projectPath)) ok('Unity bridge copied in. Restart Unity to compile it.');
  info('From now on, `npm update` (or `unity-mcp-bridge connect`) refreshes this project’s bridge automatically.');
  console.log('');
}

// 4) Register with Claude Code.
function registerMcp(scriptWin) {
  const scope = (process.env.UNITY_MCP_SCOPE || 'user').toLowerCase();
  // Clear any prior registration in every scope so we don't duplicate the name.
  sh('claude mcp remove unity-editor -s local');
  sh('claude mcp remove unity-editor -s project');
  sh('claude mcp remove unity-editor -s user');
  const cmd = `claude mcp add -s ${scope} unity-editor -- python.exe ${JSON.stringify(scriptWin)}`;
  const out = sh(cmd);
  if (out === null) {
    warn('Auto-register failed. Run this in WSL:');
    info(C.c(`claude mcp add -s ${scope} unity-editor -- python.exe "${scriptWin}"`));
    return false;
  }
  ok(`MCP server connected to Claude Code (unity-editor, ${scope} scope).`);
  return true;
}

function connect() {
  console.log(C.b('\n  unity-mcp-bridge — connecting to Claude Code\n'));

  if (process.env.UNITY_MCP_NO_POSTINSTALL) {
    warn('UNITY_MCP_NO_POSTINSTALL set — skipping auto-connect.');
    return;
  }
  if (!isWSL()) {
    warn('Not running under WSL — skipping auto-connect.');
    info('This bridge expects Claude Code in WSL with Unity on Windows.');
    return;
  }

  try {
    stageExeToHome();
  } catch (e) {
    warn('exe copy failed: ' + e.message);
  }

  // Record the package path so a standalone run of UnityMCPSetup.exe can auto-find this install.
  try {
    const srcWin = wslToWin(ROOT);
    if (srcWin) {
      fs.writeFileSync(path.join(os.homedir(), '.unity-mcp-source'), srcWin + '\n');
      ok('Recorded package path → ' + C.c('~/.unity-mcp-source'));
    }
  } catch (e) { /* non-fatal */ }

  const hasClaude = sh('command -v claude');
  const hasWinPy = sh('python.exe --version');

  // Decide which server path to register.
  let scriptWin = null;
  if (hasWinPy) {
    try {
      const staged = stageServerToWindows();
      if (staged) {
        ensureDeps(staged.reqWin);
        scriptWin = staged.script;
      }
    } catch (e) {
      warn('server staging failed: ' + e.message);
    }
  } else {
    warn('Windows Python (python.exe) not found — install Python 3.11+ (Add to PATH).');
  }
  if (!scriptWin) {
    // Fallback: point at the repo copy over the WSL share.
    scriptWin = wslToWin(path.join(SERVER_SRC, 'unity_editor_mcp.py'));
    if (scriptWin) info('Falling back to the repo server path over \\\\wsl.localhost.');
  }

  // Refresh the C# bridge in any linked Unity projects (so `npm update` delivers bridge changes too).
  try {
    refreshLinkedBridges();
  } catch (e) {
    warn('bridge refresh failed: ' + e.message);
  }

  if (!hasClaude) {
    warn('claude CLI not on PATH — could not auto-register the MCP server.');
    info('After installing Claude Code, run: ' + C.c('npx unity-mcp-bridge connect'));
  } else if (scriptWin) {
    if (registerMcp(scriptWin)) {
      console.log('');
      info(C.b('Reload Claude Code') + ' (restart it, or run ' + C.c('/mcp') + ') to load the unity_* tools.');
    }
  } else {
    warn('Could not resolve a server path to register.');
  }

  console.log('');
}

module.exports = { connect, link };

if (require.main === module) connect();
