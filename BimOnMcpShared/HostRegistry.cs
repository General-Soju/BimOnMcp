using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace BimOnMcpShared
{
    /// <summary>
    /// 팔레트(공유 UI)가 연결 상태를 표시/제어하기 위한 계약.
    /// </summary>
    public interface IConnectionController
    {
        /// <summary>이 인스턴스가 현재 MCP 활성(ON) 대상인가.</summary>
        bool IsConnected { get; }
        /// <summary>사람이 읽는 상태 문구.</summary>
        string StatusText { get; }
        /// <summary>[연결] 버튼을 눌러 전환할 수 있는 상태인가(이미 ON이면 false).</summary>
        bool CanConnectHere { get; }
        /// <summary>이 인스턴스를 활성(ON)으로 만든다(다른 인스턴스는 자동 OFF).</summary>
        void Connect();
        /// <summary>상태 변경 통지(백그라운드 스레드에서 발생할 수 있음).</summary>
        event Action? Changed;
    }

    /// <summary>
    /// 동일 제품(Revit/Navisworks/AutoCAD) 다중 인스턴스 환경에서
    /// "지금 MCP가 연결되는 단 하나의 인스턴스"를 파일 레지스트리로 관리한다.
    ///
    ///   %LOCALAPPDATA%\BimOnAI\hosts\&lt;product&gt;\
    ///       &lt;pid&gt;.json   : 각 인스턴스 등록 + 하트비트(고유 파이프명/문서명/생존)
    ///       active.json     : 현재 ON 인 단 하나의 인스턴스
    ///
    /// 각 인스턴스는 고유 파이프(base.&lt;pid&gt;)로 서버를 띄우므로 이름 경쟁이 없다.
    /// 브리지는 active.json 을 읽어 해당 인스턴스의 고유 파이프로만 연결한다.
    /// </summary>
    public sealed class HostRegistry : IConnectionController, IDisposable
    {
        public static HostRegistry? Current { get; private set; }

        private readonly string       _product;     // revit / navisworks / autocad
        private readonly string       _pipeName;    // 고유: base.<pid>
        private readonly int          _pid;
        private readonly string       _pname;       // 프로세스명(PID 재사용 방지)
        private readonly Func<string> _title;       // 현재 문서 제목 제공자(지연 평가)
        private readonly string       _dir;
        private readonly string       _selfFile;
        private readonly string       _activeFile;
        private Timer?                _timer;
        private bool                  _lastIsActive;

        public string PipeName => _pipeName;

        public event Action? Changed;

        private HostRegistry(string product, string baseName, Func<string>? title)
        {
            _product = product;
            var p     = Process.GetCurrentProcess();
            _pid      = p.Id;
            _pname    = SafeName(p);
            _pipeName = $"{baseName}.{_pid}";
            _title    = title ?? (() => product);
            _dir      = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BimOnAI", "hosts", product);
            _selfFile   = Path.Combine(_dir, _pid + ".json");
            _activeFile = Path.Combine(_dir, "active.json");
        }

        /// <summary>플러그인 시작 시 1회 호출. 이 인스턴스의 고유 파이프명을 반환한다.</summary>
        public static string Init(string product, string baseName, Func<string>? title)
        {
            var r = new HostRegistry(product, baseName, title);
            Current = r;
            r.Start();
            return r._pipeName;
        }

        private void Start()
        {
            try { Directory.CreateDirectory(_dir); } catch { }
            WriteSelf();
            // 시작 시 살아있는 활성 인스턴스가 없으면 자동 ON (단일 인스턴스는 클릭 불필요)
            if (!HasLiveActive(out _)) Activate();
            _lastIsActive = IsConnected;
            try { AppDomain.CurrentDomain.ProcessExit += (_, __) => Shutdown(); } catch { }
            _timer = new Timer(_ => Tick(), null, 2000, 2000);
        }

        private void Tick()
        {
            try
            {
                WriteSelf();   // 하트비트 + 문서 제목 갱신
                if (!HasLiveActive(out _))
                {
                    // 활성 인스턴스가 없음(미연결이거나 ON 이던 인스턴스가 종료됨).
                    // 살아있는 인스턴스가 '나 하나'뿐이면 선택지가 없으므로 자동 복구 ON.
                    var live = LivePids();
                    if (live.Count == 1 && live[0] == _pid) Activate();
                }
            }
            catch { }

            bool now = IsConnected;
            if (now != _lastIsActive)
            {
                _lastIsActive = now;
                try { Changed?.Invoke(); } catch { }
            }
        }

        // ── IConnectionController ─────────────────────────────────────
        public bool IsConnected
        {
            get
            {
                var act = TryRead(_activeFile);
                return act != null && (int?)act["pid"] == _pid;
            }
        }

        public bool CanConnectHere => !IsConnected;

        public string StatusText
        {
            get
            {
                if (IsConnected)
                    return "● 연결됨 — 이 인스턴스가 MCP 대상입니다";
                if (HasLiveActive(out var info))
                {
                    string t = info?["title"]?.ToString() ?? "";
                    return string.IsNullOrWhiteSpace(t)
                        ? "○ 다른 인스턴스가 연결됨 — [연결]로 전환"
                        : $"○ 다른 인스턴스 연결됨 ({t}) — [연결]로 전환";
                }
                return "○ 연결 안 됨 — [연결]을 누르세요";
            }
        }

        public void Connect()
        {
            Activate();
            _lastIsActive = true;
            try { Changed?.Invoke(); } catch { }
        }

        // ── 활성/해제 ─────────────────────────────────────────────────
        private void Activate()
        {
            var o = new JObject
            {
                ["pid"]   = _pid,
                ["pipe"]  = _pipeName,
                ["pname"] = _pname,
                ["product"] = _product,
                ["title"] = SafeTitle(),
                ["since"] = DateTime.UtcNow.Ticks,
            };
            WriteAtomic(_activeFile, o.ToString(Newtonsoft.Json.Formatting.None));
        }

        public void Shutdown()
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;
            try
            {
                var act = TryRead(_activeFile);
                if (act != null && (int?)act["pid"] == _pid)
                    File.Delete(_activeFile);
            }
            catch { }
            try { if (File.Exists(_selfFile)) File.Delete(_selfFile); } catch { }
        }

        public void Dispose() => Shutdown();

        // ── 내부 헬퍼 ─────────────────────────────────────────────────
        private void WriteSelf()
        {
            var o = new JObject
            {
                ["pid"]   = _pid,
                ["pipe"]  = _pipeName,
                ["pname"] = _pname,
                ["product"] = _product,
                ["title"] = SafeTitle(),
                ["ts"]    = DateTime.UtcNow.Ticks,
            };
            WriteAtomic(_selfFile, o.ToString(Newtonsoft.Json.Formatting.None));
        }

        /// <summary>active.json 이 가리키는 인스턴스가 실제로 살아있는가.</summary>
        private bool HasLiveActive(out JObject? info)
        {
            info = TryRead(_activeFile);
            return info != null && IsAlive(info);
        }

        /// <summary>레지스트리에 등록된 살아있는 인스턴스 PID 목록(죽은 파일은 정리).</summary>
        private List<int> LivePids()
        {
            var result = new List<int>();
            try
            {
                foreach (var f in Directory.GetFiles(_dir, "*.json"))
                {
                    if (Path.GetFileName(f).Equals("active.json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var j = TryRead(f);
                    if (j == null) continue;
                    if (IsAlive(j)) result.Add((int)(j["pid"] ?? 0));
                    else { try { File.Delete(f); } catch { } }   // 죽은 인스턴스 파일 정리
                }
            }
            catch { }
            return result;
        }

        private static bool IsAlive(JObject j)
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
            catch { return false; }   // 해당 PID 없음
        }

        private string SafeTitle()
        {
            try { return _title() ?? ""; } catch { return ""; }
        }

        private static string SafeName(Process p)
        {
            try { return p.ProcessName; } catch { return ""; }
        }

        private static JObject? TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JObject.Parse(File.ReadAllText(path));
            }
            catch { return null; }   // 동시쓰기 중 부분파일 → 무시
        }

        private static void WriteAtomic(string path, string content)
        {
            try
            {
                string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                File.WriteAllText(tmp, content);
                try { File.Copy(tmp, path, true); }
                finally { try { File.Delete(tmp); } catch { } }
            }
            catch { /* 일시적 IO 충돌 — 다음 하트비트에서 재시도 */ }
        }
    }
}
