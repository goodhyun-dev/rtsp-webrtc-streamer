using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace RtspWebrtcStreamer.Models
{
    /// <summary>
    /// 모든 채널이 함께 쓰는 서버 설정: 접속 계정, RTSP 포트, 전송 방식, WebRTC.
    /// 서버는 하나만 뜨고, 채널은 경로 이름(rtsp://IP:포트/경로)으로 구분된다.
    /// </summary>
    public class ServerSettings : INotifyPropertyChanged
    {
        /// <summary>앱 전체에서 쓰는 단일 인스턴스.</summary>
        public static ServerSettings Current { get; set; } = new ServerSettings();

        private string _username = "";
        private string _password = "";
        private int _rtspPort = 8554;
        private RtspTransport _transport = RtspTransport.TcpUdp;
        private bool _webRtcEnabled;
        private int _webRtcPort = 8889;

        // ----- 접속 계정 (ID 가 비어 있으면 누구나 접속) -----

        public string Username
        {
            get => _username;
            set { if (Set(ref _username, value ?? "")) OnPropertyChanged(nameof(HasAuth)); }
        }

        [XmlIgnore]
        public string Password { get => _password; set => Set(ref _password, value ?? ""); }

        /// <summary>저장용: Windows DPAPI(현재 사용자)로 암호화한 비밀번호.</summary>
        public string EncryptedPassword
        {
            get
            {
                if (string.IsNullOrEmpty(_password)) return "";
                var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(_password), null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(data);
            }
            set
            {
                if (string.IsNullOrEmpty(value)) { _password = ""; return; }
                try
                {
                    var data = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
                    _password = Encoding.UTF8.GetString(data);
                }
                catch
                {
                    _password = ""; // 다른 사용자/PC에서 저장된 값은 복호화 불가
                }
            }
        }

        [XmlIgnore]
        public bool HasAuth => !string.IsNullOrEmpty(_username);

        // ----- 서버 -----

        public int RtspPort { get => _rtspPort; set => Set(ref _rtspPort, value); }

        public RtspTransport Transport { get => _transport; set => Set(ref _transport, value); }

        public bool WebRtcEnabled { get => _webRtcEnabled; set => Set(ref _webRtcEnabled, value); }

        public int WebRtcPort { get => _webRtcPort; set => Set(ref _webRtcPort, value); }

        /// <summary>서버 시작 시점의 값을 고정하기 위한 복사본.</summary>
        public ServerSettings Clone() => new ServerSettings
        {
            _username = _username,
            _password = _password,
            _rtspPort = _rtspPort,
            _transport = _transport,
            _webRtcEnabled = _webRtcEnabled,
            _webRtcPort = _webRtcPort
        };

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
