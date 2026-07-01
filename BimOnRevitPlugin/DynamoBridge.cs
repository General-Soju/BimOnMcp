using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;

namespace BimOnRevitPlugin
{
    /// <summary>
    /// 실행 중인 Dynamo(Revit 내부 로드)와 연동.
    /// 전부 리플렉션으로 접근 → Dynamo 버전(2.x/3.x)에 결합되지 않음.
    /// Dynamo 미로드/세션 없음 시 안내 문자열 반환(예외 던지지 않음).
    /// BimOn 메인스레드(ExternalEvent)에서 호출됨 — Revit Transaction 미개시(읽기/세팅 안전).
    /// </summary>
    internal static class DynamoBridge
    {
        // ── 살아있는 RevitDynamoModel 확보 ──────────────────────────
        private static object? GetModel(out string err)
        {
            err = "";
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? t = null;
                try { t = a.GetType("Dynamo.Applications.DynamoRevit"); } catch { }
                if (t == null) continue;
                var prop = t.GetProperty("RevitDynamoModel",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                var m = prop?.GetValue(null);
                if (m != null) return m;
                err = "Dynamo is loaded but has no active session (open the Dynamo editor first).";
                return null;
            }
            err = "Dynamo is not running in this Revit session. Open Dynamo, then retry.";
            return null;
        }

        private static object? GP(object? o, string name)
        {
            if (o == null) return null;
            try { return o.GetType().GetProperty(name)?.GetValue(o); } catch { return null; }
        }

        private static List<object> Nodes(object ws) =>
            (GP(ws, "Nodes") as IEnumerable)?.Cast<object>().ToList() ?? new List<object>();

        // ── 도구 구현 ────────────────────────────────────────────────
        public static string Status()
        {
            var m = GetModel(out var err); if (m == null) return "[Dynamo] " + err;
            var ws = GP(m, "CurrentWorkspace");
            var ver = m.GetType().Assembly.GetName().Version;
            return $"[Dynamo] running. Version {ver}\n" +
                   $"  Workspace : {GP(ws, "Name")}\n" +
                   $"  RunType   : {GP(GP(ws, "RunSettings"), "RunType")}\n" +
                   $"  Nodes     : {(ws == null ? 0 : Nodes(ws).Count)}\n" +
                   $"  File      : {GP(ws, "FileName") ?? "(unsaved)"}";
        }

        public static string GetGraph()
        {
            var m = GetModel(out var err); if (m == null) return "[Dynamo] " + err;
            var ws = GP(m, "CurrentWorkspace"); if (ws == null) return "[Dynamo] no workspace";
            var nodes = Nodes(ws);
            var sb = new StringBuilder($"[Dynamo] Workspace '{GP(ws, "Name")}' — {nodes.Count} node(s):\n");
            foreach (var n in nodes)
            {
                string flag = (((bool?)GP(n, "IsSetAsInput") ?? false) ? " [IN]" : "")
                            + (((bool?)GP(n, "IsSetAsOutput") ?? false) ? " [OUT]" : "");
                sb.AppendLine($"  - {GP(n, "Name")}  <{n.GetType().Name}>{flag}  state={GP(n, "State")}");
            }
            return sb.ToString().TrimEnd();
        }

        public static string GetNodeValues(string filter)
        {
            var m = GetModel(out var err); if (m == null) return "[Dynamo] " + err;
            var ws = GP(m, "CurrentWorkspace"); if (ws == null) return "[Dynamo] no workspace";
            var sb = new StringBuilder();
            foreach (var n in Nodes(ws))
            {
                string nm = GP(n, "Name")?.ToString() ?? "";
                if (!string.IsNullOrEmpty(filter) &&
                    nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                sb.AppendLine($"  - {nm}: {DescribeValue(GP(n, "CachedValue"))}");
            }
            return sb.Length == 0 ? "[Dynamo] no matching nodes." : sb.ToString().TrimEnd();
        }

        // MirrorData(캐시 값) → 사람이 읽는 요약. 컬렉션은 개수+첫 원소.
        private static string DescribeValue(object? cv)
        {
            if (cv == null) return "null (not evaluated)";
            bool isCol = (bool?)GP(cv, "IsCollection") ?? false;
            if (!isCol)
            {
                var s = GP(cv, "StringData") ?? GP(cv, "Data");
                return s?.ToString() ?? "null";
            }
            try
            {
                var elems = (cv.GetType().GetMethod("GetElements")?.Invoke(cv, null) as IEnumerable)
                            ?.Cast<object>().ToList() ?? new List<object>();
                var first = elems.FirstOrDefault();
                var fs = first == null ? null : (GP(first, "StringData") ?? GP(first, "Data"))?.ToString();
                return $"list[{elems.Count}]" + (fs != null ? $"  first={fs}" : "");
            }
            catch (Exception e) { return $"list (read error: {e.Message})"; }
        }

        public static string SetInput(string nodeName, string value)
        {
            if (string.IsNullOrWhiteSpace(nodeName)) return "[Dynamo] nodeName is empty.";
            var m = GetModel(out var err); if (m == null) return "[Dynamo] " + err;
            var ws = GP(m, "CurrentWorkspace"); if (ws == null) return "[Dynamo] no workspace";
            var node = Nodes(ws).FirstOrDefault(n =>
                string.Equals(GP(n, "Name")?.ToString(), nodeName, StringComparison.OrdinalIgnoreCase));
            if (node == null) return $"[Dynamo] node '{nodeName}' not found.";

            // 입력 후보 프로퍼티를 순서대로 시도(슬라이더 Value / 코드블록 Code / 드롭다운 SelectedString·SelectedIndex …)
            foreach (var pn in new[] { "Value", "Code", "Text", "Number", "SelectedString", "SelectedIndex" })
            {
                var p = node.GetType().GetProperty(pn);
                if (p == null || !p.CanWrite) continue;
                try
                {
                    object conv = Convert.ChangeType(value, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType,
                        System.Globalization.CultureInfo.InvariantCulture);
                    p.SetValue(node, conv);
                    // 변경 통지 → 재평가 유도(Automatic 모드면 자동 실행). 없는 버전이면 무시.
                    try { node.GetType().GetMethod("OnNodeModified", new[] { typeof(bool) })?.Invoke(node, new object[] { true }); } catch { }
                    return $"[Dynamo] set {nodeName}.{pn} = {value}  (re-read values after run/auto-run).";
                }
                catch (Exception e) { return $"[Dynamo] set failed on {pn}: {e.Message}"; }
            }
            return $"[Dynamo] node '{nodeName}' <{node.GetType().Name}> has no settable input property " +
                   "(not a slider/number/string/code/dropdown input).";
        }

        public static string RunCurrent()
        {
            var m = GetModel(out var err); if (m == null) return "[Dynamo] " + err;
            var tried = new List<string>();

            // 1) 표준: DynamoModel.RunCancelCommand(showErrors,cancelRun) + ExecuteCommand
            try
            {
                Type? rcc = null; var t = m.GetType();
                while (t != null && rcc == null) { rcc = t.GetNestedType("RunCancelCommand"); t = t.BaseType; }
                var exec = m.GetType().GetMethod("ExecuteCommand");
                if (rcc != null && exec != null)
                {
                    var cmd = Activator.CreateInstance(rcc, false, false);
                    exec.Invoke(m, new[] { cmd });
                    return "[Dynamo] Run issued (RunCancelCommand). Re-read node values to see results.";
                }
            }
            catch (Exception e) { tried.Add("ExecuteCommand: " + e.Message); }

            // 2) DynamoModel.ForceRun()
            try { var fr = m.GetType().GetMethod("ForceRun", Type.EmptyTypes); if (fr != null) { fr.Invoke(m, null); return "[Dynamo] ForceRun issued. Re-read node values."; } }
            catch (Exception e) { tried.Add("ForceRun: " + e.Message); }

            // 3) HomeWorkspaceModel.Run()
            try { var ws = GP(m, "CurrentWorkspace"); var r = ws?.GetType().GetMethod("Run", Type.EmptyTypes); if (r != null) { r.Invoke(ws, null); return "[Dynamo] Workspace.Run issued. Re-read node values."; } }
            catch (Exception e) { tried.Add("Run: " + e.Message); }

            return "[Dynamo] run failed: " + string.Join(" | ", tried);
        }

        // ── 그래프 편집 (phase 2): 노드 추가/연결/삭제 ──────────────────
        private static Type? FindType(string fullName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? t = null; try { t = a.GetType(fullName); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        private static Type? FindNested(object mdl, string name)
        {
            var t = mdl.GetType();
            while (t != null) { var nt = t.GetNestedType(name); if (nt != null) return nt; t = t.BaseType; }
            return null;
        }

        private static void Exec(object mdl, object cmd) =>
            mdl.GetType().GetMethod("ExecuteCommand")?.Invoke(mdl, new[] { cmd });

        private static object? FindNode(object ws, string name) =>
            Nodes(ws).FirstOrDefault(n =>
                string.Equals(GP(n, "Name")?.ToString(), name, StringComparison.OrdinalIgnoreCase));

        private static double Pd(string s) =>
            double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        private static int Pi(string s) => int.TryParse(s, out var v) ? v : 0;

        public static string AddCodeBlock(string code, string xs, string ys)
        {
            if (string.IsNullOrWhiteSpace(code)) return "[Dynamo] code is empty.";
            var mdl = GetModel(out var err); if (mdl == null) return "[Dynamo] " + err;
            var ws = GP(mdl, "CurrentWorkspace"); if (ws == null) return "[Dynamo] no workspace";
            var lib = GP(GP(mdl, "EngineController"), "LibraryServices");
            if (lib == null) return "[Dynamo] LibraryServices unavailable (EngineController not ready).";
            var er = GP(ws, "ElementResolver");
            double x = Pd(xs), y = Pd(ys);
            try
            {
                var cbType = FindType("Dynamo.Graph.Nodes.CodeBlockNodeModel");
                if (cbType == null) return "[Dynamo] CodeBlockNodeModel type not found.";
                var cb  = Activator.CreateInstance(cbType, code, x, y, lib, er);
                var cnc = FindNested(mdl, "CreateNodeCommand");
                if (cnc == null || cb == null) return "[Dynamo] CreateNodeCommand unavailable.";
                Exec(mdl, Activator.CreateInstance(cnc, cb, x, y, false, true)!);
                return $"[Dynamo] code block added at ({x},{y}). name='{GP(cb, "Name")}' GUID={GP(cb, "GUID")}";
            }
            catch (Exception e) { return $"[Dynamo] add code block failed: {e.InnerException?.Message ?? e.Message}"; }
        }

        public static string AddNode(string typeName, string xs, string ys)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return "[Dynamo] typeName is empty.";
            var mdl = GetModel(out var err); if (mdl == null) return "[Dynamo] " + err;
            double x = Pd(xs), y = Pd(ys);
            try
            {
                var cnc = FindNested(mdl, "CreateNodeCommand");
                if (cnc == null) return "[Dynamo] CreateNodeCommand unavailable.";
                // 문자열 ctor: (string nodeId, string name, double x, double y, bool defaultPos, bool transform)
                Exec(mdl, Activator.CreateInstance(cnc, Guid.NewGuid().ToString(), typeName, x, y, false, true)!);
                return $"[Dynamo] create '{typeName}' issued at ({x},{y}). Verify with dynamo_get_graph " +
                       "(resolution depends on Dynamo's node library; use a valid node/function name).";
            }
            catch (Exception e) { return $"[Dynamo] add node failed: {e.InnerException?.Message ?? e.Message}"; }
        }

        public static string Connect(string fromNode, string fromPortStr, string toNode, string toPortStr)
        {
            var mdl = GetModel(out var err); if (mdl == null) return "[Dynamo] " + err;
            var ws = GP(mdl, "CurrentWorkspace"); if (ws == null) return "[Dynamo] no workspace";
            var a = FindNode(ws, fromNode); var b = FindNode(ws, toNode);
            if (a == null) return $"[Dynamo] from-node '{fromNode}' not found.";
            if (b == null) return $"[Dynamo] to-node '{toNode}' not found.";
            int fp = Pi(fromPortStr), tp = Pi(toPortStr);
            try
            {
                var mcc = FindNested(mdl, "MakeConnectionCommand");
                if (mcc == null) return "[Dynamo] MakeConnectionCommand unavailable.";
                Type? ptType = null, modeType = null;
                foreach (var c in mcc.GetConstructors())
                {
                    var ps = c.GetParameters();
                    if (ps.Length == 4 && ps[0].ParameterType.Name == "Guid")
                    { ptType = ps[2].ParameterType; modeType = ps[3].ParameterType; break; }
                }
                if (ptType == null || modeType == null) return "[Dynamo] connection enums not resolved.";
                var ptOut = Enum.Parse(ptType, "Output"); var ptIn = Enum.Parse(ptType, "Input");
                var mBegin = Enum.Parse(modeType, "Begin"); var mEnd = Enum.Parse(modeType, "End");
                var aG = (Guid)GP(a, "GUID")!; var bG = (Guid)GP(b, "GUID")!;
                Exec(mdl, Activator.CreateInstance(mcc, aG, fp, ptOut, mBegin)!);
                Exec(mdl, Activator.CreateInstance(mcc, bG, tp, ptIn, mEnd)!);
                return $"[Dynamo] connected {fromNode}[out:{fp}] -> {toNode}[in:{tp}].";
            }
            catch (Exception e) { return $"[Dynamo] connect failed: {e.InnerException?.Message ?? e.Message}"; }
        }

        public static string DeleteNode(string nodeName)
        {
            if (string.IsNullOrWhiteSpace(nodeName)) return "[Dynamo] nodeName is empty.";
            var mdl = GetModel(out var err); if (mdl == null) return "[Dynamo] " + err;
            var ws = GP(mdl, "CurrentWorkspace"); if (ws == null) return "[Dynamo] no workspace";
            var n = FindNode(ws, nodeName);
            if (n == null) return $"[Dynamo] node '{nodeName}' not found.";
            try
            {
                var dmc = FindNested(mdl, "DeleteModelCommand");
                if (dmc == null) return "[Dynamo] DeleteModelCommand unavailable.";
                var gl = new List<Guid> { (Guid)GP(n, "GUID")! };
                object? cmd = null;
                foreach (var c in dmc.GetConstructors())
                    if (c.GetParameters().Length == 1)
                    { try { cmd = Activator.CreateInstance(dmc, gl); break; } catch { } }
                if (cmd == null) return "[Dynamo] DeleteModelCommand ctor mismatch.";
                Exec(mdl, cmd);
                return $"[Dynamo] node '{nodeName}' deleted.";
            }
            catch (Exception e) { return $"[Dynamo] delete failed: {e.InnerException?.Message ?? e.Message}"; }
        }

        // ── Python 엔진 버전-안전 설정 (DynamoDS 소스 기준) ──
        //  2.7–2.12: enum Engine(PythonEngineVersion) only · 2.13–2.19: enum+string 공존 · 3.0+: string EngineName 만
        private static List<string> AvailablePythonEngines()
        {
            var result = new List<string>();
            try
            {
                Type? pem = FindType("Dynamo.PythonServices.PythonEngineManager")
                         ?? FindType("PythonNodeModels.PythonEngineManager");
                var inst = pem?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var avail = inst == null ? null
                    : inst.GetType().GetProperty("AvailableEngines")?.GetValue(inst) as IEnumerable;
                if (avail != null)
                    foreach (var e in avail)
                    {
                        var nm = e?.GetType().GetProperty("Name")?.GetValue(e)?.ToString();
                        if (!string.IsNullOrEmpty(nm)) result.Add(nm!);
                    }
            }
            catch { }
            return result;
        }

        private static void MarkModified(object node)
        {
            try { node.GetType().GetMethod("OnNodeModified", new[] { typeof(bool) })?.Invoke(node, new object[] { true }); } catch { }
        }

        // desired 비었으면 host 기본 엔진 상속(버전별 올바른 기본: 3.x=CPython3, 4.x=PythonNet3).
        // string EngineName(2.13+/3.x/4.x) 우선, enum Engine(2.7–2.12) 폴백. 요청 엔진 미가용이면 가용한 것으로 폴백.
        // 3.x에 PythonNet3 같은 미존재 엔진을 강제하지 않음.
        private static void SetPythonEngine(object node, string desired)
        {
            if (string.IsNullOrWhiteSpace(desired)) return;   // host 기본 상속
            var avail = AvailablePythonEngines();
            string engine = desired;
            if (avail.Count > 0 && !avail.Any(e => string.Equals(e, desired, StringComparison.OrdinalIgnoreCase)))
            {
                string? fb = null;
                foreach (var pref in new[] { "CPython3", "PythonNet3", "IronPython2" })
                { var hit = avail.FirstOrDefault(e => string.Equals(e, pref, StringComparison.OrdinalIgnoreCase)); if (hit != null) { fb = hit; break; } }
                if (fb == null) return;
                engine = fb;
            }
            var t = node.GetType();
            var pName = t.GetProperty("EngineName", BindingFlags.Public | BindingFlags.Instance);
            if (pName != null && pName.CanWrite && pName.PropertyType == typeof(string))
            { try { pName.SetValue(node, engine); MarkModified(node); return; } catch { } }
            var pEnum = t.GetProperty("Engine", BindingFlags.Public | BindingFlags.Instance);
            if (pEnum != null && pEnum.CanWrite && pEnum.PropertyType.IsEnum)
            { try { pEnum.SetValue(node, Enum.Parse(pEnum.PropertyType, engine)); MarkModified(node); } catch { } }
        }

        public static string AddPythonNode(string code, string engine, string inputsStr, string xs, string ys)
        {
            if (string.IsNullOrWhiteSpace(code)) return "[Dynamo] code is empty.";
            var mdl = GetModel(out var err); if (mdl == null) return "[Dynamo] " + err;
            double x = Pd(xs), y = Pd(ys);
            int inputs = Pi(inputsStr); if (inputs < 1) inputs = 1;
            try
            {
                var pnType = FindType("PythonNodeModels.PythonNode");
                if (pnType == null) return "[Dynamo] PythonNode type not found (Python node models not loaded).";
                var node = Activator.CreateInstance(pnType);
                if (node == null) return "[Dynamo] failed to create PythonNode.";

                SetPythonEngine(node, engine ?? "");   // 버전-안전(없으면 host 기본)

                // 추가 입력 포트(IN[1]…) — PythonNode는 VarInputNodeModel 파생, AddInput은 보호 멤버
                if (inputs > 1)
                {
                    var addIn = pnType.GetMethod("AddInput",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                    if (addIn != null)
                    {
                        for (int i = 1; i < inputs; i++) { try { addIn.Invoke(node, null); } catch { } }
                        try { pnType.GetMethod("RegisterAllPorts",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(node, null); } catch { }
                    }
                }

                pnType.GetProperty("Script")?.SetValue(node, code);

                var cnc = FindNested(mdl, "CreateNodeCommand");
                if (cnc == null) return "[Dynamo] CreateNodeCommand unavailable.";
                Exec(mdl, Activator.CreateInstance(cnc, node, x, y, false, true)!);
                pnType.GetProperty("Script")?.SetValue(node, code);   // ensure persisted post-add
                try { pnType.GetMethod("OnNodeModified", new[] { typeof(bool) })?.Invoke(node, new object[] { true }); } catch { }

                int nin = (GP(node, "InPorts") as IEnumerable)?.Cast<object>().Count() ?? 1;
                string engNow = GP(node, "EngineName")?.ToString() ?? "(host default)";
                return $"[Dynamo] Python node added (engine={engNow}, inputs={nin}) at ({x},{y}). " +
                       $"name='{GP(node, "Name")}'. Wire inputs IN[0..{nin - 1}], then dynamo_run_current.";
            }
            catch (Exception e) { return $"[Dynamo] add python node failed: {e.InnerException?.Message ?? e.Message}"; }
        }

        // ── 배치 빌드 (성능): 노드+연결을 1회 호출로, 빌드 중 자동평가 중단 ──
        private static bool DoConnect(object mdl, object fromNode, int fp, object toNode, int tp)
        {
            var mcc = FindNested(mdl, "MakeConnectionCommand"); if (mcc == null) return false;
            Type? ptType = null, modeType = null;
            foreach (var c in mcc.GetConstructors())
            {
                var ps = c.GetParameters();
                if (ps.Length == 4 && ps[0].ParameterType.Name == "Guid")
                { ptType = ps[2].ParameterType; modeType = ps[3].ParameterType; break; }
            }
            if (ptType == null || modeType == null) return false;
            var ptOut = Enum.Parse(ptType, "Output"); var ptIn = Enum.Parse(ptType, "Input");
            var mBegin = Enum.Parse(modeType, "Begin"); var mEnd = Enum.Parse(modeType, "End");
            var aG = (Guid)GP(fromNode, "GUID")!; var bG = (Guid)GP(toNode, "GUID")!;
            Exec(mdl, Activator.CreateInstance(mcc, aG, fp, ptOut, mBegin)!);
            Exec(mdl, Activator.CreateInstance(mcc, bG, tp, ptIn, mEnd)!);
            return true;
        }

        private static object? NodeByGuid(object ws, Guid g) =>
            Nodes(ws).FirstOrDefault(n => GP(n, "GUID") is Guid ng && ng == g);

        private static object? ResolveRef(object ws, Dictionary<string, object> idMap, string? r)
        {
            if (string.IsNullOrEmpty(r)) return null;
            if (idMap.TryGetValue(r!, out var n)) return n;
            return FindNode(ws, r!);
        }

        // spec 하나로 노드를 만들고 노드 객체를 돌려준다(연결 참조용).
        private static object? CreateNodeFromSpec(object mdl, object ws, JObject s, double x, double y)
        {
            var cnc = FindNested(mdl, "CreateNodeCommand"); if (cnc == null) return null;
            if (s["node"] != null)              // OOTB
            {
                var g = Guid.NewGuid();
                Exec(mdl, Activator.CreateInstance(cnc, g.ToString(), s["node"]!.ToString(), x, y, false, true)!);
                return NodeByGuid(ws, g);
            }
            if (s["string"] != null)            // String 입력
            {
                var g = Guid.NewGuid();
                Exec(mdl, Activator.CreateInstance(cnc, g.ToString(), "String", x, y, false, true)!);
                var node = NodeByGuid(ws, g);
                if (node != null) { try { node.GetType().GetProperty("Value")?.SetValue(node, s["string"]!.ToString()); } catch { } }
                return node;
            }
            if (s["codeblock"] != null)         // CodeBlock
            {
                var lib = GP(GP(mdl, "EngineController"), "LibraryServices"); var er = GP(ws, "ElementResolver");
                var cbType = FindType("Dynamo.Graph.Nodes.CodeBlockNodeModel"); if (cbType == null || lib == null) return null;
                var cb = Activator.CreateInstance(cbType, s["codeblock"]!.ToString(), x, y, lib, er);
                Exec(mdl, Activator.CreateInstance(cnc, cb, x, y, false, true)!);
                return cb;
            }
            if (s["python"] != null)            // Python
            {
                var pnType = FindType("PythonNodeModels.PythonNode"); if (pnType == null) return null;
                var node = Activator.CreateInstance(pnType); if (node == null) return null;
                SetPythonEngine(node, s["engine"]?.ToString() ?? "");
                int inputs = (int?)s["inputs"] ?? 1;
                if (inputs > 1)
                {
                    var addIn = pnType.GetMethod("AddInput", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (addIn != null) { for (int i = 1; i < inputs; i++) { try { addIn.Invoke(node, null); } catch { } }
                        try { pnType.GetMethod("RegisterAllPorts", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(node, null); } catch { } }
                }
                pnType.GetProperty("Script")?.SetValue(node, s["python"]!.ToString());
                Exec(mdl, Activator.CreateInstance(cnc, node, x, y, false, true)!);
                pnType.GetProperty("Script")?.SetValue(node, s["python"]!.ToString());
                return node;
            }
            return null;
        }

        public static string BuildGraph(string specJson)
        {
            if (string.IsNullOrWhiteSpace(specJson)) return "[Dynamo] spec is empty.";
            var mdl = GetModel(out var err); if (mdl == null) return "[Dynamo] " + err;
            var ws = GP(mdl, "CurrentWorkspace"); if (ws == null) return "[Dynamo] no workspace";
            JObject spec;
            try { spec = JObject.Parse(specJson); }
            catch (Exception e) { return $"[Dynamo] spec JSON parse error: {e.Message}"; }

            // 1) 자동평가 중단 (RunType=Manual)
            var rs = GP(ws, "RunSettings");
            var rtProp = rs?.GetType().GetProperty("RunType");
            object? prevRunType = rtProp?.GetValue(rs);
            bool suspended = false;
            if (rs != null && rtProp != null && rtProp.CanWrite)
                try { rtProp.SetValue(rs, Enum.Parse(rtProp.PropertyType, "Manual")); suspended = true; } catch { }

            var idMap = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var log = new List<string>();
            int nc = 0, cc = 0;
            try
            {
                double dx = 0;
                foreach (var ns in (spec["nodes"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    string id = ns["id"]?.ToString() ?? Guid.NewGuid().ToString("N");
                    double x = ns["x"] != null ? (double)ns["x"]! : 120 + dx;
                    double y = ns["y"] != null ? (double)ns["y"]! : 650;
                    dx += 260;
                    var node = CreateNodeFromSpec(mdl, ws, ns, x, y);
                    if (node == null) { log.Add($"node '{id}': create failed"); continue; }
                    idMap[id] = node; nc++;
                }
                foreach (var csp in (spec["connect"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var fromN = ResolveRef(ws, idMap, csp["from"]?.ToString());
                    var toN   = ResolveRef(ws, idMap, csp["to"]?.ToString());
                    if (fromN == null || toN == null) { log.Add($"connect {csp["from"]}->{csp["to"]}: node not found"); continue; }
                    int fp = (int?)csp["fromPort"] ?? 0, tp = (int?)csp["toPort"] ?? 0;
                    if (DoConnect(mdl, fromN, fp, toN, tp)) cc++;
                    else log.Add($"connect {csp["from"]}->{csp["to"]}: failed");
                }
            }
            catch (Exception e) { log.Add("build error: " + (e.InnerException?.Message ?? e.Message)); }
            finally
            {
                if (suspended && rtProp != null && prevRunType != null)
                    try { rtProp.SetValue(rs, prevRunType); } catch { }
            }

            // 2) 마지막에 1번만 실행
            string runMsg = "";
            if ((bool?)spec["run"] ?? true) runMsg = " | " + RunCurrent();
            string logs = log.Count > 0 ? "\n  notes: " + string.Join("; ", log) : "";
            return $"[Dynamo] batch build: {nc} node(s), {cc} connection(s) in one pass " +
                   $"(auto-eval {(suspended ? "suspended during build" : "not suspended")}).{runMsg}{logs}";
        }

        // 라이브러리에서 OOTB 노드 생성이름(MangledName) 검색 — dynamo_add_node/build_graph 의 'node' 값에 그대로 사용.
        public static string SearchNodes(string query, string limitStr)
        {
            if (string.IsNullOrWhiteSpace(query)) return "[Dynamo] query is empty.";
            var mdl = GetModel(out var err); if (mdl == null) return "[Dynamo] " + err;
            var lib = GP(GP(mdl, "EngineController"), "LibraryServices");
            if (lib == null) return "[Dynamo] LibraryServices unavailable.";
            int limit = Pi(limitStr); if (limit < 1) limit = 25;

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var pn in new[] { "BuiltinFunctionGroups", "ImportedFunctionGroups" })
                {
                    var groups = GP(lib, pn) as IEnumerable;
                    if (groups == null) continue;
                    foreach (var grp0 in groups)
                    {
                        // 프로퍼티가 Dictionary<string,FunctionGroup>를 노출하는 버전 대비 (KVP면 .Value)
                        var grp = GP(grp0, "Functions") != null ? grp0 : GP(grp0, "Value");
                        var funcs = GP(grp, "Functions") as IEnumerable;
                        if (funcs == null) continue;
                        foreach (var f in funcs)
                        {
                            var mn = (GP(f, "MangledName") ?? GP(f, "QualifiedName"))?.ToString();
                            if (!string.IsNullOrEmpty(mn) &&
                                mn!.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                                names.Add(mn);
                        }
                    }
                }
            }
            catch (Exception e) { return $"[Dynamo] search error: {e.Message}"; }

            var hits = names.Take(limit).ToList();
            string[] uiNodes = { "String", "Number", "Boolean", "Integer Slider", "Double Slider",
                                 "Code Block", "Watch", "Categories", "All Elements of Category" };
            var uiHits = uiNodes.Where(u => u.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"[Dynamo] '{query}' — OOTB matches: {names.Count}" +
                          (names.Count > limit ? $" (showing {limit})" : "") + ":");
            foreach (var h in hits) sb.AppendLine("  " + h);
            if (uiHits.Count > 0) sb.AppendLine("  UI/input nodes: " + string.Join(", ", uiHits));
            if (names.Count == 0 && uiHits.Count == 0)
                sb.AppendLine("  (no match — try a shorter keyword, or use dynamo_add_code_block / dynamo_add_python_node)");
            return sb.ToString().TrimEnd();
        }
    }
}
