using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BimOnMcpShared
{
    /// <summary>
    /// IronPython 엔진으로 스크립트를 실행합니다.
    /// net8.0-windows: IronPython 3.4 (Python 3.x 문법)
    /// net48:          IronPython 2.7 (Python 2.7 문법, Navisworks/net48 환경)
    ///
    /// IronPython 2.7 주의사항:
    ///   - preamble 파라미터로 전달된 코드는 사용자 코드와 별도 컴파일 단위로 실행됩니다.
    ///     이는 'from X import *' 뒤에 'from Y import Z'가 오는 조합에서
    ///     IronPython 2.7이 "unexpected token" 에러를 내는 버그를 우회합니다.
    ///   - lib 폴더에 Python 3 표준 라이브러리가 있으면 파싱 오류가 발생하므로
    ///     IronPython 2.7 호환 파일만 넣어야 합니다.
    /// </summary>
    public static class ScriptExecutor
    {
        public static ScriptResult Execute(
            string code,
            Dictionary<string, object?> variables,
            string? scriptDir = null,
            string? preamble  = null)
        {
            var result = new ScriptResult();
            try
            {
                var engine = IronPython.Hosting.Python.CreateEngine();

                // StdLib 경로 등록
                var paths = engine.GetSearchPaths();
                string? asmPath = System.Reflection.Assembly
                    .GetExecutingAssembly().Location;
                string baseDir = Path.GetDirectoryName(asmPath) ?? "";
#if NET48
                // IronPython 2.7: lib 폴더에 Python 2.7 호환 파일만 허용
                // (Python 3 StdLib가 있으면 파싱 에러 발생 — 호환 파일만 있는 경우에만 추가)
                foreach (var p in new[] {
                    Path.Combine(baseDir, "Lib"),
                    Path.Combine(baseDir, "lib") })
                {
                    if (Directory.Exists(p)) paths.Add(p);
                }
#else
                // IronPython 3.4: StdLib 폴더 필요 (io, os, json 등).
                // DLL 옆(개발/수동배포) 또는 공유 위치(%AppData%\BimOnAI\Lib, 인스톨러 배포)에서 탐색.
                string sharedLib = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BimOnAI", "Lib");
                foreach (var p in new[] {
                    Path.Combine(baseDir, "Lib"),
                    Path.Combine(baseDir, "lib"),
                    sharedLib })
                {
                    if (Directory.Exists(p)) paths.Add(p);
                }
#endif
                if (!string.IsNullOrEmpty(scriptDir) && Directory.Exists(scriptDir))
                    paths.Add(scriptDir);
                engine.SetSearchPaths(paths);

                var scope = engine.CreateScope();

                // API 변수 주입
                foreach (var kv in variables)
                    if (kv.Value != null) scope.SetVariable(kv.Key, kv.Value);

                // sys.stdout 리다이렉트 (한글 깨짐 방지)
                // IronPython 2.7(net48): str/unicode 모두 수용하는 커스텀 캡처 클래스 사용
                // IronPython 3.4(net8+): io.StringIO 직접 사용
#if NET48
                engine.Execute(
                    "import sys as _sys\n" +
                    "class _Capture(object):\n" +
                    "    def __init__(self): self._b=[]\n" +
                    "    def write(self,s):\n" +
                    "        if isinstance(s,bytes): s=s.decode('utf-8','replace')\n" +
                    "        self._b.append(s)\n" +
                    "    def flush(self): pass\n" +
                    "    def getvalue(self): return ''.join(self._b)\n" +
                    "_cap=_Capture()\n" +
                    "_sys.stdout=_cap\n_sys.stderr=_cap\n", scope);
#else
                engine.Execute(
                    "import sys, io\n_cap=io.StringIO()\n" +
                    "sys.stdout=_cap\nsys.stderr=_cap\n", scope);
#endif

                // 프리앰블이 있으면 별도 컴파일 단위로 먼저 실행
                // IronPython 2.7 버그 우회: 'from X import *' 가 포함된 코드를
                // 사용자 코드와 같은 컴파일 단위에 두면 "unexpected token" 에러 발생
                if (!string.IsNullOrEmpty(preamble))
                    engine.Execute(preamble, scope);

                engine.Execute(code, scope);
                engine.Execute("_out=_cap.getvalue()", scope);

                scope.TryGetVariable("_out",    out object? outObj);
                scope.TryGetVariable("result",  out object? rv);

                result.Output    = outObj?.ToString() ?? "";
                result.ReturnVal = rv?.ToString();
                result.Success   = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                // 전체 예외 체인 보고 — "Error" 같은 무의미한 메시지만으로는
                // 런타임(.NET 10 등) 비호환 문제를 진단할 수 없음
                var sbErr = new StringBuilder();
                var e = (Exception?)ex;
                while (e != null)
                {
                    sbErr.AppendLine($"{e.GetType().FullName}: {e.Message}");
                    e = e.InnerException;
                }
                var first = ex.StackTrace?.Split('\n');
                if (first != null && first.Length > 0)
                    sbErr.AppendLine(first[0].Trim());
                result.Error = sbErr.ToString().TrimEnd();
            }
            return result;
        }

        public static string FormatResult(ScriptResult r)
        {
            var sb = new StringBuilder(r.Success ? "✅ Success\n" : "❌ Failed\n");
            if (!string.IsNullOrWhiteSpace(r.Output))
                sb.AppendLine("── Output ──\n" + r.Output.TrimEnd());
            if (!string.IsNullOrWhiteSpace(r.ReturnVal))
                sb.AppendLine("── Result ──\n" + r.ReturnVal);
            if (!string.IsNullOrWhiteSpace(r.Error))
                sb.AppendLine("── Error ──\n" + r.Error);
            return sb.ToString().TrimEnd();
        }
    }

    public class ScriptResult
    {
        public bool    Success   { get; set; }
        public string  Output    { get; set; } = "";
        public string? ReturnVal { get; set; }
        public string? Error     { get; set; }
    }
}
