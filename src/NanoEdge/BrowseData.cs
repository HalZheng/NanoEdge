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

        // Chrome / Edge / Chromium 的 "Bookmarks"（JSON）。遍历各 profile，合并去重。
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

        public static int ImportChromiumFile(string path)
        {
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                var root = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }
                    .Deserialize<Dictionary<string, object>>(text);
                object rootsObj;
                if (root == null || !root.TryGetValue("roots", out rootsObj)) return 0;

                var added = 0;
                var roots = rootsObj as Dictionary<string, object>;
                if (roots == null) return 0;
                foreach (var pair in roots)
                    added += WalkChromiumNode(pair.Value as Dictionary<string, object>, "");
                return added;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("chromium import failed: " + ex.Message);
                return 0;
            }
        }

        private static int WalkChromiumNode(Dictionary<string, object> node, string folder)
        {
            if (node == null) return 0;
            var added = 0;
            var type = GetString(node, "type");
            var name = GetString(node, "name");

            if (type == "url")
            {
                var url = GetString(node, "url");
                if (!string.IsNullOrEmpty(url) && AddImported(name, url, folder)) added++;
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
                        added += WalkChromiumNode(child as Dictionary<string, object>, next);
                }
            }
            return added;
        }

        private static string GetString(Dictionary<string, object> node, string key)
        {
            object value;
            return node.TryGetValue(key, out value) && value != null ? value.ToString() : "";
        }

        private static IEnumerable<string> ChromiumBookmarkFiles()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roots = new[]
            {
                Path.Combine(local, "Google", "Chrome", "User Data"),
                Path.Combine(local, "Microsoft", "Edge", "User Data"),
                Path.Combine(local, "Chromium", "User Data")
            };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                string[] dirs;
                try { dirs = Directory.GetDirectories(root); }
                catch { continue; }
                foreach (var dir in dirs)
                {
                    var file = Path.Combine(dir, "Bookmarks");
                    if (File.Exists(file)) yield return file;
                }
            }
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
            if (string.IsNullOrEmpty(url)) return false;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return false;
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
