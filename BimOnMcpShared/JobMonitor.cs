using System;

namespace BimOnMcpShared
{
    // Per-process, metadata-only view for the shared palette. No Autodesk API access.
    public static class JobMonitor
    {
        private static readonly object Gate = new object();
        private static JobSnapshot? _latest;
        private static Func<string, JobSnapshot>? _cancel;
        public static void Attach<T>(JobQueue<T> queue)
        {
            lock (Gate) { _latest = null; _cancel = queue.Cancel; }
            queue.Changed += state => { lock (Gate) _latest = state.Copy(); };
        }
        public static JobSnapshot? Latest { get { lock (Gate) return _latest?.Copy(); } }
        public static void CancelLatest()
        {
            string? id; Func<string, JobSnapshot>? cancel;
            lock (Gate) { id = _latest?.JobId; cancel = _cancel; }
            if (id != null) cancel?.Invoke(id);
        }
    }
}
