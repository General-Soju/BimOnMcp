> Publication follow-up: version 1.1.3 is available in GitHub Releases. The test chronology and local-state descriptions below describe the validation session before publication.

> Follow-up: [live 2025–2027 host validation](LIVE_HOST_READONLY_20261117.md) is now complete. The initial build-only scope below is historical.

# Read-only annotation correction — 1.1.3 (2026-11-17)

Local release candidate; not published to GitHub.

## Change

The AutoCAD/Navisworks catalog branch previously returned before adding tool annotations. All desktop catalogs now pass through the same annotation step, using explicitly reviewed per-product allowlists. No approval settings were changed. Read-only annotations are client hints, not an authorization bypass or a universal guarantee that every client will skip confirmation.

| Catalog | Read-only | Potentially mutating / executable | Total |
|---|---:|---:|---:|
| Revit (including Dynamo) | 34 | 15 | 49 |
| AutoCAD | 18 | 7 | 25 |
| Navisworks | 16 | 7 | 23 |

Arbitrary scripts, saved-script execution, save/delete, job submission/cancellation, AutoCAD property updates and Navisworks clash approvals remain `readOnlyHint=false`. Unknown tools default to false even when their name starts with `get_`. Document/instance/revision guards and Revit-only query tools are preserved.

All project and installer versions are 1.1.3. Revit/AutoCAD remain net8.0-windows; Navisworks remains net48 with separate 2025/2026/2027 builds.

## Verification

- Added a regression that failed against the previous catalog (AutoCAD get_drawing_info had no hint), then passed after the fix.
- Bridge/session suite: 20 passed in each source tree. Includes complete AutoCAD/Navisworks classification, mutating-tool checks, unknown/cross-host negative cases, and existing Revit/Dynamo tests. Revit session tests use stubs.
- Six builds per edition passed (Revit, AutoCAD, Navisworks 2025/2026/2027, bridge). Existing API-resolution/nullable warnings remain; no build errors.
- Executable protocol tests: 8 checks per target, plus exhaustive AutoCAD/Navisworks metadata assertions. Public has three desktop targets; full suite has those three plus its additional cloud target.
- Actual Codex CLI 0.169.0-alpha.11.1 tests used each newly built executable independently, `approval_policy=never` and a read-only sandbox, with no per-tool approval overrides. AutoCAD and Navisworks `list_instances` each succeeded for BOTH editions: four actual successful calls, exit 0 for both runs.
- Each call returned `{"instances":[]}` because the host applications were not running. This verifies MCP catalog/approval/connection behavior, not model editing or a repeat of all nine live-host combinations. No new Desktop UI or Dynamo graph test is claimed for this patch.
- Public/private desktop tool catalogs matched exactly. Codex configuration hash stayed unchanged.
- Extracted public installer: all 84 host DLL destination copies plus bridge/config-merger matched the current build inputs. Additional full-suite assets were checked separately. This build was not installed over the current PC.

## Artifact

`BimOnMcp_Setup_v1.1.3.exe` — 30,334,313 bytes.

SHA256: `EA746E7E88D3035705D946E21A376F32D4551C36E0F775736BC95B3D6702AE58`.
