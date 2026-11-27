using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using RtspWebrtcStreamer.Models;

namespace RtspWebrtcStreamer.Services
{
    /// <summary>
    /// 모든 채널이 함께 쓰는 RTSP(+WebRTC) 서버 (mediamtx 프로세스 1개).
    /// 첫 채널이 시작될 때 켜지고, 마지막 채널이 멈추면 꺼진다.
    /// 채널은 경로 이름으로 구분: rtsp://IP:포트/{채널 이름}
    /// </summary>
    internal sealed class RtspServer
    {
        private readonly Action<string> _log;
        private readonly object _sync = new object();

        private Process _mtx;
        private Task<string> _startTask;
        private bool _stopping;
        private int _rtpPort = -1;
        private int _icePort = -1;
        private string _configPath;

        public RtspServer(Action<string> log) { _log = log; }

        /// <summary>서버가 비정상 종료됐을 때 (UI 스레드 아님).</summary>
        public event Action<string> Crashed;

        /// <summary>실행 중인 서버가 쓰고 있는 설정 (시작 시점 복사본).</summary>
        public ServerSettings Active { get; private set; }

        /// <summary>채널(ffmpeg)이 publish 할 때 쓰는 내부 계정. localhost 전용.</summary>
        public string PublishUser { get; private set; }
        public string PublishPass { get; private set; }

        public bool IsRunning
        {
            get { lock (_sync) { try { return _mtx != null && !_mtx.HasExited; } catch { return false; } } }
        }

        /// <summary>
        /// 서버가 꺼져 있으면 켠다. 여러 채널이 동시에 불러도 한 번만 시작한다.
        /// 성공하면 null, 실패하면 오류 메시지.
        /// </summary>
        public Task<string> EnsureStartedAsync(ServerSettings settings)
        {
            lock (_sync)
            {
                if (_startTask != null)
                {
                    if (!_startTask.IsCompleted) return _startTask;                          // 시작 중
                    if (_startTask.Result == null && IsRunningUnlocked()) return _startTask;  // 실행 중
                }
                _startTask = StartCoreAsync(settings.Clone());
                return _startTask;
            }
        }

        private bool IsRunningUnlocked()
        {
            try { return _mtx != null && !_mtx.HasExited; } catch { return false; }
        }

        private async Task<string> StartCoreAsync(ServerSettings s)
        {
            try
            {
                _stopping = false;
                string exe = ToolLocator.Find("mediamtx.exe")
                    ?? throw new InvalidOperationException("mediamtx.exe 를 찾을 수 없습니다. (tools 폴더 확인)");

                if (!NetHelper.IsTcpPortFree(s.RtspPort))
                    throw new InvalidOperationException($"RTSP 포트 {s.RtspPort} 를 다른 프로그램이 쓰고 있습니다.");
                if (s.WebRtcEnabled && !NetHelper.IsTcpPortFree(s.WebRtcPort))
                    throw new InvalidOperationException($"WebRTC 포트 {s.WebRtcPort} 를 다른 프로그램이 쓰고 있습니다.");

                if (s.Transport != RtspTransport.Tcp) _rtpPort = NetHelper.ReserveUdp(pair: true);
                if (s.WebRtcEnabled) _icePort = NetHelper.ReserveUdp(pair: false);

                PublishUser = "pub_" + RandomToken(8);
                PublishPass = RandomToken(24);

                _configPath = Path.Combine(StreamStore.DataDir, "run", "server.yml");
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath));
                File.WriteAllText(_configPath, BuildConfig(s), new UTF8Encoding(false));

                var p = new Process
                {
                    StartInfo = new ProcessStartInfo(exe, "\"" + _configPath + "\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
                    },
                    EnableRaisingEvents = true
                };
                p.OutputDataReceived += (o, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log("[server] " + e.Data); };
                p.ErrorDataReceived += (o, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log("[server] " + e.Data); };
                p.Start();
                JobObject.Add(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                lock (_sync) _mtx = p;

                if (!await WaitForTcpPort(s.RtspPort, p, TimeSpan.FromSeconds(8)))
                    throw new InvalidOperationException("RTSP 서버가 시작되지 않았습니다. 로그를 확인하세요.");

                p.Exited += (o, e) => OnExited();
                if (p.HasExited) throw new InvalidOperationException("RTSP 서버가 바로 종료되었습니다. 로그를 확인하세요.");

                Active = s;
                _log($"서버 시작: RTSP :{s.RtspPort}" + (s.WebRtcEnabled ? $", WebRTC :{s.WebRtcPort}" : ""));
                return null;
            }
            catch (Exception ex)
            {
                Cleanup();
                return ex.Message;
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                if (_mtx == null) return;
                _stopping = true;
            }
            Cleanup();
            _log("서버 정지");
        }

        private void OnExited()
        {
            if (_stopping) return;
            Cleanup();
            Crashed?.Invoke("RTSP 서버가 멈췄습니다. 로그를 확인하세요.");
        }

        private void Cleanup()
        {
            Process p;
            lock (_sync)
            {
                p = _mtx;
                _mtx = null;
                _startTask = null;
                Active = null;
            }
            if (p != null)
            {
                try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { }
                try { p.Dispose(); } catch { }
            }
            if (_rtpPort > 0) { NetHelper.ReleaseUdp(_rtpPort, pair: true); _rtpPort = -1; }
            if (_icePort > 0) { NetHelper.ReleaseUdp(_icePort, pair: false); _icePort = -1; }
            // 설정 파일에 PW 가 평문으로 들어 있으므로 삭제
            try { if (_configPath != null && File.Exists(_configPath)) File.Delete(_configPath); } catch { }
        }

        private string BuildConfig(ServerSettings s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# RtspWebrtcStreamer 가 자동 생성한 파일입니다.");
            sb.AppendLine("logLevel: info");
            sb.AppendLine("logDestinations: [stdout]");

            // --- 인증 ---
            sb.AppendLine("authMethod: internal");
            sb.AppendLine("authInternalUsers:");
            // 채널 송출용 내부 계정: localhost 에서만 publish
            sb.AppendLine($"  - user: {Y(PublishUser)}");
            sb.AppendLine($"    pass: {Y(PublishPass)}");
            sb.AppendLine("    ips: ['127.0.0.1', '::1']");
            sb.AppendLine("    permissions:");
            sb.AppendLine("      - action: publish");
            // 시청 계정 (모든 채널 공통)
            if (s.HasAuth)
            {
                sb.AppendLine($"  - user: {Y(s.Username)}");
                sb.AppendLine($"    pass: {Y(s.Password)}");
            }
            else
            {
                sb.AppendLine("  - user: any");
                sb.AppendLine("    pass: ''");
            }
            sb.AppendLine("    ips: []");
            sb.AppendLine("    permissions:");
            sb.AppendLine("      - action: read");

            // --- RTSP ---
            string transports;
            switch (s.Transport)
            {
                case RtspTransport.Tcp: transports = "[tcp]"; break;
                case RtspTransport.Udp: transports = "[udp]"; break;
                default: transports = "[udp, tcp]"; break;
            }
            sb.AppendLine("rtsp: true");
            sb.AppendLine($"rtspTransports: {transports}");
            sb.AppendLine("rtspEncryption: \"no\"");
            sb.AppendLine("rtspAuthMethods: [basic, digest]");
            sb.AppendLine($"rtspAddress: :{s.RtspPort}");
            if (_rtpPort > 0)
            {
                sb.AppendLine($"rtpAddress: :{_rtpPort}");
                sb.AppendLine($"rtcpAddress: :{_rtpPort + 1}");
            }

            // --- WebRTC ---
            if (s.WebRtcEnabled)
            {
                sb.AppendLine("webrtc: true");
                sb.AppendLine($"webrtcAddress: :{s.WebRtcPort}");
                sb.AppendLine("webrtcEncryption: false");
                sb.AppendLine($"webrtcLocalUDPAddress: :{_icePort}");
                sb.AppendLine("webrtcLocalTCPAddress: ''");
                sb.AppendLine("webrtcIPsFromInterfaces: true");
            }
            else
            {
                sb.AppendLine("webrtc: false");
            }

            // --- 쓰지 않는 기능은 끔 (포트 충돌 방지) ---
            sb.AppendLine("rtmp: false");
            sb.AppendLine("hls: false");
            sb.AppendLine("srt: false");
            sb.AppendLine("moq: false");
            sb.AppendLine("api: false");
            sb.AppendLine("metrics: false");
            sb.AppendLine("pprof: false");
            sb.AppendLine("playback: false");

            // 어떤 경로 이름이든 채널이 publish 하면 열림
            sb.AppendLine("paths:");
            sb.AppendLine("  all_others:");
            sb.AppendLine("    source: publisher");
            return sb.ToString();
        }

        private static async Task<bool> WaitForTcpPort(int port, Process owner, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                try { if (owner.HasExited) return false; } catch { return false; }
                try
                {
                    using (var c = new TcpClient())
                    {
                        var t = c.ConnectAsync("127.0.0.1", port);
                        if (await Task.WhenAny(t, Task.Delay(300)) == t && c.Connected) return true;
                    }
                }
                catch { }
                await Task.Delay(200);
            }
            return false;
        }

        private static string RandomToken(int len)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var bytes = new byte[len];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(len);
            foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

        /// <summary>YAML 작은따옴표 문자열.</summary>
        private static string Y(string s) => "'" + (s ?? "").Replace("'", "''") + "'";
    }
}
