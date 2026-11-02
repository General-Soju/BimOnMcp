using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BimOnMcpShared;
using Newtonsoft.Json.Linq;

internal static class Tests
{
    static int passed;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Case(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
    static void Code(string code, Action action)
    { try { action(); } catch (JobException ex) { Check(ex.Code == code, "Wrong code " + ex.Code); return; } throw new Exception("Expected " + code); }
    static JobSnapshot Add(JobQueue<int> q, string name, Func<int, string> work, string? key = null) => q.Submit(name, "doc-A", name, key, DateTime.UtcNow.AddMinutes(1), work);
    static async Task Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--storage-writer")
        {
            for (int n = 0; n < 5; n++)
                for (int attempt = 0; ; attempt++)
                    try { new ScriptStorageService(args[1]).Save("Revit", args[2] + "_" + n, "", "result=1", Array.Empty<string>()); break; }
                    catch (IOException ex) when (attempt < 10 && ex.Message.StartsWith("Script storage is busy"))
                    { Thread.Sleep(50); } // Busy is rejected before any file mutation; safe to retry.
            return;
        }
        Case("script storage mutex preserves concurrent process writes", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "BimOnStorageProcesses-" + Guid.NewGuid().ToString("N"));
            var children = new List<Process>();
            try
            {
                string executable = Process.GetCurrentProcess().MainModule!.FileName;
                string prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                    ? "\"" + typeof(Tests).Assembly.Location + "\" " : "";
                for (int n = 0; n < 6; n++) children.Add(Process.Start(new ProcessStartInfo(executable,
                    prefix + "--storage-writer \"" + root + "\" writer" + n) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!);
                foreach (var child in children)
                {
                    Check(child.WaitForExit(20000), "storage worker timed out");
                    Check(child.ExitCode == 0, "storage worker failed: " + child.StandardError.ReadToEnd());
                }
                Check(new ScriptStorageService(root).GetAll().Count == 30, "cross-process update lost");
            }
            finally
            {
                foreach (var child in children) { if (!child.HasExited) child.Kill(); child.Dispose(); }
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        Case("uncertain dispatch outcome is not reported as definite failure", () =>
        {
            var q = new JobQueue<int>("host");
            var job = Add(q, "run", _ => throw new JobException("OUTCOME_UNCERTAIN", "inspect first"), "same");
            q.ExecuteNext(0);
            Check(q.Get(job.JobId).Status == "unknown", "uncertain execution mislabeled");
            Check(Add(q, "run", _ => "must not execute", "same").JobId == job.JobId, "uncertain write replayed");
        });
        Case("script stores serialize independent writers and preserve corrupt metadata", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "BimOnStorageTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Parallel.For(0, 12, i => new ScriptStorageService(root).Save("Revit", "test" + i, "", "result=" + i, Array.Empty<string>()));
                var store = new ScriptStorageService(root);
                Check(store.GetAll().Count == 12, "lost script updates");
                Check(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length == 0, "temporary files left");
                string metadata = Path.Combine(root, "scripts_meta.json"); File.WriteAllText(metadata, "{broken");
                bool rejected = false;
                try { store.Save("Revit", "later", "", "result=1", Array.Empty<string>()); } catch (IOException) { rejected = true; }
                Check(rejected && File.ReadAllText(metadata) == "{broken", "corrupt metadata silently reset");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        Case("host adapter rejects stale content and deduplicates after its own mutation", () =>
        {
            int writes = 0;
            using var service = new HostJobService("unit-test", (_, __) => { writes++; return Task.FromResult("ok"); }, name => name == "write");
            service.Publish(new JObject { ["activeDocumentId"] = "doc", ["contentRevision"] = 1 });
            JObject Request(JObject args) => new JObject { ["params"] = new JObject { ["name"] = "submit_job", ["arguments"] = args } };
            var input = new JObject { ["targetInstanceId"] = service.InstanceId, ["documentId"] = "doc", ["idempotencyKey"] = "key", ["operation"] = "write", ["arguments"] = new JObject() };
            var accepted = JObject.Parse(service.Handle(Request(input), CancellationToken.None).GetAwaiter().GetResult());
            var id = accepted["job"]!["JobId"]!.ToString();
            service.Pump(); Check(writes == 1 && service.Jobs.Get(id).Status == "succeeded", "host execution");
            service.Publish(new JObject { ["activeDocumentId"] = "doc", ["contentRevision"] = 2 });
            var retry = JObject.Parse(service.Handle(Request(input), CancellationToken.None).GetAwaiter().GetResult());
            Check(retry["job"]!["JobId"]!.ToString() == id && writes == 1, "mutation broke idempotency");
            input["idempotencyKey"] = "stale"; input["contentRevision"] = "1";
            var stale = JObject.Parse(service.Handle(Request(input), CancellationToken.None).GetAwaiter().GetResult());
            Check(stale["errorCode"]?.ToString() == "CONTENT_CHANGED", "stale content accepted");
            var control = JObject.Parse(service.HandleControl(Request(input), CancellationToken.None).GetAwaiter().GetResult());
            Check(control["errorCode"]?.ToString() == "CONTROL_ONLY", "control executed write");
        });
        Case("deferred completion reserves executor without blocking host", () =>
        {
            var q = new JobQueue<int>("async-host");
            var evaluation = new TaskCompletionSource<string>();
            int writes = 0;
            var a = q.SubmitAsync("evaluate", "doc", "input", "key", DateTime.UtcNow.AddMinutes(1), _ => evaluation.Task);
            var b = Add(q, "next", _ => { writes++; return "next"; });
            Check(q.ExecuteNext(0) && q.Get(a.JobId).Status == "running", "starter blocked or premature success");
            Check(!q.ExecuteNext(0) && writes == 0, "reservation lost");
            Check(q.Cancel(a.JobId).CancelRequested && q.Get(a.JobId).Status == "running", "forced abort");
            evaluation.SetResult("evaluated");
            Check(q.Completion(a.JobId).Wait(3000), "completion not observed");
            Check(q.ExecuteNext(0) && q.Get(b.JobId).Status == "succeeded", "reservation not released");
        });
        Case("late async completion cannot overwrite shutdown unknown", () =>
        {
            var q = new JobQueue<int>("async-host"); var evaluation = new TaskCompletionSource<string>();
            var a = q.SubmitAsync("evaluate", "doc", "input", null, DateTime.UtcNow.AddMinutes(1), _ => evaluation.Task);
            q.ExecuteNext(0); q.Stop(); evaluation.SetResult("late");
            Check(q.Get(a.JobId).Status == "unknown", "shutdown result overwritten");
        });
        Case("FIFO and per-request result association", () =>
        {
            var q = new JobQueue<int>("host"); var order = new List<int>();
            var jobs = Enumerable.Range(0, 7).Select(i => Add(q, "job" + i, _ => { order.Add(i); return "result" + i; })).ToArray();
            while (q.ExecuteNext(0)) { }
            Check(order.SequenceEqual(Enumerable.Range(0, 7)), "FIFO");
            for (int i = 0; i < jobs.Length; i++) Check(q.Completion(jobs[i].JobId).Result.Result == "result" + i, "Misrouted completion");
        });
        Case("response wait expiry does not overwrite running job", () =>
        {
            var q = new JobQueue<int>("host"); using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var a = Add(q, "A", _ => { started.Set(); Check(release.Wait(5000), "release timeout"); return "A result"; });
            var running = Task.Run(() => q.ExecuteNext(0)); Check(started.Wait(5000), "start timeout");
            try
            {
                Check(!q.Completion(a.JobId).Wait(20), "unexpected completion");
                var b = Add(q, "B", _ => "B result");
                Check(!q.ExecuteNext(0), "overlapping executor");
                Check(q.Get(a.JobId).Status == "running" && q.Get(b.JobId).Status == "queued", "states");
                release.Set(); running.GetAwaiter().GetResult(); q.ExecuteNext(0);
                Check(q.Get(a.JobId).Result == "A result" && q.Get(b.JobId).Result == "B result", "overwritten request");
            }
            finally { release.Set(); }
        });
        Case("seven concurrent duplicate submissions execute once", () =>
        {
            var q = new JobQueue<int>("host"); int writes = 0;
            var ids = new string[7];
            Parallel.For(0, 7, i => ids[i] = Add(q, "write", _ => { Interlocked.Increment(ref writes); return "ok"; }, "same-key").JobId);
            Check(ids.Distinct().Count() == 1, "duplicate IDs"); q.ExecuteNext(0); Check(writes == 1, "duplicate write");
            Check(Add(q, "write", _ => "other", "same-key").JobId == ids[0], "response-loss retry");
            Code("IDEMPOTENCY_CONFLICT", () => Add(q, "different", _ => "", "same-key"));
        });
        Case("queued deadline prevents late execution", () =>
        {
            var now = DateTime.UtcNow; var q = new JobQueue<int>("host", now: () => now); int writes = 0;
            var j = q.Submit("write", "doc", "input", "key", now.AddSeconds(25), _ => { writes++; return ""; });
            now = now.AddSeconds(26); Check(!q.ExecuteNext(0), "expired execution");
            Check(q.Get(j.JobId).Status == "expired" && writes == 0, "expired state");
        });
        Case("queued cancellation and running cancellation request", () =>
        {
            var q = new JobQueue<int>("host"); var j = Add(q, "queued", _ => throw new Exception("must not run"));
            Check(q.Cancel(j.JobId).Status == "cancelled" && !q.ExecuteNext(0), "queued cancellation");
            JobSnapshot? running = null;
            running = Add(q, "running", _ => { var state = q.Cancel(running!.JobId); Check(state.Status == "running" && state.CancelRequested, "forced cancellation"); return "done"; });
            q.ExecuteNext(0); Check(q.Get(running.JobId).Status == "succeeded" && q.Get(running.JobId).CancelRequested, "running completion");
        });
        Case("target mismatch fails before side effects", () =>
        {
            var q = new JobQueue<int>("host"); int writes = 0;
            var j = Add(q, "write", activeDocument => { if (activeDocument != 1) throw new JobException("TARGET_CHANGED", "changed"); writes++; return ""; });
            q.ExecuteNext(2); Check(q.Get(j.JobId).ErrorCode == "TARGET_CHANGED" && writes == 0, "wrong document write");
        });
        Case("exceptions are isolated and queue continues", () =>
        {
            var q = new JobQueue<int>("host"); var a = Add(q, "fail", _ => throw new InvalidOperationException("test")); var b = Add(q, "ok", _ => "ok");
            q.ExecuteNext(0); q.ExecuteNext(0);
            Check(q.Get(a.JobId).Status == "failed" && q.Get(b.JobId).Status == "succeeded", "queue stuck");
            Check(q.Get(b.JobId).Verification == "not_checked", "false verification");
        });
        Case("bounds preserve keys and reject new work safely", () =>
        {
            var q = new JobQueue<int>("host", 1, 2); var a = Add(q, "a", _ => "", "a-key");
            Code("QUEUE_FULL", () => Add(q, "b", _ => "")); q.ExecuteNext(0);
            Add(q, "b", _ => ""); q.ExecuteNext(0);
            Code("HISTORY_FULL", () => Add(q, "c", _ => ""));
            Check(Add(q, "a", _ => "", "a-key").JobId == a.JobId, "key evicted");
        });
        Case("shutdown leaves running outcome unknown and never replays", () =>
        {
            var q = new JobQueue<int>("host"); using var start = new ManualResetEventSlim(); using var end = new ManualResetEventSlim();
            var a = Add(q, "a", _ => { start.Set(); Check(end.Wait(5000), "release"); return "done"; });
            var b = Add(q, "b", _ => "must not execute");
            var executing = Task.Run(() => q.ExecuteNext(0)); Check(start.Wait(5000), "start");
            try { q.Stop(); Check(q.Get(a.JobId).Status == "unknown" && q.Get(b.JobId).Status == "cancelled", "shutdown states"); }
            finally { end.Set(); }
            executing.GetAwaiter().GetResult(); Check(q.Get(a.JobId).Status == "unknown", "unknown overwritten");
            Code("HOST_STOPPING", () => Add(q, "new", _ => ""));
        });
        Case("bounded results and isolated snapshots", () =>
        {
            var q = new JobQueue<int>("host"); var j = Add(q, "large", _ => new string('x', 100000)); q.ExecuteNext(0);
            var result = q.Get(j.JobId); Check(result.ResultTruncated && result.Result!.Length == 65536, "output unbounded");
            result.Status = "changed"; Check(q.Get(j.JobId).Status == "succeeded", "snapshot shared");
        });
        Case("audit excludes payload and error text", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "BimOnJobTests-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var audit = new JobAudit(dir, "test"); var q = new JobQueue<int>("host"); q.Changed += audit.Record;
                Add(q, "execute_script", _ => throw new Exception("PRIVATE_MODEL_INFORMATION")); q.ExecuteNext(0); audit.Flush();
                string text = File.ReadAllText(Directory.GetFiles(dir).Single());
                Check(!text.Contains("PRIVATE_MODEL_INFORMATION") && !text.Contains("Result\""), "payload leaked to audit");
                Check(text.Split('\n').Count(x => x.Length > 0) == 3, "missing state records");
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        });
        await PipeTest();
        await ControlPipeTest();
        Console.WriteLine("TOTAL " + passed + " passed; runtime=" + Environment.Version);
    }
    static async Task<string> Call(string pipeName, string op)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false));
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync(new JObject { ["op"] = op }.ToString(Newtonsoft.Json.Formatting.None));
        var read = reader.ReadLineAsync();
        Check(await Task.WhenAny(read, Task.Delay(5000)) == read, "pipe response timeout");
        return JObject.Parse((await read)!) ["result"]!.ToString();
    }
    static async Task PipeTest()
    {
        string name = "BimOnQueueTest-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        var blocked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        var server = new PipeServer(name, (req, _) => req["op"]?.ToString() == "slow"
            ? Slow() : Task.FromResult("status"), 8);
        Task<string> Slow() { Interlocked.Increment(ref started); return blocked.Task; }
        var serving = server.RunAsync(cts.Token);
        try
        {
            var seven = Enumerable.Range(0, 7).Select(_ => Call(name, "slow")).ToArray();
            var deadline = Stopwatch.StartNew();
            while (Volatile.Read(ref started) < 7 && deadline.ElapsedMilliseconds < 3000) await Task.Delay(10);
            Check(started == 7, "seven requests not accepted");
            var sw = Stopwatch.StartNew(); Check(await Call(name, "status") == "status", "status response");
            Check(sw.ElapsedMilliseconds < 1000, "status blocked behind API work");
            blocked.TrySetResult("done"); Check((await Task.WhenAll(seven)).All(x => x == "done"), "responses crossed");
            passed++; Console.WriteLine("PASS eight pipe lanes: status bypasses seven blocked clients");
            using (var idle = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            { await idle.ConnectAsync(3000); cts.Cancel(); }
            Check(await Task.WhenAny(serving, Task.Delay(3000)) == serving, "shutdown blocked by idle client");
            await serving; passed++; Console.WriteLine("PASS pipe shutdown closes silent clients");
        }
        finally { blocked.TrySetResult("done"); cts.Cancel(); }
    }
    static async Task ControlPipeTest()
    {
        var name = "BimOnFullQueue-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        var execution = new PipeServer(name, (_, __) => { Interlocked.Increment(ref started); return release.Task; }, 8).RunAsync(cts.Token);
        var control = new PipeServer(name + ".control", (_, __) => Task.FromResult("status"), 2).RunAsync(cts.Token);
        try
        {
            var all = Enumerable.Range(0, 8).Select(_ => Call(name, "slow")).ToArray();
            var timer = Stopwatch.StartNew();
            while (Volatile.Read(ref started) != 8 && timer.ElapsedMilliseconds < 3000) await Task.Delay(10);
            Check(started == 8, "execution lanes not saturated");
            timer.Restart(); Check(await Call(name + ".control", "status") == "status", "control unavailable");
            Check(timer.ElapsedMilliseconds < 1000, "control blocked by execution");
            release.SetResult("done"); await Task.WhenAll(all);
            passed++; Console.WriteLine("PASS reserved control pipe responds with all eight execution lanes occupied");
        }
        finally { release.TrySetResult("done"); cts.Cancel(); await Task.WhenAll(execution, control); }
    }
}
