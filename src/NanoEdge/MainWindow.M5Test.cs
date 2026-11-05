using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NanoEdge
{
    public sealed partial class MainWindow
    {
        // --m5test：M5 简化版（伪装 / Alt+` 切换 / 密封标签通知开关）。
        // 用两个本机 127.0.0.1 服务做两种 origin：一个验"通知被拒"，一个验"通知放行"。
        // 所有数据落临时目录，不写用户历史/书签/会话。
        private sealed class M5SelfTest
        {
            private readonly MainWindow _w;
            private readonly StringBuilder _report = new StringBuilder();
            private readonly object _reportLock = new object();
            private readonly List<string> _failures = new List<string>();
            private int _passes;
            private string _root;
            private string _temp;

            private readonly List<TcpListener> _servers = new List<TcpListener>();
            private volatile bool _running;
            private int _portDenied;
            private int _portAllowed;

            public M5SelfTest(MainWindow window) { _w = window; }

            public async Task RunAsync()
            {
                _root = FindRoot();
                Line("M5 secret tab self-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Line("test data dir = " + Path.Combine(Path.GetTempPath(), "nanoedge-m5"));
                try
                {
                    Setup();
                    await RunCoreAsync();
                }
                catch (Exception ex) { Fail("unhandled", ex.ToString()); }
                finally
                {
                    StopServers();
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
                _temp = Path.Combine(Path.GetTempPath(), "nanoedge-m5");
                if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
                Directory.CreateDirectory(_temp);

                HistoryStore.FilePath = Path.Combine(_temp, "history.json");
                HistoryStore.Enabled = true;
                HistoryStore.Clear();

                BookmarkStore.FilePath = Path.Combine(_temp, "bookmarks.json");
                BookmarkStore.All();   // 先把临时文件装载起来，别让 Ctrl+D 写到用户目录

                // 设置项来自用户文件，自测里强制成默认值，避免被上一次手动开关影响。
                _w._settings.SecretNotifications = false;

                _portDenied = StartServer();
                _portAllowed = StartServer();
                Line("[server] denied-origin port=" + _portDenied + " allowed-origin port=" + _portAllowed);
            }

            private async Task RunCoreAsync()
            {
                // ---------- 1. 新建密封标签：伪装 ----------
                _w.NewSecretTab();
                var secret = _w._active;
                Check("new secret tab is marked secret", secret != null && secret.IsSecret,
                    secret == null ? "no active tab" : "IsSecret=" + secret.IsSecret);
                Check("secret tab displays as 文档", secret != null && secret.DisplayTitle == "文档",
                    secret == null ? "-" : secret.DisplayTitle);

                var normalBefore = _w._tabs.Count(t => !t.IsSecret);
                Check("secret tab is an extra tab", _w._tabs.Count == normalBefore + 1,
                    "tabs=" + _w._tabs.Count + " normal=" + normalBefore);

                // ---------- 2. 真实页面：标题不露、地址栏清空 ----------
                var logPath = Path.Combine(AppPaths.Logs, "nanoedge.log");
                var logBefore = FileLengthSafe(logPath);
                var urlDenied = "http://127.0.0.1:" + _portDenied + "/page";
                _w.Navigate(secret, urlDenied);
                var ready = await WaitReadyAsync(secret, urlDenied, 20000);
                Check("secret page loaded", ready, "ready=" + ready);
                Check("navigation log does not contain the secret url",
                    ReadFrom(logPath, logBefore).IndexOf(urlDenied, StringComparison.OrdinalIgnoreCase) < 0,
                    "log bytes=" + (FileLengthSafe(logPath) - logBefore));

                var titleChanged = await WaitForAsync(() => secret.Title != "文档", 5000);
                Check("real document title is kept internally", titleChanged,
                    "title=" + secret.Title);
                Check("tab still displays 文档 after navigation", secret.DisplayTitle == "文档",
                    secret.DisplayTitle);
                Check("address bar stays empty on a secret tab", _w._address.Text == "",
                    "address='" + _w._address.Text + "'");

                // ---------- 3. 历史 / 会话不落盘 ----------
                Check("secret navigation is not written to history", HistoryStore.All().Count == 0,
                    "entries=" + HistoryStore.All().Count + " " +
                    string.Join(",", HistoryStore.All().Select(e => e.Url)));

                var session = _w.BuildSession();
                Check("session excludes secret tabs", session.Tabs.All(t => !t.IsSecret),
                    "tabs=" + session.Tabs.Count);
                Check("session has no active tab when the secret tab is active",
                    string.IsNullOrEmpty(session.ActiveTabId), "activeId='" + session.ActiveTabId + "'");
                Check("session does not contain the secret url",
                    session.Tabs.All(t => t.Url != secret.Url),
                    "rows=" + session.Tabs.Count);

                // ---------- 4. 通知默认关闭 ----------
                var denied = await WaitPermissionAsync(secret, 12000);
                Check("notifications are denied when the switch is off", denied == "denied", denied);

                // 控制组：普通标签导航会进历史
                var normalUrl = "http://127.0.0.1:" + _portDenied + "/page?normal=1";
                _w.NewTab(normalUrl);
                var normal = _w._active;
                await WaitReadyAsync(normal, normalUrl, 20000);
                Check("normal navigation is recorded in history", HistoryStore.All().Count == 1,
                    "entries=" + HistoryStore.All().Count);

                // ---------- 5. Alt+` 切换 ----------
                _w.SelectTab(normal);
                _w.HandleShortcut(System.Windows.Input.Key.OemTilde,
                    System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.None);
                Check("alt+` switches to the secret tab", ReferenceEquals(_w._active, secret),
                    "active=" + (_w._active == null ? "-" : _w._active.DisplayTitle));

                _w.HandleShortcut(System.Windows.Input.Key.OemTilde,
                    System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.None);
                Check("alt+` switches back to the previous normal tab",
                    ReferenceEquals(_w._active, normal),
                    "active=" + (_w._active == null ? "-" : _w._active.DisplayTitle));

                // ---------- 6. 通知开 ----------
                _w._settings.SecretNotifications = true;
                _w.SelectTab(secret);
                var urlAllowed = "http://127.0.0.1:" + _portAllowed + "/page";
                _w.Navigate(secret, urlAllowed);
                var ready2 = await WaitReadyAsync(secret, urlAllowed, 20000);
                Check("secret page reloaded on a second origin", ready2, "ready=" + ready2);
                var allowed = await WaitPermissionAsync(secret, 12000);
                Check("notifications are allowed when the switch is on", allowed == "granted", allowed);

                // ---------- 7. 视觉证据 ----------
                _w.WindowState = WindowState.Normal;
                _w.Width = 1100;
                _w.Height = 700;
                _w.SelectTab(secret);
                await Task.Delay(400);
                _w.UpdateLayout();
                _w.CaptureScreenshot(Path.Combine(_root, "out", "m5-window.png"));
                Check("window screenshot written", File.Exists(Path.Combine(_root, "out", "m5-window.png")),
                    "out/m5-window.png");

                Render(_w._toolbarHost, Path.Combine(_root, "out", "m5-tabstrip.png"));
                Check("toolbar rendered", File.Exists(Path.Combine(_root, "out", "m5-tabstrip.png")),
                    "out/m5-tabstrip.png");

                Line("");
                Line("history file = " + HistoryStore.FilePath + " (" + HistoryStore.All().Count + " entries)");
                Line("bookmarks file = " + BookmarkStore.FilePath + " (" + BookmarkStore.All().Count + " items)");
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

            private static long FileLengthSafe(string path)
            {
                try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
                catch { return 0; }
            }

            private static string ReadFrom(string path, long offset)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        stream.Seek(offset, SeekOrigin.Begin);
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                            return reader.ReadToEnd();
                    }
                }
                catch { return ""; }
            }

            // ---------- 页面 / 通知 ----------

            private async Task<string> WaitPermissionAsync(TabModel tab, int timeoutMs)
            {
                var started = Environment.TickCount;
                var value = "pending";
                while (Environment.TickCount - started < timeoutMs)
                {
                    value = await EvalStringAsync(tab, "String(window.__m5 && __m5.perm)");
                    if (!string.IsNullOrEmpty(value) && value != "pending") return value;
                    await Task.Delay(150);
                }
                return string.IsNullOrEmpty(value) ? "(none)" : value;
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
                            var raw = await core.ExecuteScriptAsync("location.href");
                            var href = new JavaScriptSerializer().Deserialize<string>(raw);
                            if (href != null && href.StartsWith(url, StringComparison.OrdinalIgnoreCase))
                            {
                                var stateRaw = await core.ExecuteScriptAsync("document.readyState");
                                if (new JavaScriptSerializer().Deserialize<string>(stateRaw) == "complete")
                                    return true;
                            }
                        }
                        catch { }
                    }
                    await Task.Delay(100);
                }
                return false;
            }

            private async Task<string> EvalStringAsync(TabModel tab, string expression)
            {
                var core = _w._browser.Core(tab);
                if (core == null) return null;
                try
                {
                    var raw = await core.ExecuteScriptAsync(expression);
                    return new JavaScriptSerializer().Deserialize<string>(raw);
                }
                catch { return null; }
            }

            // ---------- 本机服务 ----------

            private int StartServer()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _servers.Add(listener);
                _running = true;

                Task.Run(async () =>
                {
                    while (_running)
                    {
                        TcpClient client;
                        try { client = await listener.AcceptTcpClientAsync(); }
                        catch { break; }
                        var captured = client;
                        var ignored = Task.Run(() => Serve(captured));
                    }
                });
                return port;
            }

            private void StopServers()
            {
                _running = false;
                foreach (var listener in _servers)
                {
                    try { listener.Stop(); } catch { }
                }
                _servers.Clear();
            }

            private void Serve(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[8192];
                        var read = stream.Read(buffer, 0, buffer.Length);
                        var request = Encoding.ASCII.GetString(buffer, 0, Math.Max(0, read));
                        var parts = request.Split(' ');
                        var path = parts.Length > 1 ? parts[1] : "/";

                        if (path.StartsWith("/page", StringComparison.OrdinalIgnoreCase))
                        {
                            Write(stream, Page());
                            return;
                        }
                        Write(stream, "<!doctype html><title>404</title>not found");
                    }
                    catch (IOException) { }
                    catch (Exception ex) { Line("[server] " + ex.Message); }
                }
            }

            private static void Write(NetworkStream stream, string body)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                var header = "HTTP/1.1 200 OK\r\n" +
                             "Content-Type: text/html; charset=utf-8\r\n" +
                             "Content-Length: " + bytes.Length + "\r\n" +
                             "Cache-Control: no-store\r\n" +
                             "Connection: close\r\n\r\n";
                var head = Encoding.ASCII.GetBytes(header);
                stream.Write(head, 0, head.Length);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }

            private static string Page()
            {
                return "<!doctype html><html><head><meta charset=\"utf-8\"><title>m5 page</title>" +
                       "<script>window.__m5 = { perm: 'pending' };" +
                       "if (window.Notification && Notification.requestPermission) {" +
                       "  Notification.requestPermission().then(function (p) { __m5.perm = p; })" +
                       "    .catch(function (e) { __m5.perm = 'error:' + e; });" +
                       "} else { __m5.perm = 'unsupported'; }</script>" +
                       "</head><body style=\"margin:0;padding:18px;background:#111;color:#eee;" +
                       "font:14px 'Segoe UI',sans-serif\">" +
                       "<h1 style=\"font-size:18px;margin:0 0 8px 0\">M5 notification probe</h1>" +
                       "<p style=\"color:#888\">served by 127.0.0.1</p></body></html>";
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
                lock (_reportLock) _report.AppendLine(text);
                Diagnostics.Log("[m5test] " + text);
            }

            private void WriteReport()
            {
                try
                {
                    var path = Path.Combine(_root ?? Path.GetTempPath(), "out", "m5-report.txt");
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.WriteAllText(path, _report.ToString(), Encoding.UTF8);
                }
                catch { }
            }

            private static void Render(FrameworkElement element, string path)
            {
                var width = Math.Max(120, element.ActualWidth > 0 ? element.ActualWidth : element.Width);
                if (double.IsNaN(width) || width <= 0) width = 900;
                element.Measure(new Size(width, 200));
                var height = Math.Max(32, element.DesiredSize.Height);
                element.Arrange(new Rect(0, 0, width, height));
                element.UpdateLayout();

                var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(element);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                using (var stream = File.Create(path)) encoder.Save(stream);
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
