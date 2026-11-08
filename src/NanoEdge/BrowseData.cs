using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace NanoEdge
{
    // URL 归一化比较：忽略 scheme/host 大小写、默认端口、末尾斜杠差异。
    // （参考 ZZZ 的 Uri.Compare 做法，避免 "github.com" 与 "github.com/" 被当成两条。）
    public static class Urls
    {
        public static bool Same(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            Uri ua, ub;
            if (Uri.TryCreate(a, UriKind.Absolute, out ua) && Uri.TryCreate(b, UriKind.Absolute, out ub))
            {
                return Uri.Compare(ua, ub,
                    UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                    UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
            }
            return string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        public static string Key(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            Uri uri;
            if (Uri.TryCreate(url, UriKind.Absolute, out uri))
                return (uri.Scheme + "://" + uri.Host + uri.PathAndQuery).ToLowerInvariant();
            return url.TrimEnd('/').ToLowerInvariant();
        }

        public static string Host(string url)
        {
            try
            {
                var uri = new Uri(url);
                return string.IsNullOrEmpty(uri.Host) ? url : uri.Host;
            }
            catch { return url ?? ""; }
        }
    }

    public sealed class HistoryEntry
    {
        public string Url = "";
        public string Title = "";
        public DateTime LastVisitUtc = DateTime.UtcNow;
        public int Visits = 1;
    }

    public sealed class BookmarkEntry
    {
        public string Url = "";
        public string Title = "";
        public DateTime AddedUtc = DateTime.UtcNow;
        public string Folder = "";
    }

    // ---------- C6 书签导入向导的数据模型 ----------

    // 一个被发现的 Chrome / Edge / Chromium profile。发现阶段就把候选解析好缓存，
    // 预览和落库都复用同一份 Candidates —— 红线：预览「将新增」== 真正落库条数。
    public sealed class ChromiumProfile
    {
        public string Browser = "";       // "Chrome" / "Edge" / "Chromium"
        public string ProfileDir = "";    // 目录名，如 "Default" / "Profile 1"
        public string DisplayName = "";   // Local State 里的 name，读不到就用 ProfileDir
        public string FilePath = "";      // Bookmarks 文件的完整路径
        public ChromiumProfileError ErrorKind;
        public string ErrorDetail = "";   // 原始异常消息（诊断用，不参与 UI 文案翻译）
        public List<BookmarkCandidate> Candidates = new List<BookmarkCandidate>();

        public bool ReadFailed { get { return ErrorKind != ChromiumProfileError.None; } }
        public int Count { get { return Candidates.Count; } }
        public string Label { get { return Browser + " · " + DisplayName; } }
    }

    public enum ChromiumProfileError { None, Io, Json }

    // 一条待导入候选。非法 URL 也保留在列表里（IsValid=false），让预览能显示「无效」；
    // 旧版是在落库时静默丢掉，用户完全看不见。
    public sealed class BookmarkCandidate
    {
        public string Title = "";
        public string Url = "";
        public string Folder = "";
        public bool IsValid { get { return BookmarkStore.IsImportableUrl(Url); } }
    }

    // 预览 / 结果页共用的数字口径。
    public sealed class ImportOutcome
    {
        public int Total;
        public int Added;
        public int Skipped;
        public int Invalid;
        public int Failed;
        public string Error = "";
    }

    // 浏览历史。去重按 URL（保留最近访问时间与累计次数），容量 5000 条 / 90 天。
    // 落盘做 3s 节流：导航很频繁，每次写整份 JSON 不划算；退出时 Flush。
    public static class HistoryStore
    {
        public static string FilePath = AppPaths.History;
        public static bool Enabled = true;
        public static int MaxEntries = 5000;
        public static int MaxAgeDays = 90;

        private static readonly List<HistoryEntry> Entries = new List<HistoryEntry>();
        private static bool _loaded;
        private static DateTime _lastSaveUtc = DateTime.MinValue;
        private static bool _dirty;

        public static IReadOnlyList<HistoryEntry> All()
        {
            EnsureLoaded();
            return Entries;
        }

        public static void Record(string url, string title)
        {
            if (!Enabled || !IsRecordable(url)) return;
            EnsureLoaded();

            var entry = Entries.FirstOrDefault(e => SameUrl(e.Url, url));
            if (entry == null)
            {
                entry = new HistoryEntry { Url = url };
                Entries.Add(entry);
            }
            entry.Title = CleanTitle(title, url);
            entry.LastVisitUtc = DateTime.UtcNow;
            entry.Visits++;
            _dirty = true;
            Trim();
            SaveThrottled();
        }

        // 标题是导航完成之后才稳定的，这里只更新标题，不算一次访问。
        public static void UpdateTitle(string url, string title)
        {
            if (!Enabled || !IsRecordable(url)) return;
            EnsureLoaded();
            var entry = Entries.FirstOrDefault(e => SameUrl(e.Url, url));
            if (entry == null) return;
            var cleaned = CleanTitle(title, url);
            if (cleaned == entry.Title) return;
            entry.Title = cleaned;
            _dirty = true;
            SaveThrottled();
        }

        public static void Remove(string url)
        {
            EnsureLoaded();
            Entries.RemoveAll(e => SameUrl(e.Url, url));
            _dirty = true;
            Flush();
        }

        public static void Clear()
        {
            Entries.Clear();
            _dirty = true;
            Flush();
        }

        public static void Flush()
        {
            if (!_dirty) return;
            try
            {
                EnsureDirectory();
                Json.Write(FilePath, new HistoryFile { Entries = Entries.OrderByDescending(e => e.LastVisitUtc).ToList() });
                _dirty = false;
                _lastSaveUtc = DateTime.UtcNow;
            }
            catch (Exception ex) { Diagnostics.Log("history save failed: " + ex.Message); }
        }

        private static void SaveThrottled()
        {
            if ((DateTime.UtcNow - _lastSaveUtc).TotalSeconds < 3) return;
            Flush();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var file = Json.Read(FilePath, (HistoryFile)null);
                if (file != null && file.Entries != null)
                {
                    Entries.AddRange(file.Entries.Where(e => !string.IsNullOrEmpty(e.Url)));
                    Trim();
                }
            }
            catch (Exception ex) { Diagnostics.Log("history load failed: " + ex.Message); }
        }

        private static void Trim()
        {
            var cutoff = DateTime.UtcNow.AddDays(-MaxAgeDays);
            Entries.RemoveAll(e => e.LastVisitUtc < cutoff);
            if (Entries.Count <= MaxEntries) return;
            Entries.Sort((a, b) => b.LastVisitUtc.CompareTo(a.LastVisitUtc));
            Entries.RemoveRange(MaxEntries, Entries.Count - MaxEntries);
        }

        private static bool IsRecordable(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (url.StartsWith("nano://", StringComparison.OrdinalIgnoreCase)) return false;
            if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return false;
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static bool SameUrl(string a, string b)
        {
            return Urls.Same(a, b);
        }

        private static string CleanTitle(string title, string url)
        {
            if (string.IsNullOrWhiteSpace(title) || title == url) return url;
            return title.Trim();
        }

        private static void EnsureDirectory()
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }

        public sealed class HistoryFile
        {
            public List<HistoryEntry> Entries = new List<HistoryEntry>();
        }
    }

    // 书签。数量小，直接同步落盘。
    public static class BookmarkStore
    {
        public static string FilePath = AppPaths.Bookmarks;

        private static readonly List<BookmarkEntry> Entries = new List<BookmarkEntry>();
        private static bool _loaded;

        public static IReadOnlyList<BookmarkEntry> All()
        {
            EnsureLoaded();
            return Entries;
        }

        public static bool Contains(string url)
        {
            EnsureLoaded();
            return Entries.Any(e => SameUrl(e.Url, url));
        }

        public static BookmarkEntry Toggle(string url, string title)
        {
            EnsureLoaded();
            var existing = Entries.FirstOrDefault(e => SameUrl(e.Url, url));
            if (existing != null)
            {
                Entries.Remove(existing);
                Save();
                return null;
            }
            var entry = new BookmarkEntry { Url = url, Title = string.IsNullOrWhiteSpace(title) ? url : title.Trim() };
            Entries.Insert(0, entry);
            Save();
            return entry;
        }

        public static void Remove(string url)
        {
            EnsureLoaded();
            Entries.RemoveAll(e => SameUrl(e.Url, url));
            Save();
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var file = Json.Read(FilePath, (BookmarkFile)null);
                if (file != null && file.Entries != null)
                    Entries.AddRange(file.Entries.Where(e => !string.IsNullOrEmpty(e.Url)));
            }
            catch (Exception ex) { Diagnostics.Log("bookmarks load failed: " + ex.Message); }
        }

        // 自测用：把静态缓存清回未加载状态，配合临时 FilePath 造干净的书签库。
        internal static void ResetForTest()
        {
            Entries.Clear();
            _loaded = false;
        }

        private static void Save()
        {
            try { Json.Write(FilePath, new BookmarkFile { Entries = Entries.ToList() }); }
            catch (Exception ex) { Diagnostics.Log("bookmarks save failed: " + ex.Message); }
        }

        private static bool SameUrl(string a, string b)
        {
            return Urls.Same(a, b);
        }

        // ---------- 导入 / 导出 ----------

        public sealed class ImportResult
        {
            public int Added;
            public int Files;
            public int Total;
        }

        // 旧的一键全量导入：UI 已不再调用，保留给自测的兼容面，行为一字不改。
        public static ImportResult ImportChromiumProfiles()
        {
            var result = new ImportResult();
            foreach (var file in ChromiumBookmarkFiles())
            {
                result.Files++;
                result.Added += ImportChromiumFile(file);
            }
            result.Total = All().Count;
            if (result.Added > 0) Save();
            return result;
        }

        // 兼容面：--navtest 断言 added == 2，签名和返回值语义保持不变。
        // 内部改成「只读解析 + 一次落库」，去重规则与向导完全一致。
        public static int ImportChromiumFile(string path)
        {
            try { return ApplyImport(ParseChromiumFile(path), true).Added; }
            catch (Exception ex)
            {
                Diagnostics.Log("chromium import failed: " + ex.Message);
                return 0;
            }
        }

        // ---------- C6：发现 / 解析 / 预演 / 落库 ----------

        // 自测注入点：指向临时目录，替代 %LOCALAPPDATA%。测试绝不读用户真实 Chrome/Edge 数据。
        internal static string UserDataRootOverride { get; set; }
        private const int MaxChromiumJsonLength = 64 * 1024 * 1024;

        private static string UserDataRoot()
        {
            return string.IsNullOrEmpty(UserDataRootOverride)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : UserDataRootOverride;
        }

        // 发现 Chrome → Edge → Chromium 三个根下确实带 Bookmarks 文件的 profile；
        // Default 排在每个浏览器的最前面。读取失败的 profile 也保留并标记，不显示成 0 条。
        public static List<ChromiumProfile> DiscoverChromiumProfiles()
        {
            var found = new List<ChromiumProfile>();
            var local = UserDataRoot();
            var roots = new[]
            {
                new { Browser = "Chrome",   Path = Path.Combine(local, "Google", "Chrome", "User Data") },
                new { Browser = "Edge",     Path = Path.Combine(local, "Microsoft", "Edge", "User Data") },
                new { Browser = "Chromium", Path = Path.Combine(local, "Chromium", "User Data") }
            };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root.Path)) continue;
                string[] dirs;
                try { dirs = Directory.GetDirectories(root.Path); }
                catch { continue; }

                var profiles = new List<ChromiumProfile>();
                foreach (var dir in dirs)
                {
                    var file = Path.Combine(dir, "Bookmarks");
                    if (!File.Exists(file)) continue;   // Crashpad / 缓存等非 profile 目录
                    var dirName = Path.GetFileName(dir);
                    var profile = new ChromiumProfile
                    {
                        Browser = root.Browser,
                        ProfileDir = dirName,
                        DisplayName = ReadDisplayName(root.Path, dirName),
                        FilePath = file
                    };
                    try { profile.Candidates = ParseChromiumFile(file); }
                    catch (IOException ex)
                    {
                        profile.ErrorKind = ChromiumProfileError.Io;
                        profile.ErrorDetail = ex.Message;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        profile.ErrorKind = ChromiumProfileError.Io;
                        profile.ErrorDetail = ex.Message;
                    }
                    catch (Exception ex)
                    {
                        // JavaScriptSerializer 对坏 JSON 抛的是通用异常，归到「JSON 损坏」。
                        profile.ErrorKind = ChromiumProfileError.Json;
                        profile.ErrorDetail = ex.Message;
                    }
                    profiles.Add(profile);
                }
                found.AddRange(profiles
                    .OrderBy(p => string.Equals(p.ProfileDir, "Default",
                        StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(p => p.ProfileDir, StringComparer.OrdinalIgnoreCase));
            }
            return found;
        }

        // Local State 的 profile.info_cache[目录名].name；缺失 / 损坏 / 无 info_cache
        // 都回退成目录名（这只是显示名，书签文件本身仍可用，不算读取失败）。
        private static string ReadDisplayName(string userDataRoot, string dirName)
        {
            try
            {
                var state = Path.Combine(userDataRoot, "Local State");
                if (!File.Exists(state)) return dirName;
                var text = File.ReadAllText(state, Encoding.UTF8);
                var root = new JavaScriptSerializer { MaxJsonLength = MaxChromiumJsonLength }
                    .Deserialize<Dictionary<string, object>>(text);
                if (root == null) return dirName;
                object profileObj;
                if (!root.TryGetValue("profile", out profileObj)) return dirName;
                var profile = profileObj as Dictionary<string, object>;
                if (profile == null) return dirName;
                object cacheObj;
                if (!profile.TryGetValue("info_cache", out cacheObj)) return dirName;
                var cache = cacheObj as Dictionary<string, object>;
                if (cache == null) return dirName;
                object entryObj;
                if (!cache.TryGetValue(dirName, out entryObj)) return dirName;
                var entry = entryObj as Dictionary<string, object>;
                if (entry == null) return dirName;
                var name = GetString(entry, "name");
                return string.IsNullOrWhiteSpace(name) ? dirName : name;
            }
            catch { return dirName; }
        }

        // 只读解析：不写库。空 URL / javascript: 之类也进列表，由 IsValid 标记，
        // 这样预览能把「无效」算给用户看，而不是落库时悄悄丢掉。
        public static List<BookmarkCandidate> ParseChromiumFile(string path)
        {
            var list = new List<BookmarkCandidate>();
            var text = File.ReadAllText(path, Encoding.UTF8);
            var root = new JavaScriptSerializer { MaxJsonLength = MaxChromiumJsonLength }
                .Deserialize<Dictionary<string, object>>(text);
            object rootsObj;
            if (root == null || !root.TryGetValue("roots", out rootsObj)) return list;
            var roots = rootsObj as Dictionary<string, object>;
            if (roots == null) return list;
            foreach (var pair in roots)
                WalkChromiumNode(pair.Value as Dictionary<string, object>, "", list);
            return list;
        }

        // folder 拼接与旧版逐字一致：" / " 拼层级，书签页按 Folder 分组的语义不变。
        private static void WalkChromiumNode(Dictionary<string, object> node, string folder,
            List<BookmarkCandidate> output)
        {
            if (node == null) return;
            var type = GetString(node, "type");
            var name = GetString(node, "name");

            if (type == "url")
            {
                output.Add(new BookmarkCandidate
                {
                    Title = name,
                    Url = GetString(node, "url"),
                    Folder = folder
                });
            }

            object childrenObj;
            if (node.TryGetValue("children", out childrenObj))
            {
                var children = childrenObj as ArrayList;
                if (children != null)
                {
                    var next = type == "folder" && !string.IsNullOrEmpty(name)
                        ? (folder.Length == 0 ? name : folder + " / " + name)
                        : folder;
                    foreach (var child in children)
                        WalkChromiumNode(child as Dictionary<string, object>, next, output);
                }
            }
        }

        private static string GetString(Dictionary<string, object> node, string key)
        {
            object value;
            return node.TryGetValue(key, out value) && value != null ? value.ToString() : "";
        }

        // 从 AddImported 前移过来的 URL 合法性判断：只认 http / https / file。
        public static bool IsImportableUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        }

        // 预览和落库唯一共用的分类逻辑：同一份候选 + 同一套 skipExisting / Urls.Same。
        // 别在两处各算一遍，否则「将新增」会和真正落库条数对不上。
        private sealed class ImportPlan
        {
            public int Total;
            public int Skipped;
            public int Invalid;
            public List<BookmarkCandidate> ToAdd = new List<BookmarkCandidate>();

            public ImportOutcome ToOutcome()
            {
                return new ImportOutcome
                {
                    Total = Total,
                    Added = ToAdd.Count,
                    Skipped = Skipped,
                    Invalid = Invalid
                };
            }
        }

        private static ImportPlan PlanImport(IEnumerable<BookmarkCandidate> candidates, bool skipExisting)
        {
            var plan = new ImportPlan();
            var existing = skipExisting ? All().Select(e => e.Url).ToList() : null;
            var pending = new List<string>();   // 本批次内已选中的，避免批内重复
            foreach (var candidate in candidates ?? Enumerable.Empty<BookmarkCandidate>())
            {
                plan.Total++;
                if (candidate == null || !candidate.IsValid) { plan.Invalid++; continue; }
                if (skipExisting)
                {
                    var url = candidate.Url;
                    if (existing.Any(u => Urls.Same(u, url)) ||
                        pending.Any(u => Urls.Same(u, url)))
                    {
                        plan.Skipped++;
                        continue;
                    }
                    pending.Add(url);
                }
                plan.ToAdd.Add(candidate);
            }
            return plan;
        }

        // 预演：只读，绝不写库。
        public static ImportOutcome PreviewImport(IEnumerable<BookmarkCandidate> candidates, bool skipExisting)
        {
            return PlanImport(candidates, skipExisting).ToOutcome();
        }

        // 落库：只追加，不 Remove / 不清空现有书签；最后一次性 Save()。
        public static ImportOutcome ApplyImport(IEnumerable<BookmarkCandidate> candidates, bool skipExisting)
        {
            var plan = PlanImport(candidates, skipExisting);
            var outcome = plan.ToOutcome();
            if (plan.ToAdd.Count == 0) return outcome;
            EnsureLoaded();
            foreach (var candidate in plan.ToAdd)
                Entries.Add(new BookmarkEntry
                {
                    Title = string.IsNullOrWhiteSpace(candidate.Title)
                        ? Urls.Host(candidate.Url) : candidate.Title.Trim(),
                    Url = candidate.Url,
                    Folder = candidate.Folder ?? "",
                    AddedUtc = DateTime.UtcNow
                });
            Save();
            return outcome;
        }

        // 只给旧 ImportChromiumProfiles 用；UI 已改用 DiscoverChromiumProfiles，
        // 留一个薄包装避免把「遍历哪些文件」的口径复制两份。
        private static IEnumerable<string> ChromiumBookmarkFiles()
        {
            return DiscoverChromiumProfiles().Select(p => p.FilePath);
        }

        // Netscape 书签 HTML（Chrome/Edge/Firefox 的导出格式）。文件夹用 <H3> + <DL> 嵌套，
        // 这里用一次正则扫描维护文件夹栈 —— 思路参考 ZZZ 的 ImportHtmlAsync。
        public static int ImportNetscapeHtml(string path)
        {
            try
            {
                var html = File.ReadAllText(path, Encoding.UTF8);
                var added = 0;
                var folders = new List<string>();
                string pendingFolder = null;

                const string pattern =
                    "<H3\\b[^>]*>(?<folder>.*?)</H3>" +
                    "|<A\\s+[^>]*HREF=[\"'](?<url>[^\"']+)[\"'][^>]*>(?<title>.*?)</A>" +
                    "|(?<open><DL\\b[^>]*>)|(?<close></DL\\s*>)";

                foreach (Match match in Regex.Matches(html, pattern,
                             RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    if (match.Groups["folder"].Success)
                    {
                        pendingFolder = CleanHtmlText(match.Groups["folder"].Value);
                        continue;
                    }
                    if (match.Groups["open"].Success)
                    {
                        folders.Add(pendingFolder ?? "");
                        pendingFolder = null;
                        continue;
                    }
                    if (match.Groups["close"].Success)
                    {
                        if (folders.Count > 0) folders.RemoveAt(folders.Count - 1);
                        continue;
                    }
                    if (!match.Groups["url"].Success) continue;

                    var url = WebUtility.HtmlDecode(match.Groups["url"].Value);
                    var title = CleanHtmlText(match.Groups["title"].Value);
                    var folder = string.Join(" / ", folders.Where(x => !string.IsNullOrWhiteSpace(x)));
                    if (AddImported(title, url, folder)) added++;
                }

                if (added > 0) Save();
                return added;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("html import failed: " + ex.Message);
                return 0;
            }
        }

        public static void ExportNetscapeHtml(string path)
        {
            var html = new StringBuilder();
            html.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
            html.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
            html.AppendLine("<TITLE>NanoEdge Bookmarks</TITLE>");
            html.AppendLine("<H1>NanoEdge Bookmarks</H1>");
            html.AppendLine("<DL><p>");
            foreach (var item in All())
                html.AppendLine("    <DT><A HREF=\"" + WebUtility.HtmlEncode(item.Url) + "\">" +
                                WebUtility.HtmlEncode(item.Title) + "</A>");
            html.AppendLine("</DL><p>");
            File.WriteAllText(path, html.ToString(), Encoding.UTF8);
        }

        private static bool AddImported(string title, string url, string folder)
        {
            // HTML 导入与向导共用同一套 URL 合法性判断（规则从 C6 前移到 IsImportableUrl）。
            if (!IsImportableUrl(url)) return false;
            if (Contains(url)) return false;

            Entries.Add(new BookmarkEntry
            {
                Title = string.IsNullOrWhiteSpace(title) ? Urls.Host(url) : title.Trim(),
                Url = url,
                Folder = folder ?? "",
                AddedUtc = DateTime.UtcNow
            });
            return true;
        }

        private static string CleanHtmlText(string value)
        {
            return WebUtility.HtmlDecode(Regex.Replace(value ?? "", "<.*?>", string.Empty)).Trim();
        }

        public sealed class BookmarkFile
        {
            public List<BookmarkEntry> Entries = new List<BookmarkEntry>();
        }
    }

    public enum SuggestionKind { Open, History, Bookmark, Tab }

    public sealed class Suggestion
    {
        public SuggestionKind Kind;
        public string Title = "";
        public string Url = "";
        public int Score;

        public string KindGlyph
        {
            get
            {
                switch (Kind)
                {
                    case SuggestionKind.Open: return "↵";
                    case SuggestionKind.Bookmark: return "★";
                    case SuggestionKind.Tab: return "▣";
                    default: return "◷";
                }
            }
        }

        public string KindName
        {
            get
            {
                switch (Kind)
                {
                    case SuggestionKind.Open: return L.T("打开");
                    case SuggestionKind.Bookmark: return L.T("书签");
                    case SuggestionKind.Tab: return L.T("已开");
                    default: return L.T("历史");
                }
            }
        }
    }

    // SPEC 7.2 联想排序：URL 前缀 > 域名前缀 > 标题前缀 > 包含；
    // 再叠加访问次数、新鲜度、书签/已开标签的权重。
    public static class SuggestionEngine
    {
        public static List<Suggestion> Query(string input, IEnumerable<TabModel> openTabs, int limit)
        {
            var result = new List<Suggestion>();
            var text = (input ?? "").Trim();
            if (text.Length == 0) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 归一化后的 URL

            if (UrlParser.LooksLikeUrl(text))
            {
                var url = UrlParser.NormalizeUrl(text);
                if (!string.IsNullOrEmpty(url))
                {
                    result.Add(new Suggestion
                    {
                        Kind = SuggestionKind.Open,
                        Title = L.T("打开 ") + Shorten(url, 72),
                        Url = url,
                        Score = 1000
                    });
                    seen.Add(Urls.Key(url));
                }
            }

            foreach (var tab in openTabs ?? Enumerable.Empty<TabModel>())
            {
                // 密封标签的 URL / 标题不能出现在联想里（否则打字就能把地址翻出来）。
                if (tab.IsSecret) continue;
                if (string.IsNullOrEmpty(tab.Url) || tab.IsStartPage) continue;
                var score = Score(text, tab.Title, tab.Url, 0, DateTime.UtcNow, false, true);
                if (score <= 0) continue;
                Add(result, seen, new Suggestion
                {
                    Kind = SuggestionKind.Tab,
                    Title = tab.Title,
                    Url = tab.Url,
                    Score = score
                });
            }

            foreach (var bookmark in BookmarkStore.All())
            {
                var score = Score(text, bookmark.Title, bookmark.Url, 0, bookmark.AddedUtc, true, false);
                if (score <= 0) continue;
                Add(result, seen, new Suggestion
                {
                    Kind = SuggestionKind.Bookmark,
                    Title = bookmark.Title,
                    Url = bookmark.Url,
                    Score = score
                });
            }

            foreach (var entry in HistoryStore.All())
            {
                var score = Score(text, entry.Title, entry.Url, entry.Visits, entry.LastVisitUtc, false, false);
                if (score <= 0) continue;
                Add(result, seen, new Suggestion
                {
                    Kind = SuggestionKind.History,
                    Title = entry.Title,
                    Url = entry.Url,
                    Score = score
                });
            }

            return result
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Url.Length)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        private static void Add(List<Suggestion> list, HashSet<string> seen, Suggestion item)
        {
            if (!seen.Add(Urls.Key(item.Url))) return;
            list.Add(item);
        }

        private static int Score(string input, string title, string url, int visits, DateTime lastUtc, bool bookmark, bool tab)
        {
            var t = title ?? "";
            var u = url ?? "";
            var score = 0;

            if (u.StartsWith(input, StringComparison.OrdinalIgnoreCase)) score += 120;
            else if (HostOf(u).StartsWith(input, StringComparison.OrdinalIgnoreCase)) score += 100;
            else if (u.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0) score += 50;

            if (t.StartsWith(input, StringComparison.OrdinalIgnoreCase)) score += 90;
            else if (t.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0) score += 40;

            if (score == 0) return 0;

            if (bookmark) score += 25;
            if (tab) score += 30;
            if (visits > 1) score += Math.Min(20, (int)(Math.Log(visits) * 10));
            if ((DateTime.UtcNow - lastUtc).TotalDays <= 7) score += 15;
            return score;
        }

        private static string HostOf(string url)
        {
            try { return new Uri(url).Host; }
            catch { return ""; }
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
            return text.Substring(0, max - 1) + "…";
        }
    }
}
