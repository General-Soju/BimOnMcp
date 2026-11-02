using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BimOnMcpShared
{
    // Only Publish/Pump are called by the host. Pipe handlers inspect copied state,
    // never Autodesk objects. Document IDs/revisions are supplied by each adapter.
    public sealed class HostJobService : IDisposable
    {
        private readonly object _gate = new object();
        private JObject _snapshot = new JObject();
        private readonly string _product;
        private readonly Func<JObject, CancellationToken, Task<string>> _dispatch;
        private readonly Func<string, bool> _supported;
        private readonly JobAudit _audit;
        public string InstanceId { get; }
        public JobQueue<int> Jobs { get; }
        public Action? WakeRequested { get; set; }
        public HostJobService(string product, Func<JObject, CancellationToken, Task<string>> dispatch, Func<string, bool> supported)
        {
            _product = product; _dispatch = dispatch; _supported = supported;
            using var p = Process.GetCurrentProcess();
            InstanceId = p.Id + "-" + p.StartTime.ToUniversalTime().Ticks;
            Jobs = new JobQueue<int>(InstanceId);
            JobMonitor.Attach(Jobs);
            _audit = new JobAudit(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BimOnAI", "logs", product + "-jobs"), InstanceId);
            Jobs.Changed += _audit.Record;
        }
        public void Publish(JObject state)
        {
            state["instanceId"] = InstanceId; state["capturedUtc"] = DateTime.UtcNow;
            state["hostThreadId"] = Thread.CurrentThread.ManagedThreadId;
            lock (_gate) _snapshot = (JObject)state.DeepClone();
        }
        public JObject Snapshot() { lock (_gate) return (JObject)_snapshot.DeepClone(); }
        public string Title => Snapshot()["documents"]?.FirstOrDefault(d => (bool?)d["active"] == true)?["title"]?.ToString() ?? _product;
        public void Pump() => Jobs.ExecuteNext(Thread.CurrentThread.ManagedThreadId);
        public async Task<string> Handle(JObject request, CancellationToken ct)
        {
            try
            {
                var tool = request["params"]?["name"]?.ToString() ?? "";
                var args = request["params"]?["arguments"] as JObject ?? new JObject();
                if (args["targetInstanceId"] != null && args["targetInstanceId"]!.ToString() != InstanceId)
                    throw new JobException("INSTANCE_CHANGED", "Different host process; nothing executed.");
                if (tool == "get_job" || tool == "cancel_job")
                    return Response(tool == "get_job" ? Jobs.Get(args["jobId"]?.ToString() ?? "") : Jobs.Cancel(args["jobId"]?.ToString() ?? ""), true);
                var state = Snapshot();
                if (tool == "list_documents") return state.ToString(Formatting.None);
                if (tool == "get_environment")
                    return new JObject { ["product"] = _product, ["instanceId"] = InstanceId, ["jobProtocol"] = 1,
                        ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                        ["pluginPath"] = typeof(HostJobService).Assembly.Location, ["snapshot"] = state,
                        ["capabilities"] = new JObject { ["separateControlPipe"] = true, ["documentPinning"] = true,
                            ["contentRevision"] = true, ["idempotency"] = "session", ["verification"] = "readback_required",
                            ["persistence"] = "not_requested", ["scriptDeferredCommands"] = "not_tracked" } }.ToString(Formatting.None);
                bool structured = tool == "submit_job";
                var control = (JObject)args.DeepClone();
                if (structured)
                {
                    foreach (string field in new[] { "targetInstanceId", "documentId", "idempotencyKey" })
                        if (string.IsNullOrWhiteSpace(control[field]?.ToString())) throw new JobException("INVALID_ARGUMENT", "Required: " + field);
                    tool = control["operation"]?.ToString() ?? "";
                    args = control["arguments"] is JObject inner ? (JObject)inner.DeepClone() : throw new JobException("INVALID_ARGUMENT", "arguments must be an object.");
                    if (tool == "save_script" || tool == "delete_script" || tool == "execute_saved_script")
                        throw new JobException("UNSUPPORTED_JOB", "Submit execute_script with resolved code; mutable script-library operations are not idempotent jobs.");
                }
                if (!_supported(tool)) throw new JobException("UNSUPPORTED_JOB", "Unknown operation: " + tool);
                if (state["capturedUtc"] == null) throw new JobException("HOST_NOT_READY", "Waiting for host Idle callback.");
                string document = state["activeDocumentId"]?.ToString() ?? "no-document";
                if (document == "no-document" && (tool == "execute_script" || tool == "execute_saved_script" || tool == "set_entity_property" || tool == "approve_clash"))
                    throw new JobException("NO_ACTIVE_DOCUMENT", "Open the target document before changing or executing scripts.");
                string revision = state["contentRevision"]?.ToString() ?? "0";
                if (control["documentId"] != null && control["documentId"]!.ToString() != document)
                    throw new JobException("TARGET_NOT_ACTIVE", "Activate the pinned document before submitting.");
                if (control["contentRevision"] != null && control["contentRevision"]!.ToString() != revision)
                    throw new JobException("CONTENT_CHANGED", "Document content changed.");
                string? key = control["idempotencyKey"]?.ToString();
                if (key?.Length > 128) throw new JobException("INVALID_ARGUMENT", "Key exceeds 128 characters.");
                if (!structured && (tool == "execute_saved_script" || tool == "save_script" || tool == "delete_script")) key = null;
                int seconds = 25;
                if (control["startDeadlineSeconds"] != null && (!int.TryParse(control["startDeadlineSeconds"]!.ToString(), out seconds) || seconds < 1 || seconds > 300))
                    throw new JobException("INVALID_ARGUMENT", "startDeadlineSeconds must be 1..300.");
                var pinned = new JObject { ["params"] = new JObject { ["name"] = tool, ["arguments"] = args.DeepClone() } };
                var canonicalArgs = (JObject)args.DeepClone();
                foreach (var field in new[] { "targetInstanceId", "documentId", "idempotencyKey", "startDeadlineSeconds" }) canonicalArgs.Remove(field);
                // Revision participates in execution validation, not the key: a retry after
                // this job changed the model must still return the original result.
                string fingerprint = Digest(tool + "\n" + document + "\n" + Canonical(canonicalArgs));
                var job = Jobs.SubmitAsync(tool, document, fingerprint, key, DateTime.UtcNow.AddSeconds(seconds), async thread =>
                {
                    var current = Snapshot();
                    if (current["activeDocumentId"]?.ToString() != document)
                        throw new JobException("TARGET_CHANGED", "Document changed while queued; nothing executed.");
                    if ((current["contentRevision"]?.ToString() ?? "0") != revision)
                        throw new JobException("CONTENT_CHANGED", "Model was replaced/appended while queued; nothing executed.");
                    var value = await _dispatch(pinned, ct).ConfigureAwait(false);
                    if (value.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase) || value.StartsWith("[오류]") || value.StartsWith("❌"))
                        throw new JobException("OPERATION_FAILED", value);
                    return value;
                });
                WakeRequested?.Invoke();
                if (!structured && !job.IsTerminal)
                    await Task.WhenAny(Jobs.Completion(job.JobId), Task.Delay(25000, ct)).ConfigureAwait(false);
                return Response(Jobs.Get(job.JobId), structured);
            }
            catch (JobException ex) { return Error(ex.Code, ex.Message); }
            catch (Exception ex) { return Error("REQUEST_FAILED", ex.Message); }
        }
        public static bool IsControl(JObject request) => new[] { "get_environment", "list_documents", "get_job", "cancel_job" }
            .Contains(request["params"]?["name"]?.ToString());
        public Task<string> HandleControl(JObject request, CancellationToken ct) => IsControl(request) ? Handle(request, ct)
            : Task.FromResult(Error("CONTROL_ONLY", "Execution is not allowed on the control pipe."));
        public static string Error(string code, string message) => JsonConvert.SerializeObject(new { bimonJobProtocol = 1, isError = true, errorCode = code, message });
        private static string Response(JobSnapshot job, bool structured) => !structured && job.Status == "succeeded" && !job.ResultTruncated
            ? job.Result ?? "" : JsonConvert.SerializeObject(new { bimonJobProtocol = 1, job,
                isError = job.IsTerminal && job.Status != "succeeded", persistence = "not_requested",
                notice = "Read back to verify. Running cancellation is request-only. Deferred commands inside scripts are not tracked." });
        private static JToken Canonical(JToken value) => value is JObject o
            ? new JObject(o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonical(p.Value))))
            : value is JArray a ? new JArray(a.Select(Canonical)) : value.DeepClone();
        private static string Digest(string text) { using var sha = SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text))); }
        public void Dispose() { Jobs.Stop(); _audit.Dispose(); }
    }
}
