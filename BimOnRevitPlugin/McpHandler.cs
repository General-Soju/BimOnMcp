using System;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BimOnMcpShared;

namespace BimOnRevitPlugin
{
    public class McpHandler : IExternalEventHandler
    {
        public JobQueue<UIApplication> Jobs { get; }
        public McpHandler(string instanceId) { Jobs = new JobQueue<UIApplication>(instanceId); }
        public void Execute(UIApplication app) => Jobs.ExecuteNext(app);

        public string GetName() => "BimOn Revit MCP Handler";
    }
}
