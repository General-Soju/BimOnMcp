using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BimOnMcpShared
{
    /// <summary>
    /// Named Pipe 서버 루프.
    /// 각 플러그인이 PipeName과 요청 핸들러를 제공하면 공통 루프를 실행합니다.
    /// </summary>
    public class PipeServer
    {
        private readonly string _pipeName;
        private readonly Func<JObject, CancellationToken, Task<string>> _handleRequest;

        private static readonly Encoding Utf8NoBom =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public PipeServer(
            string pipeName,
            Func<JObject, CancellationToken, Task<string>> handleRequest)
        {
            _pipeName      = pipeName;
            _handleRequest = handleRequest;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut,
                    1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    Log($"[{_pipeName}] 대기 중...");
                    await pipe.WaitForConnectionAsync(ct);
                    Log($"[{_pipeName}] 연결됨");
                    await HandleAsync(pipe, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log($"[{_pipeName}] 오류: {ex.Message}"); }
                finally { try { pipe.Dispose(); } catch { } }
            }
        }

        private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
        {
            using var reader = new StreamReader(pipe, Utf8NoBom, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, Utf8NoBom, 1024, leaveOpen: true)
                { AutoFlush = false };

            string? line;
            try
            {
#if NET48
                line = await reader.ReadLineAsync();
#else
                line = await reader.ReadLineAsync(ct);
#endif
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log($"읽기 오류: {ex.Message}"); return; }

            if (string.IsNullOrWhiteSpace(line)) return;

            JObject req;
            try { req = JObject.Parse(line); }
            catch (Exception ex) { Log($"JSON 파싱 오류: {ex.Message}"); return; }

            string result = await _handleRequest(req, ct);

            try
            {
                await writer.WriteLineAsync(
                    JsonConvert.SerializeObject(new { result }));
#if NET48
                await writer.FlushAsync();
#else
                await writer.FlushAsync(ct);
#endif
                pipe.WaitForPipeDrain();
            }
            catch (Exception ex) { Log($"전송 오류: {ex.Message}"); }
        }

        private static void Log(string msg) =>
            System.Diagnostics.Debug.WriteLine(
                $"[BimOnPipe {DateTime.Now:HH:mm:ss}] {msg}");
    }
}
