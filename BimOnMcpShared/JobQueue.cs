using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BimOnMcpShared
{
    // Host-neutral: no Autodesk objects, timers or API access. Execution is supplied by the host UI thread.
    public sealed class JobSnapshot
    {
        public string JobId { get; set; } = "";
        public string Operation { get; set; } = "";
        public string DocumentId { get; set; } = "";
        public string Status { get; set; } = "queued";
        public string? ErrorCode { get; set; }
        public string? Result { get; set; }
        public bool ResultTruncated { get; set; }
        public bool CancelRequested { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public string Verification { get; set; } = "not_checked";
        public bool IsTerminal => Status != "queued" && Status != "running";
        internal JobSnapshot Copy() => (JobSnapshot)MemberwiseClone();
    }

    public sealed class JobException : Exception
    {
        public string Code { get; }
        public JobException(string code, string message) : base(message) { Code = code; }
    }

    public sealed class JobQueue<T>
    {
        private sealed class Entry
        {
            internal JobSnapshot State = new JobSnapshot();
            internal string Fingerprint = "";
            internal DateTime Deadline;
            internal Func<T, Task<string>>? Work;
            internal readonly TaskCompletionSource<JobSnapshot> Completion =
                new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _jobs = new Dictionary<string, Entry>();
        private readonly Dictionary<string, Entry> _keys = new Dictionary<string, Entry>();
        private readonly Queue<Entry> _pending = new Queue<Entry>();
        private readonly Func<DateTime> _now;
        private readonly int _queueLimit, _historyLimit;
        private readonly string _instanceId;
        private bool _executing, _stopped;
        public event Action<JobSnapshot>? Changed;

        public JobQueue(string instanceId, int queueLimit = 64, int historyLimit = 512, Func<DateTime>? now = null)
        {
            if (queueLimit < 1 || historyLimit < 1) throw new ArgumentOutOfRangeException(nameof(queueLimit));
            _instanceId = instanceId; _queueLimit = queueLimit; _historyLimit = historyLimit;
            _now = now ?? (() => DateTime.UtcNow);
        }

        public JobSnapshot Submit(string operation, string documentId, string fingerprint,
            string? key, DateTime deadline, Func<T, string> work)
            => SubmitAsync(operation, documentId, fingerprint, key, deadline, context => Task.FromResult(work(context)));

        // The starter runs on the host thread. Its completion may arrive later from an
        // evaluation event; retain the execution reservation without blocking that thread.
        public JobSnapshot SubmitAsync(string operation, string documentId, string fingerprint,
            string? key, DateTime deadline, Func<T, Task<string>> work)
        {
            lock (_gate)
            {
                if (_stopped) throw new JobException("HOST_STOPPING", "Host is stopping.");
                if (!string.IsNullOrEmpty(key) && _keys.TryGetValue(key!, out var old))
                {
                    if (old.Fingerprint != fingerprint) throw new JobException("IDEMPOTENCY_CONFLICT", "Key was already used with different input or target.");
                    Expire(old);
                    return old.State.Copy();
                }
                SweepPending();
                if (_pending.Count >= _queueLimit) throw new JobException("QUEUE_FULL", "Host queue is full; nothing was executed.");
                // Never silently evict idempotency keys during this host session.
                if (_jobs.Count >= _historyLimit) throw new JobException("HISTORY_FULL", "Session result capacity reached; existing results remain available.");
                var entry = new Entry { Deadline = deadline, Work = work, Fingerprint = fingerprint,
                    State = new JobSnapshot { JobId = _instanceId + ":" + Guid.NewGuid().ToString("N"),
                        Operation = operation, DocumentId = documentId, CreatedUtc = _now() } };
                _jobs.Add(entry.State.JobId, entry);
                if (!string.IsNullOrEmpty(key)) _keys.Add(key!, entry);
                _pending.Enqueue(entry);
                Emit(entry);
                Expire(entry);
                return entry.State.Copy();
            }
        }

        public JobSnapshot Get(string id)
        {
            lock (_gate) { var e = Find(id); Expire(e); return e.State.Copy(); }
        }
        public Task<JobSnapshot> Completion(string id) { lock (_gate) return Find(id).Completion.Task; }
        public JobSnapshot Cancel(string id)
        {
            lock (_gate)
            {
                var e = Find(id); Expire(e);
                if (!e.State.IsTerminal)
                {
                    e.State.CancelRequested = true;
                    if (e.State.Status == "queued") Finish(e, "cancelled", "CANCELLED_BEFORE_START", null);
                    else Emit(e); // Running API/Python cannot be forcibly aborted safely.
                }
                return e.State.Copy();
            }
        }
        public bool HasPending { get { lock (_gate) { SweepPending(); return _pending.Count != 0; } } }

        public bool ExecuteNext(T context)
        {
            Entry e;
            Func<T, Task<string>> work;
            lock (_gate)
            {
                if (_executing || _stopped) return false;
                SweepPending();
                if (_pending.Count == 0) return false;
                e = _pending.Dequeue(); _executing = true;
                work = e.Work!;
                e.State.Status = "running"; e.State.StartedUtc = _now(); Emit(e);
            }
            _ = CompleteAsync(e, work, context);
            return true;
        }

        private async Task CompleteAsync(Entry e, Func<T, Task<string>> work, T context)
        {
            string? result = null, code = null;
            try { result = await work(context).ConfigureAwait(false); }
            catch (JobException ex) { code = ex.Code; result = ex.Message; }
            catch (Exception ex) { code = "EXECUTION_FAILED"; result = ex.GetType().Name + ": " + ex.Message; }
            lock (_gate)
            {
                _executing = false;
                if (!e.State.IsTerminal)
                    Finish(e, code == null ? "succeeded" : code == "OUTCOME_UNCERTAIN" ? "unknown" : "failed", code, result);
                e.Work = null;
            }
        }

        public void FailPending(string code, string message)
        {
            lock (_gate)
            {
                while (_pending.Count > 0)
                {
                    var e = _pending.Dequeue();
                    if (e.State.Status == "queued") Finish(e, "failed", code, message);
                }
            }
        }
        public void Stop()
        {
            lock (_gate)
            {
                _stopped = true;
                foreach (var e in _jobs.Values)
                    if (!e.State.IsTerminal) Finish(e, e.State.Status == "running" ? "unknown" : "cancelled", "HOST_STOPPING", null);
                _pending.Clear();
            }
        }
        private Entry Find(string id)
        {
            if (!_jobs.TryGetValue(id, out var e)) throw new JobException("JOB_NOT_FOUND", "Job is not available in this host session; do not automatically retry writes.");
            return e;
        }
        private void Expire(Entry e)
        {
            if (e.State.Status == "queued" && _now() >= e.Deadline) Finish(e, "expired", "START_DEADLINE_EXCEEDED", null);
        }
        private void SweepPending()
        {
            int count = _pending.Count;
            while (count-- > 0)
            {
                var e = _pending.Dequeue(); Expire(e);
                if (e.State.Status == "queued") _pending.Enqueue(e);
            }
        }
        private void Finish(Entry e, string status, string? code, string? result)
        {
            if (result != null && result.Length > 65536)
            {
                result = result.Substring(0, 65536);
                e.State.ResultTruncated = true;
            }
            e.State.Status = status; e.State.ErrorCode = code; e.State.Result = result;
            e.State.FinishedUtc = _now(); e.Work = null;
            e.Completion.TrySetResult(e.State.Copy()); Emit(e);
        }
        private void Emit(Entry e) { try { Changed?.Invoke(e.State.Copy()); } catch { /* audit must not break jobs */ } }
    }
}
