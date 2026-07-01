using System;

namespace BimOnMcpShared
{
    /// <summary>
    /// AI가 생성·저장한 스크립트의 메타데이터.
    /// host 필드로 Revit / Navisworks / AutoCAD 를 구분합니다.
    /// </summary>
    public class ScriptMeta
    {
        public string   Name        { get; set; } = "";
        public string   SafeName    { get; set; } = "";
        public string   Description { get; set; } = "";
        public string[] Tags        { get; set; } = Array.Empty<string>();
        public string   Panel       { get; set; } = "General";

        /// <summary>Revit | Navisworks | AutoCAD</summary>
        public string   Host        { get; set; } = "Revit";

        public string   ScriptPath  { get; set; } = "";
        public string   CreatedAt   { get; set; } = "";
    }
}
