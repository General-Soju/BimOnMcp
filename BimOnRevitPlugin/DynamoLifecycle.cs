using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BimOnMcpShared;
using Newtonsoft.Json.Linq;

namespace BimOnRevitPlugin
{
    internal static partial class DynamoBridge
    {
        private sealed class WorkspaceIdentity { internal string Id = Guid.NewGuid().ToString("N"); }
        private static readonly ConditionalWeakTable<object, WorkspaceIdentity> WorkspaceIds = new();
        private static readonly object EvaluationGate = new();
        private static PendingEvaluation? _evaluation;
        private static long _evaluationGeneration;
        private static string? _lastEvaluatedRevision;
        private sealed class PendingEvaluation
        {
            internal object Workspace = null!;
            internal string Revision = "", EditResult = "";
            internal string? DispatchError;
            internal int Starts;
            internal bool Finished, Failed;
            internal readonly List<(EventInfo Event, Delegate Handler)> Hooks = new();
            internal readonly TaskCompletionSource<string> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private static string Revision(object ws)
        {
            var parts = new List<string> { WorkspaceIds.GetOrCreateValue(ws).Id };
            foreach (var node in Nodes(ws).OrderBy(n => GP(n, "GUID")?.ToString(), StringComparer.Ordinal))
                parts.Add(string.Join("|", new[] { "GUID", "Name", "Value", "Code", "Script", "EngineName", "SelectedIndex", "ArgumentLacing", "IsFrozen" }
                    .Select(p => GP(node, p)?.ToString() ?? "")));
            foreach (var connector in (GP(ws, "Connectors") as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>())
                parts.Add("wire:" + GP(connector, "GUID"));
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", parts))));
        }
        // Called only from the Revit host callback. Event callbacks only set flags.
        public static JObject? CaptureState()
        {
            var model = GetModel(out _); var ws = GP(model, "CurrentWorkspace");
            if (ws == null) return null;
            var revision = Revision(ws);
            lock (EvaluationGate)
                return new JObject { ["workspaceId"] = WorkspaceIds.GetOrCreateValue(ws).Id,
                    ["graphRevision"] = revision, ["runType"] = GP(GP(ws, "RunSettings"), "RunType")?.ToString(),
                    ["version"] = model!.GetType().Assembly.GetName().Version?.ToString(),
                    ["evaluationGeneration"] = _evaluationGeneration, ["evaluationPending"] = _evaluation != null,
                    ["dispatchOutcome"] = _evaluation?.DispatchError == null ? "normal" : "uncertain_waiting_for_completion",
                    ["cachedValues"] = revision == _lastEvaluatedRevision ? "matched_last_mcp_evaluation" : "freshness_unverified",
                    ["externalDocumentFreshness"] = "not_tracked", ["mutationMode"] = "Manual_required" };
        }
        public static Task<string> ExecuteTracked(string operation, JObject args, JObject? target, Func<string> action)
        {
            var model = GetModel(out var error);
            var ws = GP(model, "CurrentWorkspace");
            if (ws == null) throw new JobException("DYNAMO_NOT_RUNNING", error);
            var current = CaptureState()!;
            if (target != null && (target["workspaceId"]?.ToString() != current["workspaceId"]?.ToString()
                || target["graphRevision"]?.ToString() != current["graphRevision"]?.ToString()))
                throw new JobException("GRAPH_CHANGED", "Workspace or graph changed while queued; nothing executed.");
            bool read = new[] { "dynamo_status", "dynamo_get_graph", "dynamo_get_node_values", "dynamo_search_nodes" }.Contains(operation);
            if (read) return Task.FromResult(action() + "\n" + current.ToString(Newtonsoft.Json.Formatting.None));
            if (GP(GP(ws, "RunSettings"), "RunType")?.ToString() != "Manual")
                throw new JobException("MANUAL_MODE_REQUIRED", "MCP graph mutations require Manual mode; no automatic-mode side effects were started.");
            if ((bool?)GP(GP(ws, "RunSettings"), "RunEnabled") != true)
                throw new JobException("DYNAMO_BUSY", "Dynamo Run is not enabled. Wait for the current evaluation or resolve the graph state.");
            bool run = operation == "dynamo_run_current" || (operation == "dynamo_build_graph" && ((bool?)JObject.Parse(args["spec"]?.ToString() ?? "{}")["run"] ?? true));
            if (!run) return Task.FromResult(action());
            var startRun = PrepareRun(); // Resolve the command before any graph edits.
            var pending = new PendingEvaluation { Workspace = ws };
            lock (EvaluationGate)
            {
                if (_evaluation != null) throw new JobException("DYNAMO_BUSY", "An evaluation is already reserved.");
                _evaluation = pending;
            }
            try
            {
                Subscribe(pending, "EvaluationStarted", _ => { lock (EvaluationGate) pending.Starts++; });
                Subscribe(pending, "EvaluationCompleted", e =>
                {
                    lock (EvaluationGate)
                    {
                        pending.Failed |= GP(e, "Error") != null || (bool?)GP(e, "EvaluationSucceeded") == false && (bool?)GP(e, "EvaluationTookPlace") != false;
                        if ((bool?)GP(e, "EvaluationTookPlace") == false) pending.Finished = true;
                    }
                });
                Subscribe(pending, "RefreshCompleted", e => { lock (EvaluationGate) { pending.Failed |= GP(e, "Error") != null; pending.Finished = true; } });
                pending.EditResult = operation == "dynamo_run_current" ? "" : action();
                pending.Revision = Revision(ws);
                try { startRun(); }
                catch (Exception ex)
                {
                    // Invocation can throw after a scheduler task was queued. Keep
                    // the document reserved until its completion events are observed.
                    lock (EvaluationGate) pending.DispatchError = ex.GetBaseException().Message;
                }
                return pending.Completion.Task;
            }
            catch
            {
                Unsubscribe(pending);
                lock (EvaluationGate) _evaluation = null;
                throw;
            }
        }
        private static void Subscribe(PendingEvaluation p, string name, Action<object?> callback)
        {
            var ev = p.Workspace.GetType().GetEvent(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new JobException("DYNAMO_TRACKING_UNSUPPORTED", "This version does not expose " + name + ". No graph edits were started.");
            var invoke = ev.EventHandlerType!.GetMethod("Invoke")!;
            var parameters = invoke.GetParameters().Select(x => Expression.Parameter(x.ParameterType, x.Name)).ToArray();
            var body = Expression.Call(Expression.Constant(callback), typeof(Action<object>).GetMethod("Invoke")!, Expression.Convert(parameters.Last(), typeof(object)));
            var handler = Expression.Lambda(ev.EventHandlerType, body, parameters).Compile();
            ev.AddEventHandler(p.Workspace, handler); p.Hooks.Add((ev, handler));
        }
        private static void Unsubscribe(PendingEvaluation p)
        { foreach (var hook in p.Hooks) { try { hook.Event.RemoveEventHandler(p.Workspace, hook.Handler); } catch { } } }
        public static void PollEvaluation()
        {
            PendingEvaluation? p;
            lock (EvaluationGate) { p = _evaluation; if (p == null || !p.Finished) return; }
            // Finalize on the host callback after scheduler/cache refresh, never in
            // the scheduler event itself. User graph edits invalidate the association.
            var model = GetModel(out _); var ws = GP(model, "CurrentWorkspace");
            bool changed = !ReferenceEquals(ws, p.Workspace) || Revision(p.Workspace) != p.Revision;
            Unsubscribe(p);
            lock (EvaluationGate) { _evaluation = null; _evaluationGeneration++; }
            if (p.DispatchError != null)
                p.Completion.TrySetException(new JobException("OUTCOME_UNCERTAIN", "Run dispatch threw after invocation; completion was observed but outcome cannot be attributed safely. Inspect results before retrying. " + p.DispatchError));
            else if (changed || p.Starts != 1)
                p.Completion.TrySetException(new JobException("EVALUATION_TARGET_CHANGED", "Graph changed or multiple runs were observed. Inspect graph/results before retrying."));
            else if (p.Failed)
                p.Completion.TrySetException(new JobException("DYNAMO_EVALUATION_FAILED", "Dynamo reported evaluation failure; graph edits may remain."));
            else
            {
                lock (EvaluationGate) _lastEvaluatedRevision = p.Revision;
                p.Completion.TrySetResult(p.EditResult + "\n[Dynamo] evaluation/cache refresh completed. Read back node values and errors; no file save requested.");
            }
        }
    }
}
