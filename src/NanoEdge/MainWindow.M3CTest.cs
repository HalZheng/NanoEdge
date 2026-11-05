using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NanoEdge
{
    public sealed partial class MainWindow
    {
        // --m3ctest：M3-c 缩放 + 下载的验收脚本。
        // 缩放档位/站点键用断言覆盖；下载用本机 127.0.0.1 的一次真实 HTTP 附件响应，
        // 不 mock WebView2 事件。所有数据落临时目录，跑完自动退出。
        private sealed class M3CSelfTest
        {
            private const ModifierKeys Ctrl = ModifierKeys.Control;

            private readonly MainWindow _w;
            private readonly StringBuilder _report = new StringBuilder();
            private readonly List<string> _failures = new List<string>();
            private int _passes;
            private string _root;
            private string _temp;

            private TcpListener _server;
            private volatile bool _serverRunning;
            private byte[] _payload;

            public M3CSelfTest(MainWindow window) { _w = window; }

            public async Task RunAsync()
            {
                _root = FindRoot();
                Line("M3-c zoom / downloads self-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Line("test data dir = " + Path.Combine(Path.GetTempPath(), "nanoedge-m3c"));
                try
                {
                    Setup();
                    await RunCoreAsync();
                }
                catch (Exception ex)
                {
                    Fail("unhandled", ex.ToString());
                }
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
                _temp = Path.Combine(Path.GetTempPath(), "nanoedge-m3c");
                var files = Path.Combine(_temp, "files");
                Directory.CreateDirectory(files);
                foreach (var f in Directory.GetFiles(files)) File.Delete(f);

                ZoomStore.FilePath = Path.Combine(_temp, "zoom.json");
                DownloadManager.FilePath = Path.Combine(_temp, "downloads.json");
                AppPaths.DownloadsFolder = files;
                foreach (var f in new[] { ZoomStore.FilePath, DownloadManager.FilePath })
                    if (File.Exists(f)) File.Delete(f);

                ZoomStore.Persist = true;
                DownloadManager.Persist = true;
                ZoomStore.Reload();
                DownloadManager.Reload();

                _payload = Encoding.ASCII.GetBytes(new string('N', 192 * 1024));
            }

            private async Task RunCoreAsync()
            {
                // ---------- 1. 缩放阶梯 ----------
                Check("zoom ladder up 100% -> 110%", Near(ZoomLevels.Next(1.0), 1.1),
                    ZoomLevels.Next(1.0).ToString());
                Check("zoom ladder up 110% -> 125%", Near(ZoomLevels.Next(1.1), 1.25),
                    ZoomLevels.Next(1.1).ToString());
                Check("zoom ladder down 125% -> 110%", Near(ZoomLevels.Previous(1.25), 1.1),
                    ZoomLevels.Previous(1.25).ToString());
                Check("zoom clamped at 500%", Near(ZoomLevels.Next(5.0), 5.0), ZoomLevels.Next(5.0).ToString());
                Check("zoom clamped at 25%", Near(ZoomLevels.Previous(0.25), 0.25),
                    ZoomLevels.Previous(0.25).ToString());
                Check("out-of-range value snaps to a step", Near(ZoomLevels.Clamp(1.32), 1.25),
                    ZoomLevels.Clamp(1.32).ToString());
                Check("zoom label", ZoomLevels.Label(1.5) == "150%", ZoomLevels.Label(1.5));

                // ---------- 2. 站点键 ----------
                Check("site key lowercases host", ZoomStore.SiteOf("https://Example.COM/a") == "example.com",
                    ZoomStore.SiteOf("https://Example.COM/a"));
                Check("site key keeps dev port", ZoomStore.SiteOf("http://localhost:3000/x") == "localhost:3000",
                    ZoomStore.SiteOf("http://localhost:3000/x"));
                Check("site key drops default port",
                    ZoomStore.SiteOf("https://example.com:443/") == "example.com",
                    ZoomStore.SiteOf("https://example.com:443/"));
                Check("file pages share one key", ZoomStore.SiteOf("file:///D:/a/b.html") == "本地文件",
                    ZoomStore.SiteOf("file:///D:/a/b.html"));
                Check("internal page has no zoom site", ZoomStore.SiteOf("nano://history") == "",
                    "site='' " );

                // ---------- 3. 站点记忆落盘 ----------
                ZoomStore.Put("example.com", 1.5);
                Check("zoom.json written", File.Exists(ZoomStore.FilePath),
                    "bytes=" + (File.Exists(ZoomStore.FilePath) ? new FileInfo(ZoomStore.FilePath).Length : 0));
                ZoomStore.Reload();
                Check("zoom survives reload", Near(ZoomStore.Get("example.com"), 1.5),
                    ZoomStore.Get("example.com").ToString());
                ZoomStore.Forget("example.com");
                ZoomStore.Reload();
                Check("reset removes the site entry", Near(ZoomStore.Get("example.com"), 1.0),
                    ZoomStore.Get("example.com").ToString());

                // ---------- 4. 真实 WebView2 + 快捷键 ----------
                var probe = Path.Combine(_temp, "zoom-probe.html");
                File.WriteAllText(probe,
                    "<!doctype html><title>zoom probe</title><body style='font:22px sans-serif'>" +
                    "NanoEdge zoom probe</body>", Encoding.UTF8);
                var probeUrl = new Uri(probe).AbsoluteUri;
                _w.NewTab(probeUrl);
                var tab = _w._active;
                var ready = await WaitForAsync(
                    () => _w._browser.HasLiveView(tab) && _w._browser.Core(tab) != null, 15000);
                Check("webview2 ready for zoom", ready,
                    "live=" + _w._browser.HasLiveView(tab) + " url=" + tab.Url);

                // 会话可能记着"最大化"，先还原再收小，否则截图是整块超宽屏、字看不清。
                _w.WindowState = WindowState.Normal;
                _w.Width = 1000;
                _w.Height = 640;
                await Task.Delay(300);

                var site = ZoomStore.SiteOf(probeUrl);
                Press(Key.D0);
                Check("ctrl+0 -> 100%", Near(_w._browser.GetZoom(tab), 1.0),
                    _w._browser.GetZoom(tab).ToString());
                Press(Key.OemPlus); Press(Key.OemPlus); Press(Key.OemPlus);
                Check("ctrl++ x3 -> 150%", Near(_w._browser.GetZoom(tab), 1.5),
                    _w._browser.GetZoom(tab).ToString());
                Check("ZoomFactor really written to WebView2", Near(_w._browser.ReadZoom(tab), 1.5),
                    "view.ZoomFactor=" + _w._browser.ReadZoom(tab));
                Check("zoom stored for the site", Near(ZoomStore.Get(site), 1.5),
                    site + "=" + ZoomStore.Get(site));
                Check("status bar shows zoom", _w._statusText.Text.Contains("150%"),
                    _w._statusText.Text);
                // WebView2 是 HwndHost，窗口截图里内容区必然是黑块；缩放提示单独渲染一张。
                Render(_w._toast.Visual, Path.Combine(_root, "out", "m3c-zoom-toast.png"));
                Check("zoom toast rendered",
                    File.Exists(Path.Combine(_root, "out", "m3c-zoom-toast.png")),
                    "out/m3c-zoom-toast.png");
                _w.UpdateLayout();
                _w.CaptureScreenshot(Path.Combine(_root, "out", "m3c-zoom.png"));

                Press(Key.OemMinus);
                Check("ctrl+- -> 125%", Near(_w._browser.GetZoom(tab), 1.25),
                    _w._browser.GetZoom(tab).ToString());
                Press(Key.D0);
                Check("ctrl+0 resets to 100% (not site default)", Near(_w._browser.GetZoom(tab), 1.0),
                    _w._browser.GetZoom(tab).ToString());
                Check("ctrl+0 forgets the site memory", Near(ZoomStore.Get(site), 1.0),
                    ZoomStore.Get(site).ToString());

                // 会话里也要能带回来（TabModel.Zoom 是持久化字段）
                ZoomStore.Put("example.com", 1.75);
                Check("per-site entry independent of current tab", Near(ZoomStore.Get("example.com"), 1.75),
                    ZoomStore.Get("example.com").ToString());

                // ---------- 5. 内部页路由 ----------
                Check("route nano://downloads",
                    InternalPages.Normalize("nano://downloads") == InternalPages.Downloads,
                    InternalPages.Normalize("nano://downloads"));
                Check("downloads title", InternalPages.TitleOf("nano://downloads") == "下载",
                    InternalPages.TitleOf("nano://downloads"));
                Check("downloads page is pure WPF",
                    InternalPages.Create("nano://downloads", _w._settings, (u, n) => { }, m => { })
                        is DownloadsPage, "type=DownloadsPage");
                Check("unknown nano:// still falls back to start",
                    InternalPages.Normalize("nano://whatever") == InternalPages.Start,
                    InternalPages.Normalize("nano://whatever"));

                // ---------- 6. 真实下载 ----------
                Check("download list starts empty", DownloadManager.All().Count == 0,
                    "count=" + DownloadManager.All().Count);
                var port = StartServer();
                var downloadUrl = "http://127.0.0.1:" + port + "/nanoedge-probe.txt";
                Line("[download] " + downloadUrl + " payload=" + _payload.Length + " bytes");
                _w.NewTab(downloadUrl);
                var downloadTab = _w._active;

                var sawProgress = false;
                Action onChanged = () =>
                {
                    if (DownloadManager.All().Any(i => i.State == DownloadStates.InProgress))
                        sawProgress = true;
                };
                DownloadManager.Changed += onChanged;

                var done = await WaitForAsync(
                    () => DownloadManager.All().Any(i => i.State == DownloadStates.Completed), 20000);
                DownloadManager.Changed -= onChanged;
                Check("download went through InProgress", sawProgress, "sawProgress=" + sawProgress);
                Check("WebView2 DownloadStarting recorded a completed download", done,
                    "items=" + DownloadManager.All().Count + " states=" +
                    string.Join(",", DownloadManager.All().Select(i => i.State)));

                var item = DownloadManager.All().FirstOrDefault(i => i.State == DownloadStates.Completed);
                if (item != null)
                {
                    Check("file saved to DownloadsFolder", File.Exists(item.Path), item.Path);
                    Check("saved bytes match payload",
                        File.Exists(item.Path) && new FileInfo(item.Path).Length == _payload.Length,
                        "onDisk=" + (File.Exists(item.Path) ? new FileInfo(item.Path).Length : -1));
                    Check("filename from Content-Disposition", item.FileName == "nanoedge-probe.txt",
                        item.FileName);
                    Check("percent is 100 for a completed download", item.Percent == 100,
                        item.Percent.ToString());
                }
                Check("no active downloads left", DownloadManager.ActiveCount == 0,
                    "active=" + DownloadManager.ActiveCount);
                Check("download exemption cleared on the tab", downloadTab.ActiveDownloads == 0,
                    "activeDownloads=" + downloadTab.ActiveDownloads);
                Check("downloads.json persisted", File.Exists(DownloadManager.FilePath),
                    "bytes=" + (File.Exists(DownloadManager.FilePath)
                        ? new FileInfo(DownloadManager.FilePath).Length : 0));
                DownloadManager.Reload();
                Check("download list survives reload", DownloadManager.All().Count == 1,
                    "count=" + DownloadManager.All().Count);

                // ---------- 7. 慢速下载 + 取消 ----------
                var slowUrl = "http://127.0.0.1:" + port + "/slow.bin";
                Line("[download] " + slowUrl + " (slow, to exercise cancel)");
                _w.NewTab(slowUrl);
                var started = await WaitForAsync(
                    () => DownloadManager.All().Any(i => i.FileName == "slow.bin" &&
                                                        i.State == DownloadStates.InProgress), 12000);
                Check("slow download appears as InProgress", started,
                    "states=" + string.Join(",", DownloadManager.All().Select(i => i.State)));
                var slow = DownloadManager.All()
                    .FirstOrDefault(i => i.FileName == "slow.bin");
                if (slow != null)
                {
                    Check("in-progress percent is below 100", slow.Percent < 100,
                        slow.Percent + "% received=" + slow.ReceivedBytes + "/" + slow.TotalBytes);
                    Check("cancel is accepted on a live download", DownloadManager.Cancel(slow.Id),
                        "id=" + slow.Id);
                    var settled = await WaitForAsync(
                        () => DownloadManager.All().Any(i => i.Id == slow.Id &&
                                                            i.State != DownloadStates.InProgress), 10000);
                    Check("cancelled download leaves InProgress", settled,
                        "state=" + DownloadManager.All().First(i => i.Id == slow.Id).State);
                    Check("cancelled download is marked Canceled",
                        DownloadManager.All().First(i => i.Id == slow.Id).State == DownloadStates.Canceled,
                        "state=" + DownloadManager.All().First(i => i.Id == slow.Id).State);
                }
                Check("no active downloads after cancel", DownloadManager.ActiveCount == 0,
                    "active=" + DownloadManager.ActiveCount);

                // ---------- 8. Ctrl+J ----------
                Press(Key.J);
                Check("ctrl+J opens nano://downloads", _w._active.InternalPage == "downloads",
                    "tab=" + _w._active.InternalPage + " url=" + _w._active.Url);
                Check("downloads page has no webview", !_w._browser.HasLiveView(_w._active),
                    "live=" + _w._browser.HasLiveView(_w._active));
                var tabsBefore = _w._tabs.Count;
                Press(Key.J);
                Check("ctrl+J reuses the existing downloads tab", _w._tabs.Count == tabsBefore,
                    "tabs=" + _w._tabs.Count);

                _w.UpdateLayout();
                _w.CaptureScreenshot(Path.Combine(_root, "out", "m3c-downloads-live.png"));
                RenderPage(new DownloadsPage(m => { }),
                    Path.Combine(_root, "out", "m3c-downloads.png"), 980, 520);
                Check("downloads page rendered", File.Exists(Path.Combine(_root, "out", "m3c-downloads.png")),
                    "out/m3c-downloads.png");

                Theme.Apply("light");
                RenderPage(new DownloadsPage(m => { }),
                    Path.Combine(_root, "out", "m3c-downloads-light.png"), 980, 520);
                Theme.Apply("dark");
                Check("light theme downloads page rendered",
                    File.Exists(Path.Combine(_root, "out", "m3c-downloads-light.png")),
                    "out/m3c-downloads-light.png");

                // ---------- 9. 列表动作 ----------
                Check("cancel on a finished download is a no-op",
                    item != null && !DownloadManager.Cancel(item.Id), "returns false");
                DownloadManager.ClearEnded();
                Check("clear ended empties the list", DownloadManager.All().Count == 0,
                    "count=" + DownloadManager.All().Count);

                Line("");
                Line("zoom.json=" + ZoomStore.FilePath + " downloads.json=" + DownloadManager.FilePath);
                Line("download folder=" + AppPaths.DownloadsFolder);
            }

            private void Press(Key key)
            {
                _w.HandleShortcut(key, Ctrl, Key.None);
            }

            private int StartServer()
            {
                _server = new TcpListener(IPAddress.Loopback, 0);
                _server.Start();
                var port = ((IPEndPoint)_server.LocalEndpoint).Port;
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
                return port;
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

                        // 只给两个附件路径回内容；favicon 之类的探测一律 404，避免多出下载项。
                        var slow = path.IndexOf("/slow.bin", StringComparison.OrdinalIgnoreCase) >= 0;
                        var probe = path.IndexOf("/nanoedge-probe.txt", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!slow && !probe)
                        {
                            var notFound = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            stream.Write(notFound, 0, notFound.Length);
                            return;
                        }

                        var name = slow ? "slow.bin" : "nanoedge-probe.txt";
                        var length = slow ? 8L * 1024 * 1024 : _payload.Length;
                        var header = "HTTP/1.1 200 OK\r\n" +
                                     "Content-Type: application/octet-stream\r\n" +
                                     "Content-Disposition: attachment; filename=\"" + name + "\"\r\n" +
                                     "Content-Length: " + length + "\r\n" +
                                     "Connection: close\r\n\r\n";
                        var head = Encoding.ASCII.GetBytes(header);
                        stream.Write(head, 0, head.Length);
                        if (slow)
                        {
                            // 只发一小段就挂住，让下载停在 InProgress，供取消测试使用。
                            stream.Write(_payload, 0, 65536);
                            stream.Flush();
                            System.Threading.Thread.Sleep(6000);
                        }
                        else
                        {
                            stream.Write(_payload, 0, _payload.Length);
                            stream.Flush();
                        }
                    }
                    catch (Exception ex) { Line("[download] server: " + ex.Message); }
                }
            }

            private void StopServer()
            {
                _serverRunning = false;
                try { if (_server != null) _server.Stop(); } catch { }
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

            private static bool Near(double a, double b) { return Math.Abs(a - b) < 0.0001; }

            private static void Render(FrameworkElement element, string path)
            {
                var width = Math.Max(120, element.Width > 0 ? element.Width : element.ActualWidth);
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
                Diagnostics.Log("[m3ctest] " + text);
            }

            private void WriteReport()
            {
                try
                {
                    var path = Path.Combine(_root ?? Path.GetTempPath(), "out", "m3c-report.txt");
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
