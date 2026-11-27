using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RtspWebrtcStreamer.Models;

namespace RtspWebrtcStreamer.Services
{
    /// <summary>
    /// 채널 1개 송출기: ffmpeg 로 동영상 파일을 실시간 속도로 읽어
    /// 공통 RTSP 서버의 /{채널 이름} 경로로 publish 한다.
    /// </summary>
    internal sealed class StreamRunner
    {
        private readonly StreamItem _item;
        private readonly RtspServer _server;
        private readonly Action<Action> _ui;      // UI 스레드에서 실행
        private readonly Action<string> _log;
        private readonly Action _ended;           // 송출이 끝났을 때 (서버 정리 판단용, UI 스레드에서 호출)

        private Process _ff;
        private int _stopping;   // 0 = 동작 중, 1 = 정지 처리됨 (Interlocked)

        public StreamRunner(StreamItem item, RtspServer server, Action<Action> ui, Action<string> log, Action ended)
        {
            _item = item;
            _server = server;
            _ui = ui;
            _log = log;
            _ended = ended;
        }

        public StreamItem Item => _item;

        public async Task StartAsync(ServerSettings settings)
        {
            Interlocked.Exchange(ref _stopping, 0);
            SetState(StreamState.Starting, "");
            try
            {
                string ffExe = ToolLocator.Find("ffmpeg.exe")
                    ?? throw new InvalidOperationException("ffmpeg.exe 를 찾을 수 없습니다. (tools 폴더 확인)");
                if (_item.SourceType == SourceType.File)
                {
                    if (!File.Exists(_item.FilePath))
                        throw new InvalidOperationException("동영상 파일이 없습니다: " + _item.FilePath);
                }
                else // Rtsp
                {
                    if (string.IsNullOrWhiteSpace(_item.FilePath) ||
                        !_item.FilePath.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("가져올 RTSP 주소를 입력하세요 (rtsp:// 로 시작).");
                }

                // 1) 공통 서버 (꺼져 있으면 켬)
                string err = await _server.EnsureStartedAsync(settings);
                if (err != null) throw new InvalidOperationException(err);
                ThrowIfStopped();

                var active = _server.Active
                    ?? throw new InvalidOperationException("RTSP 서버가 실행 중이 아닙니다.");

                // 2) 동영상 publish
                string publishUrl = $"rtsp://{_server.PublishUser}:{_server.PublishPass}@127.0.0.1:{active.RtspPort}/{_item.Name}";
                var ff = StartFfmpeg(ffExe, BuildFfmpegArgs(publishUrl, active));
                _ff = ff;

                // ffmpeg 가 바로 죽는지(코덱 문제 등) 잠깐 확인
                await Task.Delay(1500);
                ThrowIfStopped();
                if (ff.HasExited)
                    throw new InvalidOperationException("ffmpeg 가 바로 종료되었습니다. 코덱 문제라면 '다시 인코딩'을 켜 보세요.");

                ff.Exited += (s, e) => OnFfmpegExited((Process)s);
                if (ff.HasExited)
                    throw new InvalidOperationException("ffmpeg 가 예기치 않게 종료되었습니다. 로그를 확인하세요.");

                SetState(StreamState.Running, $"{DateTime.Now:HH:mm:ss}부터 송출");
                _log("시작됨 → " + _item.RtspUrl + (active.WebRtcEnabled ? "  |  WebRTC " + _item.WebRtcUrl : ""));
            }
            catch (Exception ex)
            {
                KillFfmpeg();
                if (Interlocked.Exchange(ref _stopping, 1) == 1)
                {
                    // 시작 도중 사용자가 정지함
                    NotifyEnded();
                    return;
                }
                SetState(StreamState.Error, ex.Message);
                _log("오류: " + ex.Message);
                NotifyEnded();
            }
        }

        public void Stop()
        {
            Interlocked.Exchange(ref _stopping, 1);
            KillFfmpeg();
            SetState(StreamState.Stopped, "");
        }

        /// <summary>서버가 멈췄을 때 (MainWindow 가 호출).</summary>
        public void FailBecauseServerDied(string message)
        {
            if (Interlocked.Exchange(ref _stopping, 1) == 1) return;
            KillFfmpeg();
            SetState(StreamState.Error, message);
        }

        // ------------------------------------------------------------------

        private string BuildFfmpegArgs(string publishUrl, ServerSettings server)
        {
            var a = new StringBuilder();
            a.Append("-hide_banner -loglevel warning -nostdin ");

            if (_item.SourceType == SourceType.Rtsp)
            {
                // 다른 RTSP 에서 가져오기(리레이): 이미 실시간이므로 -re/루프 없음, TCP 로 안정적으로 받음
                a.Append("-rtsp_transport tcp ");
                a.Append("-i ").Append(Quote(_item.FilePath)).Append(' ');
            }
            else
            {
                // 로컬 파일: 실시간 속도로 읽고, 필요하면 무한 반복
                a.Append("-re ");
                if (_item.Loop) a.Append("-stream_loop -1 ");
                a.Append("-i ").Append(Quote(_item.FilePath)).Append(' ');
            }
            a.Append("-map 0:v:0 -map 0:a:0? ");

            // 영상: 선택한 코덱으로 변환 (Copy면 원본 그대로)
            switch (_item.VideoCodec)
            {
                case VideoCodec.H264:
                    a.Append("-c:v libx264 -preset veryfast -tune zerolatency -profile:v main -pix_fmt yuv420p -bf 0 -g 50 ");
                    break;
                case VideoCodec.H265:
                    a.Append("-c:v libx265 -preset veryfast -tune zerolatency -pix_fmt yuv420p -g 50 ");
                    break;
                case VideoCodec.Mpeg4:
                    a.Append("-c:v mpeg4 -q:v 5 -g 50 ");
                    break;
                default: // Copy
                    a.Append("-c:v copy ");
                    break;
            }

            // 음성: WebRTC 는 AAC 미지원 → Opus 로 변환
            if (server.WebRtcEnabled)
                a.Append("-c:a libopus -ar 48000 -ac 2 -b:a 128k ");
            else if (_item.Reencode)
                a.Append("-c:a aac -ar 48000 -b:a 128k ");
            else
                a.Append("-c:a copy ");

            // localhost 송출 전송방식: 서버가 허용하는 방식과 일치해야 함
            string pubTransport = server.Transport == RtspTransport.Udp ? "udp" : "tcp";
            a.Append("-f rtsp -rtsp_transport ").Append(pubTransport).Append(' ');
            a.Append(Quote(publishUrl));
            return a.ToString();
        }

        private Process StartFfmpeg(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log("[ffmpeg] " + e.Data); };
            p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log("[ffmpeg] " + e.Data); };
            p.Start();
            JobObject.Add(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        private void OnFfmpegExited(Process p)
        {
            if (Interlocked.Exchange(ref _stopping, 1) == 1) return;
            int code;
            try { code = p.ExitCode; } catch { code = -1; }
            KillFfmpeg();

            if (code == 0 && !_item.Loop)
            {
                SetState(StreamState.Finished, "");
                _log("재생이 끝났습니다.");
            }
            else
            {
                SetState(StreamState.Error, $"ffmpeg 가 멈췄습니다 (code {code}). 로그를 확인하세요.");
                _log($"ffmpeg 가 종료되었습니다. (code {code})");
            }
            NotifyEnded();
        }

        private void NotifyEnded() => _ui(_ended);

        private void ThrowIfStopped()
        {
            if (Volatile.Read(ref _stopping) == 1) throw new OperationCanceledException();
        }

        private void KillFfmpeg()
        {
            var p = Interlocked.Exchange(ref _ff, null);
            if (p == null) return;
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { }
            try { p.Dispose(); } catch { }
        }

        private void SetState(StreamState state, string text)
        {
            _ui(() =>
            {
                _item.State = state;
                _item.StatusText = text;
            });
        }

        /// <summary>명령줄 인자 따옴표 처리.</summary>
        private static string Quote(string s) => "\"" + (s ?? "").Replace("\"", "\\\"") + "\"";
    }
}
