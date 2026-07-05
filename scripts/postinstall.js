'use strict';
/*
 * postinstall — runs automatically on `npm install`.
 * Auto-connects the Unity MCP server to Claude Code and stages the Windows setup
 * exe into ~/. All work is best-effort; this must never fail the install.
 */
try {
  require('./connect').connect();
} catch (e) {
  // Never break `npm install` — just report and move on.
  console.log('  unity-mcp-bridge postinstall skipped: ' + (e && e.message ? e.message : e));
}
