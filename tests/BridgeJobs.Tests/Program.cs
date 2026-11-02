using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BimOnMcpBridge;
using BimOnMcpShared;
using BimOnRevitPlugin;
using Newtonsoft.Json.Linq;

internal static class BridgeTests
{
    static int passed;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Case(string name, Action run) { run(); passed++; Console.WriteLine("PASS " + name); }
    static void Reject(Action run, string prefix)
    { try { run(); } catch (Exception ex) { Check(ex.Message.StartsWith(prefix), ex.Message); return; } throw new Exception("Expected " + prefix); }
    static void Main()
    {
        var instances = new JArray(new JObject { ["instanceId"] = "1-100", ["active"] = true }, new JObject { ["instanceId"] = "2-200", ["active"] = false });
        string job = "2-200:" + Guid.NewGuid().ToString("N");
        Case("default and explicit instance routes", () =>
        {
            var args = new JObject(); Check(Program.ChooseRevitInstance(instances, "get_levels", args)["instanceId"]!.ToString() == "1-100", "default");
            Check(args["targetInstanceId"]!.ToString() == "1-100", "not pinned");
            Check(Program.ChooseRevitInstance(instances, "get_levels", new JObject { ["targetInstanceId"] = "2-200" })["instanceId"]!.ToString() == "2-200", "explicit");
        });
        Case("job follows original session after active instance switch", () =>
        {
            var chosen = Program.ChooseRevitInstance(instances, "get_job", new JObject { ["jobId"] = job });
            Check(chosen["instanceId"]!.ToString() == "2-200", "job redirected");
            Reject(() => Program.ChooseRevitInstance(instances, "get_job", new JObject { ["jobId"] = job, ["targetInstanceId"] = "1-100" }), "TARGET_CONFLICT");
        });
        Case("dead/reused PID never falls back to active instance", () =>
        {
            Reject(() => Program.ChooseRevitInstance(instances, "get_job", new JObject { ["jobId"] = "2-199:" + Guid.NewGuid().ToString("N") }), "INSTANCE_UNAVAILABLE");
            Reject(() => Program.ChooseRevitInstance(instances, "cancel_job", new JObject { ["jobId"] = "bad" }), "INVALID_JOB_ID");
            Reject(() => Program.ChooseRevitInstance(instances, "cancel_job", new JObject { ["jobId"] = ":" + Guid.NewGuid().ToString("N") }), "INVALID_JOB_ID");
        });
        Case("MCP isError respects structured state and old errors", () =>
        {
            Check(Program.RevitTextIsError("{\"bimonJobProtocol\":1,\"isError\":true}"), "failed marked success");
            Check(!Program.RevitTextIsError("{\"bimonJobProtocol\":1,\"isError\":false,\"job\":{\"Status\":\"running\"}}"), "running marked failure");
            Check(Program.RevitTextIsError("[Error] Old plugin"), "legacy error");
            Check(!Program.RevitTextIsError("completed"), "ordinary text");
        });
        Case("public tool schemas preserve existing tools and add six host job tools", () =>
        {
            var tools = ToolDefinitions.Get("revit").Select(JObject.FromObject).ToArray();
            var names = tools.Select(t => t["name"]!.ToString()).ToArray();
            Check(names.Length == names.Distinct().Count(), "duplicate names");
            foreach (var name in new[] { "list_instances", "list_documents", "get_environment", "submit_job", "get_job", "cancel_job" }) Check(names.Contains(name), "missing " + name);
            Check(names.Count(n => n.StartsWith("dynamo_")) == 12, "Dynamo tools changed");
            var submit = tools.Single(t => t["name"]!.ToString() == "submit_job");
            Check(submit["inputSchema"]!["required"]!.Values<string>().Contains("documentId"), "unpinned submit");
            foreach (var target in new[] { "autocad", "navisworks" })
                Check(ToolDefinitions.Get(target).Select(JObject.FromObject).Any(t => t["name"]!.ToString() == "submit_job"), "missing host job tools");
        });
        Case("Revit tool annotations distinguish reviewed queries from mutating execution", () =>
        {
            var tools = ToolDefinitions.Get("revit").Select(JObject.FromObject).ToArray();
            foreach (var name in new[] { "get_element_parameters", "list_documents", "get_environment", "get_job", "dynamo_get_graph" })
                Check((bool?)tools.Single(t => t["name"]!.ToString() == name)["annotations"]?["readOnlyHint"] == true, "query hint missing: " + name);
            foreach (var name in new[] { "execute_script", "execute_saved_script", "submit_job", "cancel_job", "set_element_parameter", "save_script", "delete_script", "dynamo_run_current", "dynamo_build_graph" })
            {
                var annotations = tools.Single(t => t["name"]!.ToString() == name)["annotations"]!;
                Check((bool?)annotations["readOnlyHint"] == false, "unsafe read hint: " + name);
                Check(annotations["idempotentHint"] == null, "unbounded replay guarantee: " + name);
            }
            Check(tools.All(t => t["annotations"]?["readOnlyHint"]?.Type == JTokenType.Boolean), "unclassified tool");
        });
        Case("instance registry validates live processes and tolerates malformed records", () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "BimOnBridgeTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            try
            {
                using var p = Process.GetCurrentProcess();
                File.WriteAllText(Path.Combine(dir, p.Id + ".json"), new JObject { ["pid"] = p.Id, ["pname"] = p.ProcessName, ["title"] = "test" }.ToString());
                File.WriteAllText(Path.Combine(dir, "active.json"), new JObject { ["pid"] = p.Id }.ToString());
                File.WriteAllText(Path.Combine(dir, "broken.json"), "{");
                File.WriteAllText(Path.Combine(dir, "dead.json"), new JObject { ["pid"] = int.MaxValue }.ToString());
                File.WriteAllText(Path.Combine(dir, "bad-pid.json"), new JObject { ["pid"] = "not-a-pid" }.ToString());
                var found = Program.RevitInstances(dir);
                Check(found.Count == 1 && (bool)found[0]["active"]!, "live enumeration");
                Check(found[0]["instanceId"]!.ToString() == p.Id + "-" + p.StartTime.ToUniversalTime().Ticks, "session identity");
                File.WriteAllText(Path.Combine(dir, p.Id + ".json"), new JObject { ["pid"] = p.Id, ["pname"] = p.ProcessName, ["startUtcTicks"] = 1 }.ToString());
                Check(Program.RevitInstances(dir).Count == 0, "stale process-start record accepted");
            }
            finally { Directory.Delete(dir, true); }
        });
        Case("UI snapshot contract: pinned document survives refresh and rejects switches/closure", () =>
        {
            var s = new RevitSession(); var ui = new UIApplication();
            var a = new Document { Title = "same title" }; var b = new Document { Title = "same title" };
            ui.Application.Documents.Add(a); ui.Application.Documents.Add(b); ui.ActiveUIDocument = new UIDocument { Document = a };
            s.Refresh(ui); var id = s.CaptureTarget(null); s.Refresh(ui); Check(s.CaptureTarget(null) == id, "unstable ID");
            Check(s.ValidateTarget(ui, id) == a, "wrong doc");
            ui.ActiveUIDocument.Document = b;
            try { s.ValidateTarget(ui, id); throw new Exception("switch accepted"); } catch (JobException ex) { Check(ex.Code == "TARGET_CHANGED", "switch error"); }
            ui.Application.Documents.Remove(a); s.Refresh(ui); ui.Application.Documents.Add(a); ui.ActiveUIDocument.Document = a; s.Refresh(ui);
            Check(s.CaptureTarget(null) != id, "reopened doc reused identity");
            var copy = s.Snapshot(); copy["activeDocumentId"] = "bad"; Check(s.CaptureTarget(null) != "bad", "mutable snapshot");
        });
        Case("real request routing: submit, deduplicate, poll, execute and retrieve", () =>
        {
            var ui = Host(); RevitApp.Reset(ui); var host = new RevitApp();
            var request = Submit("key", "first");
            var first = JObject.Parse(host.Call(request).GetAwaiter().GetResult());
            string id = first["job"]!["JobId"]!.ToString();
            Check(first["job"]!["Status"]!.ToString() == "queued" && RevitApp.Writes == 0, "submit executed inline");
            var repeat = JObject.Parse(host.Call(request).GetAwaiter().GetResult());
            Check(repeat["job"]!["JobId"]!.ToString() == id, "duplicate request accepted twice");
            RevitApp.Pump(ui);
            var completed = JObject.Parse(host.Call(Request("get_job", new JObject { ["jobId"] = id })).GetAwaiter().GetResult());
            Check(RevitApp.Writes == 1 && completed["job"]!["Result"]!.ToString() == "first", "result routing");
            var conflict = JObject.Parse(host.Call(Submit("key", "different")).GetAwaiter().GetResult());
            Check(conflict["errorCode"]!.ToString() == "IDEMPOTENCY_CONFLICT", "key conflict missed");
        });
        Case("real routing: queued cancellation and document switch reject writes", () =>
        {
            var ui = Host(); RevitApp.Reset(ui); var host = new RevitApp();
            string id = JObject.Parse(host.Call(Submit("cancel", "write")).Result)["job"]!["JobId"]!.ToString();
            var cancelled = JObject.Parse(host.Call(Request("cancel_job", new JObject { ["jobId"] = id })).Result);
            Check(cancelled["job"]!["Status"]!.ToString() == "cancelled", "cancel response");
            RevitApp.Pump(ui); Check(RevitApp.Writes == 0, "cancelled write executed");
            id = JObject.Parse(host.Call(Submit("switch", "write")).Result)["job"]!["JobId"]!.ToString();
            var other = new Document(); ui.Application.Documents.Add(other); ui.ActiveUIDocument = new UIDocument { Document = other };
            RevitApp.Pump(ui);
            var failed = JObject.Parse(host.Call(Request("get_job", new JObject { ["jobId"] = id })).Result);
            Check(failed["job"]!["ErrorCode"]!.ToString() == "TARGET_CHANGED" && RevitApp.Writes == 0, "wrong document write");
        });
        Case("legacy response wait returns job ID without losing pending work", () =>
        {
            var ui = Host(); RevitApp.Reset(ui); var host = new RevitApp();
            var legacy = Request("get_levels", new JObject { ["timeoutSeconds"] = 1, ["startDeadlineSeconds"] = 30 });
            var response = JObject.Parse(host.Call(legacy).GetAwaiter().GetResult());
            Check(response["job"]!["Status"]!.ToString() == "queued" && !(bool)response["isError"]!, "timeout reported failure");
            string id = response["job"]!["JobId"]!.ToString(); RevitApp.Pump(ui);
            Check(JObject.Parse(host.Call(Request("get_job", new JObject { ["jobId"] = id })).Result)["job"]!["Status"]!.ToString() == "succeeded", "late completion lost");
        });
        Case("submit validates pinned target and excludes library mutations", () =>
        {
            var ui = Host(); RevitApp.Reset(ui); var host = new RevitApp();
            var missing = Submit("key", "value"); ((JObject)missing["params"]!["arguments"]!).Remove("documentId");
            Check((bool)JObject.Parse(host.Call(missing).Result)["isError"]!, "missing document accepted");
            var unsupported = Submit("key", "value"); unsupported["params"]!["arguments"]!["operation"] = "delete_script";
            Check(JObject.Parse(host.Call(unsupported).Result)["errorCode"]!.ToString() == "UNSUPPORTED_JOB", "storage job accepted");
            var badTarget = Submit("key", "value"); badTarget["params"]!["arguments"]!["targetInstanceId"] = "wrong";
            Check(JObject.Parse(host.Call(badTarget).Result)["errorCode"]!.ToString() == "INSTANCE_CHANGED", "wrong instance accepted");
        });
        Case("Dynamo absence and invalid input are typed failures", () =>
        {
            foreach (var action in new Action[] { () => DynamoBridge.Status(), () => DynamoBridge.AddNode("", "", ""), () => DynamoBridge.BuildGraph("") })
            {
                bool failed = false;
                try { action(); } catch (JobException ex) { failed = ex.Code == "DYNAMO_OPERATION_FAILED"; }
                Check(failed, "Dynamo error returned as success");
            }
        });
        Case("Dynamo waits for cache refresh and preserves uncertain dispatch reservation", () =>
        {
            var model = new Dynamo.Applications.FakeModel();
            Dynamo.Applications.DynamoRevit.RevitDynamoModel = model;
            try
            {
                var task = DynamoBridge.ExecuteTracked("dynamo_run_current", new JObject(), null, () => "");
                model.CurrentWorkspace.Complete(); DynamoBridge.PollEvaluation();
                Check(!task.IsCompleted, "evaluation completion mistaken for refreshed values");
                model.CurrentWorkspace.Refresh(); DynamoBridge.PollEvaluation();
                Check(task.IsCompletedSuccessfully, "cache refresh not finalized");
                model.ThrowAfterStart = true;
                var uncertain = DynamoBridge.ExecuteTracked("dynamo_run_current", new JObject(), null, () => "");
                Check(!uncertain.IsCompleted && (bool)DynamoBridge.CaptureState()!["evaluationPending"]!, "reservation released after uncertain dispatch");
                try { DynamoBridge.ExecuteTracked("dynamo_run_current", new JObject(), null, () => ""); throw new Exception("second run allowed"); }
                catch (JobException ex) { Check(ex.Code == "DYNAMO_BUSY", ex.Code); }
                model.CurrentWorkspace.Refresh(); DynamoBridge.PollEvaluation();
                try { uncertain.GetAwaiter().GetResult(); throw new Exception("uncertain dispatch reported success"); }
                catch (JobException ex) { Check(ex.Code == "OUTCOME_UNCERTAIN", ex.Code); }
            }
            finally { Dynamo.Applications.DynamoRevit.RevitDynamoModel = null; }
        });
        Console.WriteLine("TOTAL " + passed + " passed; Revit session tests use stubs, not a live host.");
    }
    static UIApplication Host()
    {
        var ui = new UIApplication(); var doc = new Document(); ui.Application.Documents.Add(doc);
        ui.ActiveUIDocument = new UIDocument { Document = doc }; return ui;
    }
    static JObject Request(string operation, JObject args) => new JObject { ["params"] = new JObject { ["name"] = operation, ["arguments"] = args } };
    static JObject Submit(string key, string value) => Request("submit_job", new JObject {
        ["operation"] = "execute_script", ["arguments"] = new JObject { ["value"] = value },
        ["targetInstanceId"] = RevitApp.Instance, ["documentId"] = RevitApp.ActiveDoc, ["idempotencyKey"] = key });
}
