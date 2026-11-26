> **1.1.4 (2026-11-26):** New-PC registration for Claude Desktop, Claude Code CLI and Codex CLI/Desktop. Includes independent client error handling, backups, diagnostics and a Start-menu repair action. [Download](https://github.com/General-Soju/BimOnMcp/releases/tag/v1.1.4). [Validation and limitations](docs/RELEASE_1.1.4_20261126.md).

# BimOn MCP

> **1.1.3 (2026-11-17):** AutoCAD/Navisworks read-only annotations corrected; mutation and script tools remain non-read-only. MCP ping, actual build version, closed-document session handling and configuration preservation fixes. Includes two fixed read-only Revit query tools.
> See [current validation](docs/READONLY_ANNOTATIONS_20261117.md), [1.1.2 validation](docs/RELEASE_VALIDATION_20261117.md), [historical 1.1.0 results](docs/RELEASE_VALIDATION_20261101.md) and [execution semantics](docs/REVIT_JOB_PREVIEW.md). Download the [1.1.3 installer and SHA-256 checksum](https://github.com/General-Soju/BimOnMcp/releases/tag/v1.1.3). See also [live AutoCAD/Navisworks 2025–2027 tests](docs/LIVE_HOST_READONLY_20261117.md).

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

| Host | Dedicated | Common scripts | Dynamo | Job/session tools | Total |
|--------|:--:|:--:|:--:|:--:|:--:|
| Revit | 25 | 6 | **12** | 6 | 49 |
| Navisworks | 11 | 6 | — | 6 | 23 |
| AutoCAD | 13 | 6 | — | 6 | 25 |

**Job/session 6**: `list_instances` · `get_environment` · `list_documents` · `submit_job` · `get_job` · `cancel_job`

**Conditional Revit queries**: `get_query_options` and `query_elements` combine category, associated level and explicit building criteria. They require a pinned instance/document and do not modify the model.

**Common 6**: `execute_script` · `save_script` · `execute_saved_script` · `list_scripts` · `list_scripts_search` · `delete_script`

**Dynamo 12**: read (`dynamo_status`/`get_graph`/`get_node_values`), control (`set_input`/`run_current`),
edit (`add_node`/`add_code_block`/`add_python_node`/`connect`/`delete_node`), **`dynamo_search_nodes`**
(find exact OOTB node creation names), and **`dynamo_build_graph`** (batch-build many nodes+connections in one call).
A `dynamo-mcp` Claude skill is included (`skills/dynamo-mcp`).

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
- Claude Desktop: `%AppData%\Claude\claude_desktop_config.json`; newer packaged installations use an existing profile under `%LocalAppData%\Claude-Data\claude_*\claude_desktop_config.json`. The installer merges both locations when present.
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
