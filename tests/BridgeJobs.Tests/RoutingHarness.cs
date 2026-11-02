using System;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using BimOnMcpShared;
using Newtonsoft.Json.Linq;

namespace BimOnRevitPlugin
{
    // Reuses production HandleRequest/Run/queue. Only the Autodesk host and operation body are simulated.
    public partial class RevitApp
    {
        private static RevitSession _session = new RevitSession();
        private static McpHandler? _handler;
        private static ExternalEvent? _exEvent = new ExternalEvent();
        private static UIApplication? _uiApp;
        private static JobAudit? _audit = null;
        private static readonly SemaphoreSlim _storageGate = new SemaphoreSlim(1, 1);
        internal static int Writes;
        internal static void Reset(UIApplication app)
        {
            _session = new RevitSession(); _session.Refresh(app);
            _handler = new McpHandler(_session.InstanceId); Writes = 0;
        }
        internal static string Instance => _session.InstanceId;
        internal static string ActiveDoc => _session.CaptureTarget(null);
        internal Task<string> Call(JObject request) => HandleRequest(request, CancellationToken.None);
        internal static void Pump(UIApplication app) => _handler!.Execute(app);
        private Task<string> Dispatch(JObject request, CancellationToken ct)
        {
            var args = request["params"]?["arguments"];
            return Run(_ => { Interlocked.Increment(ref Writes); return args?["value"]?.ToString() ?? "ok"; }, ct);
        }
    }
}
