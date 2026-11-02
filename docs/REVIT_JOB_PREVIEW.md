# Desktop job protocol v1 — BimOn 1.1.0

Status: 1.1.0; listed host validation completed. Revit, AutoCAD and Navisworks share the job workflow. Bridge and host DLLs must be upgraded together. Refer to RELEASE_VALIDATION_20261101.md for exact tested builds and remaining distribution limits.

## Compatibility policy

Revit, AutoCAD and Bridge retain `net8.0-windows`; Navisworks retains `net48` and its per-year API builds. Shared code retains both targets. No production project targets .NET 10. Running the isolated queue tests on .NET 10 does not certify a Revit/Dynamo build. Revit 2024 remains outside the official distribution.

## Workflow

1. `list_instances`: choose a process session (`PID-startUtcTicks`). The palette's active instance remains the default for ordinary tools.
2. `list_documents(targetInstanceId)`: read a cached UI-thread snapshot, including `capturedUtc` and document IDs. Activate the intended document in Revit. IDs represent open-document lifetimes, not file paths or document titles.
3. `get_environment(targetInstanceId)`: inspect Revit build, actual runtime, API version, loaded Python/Dynamo assemblies and queue limits.
4. `submit_job`: pin instance and document, provide an operation and its arguments, and supply a unique idempotency key. The call returns a job immediately.
5. `get_job(jobId)`: retrieve status/results from the original process even after switching the active instance.
6. Read the resulting model/graph separately. Completion alone does not prove the user's requirements were met or that anything was saved to disk.

Example request arguments, using IDs actually returned by the first two tools:

```json
{
  "targetInstanceId": "<instanceId>",
  "documentId": "<activeDocumentId>",
  "operation": "get_levels",
  "arguments": {},
  "idempotencyKey": "levels-check-001",
  "startDeadlineSeconds": 25
}
```

Do not substitute guessed IDs. Job responses are JSON text inside MCP content, for example:

```json
{
  "bimonJobProtocol": 1,
  "job": {
    "JobId": "<instanceId>:<job-guid>",
    "Status": "queued",
    "Verification": "not_checked",
    "ResultTruncated": false
  },
  "isError": false
}
```

## Execution semantics

| State | Meaning |
|---|---|
| queued | Accepted, waiting for a Revit ExternalEvent |
| running | Operation entered the Revit API context |
| succeeded | Operation returned without a reported failure; independent verification is still required |
| failed | Validation, dispatch or execution failed; writes may be partial for manually managed scripts or Dynamo graphs |
| cancelled | Cancelled before execution |
| expired | Queue start deadline elapsed before execution |
| unknown | Shutdown occurred during execution; do not assume rollback or automatically replay |

The queue executes one operation at a time. A different active document at execution produces `TARGET_CHANGED` before the operation runs. The host does not silently activate another document. This pins the starting document; it is not a sandbox for arbitrary Python accessing other documents or external systems. Selection inside the same document is not snapshotted.

`cancel_job` removes queued work. For running work it only sets `CancelRequested`; there is no forced thread abort or promise of rollback. The host cannot interrupt arbitrary Python safely.

Use the **same** idempotency key, pinned target and operation arguments after a lost submit response. Same key and input return the original job; changed input produces `IDEMPOTENCY_CONFLICT`. Resolved saved-script code participates in the fingerprint. Deduplication lasts only for that host session. No durable recovery or exactly-once guarantee is provided across a crash; an unknown job must be investigated before retrying a write.

## Bounds and legacy behavior

- 64 queued jobs; 512 retained jobs per host session, including completed jobs. Keys are not silently evicted. At capacity, new submissions fail with `QUEUE_FULL` or `HISTORY_FULL`; existing jobs remain queryable. Review pending work and results before restarting a full session.
- At most 65,536 characters retained per result. `ResultTruncated=true` explicitly marks shortened output; request smaller queries when full output is needed.
- Queue start deadline: default 25 seconds, allowed 1–300 seconds. It does not terminate running work.
- Legacy calls wait at most 25 seconds (`execute_script.timeoutSeconds`: 1–25). A successful untruncated result remains plain text. Pending work returns a job envelope instead of a misleading timeout failure. Legacy responses lost before obtaining a job ID cannot always be recovered safely; use `submit_job` for writes.
- Eight execution pipe lanes and sixteen bridge execution slots are bounded. Control calls use a separate pipe with two lanes and four bridge slots; this separates status polling from long execution calls.
- Script library save/delete are not asynchronous jobs. A root-specific named mutex serializes library operations across processes with bounded lock waits; atomic metadata replacement and corrupt-metadata checks protect writes.
- Palette script execution now uses the same queue, document check and transaction path as MCP. The palette shows the latest job with copy-ID/cancel controls; a full history UI is not included.
- Dynamo errors become typed failures. Partial batch edits are reported as failures and are not rolled back automatically. Manual mode is required for graph mutation and Run. Accepted Run is not completion: poll its job, then read node values. Completion tracks evaluation/cache refresh; external document freshness is not tracked.
- Old bridge + new add-in retains ordinary text on success but the old bridge may not set MCP `isError`. New tools require the matching bridge/add-in pair. Old add-ins do not enforce the new targeting contract.

## Metadata audit

Local directory: `%LocalAppData%\BimOnAI\logs\revit-jobs`.

Records contain job/operation/document-session IDs, states, timestamps, fixed error codes, cancellation and verification flags. They omit script code, output, exception messages, document titles and file paths. This does **not** redact ordinary tool results sent to the AI.

The writer is best effort: up to 1,024 buffered records, a 10 MiB file threshold, and cleanup of dated audit files older than seven days when a new batch is written. A final batch can exceed the threshold slightly. `get_environment` reports dropped records and write failures. Results and deduplication keys are memory-only; the audit is not a recovery journal.

## Reproducible host-free checks

Run from the repository root on Windows:

```powershell
dotnet run --project tests/JobQueue.Tests/JobQueue.Tests.csproj -c Release -f net8.0
dotnet run --project tests/JobQueue.Tests/JobQueue.Tests.csproj -c Release -f net48
dotnet --roll-forward LatestMajor tests/JobQueue.Tests/bin/Release/net8.0/JobQueue.Tests.dll
dotnet run --project tests/BridgeJobs.Tests/BridgeJobs.Tests.csproj -c Release
```

The third command may still select .NET 8 when it is installed. To validate a newer runtime, run a separate runtime configuration and record the actual runtime printed by the test. The frozen release tests passed 20 queue scenarios each on .NET 8, .NET 10 and .NET Framework, plus 13 bridge/routing scenarios. These isolated tests do not certify host API behavior.

Live host evidence and limitations are in RELEASE_VALIDATION_20261101.md. Annual product labels do not prove support for every patch, third-party package or model. Revit 2024 is excluded from this installer. Nothing in this protocol redacts ordinary MCP output sent to AI. Navisworks Clash selectors accept GUIDs or unique names; approval is attributed to BimOn MCP where the API requires an assignee.
