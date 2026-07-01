using System;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BimOnRevitPlugin
{
    public class McpHandler : IExternalEventHandler
    {
        private Func<Document?, string>? _task;
        private TaskCompletionSource<string>? _tcs;

        public void SetTask(Func<Document?, string> task, TaskCompletionSource<string> tcs)
        {
            _task = task;
            _tcs  = tcs;
        }

        public void Execute(UIApplication app)
        {
            if (_task == null || _tcs == null) return;
            try
            {
                var doc    = app.ActiveUIDocument?.Document;
                var result = _task(doc);
                _tcs.SetResult(result);
            }
            catch (Exception ex) { _tcs.SetException(ex); }
        }

        public string GetName() => "BimOn Revit MCP Handler";
    }
}
