using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using test_rtsp.Models;

namespace test_rtsp.Services
{
    /// <summary>스트림 목록 저장/불러오기 (%LOCALAPPDATA%\test_rtsp\streams.xml).</summary>
    internal static class StreamStore
    {
        public static string DataDir
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "test_rtsp");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string FilePath => Path.Combine(DataDir, "streams.xml");

        public static List<StreamItem> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<StreamItem>();
                using (var fs = File.OpenRead(FilePath))
                {
                    var list = (List<StreamItem>)new XmlSerializer(typeof(List<StreamItem>)).Deserialize(fs);
                    foreach (var it in list) it.MigrateLegacy();   // 예전 Reencode → VideoCodec
                    return list;
                }
            }
            catch
            {
                return new List<StreamItem>();
            }
        }

        public static void Save(IEnumerable<StreamItem> items)
        {
            var list = new List<StreamItem>(items);
            string tmp = FilePath + ".tmp";
            using (var fs = File.Create(tmp))
                new XmlSerializer(typeof(List<StreamItem>)).Serialize(fs, list);
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }

        // ----- 공통 서버 설정: 계정, 포트, 전송 방식, WebRTC (server.xml) -----

        private static string ServerPath => Path.Combine(DataDir, "server.xml");

        public static bool ServerExists => File.Exists(ServerPath);

        /// <summary>
        /// 이전 버전의 공통 계정 파일(account.xml)에서 ID와 암호화된 PW를 읽는다. 없으면 null.
        /// </summary>
        public static Tuple<string, string> LoadLegacyAccount()
        {
            try
            {
                string path = Path.Combine(DataDir, "account.xml");
                if (!File.Exists(path)) return null;
                var doc = System.Xml.Linq.XDocument.Load(path);
                string user = (string)doc.Root?.Element("Username");
                string pass = (string)doc.Root?.Element("EncryptedPassword");
                return string.IsNullOrEmpty(user) ? null : Tuple.Create(user, pass ?? "");
            }
            catch
            {
                return null;
            }
        }

        public static ServerSettings LoadServer()
        {
            try
            {
                if (!File.Exists(ServerPath)) return new ServerSettings();
                using (var fs = File.OpenRead(ServerPath))
                    return (ServerSettings)new XmlSerializer(typeof(ServerSettings)).Deserialize(fs);
            }
            catch
            {
                return new ServerSettings();
            }
        }

        public static void SaveServer(ServerSettings c)
        {
            string tmp = ServerPath + ".tmp";
            using (var fs = File.Create(tmp))
                new XmlSerializer(typeof(ServerSettings)).Serialize(fs, c);
            if (File.Exists(ServerPath)) File.Delete(ServerPath);
            File.Move(tmp, ServerPath);
        }
    }
}
