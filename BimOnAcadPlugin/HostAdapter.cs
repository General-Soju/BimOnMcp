using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using BimOnMcpShared;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BimOnAcadPlugin
{
    public partial class AcadApp
    {
        private static HostJobService? _jobs;
        private static readonly Dictionary<Document, string> _documentIds = new Dictionary<Document, string>();
        private static readonly HashSet<string> _operations = new HashSet<string>(new[] {
            "get_drawing_info", "get_layer_list", "get_entity_count", "get_block_list", "get_lisp_routines",
            "get_selected_entities", "get_text_styles", "get_dim_styles", "get_layouts", "get_entities_by_layer",
            "get_entity_by_handle", "set_entity_property", "get_block_attributes", "execute_script", "list_scripts",
            "list_scripts_search", "save_script", "execute_saved_script", "delete_script" });
        private static void StartJobs()
        {
            _jobs = new HostJobService("autocad", Dispatch, name => _operations.Contains(name));
            string pipe = HostRegistry.Init("autocad", PIPE_NAME, () => _jobs.Title);
            Task.Run(() => new PipeServer(pipe, _jobs.Handle, 8).RunAsync(_cts.Token));
            Task.Run(() => new PipeServer(pipe + ".control", _jobs.HandleControl, 2).RunAsync(_cts.Token));
            AcApp.Idle += OnJobIdle;
        }
        private static void OnJobIdle(object? sender, EventArgs e)
        {
            if (_jobs == null || _cts.IsCancellationRequested) return;
            try
            {
                var documents = AcApp.DocumentManager.Cast<Document>().ToList();
                foreach (var closed in _documentIds.Keys.Where(d => !documents.Contains(d)).ToList()) _documentIds.Remove(closed);
                var active = AcApp.DocumentManager.MdiActiveDocument;
                var rows = new JArray(); string activeId = "no-document";
                foreach (var doc in documents)
                {
                    if (!_documentIds.TryGetValue(doc, out var id)) _documentIds.Add(doc, id = Guid.NewGuid().ToString("N"));
                    if (doc == active) activeId = id;
                    rows.Add(new JObject { ["documentId"] = id, ["title"] = System.IO.Path.GetFileName(doc.Name), ["active"] = doc == active });
                }
                bool busy = active != null && !string.IsNullOrEmpty(active.CommandInProgress);
                _jobs.Publish(new JObject { ["documents"] = rows, ["activeDocumentId"] = activeId, ["contentRevision"] = 0,
                    ["apiVersion"] = typeof(Document).Assembly.GetName().Version?.ToString(),
                    ["pluginPath"] = typeof(AcadApp).Assembly.Location, ["phase"] = busy ? "waiting_for_command" : "idle" });
                if (!busy) _jobs.Pump();
            }
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine("BimOn Idle: " + ex); }
        }
    }
}
