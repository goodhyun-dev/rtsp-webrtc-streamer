using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using test_rtsp.Services;

namespace test_rtsp
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 예상 못 한 UI 오류로 앱이 통째로 꺼지면 송출 중인 채널도 모두 끊기므로,
            // 오류를 기록하고 알린 뒤 계속 실행한다.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            base.OnStartup(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                File.AppendAllText(Path.Combine(StreamStore.DataDir, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { /* 기록 실패는 무시 */ }

            MessageBox.Show(MainWindow, "작업 중 오류가 났습니다. 프로그램은 계속 실행됩니다.\n\n" + e.Exception.Message,
                "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
