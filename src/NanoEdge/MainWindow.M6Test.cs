using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using Microsoft.Win32;

namespace NanoEdge
{
    public sealed partial class MainWindow
    {
        // --m6test：发布收尾的验收（图标 / 默认浏览器 / 外部唤起 / 崩溃恢复）。
        // 外部唤起是真的再起一个 NanoEdge.exe；崩溃恢复是真的 kill 掉 WebView2 进程。
        private sealed class M6SelfTest
        {
            private readonly MainWindow _w;
            private readonly StringBuilder _report = new StringBuilder();
            private readonly object _reportLock = new object();
            private readonly List<string> _failures = new List<string>();
            private int _passes;
            private string _root;
            private string _temp;

            private TcpListener _server;
            private volatile bool _running;
            private int _port;

            private bool _hadProgId;
            private bool _hadAppKey;
            private bool _hadRegisteredValue;

            public M6SelfTest(MainWindow window) { _w = window; }

            public async Task RunAsync()
            {
                _root = FindRoot();
                Line("M6 release self-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                try
                {
                    Setup();
                    await RunCoreAsync();
                }
                catch (Exception ex) { Fail("unhandled", ex.ToString()); }
                finally
                {
                    StopServer();
                    RestoreRegistry();
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
                _temp = Path.Combine(Path.GetTempPath(), "nanoedge-m6");
                if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
                Directory.CreateDirectory(_temp);

                _hadProgId = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + DefaultBrowser.ProgId) != null;
                _hadAppKey = Registry.CurrentUser.OpenSubKey(DefaultBrowser.AppKey) != null;
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications"))
                    _hadRegisteredValue = key != null && key.GetValue("NanoEdge") != null;

                StartServer();
            }

            private async Task RunCoreAsync()
            {
                // ---------- 1. 图标 ----------
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                Line("[icon] exe=" + exe + " bytes=" + new FileInfo(exe).Length);
                try
                {
                    using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe))
                    {
                        Check("exe has an embedded application icon", icon != null && icon.Width >= 16,
                            icon == null ? "null" : icon.Width + "x" + icon.Height);
                        if (icon != null)
                        {
                            var path = Path.Combine(_root, "out", "m6-exe-icon.png");
                            using (var bitmap = icon.ToBitmap())
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(path));
                                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                            }
                            Check("exe icon extracted to png", File.Exists(path), "out/m6-exe-icon.png");
                        }
                    }
                }
                catch (Exception ex) { Fail("exe icon", ex.Message); }

                using (var stream = Assembly.GetExecutingAssembly()
                           .GetManifestResourceStream("NanoEdge.nanoedge.ico"))
                {
                    var length = stream == null ? 0 : stream.Length;
                    Check("window icon resource is embedded", length > 1000, "bytes=" + length);
                }

                // ---------- 2. 默认浏览器注册 ----------
                try
                {
                    DefaultBrowser.Register();
                    Check("default browser registered", DefaultBrowser.IsRegistered(), "IsRegistered");
                    Check("URL association http", ReadValue(@"Software\Classes\NanoEdgeHTML\shell\open\command") != null,
                        "command exists");
                    Check("http maps to NanoEdgeHTML",
                        Convert.ToString(ReadValue(DefaultBrowser.AppKey + @"\Capabilities\URLAssociations", "http")) ==
                        DefaultBrowser.ProgId,
                        Convert.ToString(ReadValue(DefaultBrowser.AppKey + @"\Capabilities\URLAssociations", "http")));
                    Check("https maps to NanoEdgeHTML",
                        Convert.ToString(ReadValue(DefaultBrowser.AppKey + @"\Capabilities\URLAssociations", "https")) ==
                        DefaultBrowser.ProgId,
                        Convert.ToString(ReadValue(DefaultBrowser.AppKey + @"\Capabilities\URLAssociations", "https")));
                    Check("registered in RegisteredApplications",
                        Convert.ToString(ReadValue(@"Software\RegisteredApplications", "NanoEdge")) ==
                        DefaultBrowser.AppKey + @"\Capabilities",
                        Convert.ToString(ReadValue(@"Software\RegisteredApplications", "NanoEdge")));
                    var command = Convert.ToString(ReadValue(@"Software\Classes\NanoEdgeHTML\shell\open\command"));
                    Check("open command quotes the exe and passes %1",
                        command != null && command.Contains("\"%1\"") && command.Contains(exe),
                        command);
                }
                catch (Exception ex) { Fail("default browser", ex.Message); }

                // ---------- 3. 外部唤起 ----------
                var ipcUrl = "http://127.0.0.1:" + _port + "/ipc-target";
                var exePath = Process.GetCurrentProcess().MainModule.FileName;
                var tabsBefore = _w._tabs.Count;
                try
                {
                    var startInfo = new ProcessStartInfo(exePath, "--test-mutex " + ipcUrl)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    var second = Process.Start(startInfo);
                    var forwarded = await WaitForAsync(
                        () => _w._tabs.Any(t => string.Equals(t.Url, ipcUrl, StringComparison.OrdinalIgnoreCase)), 10000);
                    Check("second instance forwards its url to the running one", forwarded,
                        "tabs=" + _w._tabs.Count + " before=" + tabsBefore);
                    var exited = second != null && second.WaitForExit(5000);
                    Check("second instance exits without opening a window", exited,
                        second == null ? "no process" : "exitCode=" + (exited ? second.ExitCode.ToString() : "running"));
                    var ipcTab = _w._tabs.FirstOrDefault(t => t.Url == ipcUrl);
                    if (ipcTab != null)
                    {
                        var ready = await WaitReadyAsync(ipcTab, ipcUrl, 15000);
                        Check("forwarded url actually loads", ready, "ready=" + ready);
                    }

                    // SPEC 7.9：`--open-url=<url>` 形式也要能被第二实例转交。
                    var ipcUrl2 = "http://127.0.0.1:" + _port + "/ipc-prefixed";
                    var second2 = Process.Start(new ProcessStartInfo(exePath, "--test-mutex --open-url=" + ipcUrl2)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    var forwarded2 = await WaitForAsync(
                        () => _w._tabs.Any(t => t.Url == ipcUrl2), 10000);
                    Check("--open-url= form is forwarded too", forwarded2,
                        "tabs=" + _w._tabs.Count);
                    if (second2 != null) second2.WaitForExit(5000);
                }
                catch (Exception ex) { Fail("ipc", ex.Message); }

                // ---------- 4. 渲染进程崩溃恢复 ----------
                var crashTab = _w._active;
                var crashUrl = crashTab == null ? ipcUrl : crashTab.Url;
                var logPath = Path.Combine(AppPaths.Logs, "nanoedge.log");
                var logBefore = FileLengthSafe(logPath);
                try
                {
                    var env = await _w._browser.EnvironmentAsync();
                    var renderers = env.GetProcessInfos()
                        .Where(p => p.Kind == Microsoft.Web.WebView2.Core.CoreWebView2ProcessKind.Renderer)
                        .Select(p => p.ProcessId).Distinct().ToList();
                    Line("[crash] renderer pids=" + string.Join(",", renderers));
                    Check("renderer processes found", renderers.Count > 0, "count=" + renderers.Count);

                    foreach (var pid in renderers)
                    {
                        try { Process.GetProcessById(pid).Kill(); }
                        catch (Exception ex) { Line("[crash] kill " + pid + ": " + ex.Message); }
                    }

                    var sawFailure = await WaitForAsync(
                        () => ReadFrom(logPath, logBefore).Contains("process failed: RenderProcessExited"), 10000);
                    Check("ProcessFailed fired for the renderer crash", sawFailure,
                        "log has RenderProcessExited=" + sawFailure);

                    var sawReload = await WaitForAsync(
                        () => ReadFrom(logPath, logBefore).Contains("nav ok " + crashUrl), 15000);
                    Check("crashed tab reloaded automatically", sawReload, "nav ok after crash=" + sawReload);

                    var recovered = await WaitReadyAsync(crashTab, crashUrl, 15000);
                    Check("crashed tab is usable again", recovered, "ready=" + recovered);
                }
                catch (Exception ex) { Fail("renderer crash recovery", ex.Message); }

                // ---------- 5. 浏览器进程退出恢复 ----------
                var browserLogBefore = FileLengthSafe(logPath);
                try
                {
                    var env = await _w._browser.EnvironmentAsync();
                    var browserPid = env.GetProcessInfos()
                        .Where(p => p.Kind == Microsoft.Web.WebView2.Core.CoreWebView2ProcessKind.Browser)
                        .Select(p => p.ProcessId).FirstOrDefault();
                    Line("[crash] browser pid=" + browserPid);
                    Check("browser process found", browserPid > 0, "pid=" + browserPid);

                    try { Process.GetProcessById(browserPid).Kill(); }
                    catch (Exception ex) { Line("[crash] kill browser: " + ex.Message); }

                    var sawExit = await WaitForAsync(
                        () => ReadFrom(logPath, browserLogBefore).Contains("browser process crash: views reset"), 15000);
                    Check("BrowserProcessExited handled and views reset", sawExit,
                        "reset logged=" + sawExit);

                    var active = _w._active;
                    var back = active != null && await WaitReadyAsync(active, active.Url, 20000);
                    Check("active tab reloads after the browser process died", back,
                        active == null ? "no active tab" : "ready=" + back);
                    Check("recovered tab has a live webview",
                        active != null && _w._browser.HasLiveView(active),
                        "live=" + (active != null && _w._browser.HasLiveView(active)));
                }
                catch (Exception ex) { Fail("browser crash recovery", ex.Message); }

                // ---------- 6. 窗口截图 ----------
                _w.WindowState = WindowState.Normal;
                _w.Width = 1100;
                _w.Height = 700;
                await Task.Delay(400);
                _w.UpdateLayout();
                _w.CaptureScreenshot(Path.Combine(_root, "out", "m6-window.png"));
                Check("window screenshot written", File.Exists(Path.Combine(_root, "out", "m6-window.png")),
                    "out/m6-window.png");

                Line("");
                Line("exe bytes = " + new FileInfo(exe).Length);
            }

            // ---------- 注册表 ----------

            private static object ReadValue(string path, string name = null)
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(path))
                        return key == null ? null : key.GetValue(name);
                }
                catch { return null; }
            }

            // 只清理自测自己造出来的键；原来就有的（用户已经设过默认浏览器）不动。
            private void RestoreRegistry()
            {
                try
                {
                    if (!_hadProgId)
                        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + DefaultBrowser.ProgId, false);
                    if (!_hadAppKey)
                        Registry.CurrentUser.DeleteSubKeyTree(DefaultBrowser.AppKey, false);
                    if (!_hadRegisteredValue)
                        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true))
                            if (key != null) key.DeleteValue("NanoEdge", false);
                    Line("[registry] restored (progId created=" + !_hadProgId + ")");

                    var progIdExists = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + DefaultBrowser.ProgId) != null;
                    var appExists = Registry.CurrentUser.OpenSubKey(DefaultBrowser.AppKey) != null;
                    var clean = (_hadProgId || !progIdExists) && (_hadAppKey || !appExists);
                    if (clean) { _passes++; Line("[PASS] registry restored after the test  clean"); }
                    else { _failures.Add("registry not cleaned"); Line("[FAIL] registry restored after the test  leftovers"); }
                }
                catch (Exception ex) { Line("[registry] restore failed: " + ex.Message); }
            }

            // ---------- 页面 ----------

            private void StartServer()
            {
                _server = new TcpListener(IPAddress.Loopback, 0);
                _server.Start();
                _port = ((IPEndPoint)_server.LocalEndpoint).Port;
                _running = true;
                Task.Run(async () =>
                {
                    while (_running)
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
                _running = false;
                try { if (_server != null) _server.Stop(); } catch { }
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

                        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>m6 page</title></head>" +
                                   "<body style=\"margin:0;padding:18px;background:#111;color:#eee;font:14px sans-serif\">" +
                                   "<h1 style=\"font-size:18px\">M6 probe · " + path +
                                   "</h1><p id=\"m6-target\">crash recovery target</p></body></html>";
                        var bytes = Encoding.UTF8.GetBytes(html);
                        var header = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                                     "Content-Length: " + bytes.Length + "\r\nCache-Control: no-store\r\n" +
                                     "Connection: close\r\n\r\n";
                        var head = Encoding.ASCII.GetBytes(header);
                        stream.Write(head, 0, head.Length);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    }
                    catch (IOException) { }
                    catch (Exception ex) { Line("[server] " + ex.Message); }
                }
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
                    await Task.Delay(150);
                }
                return false;
            }

            private async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
            {
                var started = Environment.TickCount;
                while (Environment.TickCount - started < timeoutMs)
                {
                    if (condition()) return true;
                    await Task.Delay(150);
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
                Diagnostics.Log("[m6test] " + text);
            }

            private void WriteReport()
            {
                try
                {
                    var path = Path.Combine(_root ?? Path.GetTempPath(), "out", "m6-report.txt");
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
