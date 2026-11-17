# Public 1.1.2 candidate — validation record (2026-11-17)

Status: source fixes, package validation, Claude Desktop calls and Claude Code CLI calls completed. Codex CLI approval retest also completed, as recorded below. Not published on GitHub. This is a connection/regression check, not an all-operation certification.

## Corrections

- MCP `ping` returns the required empty result; initialize reports the assembly version (1.1.2).
- Closed Revit Document wrappers use reference identity for session bookkeeping.
- Fixed read-only Revit condition queries; exported Revit catalog contains 49 tools, AutoCAD 25, Navisworks 23.
- Existing malformed JSON is preserved; valid settings are merged atomically without discarding unrelated preferences or MCP entries.
- Installer reports registration failures (automation exit code 10) and respects silent mode.
- Existing packaged Claude Desktop profiles under `%LocalAppData%/Claude-Data/claude_*` are merged in addition to legacy Roaming settings. Missing package profiles are not fabricated.
- Public installer/runtime versions must agree before export starts. Regression suites are included in the public source.

## Checks performed

| Check | Result |
|---|---|
| Fresh builds: Revit, AutoCAD, Navisworks 2025/2026/2027, bridge | All six succeeded |
| Bridge/session routing regressions | 18 passed (Revit API stubs) |
| Queue tests, .NET 8 and .NET Framework 4.8 | 20 passed each |
| Python query tests | 18 passed |
| Config merge tests under Windows PowerShell 5.1 | 13 passed |
| Actual public EXE protocol checks | 8 per target, all three targets passed |
| Export version mismatch | Rejected before destination changes |
| Extracted package payload | 84 host DLL copies plus bridge/merger matched source build payload |
| Public source scan | 69 files; no matched credential patterns, private runtime implementation, or personal development paths |
| Installer | Final package installed successfully (exit 0); public bridge and merger hashes matched, and actual packaged Desktop configuration updated |
| Packaged config merger | Applied successfully to the actual legacy Desktop, packaged Desktop and Code settings |

## Actual client calls

- **Claude Desktop 4.23118.1.0:** verified that its child processes used the public bridge. Revit `list_instances` and `get_environment` succeeded. Revit 2025 build 25.2.0.38 reported plugin 1.1.2.0 and .NET 8.0.23, with no active document. AutoCAD and Navisworks `list_instances` both returned successful empty lists (hosts not running).
- **Codex CLI:** public executable overrides were used. Revit `list_instances` and pinned `get_environment` succeeded. Initial non-interactive AutoCAD/Navisworks calls were blocked because approval was required but unavailable. On 2026-11-17 an interactive `on-request` retest displayed real approval prompts. After the user authorized both queries, `Allow` was selected once per call: both `list_instances` calls succeeded with `instances=[]` and `isError=false`. Hosts were not running. Configuration hash was unchanged; no persistent allow rules or binary changes were made. Root cause: Revit carries `readOnlyHint=true` for this query, whereas the other two catalogs omit annotations.
- **Claude Code CLI 2.1.138:** after the user renewed the expired login, all three public servers connected and actual `list_instances` calls succeeded. All returned empty lists because the test host had already been closed. Exit code 0; no permission denials. Earlier expired-token attempts (including 2.1.321) are retained as diagnostic evidence, not counted as passes.

No project model was modified. Dynamo graph operations and all nine 2025–2027 live host combinations were not repeated for this candidate. Historical live-host results describe the earlier release and do not certify every operation on every product patch. The PC was restored to its prior full installation after testing (installer exit 0). Original MCP entries were restored while preserving current preferences and login changes; Codex configuration was unchanged.

## Installer artifact

`BimOnMcp_Setup_v1.1.2.exe` — 30336388 bytes.

SHA256: `3C9EC78C3448D60F2980DB10EE3AACA754797563CB213736B4DCE0CC232CC482`.
