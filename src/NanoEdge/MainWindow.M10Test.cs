using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace NanoEdge
{
    public sealed partial class MainWindow
    {
        // --m10test：网页语言（网站检测后默认展示的语言）验收。
        // 用本机 HTTP 服务把浏览器真正发出来的 Accept-Language 回显到页面标题，
        // 再读 navigator.language；两者都不是读配置，而是读真实请求 / 真实页面。
        private sealed class M10SelfTest
        {
            private static readonly Regex Cjk = new Regex("[\u4e00-\u9fff]");
            private static readonly string[] AllowedCjk = { "中文" };

            private readonly MainWindow _w;
            private readonly StringBuilder _report = new StringBuilder();
            private readonly List<string> _failures = new List<string>();
            private int _passes;
            private string _root;
            private string _temp;
            private string _base;
            private int _port;
            private TcpListener _server;
            private volatile bool _serverRunning;

            public M10SelfTest(MainWindow window) { _w = window; }

            public async Task RunAsync()
            {
                _root = FindRoot();
                Line("M10 website-language self-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                try
                {
                    Setup();
                    await RunCoreAsync();
                }
                catch (Exception ex) { Fail("unhandled", ex.ToString()); }
                finally
                {
                    StopServer();
                    Line("");
                    Line(_failures.Count == 0
                        ? "RESULT: PASS (" + _passes + " checks)"
                        : "RESULT: FAIL " + string.Join(", ", _failures));
                    WriteReport();
                    try { Application.Current.Shutdown(); } catch { }
                }
            }

            private void Setup()
            {
                _temp = Path.Combine(Path.GetTempPath(), "nanoedge-m10");
                if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
                Directory.CreateDirectory(_temp);

                // 自测不碰用户的 settings.json。
                SettingsStore.FilePath = Path.Combine(_temp, "settings.json");
                SettingsStore.Persist = true;

                _w._settings.Language = "zh-CN";
                _w._settings.WebLanguage = WebLanguage.System;
                L.Apply("zh-CN");
                Theme.Apply("dark");
                _w._browser.ApplyWebLanguage();

                StartServer();
                _base = "http://127.0.0.1:" + _port + "/lang";
                _w.WindowState = WindowState.Normal;
                _w.Width = 1100;
                _w.Height = 780;
            }

            private async Task RunCoreAsync()
            {
                // ---------- 1. 口径与索引 ----------
                Check("system is the default and normalizes bad values",
                    WebLanguage.Normalize(null) == "system" &&
                    WebLanguage.Normalize("klingon") == "system" &&
                    WebLanguage.Normalize("ZH-cn") == "zh-CN", "system / bad / case");
                Check("only explicit modes override the header",
                    !WebLanguage.IsOverride("system") && WebLanguage.IsOverride("zh-CN") &&
                    WebLanguage.IsOverride("en-US"), "override flags");
                Check("chinese header keeps Chinese first",
                    WebLanguage.AcceptLanguage("zh-CN") == "zh-CN,zh;q=0.9,en;q=0.8,en-US;q=0.7",
                    WebLanguage.AcceptLanguage("zh-CN"));
                Check("english header does not advertise Chinese",
                    WebLanguage.AcceptLanguage("en-US") == "en-US,en;q=0.9" &&
                    !WebLanguage.AcceptLanguage("en-US").Contains("zh"),
                    WebLanguage.AcceptLanguage("en-US"));
                Check("system mode has no header / no script",
                    WebLanguage.AcceptLanguage("system") == "" &&
                    WebLanguage.Script("system") == "", "empty");
                Check("script redefines navigator.language and languages",
                    WebLanguage.Script("en-US").Contains("'en-US'") &&
                    WebLanguage.Script("en-US").Contains("Object.defineProperty") &&
                    WebLanguage.Script("zh-CN").Contains("[\"zh-CN\",\"zh\",\"en-US\",\"en\"]"),
                    "script built");
                Check("settings index maps all three modes",
                    SettingsPage.WebLanguageIndex("zh-CN") == 0 &&
                    SettingsPage.WebLanguageIndex("en-US") == 1 &&
                    SettingsPage.WebLanguageIndex("system") == 2 &&
                    SettingsPage.WebLanguageIndex("bogus") == 2, "0 / 1 / 2 / fallback");

                // ---------- 2. 两项语言在设置页里分开且都能翻译 ----------
                var zhPage = new SettingsPage(_w._settings);
                var zhPath = Path.Combine(_root, "out", "m10-settings-zh.png");
                RenderPage(zhPage, zhPath, 980, 1000);
                var zhTexts = CollectTexts(zhPage);
                Check("zh page shows both language rows",
                    zhTexts.Contains("界面语言") && zhTexts.Contains("网页语言"), "both rows");
                Check("zh page explains refresh-after-change",
                    zhTexts.Any(t => t != null && t.Contains("刷新后生效")), "hint ok");

                L.Apply("en-US");
                var enPage = new SettingsPage(_w._settings);   // 换语言必须重新 new，见 fixtest 教训
                var enPath = Path.Combine(_root, "out", "m10-settings-en.png");
                RenderPage(enPage, enPath, 980, 1000);
                var enTexts = CollectTexts(enPage);
                Check("en page shows UI language and Website language",
                    enTexts.Contains("UI language") && enTexts.Contains("Website language"), "both rows");
                var leaked = LeakedChinese(enTexts);
                Check("en page has no untranslated Chinese", leaked.Count == 0,
                    leaked.Count == 0 ? "none" : string.Join(" | ", leaked.Take(6)));
                Check("zh / en screenshots differ", FilesDiffer(zhPath, enPath), "byte compare");
                L.Apply("zh-CN");

                // ---------- 3. 界面语言切换不碰网页语言 ----------
                _w._settings.Language = "en-US";
                _w.OnSettingChanged("language");
                Check("switching UI language leaves WebLanguage alone",
                    _w._settings.WebLanguage == "system" &&
                    _w._browser.CurrentWebLanguage == "system" &&
                    _w._browser.LanguageFilterCount == 0,
                    "web=" + _w._browser.CurrentWebLanguage + " filters=" + _w._browser.LanguageFilterCount);
                _w._settings.Language = "zh-CN";
                _w.OnSettingChanged("language");

                // ---------- 4. 跟随系统：不干预 ----------
                var tab = await OpenPageAsync(_base + "?v=1");
                var native = await HeaderFromPageAsync(tab);
                Check("system mode keeps native Accept-Language",
                    !string.IsNullOrEmpty(native) && _w._browser.LanguageFilterCount == 0,
                    "native=" + native);
                _w.CaptureScreenshot(Path.Combine(_root, "out", "m10-window-system.png"));

                // ---------- 5. 中文：真实请求头 + navigator.language ----------
                _w._settings.WebLanguage = WebLanguage.Chinese;
                _w.OnSettingChanged("weblanguage");
                Check("zh override registered on the live core",
                    _w._browser.CurrentWebLanguage == "zh-CN" && _w._browser.LanguageFilterCount >= 1,
                    "filters=" + _w._browser.LanguageFilterCount);
                await ReloadAsync(tab, "?v=2");
                var zhHeader = await HeaderFromPageAsync(tab);
                Check("server received the Chinese Accept-Language",
                    zhHeader == "zh-CN,zh;q=0.9,en;q=0.8,en-US;q=0.7", "header=" + zhHeader);
                var zhNav = await JsStringAsync(tab, "navigator.language");
                Check("page sees navigator.language = zh-CN", zhNav == "zh-CN", "nav=" + zhNav);
                var zhList = await JsStringAsync(tab, "navigator.languages.join(',')");
                Check("page sees the Chinese language list",
                    zhList == "zh-CN,zh,en-US,en", "languages=" + zhList);

                // ---------- 6. 英文：同一个 core，运行时切换后重新导航 ----------
                _w._settings.WebLanguage = WebLanguage.English;
                _w.OnSettingChanged("weblanguage");
                await ReloadAsync(tab, "?v=3");
                var enHeader = await HeaderFromPageAsync(tab);
                Check("server received the English Accept-Language",
                    enHeader == "en-US,en;q=0.9" && !enHeader.Contains("zh"), "header=" + enHeader);
                var enNav = await JsStringAsync(tab, "navigator.language");
                Check("page sees navigator.language = en-US", enNav == "en-US", "nav=" + enNav);
                Check("English header differs from the native one", enHeader != native,
                    "native=" + native + " forced=" + enHeader);
                await CapturePreviewAsync(tab, Path.Combine(_root, "out", "m10-page-en.png"));
                Check("webview page screenshot written",
                    File.Exists(Path.Combine(_root, "out", "m10-page-en.png")), "out/m10-page-en.png");

                // ---------- 7. 切回跟随系统：过滤器撤掉，不再强制 ----------
                _w._settings.WebLanguage = WebLanguage.System;
                _w.OnSettingChanged("weblanguage");
                Check("switching back removes the filters",
                    _w._browser.CurrentWebLanguage == "system" && _w._browser.LanguageFilterCount == 0,
                    "filters=" + _w._browser.LanguageFilterCount);
                await ReloadAsync(tab, "?v=4");
                var backHeader = await HeaderFromPageAsync(tab);
                Check("native header restored after switching back",
                    !string.IsNullOrEmpty(backHeader) &&
                    backHeader != WebLanguage.AcceptLanguage("zh-CN") &&
                    backHeader != WebLanguage.AcceptLanguage("en-US"), "header=" + backHeader);

                // ---------- 8. 落盘与独立字段 ----------
                _w._settings.WebLanguage = WebLanguage.English;
                _w._settings.Language = "zh-CN";
                SettingsStore.Save(_w._settings);
                var reloaded = SettingsStore.Load();
                Check("WebLanguage persisted", reloaded.WebLanguage == "en-US", reloaded.WebLanguage);
                Check("UI Language persisted independently", reloaded.Language == "zh-CN", reloaded.Language);
                File.WriteAllText(SettingsStore.FilePath,
                    "{\"SchemaVersion\":3,\"Language\":\"en-US\",\"WebLanguage\":\"bogus\"}", Encoding.UTF8);
                Check("bad WebLanguage falls back to system",
                    SettingsStore.Load().WebLanguage == "system", "normalized on load");

                // ---------- 9. 收尾截图 ----------
                _w._settings.WebLanguage = WebLanguage.Chinese;
                _w._settings.Language = "zh-CN";
                _w.OnSettingChanged("weblanguage");
                await ReloadAsync(tab, "?v=5");
                await Task.Delay(300);
                _w.CaptureScreenshot(Path.Combine(_root, "out", "m10-window-zh.png"));
                Check("final window screenshot written",
                    File.Exists(Path.Combine(_root, "out", "m10-window-zh.png")), "out/m10-window-zh.png");

                Line("");
                Line("native Accept-Language = " + native);
                Line("zh forced = " + zhHeader + " | en forced = " + enHeader);
                Line("settings file = " + SettingsStore.FilePath);
            }

            // ---------- 浏览器读写 ----------

            private async Task<TabModel> OpenPageAsync(string url)
            {
                _w.NewTab(url);
                var tab = _w._active;
                var ready = await WaitReadyAsync(tab, url, 20000);
                if (!ready) Fail("open " + url, "page did not reach readyState=complete");
                return tab;
            }

            private async Task ReloadAsync(TabModel tab, string query)
            {
                var url = _base + query;
                await _w._browser.NavigateAsync(tab, url);
                var ready = await WaitReadyAsync(tab, url, 20000);
                if (!ready) Fail("reload " + query, "page did not reach readyState=complete");
            }

            private async Task<bool> WaitReadyAsync(TabModel tab, string url, int timeoutMs)
            {
                var started = Environment.TickCount;
                while (Environment.TickCount - started < timeoutMs)
                {
                    var core = _w._browser.Core(tab);
                    if (core != null)
                    {
                        try
                        {
                            var href = new JavaScriptSerializer().Deserialize<string>(
                                await core.ExecuteScriptAsync("location.href"));
                            if (href != null && href.StartsWith(url, StringComparison.OrdinalIgnoreCase))
                            {
                                var state = new JavaScriptSerializer().Deserialize<string>(
                                    await core.ExecuteScriptAsync("document.readyState"));
                                if (state == "complete") return true;
                            }
                        }
                        catch { }
                    }
                    await Task.Delay(100);
                }
                return false;
            }

            // 服务端把真实请求头写进 <title>，这里只做去前缀。
            private async Task<string> HeaderFromPageAsync(TabModel tab)
            {
                var title = await JsStringAsync(tab, "document.title");
                const string marker = "NanoEdge-Lang|";
                if (title == null || !title.StartsWith(marker, StringComparison.Ordinal)) return "";
                return title.Substring(marker.Length);
            }

            private async Task<string> JsStringAsync(TabModel tab, string expression)
            {
                var core = _w._browser.Core(tab);
                if (core == null) return "";
                try
                {
                    return new JavaScriptSerializer().Deserialize<string>(
                        await core.ExecuteScriptAsync(expression)) ?? "";
                }
                catch (Exception ex) { Line("[js] " + ex.Message); return ""; }
            }

            private async Task CapturePreviewAsync(TabModel tab, string path)
            {
                var core = _w._browser.Core(tab);
                if (core == null) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var stream = File.Create(path))
                        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                }
                catch (Exception ex) { Line("[capture] " + ex.Message); }
            }

            // ---------- 本机 HTTP 服务：回显 Accept-Language ----------

            private void StartServer()
            {
                _server = new TcpListener(IPAddress.Loopback, 0);
                _server.Start();
                _port = ((IPEndPoint)_server.LocalEndpoint).Port;
                _serverRunning = true;
                Task.Run(async () =>
                {
                    while (_serverRunning)
                    {
                        TcpClient client;
                        try { client = await _server.AcceptTcpClientAsync(); }
                        catch { break; }
                        var captured = client;
                        var ignored = Task.Run(() => Serve(captured));
                    }
                });
            }

            private void StopServer()
            {
                _serverRunning = false;
                try { if (_server != null) _server.Stop(); } catch { }
            }

            private void Serve(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[16384];
                        var read = stream.Read(buffer, 0, buffer.Length);
                        var request = Encoding.ASCII.GetString(buffer, 0, Math.Max(0, read));
                        var accept = "";
                        foreach (var line in request.Split('\n'))
                        {
                            if (!line.StartsWith("Accept-Language:", StringComparison.OrdinalIgnoreCase)) continue;
                            accept = line.Substring("Accept-Language:".Length).Trim();
                            break;
                        }
                        Write(stream, "200 OK", "text/html; charset=utf-8", Page(accept));
                    }
                    catch (IOException) { }   // 页面提前断开连接是正常的
                    catch (Exception ex) { Line("[server] " + ex.Message); }
                }
            }

            private static string Page(string accept)
            {
                var safe = (accept ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
                return "<!doctype html><html><head><meta charset=\"utf-8\">" +
                       "<title>NanoEdge-Lang|" + safe + "</title>" +
                       "<style>body{font:16px/1.6 'Segoe UI',sans-serif;margin:0;padding:36px;background:#fff;color:#1b2430}" +
                       "h1{font-size:22px;margin:0 0 18px}b{background:#eef3ff;padding:2px 6px;border-radius:4px}" +
                       "p{margin:10px 0}</style></head><body>" +
                       "<h1>NanoEdge 网页语言自测页</h1>" +
                       "<p>网站收到的 Accept-Language：<b id=\"al\">" + safe + "</b></p>" +
                       "<p>页面看到的 navigator.language：<b id=\"nl\"></b></p>" +
                       "<p>页面看到的 navigator.languages：<b id=\"nls\"></b></p>" +
                       "<script>document.getElementById('nl').textContent=navigator.language;" +
                       "document.getElementById('nls').textContent=navigator.languages.join(', ');</script>" +
                       "</body></html>";
            }

            private static void Write(NetworkStream stream, string status, string contentType, string body)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                var header = "HTTP/1.1 " + status + "\r\n" +
                             "Content-Type: " + contentType + "\r\n" +
                             "Content-Length: " + bytes.Length + "\r\n" +
                             "Cache-Control: no-store\r\n" +
                             "Connection: close\r\n\r\n";
                var head = Encoding.ASCII.GetBytes(header);
                stream.Write(head, 0, head.Length);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }

            // ---------- 设置页文本与截图 ----------

            private static List<string> CollectTexts(DependencyObject root)
            {
                var texts = new List<string>();
                if (root != null) Walk(root, texts);
                return texts;
            }

            private static void Walk(DependencyObject node, List<string> texts)
            {
                var count = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);
                    var block = child as System.Windows.Controls.TextBlock;
                    if (block != null && !string.IsNullOrEmpty(block.Text)) texts.Add(block.Text);
                    var control = child as System.Windows.Controls.ContentControl;
                    if (control != null)
                    {
                        var text = control.Content as string;
                        if (!string.IsNullOrEmpty(text)) texts.Add(text);
                    }
                    Walk(child, texts);
                }
            }

            private static List<string> LeakedChinese(List<string> texts)
            {
                var leaks = new List<string>();
                foreach (var text in texts)
                {
                    if (string.IsNullOrEmpty(text)) continue;
                    if (AllowedCjk.Any(allowed => text == allowed)) continue;
                    if (text.Contains(":\\")) continue;
                    if (Cjk.IsMatch(text)) leaks.Add(text);
                }
                return leaks;
            }

            private static bool FilesDiffer(string a, string b)
            {
                if (!File.Exists(a) || !File.Exists(b)) return false;
                var left = File.ReadAllBytes(a);
                var right = File.ReadAllBytes(b);
                if (left.Length != right.Length) return true;
                for (var i = 0; i < left.Length; i++)
                    if (left[i] != right[i]) return true;
                return false;
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
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var stream = File.Create(path)) encoder.Save(stream);
            }

            // ---------- 报告 ----------

            private void Check(string name, bool passed, string detail)
            {
                if (passed) _passes++;
                else _failures.Add(name);
                Line((passed ? "[PASS] " : "[FAIL] ") + name + "  " + (detail ?? ""));
            }

            private void Fail(string name, string detail)
            {
                _failures.Add(name);
                Line("[FAIL] " + name + "  " + detail);
            }

            private void Line(string text)
            {
                _report.AppendLine(text);
                Diagnostics.Log("[m10test] " + text);
            }

            private void WriteReport()
            {
                try
                {
                    var path = Path.Combine(_root ?? Path.GetTempPath(), "out", "m10-report.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
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
