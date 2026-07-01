# BimOn MCP — Install Guide

## Requirements
- Windows 10/11 (x64)
- Claude Desktop and/or Claude Code
- One or more of: Autodesk Revit / Navisworks Manage / AutoCAD (2025–2027)

## Install (recommended — installer)
1. **Close** Revit, Navisworks, AutoCAD **and** Claude Desktop / Claude Code.
2. Run **`BimOnMcp_Setup_v1.0.0.exe`** as a **normal user** (NOT “Run as administrator”).
   - Running elevated installs into the wrong account’s `%AppData%` and the MCP won’t be found.
3. Accept the MIT license, pick components (Revit / Navisworks / AutoCAD), install.
4. The installer merges MCP servers into your Claude config(s), preserving existing settings.
5. **Restart Claude Desktop / Claude Code.**

## Verify
- In Claude, the tools `BimOn-Revit`, `BimOn-Navisworks`, `BimOn-AutoCAD` should be available.
- Open the host (e.g. Revit) → **Add-Ins ▸ BimOn** → the **BimOn AI Scripts** palette opens.
- In the palette, the top bar shows connection status; click **연결 / Connect** to make that instance the MCP target.
- Ask Claude, e.g. *“Revit 문서 제목 알려줘”* or *“list all windows”*.

## Multiple instances
Running the same product more than once is supported. Only the **Connected (ON)** instance receives commands.
Switch by clicking **Connect** in the instance you want; if the connected one closes and only one remains,
it auto-connects.

## Dynamo (Revit)
Open **Dynamo for Revit** with a graph, then Claude can read/build/run it via the `dynamo_*` tools
(status, get_graph, get_node_values, set_input, run_current, add_node/code_block/python_node, connect,
delete_node, search_nodes, build_graph — 12 tools). The included `skills/dynamo-mcp` skill guides optimal usage.

## Manual config (if needed)
If auto-registration didn’t run, add to `%AppData%\Claude\claude_desktop_config.json` (Desktop) and/or
`%UserProfile%\.claude.json` (Code), then restart Claude:
```json
{
  "mcpServers": {
    "BimOn-Revit":      { "command": "%AppData%\\BimOnAI\\BimOnMcpBridge.exe", "args": ["--target","revit"] },
    "BimOn-Navisworks": { "command": "%AppData%\\BimOnAI\\BimOnMcpBridge.exe", "args": ["--target","navisworks"] },
    "BimOn-AutoCAD":    { "command": "%AppData%\\BimOnAI\\BimOnMcpBridge.exe", "args": ["--target","autocad"] }
  }
}
```
(Use the real expanded path, e.g. `C:\Users\<you>\AppData\Roaming\BimOnAI\BimOnMcpBridge.exe`.)

## Uninstall
Control Panel ▸ Programs ▸ **BimOn MCP** ▸ Uninstall. Remove the MCP entries from your Claude config(s) if desired.

## Troubleshooting
- **Tools not showing** → restart Claude; confirm the config has the BimOn entries and the bridge path is correct.
- **“not running / not connected”** → open the host and click **Connect** in the BimOn palette.
- **AutoCAD plugin not loading** → ensure no stale bundle in `%ProgramData%\Autodesk\ApplicationPlugins` (the installer removes it).
