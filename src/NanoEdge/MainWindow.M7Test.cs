using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NanoEdge
{
    public sealed partial class MainWindow
    {
        // --m7test：0.18 验收。
        // 覆盖：会话写入合并、关闭右侧、favicon、复制/中键关闭、最近关闭 + Ctrl+Shift+T、
        // Ctrl+F5 硬刷新、Ctrl+Shift+M 内存面板、起始页快捷方式编辑、退出前释放视图。
        private sealed class M7SelfTest
        {
            private const ModifierKeys Ctrl = ModifierKeys.Control;
            private const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;

            private readonly MainWindow _w;
            private readonly StringBuilder _report = new StringBuilder();
            private readonly List<string> _failures = new List<string>();
            private int _passes;
            private string _root;
            private TcpListener _server;
            private bool _serverRunning;
            private byte[] _iconPng;
            private string _html;
            private readonly List<string> _requests = new List<string>();

            public M7SelfTest(MainWindow window) { _w = window; }

            public async Task RunAsync()
            {
                _root = FindRoot();
                Line("M7 self-test (0.18 tab ops / favicon / start page) " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
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
                var temp = Path.Combine(Path.GetTempPath(), "nanoedge-m7");
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
                Directory.CreateDirectory(temp);

                // 全部落到临时目录，不碰用户数据。
                SettingsStore.FilePath = Path.Combine(temp, "settings.json");
                SettingsStore.Persist = true;
                SessionStore.FilePath = Path.Combine(temp, "session.json");
                HistoryStore.FilePath = Path.Combine(temp, "history.json");
                ZoomStore.FilePath = Path.Combine(temp, "zoom.json");
                DownloadManager.FilePath = Path.Combine(temp, "downloads.json");
                SessionStore.Writes = 0;

                _w._noPersist = false;   // 本节要验证真实落盘
                _w._settings.Language = "zh-CN";
                _w._settings.Theme = "dark";
                _w._settings.Layout = LayoutMode.Compact;
                _w._settings.Shortcuts = new List<Shortcut>
                {
                    new Shortcut { Title = "Bing", Url = "https://www.bing.com/" },
                    new Shortcut { Title = "GitHub", Url = "https://github.com/" }
                };
                L.Apply("zh-CN");
                Theme.Apply("dark");
                _w.ApplyLayout();

                _iconPng = MakePng();
                // 用真实的 /favicon.png 链接，验证「截获浏览器那次响应」的路径；
                // data URI 路径另用 Favicons.DecodeDataUri 单独验。
                _html = "<!doctype html><html><head><meta charset=\"utf-8\">" +
                        "<title>NanoEdge icon test</title>" +
                        "<link rel=\"icon\" type=\"image/png\" href=\"/favicon.png\">" +
                        "</head><body style=\"font:16px system-ui\"><h1>icon test</h1></body></html>";
                StartServer();
            }

            private async Task RunCoreAsync()
            {
                // ---------- 1. 会话写入合并 ----------
                _w._sessionTimer.Stop();
                _w._sessionDirty = false;
                SessionStore.Writes = 0;
                for (var i = 0; i < 6; i++) _w.SaveSession();
                Check("session writes are coalesced (nothing written yet)", SessionStore.Writes == 0,
                    "writes=" + SessionStore.Writes);
                await Task.Delay(450);
                Check("session write happened once after the debounce window", SessionStore.Writes == 1,
                    "writes=" + SessionStore.Writes);
                Check("session.json exists", File.Exists(SessionStore.FilePath), SessionStore.FilePath);
                _w.WriteSessionNow();
                Check("flush without changes does not write again", SessionStore.Writes == 1,
                    "writes=" + SessionStore.Writes);

                // ---------- 2. 关闭右侧 ----------
                _w.NewTab(); _w.NewTab(); _w.NewTab();
                var tabs = _w._tabs.ToList();
                Check("four tabs open", tabs.Count == 4, "count=" + tabs.Count);
                _w.CloseTabsToRight(tabs[0]);
                Check("close to the right keeps only the first tab",
                    _w._tabs.Count == 1 && ReferenceEquals(_w._tabs[0], tabs[0]), "count=" + _w._tabs.Count);

                // ---------- 3. favicon ----------
                var url = "http://127.0.0.1:" + Port() + "/page";
                _w.NewTab(url);
                var tab = _w._active;
                var loaded = await WaitForAsync(() => tab != null && !tab.IsStartPage && _w._browser.HasLiveView(tab), 10000);
                Check("local page loaded", loaded, tab == null ? "no tab" : tab.Url);
                Check("test icon itself is a decodable PNG",
                    Favicons.Decode(new MemoryStream(_iconPng)) != null, "png");
                Check("inline data-URI icon decodes without a network request",
                    Favicons.DecodeDataUri("data:image/png;base64," +
                        Convert.ToBase64String(_iconPng)) != null, "data uri");
                _w._browser.FocusContent(tab);
                var iconReady = await WaitForAsync(() => tab != null && tab.Favicon != null, 8000);
                Check("favicon captured from the page", iconReady,
                    tab == null || tab.Favicon == null ? "null" : "ok");
                string requests;
                lock (_requests) requests = string.Join(" | ", _requests.ToArray());
                Line("server requests = " + requests);
                Check("favicon cached by origin", Favicons.Get(url) != null, Favicons.Key(url));
                var chip = FindChip(tab);
                Check("tab chip renders an Image instead of the letter",
                    chip != null && HasImage(chip), chip == null ? "no chip" : "chip ok");

                _w.WindowState = WindowState.Normal;
                _w.Width = 1100;
                _w.Height = 760;
                await Task.Delay(400);
                _w.UpdateLayout();
                _w.CaptureScreenshot(Path.Combine(_root, "out", "m7-window.png"));
                Check("window screenshot written",
                    File.Exists(Path.Combine(_root, "out", "m7-window.png")), "out/m7-window.png");

                // ---------- 4. 复制 + 中键关闭 ----------
                var before = _w._tabs.Count;
                _w.DuplicateTab(tab);
                var copy = _w._active;
                var copied = await WaitForAsync(() => copy != null && !ReferenceEquals(copy, tab) && copy.Url == url, 8000);
                Check("duplicate tab opens the same URL", copied && _w._tabs.Count == before + 1,
                    "count=" + _w._tabs.Count + " url=" + (copy == null ? "" : copy.Url));

                _w.RebuildTabStrip();
                _w.UpdateLayout();
                var copyChip = FindChip(copy);
                Check("duplicated tab has a chip", copyChip != null, "chip");
                if (copyChip != null)
                {
                    var middle = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle)
                    {
                        RoutedEvent = UIElement.MouseDownEvent,
                        Source = copyChip
                    };
                    copyChip.RaiseEvent(middle);
                }
                Check("middle click closes the tab", !_w._tabs.Contains(copy), "count=" + _w._tabs.Count);

                // ---------- 5. 最近关闭 + Ctrl+Shift+T ----------
                var closedUrl = tab.Url;
                _w.CloseTab(tab);
                Check("tab closed", !_w._tabs.Contains(tab), "count=" + _w._tabs.Count);
                var handled = _w.HandleShortcut(Key.T, CtrlShift, Key.None);
                Check("Ctrl+Shift+T is handled", handled, "handled=" + handled);
                var reopened = await WaitForAsync(() => _w._active != null && _w._active.Url == closedUrl, 8000);
                Check("reopened tab restores the URL", reopened,
                    _w._active == null ? "no tab" : _w._active.Url);

                // ---------- 6. Ctrl+F5 ----------
                var beforeHard = _w._browser.HardReloads;
                var hardHandled = _w.HandleShortcut(Key.F5, Ctrl, Key.None);
                await Task.Delay(400);
                Check("Ctrl+F5 triggers a hard reload",
                    hardHandled && _w._browser.HardReloads == beforeHard + 1,
                    "hard=" + _w._browser.HardReloads);

                // ---------- 7. Ctrl+Shift+M ----------
                var panelHandled = _w.HandleShortcut(Key.M, CtrlShift, Key.None);
                await Task.Delay(200);
                Check("Ctrl+Shift+M opens a pinned memory panel",
                    panelHandled && _w._memoryPanel != null && _w._memoryPanel.IsOpen && _w._memoryPanel.IsPinned,
                    _w._memoryPanel == null ? "no panel" : "open=" + _w._memoryPanel.IsOpen);
                if (_w._memoryPanel != null) _w._memoryPanel.Close();

                // ---------- 8. 起始页快捷方式编辑 ----------
                var countBefore = _w._settings.Shortcuts.Count;
                var page = new StartPage(_w._settings);
                page.BeginAddForTest();
                page.SetEditorForTest("示例站点", "example.com");
                RenderPage(page, Path.Combine(_root, "out", "m7-startpage-edit.png"), 900, 620);
                Check("start page editor is visible", page.EditorVisibleForTest, "visible");
                page.CommitForTest();
                Check("shortcut added", _w._settings.Shortcuts.Count == countBefore + 1,
                    "count=" + _w._settings.Shortcuts.Count);
                var added = _w._settings.Shortcuts.FirstOrDefault(s => s.Title == "示例站点");
                Check("shortcut URL normalized to https", added != null && added.Url == "https://example.com",
                    added == null ? "missing" : added.Url);
                var reloaded = SettingsStore.Load();
                Check("shortcut persisted to settings.json",
                    reloaded.Shortcuts.Any(s => s.Url == "https://example.com"),
                    "count=" + reloaded.Shortcuts.Count);
                Check("start page screenshot written",
                    File.Exists(Path.Combine(_root, "out", "m7-startpage-edit.png")), "out/m7-startpage-edit.png");

                // 编辑已有项：替换而不是追加
                page.BeginEditForTest(0);
                page.SetEditorForTest("Bing 新", "https://cn.bing.com/");
                page.CommitForTest();
                Check("editing replaces the entry instead of appending",
                    _w._settings.Shortcuts.Count == countBefore + 1 &&
                    _w._settings.Shortcuts[0].Url == "https://cn.bing.com/",
                    "count=" + _w._settings.Shortcuts.Count);

                // 非法 URL：拒绝写入，编辑器保持打开
                var beforeInvalid = _w._settings.Shortcuts.Count;
                page.BeginAddForTest();
                page.SetEditorForTest("无效", "not a url");
                page.CommitForTest();
                Check("invalid URL is rejected and the editor stays open",
                    _w._settings.Shortcuts.Count == beforeInvalid && page.EditorVisibleForTest,
                    "count=" + _w._settings.Shortcuts.Count);

                // ---------- 9. 退出前释放视图 ----------
                _w._browser.DisposeAll();
                Check("dispose all releases every view", _w._browser.LiveViewCount(_w._tabs) == 0,
                    "live=" + _w._browser.LiveViewCount(_w._tabs));
                Check("tab host is empty after dispose", _w._tabHost.Children.Count == 0,
                    "children=" + _w._tabHost.Children.Count);
                _w._browser.DisposeAll();
                Check("dispose all is idempotent", _w._tabHost.Children.Count == 0, "ok");
            }

            // ---------- 标签 chip 查找 ----------

            private FrameworkElement FindChip(TabModel tab)
            {
                if (_w._tabStripPanel == null || tab == null) return null;
                foreach (var child in _w._tabStripPanel.Children)
                {
                    var border = child as Border;
                    if (border != null && ReferenceEquals(border.Tag, tab)) return border;
                }
                return null;
            }

            private static bool HasImage(DependencyObject root)
            {
                var count = VisualTreeHelper.GetChildrenCount(root);
                for (var i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);
                    if (child is Image) return true;
                    if (HasImage(child)) return true;
                }
                return false;
            }

            // ---------- 本地站点 ----------

            private int Port() { return ((IPEndPoint)_server.LocalEndpoint).Port; }

            private void StartServer()
            {
                _server = new TcpListener(IPAddress.Loopback, 0);
                _server.Start();
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

            private void Serve(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[4096];
                        var read = stream.Read(buffer, 0, buffer.Length);
                        var request = Encoding.ASCII.GetString(buffer, 0, Math.Max(0, read));
                        var parts = request.Split(' ');
                        var path = parts.Length > 1 ? parts[1] : "/";
                        lock (_requests) _requests.Add(path);

                        byte[] payload;
                        string contentType;
                        if (path.IndexOf("/favicon.png", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            payload = _iconPng;
                            contentType = "image/png";
                        }
                        else
                        {
                            payload = Encoding.UTF8.GetBytes(_html);
                            contentType = "text/html; charset=utf-8";
                        }

                        var header = "HTTP/1.1 200 OK\r\nContent-Type: " + contentType +
                                     "\r\nContent-Length: " + payload.Length +
                                     "\r\nConnection: close\r\n\r\n";
                        var head = Encoding.ASCII.GetBytes(header);
                        stream.Write(head, 0, head.Length);
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush();
                    }
                    catch (Exception ex) { Diagnostics.Log("[m7test][server] " + ex.Message); }
                }
            }

            private void StopServer()
            {
                _serverRunning = false;
                try { if (_server != null) _server.Stop(); } catch { }
            }

            private static byte[] MakePng()
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.DeepPink, null, new Rect(0, 0, 16, 16));
                    dc.DrawRectangle(Brushes.White, null, new Rect(4, 4, 8, 8));
                }
                var bitmap = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    return stream.ToArray();
                }
            }

            private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
            {
                var started = Environment.TickCount;
                while (Environment.TickCount - started < timeoutMs)
                {
                    if (condition()) return true;
                    await Task.Delay(100);
                }
                return condition();
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
                Diagnostics.Log("[m7test] " + text);
            }

            private void WriteReport()
            {
                try
                {
                    var path = Path.Combine(_root ?? Path.GetTempPath(), "out", "m7-report.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, _report.ToString(), Encoding.UTF8);
                }
                catch { }
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
