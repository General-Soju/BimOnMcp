# BimOn MCP

**Control Autodesk Revit · Navisworks · AutoCAD — and live Dynamo graphs — from Claude AI via MCP.**

BimOn MCP is an [MCP (Model Context Protocol)](https://modelcontextprotocol.io) suite that lets Claude (Desktop or Code)
drive your desktop Autodesk BIM hosts: query models, run IronPython scripts, save reusable scripts to an in-host
palette, and read/build/run **Dynamo** graphs — all in natural language.

> Author: **JungGeun Park (General Soju)** · [YouTube @GeneralSoju](https://www.youtube.com/@GeneralSoju) · sojunbeer119@gmail.com
> MIT License · Community edition (Revit / Navisworks / AutoCAD / Dynamo).

**Source & downloads:** [github.com/General-Soju/BimOnMcp](https://github.com/General-Soju/BimOnMcp) — or scan:

<img src="docs/bimonmcp-qr.png" alt="BimOnMcp repository QR code" width="150" />

---

## Supported versions

| | 2025 | 2026 | 2027 |
|--|:--:|:--:|:--:|
| **Revit** | ✅ | ✅ | ✅ |
| **AutoCAD** (+ Civil 3D / Map 3D / Plant 3D … verticals) | ✅ | ✅ | ✅ |
| **Navisworks** | ✅ | ✅ | ✅ |
| **Dynamo for Revit** | ✅ 3.x | ✅ 3.x | ✅ |

- Revit / AutoCAD: single **.NET 8** build loads across 2025–2027 (IronPython 3.4.2).
- Navisworks API is major-version-incompatible → **per-version builds** (`-p:NavisVersion=2026`).
- Claude clients: **Claude Desktop + Claude Code** both auto-registered.

## Architecture

```
Claude Desktop / Claude Code
        │  MCP (JSON-RPC 2.0 over stdio)
        ▼
BimOnMcpBridge.exe ──[--target revit]──────► BimOnRevitPipe.<PID>
   (self-contained)   active.json 조회       BimOnNavisPipe.<PID>
                    ──[--target …]──────────► BimOnAcadPipe.<PID>
                              │  Named Pipe (localhost) — connected instance only
                    ┌─────────┼─────────┐
                    ▼         ▼         ▼
              Revit Plugin  Navis Plugin  AutoCAD Plugin
              (net8, +Dynamo) (net48, per-ver) (net8, R25–R28 bundle)
```

Multiple instances of the same product are supported — each registers a unique pipe; the bridge routes to the
**Connected (ON)** instance chosen in the palette (`BimOn AI Scripts` → 연결/Connect).

## MCP tools

| Host | Dedicated | Common script tools | Dynamo (Revit only) |
|--------|:--:|:--:|:--:|
| Revit | 23 | 6 | **11** |
| Navisworks | 11 | 6 | — |
| AutoCAD | 13 | 6 | — |

**Common 6**: `execute_script` · `save_script` · `execute_saved_script` · `list_scripts` · `list_scripts_search` · `delete_script`

**Dynamo 11**: read (`dynamo_status`/`get_graph`/`get_node_values`), control (`set_input`/`run_current`),
edit (`add_node`/`add_code_block`/`add_python_node`/`connect`/`delete_node`), and **`dynamo_build_graph`**
(batch-build many nodes+connections in one call). A `dynamo-mcp` Claude skill is included (`skills/dynamo-mcp`).

## Install

Download the installer from the **[latest release](https://github.com/General-Soju/BimOnMcp/releases/latest)** and run it
**as a normal user** (not administrator). See **[INSTALL_GUIDE.md](INSTALL_GUIDE.md)** for details. After install,
restart Claude Desktop / Code.

> The installer is not code-signed → Windows SmartScreen may warn "Windows protected your PC"; click **More info → Run anyway**.
> Verify integrity with the SHA-256 on the release page.

| Component | Location |
|----------|-----------|
| MCP Bridge + StdLib | `%AppData%\BimOnAI\` |
| Revit plugin | `%AppData%\Autodesk\Revit\Addins\{2025·2026·2027}` |
| Navisworks plugin | `%ProgramData%\Autodesk\Navisworks Manage {ver}\Plugins\` |
| AutoCAD bundle | `%AppData%\Autodesk\ApplicationPlugins\BimOnAcadPlugin.bundle\` |
| Claude config | Desktop `claude_desktop_config.json` + Code `~\.claude.json` (merged, existing settings preserved) |

## Manual MCP registration (if it didn't auto-register)

If Claude doesn't show the `BimOn-*` tools after installing and restarting, register them by hand.

**Config file(s)** — edit the one for the client you use (create it if missing; keep existing content):
- Claude Desktop: `%AppData%\Claude\claude_desktop_config.json`
- Claude Code: `%UserProfile%\.claude.json`

Add (or merge) these `mcpServers` entries — use **double backslashes** and replace `<you>` with your Windows username:
```json
{
  "mcpServers": {
    "BimOn-Revit":      { "command": "C:\\Users\\<you>\\AppData\\Roaming\\BimOnAI\\BimOnMcpBridge.exe", "args": ["--target","revit"] },
    "BimOn-Navisworks": { "command": "C:\\Users\\<you>\\AppData\\Roaming\\BimOnAI\\BimOnMcpBridge.exe", "args": ["--target","navisworks"] },
    "BimOn-AutoCAD":    { "command": "C:\\Users\\<you>\\AppData\\Roaming\\BimOnAI\\BimOnMcpBridge.exe", "args": ["--target","autocad"] }
  }
}
```
Then **fully quit and restart** Claude Desktop / Claude Code.

Or just re-run the bundled merge script (preserves your other settings, writes both clients):
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:AppData\BimOnAI\MergeClaudeConfig.ps1" -BridgePath "$env:AppData\BimOnAI\BimOnMcpBridge.exe"
```

**Common causes of "not registered":**
- Installed **while Claude was open** → just restart Claude.
- Installer run **as administrator** → the config/bridge went to the *admin* account's `%AppData%`; reinstall as a **normal user**, or point the `command` path at the correct `%AppData%`.
- **Claude Desktop rewrites its config on exit** → edit the file only while Claude is **fully closed**, then reopen.
- Confirm the bridge exists: `%AppData%\BimOnAI\BimOnMcpBridge.exe`.

## Build from source

```bash
# Revit / AutoCAD / Navisworks 2025 + Bridge
dotnet build BimOnMcp.sln -c Release

# Navisworks per-version (API v23 / v24)
dotnet build BimOnNavisPlugin -c Release -p:NavisVersion=2026 -p:OutputPath=bin\Release\net48-2026\ -p:AppendTargetFrameworkToOutputPath=false
dotnet build BimOnNavisPlugin -c Release -p:NavisVersion=2027 -p:OutputPath=bin\Release\net48-2027\ -p:AppendTargetFrameworkToOutputPath=false

# Bridge (single self-contained exe) → Installer\BridgeOutput
dotnet publish BimOnMcpBridge -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o Installer/BridgeOutput

# Installer
ISCC.exe Installer/BimOnMcp.iss
```

## Notes / limits
- **One connected instance per product** at a time (switch via palette Connect button).
- **Navisworks** IronPython 2.7 stdlib is limited (`os, collections, functools, ntpath, __future__`).
- **AutoCAD** explicit imports need `clr.AddReference('acdbmgd')` / `('acmgd')`.
- Operations > 28s may time out the follow-up request.

## License

**MIT** — Copyright (c) 2026 **JungGeun Park (General Soju)**. See [LICENSE](LICENSE).

**Contact:** sojunbeer119@gmail.com · [YouTube @GeneralSoju](https://www.youtube.com/@GeneralSoju)
