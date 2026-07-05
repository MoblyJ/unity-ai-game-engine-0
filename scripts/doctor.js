'use strict';
/*
 * doctor.js — one-shot health check for the Unity ⇄ MCP ⇄ Claude Code bridge.
 *
 * Run:  unity-mcp-bridge doctor   (or  npx unity-mcp-bridge doctor)
 *
 * Checks, in the order they matter, and prints PASS / WARN / FAIL with a fix hint:
 *   WSL · Node · Claude CLI · Windows Python · Python deps (mcp+pydantic) ·
 *   server staged to %USERPROFILE% · MCP registered with Claude Code ·
 *   Unity Editor installed · Unity bridge port 8765 · setup exe in ~/
 * Exit code = number of FAILs (0 = healthy), so it's scriptable.
 */
const fs = require('fs');
const path = require('path');
const os = require('os');
const { execSync } = require('child_process');

const C = {
  g: (s) => `\x1b[32m${s}\x1b[0m`,
  y: (s) => `\x1b[33m${s}\x1b[0m`,
  r: (s) => `\x1b[31m${s}\x1b[0m`,
  c: (s) => `\x1b[36m${s}\x1b[0m`,
  b: (s) => `\x1b[1m${s}\x1b[0m`,
  d: (s) => `\x1b[2m${s}\x1b[0m`,
};

function sh(cmd) {
  try {
    return execSync(cmd, { stdio: ['ignore', 'pipe', 'pipe'], timeout: 20000 }).toString().trim();
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
function winToWsl(p) {
  return sh(`wslpath -u ${JSON.stringify(p)}`);
}

const PASS = 'PASS', WARN = 'WARN', FAIL = 'FAIL', SKIP = 'SKIP';

function doctor() {
  console.log(C.b('\n  unity-mcp-bridge doctor — environment health check\n'));

  const wsl = isWSL();
  const R = [];
  const add = (status, name, detail, hint) => R.push({ status, name, detail, hint: hint || '' });

  // 1. WSL
  add(wsl ? PASS : WARN, 'WSL', wsl ? (process.env.WSL_DISTRO_NAME || 'linux') : 'not under WSL',
    wsl ? '' : 'This bridge expects Claude Code in WSL with Unity on Windows.');

  // 2. Node
  add(PASS, 'Node', process.version, '');

  // 3. Claude CLI
  const claude = sh('command -v claude');
  add(claude ? PASS : FAIL, 'Claude CLI', claude || 'not found on PATH',
    claude ? '' : 'Install Claude Code and make sure `claude` is on PATH.');

  if (wsl) {
    // 4. Windows Python
    const winPy = sh('python.exe --version 2>&1');
    const havePy = !!winPy && /Python/.test(winPy);
    add(havePy ? PASS : FAIL, 'Windows Python', havePy ? winPy : 'python.exe not found',
      havePy ? '' : 'Install Python 3.11+ from python.org (tick “Add to PATH”). The MCP server runs on Windows Python.');

    // 5. Python deps
    if (havePy) {
      const deps = sh('python.exe -c "import mcp, pydantic; print(\'ok\')" 2>&1');
      const ok = deps === 'ok';
      add(ok ? PASS : FAIL, 'Python deps (mcp+pydantic)', ok ? 'importable' : 'missing / import failed',
        ok ? '' : 'Run `unity-mcp-bridge connect` (installs mcp + pydantic on Windows Python).');
    } else {
      add(SKIP, 'Python deps (mcp+pydantic)', 'skipped — no Windows Python', '');
    }

    // 6. Server staged to %USERPROFILE%\unity-mcp\server
    const profWin = sh('cmd.exe /c "echo %USERPROFILE%"');
    if (profWin && /^[A-Za-z]:\\/.test(profWin)) {
      const profWsl = winToWsl(profWin);
      const serverWin = profWin.replace(/\\+$/, '') + '\\unity-mcp\\server\\unity_editor_mcp.py';
      const serverWsl = profWsl ? path.join(profWsl, 'unity-mcp', 'server', 'unity_editor_mcp.py') : null;
      const staged = !!serverWsl && fs.existsSync(serverWsl);
      add(staged ? PASS : FAIL, 'Server staged', staged ? serverWin : 'missing: ' + serverWin,
        staged ? '' : 'Run `unity-mcp-bridge connect` to stage the server to your Windows profile.');
    } else {
      add(WARN, 'Server staged', 'could not resolve %USERPROFILE%', 'Run `unity-mcp-bridge connect`.');
    }
  } else {
    add(SKIP, 'Windows Python', 'skipped — not WSL', '');
    add(SKIP, 'Python deps (mcp+pydantic)', 'skipped — not WSL', '');
    add(SKIP, 'Server staged', 'skipped — not WSL', '');
  }

  // 7. MCP registered with Claude Code
  const mcp = sh('claude mcp get unity-editor');
  const registered = !!mcp && /unity-editor/.test(mcp);
  let mcpCmd = '';
  if (mcp) {
    const cmdM = mcp.match(/Command:\s*(.+)/);
    const argM = mcp.match(/Args:\s*(.+)/);
    if (cmdM) mcpCmd = (cmdM[1] || '').trim() + ' ' + ((argM && argM[1]) || '').trim();
  }
  add(registered ? PASS : FAIL, 'MCP registered', registered ? (mcpCmd || 'unity-editor') : 'unity-editor not registered',
    registered ? '' : 'Run `unity-mcp-bridge connect` to register the server with Claude Code.');

  if (wsl) {
    // 8. Unity Editor installed
    const hub = fs.existsSync('/mnt/c/Program Files/Unity Hub/Unity Hub.exe');
    const editorsDir = '/mnt/c/Program Files/Unity/Hub/Editor';
    let editors = [];
    try {
      editors = fs.readdirSync(editorsDir).filter((d) => fs.existsSync(path.join(editorsDir, d, 'Editor', 'Unity.exe')));
    } catch { /* none */ }
    add(editors.length ? PASS : WARN, 'Unity Editor', editors.length ? editors.join(', ') : (hub ? 'Hub only — no Editor' : 'Unity Hub not found'),
      editors.length ? '' : 'Install a Unity 6 (6000.x) Editor via Unity Hub.');

    // 9. Unity bridge port (Unity open with the bridge?)
    const port = sh('powershell.exe -NoProfile -Command "if((Test-NetConnection 127.0.0.1 -Port 8765 -WarningAction SilentlyContinue).TcpTestSucceeded){\'open\'}else{\'closed\'}"');
    const open = port === 'open';
    add(open ? PASS : WARN, 'Unity bridge (127.0.0.1:8765)', open ? 'listening' : 'not listening',
      open ? '' : 'Open Unity with the Agent Bridge (run ~/UnityMCPSetup.exe). Only needed when you want to build.');
  } else {
    add(SKIP, 'Unity Editor', 'skipped — not WSL', '');
    add(SKIP, 'Unity bridge (127.0.0.1:8765)', 'skipped — not WSL', '');
  }

  // 10. Setup exe staged in ~/
  const homeExe = path.join(os.homedir(), 'UnityMCPSetup.exe');
  const hasHomeExe = fs.existsSync(homeExe);
  add(hasHomeExe ? PASS : WARN, 'Setup exe in ~/', hasHomeExe ? homeExe : 'not found',
    hasHomeExe ? '' : 'Run `unity-mcp-bridge connect` to drop UnityMCPSetup.exe in ~/ (copy to Windows + run).');

  // ---- print ----
  const badge = { PASS: C.g('PASS'), WARN: C.y('WARN'), FAIL: C.r('FAIL'), SKIP: C.d('SKIP') };
  for (const rr of R) {
    console.log('  ' + badge[rr.status] + '  ' + C.b(rr.name.padEnd(26)) + ' ' + C.d(rr.detail));
    if (rr.hint && (rr.status === FAIL || rr.status === WARN)) console.log('        ' + C.c('→ ' + rr.hint));
  }

  const nFail = R.filter((r) => r.status === FAIL).length;
  const nWarn = R.filter((r) => r.status === WARN).length;
  const nPass = R.filter((r) => r.status === PASS).length;
  console.log('');
  console.log('  ' + C.b('Summary: ') + C.g(nPass + ' pass') + ' · ' + (nWarn ? C.y(nWarn + ' warn') : nWarn + ' warn') + ' · ' + (nFail ? C.r(nFail + ' fail') : nFail + ' fail'));
  if (nFail) console.log('  ' + C.y('Fix the FAIL items above (usually: install Windows Python, then ') + C.c('unity-mcp-bridge connect') + C.y('), then re-run ') + C.c('unity-mcp-bridge doctor') + C.y('.'));
  else if (nWarn) console.log('  ' + C.g('Core setup looks good.') + ' Warnings are usually just “Unity not open yet”.');
  else console.log('  ' + C.g('All good — you’re ready to build games in Unity.'));
  console.log('');
  return nFail;
}

module.exports = { doctor };

if (require.main === module) process.exit(doctor());
