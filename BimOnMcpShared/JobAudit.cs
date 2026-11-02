using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace BimOnMcpShared
{
    // Best-effort metadata only. Never logs code, result, error messages, paths or document titles.
    public sealed class JobAudit : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Queue<string> _pending = new Queue<string>();
        private readonly string _directory, _file;
        private readonly Timer _timer;
        private int _flushing, _dropped;
        private bool _disposed;
        private volatile bool _writeFailed;
        public int Dropped => Volatile.Read(ref _dropped);
        public bool WriteFailed => _writeFailed;
        public JobAudit(string directory, string instanceId)
        {
            _directory = directory;
            _file = Path.Combine(directory, "jobs-" + DateTime.UtcNow.ToString("yyyyMMdd") + "-" + instanceId + ".jsonl");
            _timer = new Timer(_ => Flush(), null, 1000, 1000);
        }
        public void Record(JobSnapshot job)
        {
            var row = JsonConvert.SerializeObject(new { schema = 1, utc = DateTime.UtcNow, job.JobId,
                job.Operation, job.DocumentId, job.Status, job.ErrorCode, job.CancelRequested,
                job.CreatedUtc, job.StartedUtc, job.FinishedUtc, job.Verification, job.ResultTruncated });
            lock (_gate)
            {
                if (_disposed || _pending.Count >= 1024) { Interlocked.Increment(ref _dropped); return; }
                _pending.Enqueue(row);
            }
        }
        public void Flush()
        {
            if (Interlocked.CompareExchange(ref _flushing, 1, 0) != 0) return;
            var rows = new List<string>();
            try
            {
                lock (_gate) while (_pending.Count > 0) rows.Add(_pending.Dequeue());
                if (rows.Count == 0) return;
                Directory.CreateDirectory(_directory);
                foreach (var file in Directory.GetFiles(_directory, "jobs-*.jsonl"))
                {
                    var name = Path.GetFileName(file);
                    if (name.Length > 13 && DateTime.TryParseExact(name.Substring(5, 8), "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
                        && date < DateTime.UtcNow.Date.AddDays(-7))
                        try { File.Delete(file); } catch { }
                }
                if (File.Exists(_file) && new FileInfo(_file).Length >= 10 * 1024 * 1024)
                { Interlocked.Add(ref _dropped, rows.Count); return; }
                File.AppendAllLines(_file, rows, new UTF8Encoding(false));
                _writeFailed = false;
            }
            catch { _writeFailed = true; Interlocked.Add(ref _dropped, rows.Count); }
            finally { Volatile.Write(ref _flushing, 0); }
        }
        public void Dispose()
        {
            lock (_gate) _disposed = true;
            _timer.Dispose();
            Flush();
        }
    }
}
