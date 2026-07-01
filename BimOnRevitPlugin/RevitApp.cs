using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using BimOnMcpShared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BimOnRevitPlugin
{
    [Transaction(TransactionMode.Manual)]
    public class RevitApp : IExternalApplication
    {
        private const string PIPE_NAME   = "BimOnRevitPipe";
        private const string HOST        = "Revit";

        private static readonly Encoding Utf8NoBom =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private static McpHandler?          _handler;
        private static ExternalEvent?       _exEvent;
        private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource     _cts = new();

        private static ScriptStorageService _storage = new();
        private static UIApplication?       _uiApp;
        private static ScriptPaletteContent? _palette;

        internal static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("A1B2C3D4-E5F6-7890-ABCD-EF0000000001"));

        public Result OnStartup(UIControlledApplication app)
        {
            _handler = new McpHandler();
            _exEvent = ExternalEvent.Create(_handler);
            _cts     = new CancellationTokenSource();
            _storage = new ScriptStorageService();

            app.Idling += (s, _) => { if (s is UIApplication u) _uiApp = u; };

            // 다중 인스턴스 관리: 고유 파이프명 + 활성 레지스트리 등록.
            // RegisterPalette 보다 먼저 호출 — 패널이 복원/생성되는 시점에
            // HostRegistry.Current 가 이미 준비돼 연결 바가 정상 표시되도록 한다.
            string pipeName = HostRegistry.Init("revit", PIPE_NAME,
                () => { try { return _uiApp?.ActiveUIDocument?.Document?.Title ?? "Revit"; }
                        catch { return "Revit"; } });

            RegisterPalette(app);
            RegisterRibbon(app);

            Task.Run(() => new PipeServer(pipeName, HandleRequest).RunAsync(_cts.Token));
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            HostRegistry.Current?.Shutdown();
            _cts.Cancel();
            return Result.Succeeded;
        }

        private static void RegisterPalette(UIControlledApplication app)
        {
            app.RegisterDockablePane(PaneId, "BimOn AI Scripts",
                new RevitPaletteProvider());
        }

        private static void RegisterRibbon(UIControlledApplication app)
        {
            // 별도 탭을 만들지 않고 기본 "Add-Ins" 탭에 BimOn 패널을 추가한다.
            RibbonPanel panel;
            try { panel = app.CreateRibbonPanel(Tab.AddIns, "BimOn"); }
            catch { panel = app.GetRibbonPanels(Tab.AddIns).FirstOrDefault(p => p.Name == "BimOn")
                            ?? app.CreateRibbonPanel("BimOn"); }

            var btn = new PushButtonData(
                "BimOnScripts", "BimOn",
                typeof(RevitApp).Assembly.Location,
                typeof(OpenPaletteCmd).FullName!);
            btn.ToolTip       = "BimOn AI";
            btn.LongDescription = "Open or close the BimOn AI Scripts palette.";
            btn.LargeImage    = CreateButtonIcon(32);
            btn.Image         = CreateButtonIcon(16);
            panel.AddItem(btn);
        }

        /// <summary>
        /// BimOn 브랜드 아이콘 — 다크 카드 위 육각형 테두리(시안→퍼플 그라데이션)와
        /// 흰색 "B" 모노그램.
        /// </summary>
        private static System.Windows.Media.ImageSource CreateButtonIcon(int size)
        {
            double s = size;
            var dg = new System.Windows.Media.DrawingGroup();
            using (var dc = dg.Open())
            {
                // ── 배경: 다크 카드 + 은은한 경계선 ──
                var bg = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x2E));
                double bw = Math.Max(0.6, s * 0.04);
                var borderPen = new System.Windows.Media.Pen(
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x3F, 0x3F, 0x5F)), bw);
                dc.DrawRoundedRectangle(bg, borderPen,
                    new System.Windows.Rect(bw / 2, bw / 2, s - bw, s - bw),
                    s * 0.18, s * 0.18);

                // ── 시안→퍼플 대각선 그라데이션 ──
                var grad = new System.Windows.Media.LinearGradientBrush
                {
                    StartPoint = new System.Windows.Point(0, 0),
                    EndPoint   = new System.Windows.Point(1, 1),
                };
                grad.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Color.FromRgb(0x00, 0xC2, 0xFF), 0));
                grad.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Color.FromRgb(0x8C, 0x52, 0xFF), 1));
                grad.Freeze();

                // ── 육각형 테두리 (pointy-top) ──
                double cx = s / 2.0, cy = s / 2.0, r = s * 0.32;
                System.Windows.Point V(double deg)
                {
                    double a = Math.PI * deg / 180.0;
                    return new System.Windows.Point(cx + r * Math.Cos(a), cy - r * Math.Sin(a));
                }
                var hexPen = new System.Windows.Media.Pen(grad, s * 0.06)
                {
                    LineJoin = System.Windows.Media.PenLineJoin.Round,
                };
                FillPoly(dc, hexPen, null, V(90), V(30), V(-30), V(-90), V(210), V(150));

                // ── "B" 모노그램 ──
                double fontSize = s * 0.44;
                var ft = new System.Windows.Media.FormattedText(
                    "B",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    new System.Windows.Media.Typeface(
                        new System.Windows.Media.FontFamily("Segoe UI"),
                        System.Windows.FontStyles.Normal,
                        System.Windows.FontWeights.Bold,
                        System.Windows.FontStretches.Normal),
                    fontSize,
                    System.Windows.Media.Brushes.White,
                    1.25);
                dc.DrawText(ft, new System.Windows.Point(
                    (s - ft.Width) / 2, (s - ft.Height) / 2));
            }
            var di = new System.Windows.Media.DrawingImage(dg);
            di.Freeze();
            return di;
        }

        private static System.Windows.Media.SolidColorBrush Brush(byte r, byte g, byte b)
        {
            var br = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        private static void FillPoly(
            System.Windows.Media.DrawingContext dc,
            System.Windows.Media.Pen pen,
            System.Windows.Media.Brush brush,
            params System.Windows.Point[] pts)
        {
            var geo = new System.Windows.Media.StreamGeometry();
            using (var gc = geo.Open())
            {
                gc.BeginFigure(pts[0], true, true);
                for (int i = 1; i < pts.Length; i++) gc.LineTo(pts[i], true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(brush, pen, geo);
        }

        // ── 요청 라우팅 ───────────────────────────────────────────────
        private async Task<string> HandleRequest(JObject req, CancellationToken ct)
        {
            var tool = req["params"]?["name"]?.ToString();
            var args = req["params"]?["arguments"] as JObject;

            return tool switch
            {
                "get_document_title"        => await Run(doc => doc == null ? "No document open" : $"Document title: {doc.Title}", ct),
                "get_document_info"         => await Run(GetDocumentInfo, ct),
                "get_element_count"         => await Run(doc => { if (doc==null) return "No document open"; using var colCnt = new FilteredElementCollector(doc); return $"Total elements: {colCnt.WhereElementIsNotElementType().GetElementCount():N0}"; }, ct),
                "get_levels"                => await Run(GetLevels, ct),
                "get_project_info"          => await Run(GetProjectInfo, ct),
                "get_warnings"              => await Run(GetWarnings, ct),
                "get_linked_documents"      => await Run(GetLinkedDocuments, ct),
                "get_selected_elements"     => await Run(GetSelectedElements, ct),
                "get_worksets"              => await Run(GetWorksets, ct),
                "get_units"                 => await Run(GetUnits, ct),
                "get_element_by_id"         => await Run(doc => GetElementById(doc, args), ct),
                "get_elements_by_category"  => await Run(doc => GetElementsByCategory(doc, args), ct),
                "get_elements_by_filter"    => await Run(doc => GetElementsByFilter(doc, args), ct),
                "get_element_parameters"    => await Run(doc => GetElementParameters(doc, args), ct),
                "set_element_parameter"     => await Run(doc => SetElementParameter(doc, args), ct),
                "get_views"                 => await Run(doc => GetViews(doc, args), ct),
                "get_sheets"                => await Run(doc => GetSheets(doc, args), ct),
                "get_schedules"             => await Run(doc => GetSchedules(doc, args), ct),
                "get_rooms"                 => await Run(doc => GetRooms(doc, args), ct),
                "get_families"              => await Run(doc => GetFamilies(doc, args), ct),
                "get_family_types"          => await Run(doc => GetFamilyTypes(doc, args), ct),
                "get_element_geometry"      => await Run(doc => GetElementGeometry(doc, args), ct),
                "get_shared_parameters"     => await Run(GetSharedParameters, ct),

                // 스크립트 도구
                "execute_script"            => await HandleExecuteScript(args, ct),
                "list_scripts"              => HandleListScripts(null, null),
                "list_scripts_search"       => HandleListScripts(args?["keyword"]?.ToString(), null),
                "save_script"               => await HandleSaveScript(args, ct),
                "execute_saved_script"      => await HandleExecuteSavedScript(args, ct),
                "delete_script"             => HandleDeleteScript(args),

                // 실행 중 Dynamo 연동 (리플렉션, 메인스레드)
                "dynamo_status"             => await Run(_ => DynamoBridge.Status(), ct),
                "dynamo_get_graph"          => await Run(_ => DynamoBridge.GetGraph(), ct),
                "dynamo_get_node_values"    => await Run(_ => DynamoBridge.GetNodeValues(args?["filter"]?.ToString() ?? ""), ct),
                "dynamo_set_input"          => await Run(_ => DynamoBridge.SetInput(args?["nodeName"]?.ToString() ?? "", args?["value"]?.ToString() ?? ""), ct),
                "dynamo_run_current"        => await Run(_ => DynamoBridge.RunCurrent(), ct),
                // 그래프 편집 (phase 2)
                "dynamo_add_code_block"     => await Run(_ => DynamoBridge.AddCodeBlock(args?["code"]?.ToString() ?? "", args?["x"]?.ToString() ?? "", args?["y"]?.ToString() ?? ""), ct),
                "dynamo_add_node"           => await Run(_ => DynamoBridge.AddNode(args?["typeName"]?.ToString() ?? "", args?["x"]?.ToString() ?? "", args?["y"]?.ToString() ?? ""), ct),
                "dynamo_add_python_node"    => await Run(_ => DynamoBridge.AddPythonNode(args?["code"]?.ToString() ?? "", args?["engine"]?.ToString() ?? "", args?["inputs"]?.ToString() ?? "", args?["x"]?.ToString() ?? "", args?["y"]?.ToString() ?? ""), ct),
                "dynamo_connect"            => await Run(_ => DynamoBridge.Connect(args?["fromNode"]?.ToString() ?? "", args?["fromPort"]?.ToString() ?? "0", args?["toNode"]?.ToString() ?? "", args?["toPort"]?.ToString() ?? "0"), ct),
                "dynamo_delete_node"        => await Run(_ => DynamoBridge.DeleteNode(args?["nodeName"]?.ToString() ?? ""), ct),
                "dynamo_build_graph"        => await Run(_ => DynamoBridge.BuildGraph(args?["spec"]?.ToString() ?? ""), ct),
                "dynamo_search_nodes"       => await Run(_ => DynamoBridge.SearchNodes(args?["query"]?.ToString() ?? "", args?["limit"]?.ToString() ?? ""), ct),

                _ => $"[Error] Unknown tool: {tool}"
            };
        }

        // ── 스크립트 도구 ─────────────────────────────────────────────
        // Revit IronPython 공통 프리앰블 — clr 로드 및 주요 네임스페이스 import
        private const string RevitPreamble =
            "import clr\n" +
            "clr.AddReference('RevitAPI')\n" +
            "clr.AddReference('RevitAPIUI')\n" +
            "from Autodesk.Revit.DB import *\n" +
            "from Autodesk.Revit.DB.Architecture import *\n" +
            "from Autodesk.Revit.UI import *\n" +
            "from Autodesk.Revit.DB.Mechanical import *\n" +
            "from Autodesk.Revit.DB.Electrical import *\n" +
            "from Autodesk.Revit.DB.Plumbing import *\n";

        private Task<string> HandleExecuteScript(JObject? args, CancellationToken ct)
        {
            string code = args?["code"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(code)) return Task.FromResult("[Error] code is empty.");
            bool needPreamble    = !code.Contains("clr.AddReference");
            // If user code already manages its own Transaction, don't auto-wrap
            bool userHasTx       = code.Contains("Transaction(") || code.Contains(".Start()");

            return Run(doc =>
            {
                if (doc == null) return "No document open";
                var vars = new Dictionary<string, object?>
                {
                    ["doc"]   = doc,
                    ["uidoc"] = new UIDocument(doc),
                    ["app"]   = doc.Application
                };

                if (!userHasTx && !doc.IsReadOnly)
                {
                    // Auto-wrap in Transaction so AI code can create/modify
                    // elements without needing explicit Transaction management.
                    using var tx = new Transaction(doc, "BimOn AI Script");
                    tx.Start();
                    var r = ScriptExecutor.Execute(code, vars,
                        preamble: needPreamble ? RevitPreamble : null);
                    if (r.Success) tx.Commit();
                    else           tx.RollBack();
                    return ScriptExecutor.FormatResult(r);
                }

                // User manages Transaction (or document is read-only)
                return ScriptExecutor.FormatResult(
                    ScriptExecutor.Execute(code, vars,
                        preamble: needPreamble ? RevitPreamble : null));
            }, ct);
        }

        private string HandleListScripts(string? keyword, string? host)
        {
            var metas = string.IsNullOrWhiteSpace(keyword)
                ? _storage.GetAll()
                : _storage.Search(keyword, host);
            if (metas.Count == 0) return "No saved scripts found.";
            var sb = new StringBuilder($"Saved scripts: {metas.Count}\n\n");
            foreach (var m in metas)
            {
                sb.AppendLine($"▶ [{m.Host}] {m.Name}");
                sb.AppendLine($"   Desc: {m.Description}");
                sb.AppendLine($"   Tags: {string.Join(", ", m.Tags)}");
                sb.AppendLine($"   Created: {m.CreatedAt}");
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private async Task<string> HandleSaveScript(JObject? args, CancellationToken ct)
        {
            string name        = args?["name"]?.ToString() ?? "";
            string description = args?["description"]?.ToString() ?? "";
            string userCode    = args?["code"]?.ToString() ?? "";
            string tagsRaw     = args?["tags"]?.ToString() ?? "";
            string panel       = args?["panel"]?.ToString() ?? "General";
            bool   execAfter   = (args?["executeAfterSave"]?.ToString() ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(name))     return "[Error] name is empty.";
            if (string.IsNullOrWhiteSpace(userCode)) return "[Error] code is empty.";

            string[] tags = tagsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToArray();
            var meta = _storage.Save(HOST, name, description, userCode, tags, panel);

            _palette?.Dispatcher.InvokeAsync(() => _palette?.LoadScripts());

            var sb = new StringBuilder($"✅ [{HOST}] '{name}' saved\n");
            sb.AppendLine($"   Path: {meta.ScriptPath}");

            if (execAfter)
            {
                sb.AppendLine("\n── Execute Result ──");
                sb.AppendLine(await HandleExecuteScript(args, ct));
            }
            return sb.ToString().TrimEnd();
        }

        private string HandleDeleteScript(JObject? args)
        {
            string name = args?["scriptName"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(name)) return "[Error] scriptName is empty.";
            var meta = _storage.FindByName(name);
            if (meta == null) return $"[Error] Script '{name}' not found.";
            bool ok = _storage.Delete(meta.Name, meta.Host);
            if (!ok) return $"[Error] Failed to delete '{name}'.";
            _palette?.Dispatcher.InvokeAsync(() => _palette?.LoadScripts());
            return $"✅ Deleted '{meta.Name}' ({meta.Host})";
        }

        private Task<string> HandleExecuteSavedScript(JObject? args, CancellationToken ct)
        {
            string scriptName   = args?["scriptName"]?.ToString() ?? "";
            string paramsJson   = args?["parameters"]?.ToString() ?? "";
            string overrideCode = args?["overrideCode"]?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(scriptName)) return Task.FromResult("[Error] scriptName is empty.");
            var meta = _storage.FindByName(scriptName);
            if (meta == null) return Task.FromResult($"[오류] Script '{scriptName}' not found.");
            if (!meta.Host.Equals(HOST, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult($"[Error] This script is for {meta.Host}. Current: {HOST}");

            string code = !string.IsNullOrWhiteSpace(overrideCode)
                ? overrideCode
                : (File.Exists(meta.ScriptPath)
                    ? ScriptStorageService.ExtractUserCode(File.ReadAllText(meta.ScriptPath))
                    : "");
            if (string.IsNullOrWhiteSpace(code)) return Task.FromResult("[Error] Script file not found.");

            if (!string.IsNullOrWhiteSpace(paramsJson))
            {
                try
                {
                    var d = JsonConvert.DeserializeObject<Dictionary<string,string>>(paramsJson);
                    if (d != null) foreach (var kv in d) code = code.Replace($"{{{{{kv.Key}}}}}", kv.Value);
                }
                catch { }
            }

            var execArgs = new JObject { ["code"] = code };
            return Task.FromResult($"▶ '{scriptName}'\n").ContinueWith(
                _ => HandleExecuteScript(execArgs, ct).Result);
        }

        // ── Revit 메인 스레드 실행 ────────────────────────────────────
        private async Task<string> Run(Func<Document?, string> task, CancellationToken ct)
        {
            await _lock.WaitAsync(ct);
            try
            {
                var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
                _handler!.SetTask(task, tcs);
                _exEvent!.Raise();
                using var timeout = new CancellationTokenSource(25_000);
                var done = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeout.Token));
                if (done != tcs.Task) return "[Error] Revit timeout";
                return await tcs.Task;
            }
            catch (OperationCanceledException) { return "[Error] Cancelled"; }
            catch (Exception ex) { return $"[Error] {ex.Message}"; }
            finally { _lock.Release(); }
        }

        // ── 기존 Revit 도구 구현 ─────────────────────────────────────

        private static string GetDocumentInfo(Document? doc)
        {
            if (doc == null) return "No document open";
            return $"Title: {doc.Title}\nPath: {(string.IsNullOrEmpty(doc.PathName) ? "(unsaved)" : doc.PathName)}\nWorkshared: {doc.IsWorkshared}\nModified: {doc.IsModified}\nRevit {doc.Application.VersionNumber}";
        }

        private static string GetLevels(Document? doc)
        {
            if (doc == null) return "No document open";
            using var colLv = new FilteredElementCollector(doc);
            var levels = colLv.OfClass(typeof(Level)).ToElements();
            var sb = new StringBuilder($"Levels ({levels.Count}):\n");
            foreach (Level lv in levels)
                sb.AppendLine($"  • {lv.Name} — {UnitUtils.ConvertFromInternalUnits(lv.Elevation, UnitTypeId.Meters):F3}m");
            return sb.ToString().TrimEnd();
        }

        private static string GetProjectInfo(Document? doc)
        {
            if (doc == null) return "No document open";
            var sb = new StringBuilder("Project info:\n");
            foreach (Parameter p in doc.ProjectInformation.Parameters)
            {
                string v = p.StorageType == StorageType.String ? (p.AsString() ?? "") : (p.AsValueString() ?? "");
                if (!string.IsNullOrWhiteSpace(v)) sb.AppendLine($"  {p.Definition.Name}: {v}");
            }
            return sb.ToString().TrimEnd();
        }

        private static string GetWarnings(Document? doc)
        {
            if (doc == null) return "No document open";
            var w = doc.GetWarnings();
            if (w.Count == 0) return "No warnings.";
            var sb = new StringBuilder($"Warnings ({w.Count}):\n");
            for (int i = 0; i < w.Count; i++)
                sb.AppendLine($"  [{i+1}] {w[i].GetDescriptionText()}");
            return sb.ToString().TrimEnd();
        }

        private static string GetLinkedDocuments(Document? doc)
        {
            if (doc == null) return "No document open";
            using var colLnk = new FilteredElementCollector(doc);
            var links = colLnk.OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            if (links.Count == 0) return "No links.";
            var sb = new StringBuilder($"Links ({links.Count}):\n");
            foreach (var l in links) sb.AppendLine($"  • {l.Name} — {l.GetLinkDocument()?.PathName ?? "(unloaded)"}");
            return sb.ToString().TrimEnd();
        }

        private static string GetSelectedElements(Document? doc)
        {
            if (doc == null) return "No document open";
            var ids = new UIDocument(doc).Selection.GetElementIds();
            if (ids.Count == 0) return "No elements selected.";
            var sb = new StringBuilder($"Selected elements ({ids.Count}):\n");
            foreach (var id in ids) { var e = doc.GetElement(id); if (e == null || !e.IsValidObject) continue; sb.AppendLine($"  [{e.Id.Value}] {e.Name} / {e.Category?.Name}"); }
            return sb.ToString().TrimEnd();
        }

        private static string GetWorksets(Document? doc)
        {
            if (doc == null) return "No document open";
            if (!doc.IsWorkshared) return "Worksharing not enabled.";
            var ws = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).OrderBy(w => w.Name).ToList();
            var sb = new StringBuilder($"Worksets ({ws.Count}):\n");
            foreach (var w in ws) sb.AppendLine($"  {w.Name}  소유자:{(string.IsNullOrEmpty(w.Owner)?"-":w.Owner)}  {(w.IsEditable?"editable":"read-only")}");
            return sb.ToString().TrimEnd();
        }

        private static string GetUnits(Document? doc)
        {
            if (doc == null) return "No document open";
            var units = doc.GetUnits();
            var sb = new StringBuilder("Units:\n");
            foreach (var (sid, label) in new[] { (SpecTypeId.Length,"Length"),(SpecTypeId.Area,"Area"),(SpecTypeId.Volume,"Volume"),(SpecTypeId.Angle,"Angle") })
            {
                try { var f = units.GetFormatOptions(sid); sb.AppendLine($"  {label}: {f.GetUnitTypeId().TypeId}"); }
                catch { sb.AppendLine($"  {label}: 조회 불가"); }
            }
            return sb.ToString().TrimEnd();
        }

        private static string GetElementById(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            if (!long.TryParse(args?["elementId"]?.ToString(), out long eid)) return "Please provide a valid elementId.";
            var el = doc.GetElement(new ElementId(eid));
            if (el == null) return $"Element ID {eid} not found.";
            var sb = new StringBuilder($"[{eid}] {el.Name} / {el.Category?.Name}\n타입: {doc.GetElement(el.GetTypeId())?.Name}\n\n파라미터:\n");
            foreach (Parameter p in el.Parameters) { string v = p.StorageType==StorageType.String?(p.AsString()??""):(p.AsValueString()??""); if (!string.IsNullOrWhiteSpace(v)) sb.AppendLine($"  {p.Definition.Name}: {v}"); }
            return sb.ToString().TrimEnd();
        }

        private static string GetElementsByCategory(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            if (!long.TryParse(args?["categoryId"]?.ToString(), out long catId)) return "Please provide a valid categoryId.";
            int max = int.TryParse(args?["maxCount"]?.ToString(), out int mc) ? mc : 100;
            using var colCat = new FilteredElementCollector(doc);
            var elems = colCat.OfCategoryId(new ElementId(catId)).WhereElementIsNotElementType().Cast<Element>().Take(max).ToList();
            if (elems.Count == 0) return $"No elements in category {catId}.";
            var sb = new StringBuilder($"Category '{elems[0].Category?.Name}' ({elems.Count}):\n");
            foreach (var e in elems) sb.AppendLine($"  [{e.Id.Value}] {e.Name}");
            return sb.ToString().TrimEnd();
        }

        private static string GetElementsByFilter(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            long catId = long.TryParse(args?["categoryId"]?.ToString(), out long cid) ? cid : 0;
            string pname = args?["parameterName"]?.ToString() ?? ""; string pval = args?["parameterValue"]?.ToString() ?? "";
            int max = int.TryParse(args?["maxCount"]?.ToString(), out int mc) ? mc : 50;
            // using(col) ensures native Revit iterator is released — Revit 2025/.NET 8 leak fix
            FilteredElementCollector col = catId != 0
                ? new FilteredElementCollector(doc).OfCategoryId(new ElementId(catId))
                : new FilteredElementCollector(doc).WhereElementIsNotElementType();
            List<Element> matched;
            using (col)
            {
                matched = col.WhereElementIsNotElementType().Cast<Element>()
                    .Where(e => { var p = e.LookupParameter(pname); if (p==null) return false; string v = p.StorageType==StorageType.String?(p.AsString()??""):(p.AsValueString()??""); return v.Contains(pval,StringComparison.OrdinalIgnoreCase); })
                    .Take(max).ToList();
            }
            if (matched.Count == 0) return "No elements match the filter.";
            var sb = new StringBuilder($"Filter result ({matched.Count}):\n");
            foreach (var e in matched) sb.AppendLine($"  [{e.Id.Value}] {e.Name} / {e.Category?.Name}");
            return sb.ToString().TrimEnd();
        }

        private static string GetElementParameters(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            if (!long.TryParse(args?["elementId"]?.ToString(), out long eid)) return "Please provide a valid elementId.";
            string filter = args?["filterName"]?.ToString() ?? "";
            var el = doc.GetElement(new ElementId(eid)); if (el == null) return $"Element ID {eid} not found.";
            var sb = new StringBuilder($"Parameters [{eid}] {el.Name}:\n\n[Instance]\n");
            foreach (Parameter p in el.Parameters) { if (!string.IsNullOrEmpty(filter) && !p.Definition.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue; string v = p.StorageType==StorageType.String?(p.AsString()??""):(p.AsValueString()??""); sb.AppendLine($"  {p.Definition.Name}{(p.IsReadOnly?" (RO)":"")}: {v}"); }
            var tp = doc.GetElement(el.GetTypeId()); if (tp!=null) { sb.AppendLine("\n[Type]"); foreach (Parameter p in tp.Parameters) { if (!string.IsNullOrEmpty(filter) && !p.Definition.Name.Contains(filter,StringComparison.OrdinalIgnoreCase)) continue; string v = p.StorageType==StorageType.String?(p.AsString()??""):(p.AsValueString()??""); sb.AppendLine($"  {p.Definition.Name}: {v}"); } }
            return sb.ToString().TrimEnd();
        }

        private static string SetElementParameter(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            string pname = args?["parameterName"]?.ToString() ?? ""; string val = args?["value"]?.ToString() ?? "";
            var ids = new UIDocument(doc).Selection.GetElementIds(); if (!ids.Any()) return "No elements selected.";
            var el = doc.GetElement(ids.First());
            using var tx = new Transaction(doc, "BimOn Param"); tx.Start();
            var p = el.LookupParameter(pname); if (p==null||p.IsReadOnly) { tx.RollBack(); return "Parameter not found or read-only"; }
            bool ok = p.StorageType switch { StorageType.String => p.Set(val)||true, StorageType.Double => double.TryParse(val,System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out double d)&&p.Set(d)||false, StorageType.Integer => int.TryParse(val,out int i)&&p.Set(i)||false, _ => false };
            if (!ok) { tx.RollBack(); return "Value conversion failed"; } tx.Commit();
            return $"'{pname}' = '{val}' updated";
        }

        private static string GetViews(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            string tf = args?["viewType"]?.ToString() ?? "";
            using var colVw = new FilteredElementCollector(doc);
            var views = colVw.OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && (string.IsNullOrEmpty(tf)||v.ViewType.ToString().Equals(tf,StringComparison.OrdinalIgnoreCase))).OrderBy(v=>v.ViewType.ToString()).ToList();
            var sb = new StringBuilder($"Views ({views.Count}):\n");
            foreach (var v in views) sb.AppendLine($"  [{v.Id.Value}] {v.ViewType,-12} {v.Name}");
            return sb.ToString().TrimEnd();
        }

        private static string GetSheets(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            string f = args?["filter"]?.ToString() ?? "";
            using var colSh = new FilteredElementCollector(doc);
            var sheets = colSh.OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => string.IsNullOrEmpty(f)||s.SheetNumber.Contains(f,StringComparison.OrdinalIgnoreCase)||s.Name.Contains(f,StringComparison.OrdinalIgnoreCase)).OrderBy(s=>s.SheetNumber).ToList();
            var sb = new StringBuilder($"Sheets ({sheets.Count}):\n");
            foreach (var s in sheets) sb.AppendLine($"  [{s.SheetNumber}] {s.Name}");
            return sb.ToString().TrimEnd();
        }

        private static string GetSchedules(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            string sn = args?["scheduleName"]?.ToString() ?? ""; int mr = int.TryParse(args?["maxRows"]?.ToString(),out int r)?r:200;
            using var colSch = new FilteredElementCollector(doc);
            var schedules = colSch.OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Where(s=>!s.IsTemplate&&!s.IsInternalKeynoteSchedule).ToList();
            if (string.IsNullOrEmpty(sn)) { var sb2=new StringBuilder($"Schedules ({schedules.Count}):\n"); foreach (var s in schedules) sb2.AppendLine($"  {s.Name}"); return sb2.ToString().TrimEnd(); }
            var target = schedules.FirstOrDefault(s=>s.Name.Contains(sn,StringComparison.OrdinalIgnoreCase));
            if (target==null) return $"Schedule '{sn}' not found.";
            var td=target.GetTableData(); var sec=td.GetSectionData(SectionType.Body);
            int rows=Math.Min(sec.NumberOfRows,mr); int cols=sec.NumberOfColumns;
            var sb=new StringBuilder($"Schedule: {target.Name} ({rows}×{cols})\n");
            var hd=td.GetSectionData(SectionType.Header);
            sb.AppendLine(string.Join("|",Enumerable.Range(0,hd.NumberOfColumns).Select(c=>target.GetCellText(SectionType.Header,0,c))));
            for (int rr=0;rr<rows;rr++) sb.AppendLine(string.Join("|",Enumerable.Range(0,cols).Select(c=>target.GetCellText(SectionType.Body,rr,c))));
            return sb.ToString().TrimEnd();
        }

        private static string GetRooms(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            string lf=args?["levelFilter"]?.ToString()??""; string rf=args?["roomFilter"]?.ToString()??"";
            using var colRm = new FilteredElementCollector(doc);
            var rooms=colRm.OfCategory(BuiltInCategory.OST_Rooms).Cast<Room>().Where(r=>r.Location!=null).Where(r=>string.IsNullOrEmpty(lf)||r.Level?.Name.Contains(lf,StringComparison.OrdinalIgnoreCase)==true).Where(r=>string.IsNullOrEmpty(rf)||r.Name.Contains(rf,StringComparison.OrdinalIgnoreCase)||r.Number.Contains(rf,StringComparison.OrdinalIgnoreCase)).OrderBy(r=>r.Level?.Name).ThenBy(r=>r.Number).ToList();
            if (rooms.Count==0) return "No rooms match the filter.";
            double total=0; var sb=new StringBuilder($"Rooms ({rooms.Count}):\n");
            foreach (var r in rooms) { double a=UnitUtils.ConvertFromInternalUnits(r.Area,UnitTypeId.SquareMeters); total+=a; sb.AppendLine($"  [{r.Number}] {r.Name,-18} {a,7:F2}m²  {r.Level?.Name}"); }
            sb.AppendLine($"\n  Total: {total:F2}m²");
            return sb.ToString().TrimEnd();
        }

        private static string GetFamilies(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            string cf = args?["categoryFilter"]?.ToString() ?? "";
            using var colFam = new FilteredElementCollector(doc);
            var fams = colFam.OfClass(typeof(Family)).Cast<Family>().Where(f=>string.IsNullOrEmpty(cf)||f.FamilyCategory?.Name.Contains(cf,StringComparison.OrdinalIgnoreCase)==true).OrderBy(f=>f.Name).ToList();
            var sb = new StringBuilder($"Families ({fams.Count}):\n");
            foreach (var f in fams) sb.AppendLine($"  {f.Name}  [{f.FamilyCategory?.Name}]  타입:{f.GetFamilySymbolIds().Count}");
            return sb.ToString().TrimEnd();
        }

        private static string GetFamilyTypes(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            string fn = args?["familyName"]?.ToString() ?? "";
            using var colFamT = new FilteredElementCollector(doc);
            var fam = colFamT.OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f=>f.Name.Contains(fn,StringComparison.OrdinalIgnoreCase));
            if (fam == null) return $"Family '{fn}' not found.";
            var types = fam.GetFamilySymbolIds().Select(id=>doc.GetElement(id) as FamilySymbol).Where(fs=>fs!=null).OrderBy(fs=>fs!.Name).ToList();
            var sb = new StringBuilder($"'{fam.Name}' types ({types.Count}):\n");
            foreach (var fs in types) sb.AppendLine($"  {fs!.Name} [{fs.Id.Value}]");
            return sb.ToString().TrimEnd();
        }

        private static string GetElementGeometry(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            if (!long.TryParse(args?["elementId"]?.ToString(), out long eid)) return "Please provide a valid elementId.";
            var el = doc.GetElement(new ElementId(eid)); if (el==null) return $"ID {eid} 없음";
            var sb = new StringBuilder($"Geometry [{eid}]:\n");
            var bb = el.get_BoundingBox(null);
            if (bb!=null) sb.AppendLine($"  BB: ({bb.Min.X:F2},{bb.Min.Y:F2},{bb.Min.Z:F2}) ~ ({bb.Max.X:F2},{bb.Max.Y:F2},{bb.Max.Z:F2})");
            if (el.Location is LocationPoint lp) sb.AppendLine($"  Location: ({lp.Point.X:F2},{lp.Point.Y:F2},{lp.Point.Z:F2})");
            if (el.Location is LocationCurve lc) sb.AppendLine($"  Length: {lc.Curve.Length:F3}");
            return sb.ToString().TrimEnd();
        }

        private static string GetSharedParameters(Document? doc)
        {
            if (doc == null) return "No document open";
            using var colSp = new FilteredElementCollector(doc);
            var sps = colSp.OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>().OrderBy(s=>s.Name).ToList();
            if (sps.Count==0) return "No shared parameters.";
            var sb = new StringBuilder($"Shared parameters ({sps.Count}):\n");
            foreach (var sp in sps) sb.AppendLine($"  {sp.Name}  {sp.GuidValue}");
            return sb.ToString().TrimEnd();
        }

        // ── 팔레트 참조 ──────────────────────────────────────────────
        internal static void SetPalette(ScriptPaletteContent p) => _palette = p;
        internal static (ScriptStorageService storage, string host, Action<ScriptMeta> exec) GetPaletteArgs() =>
            (_storage, HOST, meta =>
            {
                if (_uiApp == null) return;
                string raw  = File.ReadAllText(meta.ScriptPath);
                string code = ScriptStorageService.ExtractUserCode(raw);
                var doc     = _uiApp.ActiveUIDocument?.Document;
                if (doc == null) { MessageBox.Show("No document is currently open."); return; }
                var vars = new Dictionary<string, object?> { ["doc"]=doc, ["uidoc"]=new UIDocument(doc), ["app"]=doc.Application };
                bool needPreamble = !code.Contains("clr.AddReference");
                var result = ScriptExecutor.Execute(code, vars, preamble: needPreamble ? RevitPreamble : null);
                MessageBox.Show(ScriptExecutor.FormatResult(result), $"[{meta.Name}] 결과",
                    MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            });
    }

    // ── Revit 팔레트 공급자 ──────────────────────────────────────────
    public class RevitPaletteProvider : IDockablePaneProvider
    {
        public void SetupDockablePane(DockablePaneProviderData data)
        {
            var (storage, host, exec) = RevitApp.GetPaletteArgs();
            var content = new ScriptPaletteContent(storage, host, exec, HostRegistry.Current);
            RevitApp.SetPalette(content);
            data.FrameworkElement = content;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right, MinimumWidth = 300 };
        }
    }

    // ── 팔레트 열기 커맨드 ───────────────────────────────────────────
    [Transaction(TransactionMode.Manual)]
    public class OpenPaletteCmd : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string msg, ElementSet els)
        {
            data.Application.GetDockablePane(RevitApp.PaneId).Show();
            return Result.Succeeded;
        }
    }
}
