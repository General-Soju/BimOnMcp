using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BimOnMcpBridge
{
    partial class Program
    {
        internal static JArray RevitInstances(string? directory = null)
        {
            var rows = new JArray();
            directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BimOnAI", "hosts", _target);
            if (!Directory.Exists(directory)) return rows;
            var active = TryReadJson(Path.Combine(directory, "active.json"));
            if (active != null && !IsHostAlive(active)) active = null;
            foreach (var file in Directory.GetFiles(directory, "*.json"))
            {
                if (Path.GetFileName(file).Equals("active.json", StringComparison.OrdinalIgnoreCase)) continue;
                var record = TryReadJson(file);
                if (record == null || !IsHostAlive(record)) continue;
                try
                {
                    int pid = (int)record["pid"]!;
                    using var process = Process.GetProcessById(pid);
                    string instanceId = pid + "-" + process.StartTime.ToUniversalTime().Ticks;
                    rows.Add(new JObject { ["instanceId"] = instanceId, ["pid"] = pid,
                        ["title"] = record["title"], ["pipe"] = PipeName + "." + pid,
                        ["jobProtocol"] = record["jobProtocol"] ?? 0, ["controlPipe"] = record["controlPipe"],
                        ["active"] = (int?)active?["pid"] == pid,
                        ["heartbeatUtc"] = record["ts"] });
                }
                catch { /* process exited while enumerating */ }
            }
            return rows;
        }
        internal static JObject ChooseRevitInstance(JArray instances, string tool, JObject args)
        {
            string? target = args["targetInstanceId"]?.ToString();
            if (tool == "get_job" || tool == "cancel_job")
            {
                var parts = (args["jobId"]?.ToString() ?? "").Split(':');
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || !Guid.TryParseExact(parts[1], "N", out _))
                    throw new ArgumentException("INVALID_JOB_ID: Supply the JobId returned by submit_job.");
                if (!string.IsNullOrEmpty(target) && target != parts[0])
                    throw new ArgumentException("TARGET_CONFLICT: Job and instance IDs disagree.");
                target = parts[0];
            }
            var selected = instances.OfType<JObject>().FirstOrDefault(i => string.IsNullOrEmpty(target)
                ? (bool?)i["active"] == true : i["instanceId"]?.ToString() == target);
            if (selected == null) throw new InvalidOperationException(string.IsNullOrEmpty(target)
                ? "NO_ACTIVE_INSTANCE: Open the product and select Connect in the BimOn palette."
                : "INSTANCE_UNAVAILABLE: The original host session is unavailable. Do not replay writes on another instance.");
            args["targetInstanceId"] = selected["instanceId"];
            return selected;
        }
        private static async Task HandleRevitSafely(JToken id, JObject request, SemaphoreSlim lane)
        {
            try { await HandleRevitJobCall(id, request); }
            catch (Exception ex) { SendToolError(id, "REQUEST_FAILED: " + ex.Message); }
            finally { lane.Release(); }
        }
        private static async Task HandleRevitJobCall(JToken id, JObject request)
        {
            var parameters = request["params"] as JObject ?? new JObject();
            string tool = parameters["name"]?.ToString() ?? "";
            var args = parameters["arguments"] as JObject ?? new JObject();
            if (_target == "revit" && RevitQuery.IsTool(tool)) {
                parameters = RevitQuery.Translate(tool, args);
                tool = parameters["name"]!.ToString();
                args = (JObject)parameters["arguments"]!;
            }
            bool explicitPin = args["targetInstanceId"] != null || args["documentId"] != null || args["idempotencyKey"] != null;
            parameters["arguments"] = args;
            request["params"] = parameters;
            var instances = RevitInstances();
            if (tool == "list_instances")
            {
                SendRevitText(id, new JObject { ["instances"] = instances }.ToString(Formatting.None));
                return;
            }
            JObject selected;
            try { selected = ChooseRevitInstance(instances, tool, args); }
            catch (Exception ex) { SendToolError(id, ex.Message); return; }
            bool controlCall = tool == "get_job" || tool == "cancel_job" || tool == "get_environment" || tool == "list_documents";
            if ((int?)selected["jobProtocol"] != 1 && (controlCall || tool == "submit_job" || explicitPin))
            {
                // Do not silently claim that an older plugin honors pinned writes.
                SendToolError(id, "PLUGIN_UPDATE_REQUIRED: This older plugin does not support pinned jobs/control tools. Existing unpinned legacy tools remain available; install the matching plugin for tracking guarantees.");
                return;
            }
            var route = controlCall ? selected["controlPipe"]?.ToString() : selected["pipe"]?.ToString();
            if (string.IsNullOrEmpty(route)) { SendToolError(id, "PROTOCOL_MISMATCH: Missing control pipe."); return; }
            using var pipe = new NamedPipeClientStream(".", route, PipeDirection.InOut, PipeOptions.Asynchronous);
            bool sent = false;
            try
            {
                await pipe.ConnectAsync(5000);
                var utf8 = new UTF8Encoding(false);
                using var writer = new StreamWriter(pipe, utf8, 1024, true);
                using var reader = new StreamReader(pipe, utf8, false, 1024, true);
                using var timeout = new CancellationTokenSource(30_000);
                using var closeOnTimeout = timeout.Token.Register(() => { try { pipe.Dispose(); } catch { } });
                sent = true; // Partial writes are also ambiguous; never auto-retry.
                await writer.WriteLineAsync(request.ToString(Formatting.None));
                await writer.FlushAsync(timeout.Token);
                var response = await reader.ReadLineAsync(timeout.Token);
                if (response == null) throw new IOException("Host disconnected before replying.");
                var text = JObject.Parse(response)["result"]?.ToString() ?? response;
                SendRevitText(id, text);
            }
            catch (Exception)
            {
                SendToolError(id, (sent ? "OUTCOME_UNKNOWN" : "HOST_UNREACHABLE") + ": instanceId=" + selected["instanceId"] +
                    ". Query the known jobId. If its response was lost, repeat submit_job only with the SAME instanceId, documentId, idempotencyKey and arguments. Never replay on another session.");
            }
        }
        internal static bool RevitTextIsError(string text)
        {
            try
            {
                var value = JObject.Parse(text);
                if ((int?)value["bimonJobProtocol"] == 1) return (bool?)value["isError"] == true;
            }
            catch { }
            return text.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase) || text.StartsWith("[오류]") || text.StartsWith("❌");
        }
        private static void SendRevitText(JToken id, string text) => Send(new { jsonrpc = "2.0", id,
            result = new { content = new[] { new { type = "text", text } }, isError = RevitTextIsError(text) } });
    }
    internal static partial class ToolDefinitions
    {
        private static object[] WithRevitJobs(List<object> legacy, string host = "Revit")
        {
            var result = new List<object>();
            foreach (var original in legacy)
            {
                var tool = JObject.FromObject(original);
                var properties = (JObject)tool["inputSchema"]!["properties"]!;
                properties["targetInstanceId"] = new JObject { ["type"] = "string", ["description"] = "Optional pinned instance from list_instances; defaults to the palette's active instance." };
                properties["documentId"] = new JObject { ["type"] = "string", ["description"] = "Optional active document session ID from list_documents; changes before execution fail closed." };
                if (host == "Revit" && tool["name"]?.ToString() == "execute_script")
                {
                    properties["transactionMode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("automatic", "manual"), ["description"] = "automatic wraps in a Revit transaction; manual leaves transactions/saving to the script. Omitted preserves legacy heuristic." };
                    tool["description"] = "Executes IronPython in the Revit API context. Pre-injected: doc, uidoc, app, __revit__. Assign result to return data. On writable documents the host wraps code in a transaction unless it contains Transaction( or .Start(). Success is not independent verification or a disk save. Prefer submit_job for writes and use a readback to verify.";
                }
                result.Add(tool);
            }
            result.Add(Tool("list_instances", "Lists live Revit process sessions and the palette's active target. Does not switch the palette."));
            result.Add(ToolP("list_documents", "Returns the last UI-thread snapshot with capturedUtc and document session IDs. Activate the target document before submitting work.",
                new[] { ("targetInstanceId", "string", "Instance ID from list_instances") }, Array.Empty<string>()));
            result.Add(ToolP("get_environment", "Reports actual Revit build, runtime and job capabilities without accessing Revit APIs on a pipe thread.",
                new[] { ("targetInstanceId", "string", "Instance ID from list_instances") }, Array.Empty<string>()));
            result.Add(ToolP("submit_job", "Queues one Revit/Dynamo operation and returns a jobId immediately. Use a stable key for retries within this host session. No automatic crash replay. succeeded means the operation completed; Dynamo Run waits for evaluation/cache refresh when supported. Independently read back model/node results and verify file saving. Script library save/delete are excluded.",
                new[] { ("operation", "string", "Existing Revit/Dynamo tool name"), ("arguments", "object", "Operation arguments"),
                    ("targetInstanceId", "string", "Pinned instance ID"), ("documentId", "string", "Pinned active document ID"),
                    ("idempotencyKey", "string", "Unique key for this logical operation; same key + input returns same job"),
                    ("startDeadlineSeconds", "integer", "Maximum queued lifetime, 1..300; default 25. Does not interrupt running work.") },
                new[] { "operation", "arguments", "targetInstanceId", "documentId", "idempotencyKey" }));
            result.Add(ToolP("get_job", "Returns queued/running/terminal status and result from the original host session, even when a different instance is active. No Revit API execution.",
                new[] { ("jobId", "string", "Exact JobId returned by submit_job or a legacy response timeout") }, new[] { "jobId" }));
            result.Add(ToolP("cancel_job", "Cancels queued work. For running work, only sets CancelRequested; it cannot stop Python/API execution or roll back completed changes.",
                new[] { ("jobId", "string", "Exact JobId") }, new[] { "jobId" }));
            if (host != "Revit")
                result = result.Select(value =>
                {
                    var definition = JObject.FromObject(value);
                    definition["description"] = definition["description"]?.ToString().Replace("Revit/Dynamo", host).Replace("Revit", host);
                    var properties = definition["inputSchema"]?["properties"] as JObject;
                    if (properties != null) properties["contentRevision"] = new JObject { ["type"] = "string", ["description"] = "Optional document content revision from list_documents; stale model requests fail closed." };
                    return (object)definition;
                }).ToList();
            else
            {
                result.Add(RevitQuery.Definition("get_query_options"));
                result.Add(RevitQuery.Definition("query_elements"));
            }
            return WithReadOnlyHints(result, host);
        }

        internal static object[] WithReadOnlyHints(IEnumerable<object> tools, string host)
        {
            // These are client hints, not authorization or a cross-session replay guarantee.
            // Explicit per-host allowlists, never a name-prefix heuristic. Unknown tools
            // and arbitrary execution/job submission remain potentially mutating.
            var readOnly = new HashSet<string>(StringComparer.Ordinal) {
                "list_scripts", "list_scripts_search", "list_instances", "list_documents", "get_environment", "get_job"
            };
            if (host == "Revit") readOnly.UnionWith(new[] {
                "get_document_title", "get_document_info", "get_element_count", "get_levels",
                "get_project_info", "get_warnings", "get_linked_documents", "get_selected_elements",
                "get_worksets", "get_units", "get_element_by_id", "get_elements_by_category",
                "get_elements_by_filter", "get_element_parameters", "get_views", "get_sheets",
                "get_schedules", "get_rooms", "get_families", "get_family_types",
                "get_element_geometry", "get_shared_parameters", "dynamo_status", "dynamo_get_graph",
                "dynamo_get_node_values", "dynamo_search_nodes", "get_query_options", "query_elements"
            });
            else if (host == "AutoCAD") readOnly.UnionWith(new[] {
                "get_drawing_info", "get_layer_list", "get_entity_count", "get_block_list", "get_lisp_routines",
                "get_selected_entities", "get_text_styles", "get_dim_styles", "get_layouts",
                "get_entities_by_layer", "get_entity_by_handle", "get_block_attributes"
            });
            else if (host == "Navisworks") readOnly.UnionWith(new[] {
                "get_document_title", "get_document_info", "get_model_count", "get_clash_tests", "get_clash_results",
                "get_selection_sets", "get_viewpoints", "get_timeliner_tasks", "get_model_items", "get_item_properties"
            });
            return tools.Select(value => {
                var definition = JObject.FromObject(value);
                definition["annotations"] = new JObject {
                    ["readOnlyHint"] = readOnly.Contains(definition["name"]?.ToString() ?? "")
                };
                return (object)definition;
            }).ToArray();
        }
    }
}
