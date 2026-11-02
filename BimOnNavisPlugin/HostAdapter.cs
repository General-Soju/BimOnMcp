using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autodesk.Navisworks.Api;
using BimOnMcpShared;
using Newtonsoft.Json.Linq;
using NavisApplication = Autodesk.Navisworks.Api.Application;

namespace BimOnNavisPlugin
{
    public partial class NavisApp
    {
        internal static void StopJobs()
        {
            NavisApplication.Idle -= OnJobIdle;
            if (_trackedDocument != null)
            {
                _trackedDocument.Models.CollectionChanged -= OnContentChanged;
                _trackedDocument.FilesUpdated -= OnContentChanged;
                _trackedDocument = null;
            }
            _hostDispatcher = null;
            _jobs?.Dispose(); _cts?.Cancel();
            HostRegistry.Current?.Shutdown();
        }
        private static HostJobService? _jobs;
        private static Document? _trackedDocument;
        private static string _documentId = "no-document";
        private static long _contentRevision;
        private static System.Windows.Threading.Dispatcher? _hostDispatcher;
        private static readonly HashSet<string> _operations = new HashSet<string>(new[] {
            "get_document_title", "get_document_info", "get_model_count", "get_clash_tests", "get_clash_results",
            "approve_clash", "get_selection_sets", "get_viewpoints", "get_timeliner_tasks", "get_model_items",
            "get_item_properties", "execute_script", "list_scripts", "list_scripts_search", "save_script",
            "execute_saved_script", "delete_script" });
        private static void StartJobs()
        {
            _jobs = new HostJobService("navisworks", (req, ct) =>
            {
                try { return Task.FromResult(Dispatch(req, ct)); }
                finally { RefreshState(); }
            }, name => _operations.Contains(name));
            _jobs.WakeRequested = () => _hostDispatcher?.BeginInvoke(new Action(() => OnJobIdle(null, EventArgs.Empty)));
            string pipe = HostRegistry.Init("navisworks", PIPE_NAME, () => _jobs.Title);
            Task.Run(() => new PipeServer(pipe, _jobs.Handle, 8).RunAsync(_cts!.Token));
            Task.Run(() => new PipeServer(pipe + ".control", _jobs.HandleControl, 2).RunAsync(_cts!.Token));
            NavisApplication.Idle -= OnJobIdle;
            NavisApplication.Idle += OnJobIdle;
            AppDomain.CurrentDomain.ProcessExit += (_, __) => { _jobs?.Dispose(); _cts?.Cancel(); };
        }
        private static void OnContentChanged(object? sender, EventArgs args) => _contentRevision++;
        private static void OnJobIdle(object? sender, EventArgs args)
        {
            if (_jobs == null || _cts == null || _cts.IsCancellationRequested) return;
            try
            {
                // Capture from a Navisworks host callback, never from the palette STA.
                _hostDispatcher ??= System.Windows.Threading.Dispatcher.CurrentDispatcher;
                RefreshState();
                _jobs.Pump();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("BimOn Idle: " + ex); }
        }
        private static void RefreshState()
        {
            if (_jobs == null) return;
                var doc = NavisApplication.ActiveDocument;
                if (!ReferenceEquals(doc, _trackedDocument))
                {
                    if (_trackedDocument != null)
                    {
                        _trackedDocument.Models.CollectionChanged -= OnContentChanged;
                        _trackedDocument.FilesUpdated -= OnContentChanged;
                    }
                    _trackedDocument = doc; _documentId = doc == null ? "no-document" : Guid.NewGuid().ToString("N");
                    _contentRevision++;
                    if (doc != null)
                    {
                        doc.Models.CollectionChanged += OnContentChanged;
                        doc.FilesUpdated += OnContentChanged;
                    }
                }
                _jobs.Publish(new JObject { ["activeDocumentId"] = _documentId, ["contentRevision"] = _contentRevision,
                    ["documents"] = doc == null ? new JArray() : new JArray(new JObject { ["documentId"] = _documentId, ["title"] = doc.Title,
                        ["active"] = true, ["modelCount"] = doc.Models.Count }),
                    ["apiVersion"] = typeof(Document).Assembly.GetName().Version?.ToString(),
                    ["pluginPath"] = typeof(NavisApp).Assembly.Location, ["phase"] = "idle" });
        }
    }

    [Autodesk.Navisworks.Api.Plugins.Plugin("BimOnMcpStartup", "BIMON", DisplayName = "BimOn MCP startup")]
    public sealed class BimOnStartup : Autodesk.Navisworks.Api.Plugins.EventWatcherPlugin
    {
        public override void OnLoaded()
        {
            try { AssemblyBootstrap.Log("OnLoaded"); NavisApp.EnsureStarted(); }
            catch (Exception ex) { AssemblyBootstrap.Log(ex.ToString()); }
        }
        public override void OnUnloading() => NavisApp.StopJobs();
    }
}
