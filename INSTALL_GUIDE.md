# BimOn MCP — Install Guide

> **ChatGPT connection limit:** This installer registers Claude Desktop, Claude Code CLI and local Codex. It does **not** connect ordinary ChatGPT web/app chats through apps/plugins. A local Codex host in ChatGPT desktop is a separate supported execution context. See the [support matrix and official references](README.md#ai-client-support-114). Reinstallation/repair does not create a ChatGPT account plugin; a remote MCP relay and authentication are not included.

## Requirements
- Windows 10/11 (x64)
- Claude Desktop, Claude Code CLI, or local Codex CLI/Desktop
- One or more of: Autodesk Revit / Navisworks Manage / AutoCAD (2025–2027)

## Install (recommended — installer)
1. **Close** Revit, Navisworks, AutoCAD **and** the Claude / Codex clients using BimOn.
2. Run **`BimOnMcp_Setup_v1.1.4.exe`** as a **normal user** (NOT “Run as administrator”). Download it from [GitHub release 1.1.4](https://github.com/General-Soju/BimOnMcp/releases/tag/v1.1.4), together with its SHA-256 checksum.
   - Running elevated installs into the wrong account’s `%AppData%` and the MCP won’t be found.
3. Accept the MIT license, pick components (Revit / Navisworks / AutoCAD), install.
4. Select the AI clients to register. The installer merges their MCP configurations, preserving existing settings.
5. **Restart Claude Desktop / Claude Code / Codex.**

If existing Claude JSON is malformed, installation preserves it and reports that registration needs attention (exit code 10 for automation). Correct the JSON and rerun the installed `MergeClaudeConfig.ps1` with `-BridgePath` set to the full bridge EXE path. Do not replace an existing configuration wholesale.

The installer offers three registration checkboxes: **Claude Desktop**, **Claude Code CLI**, and **Codex CLI / Desktop**. They are selected by default. New settings are created even before first use; existing servers, preferences and approval settings are preserved. Codex CLI does not need to be installed to register the desktop app. CLI and desktop share the Codex entry, so it is registered once.

Registration report: `%APPDATA%\BimOnAI\McpRegistration.json`. Failed clients do not block other clients. Exit code 10 means some registration needs attention; the report gives each path and result without dumping credentials. Changed existing configs receive a uniquely named `.bimon-*.bak` backup beside them. Keep those backups private. TOML formatting may be normalized; semantic settings and comments are preserved by the parser.

If an AI app is installed/initialized later, or creates a new Desktop profile, run **Start > BimOn AI > Repair MCP registration**, then restart that AI app. This repair registers all supported targets with all three clients. Custom `CODEX_HOME` and `CLAUDE_CONFIG_DIR` environment variables are honored when running setup/repair. Claude Code uses `<CLAUDE_CONFIG_DIR>/.claude.json` when set; Codex uses `<CODEX_HOME>/config.toml`.

For a read-only diagnostic, run the installed bridge with `--register-clients --diagnose`. For selective registration use `--register-clients --clients code,codex` (choices: `desktop`, `code`, `codex`, `none`). Registration confirms configuration, not live model connectivity. An existing disabled server or restrictive policy stays disabled/restrictive; registration does not bypass security settings.

## Verify
- In the supported Claude / local Codex client, the tools `BimOn-Revit`, `BimOn-Navisworks`, `BimOn-AutoCAD` should be available.
- Open the host (e.g. Revit) → **Add-Ins ▸ BimOn** → the **BimOn AI Scripts** palette opens.
- In the palette, the top bar shows connection status; click **연결 / Connect** to make that instance the MCP target.
- Ask the AI client, e.g. *“Revit 문서 제목 알려줘”* or *“list all windows”*.

## Multiple instances
Running the same product more than once is supported. Only the **Connected (ON)** instance receives commands.
Switch by clicking **Connect** in the instance you want; if the connected one closes and only one remains,
it auto-connects.

## Dynamo (Revit)
Open **Dynamo for Revit** with a graph, then the AI client can read/build/run it via the `dynamo_*` tools
(status, get_graph, get_node_values, set_input, run_current, add_node/code_block/python_node, connect,
delete_node, search_nodes, build_graph — 12 tools). The included `skills/dynamo-mcp` skill guides optimal usage.

## Manual Claude config (if needed)

This JSON example is for Claude only. Codex uses `%USERPROFILE%\.codex\config.toml` or `<CODEX_HOME>/config.toml`; use the repair action above to register it.
Newer packaged Claude Desktop installations may use `%LocalAppData%\Claude-Data\claude_*\claude_desktop_config.json`. Edit the existing profile used by your app. The installer detects and merges existing profiles in this location as well.

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
Control Panel ▸ Programs ▸ **BimOn MCP** ▸ Uninstall. Remove the MCP entries from your Claude JSON / Codex TOML config(s) if desired.

## Troubleshooting
- **Tools absent in ordinary ChatGPT** → this connection route is not provided; see the support notice above.
- **Tools not showing in a supported client** → run the repair action and restart Claude / Codex; confirm the config has the BimOn entries and the bridge path is correct.
- **“not running / not connected”** → open the host and click **Connect** in the BimOn palette.
- **AutoCAD plugin not loading** → ensure no stale bundle in `%ProgramData%\Autodesk\ApplicationPlugins` (the installer removes it).
