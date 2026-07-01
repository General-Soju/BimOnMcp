using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Autodesk.Navisworks.Api.Plugins;
using NavisApplication = Autodesk.Navisworks.Api.Application;
using BimOnMcpShared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BimOnNavisPlugin
{
    [Plugin("BimOnNavisPlugin", "BIMON",
        DisplayName = "BimOn MCP",
        ToolTip = "BimOn MCP Suite — Navisworks Plugin")]
    [AddInPlugin(AddInLocation.AddIn)]
    public class NavisApp : AddInPlugin
    {
        private const string PIPE_NAME = "BimOnNavisPipe";
        private const string HOST      = "Navisworks";

        private static ScriptStorageService      _storage    = new ScriptStorageService();
        private static CancellationTokenSource?  _cts;
        private static ScriptPaletteContent?     _palette;
        private static NavisScriptWindow?        _scriptWin;
        // 창의 STA 스레드 Dispatcher — 메인 스레드에서 안전하게 접근 가능한 유일한 방법
        private static System.Windows.Threading.Dispatcher? _scriptWinDispatcher;

        // ── DLL 로드 시 자동 시작 (Navisworks 시작과 동시에 실행) ──────────
        // 주의: 플러그인 스캐너가 어트리뷰트만 읽어도 ModuleInitializer가 실행된다.
        // 본문이 외부 어셈블리 타입을 직접 참조하면 "JIT 시점"에 FileNotFoundException이
        // 발생할 수 있고, 이는 메서드 내부 try-catch로 잡히지 않아 호스트가 통째로
        // 크래시한다(Navisworks 2027 설치폴더 Plugins 로드에서 실제 발생).
        // → 외부 타입 참조를 NoInlining 별도 메서드로 분리하고, 의존성 resolve 훅을 먼저 등록.
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void AutoStart()
        {
            try
            {
                // 플러그인 폴더에서 의존 어셈블리 resolve (mscorlib 타입만 사용 — JIT 안전)
                AppDomain.CurrentDomain.AssemblyResolve += ResolvePluginAssembly;
                AutoStartCore();
            }
            catch { /* JIT/타입로드 예외 포함 — 호스트 프로세스 보호 */ }
        }

        private static System.Reflection.Assembly? ResolvePluginAssembly(
            object? sender, ResolveEventArgs args)
        {
            try
            {
                string name = new System.Reflection.AssemblyName(args.Name).Name + ".dll";
                string dir  = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
                string path = System.IO.Path.Combine(dir, name);
                return System.IO.File.Exists(path)
                    ? System.Reflection.Assembly.LoadFrom(path) : null;
            }
            catch { return null; }
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void AutoStartCore()
        {
            _storage = new ScriptStorageService();
            _cts     = new CancellationTokenSource();
            string pipeName = HostRegistry.Init("navisworks", PIPE_NAME,
                () => { try { return NavisApplication.ActiveDocument?.Title ?? "Navisworks"; }
                        catch { return "Navisworks"; } });
            Task.Run(() => new PipeServer(pipeName, HandleRequest).RunAsync(_cts.Token));
        }

        // ── Add-ins 버튼 클릭 → 스크립트 팔레트 창 열기 ─────────────────
        // 자동 시작(ModuleInitializer)으로 서버는 이미 실행 중.
        // 버튼 역할: ① 스크립트 팔레트 창 열기  ② 서버 비정상 종료 시 재시작
        public override int Execute(string[] parameters)
        {
            bool running = _cts != null && !_cts.IsCancellationRequested;
            if (!running)
            {
                _storage = new ScriptStorageService();
                _cts     = new CancellationTokenSource();
                string pipeName = HostRegistry.Current?.PipeName ?? PIPE_NAME;
                Task.Run(() => new PipeServer(pipeName, HandleRequest).RunAsync(_cts.Token));
            }
            ShowScriptPalette();
            return 0;
        }

        // ── 스크립트 팔레트 창 ────────────────────────────────────────────
        private static void ShowScriptPalette()
        {
            // ── 이미 열려 있으면 새로고침 + 포커스 ─────────────────────────
            // WPF 창은 STA 스레드 소유 → IsLoaded 등 직접 접근 금지(크로스스레드 크래시).
            // Dispatcher 객체 자체는 임의 스레드에서 안전하게 읽을 수 있다.
            var disp = _scriptWinDispatcher;
            if (disp != null && !disp.HasShutdownStarted)
            {
                // BeginInvoke: 논블로킹 — 메인 스레드가 블록되지 않아 데드락 없음
                disp.BeginInvoke(new Action(() =>
                {
                    _scriptWin?.Refresh();
                    _scriptWin?.Activate();
                }));
                return;
            }

            // ── 새 STA 스레드에서 창 생성 ──────────────────────────────────
            var thread = new Thread(() =>
            {
                try
                {
                    var (storage, host, exec) = GetPaletteArgs();
                    _palette   = new ScriptPaletteContent(storage, host, exec, HostRegistry.Current);
                    _scriptWin = new NavisScriptWindow(_palette);

                    // Dispatcher를 먼저 저장해 두어야 메인 스레드가 안전하게 참조 가능
                    _scriptWinDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

                    _scriptWin.Closed += (s, e) =>
                    {
                        _scriptWin = null;
                        _palette   = null;
                        // null 설정 후 Shutdown → 이후 버튼 클릭 시 새 창 생성
                        var d = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                        _scriptWinDispatcher = null;
                        d.InvokeShutdown();   // Dispatcher.Run() 루프 종료
                    };

                    SetPalette(_palette);
                    _scriptWin.Show();
                    _scriptWin.Activate();
                    System.Windows.Threading.Dispatcher.Run();  // 창이 닫힐 때까지 대기
                }
                catch { /* 창 생성 실패 방어 */ }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        // ── 요청 라우팅 ───────────────────────────────────────────────
        private static async Task<string> HandleRequest(JObject req, CancellationToken ct)
        {
            var tool = req["params"]?["name"]?.ToString();
            var args = req["params"]?["arguments"] as JObject;
            var doc  = NavisApplication.ActiveDocument;

            return tool switch
            {
                "get_document_title"   => doc == null ? "No document open" : $"Document title: {doc.Title}",
                "get_document_info"    => GetDocumentInfo(doc),
                "get_model_count"      => doc == null ? "No document open" : $"Loaded models: {doc.Models.Count}",
                "get_clash_tests"      => GetClashTests(doc),
                "get_clash_results"    => GetClashResults(doc, args),
                // approve_clash: Navisworks API is tolerant for background-thread writes
                // (unlike Revit). The WPF STA thread is NOT the Navisworks main thread,
                // so dispatching to _scriptWinDispatcher would be incorrect here.
                "approve_clash"        => ApproveClash(doc, args),
                "get_selection_sets"   => GetSelectionSets(doc),
                "get_viewpoints"       => GetViewpoints(doc),
                "get_timeliner_tasks"  => GetTimelinerTasks(doc),
                "get_model_items"      => GetModelItems(doc, args),
                "get_item_properties"  => GetItemProperties(doc, args),

                "execute_script"       => HandleExecuteScript(args, doc),
                "list_scripts"         => HandleListScripts(null),
                "list_scripts_search"  => HandleListScripts(args?["keyword"]?.ToString()),
                "save_script"          => await HandleSaveScript(args, doc),
                "execute_saved_script" => HandleExecuteSavedScript(args, doc),
                "delete_script"        => HandleDeleteScript(args),

                _ => $"[Error] Unknown tool: {tool}"
            };
        }

        // ── Navisworks 도구 구현 ─────────────────────────────────────

        private static string GetDocumentInfo(Document? doc)
        {
            if (doc == null) return "No document open";
            return $"제목: {doc.Title}\n경로: {(string.IsNullOrEmpty(doc.FileName) ? "(unsaved)" : doc.FileName)}\n모델 수: {doc.Models.Count}\n단위: {doc.Units}";
        }

        private static string GetClashTests(Document? doc)
        {
            if (doc == null) return "No document open";
            dynamic clash = DocumentExtensions.GetClash(doc);
            if (clash == null) return "Clash Detective not found.";
            var sb = new StringBuilder("Clash tests:\n");
            try
            {
                dynamic testsData = clash.TestsData;
                foreach (dynamic test in testsData.Tests)
                    sb.AppendLine($"  • {test.DisplayName}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  [오류] {ex.Message}");
            }
            return sb.ToString().TrimEnd();
        }

        private static string GetClashResults(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            string testName    = args?["testName"]?.ToString() ?? "";
            string statusFilter= args?["statusFilter"]?.ToString() ?? "All";
            int max            = int.TryParse(args?["maxCount"]?.ToString(), out int mc) ? mc : 100;

            dynamic clash = DocumentExtensions.GetClash(doc);
            if (clash == null) return "Clash Detective not found.";
            var sb = new StringBuilder($"[{testName}] Clash items:\n");
            try
            {
                dynamic testsData = clash.TestsData;
                dynamic? targetTest = null;
                foreach (dynamic test in testsData.Tests)
                {
                    if (((string)test.DisplayName).Equals(testName, StringComparison.OrdinalIgnoreCase))
                    { targetTest = test; break; }
                }
                if (targetTest == null) return $"'{testName}' 테스트를 찾을 수 없습니다.";

                int count = 0;
                foreach (dynamic item in targetTest.Children)
                {
                    if (count >= max) break;
                    var itemStatus = (string)item.Status.ToString();
                    if (!statusFilter.Equals("All", StringComparison.OrdinalIgnoreCase)
                        && !itemStatus.Equals(statusFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    sb.AppendLine($"  • {item.DisplayName}  상태:{itemStatus}  거리:{item.Distance:F3}");
                    count++;
                }
                if (count == 0) sb.AppendLine("  조건에 맞는 item not found");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  [오류] {ex.Message}");
            }
            return sb.ToString().TrimEnd();
        }

        private static string ApproveClash(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            string testName  = args?["testName"]?.ToString() ?? "";
            string clashName = args?["clashName"]?.ToString() ?? "";
            string statusStr = args?["status"]?.ToString() ?? "Approved";

            dynamic clash = DocumentExtensions.GetClash(doc);
            if (clash == null) return "Clash Detective not found.";
            if (!Enum.TryParse<ClashResultStatus>(statusStr, true, out var newStatus))
                return $"Invalid status: {statusStr}";

            try
            {
                dynamic testsData = clash.TestsData;
                foreach (dynamic test in testsData.Tests)
                {
                    if (!((string)test.DisplayName).Equals(testName, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (IClashResult result in test.Children)
                    {
                        if (result.DisplayName.Equals(clashName, StringComparison.OrdinalIgnoreCase))
                        {
                            testsData.TestsEditResultStatus(result, newStatus);
                            return $"'{clashName}' → {newStatus} processed";
                        }
                    }
                }
                return $"'{clashName}' item not found";
            }
            catch (Exception ex)
            {
                return $"[Error] {ex.Message}";
            }
        }

        private static string GetSelectionSets(Document? doc)
        {
            if (doc == null) return "No document open";
            var sb = new StringBuilder("Selection sets:\n");
            foreach (var set in doc.SelectionSets.RootItem.Children)
                sb.AppendLine($"  • {set.DisplayName}");
            return sb.ToString().TrimEnd();
        }

        private static string GetViewpoints(Document? doc)
        {
            if (doc == null) return "No document open";
            var sb = new StringBuilder("Saved viewpoints:\n");
            foreach (var vp in doc.SavedViewpoints.Value)
                sb.AppendLine($"  • {vp.DisplayName}");
            return sb.ToString().TrimEnd();
        }

        private static string GetTimelinerTasks(Document? doc)
        {
            if (doc == null) return "No document open";
            if (doc.Timeliner == null) return "TimeLiner not available.";
            var sb = new StringBuilder("TimeLiner tasks:\n");
            try
            {
                dynamic tl = doc.Timeliner;
                dynamic tasks = tl.TasksData;
                foreach (dynamic task in tasks)
                    sb.AppendLine($"  • {task.DisplayName}  시작:{task.PlannedStartDate:yyyy-MM-dd}  종료:{task.PlannedEndDate:yyyy-MM-dd}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  [오류] {ex.Message}");
            }
            return sb.ToString().TrimEnd();
        }

        private static string GetModelItems(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            string search = args?["searchTerm"]?.ToString() ?? "";
            int max       = int.TryParse(args?["maxCount"]?.ToString(), out int mc) ? mc : 50;

            var matched  = new System.Collections.Generic.List<ModelItem>();
            var fallback = new System.Collections.Generic.List<ModelItem>(); // 검색 0개일 때 샘플 표시
            int total    = 0;

            try
            {
                foreach (var model in doc.Models)
                {
                    CollectItems(model.RootItem, search, max, matched, fallback, ref total);
                    if (matched.Count >= max) break;
                }
            }
            catch (Exception ex)
            {
                return $"[Error] {ex.Message}";
            }

            var sb = new StringBuilder();

            if (matched.Count > 0)
            {
                sb.AppendLine($"Search '{search}' — {matched.Count}개 (of {total} total 중):");
                foreach (var item in matched)
                    sb.AppendLine($"  • {item.DisplayName}  [{item.ClassDisplayName}]");
            }
            else if (fallback.Count > 0)
            {
                sb.AppendLine($"'{search}' No match. Sample items (total {total}개):");
                foreach (var item in fallback)
                    sb.AppendLine($"  • {item.DisplayName}  [{item.ClassDisplayName}]");
            }
            else
            {
                sb.AppendLine($"No items in model. (Models: {doc.Models.Count}, 순회: {total}개)");
            }

            return sb.ToString().TrimEnd();
        }

        private static void CollectItems(
            ModelItem root, string search, int max,
            System.Collections.Generic.List<ModelItem> matched,
            System.Collections.Generic.List<ModelItem> fallback,
            ref int total)
        {
            var stack = new System.Collections.Generic.Stack<ModelItem>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var item = stack.Pop();
                total++;

                // 자식이 없는 리프 노드만 수집
                bool isLeaf = !item.Children.Any();
                if (isLeaf)
                {
                    string name = item.DisplayName ?? "";
                    // 샘플 (최대 10개)
                    if (fallback.Count < 10) fallback.Add(item);

                    // 검색어 매칭 — DisplayName 우선, 속성값 보조
                    if (!string.IsNullOrWhiteSpace(search) && matched.Count < max)
                    {
                        bool hit = name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!hit)
                        {
                            foreach (var cat in item.PropertyCategories)
                            {
                                foreach (var prop in cat.Properties)
                                {
                                    try
                                    {
                                        if (prop.Value == null) continue;
                                        var v = prop.Value.IsDisplayString
                                            ? prop.Value.ToDisplayString()
                                            : prop.Value.ToString() ?? "";
                                        if (v.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                                        { hit = true; break; }
                                    }
                                    catch { /* 변환 불가 속성 스킵 */ }
                                }
                                if (hit) break;
                            }
                        }
                        if (hit) matched.Add(item);
                    }
                }

                // 자식 추가 (역순으로 push해야 순서 유지)
                var children = item.Children.ToList();
                for (int i = children.Count - 1; i >= 0; i--)
                    stack.Push(children[i]);
            }
        }

        private static System.Collections.Generic.List<ModelItem> WalkItems(
            System.Collections.Generic.IEnumerable<ModelItem> items, string search, int max)
        {
            var result = new System.Collections.Generic.List<ModelItem>();
            foreach (var item in items)
            {
                if (result.Count >= max) break;
                if (string.IsNullOrWhiteSpace(item.DisplayName)) continue;
                if (item.DisplayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                    result.Add(item);
            }
            return result;
        }

        private static string GetItemProperties(Document? doc, JObject? args)
        {
            if (doc == null) return "No document open";
            string path = args?["itemPath"]?.ToString() ?? "";
            var sel = doc.CurrentSelection.SelectedItems;
            if (sel.Count == 0) return "선택된 item not found";
            var item = sel.First();
            var sb   = new StringBuilder($"[{item.DisplayName}] 속성:\n");
            foreach (var cat in item.PropertyCategories)
            {
                sb.AppendLine($"\n  [{cat.DisplayName}]");
                foreach (var prop in cat.Properties)
                    sb.AppendLine($"    {prop.DisplayName}: {prop.Value?.ToDisplayString()}");
            }
            return sb.ToString().TrimEnd();
        }

        // ── 스크립트 도구 ─────────────────────────────────────────────

        // Navisworks IronPython 2.7 공통 프리앰블
        // ※ from __future__ 제거: IronPython 2.7에서 engine.Execute() 시
        //    __future__.py 파일을 찾으려 하는 이슈 존재
        // ※ 이 프리앰블은 사용자 코드와 별도 컴파일 단위로 실행됩니다 (ScriptExecutor.preamble 파라미터)
        //    → 'from X import *' 와 사용자 코드의 'from Y import Z' 충돌 방지
        private const string NavisPreamble =
            "import clr\n" +
            "clr.AddReference('Autodesk.Navisworks.Api')\n" +
            "clr.AddReference('Autodesk.Navisworks.Clash')\n" +
            "from Autodesk.Navisworks.Api import *\n" +
            "from Autodesk.Navisworks.Api.Clash import *\n" +
            "from Autodesk.Navisworks.Api.Plugins import *\n";

        private static string HandleExecuteScript(JObject? args, Document? doc)
        {
            string code = args?["code"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(code)) return "[Error] code is empty.";
            if (doc == null) return "No document open";
            // clr.AddReference가 이미 있으면 사용자가 직접 관리 — 프리앰블 없이 실행
            // 없으면 프리앰블을 별도 컴파일 단위로 먼저 실행 (IronPython 2.7 버그 우회)
            bool needPreamble = !code.Contains("clr.AddReference");
            var vars = new Dictionary<string, object?> { ["doc"] = doc, ["app"] = doc };
            return ScriptExecutor.FormatResult(
                ScriptExecutor.Execute(code, vars, preamble: needPreamble ? NavisPreamble : null));
        }

        private static string HandleListScripts(string? keyword)
        {
            var metas = string.IsNullOrWhiteSpace(keyword)
                ? _storage.GetAll() : _storage.Search(keyword);
            if (metas.Count == 0) return "No saved scripts found.";
            var sb = new StringBuilder($"Saved scripts: {metas.Count}\n\n");
            foreach (var m in metas)
            {
                sb.AppendLine($"▶ [{m.Host}] {m.Name}");
                sb.AppendLine($"   Desc: {m.Description}");
                sb.AppendLine($"   Tags: {string.Join(", ", m.Tags)}");
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private static async Task<string> HandleSaveScript(JObject? args, Document? doc)
        {
            string name        = args?["name"]?.ToString() ?? "";
            string description = args?["description"]?.ToString() ?? "";
            string userCode    = args?["code"]?.ToString() ?? "";
            string tagsRaw     = args?["tags"]?.ToString() ?? "";
            string panel       = args?["panel"]?.ToString() ?? "General";
            bool   execAfter   = (args?["executeAfterSave"]?.ToString()??"false").Equals("true",StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(name))     return "[Error] name is empty.";
            if (string.IsNullOrWhiteSpace(userCode)) return "[Error] code is empty.";

            string[] tags = tagsRaw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToArray();
            var meta = _storage.Save(HOST, name, description, userCode, tags, panel);

            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => _palette?.LoadScripts());

            var sb = new StringBuilder($"✅ [{HOST}] '{name}' saved\n   경로: {meta.ScriptPath}");
            if (execAfter)
            {
                sb.AppendLine("\n── Execute Result ──");
                sb.AppendLine(HandleExecuteScript(args, doc));
            }
            return sb.ToString().TrimEnd();
        }

        private static string HandleDeleteScript(JObject? args)
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

        private static string HandleExecuteSavedScript(JObject? args, Document? doc)
        {
            string scriptName   = args?["scriptName"]?.ToString() ?? "";
            string paramsJson   = args?["parameters"]?.ToString() ?? "";
            string overrideCode = args?["overrideCode"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(scriptName)) return "[Error] scriptName is empty.";
            var meta = _storage.FindByName(scriptName);
            if (meta == null) return $"[Error] Script '{scriptName}' not found.";
            if (!meta.Host.Equals(HOST, StringComparison.OrdinalIgnoreCase))
                return $"[Error] This script is for {meta.Host}.";
            string code = !string.IsNullOrWhiteSpace(overrideCode) ? overrideCode
                : (File.Exists(meta.ScriptPath) ? ScriptStorageService.ExtractUserCode(File.ReadAllText(meta.ScriptPath)) : "");
            if (!string.IsNullOrWhiteSpace(paramsJson))
            {
                try { var d = JsonConvert.DeserializeObject<Dictionary<string,string>>(paramsJson); if (d!=null) foreach (var kv in d) code=code.Replace($"{{{{{kv.Key}}}}}", kv.Value); } catch { }
            }
            return HandleExecuteScript(new JObject { ["code"] = code }, doc);
        }

        internal static void SetPalette(ScriptPaletteContent p) => _palette = p;
        internal static (ScriptStorageService storage, string host, Action<ScriptMeta> exec) GetPaletteArgs() =>
            (_storage, HOST, meta =>
            {
                var doc = NavisApplication.ActiveDocument;
                if (doc == null) { MessageBox.Show("No document is currently open."); return; }
                string raw  = File.ReadAllText(meta.ScriptPath);
                string code = ScriptStorageService.ExtractUserCode(raw);
                var vars    = new Dictionary<string, object?> { ["doc"] = doc, ["app"] = doc };
                // preamble 분리 실행 (IronPython 2.7 import * 버그 우회)
                bool needPreamble = !code.Contains("clr.AddReference");
                var result = ScriptExecutor.Execute(code, vars,
                    preamble: needPreamble ? NavisPreamble : null);
                MessageBox.Show(ScriptExecutor.FormatResult(result), $"[{meta.Name}] 결과",
                    MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            });
    }
}
