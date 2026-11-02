using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Events;
using BimOnMcpShared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BimOnRevitPlugin
{
    public partial class RevitApp
    {
        private sealed class RequestOptions
        {
            internal string Operation = "", DocumentId = "", Fingerprint = "";
            internal string? Key;
            internal bool Async;
            internal DateTime Deadline;
            internal int WaitMilliseconds;
            internal JObject Arguments = new JObject();
            internal JObject? DynamoTarget;
        }
        private static readonly AsyncLocal<RequestOptions?> _request = new AsyncLocal<RequestOptions?>();
        private static readonly HashSet<string> JobOperations = new HashSet<string>(new[] {
            "get_document_title", "get_document_info", "get_element_count", "get_levels", "get_project_info",
            "get_warnings", "get_linked_documents", "get_selected_elements", "get_worksets", "get_units",
            "get_element_by_id", "get_elements_by_category", "get_elements_by_filter", "get_element_parameters",
            "set_element_parameter", "get_views", "get_sheets", "get_schedules", "get_rooms", "get_families",
            "get_family_types", "get_element_geometry", "get_shared_parameters", "execute_script", "execute_saved_script",
            "dynamo_status", "dynamo_get_graph", "dynamo_get_node_values", "dynamo_set_input", "dynamo_run_current",
            "dynamo_add_code_block", "dynamo_add_node", "dynamo_add_python_node", "dynamo_connect",
            "dynamo_delete_node", "dynamo_build_graph", "dynamo_search_nodes" });

        private void OnIdling(object? sender, IdlingEventArgs e)
        {
            if (sender is UIApplication ui)
            {
                _uiApp = ui;
                DynamoBridge.PollEvaluation();
                try { _session.Refresh(ui); } catch { /* Next idle retries; execution validates independently. */ }
                WakeQueue();
            }
        }
        private static void WakeQueue()
        {
            if (_handler == null || !_handler.Jobs.HasPending || _exEvent == null) return;
            try
            {
                var raised = _exEvent.Raise();
                if (raised == ExternalEventRequest.Denied)
                    _handler.Jobs.FailPending("EXTERNAL_EVENT_DENIED", "Revit rejected the external event; nothing was executed.");
                // Accepted/Pending already schedule execution; TimedOut is retried on the next Idling event.
            }
            catch { _handler.Jobs.FailPending("EXTERNAL_EVENT_UNAVAILABLE", "Revit external event is unavailable."); }
        }
        private async Task<string> HandleRequest(JObject request, CancellationToken ct)
        {
            var previous = _request.Value;
            bool storageLocked = false;
            try
            {
                var tool = request["params"]?["name"]?.ToString() ?? "";
                var args = (JObject?)request["params"]?["arguments"] ?? new JObject();
                var target = args["targetInstanceId"]?.ToString();
                if (!string.IsNullOrEmpty(target) && target != _session.InstanceId)
                    throw new JobException("INSTANCE_CHANGED", "This is a different Revit process/session; nothing was executed.");
                if (tool == "get_environment")
                {
                    var environment = _session.EnvironmentInfo();
                    environment["auditDropped"] = _audit?.Dropped ?? 0;
                    environment["auditWriteFailed"] = _audit?.WriteFailed ?? false;
                    return environment.ToString(Formatting.None);
                }
                if (tool == "list_documents") return _session.Snapshot().ToString(Formatting.None);
                if (tool == "get_job" || tool == "cancel_job")
                {
                    string id = args["jobId"]?.ToString() ?? "";
                    return JobResponse(tool == "get_job" ? _handler!.Jobs.Get(id) : _handler!.Jobs.Cancel(id), true);
                }
                bool async = tool == "submit_job";
                var control = args;
                if (async)
                {
                    foreach (var required in new[] { "targetInstanceId", "documentId", "idempotencyKey" })
                        if (string.IsNullOrWhiteSpace(control[required]?.ToString()))
                            throw new JobException("INVALID_ARGUMENT", "submit_job requires " + required + ".");
                    if (!(args["arguments"] is JObject)) throw new JobException("INVALID_ARGUMENT", "arguments must be an object.");
                    tool = args["operation"]?.ToString() ?? "";
                    args = args["arguments"] is JObject inner ? (JObject)inner.DeepClone() : new JObject();
                    if (!JobOperations.Contains(tool)) throw new JobException("UNSUPPORTED_JOB", "Operation cannot be submitted as a job.");
                    if (string.IsNullOrWhiteSpace(control["idempotencyKey"]?.ToString()))
                        throw new JobException("KEY_REQUIRED", "submit_job requires an idempotencyKey. Reuse it only for the same request and target.");
                    if (control["idempotencyKey"]!.ToString().Length > 128)
                        throw new JobException("INVALID_ARGUMENT", "idempotencyKey must be at most 128 characters.");
                    request = new JObject { ["params"] = new JObject { ["name"] = tool, ["arguments"] = args } };
                }
                bool executes = JobOperations.Contains(tool) || (tool == "save_script" &&
                    string.Equals(args["executeAfterSave"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase));
                if (executes)
                {
                    string documentId = _session.CaptureTarget(control["documentId"]?.ToString());
                    var fingerprintArgs = (JObject)args.DeepClone();
                    foreach (var k in new[] { "targetInstanceId", "documentId", "idempotencyKey", "timeoutSeconds", "startDeadlineSeconds" }) fingerprintArgs.Remove(k);
                    _request.Value = new RequestOptions { Operation = tool, DocumentId = documentId, Async = async,
                        Key = tool == "save_script" ? null : control["idempotencyKey"]?.ToString(),
                        Deadline = DateTime.UtcNow.AddSeconds(Bounded(control, "startDeadlineSeconds", 25, 300)),
                        WaitMilliseconds = Bounded(control, "timeoutSeconds", 25, 25) * 1000,
                        Arguments = (JObject)args.DeepClone(),
                        DynamoTarget = _session.Snapshot()["dynamo"] as JObject,
                        Fingerprint = Digest(tool + "\n" + documentId + "\n" + Canonical(fingerprintArgs).ToString(Formatting.None)) };
                }
                if (tool == "save_script" || tool == "delete_script" || tool == "execute_saved_script" || tool == "list_scripts" || tool == "list_scripts_search")
                {
                    await _storageGate.WaitAsync(ct);
                    storageLocked = true;
                }
                var result = await Dispatch(request, ct);
                if (IsFailureText(result)) return ErrorResponse("INVALID_OPERATION", result);
                return result;
            }
            catch (JobException ex) { return ErrorResponse(ex.Code, ex.Message); }
            catch (OperationCanceledException) { return ErrorResponse("HOST_STOPPING", "Host is stopping. Query known job IDs before retrying writes."); }
            catch (Exception ex) { return ErrorResponse("REQUEST_FAILED", ex.Message); }
            finally { if (storageLocked) _storageGate.Release(); _request.Value = previous; }
        }
        private async Task<string> Run(Func<Document?, string> task, CancellationToken ct)
        {
            var options = _request.Value ?? throw new JobException("MISSING_CONTEXT", "Request context is missing.");
            ct.ThrowIfCancellationRequested();
            var jobs = _handler!.Jobs;
            var submitted = jobs.SubmitAsync(options.Operation, options.DocumentId, options.Fingerprint,
                options.Key, options.Deadline, ui =>
                {
                    _uiApp = ui;
                    var document = _session.ValidateTarget(ui, options.DocumentId);
                    if (options.Operation.StartsWith("dynamo_", StringComparison.Ordinal))
                        return DynamoBridge.ExecuteTracked(options.Operation, options.Arguments, options.DynamoTarget, () =>
                        {
                            try { return task(document); }
                            finally { _session.Refresh(ui); }
                        });
                    string text;
                    try { text = task(document); }
                    finally { _session.Refresh(ui); }
                    if (IsFailureText(text)) throw new JobException("OPERATION_FAILED", text);
                    return Task.FromResult(text);
                });
            WakeQueue();
            if (!options.Async && !submitted.IsTerminal)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var completion = jobs.Completion(submitted.JobId);
                await Task.WhenAny(completion, Task.Delay(options.WaitMilliseconds, wait.Token));
                wait.Cancel(); // End only the response wait, never cancel a running Revit operation.
            }
            return JobResponse(jobs.Get(submitted.JobId), options.Async);
        }

        private static int Bounded(JObject args, string name, int fallback, int max)
        {
            if (args[name] == null) return fallback;
            if (!int.TryParse(args[name]!.ToString(), out int value) || value < 1 || value > max)
                throw new JobException("INVALID_ARGUMENT", name + " must be between 1 and " + max + ".");
            return value;
        }
        private static JToken Canonical(JToken value) => value is JObject o
            ? new JObject(o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonical(p.Value))))
            : value is JArray a ? new JArray(a.Select(Canonical)) : value.DeepClone();
        private static string Digest(string text)
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }
        private static bool IsFailureText(string text) => text.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[오류]") || text.StartsWith("❌");
        private static string JobResponse(JobSnapshot job, bool structured)
        {
            if (!structured && job.Status == "succeeded" && !job.ResultTruncated) return job.Result ?? "";
            return JsonConvert.SerializeObject(new { bimonJobProtocol = 1, job,
                isError = job.Status == "failed" || job.Status == "expired" || job.Status == "cancelled" || job.Status == "unknown",
                notice = job.IsTerminal ? "Completion does not verify model changes; use a separate readback."
                    : "Work is still queued/running. Query get_job with this JobId; do not submit a new write. Running cancellation is request-only." });
        }
        private static string ErrorResponse(string code, string message) => JsonConvert.SerializeObject(
            new { bimonJobProtocol = 1, isError = true, errorCode = code, message });
    }
}
