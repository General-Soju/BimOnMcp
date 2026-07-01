using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace BimOnMcpShared
{
    /// <summary>
    /// AI 생성 스크립트를 host별 폴더에 저장하고 메타데이터를 관리합니다.
    /// 저장 경로: %AppData%\BimOnAI\Scripts\{host}\{safeName}\script.py
    /// </summary>
    public class ScriptStorageService
    {
        private static readonly Encoding Utf8NoBom =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly string _metaDbPath;
        private readonly string _scriptRootPath;
        private readonly string _archiveRootPath;
        private readonly string _archiveMetaPath;

        public ScriptStorageService()
        {
            string appData    = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _scriptRootPath   = Path.Combine(appData, "BimOnAI", "Scripts");
            _metaDbPath       = Path.Combine(appData, "BimOnAI", "scripts_meta.json");
            _archiveRootPath  = Path.Combine(appData, "BimOnAI", "Scripts_Archive");
            _archiveMetaPath  = Path.Combine(appData, "BimOnAI", "scripts_meta_archive.json");
            Directory.CreateDirectory(_scriptRootPath);
        }

        // ── 목록 조회 ──────────────────────────────────────────────

        public List<ScriptMeta> GetAll() => LoadMeta();

        public List<ScriptMeta> GetByHost(string host) =>
            LoadMeta().Where(m => m.Host.Equals(host, StringComparison.OrdinalIgnoreCase)).ToList();

        public List<ScriptMeta> Search(string? keyword, string? host = null)
        {
            var all = string.IsNullOrWhiteSpace(host) ? LoadMeta() : GetByHost(host);
            if (string.IsNullOrWhiteSpace(keyword)) return all;
            var kw = keyword.ToLowerInvariant();
            return all.Where(m =>
                m.Name.ToLowerInvariant().Contains(kw) ||
                m.Description.ToLowerInvariant().Contains(kw) ||
                m.Tags.Any(t => t.ToLowerInvariant().Contains(kw))).ToList();
        }

        public ScriptMeta? FindByName(string name, string? host = null)
        {
            var list = string.IsNullOrWhiteSpace(host) ? LoadMeta() : GetByHost(host);
            return list.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        // ── 저장 ───────────────────────────────────────────────────

        /// <summary>
        /// AI 생성 코드를 저장합니다.
        /// pyRevit 호환 헤더는 host == "Revit" 일 때만 삽입됩니다.
        /// </summary>
        public ScriptMeta Save(
            string host,
            string name,
            string description,
            string userCode,
            string[] tags,
            string panel = "General")
        {
            string safeName  = Sanitize(name);
            string hostDir   = Path.Combine(_scriptRootPath, host);
            string btnPath   = Path.Combine(hostDir, safeName);
            Directory.CreateDirectory(btnPath);

            // host별 호환 헤더 생성
            string finalCode = BuildCode(host, userCode);
            File.WriteAllText(Path.Combine(btnPath, "script.py"), finalCode, Utf8NoBom);

            string yaml =
                $"title: {name}\n" +
                $"description: {description}\n" +
                $"host: {host}\n" +
                $"author: AI (BimOn)\n" +
                $"tags: [{string.Join(", ", tags)}]\n" +
                $"created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
            File.WriteAllText(Path.Combine(btnPath, "meta.yaml"), yaml, Utf8NoBom);

            var metas = LoadMeta();
            metas.RemoveAll(m =>
                m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                m.Host.Equals(host, StringComparison.OrdinalIgnoreCase));

            var meta = new ScriptMeta
            {
                Name        = name,
                SafeName    = safeName,
                Description = description,
                Tags        = tags,
                Panel       = panel,
                Host        = host,
                ScriptPath  = Path.Combine(btnPath, "script.py"),
                CreatedAt   = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            metas.Add(meta);
            SaveMeta(metas);
            return meta;
        }

        // ── 삭제 / 아카이브 / 정리 (유지보수) ──────────────────────

        /// <summary>
        /// 스크립트를 삭제합니다 (메타 항목 + 폴더 동시 제거).
        /// 안전: _scriptRootPath 하위 폴더만 삭제 (경로 이탈 방지).
        /// </summary>
        public bool Delete(string name, string host)
        {
            var metas = LoadMeta();
            int idx = metas.FindIndex(m =>
                m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                m.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;

            var meta = metas[idx];
            TryDeleteScriptFolder(meta);
            metas.RemoveAt(idx);
            SaveMeta(metas);
            return true;
        }

        /// <summary>
        /// 스크립트를 아카이브로 이동합니다 (삭제 아님).
        /// Scripts_Archive\{yyyyMM}\{host}\{safeName}\ 로 폴더 이동 + 별도 메타에 보관.
        /// </summary>
        public bool Archive(string name, string host)
        {
            var metas = LoadMeta();
            int idx = metas.FindIndex(m =>
                m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                m.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;

            var meta = metas[idx];
            try
            {
                string? srcDir = Path.GetDirectoryName(meta.ScriptPath);
                if (!string.IsNullOrEmpty(srcDir) && Directory.Exists(srcDir) &&
                    IsUnderRoot(srcDir, _scriptRootPath))
                {
                    string bucket  = DateTime.Now.ToString("yyyyMM");
                    string destDir = Path.Combine(_archiveRootPath, bucket, host, meta.SafeName);
                    Directory.CreateDirectory(Path.GetDirectoryName(destDir)!);
                    if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
                    Directory.Move(srcDir, destDir);
                    meta.ScriptPath = Path.Combine(destDir, "script.py");
                }
            }
            catch { return false; }

            var archive = LoadArchiveMeta();
            archive.RemoveAll(m =>
                m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                m.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
            archive.Add(meta);
            SaveArchiveMeta(archive);

            metas.RemoveAt(idx);
            SaveMeta(metas);
            return true;
        }

        /// <summary>
        /// 고아 정리: 끊긴 메타 링크(파일 없음) 제거 + 참조되지 않는 폴더 삭제.
        /// 반환: (제거된 끊긴 링크 수, 삭제된 고아 폴더 수)
        /// </summary>
        public (int brokenLinks, int orphanFolders) CleanOrphans()
        {
            var metas = LoadMeta();

            // 1) 파일이 존재하지 않는 메타 항목 제거
            int brokenLinks = 0;
            var alive = new List<ScriptMeta>();
            foreach (var m in metas)
            {
                if (!string.IsNullOrEmpty(m.ScriptPath) && File.Exists(m.ScriptPath)) alive.Add(m);
                else brokenLinks++;
            }
            if (brokenLinks > 0) SaveMeta(alive);

            // 2) 메타가 참조하지 않는 스크립트 폴더 삭제
            int orphanFolders = 0;
            var referenced = new HashSet<string>(
                alive.Select(m => Path.GetDirectoryName(m.ScriptPath) ?? "")
                     .Where(d => !string.IsNullOrEmpty(d)),
                StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(_scriptRootPath))
            {
                foreach (var hostDir in Directory.GetDirectories(_scriptRootPath))
                {
                    foreach (var scriptDir in Directory.GetDirectories(hostDir))
                    {
                        if (!referenced.Contains(scriptDir))
                        {
                            try { Directory.Delete(scriptDir, true); orphanFolders++; }
                            catch { /* 권한 등 오류 시 건너뜀 */ }
                        }
                    }
                }
            }
            return (brokenLinks, orphanFolders);
        }

        /// <summary>저장소 통계 요약 문자열.</summary>
        public string GetStatsSummary()
        {
            var metas = LoadMeta();
            var sb = new StringBuilder();
            sb.AppendLine($"Total scripts: {metas.Count}");
            foreach (var g in metas.GroupBy(m => m.Host).OrderBy(g => g.Key))
                sb.AppendLine($"  {g.Key}: {g.Count()}");

            long bytes = 0;
            if (Directory.Exists(_scriptRootPath))
                foreach (var f in Directory.GetFiles(_scriptRootPath, "*", SearchOption.AllDirectories))
                    try { bytes += new FileInfo(f).Length; } catch { }
            sb.AppendLine($"Storage: {bytes / 1024.0:F1} KB");

            if (metas.Count > 0)
            {
                sb.AppendLine($"Oldest: {metas.OrderBy(m => m.CreatedAt).First().CreatedAt}");
                sb.AppendLine($"Newest: {metas.OrderByDescending(m => m.CreatedAt).First().CreatedAt}");
            }
            return sb.ToString().TrimEnd();
        }

        private void TryDeleteScriptFolder(ScriptMeta meta)
        {
            try
            {
                string? dir = Path.GetDirectoryName(meta.ScriptPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) &&
                    IsUnderRoot(dir, _scriptRootPath))
                    Directory.Delete(dir, true);
            }
            catch { /* 폴더 삭제 실패해도 메타는 제거 진행 */ }
        }

        private static bool IsUnderRoot(string path, string root)
        {
            string full = Path.GetFullPath(path);
            string rootFull = Path.GetFullPath(root);
            return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
        }

        private List<ScriptMeta> LoadArchiveMeta()
        {
            try
            {
                if (!File.Exists(_archiveMetaPath)) return new List<ScriptMeta>();
                return JsonConvert.DeserializeObject<List<ScriptMeta>>(
                    File.ReadAllText(_archiveMetaPath)) ?? new List<ScriptMeta>();
            }
            catch { return new List<ScriptMeta>(); }
        }

        private void SaveArchiveMeta(List<ScriptMeta> metas)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_archiveMetaPath)!);
            File.WriteAllText(_archiveMetaPath,
                JsonConvert.SerializeObject(metas, Formatting.Indented), Utf8NoBom);
        }

        // ── 코드 원본 추출 (헤더 제거) ────────────────────────────

        public static string ExtractUserCode(string rawCode)
        {
            const string marker = "pass  # MCP: API objects already injected\n\n";
            int idx = rawCode.IndexOf(marker, StringComparison.Ordinal);
            return idx >= 0 ? rawCode.Substring(idx + marker.Length) : rawCode;
        }

        // ── 내부 헬퍼 ──────────────────────────────────────────────

        private static string BuildCode(string host, string userCode)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# -*- coding: utf-8 -*-");
            sb.AppendLine($"# BimOn AI Script [{host}] — MCP execution and palette button compatible");
            sb.AppendLine("try:");

            switch (host.ToLower())
            {
                case "revit":
                    sb.AppendLine("    import clr");
                    sb.AppendLine("    doc   = __revit__.ActiveUIDocument.Document");
                    sb.AppendLine("    uidoc = __revit__.ActiveUIDocument");
                    sb.AppendLine("    app   = __revit__.Application");
                    break;
                case "navisworks":
                    sb.AppendLine("    import clr");
                    sb.AppendLine("    doc   = __navisworks__.ActiveDocument");
                    sb.AppendLine("    app   = __navisworks__");
                    break;
                case "autocad":
                    sb.AppendLine("    import clr");
                    sb.AppendLine("    db    = __acadapp__.DocumentManager.MdiActiveDocument.Database");
                    sb.AppendLine("    doc   = __acadapp__.DocumentManager.MdiActiveDocument");
                    sb.AppendLine("    ed    = doc.Editor");
                    sb.AppendLine("    app   = __acadapp__");
                    break;
            }

            sb.AppendLine("except:");
            sb.AppendLine("    pass  # MCP: API objects already injected");
            sb.AppendLine();
            sb.Append(userCode);
            return sb.ToString();
        }

        public List<ScriptMeta> LoadMeta()
        {
            try
            {
                if (!File.Exists(_metaDbPath)) return new List<ScriptMeta>();
                return JsonConvert.DeserializeObject<List<ScriptMeta>>(
                    File.ReadAllText(_metaDbPath)) ?? new List<ScriptMeta>();
            }
            catch { return new List<ScriptMeta>(); }
        }

        public void SaveMeta(List<ScriptMeta> metas)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_metaDbPath)!);
            File.WriteAllText(_metaDbPath,
                JsonConvert.SerializeObject(metas, Formatting.Indented), Utf8NoBom);
        }

        private static string Sanitize(string name) =>
            new string(name.Replace(" ", "_")
                .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }
}
