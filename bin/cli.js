#!/usr/bin/env node
'use strict';
/*
 * unity-mcp-bridge — bridge Claude Code (in WSL) to the Unity Editor on Windows.
 *
 * Commands:
 *   unity-mcp-bridge check     Detect WSL, Windows Python, Unity Hub + Editors, Claude CLI.
 *   unity-mcp-bridge connect   Register the MCP server with Claude Code + stage the exe to ~/ (also runs on npm install).
 *   unity-mcp-bridge build     Compile the Windows setup exe from source (uses the built-in csc).
 *   unity-mcp-bridge setup     (default) check -> ensure exe -> run the exe to wire everything up.
 *   unity-mcp-bridge run       Just run the setup exe.
 *
 * Runs as a Linux (WSL) Node process that drives Windows via interop (python.exe / wsl.exe / the exe).
 * The setup exe is spawned with cwd=/mnt/c so the Windows process has a real C:\ working directory
 * and can read this package's files over the \\wsl.localhost share (a Windows process launched from a
 * WSL-share cwd cannot resolve those UNC paths — this is the fix).
 */
const fs = require('fs');
const path = require('path');
const { execSync, spawnSync } = require('child_process');

const ROOT = path.resolve(__dirname, '..');
const EXE = path.join(ROOT, 'setup', 'UnityMCPSetup.exe');
const EXE_CS = path.join(ROOT, 'setup', 'UnityMCPSetup.cs');
const CSC = '/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe';
const HUB = '/mnt/c/Program Files/Unity Hub/Unity Hub.exe';
const EDITORS_DIR = '/mnt/c/Program Files/Unity/Hub/Editor';

// ---- small helpers -------------------------------------------------------

const C = {
  g: (s) => `\x1b[32m${s}\x1b[0m`,
  y: (s) => `\x1b[33m${s}\x1b[0m`,
  r: (s) => `\x1b[31m${s}\x1b[0m`,
  c: (s) => `\x1b[36m${s}\x1b[0m`,
  b: (s) => `\x1b[1m${s}\x1b[0m`,
};

function tryExec(cmd) {
  try {
    return execSync(cmd, { stdio: ['ignore', 'pipe', 'ignore'] }).toString().trim();
  } catch {
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

function toWinPath(p) {
  const r = tryExec(`wslpath -w ${JSON.stringify(p)}`);
  return r || null;
}

function winUser() {
  return tryExec('cmd.exe /c "echo %USERNAME%"') || 'user';
}

// ---- detection -----------------------------------------------------------

function detect() {
  const editors = fs.existsSync(EDITORS_DIR)
    ? fs.readdirSync(EDITORS_DIR).filter((d) => fs.existsSync(path.join(EDITORS_DIR, d, 'Editor', 'Unity.exe')))
    : [];
  return {
    wsl: isWSL(),
    distro: process.env.WSL_DISTRO_NAME || null,
    winPython: tryExec('python.exe --version'),
    claude: tryExec('command -v claude'),
    hub: fs.existsSync(HUB),
    editors,
    csc: fs.existsSync(CSC),
    exe: fs.existsSync(EXE),
  };
}

function report(d) {
  const ok = (b) => (b ? C.g('OK   ') : C.r('MISS '));
  console.log(C.b('\n  Unity ⇄ MCP ⇄ Claude Code — environment check\n'));
  console.log(`  ${ok(d.wsl)} WSL            ${d.wsl ? `(distro: ${d.distro || '?'})` : 'not running under WSL'}`);
  console.log(`  ${ok(!!d.winPython)} Windows Python ${d.winPython || '— install from python.org'}`);
  console.log(`  ${ok(!!d.claude)} Claude CLI     ${d.claude || '— not on PATH in this shell'}`);
  console.log(`  ${ok(d.hub)} Unity Hub      ${d.hub ? '' : '— install Unity Hub (unity.com/download)'}`);
  console.log(
    `  ${ok(d.editors.length > 0)} Unity Editor   ${
      d.editors.length ? d.editors.join(', ') : '— no Editor installed (install a 6000.x LTS via Hub)'
    }`
  );
  console.log(`  ${ok(d.csc)} .NET csc.exe   ${d.csc ? '(can build the setup exe)' : '— missing'}`);
  console.log(`  ${ok(d.exe)} setup exe      ${d.exe ? EXE : '— will build on setup'}`);
  console.log('');
  return d;
}

// ---- build the setup exe -------------------------------------------------

function build() {
  if (!fs.existsSync(CSC)) throw new Error('csc.exe (.NET Framework) not found on Windows — cannot build the exe.');
  const stage = `/mnt/c/Users/${winUser()}/.unity-mcp-build`;
  fs.mkdirSync(stage, { recursive: true });
  fs.copyFileSync(EXE_CS, path.join(stage, 'UnityMCPSetup.cs'));
  console.log(C.c('Building UnityMCPSetup.exe ...'));
  const r = spawnSync(CSC, ['-nologo', '-optimize+', '-out:UnityMCPSetup.exe', 'UnityMCPSetup.cs'], {
    cwd: stage, // a /mnt/c path => Windows cwd on C:, so csc runs cleanly
    stdio: 'inherit',
  });
  if (r.status !== 0) throw new Error('csc failed to build the exe.');
  fs.copyFileSync(path.join(stage, 'UnityMCPSetup.exe'), EXE);
  console.log(C.g('Built ') + EXE);
}

// ---- run the setup exe ---------------------------------------------------

function run() {
  if (!fs.existsSync(EXE)) build();
  const source = toWinPath(ROOT); // \\wsl.localhost\<distro>\...  (this package's files)
  const distro = process.env.WSL_DISTRO_NAME || 'Ubuntu';
  if (!source) throw new Error('Could not resolve a Windows path for this package (is wslpath available?).');
  console.log(C.c(`\nLaunching setup exe (source: ${source}) ...\n`));
  const r = spawnSync(EXE, ['--source', source, '--distro', distro], {
    cwd: '/mnt/c', // give the Windows process a real C:\ cwd so it can read the \\wsl.localhost source
    stdio: 'inherit',
  });
  return r.status || 0;
}

// ---- cli -----------------------------------------------------------------

function help() {
  console.log(`
${C.b('unity-mcp-bridge')} — bridge Claude Code (WSL) to the Unity Editor (Windows)

Usage:
  unity-mcp-bridge [command]

Commands:
  check      Detect WSL, Windows Python, Unity Hub + Editors, Claude CLI (no changes)
  connect    Register the MCP server with Claude Code + drop the setup exe in ~/ (auto-runs on npm install)
  link <p>   Remember a Unity project + copy the C# bridge in; npm update then refreshes it automatically
  build      Compile the Windows setup exe from source
  setup      (default) check, ensure the exe exists, then run it to wire everything up
  run        Just run the setup exe
  help       Show this help

The setup exe stages the MCP server to your Windows profile, installs its Python deps,
registers it with Claude Code, and (optionally) creates a Unity project + injects the live
Editor bridge. Reload Claude Code afterwards so the unity_* tools load.
`);
}

function main() {
  const cmd = (process.argv[2] || 'setup').toLowerCase();
  try {
    switch (cmd) {
      case 'check':
      case 'doctor':
        report(detect());
        return;
      case 'connect':
        require('../scripts/connect').connect();
        return;
      case 'link':
        require('../scripts/connect').link(process.argv[3]);
        return;
      case 'build':
        build();
        return;
      case 'run':
        process.exit(run());
        return;
      case 'help':
      case '-h':
      case '--help':
        help();
        return;
      case 'setup':
      default: {
        const d = report(detect());
        if (!d.wsl) console.log(C.y('  Note: not detected as WSL — the bridge expects Claude Code in WSL + Unity on Windows.'));
        if (!d.winPython) {
          console.log(C.r('  Windows Python is required. Install Python 3.11+ from python.org (Add to PATH), then re-run.'));
          process.exit(1);
        }
        if (!d.hub || d.editors.length === 0) {
          console.log(C.y('  Unity Hub/Editor missing — the setup exe will help you install them, then re-run this.'));
        }
        process.exit(run());
      }
    }
  } catch (e) {
    console.error(C.r('Error: ') + e.message);
    process.exit(1);
  }
}

main();
