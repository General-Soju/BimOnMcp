using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using System.Windows.Forms.Integration;
using BimOnMcpShared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(BimOnAcadPlugin.AcadApp))]

namespace BimOnAcadPlugin
{
    public class AcadApp : IExtensionApplication
    {
        private const string PIPE_NAME = "BimOnAcadPipe";
        private const string HOST      = "AutoCAD";

        private static ScriptStorageService    _storage = new();
        private static CancellationTokenSource _cts     = new();
        private static PaletteSet?             _paletteSet;
        private static ScriptPaletteContent?   _palette;

        public void Initialize()
        {
            _storage = new ScriptStorageService();
            _cts     = new CancellationTokenSource();

            // ① Pipe 서버 최우선 시작 (팔레트 오류와 무관하게 MCP 연결 보장)
            //    다중 인스턴스 관리: 고유 파이프명 + 활성 레지스트리 등록
            string pipeName = HostRegistry.Init("autocad", PIPE_NAME,
                () => { try { return System.IO.Path.GetFileName(
                                  AcApp.DocumentManager.MdiActiveDocument?.Name ?? "AutoCAD"); }
                        catch { return "AutoCAD"; } });
            Task.Run(() => new PipeServer(pipeName, HandleRequest).RunAsync(_cts.Token));

            // ② 팔레트 등록은 AutoCAD 완전 초기화 후 Idle 이벤트에서 수행
            AcApp.Idle += OnFirstIdle;
        }

        public void Terminate()
        {
            HostRegistry.Current?.Shutdown();
            _cts.Cancel();
        }

        private static void OnFirstIdle(object? sender, EventArgs e)
        {
            AcApp.Idle -= OnFirstIdle;
            try { RegisterPalette(); }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BimOnAcad] RegisterPalette 오류: {ex}");
            }
        }

        private static void RegisterPalette()
        {
            _paletteSet = new PaletteSet("BimOn AI Scripts",
                new Guid("A1B2C3D4-E5F6-7890-ABCD-EF0000000003"));

            var (storage, host, exec) = GetPaletteArgs();
            _palette = new ScriptPaletteContent(storage, host, exec, HostRegistry.Current);
            var elementHost = new ElementHost { Child = _palette, Dock = System.Windows.Forms.DockStyle.Fill };
            _paletteSet.Add("Scripts", elementHost);
            _paletteSet.Visible = false;
        }

        // ── 리본 버튼 명령 ───────────────────────────────────────────
        [CommandMethod("BIMONAI")]
        public static void OpenPalette()
        {
            // 팔레트가 없으면 그 자리에서 생성 (Idle 이벤트보다 늦게 실행될 수도 있음)
            if (_paletteSet == null)
            {
                try { RegisterPalette(); }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BimOnAcad] RegisterPalette 오류: {ex}");
                    return;
                }
            }
            _paletteSet!.Visible = !_paletteSet.Visible;
        }

        // ── 요청 라우팅 ───────────────────────────────────────────────
        private static async Task<string> HandleRequest(JObject req, CancellationToken ct)
        {
            var tool = req["params"]?["name"]?.ToString();
            var args = req["params"]?["arguments"] as JObject;
            var doc  = AcApp.DocumentManager.MdiActiveDocument;

            // Lock document for the entire request — ensures thread safety
            // for all read/write operations called from the PipeServer background thread.
            using var docLock = doc?.LockDocument();

            return tool switch
            {
                "get_drawing_info"       => GetDrawingInfo(doc),
                "get_layer_list"         => GetLayerList(doc),
                "get_entity_count"       => GetEntityCount(doc),
                "get_block_list"         => GetBlockList(doc),
                "get_lisp_routines"      => GetLispRoutines(doc),
                "get_selected_entities"  => GetSelectedEntities(doc),
                "get_text_styles"        => GetTextStyles(doc),
                "get_dim_styles"         => GetDimStyles(doc),
                "get_layouts"            => GetLayouts(doc),
                "get_entities_by_layer"  => GetEntitiesByLayer(doc, args),
                "get_entity_by_handle"   => GetEntityByHandle(doc, args),
                "set_entity_property"    => SetEntityProperty(doc, args),
                "get_block_attributes"   => GetBlockAttributes(doc, args),

                "execute_script"         => HandleExecuteScript(args, doc),
                "list_scripts"           => HandleListScripts(null),
                "list_scripts_search"    => HandleListScripts(args?["keyword"]?.ToString()),
                "save_script"            => await HandleSaveScript(args, doc),
                "execute_saved_script"   => HandleExecuteSavedScript(args, doc),
                "delete_script"          => HandleDeleteScript(args),

                _ => $"[Error] Unknown tool: {tool}"
            };
        }

        // ── AutoCAD 도구 구현 ─────────────────────────────────────────

        private static string GetDrawingInfo(Document? doc)
        {
            if (doc == null) return "No drawing open";
            var db = doc.Database;
            return $"파일: {System.IO.Path.GetFileName(doc.Name)}\n경로: {doc.Name}\n저장 형식: DWG R{db.OriginalFileVersion}";
        }

        private static string GetLayerList(Document? doc)
        {
            if (doc == null) return "No drawing open";
            var db = doc.Database;
            var sb = new StringBuilder("Layers:\n");
            using var tr = db.TransactionManager.StartTransaction();
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                var layer = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                sb.AppendLine($"  {layer.Name,-25} 색상:{layer.Color.ColorIndex,3}  On:{!layer.IsOff}  Locked:{layer.IsLocked}  Frozen:{layer.IsFrozen}");
            }
            tr.Commit();
            return sb.ToString().TrimEnd();
        }

        private static string GetEntityCount(Document? doc)
        {
            if (doc == null) return "No drawing open";
            var db = doc.Database;
            using var tr = db.TransactionManager.StartTransaction();
            var ms    = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            int count = ms.Cast<ObjectId>().Count();
            tr.Commit();
            return $"Entity count: {count:N0}개";
        }

        private static string GetBlockList(Document? doc)
        {
            if (doc == null) return "No drawing open";
            var db = doc.Database;
            var sb = new StringBuilder("Block definitions:\n");
            using var tr = db.TransactionManager.StartTransaction();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!btr.IsAnonymous && !btr.IsLayout)
                    sb.AppendLine($"  {btr.Name,-30} entities:{btr.Cast<ObjectId>().Count()}개");
            }
            tr.Commit();
            return sb.ToString().TrimEnd();
        }

        private static string GetLispRoutines(Document? doc)
        {
            if (doc == null) return "No drawing open";
            // AutoCAD LISP 함수 목록은 AcedGetSysVars 등으로 조회 가능
            return "AutoLISP routine query: use (atoms-family 1 nil) in execute_script";
        }

        private static string GetSelectedEntities(Document? doc)
        {
            if (doc == null) return "No drawing open";
            var ed = doc.Editor;
            var ss = ed.SelectImplied();
            if (ss.Status != PromptStatus.OK || ss.Value.Count == 0) return "No objects selected.";
            var db = doc.Database;
            var sb = new StringBuilder($"Selected objects ({ss.Value.Count}):\n");
            using var tr = db.TransactionManager.StartTransaction();
            foreach (SelectedObject so in ss.Value)
            {
                var ent = (Entity)tr.GetObject(so.ObjectId, OpenMode.ForRead);
                sb.AppendLine($"  [{so.ObjectId.Handle}] {ent.GetType().Name}  Layer:{ent.Layer}  Color:{ent.Color.ColorIndex}");
            }
            tr.Commit();
            return sb.ToString().TrimEnd();
        }

        private static string GetTextStyles(Document? doc)
        {
            if (doc == null) return "No drawing open";
            var db = doc.Database;
            var sb = new StringBuilder("Text styles:\n");
            using var tr = db.TransactionManager.StartTransaction();
            var tt = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            foreach (ObjectId id in tt)
            {
                var ts = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                sb.AppendLine($"  {ts.Name,-20} Height:{ts.TextSize:F2}  Font:{ts.FileName}");
            }
            tr.Commit();
            return sb.ToString().TrimEnd();
        }

        private static string GetDimStyles(Document? doc)
        {
            if (doc == null) return "No drawing open";
            var db = doc.Database;
            var sb = new StringBuilder("Dimension styles:\n");
            using var tr = db.TransactionManager.StartTransaction();
            var dt = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            foreach (ObjectId id in dt)
            {
                var ds = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                sb.AppendLine($"  {ds.Name,-25} Scale:{ds.Dimscale:F2}");
            }
            tr.Commit();
            return sb.ToString().TrimEnd();
        }

        private static string GetLayouts(Document? doc)
        {
            if (doc == null) return "No drawing open";
            var db = doc.Database;
            var sb = new StringBuilder("Layouts:\n");
            using var tr = db.TransactionManager.StartTransaction();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!btr.IsLayout) continue;
                var layout = (Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead);
                sb.AppendLine($"  [{layout.TabOrder}] {layout.LayoutName,-20} ({(layout.ModelType?"Model":"Sheet")})");
            }
            tr.Commit();
            return sb.ToString().TrimEnd();
        }

        private static string GetEntitiesByLayer(Document? doc, JObject? args)
        {
            if (doc == null) return "No drawing open";
            string layerName = args?["layerName"]?.ToString() ?? "";
            int max          = int.TryParse(args?["maxCount"]?.ToString(), out int mc) ? mc : 100;
            var db = doc.Database;
            var sb = new StringBuilder($"레이어 '{layerName}' entities:\n");
            int count = 0;
            using var tr = db.TransactionManager.StartTransaction();
            var ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (count >= max) break;
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || !ent.Layer.Equals(layerName, StringComparison.OrdinalIgnoreCase)) continue;
                sb.AppendLine($"  [{id.Handle}] {ent.GetType().Name}");
                count++;
            }
            tr.Commit();
            if (count == 0) return $"레이어 '{layerName}'에 객체 없음";
            return sb.ToString().TrimEnd();
        }

        private static string GetEntityByHandle(Document? doc, JObject? args)
        {
            if (doc == null) return "No drawing open";
            string handleStr = args?["handle"]?.ToString() ?? "";
            if (!long.TryParse(handleStr, System.Globalization.NumberStyles.HexNumber, null, out long handleVal))
                return "Please provide a valid Handle.";
            var db = doc.Database;
            var handle = new Handle(handleVal);
            if (!db.TryGetObjectId(handle, out ObjectId objId)) return $"Handle '{handleStr}' 없음";
            using var tr = db.TransactionManager.StartTransaction();
            var ent = tr.GetObject(objId, OpenMode.ForRead) as Entity;
            if (ent == null) { tr.Commit(); return "Entity not found."; }
            var sb = new StringBuilder($"[{handleStr}] {ent.GetType().Name}:\n");
            sb.AppendLine($"  레이어: {ent.Layer}");
            sb.AppendLine($"  색상: {ent.Color.ColorIndex}");
            sb.AppendLine($"  선종류: {ent.Linetype}");
            if (ent is DBText txt) sb.AppendLine($"  문자: {txt.TextString}");
            if (ent is BlockReference br)
            {
                sb.AppendLine($"  블록: {br.Name}");
                sb.AppendLine($"  삽입점: ({br.Position.X:F2},{br.Position.Y:F2},{br.Position.Z:F2})");
            }
            tr.Commit();
            return sb.ToString().TrimEnd();
        }

        private static string SetEntityProperty(Document? doc, JObject? args)
        {
            if (doc == null) return "No drawing open";
            string handleStr = args?["handle"]?.ToString() ?? "";
            string property  = args?["property"]?.ToString() ?? "";
            string value     = args?["value"]?.ToString() ?? "";

            if (!long.TryParse(handleStr, System.Globalization.NumberStyles.HexNumber, null, out long handleVal))
                return "Please provide a valid Handle.";
            var db = doc.Database;
            var handle = new Handle(handleVal);
            if (!db.TryGetObjectId(handle, out ObjectId objId)) return $"Handle '{handleStr}' 없음";

            using var tr = db.TransactionManager.StartTransaction();
            var ent = tr.GetObject(objId, OpenMode.ForWrite) as Entity;
            if (ent == null) { tr.Commit(); return "Entity not found."; }

            bool ok = property.ToLower() switch
            {
                "layer"    => SetAndReturn(() => ent.Layer    = value),
                "color"    => short.TryParse(value, out short c) && SetAndReturn(() => ent.ColorIndex = c),
                "linetype" => SetAndReturn(() => ent.Linetype = value),
                _          => false
            };
            tr.Commit();
            return ok ? $"[{handleStr}] {property} = {value} updated" : $"속성 '{property}' 변경 실패";
        }

        private static bool SetAndReturn(Action action) { action(); return true; }

        private static string GetBlockAttributes(Document? doc, JObject? args)
        {
            if (doc == null) return "No drawing open";
            string blockName = args?["blockName"]?.ToString() ?? "";
            int max          = int.TryParse(args?["maxCount"]?.ToString(), out int mc) ? mc : 50;
            var db = doc.Database;
            var sb = new StringBuilder($"블록 '{blockName}' 속성:\n");
            int count = 0;
            using var tr = db.TransactionManager.StartTransaction();
            var ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (count >= max) break;
                var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (br == null || !br.Name.Contains(blockName, StringComparison.OrdinalIgnoreCase)) continue;
                sb.AppendLine($"\n  [{br.ObjectId.Handle}] {br.Name}:");
                foreach (ObjectId attId in br.AttributeCollection)
                {
                    var att = (AttributeReference)tr.GetObject(attId, OpenMode.ForRead);
                    sb.AppendLine($"    {att.Tag}: {att.TextString}");
                }
                count++;
            }
            tr.Commit();
            if (count == 0) return $"블록 '{blockName}' 없음";
            return sb.ToString().TrimEnd();
        }

        // ── 스크립트 도구 ─────────────────────────────────────────────

        private static string HandleExecuteScript(JObject? args, Document? doc)
        {
            string code = args?["code"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(code)) return "[Error] code is empty.";
            if (doc == null) return "No drawing open";
            var db = doc.Database;
            var vars = new Dictionary<string, object?>
            {
                ["doc"]      = doc,
                ["db"]       = db,
                ["ed"]       = doc.Editor,
                ["app"]      = AcApp.DocumentManager,
            };
            return ScriptExecutor.FormatResult(ScriptExecutor.Execute(code, vars));
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

            string[] tags = tagsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToArray();
            var meta = _storage.Save(HOST, name, description, userCode, tags, panel);

            // _palette.Dispatcher 로 직접 refresh (Application.Current 가 null 일 수 있음)
            _palette?.Dispatcher.InvokeAsync(() => _palette?.LoadScripts());

            var sb = new StringBuilder($"✅ [{HOST}] '{name}' saved\n   경로: {meta.ScriptPath}");
            if (execAfter) { sb.AppendLine("\n── 즉시 실행 결과 ──"); sb.AppendLine(HandleExecuteScript(args, doc)); }
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
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) { MessageBox.Show("No drawing is currently open."); return; }
                string raw  = File.ReadAllText(meta.ScriptPath);
                string code = ScriptStorageService.ExtractUserCode(raw);
                var vars    = new Dictionary<string, object?> { ["doc"]=doc, ["db"]=doc.Database, ["ed"]=doc.Editor };
                var result  = ScriptExecutor.Execute(code, vars);
                MessageBox.Show(ScriptExecutor.FormatResult(result), $"[{meta.Name}] 결과");
            });
    }
}
