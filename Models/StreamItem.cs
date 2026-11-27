using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;

namespace test_rtsp.Models
{
    public enum RtspTransport
    {
        Tcp,
        Udp,
        TcpUdp
    }

    public enum StreamState
    {
        Stopped,
        Starting,
        Running,
        Finished,
        Error
    }

    /// <summary>송출 시 영상 코덱. Copy = 변환 없이 원본 그대로.</summary>
    public enum VideoCodec
    {
        Copy,    // -c:v copy (원본 그대로)
        H264,    // libx264
        H265,    // libx265
        Mpeg4    // mpeg4 (MPEG-4 Part 2)
    }

    /// <summary>입력 소스 종류.</summary>
    public enum SourceType
    {
        File,    // 로컬 동영상 파일
        Rtsp     // 다른 RTSP 주소에서 가져오기(리레이)
    }

    /// <summary>
    /// 채널 하나 = 동영상 파일 하나. 공통 RTSP 서버의 경로(rtsp://IP:포트/{Name})로 송출된다.
    /// 포트, 전송 방식, 계정, WebRTC 는 <see cref="ServerSettings"/> 에서 모든 채널이 공유한다.
    /// </summary>
    public class StreamItem : INotifyPropertyChanged
    {
        /// <summary>URL 표시용 호스트 IP (모든 채널 공통).</summary>
        public static string HostIp { get; set; } = "127.0.0.1";

        private string _name = "stream1";
        private string _filePath = "";
        private SourceType _sourceType = SourceType.File;
        private bool _loop = true;
        private VideoCodec _videoCodec = VideoCodec.Copy;
        private int _channelColor = -1;
        private StreamState _state = StreamState.Stopped;
        private string _statusText = "";

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>채널 식별색 (컬러 바 색상 인덱스). -1이면 미지정.</summary>
        public int ChannelColor { get => _channelColor; set => Set(ref _channelColor, value); }

        /// <summary>RTSP 경로 이름 (rtsp://ip:port/{Name}). 채널끼리 겹치면 안 됨.</summary>
        public string Name { get => _name; set { if (Set(ref _name, value)) RaiseServer(); } }

        /// <summary>입력 소스(파일 경로 또는 RTSP 주소).</summary>
        public string FilePath { get => _filePath; set { if (Set(ref _filePath, value)) OnPropertyChanged(nameof(FileName)); } }

        /// <summary>입력 소스 종류: 파일 또는 다른 RTSP 가져오기.</summary>
        public SourceType SourceType
        {
            get => _sourceType;
            set
            {
                if (Set(ref _sourceType, value))
                {
                    OnPropertyChanged(nameof(IsFileSource));
                    OnPropertyChanged(nameof(IsRtspSource));
                    OnPropertyChanged(nameof(FileName));
                }
            }
        }

        [XmlIgnore] public bool IsFileSource => _sourceType == SourceType.File;
        [XmlIgnore] public bool IsRtspSource => _sourceType == SourceType.Rtsp;

        public bool Loop { get => _loop; set => Set(ref _loop, value); }

        /// <summary>송출 영상 코덱. Copy면 변환 없이 원본 그대로, 그 외는 재인코딩(CPU 사용).</summary>
        public VideoCodec VideoCodec { get => _videoCodec; set => Set(ref _videoCodec, value); }

        /// <summary>변환(재인코딩) 여부. Copy 가 아니면 변환함.</summary>
        [XmlIgnore]
        public bool Reencode => _videoCodec != VideoCodec.Copy;

        // ----- 런타임 상태 (저장 안 함) -----

        [XmlIgnore]
        public StreamState State
        {
            get => _state;
            set
            {
                if (Set(ref _state, value))
                {
                    OnPropertyChanged(nameof(IsEditable));
                    OnPropertyChanged(nameof(IsActive));
                }
            }
        }

        [XmlIgnore]
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

        [XmlIgnore]
        public bool IsActive => _state == StreamState.Starting || _state == StreamState.Running;

        [XmlIgnore]
        public bool IsEditable => !IsActive;

        [XmlIgnore]
        public string FileName => _sourceType == SourceType.Rtsp
            ? _filePath   // RTSP 는 주소를 그대로 표시
            : (string.IsNullOrEmpty(_filePath) ? "" : System.IO.Path.GetFileName(_filePath));

        // ----- 공통 서버 설정에서 오는 값 (표시용) -----

        [XmlIgnore]
        public bool HasAuth => ServerSettings.Current.HasAuth;

        [XmlIgnore]
        public bool WebRtcEnabled => ServerSettings.Current.WebRtcEnabled;

        [XmlIgnore]
        public string RtspUrl => $"rtsp://{HostIp}:{ServerSettings.Current.RtspPort}/{_name}";

        [XmlIgnore]
        public string WebRtcUrl => WebRtcEnabled ? $"http://{HostIp}:{ServerSettings.Current.WebRtcPort}/{_name}" : "";

        /// <summary>ID/PW를 포함한 RTSP URL (표시/복사용). 계정이 없으면 일반 주소와 같다.</summary>
        [XmlIgnore]
        public string RtspUrlWithAuth
        {
            get
            {
                var s = ServerSettings.Current;
                if (!s.HasAuth) return RtspUrl;
                return $"rtsp://{Uri.EscapeDataString(s.Username)}:{Uri.EscapeDataString(s.Password)}@{HostIp}:{s.RtspPort}/{_name}";
            }
        }

        /// <summary>공통 설정(IP, 포트, 계정, WebRTC)이 바뀌었을 때 표시 갱신.</summary>
        public void RaiseServer()
        {
            OnPropertyChanged(nameof(RtspUrl));
            OnPropertyChanged(nameof(RtspUrlWithAuth));
            OnPropertyChanged(nameof(WebRtcUrl));
            OnPropertyChanged(nameof(WebRtcEnabled));
            OnPropertyChanged(nameof(HasAuth));
        }

        /// <summary>예전 저장 파일 호환: 불러온 뒤 <c>&lt;Reencode&gt;true&lt;/Reencode&gt;</c>를 H.264로 변환한다.</summary>
        public void MigrateLegacy()
        {
            if (LegacyReencode == true && _videoCodec == VideoCodec.Copy)
                _videoCodec = VideoCodec.H264;
        }

        /// <summary>예전 버전(bool Reencode) 호환: 읽기만 하고 저장하지 않음.</summary>
        [XmlElement("Reencode")]
        public bool? LegacyReencode { get; set; }
        public bool ShouldSerializeLegacyReencode() => false;

        // ----- 예전 버전(채널별 계정/포트) 호환: 읽기만 하고 저장하지 않음 -----

        [XmlElement("Username")]
        public string LegacyUsername { get; set; }
        public bool ShouldSerializeLegacyUsername() => false;

        [XmlElement("EncryptedPassword")]
        public string LegacyEncryptedPassword { get; set; }
        public bool ShouldSerializeLegacyEncryptedPassword() => false;

        [XmlElement("Transport")]
        public RtspTransport? LegacyTransport { get; set; }
        public bool ShouldSerializeLegacyTransport() => false;

        [XmlElement("WebRtcEnabled")]
        public bool? LegacyWebRtcEnabled { get; set; }
        public bool ShouldSerializeLegacyWebRtcEnabled() => false;

        [XmlElement("RtspPort")]
        public int? LegacyRtspPort { get; set; }
        public bool ShouldSerializeLegacyRtspPort() => false;

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
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
