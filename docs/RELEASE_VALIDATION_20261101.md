# BimOn MCP 1.1.0 — public build validation

Validated on 2026-11-01 (Asia/Seoul). Public 1.1.0 source and the installer containing the same validated host DLLs are distributed through [GitHub Releases](https://github.com/General-Soju/BimOnMcp/releases/tag/v1.1.0).

| Host | 2025 | 2026 | 2027 |
|---|---|---|---|
| Revit build | 25.2.0.38 PASS | 26.4.10.51 PASS | 27.0.20.10 PASS |
| Dynamo Core | 3.2.1.5366 PASS | 3.6.2.11575 PASS | 4.0.2.3852 PASS |
| AutoCAD API | 25.0.0.0 PASS | 25.1.0.0 PASS | 26.0.0.0 PASS |
| Navisworks API | 22.0.0.0 PASS | 23.0.0.0 PASS | 24.0.0.0 PASS |

Revit/AutoCAD retain .NET 8 targets and ran on .NET 8.0.23 (2025/26) and .NET 10.0.10 (2027). Navisworks retains per-year net48 builds. Revit 2025.5 was not installed in this test; the actual baseline was 2025.2. Revit 2024 is excluded.

Live tests used disposable local fixtures: pinned documents, duplicate submission, independent readback, intentional-error recovery and saving. Revit transaction rollback passed. AutoCAD saved DWGs were reopened in a separate database and checked for one radius-3 circle. Navisworks NWF save/reopen and synthetic Clash result approval persistence passed. The Clash test does not validate geometry clash detection.

Dynamo tests covered Manual-mode mixed OOTB/CodeBlock/Python graphs (slider 3 → Watch 7), selected wall width compared to the Revit API, ambiguous-name deletion rejection, .dyn save/reopen and tracked reevaluation. An interrupted early 2027 partial graph attempt did not reproduce in the fresh initialized session; partial changes are never assumed rolled back. Revit save was tested, but an additional RVT reopen was not.

Original validation: 20 queue cases each on net8/net10/net48 and 13 bridge/routing contract cases. For publication, the current source passed 20 net8 queue cases and 14 bridge/routing cases, including the additional read-only annotation test. Public MCP stdio initialized and routed to real Revit, AutoCAD and Navisworks sessions (47/25/23 tools).

Publication update: the bridge was rebuilt to include reviewed read-only query annotations. These are client hints, not security permissions. All host DLLs are unchanged; MCP stdio checks passed for all three targets. The installer was extracted again: 84 embedded host DLLs matched the frozen payload and the bridge matched the newly tested executable.

The compiled public installer was independently extracted. Embedded DLLs match the frozen files used for live tests, and the bridge matches the tested public executable. The package contains only the public Revit/AutoCAD/Navisworks/Dynamo scope. Extraction is not clean-PC installation/permissions testing.

File: `BimOnMcp_Setup_v1.1.0.exe` (30328058 bytes).
SHA-256: `1944860D0B9F97BB358FA08DBFA03C5E677B70799509DF13E14DF908BFC1E4A0`.

Close Autodesk applications before installing. Dynamo edits/Run require Manual mode; queued cancellation differs from running cancellation, and completion differs from verification/save. See [execution semantics](REVIT_JOB_PREVIEW.md). Ordinary MCP results sent to AI are not redacted by this update. No compatibility claim is made for every patch or third-party package.
