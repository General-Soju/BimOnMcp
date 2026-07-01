using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BimOnMcpBridge
{
    /// <summary>
    /// Claude Desktop이 직접 실행하는 MCP Bridge.
    /// 명령행 인자 --target [revit|navisworks|autocad] 로 연결 대상을 지정합니다.
    /// 기본값: revit
    ///
    /// claude_desktop_config.json 예시:
    /// {
    ///   "mcpServers": {
    ///     "BimOn-Revit":      { "command": "BimOnMcpBridge.exe", "args": ["--target","revit"]      },
    ///     "BimOn-Navisworks": { "command": "BimOnMcpBridge.exe", "args": ["--target","navisworks"] },
    ///     "BimOn-AutoCAD":    { "command": "BimOnMcpBridge.exe", "args": ["--target","autocad"]    }
    ///   }
    /// }
    /// </summary>
    class Program
    {
        private const int PIPE_TIMEOUT_MS    = 28_000;   // Must be < REVIT_TIMEOUT_MS
        private const int REVIT_TIMEOUT_MS   = 30_000;

        private static string _target = "revit";
        private static string PipeName => _target.ToLower() switch
        {
            "navisworks" => "BimOnNavisPipe",
            "autocad"    => "BimOnAcadPipe",
            _            => "BimOnRevitPipe",
        };

        // ── 활성 인스턴스 해석 (다중 인스턴스 지원) ─────────────────────
        // %LOCALAPPDATA%\BimOnAI\hosts\<product>\active.json 이 가리키는,
        // 실제로 살아있는 인스턴스의 고유 파이프명을 반환한다.
        private static string? ResolveActivePipe(string product, out int liveCount)
        {
            liveCount = 0;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BimOnAI", "hosts", product);
                if (!Directory.Exists(dir)) return null;

                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    if (Path.GetFileName(f).Equals("active.json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var j = TryReadJson(f);
                    if (j != null && IsHostAlive(j)) liveCount++;
                }

                var act = TryReadJson(Path.Combine(dir, "active.json"));
                if (act != null && IsHostAlive(act))
                    return act["pipe"]?.ToString();
            }
            catch { }
            return null;
        }

        private static bool IsHostAlive(JObject j)
        {
            int pid = (int?)j["pid"] ?? 0;
            if (pid <= 0) return false;
            string pname = j["pname"]?.ToString() ?? "";
            try
            {
                var p = Process.GetProcessById(pid);
                if (p == null || p.HasExited) return false;
                return string.IsNullOrEmpty(pname)
                    || string.Equals(p.ProcessName, pname, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }   // 해당 PID 프로세스 없음
        }

        private static JObject? TryReadJson(string path)
        {
            try { return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null; }
            catch { return null; }
        }

        static async Task Main(string[] args)
        {
            // --target 파싱
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--target") _target = args[i + 1].ToLower();

            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding  = Encoding.UTF8;

            {
                Log($"BimOnMcpBridge starting — target: {_target} / pipe: {PipeName}");
            }

            while (true)
            {
                try
                {
                    string? line = Console.ReadLine();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    JObject request;
                    try { request = JObject.Parse(line); }
                    catch { SendError(null, -32700, "Parse error"); continue; }

                    var method = request["method"]?.ToString();
                    var id     = request["id"];

                    switch (method)
                    {
                        case "initialize":
                            Send(new {
                                jsonrpc = "2.0", id,
                                result  = new {
                                    protocolVersion = "2024-11-05",
                                    serverInfo      = new { name = $"BimOn-{_target}", version = "3.0.0" },
                                    capabilities    = new { tools = new { } }
                                }
                            });
                            break;

                        case "notifications/initialized":
                            break;

                        case "tools/list":
                            Send(new {
                                jsonrpc = "2.0", id,
                                result  = new { tools = ToolDefinitions.Get(_target) }
                            });
                            break;

                        case "tools/call":
                            if (id != null) await HandleToolCall(id, request);
                            break;

                        default:
                            if (id != null) SendError(id, -32601, $"Method not found: {method}");
                            break;
                    }
                }
                catch (Exception ex) { Log($"Processing error: {ex.Message}"); }
            }
        }

        private static async Task HandleToolCall(JToken id, JObject request)
        {

            // 다중 인스턴스: active.json 이 가리키는 인스턴스의 고유 파이프로만 연결
            string? pipeName = ResolveActivePipe(_target, out int liveCount);
            if (pipeName == null)
            {
                SendToolError(id, liveCount > 0
                    ? $"No active {_target} instance is connected. In {_target}, open the 'BimOn AI Scripts' palette and click '연결' (Connect)."
                    : $"{_target} is not running or not reachable. Please check if it is open.");
                return;
            }

            using var pipe = new NamedPipeClientStream(
                "localhost", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(PIPE_TIMEOUT_MS);
            }
            catch (TimeoutException)
            {
                SendToolError(id, $"The connected {_target} instance did not respond (it may have just closed). Open the instance you want and click '연결' (Connect).");
                return;
            }

            var utf8 = new UTF8Encoding(false);
            using var writer = new StreamWriter(pipe, utf8, 1024, leaveOpen: true) { AutoFlush = false };
            using var reader = new StreamReader(pipe, utf8, false, 1024, leaveOpen: true);

            await writer.WriteLineAsync(request.ToString(Formatting.None));
            await writer.FlushAsync();

            using var cts = new CancellationTokenSource(REVIT_TIMEOUT_MS);
            string? result;
            try { result = await reader.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                SendToolError(id, "Response timeout (exceeded 30s)");
                return;
            }

            if (result == null) { SendToolError(id, "No response"); return; }

            string text = result;
            try
            {
                var parsed = JObject.Parse(result);
                text = parsed["result"]?.ToString() ?? result;
            }
            catch { }

            Send(new {
                jsonrpc = "2.0", id,
                result  = new { content = new[] { new { type = "text", text } } }
            });
        }


        private static void Send(object obj)
        {
            string json = JsonConvert.SerializeObject(obj);
            Console.WriteLine(json);
        }
        private static void SendError(JToken? id, int code, string msg) =>
            Send(new { jsonrpc = "2.0", id, error = new { code, message = msg } });
        private static void SendToolError(JToken id, string msg) =>
            Send(new {
                jsonrpc = "2.0", id,
                result  = new { content = new[] { new { type = "text", text = $"[오류] {msg}" } }, isError = true }
            });
        internal static void Log(string msg) =>
            Console.Error.WriteLine($"[Bridge {DateTime.Now:HH:mm:ss}] {msg}");
    }

    // ── 도구 정의 ─────────────────────────────────────────────────────────
    internal static class ToolDefinitions
    {
        public static object[] Get(string target) => target.ToLower() switch
        {
            "navisworks" => Navisworks(),
            "autocad"    => AutoCAD(),
            _            => Revit(),
        };

        // ── 공통 스크립트 도구 (3개 모두 동일) ──────────────────────────
        private static object[] CommonScriptTools(string host) => new object[]
        {
            ToolP("execute_script",
                $"Executes IronPython code in the {host} API context. " +
                "Use result = '...' at the end to return a value. " +
                "Read-only queries run without a transaction. " +
                "Create/modify operations are automatically wrapped in a transaction " +
                "unless your code already contains 'Transaction(' — in that case manage it yourself.",
                new[] { ("code","string","IronPython code to execute"), ("timeoutSeconds","integer","Timeout seconds (default 25)") },
                new[] { "code" }),

            Tool("list_scripts",
                "Returns the full list of saved scripts. Call this tool first when the user requests a script."),

            ToolP("list_scripts_search",
                "Searches saved scripts by keyword.",
                new[] { ("keyword","string","Search keyword") }, new[] { "keyword" }),

            ToolP("save_script",
                "Saves AI-generated code to the script repository. Appears in the palette immediately.",
                new[]
                {
                    ("name",            "string", "Script name"),
                    ("description",     "string", "Script description"),
                    ("code",            "string", "IronPython code to save"),
                    ("tags",            "string", "Comma-separated tags"),
                    ("panel",           "string", "Panel name (default: General)"),
                    ("executeAfterSave","string", "Whether to execute after saving (true/false)"),
                },
                new[] { "name", "description", "code" }),

            ToolP("execute_saved_script",
                "Finds and runs a saved script by name. Use {{varName}} for parameter substitution.",
                new[]
                {
                    ("scriptName",  "string", "Name of the script to execute"),
                    ("parameters",  "string", "Parameter substitution JSON (optional)"),
                    ("overrideCode","string", "Override code to run instead of saved code (optional)"),
                },
                new[] { "scriptName" }),

            ToolP("delete_script",
                "Deletes a saved script by name (removes metadata entry and script folder). " +
                "Cannot be undone. The palette refreshes immediately.",
                new[] { ("scriptName","string","Name of the script to delete") },
                new[] { "scriptName" }),
        };

        // ── Revit 도구 ───────────────────────────────────────────────────
        private static object[] Revit()
        {
            var list = new List<object>
            {
                Tool("get_document_title",   "Returns the title of the currently open Revit document."),
                Tool("get_document_info",    "Returns document path, version, worksharing status, and other details."),
                Tool("get_element_count",    "Returns total or category-specific element count."),
                Tool("get_levels",           "Returns all level names and elevations."),
                Tool("get_project_info",     "Returns project number, name, client name, and other project info."),
                Tool("get_warnings",         "Returns the list of model warnings."),
                Tool("get_linked_documents", "Returns the list of linked files."),
                Tool("get_selected_elements","Returns information about currently selected elements."),
                Tool("get_worksets",         "Returns the list of worksets."),
                Tool("get_units",            "Returns unit settings."),
                ToolP("get_element_by_id",
                    "Retrieves detailed information about an element by its ID.",
                    new[] { ("elementId","integer","Element ID to query") }, new[] { "elementId" }),
                ToolP("get_elements_by_category",
                    "Retrieves elements by BuiltInCategory ID.",
                    new[] { ("categoryId","integer","BuiltInCategory ID"), ("maxCount","integer","Maximum count") },
                    new[] { "categoryId" }),
                ToolP("get_elements_by_filter",
                    "Filters elements by parameter value.",
                    new[] { ("categoryId","integer","Category ID (0=all)"), ("parameterName","string","Parameter name"), ("parameterValue","string","Parameter value"), ("maxCount","integer","Maximum count") },
                    new[] { "parameterName", "parameterValue" }),
                ToolP("get_element_parameters",
                    "Returns the parameter list for an element by ID.",
                    new[] { ("elementId","integer","Element ID"), ("filterName","string","Filter keyword") }, new[] { "elementId" }),
                ToolP("set_element_parameter",
                    "Changes a parameter value on the currently selected element in Revit. " +
                    "IMPORTANT: The user must select the target element in Revit before calling this tool.",
                    new[] { ("parameterName","string","Parameter name"), ("value","string","New value") },
                    new[] { "parameterName", "value" }),
                ToolP("get_views",
                    "Returns the list of views.",
                    new[] { ("viewType","string","View type filter (empty = all)") }, Array.Empty<string>()),
                ToolP("get_sheets",
                    "Returns the list of sheets.",
                    new[] { ("filter","string","Number/name filter") }, Array.Empty<string>()),
                ToolP("get_schedules",
                    "Returns schedule data.",
                    new[] { ("scheduleName","string","Schedule name"), ("maxRows","integer","Maximum row count") }, Array.Empty<string>()),
                ToolP("get_rooms",
                    "Returns room list and areas.",
                    new[] { ("levelFilter","string","Level filter"), ("roomFilter","string","Room name filter") }, Array.Empty<string>()),
                ToolP("get_families",
                    "Returns the list of families.",
                    new[] { ("categoryFilter","string","Category filter") }, Array.Empty<string>()),
                ToolP("get_family_types",
                    "Returns family types for a given family.",
                    new[] { ("familyName","string","Family name") }, new[] { "familyName" }),
                ToolP("get_element_geometry",
                    "Returns geometry information for an element.",
                    new[] { ("elementId","integer","Element ID") }, new[] { "elementId" }),
                ToolP("get_shared_parameters",
                    "Returns the list of shared parameters.",
                    Array.Empty<(string,string,string)>(), Array.Empty<string>()),

                // ── 실행 중 Dynamo 연동 (Dynamo for Revit, 리플렉션) ──
                Tool("dynamo_status",
                    "[Dynamo] Reports whether Dynamo is running inside Revit and details of the open graph " +
                    "(Dynamo version, workspace name, run type, node count, file)."),
                Tool("dynamo_get_graph",
                    "[Dynamo] Lists the nodes of the currently open Dynamo graph: each node's name, type, " +
                    "input/output flags, and evaluation state."),
                ToolP("dynamo_get_node_values",
                    "[Dynamo] Returns the evaluated output values of graph nodes (collections are shown as " +
                    "count + first element). Optional 'filter' matches node names (substring).",
                    new[] { ("filter","string","Node-name substring filter (optional)") },
                    Array.Empty<string>()),
                ToolP("dynamo_set_input",
                    "[Dynamo] Sets an input node's value by node name (slider Value / number / string / " +
                    "CodeBlock Code / dropdown SelectedString·SelectedIndex). Then call dynamo_run_current " +
                    "or rely on Automatic mode, and re-read with dynamo_get_node_values.",
                    new[] { ("nodeName","string","Target node name"), ("value","string","New value (string; auto-converted)") },
                    new[] { "nodeName", "value" }),
                Tool("dynamo_run_current",
                    "[Dynamo] Triggers evaluation of the open graph (like clicking Run). Asynchronous — " +
                    "then call dynamo_get_node_values to read the refreshed results."),

                // ── Dynamo 그래프 편집 (노드 추가/연결/삭제) ──
                // 노드 타입 선택 원칙(최적 그래프는 셋을 혼합):
                //   • dynamo_add_node (OOTB)     : 표준 작업에 해당 노드가 있을 때 — 가독성·버전안정·리뷰 용이 (1순위)
                //   • dynamo_add_code_block       : 짧은 수식/리스트/글루, 제로터치 몇 개를 한 노드로 압축
                //   • dynamo_add_python_node      : 복잡 로직(루프·조건·딕셔너리), Revit API 직접 호출, OOTB로는 노드가 너무 많아질 때
                ToolP("dynamo_add_node",
                    "[Dynamo] Adds an OOTB library node by creation name (node name or function signature, " +
                    "e.g. 'Revit.Elements.Element.GetParameterValueByName@string'). PREFER this for standard, " +
                    "readable operations that have a built-in node. Verify with dynamo_get_graph.",
                    new[] { ("typeName","string","Node creation name / function signature"), ("x","string","Canvas X (optional)"), ("y","string","Canvas Y (optional)") },
                    new[] { "typeName" }),
                ToolP("dynamo_add_code_block",
                    "[Dynamo] Adds a Code Block (DesignScript) node. Undefined identifiers become input ports " +
                    "(e.g. 'x*2;' creates input 'x'). Use for COMPACT math/list/glue or chaining a few zero-touch " +
                    "calls in one node (e.g. 'walls.ElementType.GetParameterValueByName(\"폭\");').",
                    new[] { ("code","string","DesignScript code (e.g. 'x*2;')"), ("x","string","Canvas X (optional)"), ("y","string","Canvas Y (optional)") },
                    new[] { "code" }),
                ToolP("dynamo_add_python_node",
                    "[Dynamo] Adds a Python Script node. Inputs are IN[0], IN[1]…; assign the result to OUT. " +
                    "Use for COMPLEX logic (loops, conditionals, dicts), direct Revit API access " +
                    "(e.g. 'UnwrapElement(IN[0]).Width'), external libraries, or when OOTB/CodeBlock would need " +
                    "many nodes. engine: omit to inherit the version-appropriate default (CPython3 on Dynamo 3.x, " +
                    "PythonNet3 on 4.x); or CPython3 / IronPython2 / PythonNet3 (auto-falls back if unavailable).",
                    new[] { ("code","string","Python code; result to OUT, inputs via IN[i]"), ("engine","string","omit=version default | CPython3 | IronPython2 | PythonNet3"), ("inputs","string","Input port count (default 1)"), ("x","string","Canvas X (optional)"), ("y","string","Canvas Y (optional)") },
                    new[] { "code" }),
                ToolP("dynamo_connect",
                    "[Dynamo] Wires one node's output port to another node's input port (by node name). " +
                    "Ports are zero-based indices.",
                    new[] { ("fromNode","string","Source node name"), ("fromPort","string","Source output port index (default 0)"), ("toNode","string","Target node name"), ("toPort","string","Target input port index (default 0)") },
                    new[] { "fromNode", "toNode" }),
                ToolP("dynamo_delete_node",
                    "[Dynamo] Deletes a node from the open graph by name.",
                    new[] { ("nodeName","string","Node name to delete") },
                    new[] { "nodeName" }),
                ToolP("dynamo_build_graph",
                    "[Dynamo] FAST batch build — creates many nodes AND connections in ONE call with auto-eval " +
                    "suspended during construction (single re-evaluation at the end). Strongly PREFER this over " +
                    "repeated add/connect calls when building or extending a graph (far fewer round-trips + re-runs). " +
                    "spec = JSON: { \"nodes\":[{\"id\":\"a\",\"node|codeblock|python|string\":\"...\",\"engine?\":\"CPython3\",\"inputs?\":1,\"x?\":0,\"y?\":0}], " +
                    "\"connect\":[{\"from\":\"a-or-existingName\",\"fromPort?\":0,\"to\":\"b\",\"toPort?\":0}], \"run?\":true }. " +
                    "Each node uses ONE of: node(OOTB name), codeblock(DesignScript), python(code), string(value). " +
                    "'from'/'to' reference a spec id OR an existing node's name.",
                    new[] { ("spec","string","Graph spec JSON (nodes + connect + run)") },
                    new[] { "spec" }),
                ToolP("dynamo_search_nodes",
                    "[Dynamo] Searches the loaded Dynamo node library for OOTB nodes whose creation name matches a " +
                    "keyword, returning EXACT creation names (e.g. 'Revit.Elements.Element.GetParameterValueByName@string') " +
                    "to feed dynamo_add_node / dynamo_build_graph. Use this instead of guessing an OOTB node name. " +
                    "e.g. query='GetParameterValue', 'List.Filter', 'Wall.', 'BoundingBox'.",
                    new[] { ("query","string","Keyword matched against node creation names"), ("limit","string","Max results (default 25)") },
                    new[] { "query" }),
            };
            list.AddRange(CommonScriptTools("Revit"));
            return list.ToArray();
        }

        // ── Navisworks 도구 ──────────────────────────────────────────────
        private static object[] Navisworks()
        {
            var list = new List<object>
            {
                Tool("get_document_title",    "Returns the title of the currently open Navisworks document."),
                Tool("get_document_info",     "Returns file path, version, model count, and other details."),
                Tool("get_model_count",       "Returns the number of loaded models."),
                Tool("get_clash_tests",       "Returns the list of Clash Detective tests."),
                ToolP("get_clash_results",
                    "Returns clash items for a specific clash test.",
                    new[] { ("testName","string","Clash test name"), ("statusFilter","string","Status filter (All/New/Active/Reviewed/Approved/Resolved)"), ("maxCount","integer","Maximum count") },
                    new[] { "testName" }),
                ToolP("approve_clash",
                    "Approves, resolves, or reviews a clash item.",
                    new[] { ("testName","string","Test name"), ("clashName","string","Clash item name"), ("status","string","Status to set (Approved/Resolved/Reviewed)") },
                    new[] { "testName", "clashName", "status" }),
                Tool("get_selection_sets",    "Returns the list of selection sets."),
                Tool("get_viewpoints",        "Returns the list of saved viewpoints."),
                Tool("get_timeliner_tasks",   "Returns the list of TimeLiner tasks."),
                ToolP("get_model_items",
                    "Queries model items.",
                    new[] { ("searchTerm","string","Search term (name/property)"), ("maxCount","integer","Maximum count") },
                    new[] { "searchTerm" }),
                ToolP("get_item_properties",
                    "Returns properties of the selected item.",
                    new[] { ("itemPath","string","Item path") }, new[] { "itemPath" }),
            };
            list.AddRange(CommonScriptTools("Navisworks"));
            return list.ToArray();
        }

        // ── AutoCAD 도구 ─────────────────────────────────────────────────
        private static object[] AutoCAD()
        {
            var list = new List<object>
            {
                Tool("get_drawing_info",      "Returns the filename, path, and save status of the current drawing."),
                Tool("get_layer_list",        "Returns the layer list with state (on/locked/color)."),
                Tool("get_entity_count",      "Returns total or per-layer entity count in the current drawing."),
                Tool("get_block_list",        "Returns the list of block definitions."),
                Tool("get_lisp_routines",     "Returns the list of AutoLISP routines loaded in the drawing."),
                Tool("get_selected_entities", "Returns information about currently selected entities."),
                Tool("get_text_styles",       "Returns the list of text styles."),
                Tool("get_dim_styles",        "Returns the list of dimension styles."),
                Tool("get_layouts",           "Returns the list of layouts (paper space tabs)."),
                ToolP("get_entities_by_layer",
                    "Returns entities on a specific layer.",
                    new[] { ("layerName","string","Layer name"), ("maxCount","integer","Maximum count") },
                    new[] { "layerName" }),
                ToolP("get_entity_by_handle",
                    "Retrieves detailed information about an entity by its Handle.",
                    new[] { ("handle","string","Entity Handle value") }, new[] { "handle" }),
                ToolP("set_entity_property",
                    "Changes entity properties (layer, color, linetype).",
                    new[] { ("handle","string","Handle"), ("property","string","Property name (Layer/Color/Linetype)"), ("value","string","New value") },
                    new[] { "handle", "property", "value" }),
                ToolP("get_block_attributes",
                    "Returns Attribute values of block references.",
                    new[] { ("blockName","string","Block name (partial match)"), ("maxCount","integer","Maximum count") },
                    new[] { "blockName" }),
            };
            list.AddRange(CommonScriptTools("AutoCAD"));
            return list.ToArray();
        }


        // ── 헬퍼 ────────────────────────────────────────────────────────
        private static object Tool(string name, string desc) => new
        {
            name, description = desc,
            inputSchema = new { type = "object", properties = new { }, required = Array.Empty<string>() }
        };

        private static object ToolP(
            string name, string desc,
            (string name, string type, string desc)[] parms,
            string[] required)
        {
            var props = new System.Collections.Generic.Dictionary<string, object>();
            foreach (var p in parms)
                props[p.name] = new { type = p.type, description = p.desc };
            return new { name, description = desc,
                inputSchema = new { type = "object", properties = props, required } };
        }
    }

}
