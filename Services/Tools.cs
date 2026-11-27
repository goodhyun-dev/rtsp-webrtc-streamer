using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace test_rtsp.Services
{
    /// <summary>외부 실행 파일(ffmpeg, mediamtx) 위치 탐색.</summary>
    internal static class ToolLocator
    {
        public static string Find(string exeName)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new List<string>
            {
                Path.Combine(baseDir, "tools", exeName),
                Path.Combine(baseDir, exeName),
            };
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            candidates.AddRange(pathEnv.Split(Path.PathSeparator)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => { try { return Path.Combine(p.Trim(), exeName); } catch { return null; } })
                .Where(p => p != null));
            return candidates.FirstOrDefault(File.Exists);
        }
    }

    internal static class NetHelper
    {
        private static readonly object Sync = new object();
        private static readonly HashSet<int> ReservedUdp = new HashSet<int>();
        private static int _nextUdp = 30000;

        /// <summary>이 PC의 IPv4 주소 목록 (게이트웨이가 있는 인터페이스 우선).</summary>
        public static List<string> GetLocalIPv4()
        {
            var result = new List<(string ip, bool gw)>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                bool hasGw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                            && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        result.Add((ua.Address.ToString(), hasGw));
            }
            var list = result.OrderByDescending(r => r.gw).Select(r => r.ip).Distinct().ToList();
            list.Add("127.0.0.1");
            return list;
        }

        public static bool IsTcpPortFree(int port)
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, port);
                l.Start();
                l.Stop();
                return true;
            }
            catch { return false; }
        }

        private static bool IsUdpPortFree(int port)
        {
            try { using (new UdpClient(port)) return true; }
            catch { return false; }
        }

        /// <summary>
        /// 사용 가능한 UDP 포트를 예약. pair=true면 짝수 포트 p와 p+1 (RTP/RTCP) 을 함께 예약하고 p를 반환.
        /// </summary>
        public static int ReserveUdp(bool pair)
        {
            lock (Sync)
            {
                for (int i = 0; i < 15000; i++)
                {
                    int p = _nextUdp;
                    _nextUdp += 2;
                    if (_nextUdp > 60000) _nextUdp = 30000;

                    if (ReservedUdp.Contains(p) || (pair && ReservedUdp.Contains(p + 1))) continue;
                    if (!IsUdpPortFree(p)) continue;
                    if (pair && !IsUdpPortFree(p + 1)) continue;

                    ReservedUdp.Add(p);
                    if (pair) ReservedUdp.Add(p + 1);
                    return p;
                }
            }
            throw new InvalidOperationException("사용 가능한 UDP 포트를 찾을 수 없습니다.");
        }

        public static void ReleaseUdp(int port, bool pair)
        {
            lock (Sync)
            {
                ReservedUdp.Remove(port);
                if (pair) ReservedUdp.Remove(port + 1);
            }
        }
    }
}
