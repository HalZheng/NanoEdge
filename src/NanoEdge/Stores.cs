using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace NanoEdge
{
    public static class AppPaths
    {
        public static readonly string Roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NanoEdge");
        public static readonly string Local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NanoEdge");

        public static readonly string Settings = Path.Combine(Roaming, "settings.json");
        public static readonly string Session = Path.Combine(Roaming, "session.json");
        public static readonly string History = Path.Combine(Roaming, "history.json");
        public static readonly string Bookmarks = Path.Combine(Roaming, "bookmarks.json");
        public static readonly string Zoom = Path.Combine(Roaming, "zoom.json");
        public static readonly string Downloads = Path.Combine(Roaming, "downloads.json");
        public static readonly string ScriptIndex = Path.Combine(Roaming, "scripts.json");
        public static readonly string ScriptValues = Path.Combine(Roaming, "script-values.json");
        public static readonly string Scripts = Path.Combine(Roaming, "scripts");
        public static readonly string UserData = Path.Combine(Local, "WebView2");
        public static readonly string Native = Path.Combine(Local, "native");
        public static readonly string Logs = Path.Combine(Local, "logs");

        // 下载落盘目录。可写，便于自测把产物导到临时目录，不污染用户的 Downloads。
        public static string DownloadsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        public static void Ensure()
        {
            Directory.CreateDirectory(Roaming);
            Directory.CreateDirectory(Local);
            Directory.CreateDirectory(Logs);
        }
    }

    internal static class Json
    {
        private static readonly JavaScriptSerializer Serializer =
            new JavaScriptSerializer { MaxJsonLength = 20 * 1024 * 1024 };

        public static T Read<T>(string path, T fallback) where T : class
        {
            try
            {
                if (!File.Exists(path)) return fallback;
                var text = File.ReadAllText(path, Encoding.UTF8);
                return string.IsNullOrWhiteSpace(text) ? fallback : Serializer.Deserialize<T>(text) ?? fallback;
            }
            catch { return fallback; }
        }

        // Atomic write: never leave a half-written settings/session file behind.
        public static void Write(string path, object value)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var temp = path + ".tmp";
                File.WriteAllText(temp, Serializer.Serialize(value), Encoding.UTF8);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch { }
        }
    }

    public static class SettingsStore
    {
        public static string FilePath = AppPaths.Settings;
        public static bool Persist = true;

        public static AppSettings Load()
        {
            var settings = Json.Read(FilePath, new AppSettings());
            // 只有字段缺失才补默认；用户把起始页快捷方式全删掉是有效状态，
            // 不能每次启动又给长回来。
            if (settings.Shortcuts == null) settings.Shortcuts = Defaults();

            // v1 → v2：默认搜索引擎从 Bing 换成 Google。只动仍停留在旧默认值的配置，
            // 用户手动选过别的引擎就保留。不这样做的话，老 settings.json 会把新默认值盖掉。
            if (settings.SchemaVersion < 2)
            {
                if (string.IsNullOrWhiteSpace(settings.SearchEngine) ||
                    string.Equals(settings.SearchEngine, SearchEngineOptions.LegacyBing,
                        StringComparison.OrdinalIgnoreCase))
                    settings.SearchEngine = SearchEngineOptions.Google;
                settings.SchemaVersion = 2;
            }

            // v2 → v3：状态栏默认隐藏、语言默认跟随系统。老 settings.json 里写死的
            // true / zh-CN 会盖掉新默认值，看起来像没改，所以必须迁移一次。
            // 语言只动仍停留在旧默认值的配置，用户手动选过 en-US 的要保留。
            if (settings.SchemaVersion < 3)
            {
                if (settings.ShowStatusBar) settings.ShowStatusBar = false;
                if (string.IsNullOrWhiteSpace(settings.Language) ||
                    string.Equals(settings.Language, "zh-CN", StringComparison.OrdinalIgnoreCase))
                    settings.Language = "system";
                settings.SchemaVersion = 3;
            }
            settings.SearchEngine = SearchEngineOptions.Normalize(settings.SearchEngine);
            // 手改坏的组合回退默认键，别让密封标签切换键彻底消失。
            settings.SecretHotkey = Hotkey.Normalize(settings.SecretHotkey);
            // 旧配置没有 WebLanguage 字段，反序列化会保留字段初始值 system；
            // 手改坏的值也在这里收回，避免把非法语言写进请求头。
            settings.WebLanguage = WebLanguage.Normalize(settings.WebLanguage);
            return settings;
        }

        public static void Save(AppSettings settings)
        {
            if (!Persist) return;
            Json.Write(FilePath, settings);
        }

        private static List<Shortcut> Defaults()
        {
            return new List<Shortcut>
            {
                new Shortcut { Title = "Google", Url = "https://www.google.com/" },
                new Shortcut { Title = "GitHub", Url = "https://github.com/" },
                new Shortcut { Title = "localhost:3000", Url = "http://localhost:3000/" },
                new Shortcut { Title = "localhost:8080", Url = "http://localhost:8080/" },
                new Shortcut { Title = "pgAdmin", Url = "http://localhost:5050/" },
                new Shortcut { Title = "eck", Url = "http://localhost:5601/" },
                new Shortcut { Title = "MDN", Url = "https://developer.mozilla.org/" },
                new Shortcut { Title = "Stack Overflow", Url = "https://stackoverflow.com/" }
            };
        }
    }

    public static class SessionStore
    {
        public static string FilePath = AppPaths.Session;
        public static int Writes;   // 自测用：确认合并写真的生效

        public static SessionData Load()
        {
            return Json.Read(FilePath, new SessionData());
        }

        public static void Save(SessionData session)
        {
            foreach (var tab in session.Tabs)
            {
                tab.MemoryState = TabMemoryState.Active;
                if (tab.IsSecret) tab.Url = "";
            }
            Json.Write(FilePath, session);
            Writes++;
        }
    }

    // SPEC 4.4：丢弃快照落盘，标签关闭即删；启动时清理孤儿文件。
    public static class SnapshotStore
    {
        private static string Root { get { return Path.Combine(AppPaths.Roaming, "snapshots"); } }

        private static string FileFor(string tabId)
        {
            return Path.Combine(Root, tabId + ".json");
        }

        public static void Save(TabModel tab, TabSnapshot snapshot)
        {
            if (snapshot == null) return;
            snapshot.Bytes = SnapshotScript.Measure(snapshot);
            Json.Write(FileFor(tab.Id), snapshot);
        }

        public static TabSnapshot Load(string tabId)
        {
            return Json.Read(FileFor(tabId), (TabSnapshot)null);
        }

        public static void Delete(string tabId)
        {
            try
            {
                var path = FileFor(tabId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        public static void Prune(IEnumerable<string> liveTabIds)
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                var keep = new HashSet<string>(liveTabIds ?? new string[0]);
                foreach (var file in Directory.GetFiles(Root, "*.json"))
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    if (!keep.Contains(id)) File.Delete(file);
                }
            }
            catch { }
        }
    }

    public static class Diagnostics
    {
        public static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Logs);
                File.AppendAllText(Path.Combine(AppPaths.Logs, "nanoedge.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }
    }
}
