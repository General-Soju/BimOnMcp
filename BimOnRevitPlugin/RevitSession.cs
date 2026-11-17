using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BimOnMcpShared;
using Newtonsoft.Json.Linq;

namespace BimOnRevitPlugin
{
    // Autodesk objects stay on the UI thread; pipe/timer threads read copied JSON only.
    internal sealed class RevitSession
    {
        private readonly object _gate = new object();
        // Closed Revit wrappers can throw from Equals/GetHashCode. Never use them as dictionary keys.
        private readonly List<(Document Document, string Id)> _documents = new List<(Document, string)>();
        private JObject _snapshot = new JObject();
        public string InstanceId { get; }
        public RevitSession()
        {
            using var process = Process.GetCurrentProcess();
            InstanceId = process.Id + "-" + process.StartTime.ToUniversalTime().Ticks;
        }
        public void Refresh(UIApplication app)
        {
            var open = app.Application.Documents.Cast<Document>().Where(d => d.IsValidObject && !d.IsLinked).ToList();
            _documents.RemoveAll(entry => !entry.Document.IsValidObject || !open.Any(d => d.Equals(entry.Document)));
            var active = app.ActiveUIDocument?.Document;
            var rows = new JArray();
            string? activeId = null;
            foreach (var doc in open)
            {
                var tracked = _documents.FirstOrDefault(entry => entry.Document.IsValidObject && doc.Equals(entry.Document));
                var id = tracked.Id;
                if (id == null) _documents.Add((doc, id = Guid.NewGuid().ToString("N")));
                bool selected = doc.Equals(active);
                if (selected) activeId = id;
                rows.Add(new JObject { ["documentId"] = id, ["title"] = doc.Title,
                    ["active"] = selected, ["readOnly"] = doc.IsReadOnly, ["family"] = doc.IsFamilyDocument });
            }
            lock (_gate) _snapshot = new JObject { ["instanceId"] = InstanceId,
                ["activeDocumentId"] = activeId, ["documents"] = rows,
                ["capturedUtc"] = DateTime.UtcNow, ["revitVersion"] = app.Application.VersionNumber,
                ["dynamo"] = DynamoBridge.CaptureState(),
                ["revitBuild"] = app.Application.VersionBuild };
        }
        public JObject Snapshot() { lock (_gate) return (JObject)_snapshot.DeepClone(); }
        public string Title => Snapshot()["documents"]?.FirstOrDefault(d => (bool?)d["active"] == true)?["title"]?.ToString() ?? "Revit";
        public string CaptureTarget(string? requested)
        {
            var state = Snapshot();
            if (state["capturedUtc"] == null) throw new JobException("HOST_NOT_READY", "Wait for the first Revit idle event.");
            string active = state["activeDocumentId"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(active)) throw new JobException("NO_ACTIVE_DOCUMENT", "Open a document before submitting work.");
            if (!string.IsNullOrEmpty(requested) && requested != active)
                throw new JobException("TARGET_NOT_ACTIVE", "Activate the requested document in Revit before submitting work.");
            return active;
        }
        public Document ValidateTarget(UIApplication app, string documentId)
        {
            Refresh(app);
            if (Snapshot()["activeDocumentId"]?.ToString() != documentId)
                throw new JobException("TARGET_CHANGED", "The target document closed or is no longer active; nothing was executed.");
            return app.ActiveUIDocument?.Document ?? throw new JobException("TARGET_CHANGED", "The target document is no longer active.");
        }
        public JObject EnvironmentInfo()
        {
            var info = Snapshot();
            info.Remove("documents");
            info["instanceId"] = InstanceId;
            info["runtime"] = RuntimeInformation.FrameworkDescription;
            info["pluginVersion"] = typeof(RevitSession).Assembly.GetName().Version?.ToString();
            info["implementation"] = "revit-jobs-v1-preview";
            info["pluginTargetFramework"] = "net8.0-windows";
            info["revitApiVersion"] = typeof(Document).Assembly.GetName().Version?.ToString();
            var dependencies = new JObject();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName();
                if (name.Name == "IronPython" || name.Name == "DynamoCore" || name.Name == "DynamoRevit")
                    dependencies[name.Name] = name.Version?.ToString();
            }
            info["loadedDependencies"] = dependencies;
            info["jobProtocol"] = 1;
            info["separateControlPipe"] = true;
            info["pluginPath"] = typeof(RevitSession).Assembly.Location;
            info["queueCapacity"] = 64;
            info["sessionHistoryCapacity"] = 512;
            info["maxResultCharacters"] = 65536;
            info["runningCancellation"] = "request_only";
            info["persistence"] = "host_session_only";
            info["verification"] = "separate_readback_required";
            return info;
        }
    }
}
