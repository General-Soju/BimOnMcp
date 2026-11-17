> Publication follow-up: version 1.1.3 is available in GitHub Releases. The test chronology and local-state descriptions below describe the validation session before publication.

# Live AutoCAD / Navisworks read-only validation — 2026-11-17

The 1.1.3 full-suite installer was applied successfully on the test PC (exit 0). Shared host plugins from that installation were tested with BOTH independently built public and private 1.1.3 bridges. This was not a second installation of the public package; its payload had already been checked independently during packaging.

## Launched hosts and outcomes

| Host | Year | Actual API | Actual runtime | Public bridge | Private bridge |
|---|---|---|---|---|---|
| autocad | 2025 | 25.0.0.0 | .NET 8.0.23 | Pass | Pass |
| autocad | 2026 | 25.1.0.0 | .NET 8.0.23 | Pass | Pass |
| autocad | 2027 | 26.0.0.0 | .NET 10.0.10 | Pass | Pass |
| navisworks | 2025 | 22.0.0.0 | .NET Framework 4.8.9310.0 | Pass | Pass |
| navisworks | 2026 | 23.0.0.0 | .NET Framework 4.8.9310.0 | Pass | Pass |
| navisworks | 2027 | 24.0.0.0 | .NET Framework 4.8.9310.0 | Pass | Pass |

Each edition/host/year case completed nine tool calls: instance enumeration, environment, document snapshot, five host-specific queries and a final document snapshot. Total: 108 recorded successful tool calls, excluding startup readiness polls.

- AutoCAD: drawing info, layer list, entity count, block list and layouts. New metric-template drawings contained zero model-space entities; layer `0` was returned.
- Navisworks: document info, model count, selection sets, viewpoints and model-item traversal. New untitled documents contained zero models.
- All calls were pinned to the process instance. Model queries also used the document ID/content revision. Document IDs and revisions stayed stable across read-only queries.
- Loaded plugin paths reported by the host were checked against the final 1.1.3 build hashes for the appropriate year. The AutoCAD and Navisworks environment tools do not directly expose a package version, so package identity was verified through the files, not inferred from their API version.

## Actual Codex CLI verification

On the running 2027 hosts, each edition separately completed four real Codex tool calls: AutoCAD get_environment/get_layer_list and Navisworks get_environment/get_model_count. Both CLI runs exited 0. All eight calls succeeded with the existing never-approval/read-only test settings. No persistent approval rules were added; Codex configuration hash stayed unchanged.

## Startup observation and limits

AutoCAD 2025 startup took time before registering the plugin. AutoCAD 2026 registered before its first Idle snapshot was ready; an initial probe stopped on missing readiness information. After allowing initialization and sending Escape to the blank test drawing, both edition tests passed. The harness was updated to wait for an idle snapshot. No production-code change was needed during this follow-up, and this does not establish a startup-time guarantee.

These are live-host read-only smoke tests on empty documents. They do not certify non-empty model traversal, clash editing, drawing writes, saving, arbitrary scripts, Revit or Dynamo. Existing user models were not opened or modified.

The 2025/2026 test sessions were closed. AutoCAD 2027 and Navisworks Manage 2027 remain open with blank documents. The PC now has the 1.1.3 full installation. Installer hashes remain the same as the preceding build report; no GitHub push was made.
