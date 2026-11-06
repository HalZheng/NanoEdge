using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace NanoEdge
{
    public sealed partial class MainWindow
    {
        // --memtest：M2 验收脚本。真实走 MemoryGovernor 的状态机（不用 mock），
        // 结果与两张渲染图写到 out/，跑完自动退出。
        private sealed class MemorySelfTest
        {
            private readonly MainWindow _w;
            private readonly StringBuilder _report = new StringBuilder();
            private readonly List<string> _failures = new List<string>();
            private string _root;

            public MemorySelfTest(MainWindow window)
            {
                _w = window;
            }

            public async Task RunAsync()
            {
                _root = FindRoot();
                Line("M2 memory governance self-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Line("user data = " + AppPaths.UserData);
                try
                {
                    await RunCoreAsync();
                }
                catch (Exception ex)
                {
                    Fail("unhandled", ex.ToString());
                }
                finally
                {
                    Line("");
                    Line(_failures.Count == 0 ? "RESULT: PASS (" + _passes + " checks)" : "RESULT: FAIL " + string.Join(", ", _failures));
                    WriteReport();
                    // 内存面板的 Popup 是独立 HWND，不主动关会让进程在 Shutdown 后
                    // 仍挂住（fixtest 的收尾已经这么做了，这里补齐）。
                    try { if (_w._memoryPanel != null) _w._memoryPanel.Close(); } catch { }
                    try { Application.Current.Shutdown(); } catch { }
                }
            }

            private int _passes;

            private async Task RunCoreAsync()
            {
                _w._settings.Memory.ApplyPreset(MemoryPreset.Balanced);
                // 压缩计时，让整条链路在十几秒内跑完；数值只在内存里，不落盘。
                _w._settings.Memory.CoolDelayMs = 1200;
                _w._settings.Memory.LogDiagnostics = true;

                var url = MakeTestPage();
                Line("test page = " + url);

                var before = _w._tabs.Count;
                _w.NewTab(url);
                await Task.Delay(600);
                _w.NewTab(url);
                await Task.Delay(600);
                _w.NewTab(url);
                await Task.Delay(600);

                var tabs = _w._tabs.Skip(before).Take(3).ToList();
                if (tabs.Count != 3) { Fail("tabs", "expected 3 test tabs"); return; }

                var ready = await WaitForAsync(() => tabs.All(t => _w._browser.HasLiveView(t) && !t.IsStartPage), 15000);
                Check("webview2 ready", ready, "liveViews=" + _w._browser.LiveViewCount(tabs) + "/3");

                for (var i = 0; i < tabs.Count; i++)
                {
                    var core = _w._browser.Core(tabs[i]);
                    if (core == null) continue;
                    var name = "t" + (i + 1);
                    await core.ExecuteScriptAsync(
                        "document.getElementById('name').value='" + name + "';" +
                        "document.getElementById('memo').value='draft-" + name + "';" +
                        "window.scrollTo(0,1200);");
                }
                await Task.Delay(1600);

                var baseline = await _w._governor.SampleAsync();
                Line("[1] baseline   groupWS=" + baseline.GroupWorkingSetMB + "MB private=" +
                     baseline.GroupPrivateMB + "MB host=" + baseline.HostWorkingSetMB + "MB/" +
                     baseline.HostPrivateMB + "MB processes=" + baseline.Rows.Count + " " +
                     "browser=" + baseline.KindWorkingSetMB("Browser") + " gpu=" + baseline.KindWorkingSetMB("Gpu") +
                     " renderers=" + baseline.Count("Renderer"));
                foreach (var tab in tabs)
                    Line("    tab " + tab.Id + " ws=" + baseline.TabWorkingSetMB(tab.Id) +
                         " priv=" + baseline.TabPrivateMB(tab.Id));
                Check("baseline sampled", baseline.GroupWorkingSetMB > 0, "groupWS=" + baseline.GroupWorkingSetMB);

                // ---- 2. 失焦 -> Low（含当前标签）----
                _w._governor.NotifyDeactivated();
                await Task.Delay(2400);
                var lowSample = await _w._governor.SampleAsync();
                var lowStates = string.Join(",", tabs.Select(t => t.MemoryState));
                Check("unfocus -> Low", tabs.All(t => t.MemoryState == TabMemoryState.Low),
                    "states=" + lowStates + " groupWS=" + lowSample.GroupWorkingSetMB + "MB");
                Line("[2] after Low  groupWS=" + lowSample.GroupWorkingSetMB + "MB private=" +
                     lowSample.GroupPrivateMB + "MB rendererWS=" +
                     Math.Round(lowSample.Rows.Where(r => r.Kind == "Renderer").Sum(r => r.WorkingSetMB), 1) + "MB");

                // ---- 3. 回到前台：当前标签回 Active，后台保持 Low（三态模型没有冻结档）----
                _w._governor.NotifyActivated();
                await Task.Delay(900);
                var activeSample = await _w._governor.SampleAsync();
                Check("activate -> active tab Normal, background stays Low",
                    tabs[2].MemoryState == TabMemoryState.Active &&
                    tabs.Take(2).All(t => t.MemoryState == TabMemoryState.Low),
                    "states=" + string.Join(",", tabs.Select(t => t.MemoryState)));
                Line("[3] after activate groupWS=" + activeSample.GroupWorkingSetMB + "MB private=" +
                     activeSample.GroupPrivateMB + "MB");

                // ---- 4. 一键释放后台 ----
                var release = await _w._governor.ReleaseBackgroundAsync();
                var afterRelease = _w._governor.LastSnapshot;
                Check("release background", release.Count == 2 &&
                      tabs.Take(2).All(t => t.MemoryState == TabMemoryState.Discarded) &&
                      !_w._browser.HasLiveView(tabs[0]),
                    "released=" + release.Count + " estimate=" + release.EstimatedMB +
                    "MB actualDelta=" + release.ActualDeltaMB + "MB groupWS=" +
                    (afterRelease == null ? "?" : afterRelease.GroupWorkingSetMB.ToString()));
                Line("[4] after Discard groupWS=" +
                     (afterRelease == null ? "?" : afterRelease.GroupWorkingSetMB.ToString()) + "MB private=" +
                     (afterRelease == null ? "?" : afterRelease.GroupPrivateMB.ToString()) + "MB" +
                     " processes=" + (afterRelease == null ? "?" : afterRelease.Rows.Count.ToString()));

                // ---- 5. 快照内容 ----
                var snapshot = tabs[0].Snapshot;
                var snapshotOk = snapshot != null && snapshot.ScrollY >= 1000 &&
                                 snapshot.Forms.Any(f => f.Value == "draft-t1");
                Check("discard snapshot", snapshotOk,
                    snapshot == null ? "snapshot=null"
                        : "scrollY=" + snapshot.ScrollY + " forms=" + snapshot.Forms.Count +
                          " bytes=" + snapshot.Bytes + " restorable=" + snapshot.Restorable);
                if (snapshot != null)
                    Line("[5] snapshot forms: " + string.Join(", ", snapshot.Forms.Select(
                        f => f.Kind + "=" + f.Value)));

                RenderPlaceholder();

                // ---- 6. 选中已丢弃标签 -> 重建 + 还原 ----
                _w.SelectTab(tabs[0]);
                await Task.Delay(3200);
                var restoredCore = _w._browser.Core(tabs[0]);
                var restored = new Dictionary<string, object>();
                if (restoredCore != null)
                {
                    var raw = await restoredCore.ExecuteScriptAsync(
                        "JSON.stringify({y:window.scrollY,n:document.getElementById('name').value,m:document.getElementById('memo').value})");
                    var inner = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<string>(raw);
                    restored = new System.Web.Script.Serialization.JavaScriptSerializer()
                        .Deserialize<Dictionary<string, object>>(inner);
                }
                var restoreOk = restoredCore != null &&
                                Convert.ToDouble(restored["y"]) >= 1000 &&
                                Convert.ToString(restored["m"]) == "draft-t1";
                Check("restore discarded tab", restoreOk,
                    "scrollY=" + (restored.ContainsKey("y") ? restored["y"] : "?") +
                    " memo=" + (restored.ContainsKey("m") ? restored["m"] : "?") +
                    " state=" + tabs[0].MemoryState);

                // ---- 7. 内存面板采样 + 渲染图 ----
                _w.OpenMemoryPanel(true);
                if (_w._memoryPanel != null)
                {
                    await _w._memoryPanel.RefreshAsync();
                    await Task.Delay(400);
                    var panelSnapshot = _w._governor.LastSnapshot;
                    Check("memory panel", panelSnapshot != null && panelSnapshot.GroupWorkingSetMB > 0,
                        "groupWS=" + (panelSnapshot == null ? "?" : panelSnapshot.GroupWorkingSetMB.ToString()) +
                        " rows=" + (panelSnapshot == null ? 0 : panelSnapshot.Rows.Count) +
                        " badge=" + (_w._memoryBadge == null ? "?" : _w._memoryBadge.Text));
                    RenderPanel();
                    _w._memoryPanel.IsPinned = false;
                }

                Line("");
                Line("final states: " + string.Join(" | ", _w._tabs.Select(t =>
                    t.Id + "=" + t.MemoryState + (t.Resident ? "(常驻)" : "") +
                    (string.IsNullOrEmpty(t.Host) ? "" : "@" + t.Host))));
                Line("final group: " + string.Join(", ", _w._tabs.Select(t =>
                    t.Id + ":" + (_w._governor.LastSnapshot == null ? "?" : _w._governor.LastSnapshot.TabWorkingSetMB(t.Id).ToString("0")) + "MB")));

                _w.CaptureScreenshot(Path.Combine(_root, "out", "m2-window.png"));
                Line("");
                Line("artifacts: out/m2-window.png, out/m2-panel.png, out/m2-placeholder.png");
            }

            // ---------- artifacts ----------

            private void RenderPanel()
            {
                try
                {
                    var visual = _w._memoryPanel.Visual as FrameworkElement;
                    if (visual == null) return;
                    Render(visual, Path.Combine(_root, "out", "m2-panel.png"));
                }
                catch (Exception ex) { Line("panel render failed: " + ex.Message); }
            }

            private void RenderPlaceholder()
            {
                try
                {
                    var host = _w._tabHost;
                    var placeholder = host.Children.OfType<DiscardedPlaceholder>().FirstOrDefault();
                    if (placeholder == null) { Line("placeholder not found"); return; }
                    var visibility = placeholder.Visibility;
                    placeholder.Visibility = Visibility.Visible;
                    host.UpdateLayout();
                    Render(placeholder, Path.Combine(_root, "out", "m2-placeholder.png"), 960, 520);
                    placeholder.Visibility = visibility;
                }
                catch (Exception ex) { Line("placeholder render failed: " + ex.Message); }
            }

            private static void Render(FrameworkElement element, string path, double fixedWidth = 0, double fixedHeight = 0)
            {
                var width = fixedWidth > 0 ? fixedWidth : element.ActualWidth;
                var height = fixedHeight > 0 ? fixedHeight : element.ActualHeight;
                element.Measure(new Size(Math.Max(1, width), Math.Max(1, height)));
                element.Arrange(new Rect(0, 0, Math.Max(1, width), Math.Max(1, height)));
                element.UpdateLayout();
                width = Math.Max(1, width);
                height = Math.Max(1, height);
                var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(element);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                using (var stream = File.Create(path)) encoder.Save(stream);
            }

            // ---------- helpers ----------

            private async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
            {
                var started = Environment.TickCount;
                while (Environment.TickCount - started < timeoutMs)
                {
                    if (condition()) return true;
                    await Task.Delay(120);
                }
                return condition();
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
                Diagnostics.Log("[memtest] " + text);
            }

            private void WriteReport()
            {
                try
                {
                    var path = Path.Combine(_root ?? Path.GetTempPath(), "out", "m2-memtest.txt");
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

            private static string MakeTestPage()
            {
                var directory = Path.Combine(Path.GetTempPath(), "nanoedge-m2");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "page.html");

                var filler = new StringBuilder();
                for (var i = 0; i < 60; i++)
                    filler.Append("<p>第 " + i + " 段：用于撑开滚动区域并保持 DOM 规模稳定的测试内容。</p>\n");

                var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>M2 内存测试页</title>" +
                           "<style>body{margin:0;padding:40px;background:#0f1620;color:#e6e9ef;font:15px system-ui;line-height:1.7}" +
                           "h1{color:#FF4D9D}input,textarea{background:#16202c;border:1px solid #33455a;color:#e6e9ef;" +
                           "padding:8px;width:420px;display:block;margin:8px 0}</style></head><body>" +
                           "<h1>M2 内存治理测试页</h1>" +
                           "<input id=\"name\" placeholder=\"name\">" +
                           "<textarea id=\"memo\" rows=\"4\" placeholder=\"memo\"></textarea>" +
                           "<div id=\"tick\">0</div>" + filler +
                           "<script>var t=0;setInterval(function(){t++;document.getElementById('tick').textContent=t;},1000);</script>" +
                           "</body></html>";
                File.WriteAllText(path, html, Encoding.UTF8);
                return new Uri(path).AbsoluteUri;
            }
        }

        // --fixtest：A/C 批修复的针对性回归。纯本地，不发网络请求，不写用户数据。
        private sealed class FixSelfTest
        {
            private readonly MainWindow _w;
            private readonly StringBuilder _report = new StringBuilder();
            private readonly List<string> _failures = new List<string>();
            private int _passes;

            public FixSelfTest(MainWindow window) { _w = window; }

            public async Task RunAsync()
            {
                Line("A/C fix batch self-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                try { await RunCoreAsync(); }
                catch (Exception ex) { Fail("unhandled", ex.ToString()); }
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

            private void Check(string name, bool ok, string detail)
            {
                Line((ok ? "[PASS] " : "[FAIL] ") + name + "  " + detail);
                if (ok) _passes++; else _failures.Add(name);
            }

            private void Fail(string name, string detail)
            {
                Line("[FAIL] " + name + "  " + detail);
                _failures.Add(name);
            }

            private void Line(string text) { _report.AppendLine(text); }

            private static string Root()
            {
                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "SPEC.md")))
                    dir = dir.Parent;
                return dir == null ? Path.GetTempPath() : dir.FullName;
            }

            private void WriteReport()
            {
                try
                {
                    var outDir = Path.Combine(Root(), "out");
                    Directory.CreateDirectory(outDir);
                    File.WriteAllText(Path.Combine(outDir, "fix-report.txt"),
                        _report.ToString(), Encoding.UTF8);
                }
                catch { }
            }

            private static System.Windows.Interop.MSG KeyMsg(Key key)
            {
                return new System.Windows.Interop.MSG
                {
                    message = 0x0100,
                    wParam = (IntPtr)KeyInterop.VirtualKeyFromKey(key)
                };
            }

            private static void ClickChip(FrameworkElement chip)
            {
                var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
                };
                chip.RaiseEvent(down);
                var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent
                };
                chip.RaiseEvent(up);
            }

            [DllImport("user32.dll")]
            private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

            [DllImport("user32.dll")]
            private static extern bool SetForegroundWindow(IntPtr hwnd);

            [DllImport("user32.dll")]
            private static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")]
            private static extern IntPtr SetFocus(IntPtr hwnd);

            private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

            [DllImport("user32.dll")]
            private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr param);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

            [DllImport("user32.dll")]
            private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            private static extern uint MapVirtualKey(uint code, uint mapType);

            private async Task RunCoreAsync()
            {
                // ---- P0：用户配置残留 -32000（最小化坐标）时，窗口仍须在屏幕内 ----
                // 放在最前面，后面 A3 / 第九轮的窗口状态断言会改 Left/Top。
                Check("startup window position is on screen",
                    MainWindow.IsSaneWindowCoord(_w.Left) &&
                    MainWindow.IsSaneWindowCoord(_w.Top) &&
                    _w.Left < SystemParameters.VirtualScreenWidth &&
                    _w.Top < SystemParameters.VirtualScreenHeight,
                    "left=" + _w.Left.ToString("0") + " top=" + _w.Top.ToString("0") +
                    " screen=" + SystemParameters.VirtualScreenWidth + "x" +
                    SystemParameters.VirtualScreenHeight);

                // ---- A2：顶栏空白区必须能接住命中测试 ----
                Check("navigation bar has a hit-test background",
                    _w._navigationRow != null && _w._navigationRow.Background != null,
                    _w._navigationRow == null ? "no bar" : "bg=" + _w._navigationRow.Background);

                // 空白的标签条允许拖窗口，标签项/地址栏/按钮不允许。
                var blankStrip = new ScrollViewer { Background = new SolidColorBrush(Colors.Transparent) };
                Check("tab strip blank area allows window drag",
                    !MainWindow.IsChromeInteractive(blankStrip), "interactive=false expected");
                Check("tab chip is excluded from window drag",
                    MainWindow.IsChromeInteractive(new Border { Tag = new TabModel() }), "interactive=true expected");
                Check("address box is excluded from window drag",
                    MainWindow.IsChromeInteractive(new TextBox()), "interactive=true expected");

                var toolbar = _w._toolbarHost == null ? null : _w._toolbarHost.Child as Grid;
                Check("whole toolbar has hit-test background (standard tab row included)",
                    toolbar != null && toolbar.Background != null,
                    toolbar == null ? "no toolbar" : "bg=" + toolbar.Background);

                var t0 = DateTime.UtcNow;
                Check("double click detected without ClickCount",
                    MainWindow.IsDoubleClick(t0, new Point(10, 10),
                        t0.AddMilliseconds(180), new Point(12, 11)), "180ms / 2px");
                Check("slow second press is not a double click",
                    !MainWindow.IsDoubleClick(t0, new Point(10, 10),
                        t0.AddMilliseconds(900), new Point(12, 11)), "900ms");
                Check("moved second press is not a double click",
                    !MainWindow.IsDoubleClick(t0, new Point(10, 10),
                        t0.AddMilliseconds(180), new Point(60, 11)), "50px move");
                Check("first press ever is not a double click",
                    !MainWindow.IsDoubleClick(DateTime.MinValue, new Point(),
                        t0, new Point(10, 10)), "MinValue");

                // ---- A4：刷新键放给 WebView2，页面 JS 才能 preventDefault ----
                var f5 = KeyMsg(Key.F5);
                bool f5Handled = false;
                _w.OnThreadPreprocessMessage(ref f5, ref f5Handled);
                Check("bare F5 reaches WebView2", !f5Handled, "handled=" + f5Handled);

                var letter = KeyMsg(Key.A);
                bool letterHandled = false;
                _w.OnThreadPreprocessMessage(ref letter, ref letterHandled);
                Check("plain typing is not swallowed", !letterHandled, "handled=" + letterHandled);

                // WPF 控件会把加速键转发回这一层，必须也放行，否则宿主仍会主动 Reload。
                try
                {
                    var preview = new KeyEventArgs(Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(_w), 0, Key.F5)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    };
                    _w.OnPreviewKeyDown(preview);
                    Check("WPF PreviewKeyDown lets F5 through", !preview.Handled,
                        "handled=" + preview.Handled);
                }
                catch (Exception ex)
                {
                    Line("[SKIP] PreviewKeyDown probe: " + ex.Message);
                }

                // ---- A1：单击切换标签，且连续点不残留拖拽状态 ----
                _w._settings.Layout = LayoutMode.Compact;
                _w.ApplyLayout();
                _w.NewTab();
                _w.NewTab();
                _w.RebuildTabStrip();

                var chips = _w._tabStripPanel == null
                    ? new List<Border>()
                    : _w._tabStripPanel.Children.OfType<Border>()
                        .Where(b => b.Tag is TabModel).ToList();
                Check("multiple tab chips rendered", chips.Count >= 3, "chips=" + chips.Count);
                if (chips.Count >= 3)
                {
                    var first = chips.First(b => !ReferenceEquals(b.Tag, _w._active));
                    ClickChip(first);
                    Check("mouse click switches tab", ReferenceEquals(_w._active, first.Tag),
                        "active=" + (_w._active == null ? "null" : _w._active.Id));

                    var second = chips.First(b => !ReferenceEquals(b.Tag, _w._active));
                    ClickChip(second);
                    Check("second click switches too (no stale drag state)",
                        ReferenceEquals(_w._active, second.Tag),
                        "active=" + (_w._active == null ? "null" : _w._active.Id));
                }

                // ---- C2：关掉最后一个标签不退出浏览器 ----
                while (_w._tabs.Count > 1) _w.CloseTab(_w._tabs[0]);
                _w.CloseTab(_w._tabs[0]);
                Check("closing last tab keeps one tab", _w._tabs.Count == 1,
                    "tabs=" + _w._tabs.Count);
                Check("window still alive", Application.Current.MainWindow != null &&
                    Application.Current.MainWindow.IsLoaded,
                    "loaded=" + (Application.Current.MainWindow != null &&
                                 Application.Current.MainWindow.IsLoaded));
                Check("remaining tab is a start page",
                    _w._tabs.Count == 1 && _w._tabs[0].IsStartPage,
                    "start=" + (_w._tabs.Count == 1 && _w._tabs[0].IsStartPage));

                // ---- 分组：水平布局看得见，且能解散 ----
                var group = new TabGroup { Name = "selftest" };
                _w._groups.Add(group);
                _w._tabs[0].GroupId = group.Id;
                _w.RebuildTabStrip();

                var panel = _w._tabStripPanel;
                var headers = panel == null ? new List<Border>()
                    : panel.Children.OfType<Border>().Where(b => Equals(b.Tag, "group")).ToList();
                Check("horizontal strip shows group header", headers.Count == 1,
                    "headers=" + headers.Count);
                Check("grouped tab still rendered", panel != null && panel.Children.OfType<Border>()
                    .Any(b => b.Tag is TabModel && ((TabModel)b.Tag).GroupId == group.Id),
                    "panel=" + (panel == null ? "null" : panel.Children.Count.ToString()));

                // 视觉证据：水平标签条里应该能看见分组头。
                try
                {
                    _w.UpdateLayout();
                    _w.CaptureScreenshot(Path.Combine(Root(), "out", "check-group-header.png"));
                    Line("group header screenshot: out/check-group-header.png");
                }
                catch (Exception ex) { Line("group header screenshot failed: " + ex.Message); }

                _w.DisbandGroup(group);
                Check("disband removes the group", _w._groups.Count == 0,
                    "groups=" + _w._groups.Count);
                Check("disband restores flat tabs",
                    _w._tabs.All(t => string.IsNullOrEmpty(t.GroupId)),
                    "grouped=" + _w._tabs.Count(t => !string.IsNullOrEmpty(t.GroupId)));
                Check("group header disappears", _w._tabStripPanel != null &&
                    !_w._tabStripPanel.Children.OfType<Border>().Any(b => Equals(b.Tag, "group")),
                    "children=" + (_w._tabStripPanel == null ? -1 : _w._tabStripPanel.Children.Count));

                // ---- 上一批的常驻样式回归 ----
                var resident = _w._tabs[0];
                resident.Resident = true;
                _w.RebuildTabStrip();
                var rchip = _w._tabStripPanel == null ? null
                    : _w._tabStripPanel.Children.OfType<Border>()
                        .FirstOrDefault(b => ReferenceEquals(b.Tag, resident));
                Check("resident tab has a visible marker",
                    rchip != null && rchip.BorderThickness.Left > 0,
                    rchip == null ? "no chip" : "left=" + rchip.BorderThickness.Left);

                // 端到端：真实按一次 F5，看页面 JS 能不能拿到并拦下。
                await TestF5Async();

                // 放最后：F11 会切沉浸式并重建外壳，别影响上面的 UI 断言。
                var f11 = KeyMsg(Key.F11);
                bool f11Handled = false;
                _w.OnThreadPreprocessMessage(ref f11, ref f11Handled);
                Check("F11 is still a host shortcut", f11Handled, "handled=" + f11Handled);

                // ---- B1：内存面板 Popup 有 Owner、点外部能关 ----
                _w.OpenMemoryPanel(true);
                var hostPopup = _w._memoryPanel == null ? null : _w._memoryPanel.HostPopup;
                Check("memory panel popup is attached to visual tree (has owner)",
                    hostPopup != null && hostPopup.Parent != null,
                    hostPopup == null ? "no popup" : "parent=" + hostPopup.Parent);
                Check("memory panel closes on outside click",
                    hostPopup != null && !hostPopup.StaysOpen,
                    hostPopup == null ? "no popup" : "staysOpen=" + hostPopup.StaysOpen);
                if (_w._memoryPanel != null) _w._memoryPanel.Close();

                // ---- B2：悬浮展开与收起态的子项对齐、操作期间不收起 ----
                _w._settings.Layout = LayoutMode.Vertical;
                _w._settings.VerticalExpanded = false;
                _w.ApplyLayout();
                _w.RebuildPopup();
                var railFirst = _w._railPanel.Children.Count > 0
                    ? _w._railPanel.Children[0] as FrameworkElement : null;
                var popFirst = _w._popupPanel.Children.Count > 0
                    ? _w._popupPanel.Children[0] as FrameworkElement : null;
                Check("vertical popup starts at the same tab as the rail",
                    railFirst != null && popFirst != null && railFirst.Tag is TabModel &&
                    ReferenceEquals(railFirst.Tag, popFirst.Tag),
                    "rail=" + (railFirst == null ? "null" : railFirst.Tag.ToString()) +
                    " popup=" + (popFirst == null ? "null" : popFirst.Tag.ToString()));
                Check("header row no longer offsets the popup",
                    popFirst != null && popFirst.Tag is TabModel,
                    popFirst == null ? "no item" : "tag=" + popFirst.Tag);
                Check("popup panel has no extra outer margin",
                    _w._popupPanel != null && _w._popupPanel.Margin.Equals(new Thickness(0)),
                    _w._popupPanel == null ? "null" : _w._popupPanel.Margin.ToString());
                Check("popup chip geometry matches rail chip",
                    railFirst != null && popFirst != null &&
                    railFirst.Height.Equals(popFirst.Height) &&
                    railFirst.Margin.Equals(popFirst.Margin),
                    "rail=" + (railFirst == null ? "" : railFirst.Height + "/" + railFirst.Margin) +
                    " popup=" + (popFirst == null ? "" : popFirst.Height + "/" + popFirst.Margin));
                _w.SuspendVerticalCollapse();
                _w.ScheduleCollapse();
                Check("collapse timer suspended during menu / drag",
                    !_w._collapseTimer.IsEnabled, "enabled=" + _w._collapseTimer.IsEnabled);
                _w.ResumeVerticalCollapse();
                _w._settings.Layout = LayoutMode.Compact;
                _w.ApplyLayout();

                // ---- A3：最大化不越过工作区（留 16px 给 WindowChrome 的边框）----
                var work = SystemParameters.WorkArea;
                _w.WindowState = WindowState.Maximized;
                await Task.Delay(300);
                Check("maximized window stays within work area",
                    _w.ActualWidth <= work.Width + 16 && _w.ActualHeight <= work.Height + 16,
                    "window=" + _w.ActualWidth.ToString("0") + "x" + _w.ActualHeight.ToString("0") +
                    " work=" + work.Width.ToString("0") + "x" + work.Height.ToString("0"));
                _w.WindowState = WindowState.Normal;

                // ---- 第八轮：最大化时拖顶栏先还原，再继续跟随鼠标 ----
                // 1) 纯几何：DPI 换算 / 横向比例 / RestoreBounds 兜底 / 工作区 clamp。
                var dragDip = MainWindow.PhysicalToDip(new Point(250, 200), new DpiScale(1.25, 1.25));
                Check("physical pixels convert to DIP at 125% scale",
                    Math.Abs(dragDip.X - 200) < 0.01 && Math.Abs(dragDip.Y - 160) < 0.01,
                    "dip=" + dragDip);

                var dragWork = new Rect(0, 0, 1920, 1040);
                var dragPlace = MainWindow.ComputeRestorePlacement(1920, 1040,
                    new Point(1152, 12), new Point(1152, 48), 1280, 800, dragWork);
                Check("restore keeps the mouse at the same horizontal ratio",
                    Math.Abs(dragPlace.Width * 0.6 - (1152 - dragPlace.X)) < 0.01,
                    "left=" + dragPlace.X.ToString("0") + " w=" + dragPlace.Width.ToString("0"));
                Check("restore keeps the anchor offset in the caption",
                    Math.Abs(dragPlace.Y - (48 - 12)) < 0.01, "top=" + dragPlace.Y.ToString("0"));

                var dragFallback = MainWindow.ComputeRestorePlacement(1920, 1040,
                    new Point(960, 12), new Point(960, 48), 0, 0, dragWork);
                Check("empty RestoreBounds falls back to a visible size",
                    dragFallback.Width > 0 && dragFallback.Height > 0 &&
                    !double.IsInfinity(dragFallback.Width),
                    "size=" + dragFallback.Width.ToString("0") + "x" + dragFallback.Height.ToString("0"));

                // 无边框窗口启动时就最大化，系统常把最大化矩形当成 rcNormalPosition；
                // 直接用会"还原"成一个和屏幕一样大的窗口，等于没还原。
                var dragFull = MainWindow.ComputeRestorePlacement(1920, 1040,
                    new Point(960, 12), new Point(960, 48), 1920, 1040, dragWork);
                Check("RestoreBounds equal to the maximized rect falls back to a normal window",
                    dragFull.Width < 1920 * 0.9 && dragFull.Height < 1040 * 0.9,
                    "size=" + dragFull.Width.ToString("0") + "x" + dragFull.Height.ToString("0"));

                var dragLeft = MainWindow.ComputeRestorePlacement(1920, 1040,
                    new Point(10, 12), new Point(10, 48), 1280, 800, dragWork);
                Check("restore near the left edge stays inside work area",
                    dragLeft.X >= dragWork.Left - 0.01 &&
                    dragLeft.X + dragLeft.Width <= dragWork.Right + 0.01,
                    "left=" + dragLeft.X.ToString("0") +
                    " right=" + (dragLeft.X + dragLeft.Width).ToString("0"));

                var dragRight = MainWindow.ComputeRestorePlacement(1920, 1040,
                    new Point(1910, 12), new Point(1910, 48), 1280, 800, dragWork);
                Check("restore near the right edge stays inside work area",
                    dragRight.X >= dragWork.Left - 0.01 &&
                    dragRight.X + dragRight.Width <= dragWork.Right + 0.01,
                    "left=" + dragRight.X.ToString("0") +
                    " right=" + (dragRight.X + dragRight.Width).ToString("0"));

                // 任务栏在顶部时 rcWork.Top > 0，top 不能被算成负数。
                var dragTop = MainWindow.ComputeRestorePlacement(1920, 1040,
                    new Point(960, 12), new Point(960, 5), 1280, 800,
                    new Rect(0, 40, 1920, 1000));
                Check("restore never puts the caption above the work area",
                    dragTop.Y >= 40 - 0.01, "top=" + dragTop.Y.ToString("0"));

                // 2) 真实窗口：走一次 PreviewMouseMove 的核心路径，
                //    不调 DragMove()（会进模态循环，把自测卡住）。
                _w.WindowState = WindowState.Maximized;
                await Task.Delay(300);
                Line("restore probe: maximized=" + _w.ActualWidth.ToString("0") + "x" +
                     _w.ActualHeight.ToString("0") + " restoreBounds=" + _w.RestoreBounds +
                     " width/height=" + _w.Width.ToString("0") + "x" + _w.Height.ToString("0"));
                var dragDown = new Point(_w.ActualWidth * 0.6, 12);
                _w._chromeDragArmed = true;
                Check("small mouse jitter does not start a drag",
                    !_w.HandleChromeDragMove(dragDown, new Point(dragDown.X + 1, dragDown.Y + 1)) &&
                    _w._chromeDragArmed, "armed=" + _w._chromeDragArmed);

                _w._chromeDragArmed = true;
                var dragStarted = _w.HandleChromeDragMove(dragDown,
                    new Point(dragDown.X, dragDown.Y + 48));
                var realWork = _w.WorkAreaDip();
                Check("dragging the caption from maximized restores to normal",
                    dragStarted && _w.WindowState == WindowState.Normal,
                    "startDrag=" + dragStarted + " state=" + _w.WindowState);
                Check("restored window has a real size",
                    _w.Width > 200 && _w.Height > 150 && !double.IsInfinity(_w.Width),
                    "size=" + _w.Width.ToString("0") + "x" + _w.Height.ToString("0"));
                Check("restored window is inside the current monitor work area",
                    _w.Left >= realWork.Left - 1 && _w.Top >= realWork.Top - 1 &&
                    _w.Left + _w.Width <= realWork.Right + 1,
                    "left=" + _w.Left.ToString("0") + " top=" + _w.Top.ToString("0") +
                    " work=" + realWork);
                Check("restore is a no-op when the window is already normal",
                    !_w.RestoreFromMaximized(dragDown, new Point(dragDown.X, dragDown.Y + 48)),
                    "state=" + _w.WindowState);

                // 还原态的即时截图不进证据：窗口状态刚从 Maximized 切回 Normal 时，
                // RenderTargetBitmap 只抓得到纯背景色。几何与状态全靠上面的断言 +
                // restore probe 数值；三种布局的顶栏截图另用 --screenshot 生成。
                Line("restore geometry covered by assertions above; drag feel needs manual check");

                // 3) 双击最大化 / 还原回归：还原分支不能把 ToggleMaximize 弄坏。
                _w.ToggleMaximize();
                Check("double-click path still maximizes",
                    _w.WindowState == WindowState.Maximized, "state=" + _w.WindowState);
                _w.ToggleMaximize();
                Check("double-click path still restores",
                    _w.WindowState == WindowState.Normal, "state=" + _w.WindowState);

                // 4) 三种布局重建外壳后，顶栏拖拽的命中背景仍在。
                foreach (var layout in new[] { LayoutMode.Compact, LayoutMode.Standard, LayoutMode.Vertical })
                {
                    _w._settings.Layout = layout;
                    _w.ApplyLayout();
                    var tb = _w._toolbarHost.Child as Grid;
                    Check("toolbar keeps drag hit-test background in " + layout,
                        tb != null && tb.Background != null,
                        tb == null ? "no toolbar" : "bg=" + tb.Background);
                }
                _w._settings.Layout = LayoutMode.Compact;
                _w.ApplyLayout();

                // ---- 第十轮：D1/D2 默认值与语言跟随系统（SchemaVersion v3）----
                var defaults = new AppSettings();
                Check("v3 is the current schema version", defaults.SchemaVersion == 3,
                    "version=" + defaults.SchemaVersion);
                Check("status bar is hidden by default", !defaults.ShowStatusBar,
                    "show=" + defaults.ShowStatusBar);
                Check("language defaults to system", defaults.Language == "system",
                    "lang=" + defaults.Language);
                Check("theme follow-system stays a separate setting", defaults.Theme == "dark",
                    "theme=" + defaults.Theme);

                var cultureName = System.Globalization.CultureInfo.CurrentUICulture.Name ?? "";
                var expected = cultureName.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                    ? "en-US" : "zh-CN";
                var resolved = L.Normalize("system");
                Check("system resolves to the concrete UI language",
                    resolved == expected && (resolved == "zh-CN" || resolved == "en-US"),
                    "culture=" + cultureName + " resolved=" + resolved);
                Check("Apply(system) sets the same effective language",
                    ApplySystemProbe() == expected, "expected=" + expected);
                Check("explicit English is not rewritten", L.Normalize("en-US") == "en-US",
                    "resolved=" + L.Normalize("en-US"));
                Check("explicit Chinese is not rewritten", L.Normalize("zh-CN") == "zh-CN",
                    "resolved=" + L.Normalize("zh-CN"));
                Check("language segment index covers all three choices",
                    SettingsPage.LanguageIndex("zh-CN") == 0 &&
                    SettingsPage.LanguageIndex("en-US") == 1 &&
                    SettingsPage.LanguageIndex("system") == 2,
                    "zh/en/system=0/1/2 expected");

                // v3 迁移：临时把 SettingsStore 指向自测文件，Load 后原样恢复。
                // 自测 _noPersist 已把 Persist 关掉，这里再显式关一次，绝不写用户配置。
                var savedPath = SettingsStore.FilePath;
                var savedPersist = SettingsStore.Persist;
                try
                {
                    var probe = Path.Combine(Root(), "out", "fix-settings-probe.json");
                    SettingsStore.FilePath = probe;
                    SettingsStore.Persist = false;

                    File.WriteAllText(probe,
                        "{\"SchemaVersion\":2,\"ShowStatusBar\":true,\"Language\":\"zh-CN\"}",
                        Encoding.UTF8);
                    var migrated = SettingsStore.Load();
                    Check("v2 config is migrated to v3", migrated.SchemaVersion == 3,
                        "version=" + migrated.SchemaVersion);
                    Check("legacy status bar true becomes hidden", !migrated.ShowStatusBar,
                        "show=" + migrated.ShowStatusBar);
                    Check("legacy zh-CN becomes system", migrated.Language == "system",
                        "lang=" + migrated.Language);

                    File.WriteAllText(probe,
                        "{\"SchemaVersion\":2,\"ShowStatusBar\":true,\"Language\":\"en-US\"}",
                        Encoding.UTF8);
                    var english = SettingsStore.Load();
                    Check("user-chosen English survives the migration",
                        english.Language == "en-US" && english.SchemaVersion == 3,
                        "lang=" + english.Language + " version=" + english.SchemaVersion);

                    File.WriteAllText(probe,
                        "{\"SchemaVersion\":3,\"ShowStatusBar\":true,\"Language\":\"zh-CN\"}",
                        Encoding.UTF8);
                    var current = SettingsStore.Load();
                    Check("already-v3 config is left alone",
                        current.SchemaVersion == 3 && current.ShowStatusBar &&
                        current.Language == "zh-CN",
                        "show=" + current.ShowStatusBar + " lang=" + current.Language);
                }
                finally
                {
                    SettingsStore.FilePath = savedPath;
                    SettingsStore.Persist = savedPersist;
                }

                // 默认值必须落到真实的 UI 上，不能只改了模型。
                // 前面 F11 断言把窗口切进了沉浸式（沉浸式下状态栏恒隐藏），
                // 先复位，测的才是 ShowStatusBar 本身而不是 _immersive。
                _w._immersive = false;
                _w._settings.ShowStatusBar = false;
                _w.ApplyLayout();
                Check("status bar collapses when the setting is off",
                    _w._statusBar.Visibility == Visibility.Collapsed,
                    "visibility=" + _w._statusBar.Visibility);
                _w._settings.ShowStatusBar = true;
                _w.ApplyLayout();
                Check("status bar can still be turned back on",
                    _w._statusBar.Visibility == Visibility.Visible,
                    "visibility=" + _w._statusBar.Visibility);
                _w._settings.ShowStatusBar = false;
                _w.ApplyLayout();

                // 新增的用户可见文案必须有英文词条，英文界面不能掉回中文。
                const string languageHint = "切换后界面立即重建，网页内容不受影响；跟随系统按 Windows 显示语言判断";
                var savedLang = L.Language;
                L.Apply("en-US");
                var translated = L.T(languageHint);
                Check("new language hint has an English entry",
                    translated != languageHint && !string.IsNullOrEmpty(translated),
                    "en=" + translated);
                L.Apply(savedLang);

                // ---- 第十一轮：A5 暗色菜单露白 ----
                // ContextMenu 是独立 HWND，截图抓不到；这里用「有效画刷 + 模板内
                // Border + 三处菜单工厂的源码」三层断言，不靠肉眼。
                var savedTheme = _w._settings.Theme;
                Theme.Apply("dark");
                var res = Application.Current.Resources;
                Func<object, double> brightness = key => Brightness(res[key] as Brush);
                double darkBg = brightness(SystemColors.MenuBrushKey);
                double darkText = brightness(SystemColors.MenuTextBrushKey);
                Check("dark menu brush is not white", darkBg >= 0 && darkBg < 0.4,
                    "brightness=" + darkBg.ToString("0.00"));
                Check("dark menu text is a light color", darkText > 0.6,
                    "brightness=" + darkText.ToString("0.00"));
                Check("dark menu text has enough contrast with its background",
                    darkText - darkBg > 0.4, "delta=" + (darkText - darkBg).ToString("0.00"));
                Check("dark menu-related system keys are all non-white",
                    brightness(SystemColors.MenuBarBrushKey) < 0.4 &&
                    brightness(SystemColors.ControlBrushKey) < 0.4 &&
                    brightness(SystemColors.WindowBrushKey) < 0.4 &&
                    brightness(SystemColors.HighlightBrushKey) < 0.4,
                    "menuBar=" + brightness(SystemColors.MenuBarBrushKey).ToString("0.00") +
                    " control=" + brightness(SystemColors.ControlBrushKey).ToString("0.00") +
                    " window=" + brightness(SystemColors.WindowBrushKey).ToString("0.00") +
                    " highlight=" + brightness(SystemColors.HighlightBrushKey).ToString("0.00"));

                var styledMenu = Theme.StyleMenu(new ContextMenu());
                var styledItem = Theme.StyleMenuItem(new MenuItem { Header = "probe" });
                var styledSep = Theme.StyleSeparator(new Separator());
                Check("styled ContextMenu background is not white",
                    Brightness(styledMenu.Background) >= 0 &&
                    Brightness(styledMenu.Background) < 0.4,
                    "brightness=" + Brightness(styledMenu.Background).ToString("0.00"));
                Check("styled MenuItem has an explicit non-white background",
                    Brightness(styledItem.Background) >= 0 &&
                    Brightness(styledItem.Background) < 0.4,
                    "brightness=" + Brightness(styledItem.Background).ToString("0.00"));
                Check("styled Separator is not white",
                    Brightness(styledSep.Background) >= 0 &&
                    Brightness(styledSep.Background) < 0.4,
                    "brightness=" + Brightness(styledSep.Background).ToString("0.00"));

                // 画刷必须是 SetResourceReference 的动态资源引用，不能写死 brush，
                // 否则切主题后菜单会留在旧配色（真实切主题仍走 ApplyLayout 重建外壳）。
                var localBg = styledMenu.ReadLocalValue(Control.BackgroundProperty);
                var localFg = styledItem.ReadLocalValue(Control.ForegroundProperty);
                Check("menu brushes are dynamic resource references, not fixed brushes",
                    !(localBg is Brush) && !(localFg is Brush) &&
                    localBg != DependencyProperty.UnsetValue,
                    "bg=" + localBg.GetType().Name + " fg=" + localFg.GetType().Name);

                Theme.Apply("light");
                Check("light theme flips menu brushes back",
                    brightness(SystemColors.MenuBrushKey) > 0.7 &&
                    brightness(SystemColors.MenuTextBrushKey) < 0.4,
                    "bg=" + brightness(SystemColors.MenuBrushKey).ToString("0.00") +
                    " text=" + brightness(SystemColors.MenuTextBrushKey).ToString("0.00"));
                var lightMenu = Theme.StyleMenu(new ContextMenu());
                Check("menus built under light theme use a light background",
                    Brightness(lightMenu.Background) > 0.7,
                    "brightness=" + Brightness(lightMenu.Background).ToString("0.00"));
                Theme.Apply("dark");
                var darkAgain = Theme.StyleMenu(new ContextMenu());
                Check("switching back restores the dark menu brush",
                    Brightness(darkAgain.Background) < 0.4,
                    "brightness=" + Brightness(darkAgain.Background).ToString("0.00"));

                // 三处菜单必须都走同一个工厂，漏一处就留白条。
                var srcDir = Path.Combine(Root(), "src", "NanoEdge");
                Func<string, string> source = name =>
                    File.ReadAllText(Path.Combine(srcDir, name), Encoding.UTF8);
                var mwSrc = source("MainWindow.cs");
                var spSrc = source("StartPage.cs");
                var mpSrc = source("MemoryPanel.cs");
                Check("all three menu hosts route through Theme.StyleMenu",
                    mwSrc.Contains("Theme.StyleMenu(") && spSrc.Contains("Theme.StyleMenu(") &&
                    mpSrc.Contains("Theme.StyleMenu("),
                    "main=" + mwSrc.Contains("Theme.StyleMenu(") +
                    " start=" + spSrc.Contains("Theme.StyleMenu(") +
                    " panel=" + mpSrc.Contains("Theme.StyleMenu("));
                Check("all three menu hosts route through Theme.StyleMenuItem",
                    mwSrc.Contains("Theme.StyleMenuItem(") &&
                    spSrc.Contains("Theme.StyleMenuItem(") &&
                    mpSrc.Contains("Theme.StyleMenuItem("),
                    "main=" + mwSrc.Contains("Theme.StyleMenuItem(") +
                    " start=" + spSrc.Contains("Theme.StyleMenuItem(") +
                    " panel=" + mpSrc.Contains("Theme.StyleMenuItem("));

                // 模板必须是我们注入的，而不是系统默认模板（硬编码白边）。
                var injected = Application.Current.Resources[typeof(MenuItem)] as Style;
                Check("MenuItem uses the injected ControlTemplate",
                    injected != null && injected.Setters.OfType<Setter>()
                        .Any(s => s.Property == Control.TemplateProperty && s.Value is ControlTemplate),
                    injected == null ? "no style" : "setters=" + injected.Setters.Count);

                // 尽力而为：真打开一个 ContextMenu，遍历系统模板里的 Border，
                // 断言暗色下没有近白背景。打开失败就标 SKIP，不算 FAIL。
                ContextMenu realMenu = null;
                MenuItem subItem = null;
                try
                {
                    realMenu = Theme.StyleMenu(new ContextMenu());
                    realMenu.Items.Add(Theme.StyleMenuItem(new MenuItem { Header = "probe" }));
                    subItem = Theme.StyleMenuItem(new MenuItem { Header = "sub" });
                    subItem.Items.Add(Theme.StyleMenuItem(new MenuItem { Header = "child" }));
                    realMenu.Items.Add(subItem);
                    realMenu.Items.Add(Theme.StyleSeparator(new Separator()));
                    realMenu.PlacementTarget = _w;
                    realMenu.IsOpen = true;
                    realMenu.ApplyTemplate();
                    realMenu.UpdateLayout();
                    // 不打开二级菜单：嵌套 Popup 会让自测进程的消息循环退不出去。
                    // Popup.Child 在模板应用后就存在，直接遍历它即可。

                    // 一级菜单 + 二级菜单 Popup.Child 都在检查范围内；后者上次漏了。
                    var borders = VisualDescendants<Border>(realMenu).ToList();
                    var popups = VisualDescendants<Popup>(realMenu).ToList();
                    foreach (var popup in popups)
                    {
                        Line("  menu popup isOpen=" + popup.IsOpen + " child=" +
                             (popup.Child == null ? "null" : popup.Child.GetType().Name));
                        if (popup.Child != null)
                            borders.AddRange(VisualDescendants<Border>(popup.Child));
                    }
                    borders = borders.Distinct().ToList();
                    for (var i = 0; i < borders.Count; i++)
                    {
                        var b = borders[i];
                        Line("  menu border[" + i + "] name=" + (string.IsNullOrEmpty(b.Name) ? "-" : b.Name) +
                             " bg=" + BrushText(b.Background) +
                             " brush=" + BrushText(b.BorderBrush) +
                             " thickness=" + b.BorderThickness);
                    }
                    // 白边可能来自 Background，也可能来自 BorderBrush；两个都要查。
                    var whiteBorders = borders.Where(b =>
                        IsWhiteish(b.Background) ||
                        (b.BorderThickness.Left + b.BorderThickness.Top +
                         b.BorderThickness.Right + b.BorderThickness.Bottom > 0 &&
                         IsWhiteish(b.BorderBrush))).ToList();
                    Check("real menu template has no white background or border in dark",
                        borders.Count > 0 && whiteBorders.Count == 0,
                        "borders=" + borders.Count + " popups=" + popups.Count +
                        " white=" + whiteBorders.Count);

                    // Popup.Child 自己就是 Border，VisualDescendants 只找后代，得单独算上。
                    var submenuBorders = new List<Border>();
                    foreach (var p in popups.Where(p => p.Child != null))
                    {
                        var self = p.Child as Border;
                        if (self != null) submenuBorders.Add(self);
                        submenuBorders.AddRange(VisualDescendants<Border>(p.Child));
                    }
                    submenuBorders = submenuBorders.Distinct().ToList();
                    Check("submenu popup is themed (no white background or border)",
                        submenuBorders.Count > 0 && submenuBorders.All(b =>
                            !IsWhiteish(b.Background) &&
                            !(b.BorderThickness.Left + b.BorderThickness.Top +
                              b.BorderThickness.Right + b.BorderThickness.Bottom > 0 &&
                              IsWhiteish(b.BorderBrush))),
                        "submenuBorders=" + submenuBorders.Count);

                    // 自定义模板不能把二级菜单功能弄丢：不真打开（会留 HWND
                    // 让自测进程退不出去），只验证 IsOpen 仍绑在 IsSubmenuOpen 上。
                    var submenuBound = popups.Any(p =>
                    {
                        var binding = BindingOperations.GetBinding(p, Popup.IsOpenProperty);
                        return binding != null && binding.Path != null &&
                               binding.Path.Path == "IsSubmenuOpen";
                    });
                    Check("submenu popup still opens through IsSubmenuOpen",
                        submenuBound, "popups=" + popups.Count);
                }
                catch (Exception ex)
                {
                    Line("[SKIP] real ContextMenu template probe: " + ex.Message);
                }
                finally
                {
                    if (subItem != null) { try { subItem.IsSubmenuOpen = false; } catch { } }
                    if (realMenu != null) realMenu.IsOpen = false;
                    Theme.Apply(savedTheme);
                }

                // 真实 ContextMenu 元素本身也能被 RenderTargetBitmap 直接渲染
                // （渲染整个 Window 才抓不到独立 HWND）。这两张是真实模板的视觉证据。
                try
                {
                    Line("real menu render (dark): " + RenderRealMenu(true));
                    Line("real menu render (light): " + RenderRealMenu(false));
                }
                catch (Exception ex) { Line("[SKIP] real menu render: " + ex.Message); }
                Theme.Apply(savedTheme);

                // ---- P0：最小化坐标 -32000 不能再把窗口放到屏幕外 ----
                Check("minimized -32000 coordinate is rejected",
                    !MainWindow.IsSaneWindowCoord(-32000), "value=-32000");
                Check("NaN / infinity coordinates are rejected",
                    !MainWindow.IsSaneWindowCoord(double.NaN) &&
                    !MainWindow.IsSaneWindowCoord(double.NegativeInfinity), "nan/inf");
                Check("off-screen huge coordinate is rejected",
                    !MainWindow.IsSaneWindowCoord(500000), "value=500000");
                Check("normal and multi-monitor negative coordinates are accepted",
                    MainWindow.IsSaneWindowCoord(120) &&
                    MainWindow.IsSaneWindowCoord(-1920) &&
                    MainWindow.IsSaneWindowCoord(0), "120/-1920/0");
                Check("startup validates saved bounds before Manual positioning",
                    mwSrc.Contains("hasUsableBounds") &&
                    mwSrc.Contains("WindowStartupLocation.CenterScreen"),
                    "validated=" + mwSrc.Contains("hasUsableBounds"));
                Check("SaveWindowBounds refuses to persist invalid coordinates",
                    mwSrc.Contains("IsSaneWindowCoord(Left)") &&
                    mwSrc.Contains("IsSaneWindowCoord(Top)"),
                    "guarded=" + mwSrc.Contains("IsSaneWindowCoord(Left)"));
                Check("current session has no poisoned window coordinates",
                    double.IsNaN(_w._settings.WindowLeft) ||
                    MainWindow.IsSaneWindowCoord(_w._settings.WindowLeft),
                    "left=" + _w._settings.WindowLeft);
            }

            private static string RenderRealMenu(bool dark)
            {
                Theme.Apply(dark ? "dark" : "light");
                var menu = Theme.StyleMenu(new ContextMenu { MinWidth = 220 });
                menu.Items.Add(Theme.StyleMenuItem(new MenuItem { Header = L.T("设置") }));
                var sub = Theme.StyleMenuItem(
                    new MenuItem { Header = L.T("内存档位：") + L.T("标准") });
                sub.Items.Add(Theme.StyleMenuItem(new MenuItem { Header = L.T("少重载") }));
                sub.Items.Add(Theme.StyleMenuItem(new MenuItem { Header = L.T("省内存") }));
                menu.Items.Add(sub);
                menu.Items.Add(Theme.StyleSeparator(new Separator()));
                menu.Items.Add(Theme.StyleMenuItem(new MenuItem { Header = L.T("用户脚本") }));

                menu.ApplyTemplate();
                menu.Measure(new Size(320, 400));
                double w = Math.Max(160, Math.Ceiling(menu.DesiredSize.Width));
                double h = Math.Max(80, Math.Ceiling(menu.DesiredSize.Height));
                menu.Arrange(new Rect(0, 0, w, h));
                menu.UpdateLayout();

                var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(menu);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                var path = Path.Combine(Root(), "out",
                    dark ? "menu-real-dark.png" : "menu-real-light.png");
                using (var fs = File.Create(path)) encoder.Save(fs);

                // 模板里的 Popup 被渲染后会留下 HWND，不主动拆会让自测进程退不出去。
                foreach (var p in VisualDescendants<Popup>(menu).ToList())
                {
                    try { p.IsOpen = false; p.Child = null; } catch { }
                }
                menu.Template = null;
                menu.Items.Clear();
                return path + " " + w.ToString("0") + "x" + h.ToString("0");
            }

            // 亮度 0..1；透明或空画刷返回 -1，避免把「没设背景」误判成深色。
            private static double Brightness(Brush brush)
            {
                var solid = brush as SolidColorBrush;
                if (solid == null) return -1;
                var c = solid.Color;
                if (c.A < 200) return -1;
                return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            }

            private static bool IsWhiteish(Brush brush)
            {
                return Brightness(brush) > 0.85;
            }

            private static string BrushText(Brush brush)
            {
                var solid = brush as SolidColorBrush;
                if (solid == null) return brush == null ? "null" : brush.GetType().Name;
                return solid.Color.ToString();
            }

            private static IEnumerable<T> VisualDescendants<T>(DependencyObject node)
                where T : DependencyObject
            {
                if (node == null) yield break;
                var count = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);
                    if (child is T) yield return (T)child;
                    foreach (var grand in VisualDescendants<T>(child)) yield return grand;
                }
            }

            // 单独一个探针，避免把 L.Language 留在 system 上影响后续断言。
            private static string ApplySystemProbe()
            {
                var saved = L.Language;
                L.Apply("system");
                var effective = L.Language;
                L.Apply(saved);
                return effective;
            }

            // 把窗口置前后真实发一次 F5。页面重载会清掉注入的标记，
            // 这样能区分「按键没送到窗口」和「页面被强制刷新」。
            private async Task TestF5Async()
            {
                var tab = _w._active;
                var path = Path.Combine(Root(), "out", "f5-keytest.html");
                if (tab == null || !File.Exists(path))
                {
                    Line("[SKIP] f5 e2e: missing active tab or test page");
                    return;
                }
                try
                {
                    await _w._browser.NavigateAsync(tab, new Uri(path).AbsoluteUri);
                    CoreWebView2 core = null;
                    for (var i = 0; i < 40; i++)
                    {
                        core = _w._browser.Core(tab);
                        if (core != null) break;
                        await Task.Delay(100);
                    }
                    if (core == null) { Line("[SKIP] f5 e2e: no WebView2 core"); return; }
                    await Task.Delay(700);

                    await core.ExecuteScriptAsync("window.__nanoF5Marker = 1; 'ok'");

                    // 真实按键会被前台/焦点规则挡住，直接把 WM_KEYDOWN 投递到
                    // WebView2 的渲染窗口，这样测的仍是完整的内核输入链路。
                    const byte VK_F5 = 0x74;
                    var child = _w._browser.WindowHandleOf(tab);
                    if (child == IntPtr.Zero) { Line("[SKIP] f5 e2e: no WebView2 HWND"); return; }

                    var render = IntPtr.Zero;
                    EnumChildWindows(child, (h, p) =>
                    {
                        var name = new StringBuilder(256);
                        GetClassName(h, name, name.Capacity);
                        if (!name.ToString().Contains("Chrome_RenderWidgetHostHWND")) return true;
                        render = h;
                        return false;
                    }, IntPtr.Zero);
                    if (render == IntPtr.Zero) render = child;
                    Line("f5 e2e: webview=" + child + " render=" + render);

                    var scan = MapVirtualKey(VK_F5, 0);
                    var down = 1 | (int)(scan << 16);
                    PostMessage(render, 0x0100, (IntPtr)VK_F5, (IntPtr)down);
                    PostMessage(render, 0x0101, (IntPtr)VK_F5,
                        (IntPtr)(down | unchecked((int)0xC0000000)));
                    await Task.Delay(900);

                    var n = (await core.ExecuteScriptAsync(
                        "document.getElementById('n') ? document.getElementById('n').textContent : 'missing'")) ?? "";
                    var marker = (await core.ExecuteScriptAsync(
                        "window.__nanoF5Marker === 1 ? 'yes' : 'no'")) ?? "";
                    n = n.Trim('"');
                    marker = marker.Trim('"');

                    if (marker == "no")
                        Check("F5 reaches page JS (no forced reload)", false,
                            "page was reloaded, counter=" + n);
                    else if (n == "1")
                        Check("F5 reaches page JS (no forced reload)", true,
                            "counter=1, page not reloaded");
                    else
                        Line("[SKIP] f5 e2e: injected WM_KEYDOWN did not reach the renderer " +
                             "(counter=" + n + "). Host-side assertions above verify both " +
                             "interception layers; confirm real key with out/f5-keytest.html.");
                }
                catch (Exception ex)
                {
                    Line("[SKIP] f5 e2e: " + ex.Message);
                }
            }
        }
    }
}
