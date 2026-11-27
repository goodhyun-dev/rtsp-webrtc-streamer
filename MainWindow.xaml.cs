using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using test_rtsp.Models;
using test_rtsp.Services;
using test_rtsp.Themes;

namespace test_rtsp
{
    public partial class MainWindow : Window
    {
        private static readonly string[] VideoExts =
            { ".mp4", ".mkv", ".avi", ".mov", ".ts", ".m2ts", ".flv", ".wmv", ".webm", ".mpg", ".mpeg", ".m4v", ".h264", ".h265", ".264", ".265" };

        private static readonly Regex NameRegex = new Regex(@"^[A-Za-z0-9_\-.]+$");

        // MediaMTX 가 허용하는 ID/PW 문자
        private static readonly Regex CredRegex = new Regex(@"^[A-Za-z0-9!$()*+.;<=>\[\]^_\-,@#&]+$");

        private readonly ObservableCollection<StreamItem> _streams = new ObservableCollection<StreamItem>();
        private readonly Dictionary<string, StreamRunner> _runners = new Dictionary<string, StreamRunner>();
        private readonly RtspServer _server;
        private bool _syncingUi;

        public MainWindow()
        {
            InitializeComponent();

            _server = new RtspServer(Log);
            _server.Crashed += msg => Dispatcher.BeginInvoke(new Action(() => OnServerCrashed(msg)));

            // 공통 서버 설정
            bool serverSaved = StreamStore.ServerExists;
            ServerSettings.Current = StreamStore.LoadServer();

            // 채널 목록
            foreach (var s in StreamStore.Load())
            {
                if (s.ChannelColor < 0) s.ChannelColor = NextChannelColor();
                Track(s);
                _streams.Add(s);
            }
            StreamList.ItemsSource = _streams;
            _streams.CollectionChanged += (o, e) =>
            {
                if (e.NewItems != null) foreach (StreamItem s in e.NewItems) Track(s);
                UpdateSummary();
            };

            if (!serverSaved) MigrateLegacySettings();
            BindServerSettings();

            HostIpCombo.ItemsSource = NetHelper.GetLocalIPv4();
            HostIpCombo.SelectedIndex = 0;

            if (ToolLocator.Find("mediamtx.exe") == null || ToolLocator.Find("ffmpeg.exe") == null)
                Log("※ 경고: tools 폴더에 mediamtx.exe / ffmpeg.exe 가 필요합니다.");

            UpdateSummary();
            if (_streams.Count > 0) StreamList.SelectedIndex = 0;
            else BindEditor(null);
        }

        // ================= 공통 서버 설정 =================

        private void BindServerSettings()
        {
            var s = ServerSettings.Current;
            ServerStrip.DataContext = s;
            s.PropertyChanged += ServerSettings_PropertyChanged;

            _syncingUi = true;
            PwBox.Password = s.Password;
            PwText.Text = s.Password;
            TcpRadio.IsChecked = s.Transport == RtspTransport.Tcp;
            UdpRadio.IsChecked = s.Transport == RtspTransport.Udp;
            BothRadio.IsChecked = s.Transport == RtspTransport.TcpUdp;
            _syncingUi = false;
        }

        private void ServerSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            foreach (var item in _streams) item.RaiseServer();   // 주소, 자물쇠, WebRTC 표시 갱신
            try { StreamStore.SaveServer(ServerSettings.Current); }
            catch (Exception ex) { Log("설정 저장 실패: " + ex.Message); }
        }

        private void Transport_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingUi) return;
            var s = ServerSettings.Current;
            if (sender == TcpRadio) s.Transport = RtspTransport.Tcp;
            else if (sender == UdpRadio) s.Transport = RtspTransport.Udp;
            else s.Transport = RtspTransport.TcpUdp;
        }

        private void PwBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingUi) return;
            ServerSettings.Current.Password = PwBox.Password;
            _syncingUi = true;
            PwText.Text = PwBox.Password;
            _syncingUi = false;
        }

        private void PwText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingUi) return;
            ServerSettings.Current.Password = PwText.Text;
            _syncingUi = true;
            PwBox.Password = PwText.Text;
            _syncingUi = false;
        }

        private void ShowPw_Changed(object sender, RoutedEventArgs e)
        {
            bool show = ShowPwCheck.IsChecked == true;
            PwText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            PwBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        }

        private void HostIpCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HostIpCombo.SelectedItem is string ip)
            {
                StreamItem.HostIp = ip;
                foreach (var s in _streams) s.RaiseServer();
            }
        }

        /// <summary>
        /// 예전 버전(채널마다 서버/계정/포트)의 저장 내용을 공통 서버 설정으로 옮긴다. 처음 한 번만.
        /// </summary>
        private void MigrateLegacySettings()
        {
            var s = ServerSettings.Current;
            var notes = new List<string>();

            // 계정: 바로 전 버전의 account.xml → 없으면 더 예전 버전의 채널별 계정
            var legacyAccount = StreamStore.LoadLegacyAccount();
            var withAuth = _streams.FirstOrDefault(x => !string.IsNullOrEmpty(x.LegacyUsername));
            if (legacyAccount != null)
            {
                s.Username = legacyAccount.Item1;
                s.EncryptedPassword = legacyAccount.Item2;
                notes.Add($"계정(ID: {s.Username})");
            }
            else if (withAuth != null)
            {
                s.Username = withAuth.LegacyUsername;
                s.EncryptedPassword = withAuth.LegacyEncryptedPassword;
                notes.Add($"계정(ID: {s.Username})");
            }

            var ports = _streams.Where(x => x.LegacyRtspPort.HasValue).Select(x => x.LegacyRtspPort.Value).ToList();
            if (ports.Count > 0) { s.RtspPort = ports.Min(); notes.Add($"RTSP 포트 {s.RtspPort}"); }

            var tr = _streams.FirstOrDefault(x => x.LegacyTransport.HasValue);
            if (tr != null) s.Transport = tr.LegacyTransport.Value;

            if (_streams.Any(x => x.LegacyWebRtcEnabled == true)) { s.WebRtcEnabled = true; notes.Add("WebRTC 켜짐"); }

            if (legacyAccount != null || _streams.Any(x => x.LegacyRtspPort.HasValue || x.LegacyUsername != null))
            {
                MakeNamesUnique();
                StreamStore.SaveServer(s);
                Save(); // 채널별 계정/포트 항목 제거
                Log("이제 모든 채널이 서버 하나(포트 하나)를 함께 씁니다. 예전 설정에서 옮긴 값: "
                    + (notes.Count > 0 ? string.Join(", ", notes) : "없음"));
            }
        }

        /// <summary>경로 이름이 겹치는 채널 뒤에 _2, _3 … 을 붙인다.</summary>
        private void MakeNamesUnique()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in _streams)
            {
                string n = it.Name;
                for (int i = 2; seen.Contains(n); i++) n = it.Name + "_" + i;
                if (n != it.Name) { Log($"경로 이름이 겹쳐서 '{it.Name}' → '{n}' 로 바꿨습니다."); it.Name = n; }
                seen.Add(n);
            }
        }

        // ================= 상태 표시 =================

        private void Track(StreamItem s)
        {
            s.PropertyChanged += (o, e) =>
            {
                if (e.PropertyName == nameof(StreamItem.State)) UpdateSummary();
            };
        }

        private int NextChannelColor()
        {
            // 현재 가장 적게 쓰인 색을 고름
            var counts = new int[ChannelBrushConverter.Count];
            foreach (var s in _streams)
                if (s.ChannelColor >= 0) counts[s.ChannelColor % counts.Length]++;
            int best = 0;
            for (int i = 1; i < counts.Length; i++) if (counts[i] < counts[best]) best = i;
            return best;
        }

        private void UpdateSummary()
        {
            int total = _streams.Count;
            int live = _streams.Count(s => s.State == StreamState.Running);
            int err = _streams.Count(s => s.State == StreamState.Error);
            string text = total == 0 ? "채널 없음" : $"채널 {total}개 중 {live}개 송출 중";
            if (err > 0) text += $", {err}개 오류";
            SummaryText.Text = text;
            EmptyList.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 서버가 켜져 있으면 공통 설정 잠금 (실행 중인 서버에는 반영되지 않으므로)
            bool serverOn = _server.IsRunning || _streams.Any(s => s.IsActive);
            ServerFields.IsEnabled = !serverOn;
            AccountPanel.IsEnabled = !serverOn;
            LockHint.Visibility = serverOn ? Visibility.Visible : Visibility.Collapsed;
            AccountHint.Visibility = serverOn ? Visibility.Collapsed : Visibility.Visible;

            var active = _server.Active;
            ServerStateText.Text = active != null ? $"실행 중  :{active.RtspPort}" : serverOn ? "켜는 중" : "꺼짐";
            ServerDot.Fill = (System.Windows.Media.Brush)FindResource(active != null ? "Tally" : "Line");
        }

        private void LogToggle_Click(object sender, RoutedEventArgs e)
        {
            bool open = LogRow.Height.Value == 0;
            LogRow.Height = open ? new GridLength(190) : new GridLength(0);
            LogToggleText.Text = open ? "로그 숨기기" : "로그 보기";
            LogChevron.Text = open ? "" : "";
            if (open) LogBox.ScrollToEnd();
        }

        private StreamItem Selected => StreamList.SelectedItem as StreamItem;

        private List<StreamItem> SelectedItems => StreamList.SelectedItems.Cast<StreamItem>().ToList();

        // ================= 추가 / 삭제 =================

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "동영상 파일 선택 (여러 개 선택 가능)",
                Multiselect = true,
                Filter = "동영상 파일|" + string.Join(";", VideoExts.Select(x => "*" + x)) + "|모든 파일|*.*"
            };
            if (dlg.ShowDialog(this) == true) AddFiles(dlg.FileNames);
        }

        private void AddFiles(IEnumerable<string> files)
        {
            StreamItem last = null;
            foreach (var f in files)
            {
                var item = new StreamItem
                {
                    ChannelColor = NextChannelColor(),
                    FilePath = f,
                    Name = UniqueName(MakeName(Path.GetFileNameWithoutExtension(f)))
                };
                _streams.Add(item);
                last = item;
            }
            if (last != null)
            {
                StreamList.SelectedItem = last;
                StreamList.ScrollIntoView(last);
                Save();
            }
        }

        private string MakeName(string baseName)
        {
            string n = Regex.Replace(baseName ?? "", @"[^A-Za-z0-9_\-.]", "");
            if (string.IsNullOrEmpty(n)) n = "stream" + (_streams.Count + 1);
            return n;
        }

        /// <summary>다른 채널과 겹치지 않는 경로 이름.</summary>
        private string UniqueName(string baseName, StreamItem except = null)
        {
            bool Taken(string n) => _streams.Any(s => s != except && string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase));
            string name = baseName;
            for (int i = 2; Taken(name); i++) name = baseName + "_" + i;
            return name;
        }

        private void Duplicate_Click(object sender, RoutedEventArgs e)
        {
            var src = Selected;
            if (src == null) return;
            var item = new StreamItem
            {
                ChannelColor = NextChannelColor(),
                FilePath = src.FilePath,
                Name = UniqueName(src.Name),
                SourceType = src.SourceType,
                Loop = src.Loop,
                VideoCodec = src.VideoCodec
            };
            _streams.Add(item);
            StreamList.SelectedItem = item;
            Save();
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var items = SelectedItems;
            if (items.Count == 0) return;
            if (MessageBox.Show(this, $"{items.Count}개 채널을 삭제할까요?", "삭제",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            foreach (var it in items)
            {
                StopItem(it);
                _streams.Remove(it);
            }
            Save();
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files)) return;
            var videos = files.Where(f => File.Exists(f) &&
                                          VideoExts.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
            if (videos.Count == 0)
            {
                Log("지원하지 않는 파일 형식입니다.");
                return;
            }
            AddFiles(videos);
        }

        // ================= 시작 / 정지 =================

        private void Start_Click(object sender, RoutedEventArgs e) => StartItems(SelectedItems);

        private void StartAll_Click(object sender, RoutedEventArgs e) => StartItems(_streams.ToList());

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            foreach (var it in SelectedItems) StopItem(it);
        }

        private void StopAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var it in _streams) StopItem(it);
        }

        private void StartItems(List<StreamItem> items)
        {
            Save();
            // 서버가 꺼져 있을 때만 공통 설정 검사 (켜져 있으면 이미 쓰는 중인 설정)
            string serverErr = _server.IsRunning ? null : ValidateServer();
            var settings = ServerSettings.Current;

            foreach (var item in items)
            {
                if (item.IsActive) continue;
                string err = serverErr ?? Validate(item);
                if (err != null)
                {
                    item.State = StreamState.Error;
                    item.StatusText = err;
                    Log($"[{item.Name}] {err}");
                    continue;
                }

                var name = item.Name;
                var runner = new StreamRunner(item, _server,
                    a => Dispatcher.BeginInvoke(a),
                    msg => Log($"[{name}] {msg}"),
                    StopServerIfIdle);
                _runners[item.Id] = runner;
                item.State = StreamState.Starting; // 바로 표시 → 설정 잠금, 이름 중복 검사에 반영
                _ = runner.StartAsync(settings);
            }
        }

        private void StopItem(StreamItem item)
        {
            if (_runners.TryGetValue(item.Id, out var r))
            {
                r.Stop();
                _runners.Remove(item.Id);
            }
            else if (item.State != StreamState.Stopped)
            {
                item.State = StreamState.Stopped;
                item.StatusText = "";
            }
            // 상태 갱신(BeginInvoke)이 반영된 뒤에 서버를 끌지 판단
            Dispatcher.BeginInvoke(new Action(StopServerIfIdle));
        }

        /// <summary>송출 중인 채널이 하나도 없으면 서버를 끈다.</summary>
        private void StopServerIfIdle()
        {
            if (!_streams.Any(s => s.IsActive) && _server.IsRunning) _server.Stop();
            UpdateSummary();
        }

        private void OnServerCrashed(string message)
        {
            Log(message);
            foreach (var r in _runners.Values.ToList()) r.FailBecauseServerDied(message);
            _runners.Clear();
            UpdateSummary();
        }

        /// <summary>공통 서버 설정 검사. 문제 없으면 null.</summary>
        private static string ValidateServer()
        {
            var s = ServerSettings.Current;
            if (s.RtspPort < 1 || s.RtspPort > 65535) return "서버: RTSP 포트가 올바르지 않습니다.";
            if (s.WebRtcEnabled)
            {
                if (s.WebRtcPort < 1 || s.WebRtcPort > 65535) return "서버: WebRTC 포트가 올바르지 않습니다.";
                if (s.WebRtcPort == s.RtspPort) return "서버: RTSP 포트와 WebRTC 포트가 같습니다.";
            }
            if (s.HasAuth)
            {
                if (s.Username.Equals("any", StringComparison.OrdinalIgnoreCase)) return "접속 계정: 'any' 는 ID로 쓸 수 없습니다.";
                if (string.IsNullOrEmpty(s.Password)) return "접속 계정: ID를 입력했으면 PW도 입력하세요.";
                if (!CredRegex.IsMatch(s.Username) || !CredRegex.IsMatch(s.Password))
                    return "접속 계정: ID/PW에는 영문, 숫자와 ! $ ( ) * + . ; < = > [ ] ^ _ - , @ # & 만 쓸 수 있습니다.";
            }
            else if (!string.IsNullOrEmpty(s.Password))
            {
                return "접속 계정: PW를 쓰려면 ID도 입력하세요.";
            }
            return null;
        }

        private string Validate(StreamItem item)
        {
            if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath))
                return "동영상 파일을 선택하세요.";
            if (!NameRegex.IsMatch(item.Name ?? ""))
                return "경로 이름은 영문, 숫자, _ - . 만 쓸 수 있습니다.";
            var same = _streams.FirstOrDefault(s => s != item && string.Equals(s.Name, item.Name, StringComparison.OrdinalIgnoreCase));
            if (same != null)
                return $"경로 이름 '{item.Name}' 을 다른 채널도 쓰고 있습니다. 이름을 바꾸세요.";
            return null;
        }

        // ================= URL =================

        /// <summary>주소 상자에 포커스가 가면 전체 선택 → 바로 Ctrl+C 로 복사할 수 있게.</summary>
        private void UrlBox_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            (sender as TextBox)?.SelectAll();
        }

        /// <summary>첫 클릭 시 커서만 놓이지 않고 전체가 선택되도록.</summary>
        private void UrlBox_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                tb.Focus();   // GotKeyboardFocus 에서 SelectAll 실행
            }
        }

        private void OpenWebRtc_Click(object sender, RoutedEventArgs e)
        {
            var item = Selected;
            if (item == null || !item.WebRtcEnabled) return;
            try { Process.Start(new ProcessStartInfo(item.WebRtcUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Log("브라우저 실행 실패: " + ex.Message); }
        }

        // ================= 채널 편집 =================

        private void StreamList_SelectionChanged(object sender, SelectionChangedEventArgs e) => BindEditor(Selected);

        private void BindEditor(StreamItem item)
        {
            DetailPanel.DataContext = item;
            DetailPanel.Visibility = item == null ? Visibility.Collapsed : Visibility.Visible;
            EmptyDetail.Visibility = item == null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var item = Selected;
            if (item == null) return;
            var dlg = new OpenFileDialog
            {
                Title = "동영상 파일 선택",
                Filter = "동영상 파일|" + string.Join(";", VideoExts.Select(x => "*" + x)) + "|모든 파일|*.*"
            };
            if (dlg.ShowDialog(this) == true)
            {
                item.FilePath = dlg.FileName;
                if (item.Name.StartsWith("stream"))
                    item.Name = UniqueName(MakeName(Path.GetFileNameWithoutExtension(dlg.FileName)), item);
            }
        }

        // ================= 로그 / 저장 / 종료 =================

        private void Log(string msg)
        {
            string line = $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}";
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LogBox.AppendText(line);
                if (!msg.Contains("[server]")) LastLogText.Text = line.TrimEnd(); // 서버 상세 로그는 로그 창에서만
                if (LogBox.Text.Length > 300_000)
                    LogBox.Text = LogBox.Text.Substring(LogBox.Text.Length - 200_000);
                LogBox.ScrollToEnd();
            }));
        }

        private void Save()
        {
            try { StreamStore.Save(_streams); }
            catch (Exception ex) { Log("저장 실패: " + ex.Message); }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            Save();
            foreach (var r in _runners.Values.ToList()) r.Stop();
            _runners.Clear();
            _server.Stop();
        }
    }
}
