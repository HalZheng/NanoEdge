using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NanoEdge
{
    public sealed partial class MainWindow
    {
        // --navtest：地址栏联想 / URL 解析的验收脚本。用临时 history/bookmarks 文件，
        // 不碰用户数据；结果与下拉渲染图写到 out/。
        private sealed class NavSelfTest
        {
            private readonly MainWindow _w;
            private readonly StringBuilder _report = new StringBuilder();
            private readonly List<string> _failures = new List<string>();
            private int _passes;
            private string _root;

            public NavSelfTest(MainWindow window)
            {
                _w = window;
            }

            public async Task RunAsync()
            {
                _root = FindRoot();
                Line("M3 address bar / internal pages self-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                try
                {
                    await RunCoreAsync();
                    RenderSuggestions();
                }
                catch (Exception ex)
                {
                    Fail("unhandled", ex.ToString());
                }
                finally
                {
                    Line("");
                    Line(_failures.Count == 0
                        ? "RESULT: PASS (" + _passes + " checks)"
                        : "RESULT: FAIL " + string.Join(", ", _failures));
                    WriteReport();
                    try { Application.Current.Shutdown(); } catch { }
                }
            }

            private async Task RunCoreAsync()
            {
                var temp = Path.Combine(Path.GetTempPath(), "nanoedge-m3");
                Directory.CreateDirectory(temp);
                HistoryStore.FilePath = Path.Combine(temp, "history.json");
                BookmarkStore.FilePath = Path.Combine(temp, "bookmarks.json");
                if (File.Exists(HistoryStore.FilePath)) File.Delete(HistoryStore.FilePath);
                if (File.Exists(BookmarkStore.FilePath)) File.Delete(BookmarkStore.FilePath);

                HistoryStore.Enabled = true;
                Seed();
                HistoryStore.Flush();

                Check("history file written", File.Exists(HistoryStore.FilePath),
                    "path=" + HistoryStore.FilePath + " bytes=" +
                    (File.Exists(HistoryStore.FilePath) ? new FileInfo(HistoryStore.FilePath).Length : 0));

                // ---- 排序 ----
                var git = SuggestionEngine.Query("git", _w._tabs, 8);
                Line("[1] query 'git' -> " + Describe(git));
                Check("query 'git' ranks github first",
                    git.Count > 0 && git[0].Url.IndexOf("github.com", StringComparison.OrdinalIgnoreCase) >= 0,
                    git.Count == 0 ? "empty" : "top=" + git[0].Url);

                var mdn = SuggestionEngine.Query("mdn", _w._tabs, 8);
                Line("[2] query 'mdn' -> " + Describe(mdn));
                Check("query 'mdn' finds MDN",
                    mdn.Count > 0 && mdn[0].Url.IndexOf("mozilla", StringComparison.OrdinalIgnoreCase) >= 0,
                    mdn.Count == 0 ? "empty" : "top=" + mdn[0].Url);

                var local = SuggestionEngine.Query("local", _w._tabs, 8);
                Line("[3] query 'local' -> " + Describe(local));
                Check("query 'local' finds localhost entries",
                    local.Count >= 2 && local.All(x => x.Url.IndexOf("local", StringComparison.OrdinalIgnoreCase) >= 0),
                    "count=" + local.Count);

                var urlInput = SuggestionEngine.Query("https://example.com/a", _w._tabs, 8);
                Line("[4] query 'https://example.com/a' -> " + Describe(urlInput));
                Check("url input puts 打开 on top",
                    urlInput.Count > 0 && urlInput[0].Kind == SuggestionKind.Open &&
                    urlInput[0].Url == "https://example.com/a",
                    urlInput.Count == 0 ? "empty" : "top=" + urlInput[0].Kind + " " + urlInput[0].Url);

                var empty = SuggestionEngine.Query("zzz-no-such-thing", _w._tabs, 8);
                Check("no match -> empty list", empty.Count == 0, "count=" + empty.Count);

                // ---- 书签权重与去重 ----
                BookmarkStore.Toggle("https://gitee.com/", "我的 Gitee");
                var gitee = SuggestionEngine.Query("gitee", _w._tabs, 8);
                Line("[5] query 'gitee' -> " + Describe(gitee));
                Check("bookmark wins for its url",
                    gitee.Count == 1 && gitee[0].Kind == SuggestionKind.Bookmark,
                    "count=" + gitee.Count + " kind=" + (gitee.Count > 0 ? gitee[0].Kind.ToString() : "-"));

                var listed = SuggestionEngine.Query("github", _w._tabs, 8);
                var distinct = listed.Select(x => x.Url.ToLowerInvariant()).Distinct().Count();
                Check("no duplicate urls in list", distinct == listed.Count,
                    "rows=" + listed.Count + " distinct=" + distinct);

                // ---- URL 解析 ----
                Check("localhost looks like url", UrlParser.LooksLikeUrl("localhost:3000"),
                    "normalize=" + UrlParser.NormalizeUrl("localhost:3000"));
                Check("chinese query is not a url", !UrlParser.LooksLikeUrl("如何 学习"),
                    "looksLikeUrl=false");
                Check("dotless input is not a url", !UrlParser.LooksLikeUrl("github"),
                    "looksLikeUrl=false");
                Check("ctrl+enter completes .com", UrlParser.CompleteDotCom("github") == "github.com",
                    "result=" + UrlParser.CompleteDotCom("github"));
                Check("ctrl+enter keeps existing tld",
                    UrlParser.CompleteDotCom("github.io/x") == "github.io/x",
                    "result=" + UrlParser.CompleteDotCom("github.io/x"));
                Check("paste cleanup strips quotes and spaces",
                    UrlParser.NormalizeUrl("  \"https://a.example/b\"  ") == "https://a.example/b",
                    "result=" + UrlParser.NormalizeUrl("  \"https://a.example/b\"  "));
                Check("plain word normalizes to null",
                    UrlParser.NormalizeUrl("github") == null,
                    "result=null");

                // ---- 内部页 ----
                Check("route nano://history",
                    InternalPages.Normalize("nano://history") == InternalPages.History,
                    "normalized=" + InternalPages.Normalize("nano://history"));
                Check("route nano://bookmarks",
                    InternalPages.Normalize("nano://bookmarks") == InternalPages.Bookmarks,
                    "normalized=" + InternalPages.Normalize("nano://bookmarks"));
                Check("unknown nano:// falls back to start",
                    InternalPages.Normalize("nano://whatever") == InternalPages.Start,
                    "normalized=" + InternalPages.Normalize("nano://whatever"));

                // ---- 书签导入：Chrome JSON ----
                var chromeJson = @"{""roots"":{""bookmark_bar"":{""type"":""folder"",""name"":""书签栏"",""children"":[" +
                    @"{""type"":""url"",""name"":""GitHub"",""url"":""https://github.com/""}," +
                    @"{""type"":""folder"",""name"":""开发"",""children"":[" +
                    @"{""type"":""url"",""name"":""MDN"",""url"":""https://developer.mozilla.org/""}]}]}," +
                    @"""other"":{""type"":""folder"",""name"":""其他书签"",""children"":[]}}}";
                var chromePath = Path.Combine(temp, "Bookmarks.json");
                File.WriteAllText(chromePath, chromeJson, Encoding.UTF8);
                var chromiumAdded = BookmarkStore.ImportChromiumFile(chromePath);
                Check("chromium json import", chromiumAdded == 2, "added=" + chromiumAdded);

                // ---- 书签导入：Netscape HTML ----
                var netscape = "<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<DL><p>\n" +
                               "  <DT><H3>工具</H3>\n  <DL><p>\n" +
                               "    <DT><A HREF=\"https://stackoverflow.com/\">Stack Overflow</A>\n" +
                               "    <DT><A HREF=\"https://www.npmjs.com/\">npm</A>\n" +
                               "  </DL><p>\n</DL><p>\n";
                var htmlPath = Path.Combine(temp, "bookmarks.html");
                File.WriteAllText(htmlPath, netscape, Encoding.UTF8);
                var htmlAdded = BookmarkStore.ImportNetscapeHtml(htmlPath);
                Check("netscape html import", htmlAdded == 2, "added=" + htmlAdded);
                Check("import keeps folders",
                    BookmarkStore.All().Any(b => b.Folder.IndexOf("开发", StringComparison.Ordinal) >= 0) &&
                    BookmarkStore.All().Any(b => b.Folder == "工具"),
                    "folders=" + string.Join(",", BookmarkStore.All().Select(b => b.Folder).Distinct()));

                // ---- 打开内部页：标签里不建 WebView2 ----
                _w.OpenInternalPage(_w._active, InternalPages.History);
                Check("internal page opens without webview",
                    _w._active.InternalPage == "history" && !_w._browser.HasLiveView(_w._active),
                    "tab=" + _w._active.InternalPage + " url=" + _w._active.Url +
                    " live=" + _w._browser.HasLiveView(_w._active));

                RenderPage(new HistoryPage((u, n) => { }, m => { }),
                    Path.Combine(_root, "out", "m3-history.png"), 980, 780);
                RenderPage(new BookmarksPage((u, n) => { }, m => { }),
                    Path.Combine(_root, "out", "m3-bookmarks.png"), 980, 520);

                // ---- 地址栏输入 nano:// 也走同一条路由 ----
                _w.Navigate(_w._active, "nano://bookmarks");
                Check("address bar routes nano://bookmarks",
                    _w._active.InternalPage == "bookmarks",
                    "tab=" + _w._active.InternalPage);

                // ---- 内部页 -> 网页：占位视图必须被换掉，不能残留两个子元素 ----
                var probe = Path.Combine(temp, "probe.html");
                File.WriteAllText(probe, "<!doctype html><title>probe</title><body>ok</body>",
                    Encoding.UTF8);
                var probeUrl = new Uri(probe).AbsoluteUri;
                _w.Navigate(_w._active, probeUrl);
                var swapped = await WaitForAsync(
                    () => _w._browser.HasLiveView(_w._active) && _w._tabHost.Children.Count == 1, 12000);
                Check("internal -> web swaps the view", swapped,
                    "live=" + _w._browser.HasLiveView(_w._active) +
                    " children=" + _w._tabHost.Children.Count +
                    " url=" + _w._active.Url);
                var recorded = await WaitForAsync(
                    () => HistoryStore.All().Any(e => Urls.Same(e.Url, probeUrl)), 6000);
                Check("navigation from internal page is recorded", recorded,
                    "probe=" + probeUrl);

                // ---- 快捷键表（页面聚焦时由线程预处理调用同一个入口）----
                _w.HandleShortcut(System.Windows.Input.Key.D,
                    System.Windows.Input.ModifierKeys.Control, System.Windows.Input.Key.None);
                Check("ctrl+D bookmarks current page", BookmarkStore.Contains(probeUrl),
                    "bookmarks=" + BookmarkStore.All().Count);
                _w.HandleShortcut(System.Windows.Input.Key.D,
                    System.Windows.Input.ModifierKeys.Control, System.Windows.Input.Key.None);
                Check("ctrl+D toggles off", !BookmarkStore.Contains(probeUrl),
                    "bookmarks=" + BookmarkStore.All().Count);

                _w.HandleShortcut(System.Windows.Input.Key.H,
                    System.Windows.Input.ModifierKeys.Control, System.Windows.Input.Key.None);
                Check("ctrl+H opens history", _w._active.InternalPage == "history",
                    "tab=" + _w._active.InternalPage);
                _w.HandleShortcut(System.Windows.Input.Key.O,
                    System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift,
                    System.Windows.Input.Key.None);
                Check("ctrl+shift+O opens bookmarks", _w._active.InternalPage == "bookmarks",
                    "tab=" + _w._active.InternalPage);

                Line("");
                Line("history entries=" + HistoryStore.All().Count +
                     " bookmarks=" + BookmarkStore.All().Count);
                Line("sample rows: " + string.Join(" | ", HistoryStore.All().Take(4)
                    .Select(e => e.Title + " <" + e.Url + "> x" + e.Visits)));
            }

            private void Seed()
            {
                AddVisit("https://github.com/", "GitHub", 6);
                AddVisit("https://github.com/dotnet/wpf", "dotnet/wpf: WPF 官方仓库", 2);
                AddVisit("https://gitlab.com/", "GitLab", 2);
                AddVisit("https://gitee.com/", "Gitee", 1);
                AddVisit("https://developer.mozilla.org/zh-CN/", "MDN Web Docs", 4);
                AddVisit("https://www.bing.com/", "Bing", 3);
                AddVisit("https://stackoverflow.com/questions", "Stack Overflow", 2);
                AddVisit("http://localhost:3000/", "vite dev server", 8);
                AddVisit("http://localhost:8080/api/health", "本地 API", 3);
                AddVisit("http://localhost:5050/browser/", "pgAdmin 4", 2);
                AddVisit("http://localhost:5601/app", "eck 控制台", 2);
                AddVisit("https://www.npmjs.com/", "npm", 1);
            }

            private static void AddVisit(string url, string title, int times)
            {
                for (var i = 0; i < times; i++) HistoryStore.Record(url, title);
            }

            private void RenderSuggestions()
            {
                _w.ShowSuggestionsForTest("git");
                var visual = _w._suggestions == null ? null : _w._suggestions.Visual as FrameworkElement;
                if (visual == null)
                {
                    Fail("render suggestions", "popup visual is null");
                    return;
                }
                var path = Path.Combine(_root, "out", "m3-suggest.png");
                Render(visual, path);
                Line("");
                Line("suggestion rows: " + (_w._suggestions == null ? 0 : _w._suggestions.Count) +
                     " (image " + path + ")");
                Check("suggestions popup rendered", File.Exists(path), path);
            }

            private async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
            {
                var started = Environment.TickCount;
                while (Environment.TickCount - started < timeoutMs)
                {
                    if (condition()) return true;
                    await Task.Delay(100);
                }
                return condition();
            }

            private static void RenderPage(FrameworkElement page, string path, double width, double height)
            {
                page.Width = width;
                page.Height = height;
                page.Measure(new Size(width, height));
                page.Arrange(new Rect(0, 0, width, height));
                page.UpdateLayout();

                var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(page);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                using (var stream = File.Create(path)) encoder.Save(stream);
            }

            private static void Render(FrameworkElement element, string path)
            {
                var width = Math.Max(320, element.ActualWidth);
                var height = Math.Max(40, element.ActualHeight);
                element.Measure(new Size(width, height));
                element.Arrange(new Rect(0, 0, width, height));
                element.UpdateLayout();
                width = Math.Max(320, element.ActualWidth);
                height = Math.Max(40, element.ActualHeight);

                var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(element);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                using (var stream = File.Create(path)) encoder.Save(stream);
            }

            private static string Describe(List<Suggestion> items)
            {
                if (items.Count == 0) return "(empty)";
                return string.Join(" | ", items.Take(5).Select(x => x.KindName + ":" + x.Url + "(" + x.Score + ")"));
            }

            private void Check(string name, bool passed, string detail)
            {
                if (passed) _passes++;
                else _failures.Add(name);
                Line((passed ? "[PASS] " : "[FAIL] ") + name + "  " + detail);
            }

            private void Fail(string name, string detail)
            {
                _failures.Add(name);
                Line("[FAIL] " + name + "  " + detail);
            }

            private void Line(string text)
            {
                _report.AppendLine(text);
                Diagnostics.Log("[navtest] " + text);
            }

            private void WriteReport()
            {
                try
                {
                    var path = Path.Combine(_root ?? Path.GetTempPath(), "out", "m3-navtest.txt");
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.WriteAllText(path, _report.ToString(), Encoding.UTF8);
                }
                catch { }
            }

            private static string FindRoot()
            {
                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "SPEC.md")))
                    dir = dir.Parent;
                return dir == null ? Path.GetTempPath() : dir.FullName;
            }
        }
    }
}
