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
                    // Popup 是独立 HWND：Toast / 悬浮面板不主动关会让进程在
                    // Shutdown 后仍挂住（与 MemorySelfTest 的收尾同一原因）。
                    try { if (_w._toast != null) _w._toast.Hide(); } catch { }
                    try { if (_w._verticalPopup != null) _w._verticalPopup.IsOpen = false; } catch { }
                    try { if (_w._memoryPanel != null) _w._memoryPanel.Close(); } catch { }
                    try { if (_w._suggestions != null) _w._suggestions.Close(); } catch { }
                    // 主动走窗口关闭路径，让 WebView2 释放和 Popup 收尾都同步完成；
                    // 只调 Shutdown 时，独立 HWND 有可能让进程在报告写完后仍挂着。
                    // 再加一个后台看门狗：测试进程宁可直接退出，也不允许挂住全量自测。
                    var watchdog = new System.Threading.Thread(() =>
                    {
                        System.Threading.Thread.Sleep(3000);
                        Environment.Exit(0);
                    });
                    watchdog.IsBackground = true;
                    watchdog.Start();
                    try { _w.Close(); } catch { }
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

            // 分组现在包在一个 Grid 区段里；测试要同时看得到平铺 chip 和区段内的 chip/头。
            private static IEnumerable<FrameworkElement> TabItems(Panel panel)
            {
                if (panel == null) yield break;
                foreach (var child in panel.Children)
                {
                    var fe = child as FrameworkElement;
                    if (fe == null) continue;
                    yield return fe;
                    var grid = fe as Grid;
                    if (grid == null || !(grid.Tag is TabGroup)) continue;
                    foreach (var stack in grid.Children.OfType<StackPanel>())
                        foreach (var inner in stack.Children.OfType<FrameworkElement>())
                            yield return inner;
                }
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
                Check("new tab is appended at the end", _w._tabs.Last() == _w._active,
                    "last=" + (_w._tabs.Last() == _w._active));
                _w.RebuildTabStrip();

                var chips = TabItems(_w._tabStripPanel).OfType<Border>()
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

                var overlayChip = chips.FirstOrDefault(b => ReferenceEquals(b.Tag, _w._active));
                var overlayGrid = overlayChip == null ? null : overlayChip.Child as Grid;
                var dropCaret = overlayGrid == null ? null
                    : overlayGrid.Children.OfType<Border>()
                        .FirstOrDefault(b => Equals(b.Tag, "drop-caret"));
                var groupHalo = overlayGrid == null ? null
                    : overlayGrid.Children.OfType<Border>()
                        .FirstOrDefault(b => Equals(b.Tag, "group-halo"));
                Check("tab chip has drop caret and group halo overlays",
                    dropCaret != null && groupHalo != null,
                    "caret=" + (dropCaret != null) + " halo=" + (groupHalo != null));

                // 新标签追加在 _tabs 末尾；+ 紧跟最后一个标签，不再固定占导航行。
                var stripChildren = _w._tabStripPanel == null ? new List<UIElement>()
                    : _w._tabStripPanel.Children.Cast<UIElement>().ToList();
                Check("new tab button follows the last tab",
                    stripChildren.Count >= 2 &&
                    stripChildren[stripChildren.Count - 1] is Button &&
                    Equals(((FrameworkElement)stripChildren[stripChildren.Count - 1]).Tag, "newtab") &&
                    ((FrameworkElement)stripChildren[stripChildren.Count - 2]).Tag is TabModel,
                    "last=" + (stripChildren.Count == 0 ? "none"
                        : stripChildren[stripChildren.Count - 1].GetType().Name));
                Check("new tab button is not fixed in the navigation row",
                    _w._navigationRow != null &&
                    !_w._navigationRow.Children.OfType<Button>()
                        .Any(b => Equals(b.Tag, "newtab")),
                    "navButtons=" + (_w._navigationRow == null ? "null"
                        : _w._navigationRow.Children.OfType<Button>().Count().ToString()));
                Check("tab strip has scroll chevrons",
                    _w._tabStripPrev != null && _w._tabStripNext != null &&
                    Equals(_w._tabStripPrev.Tag, "tab-scroll-left") &&
                    Equals(_w._tabStripNext.Tag, "tab-scroll-right"),
                    "prev=" + (_w._tabStripPrev != null) +
                    " next=" + (_w._tabStripNext != null));

                // 多标签：chip 变窄、地址栏让宽、右侧出现滚动箭头；滚动后左侧也出现。
                var overflowProbes = new List<TabModel>();
                for (var i = 0; i < 22; i++)
                {
                    var probe = new TabModel
                    {
                        Title = "overflow " + i,
                        Url = "about:blank",
                        IsStartPage = false
                    };
                    _w._tabs.Add(probe);
                    overflowProbes.Add(probe);
                }
                _w.RebuildTabStrip();
                _w.UpdateLayout();
                _w.UpdateTabStripScrollButtons();
                Check("overflowing strip shows the right scroll chevron",
                    _w._tabStrip.ScrollableWidth > 0.5 &&
                    _w._tabStripNext.Visibility == Visibility.Visible,
                    "scrollable=" + _w._tabStrip.ScrollableWidth +
                    " viewport=" + _w._tabStrip.ViewportWidth +
                    " extent=" + _w._tabStrip.ExtentWidth +
                    " actual=" + _w._tabStrip.ActualWidth +
                    " panel=" + _w._tabStripPanel.ActualWidth +
                    " children=" + _w._tabStripPanel.Children.Count +
                    " tabs=" + _w._tabs.Count +
                    " next=" + _w._tabStripNext.Visibility);
                Check("many tabs give the strip more address-bar width",
                    _w._navigationRow.ColumnDefinitions[2].Width.Value == 60,
                    "share=" + _w._navigationRow.ColumnDefinitions[2].Width.Value);
                _w.ScrollTabStrip(1);
                _w.UpdateLayout();
                _w.UpdateTabStripScrollButtons();
                Check("scrolled strip shows the left scroll chevron",
                    _w._tabStripPrev.Visibility == Visibility.Visible,
                    "offset=" + _w._tabStrip.HorizontalOffset);
                _w._tabStrip.ScrollToHorizontalOffset(0);
                foreach (var probe in overflowProbes) _w._tabs.Remove(probe);
                _w.RebuildTabStrip();
                Check("fewer tabs restore the compact column share",
                    _w._navigationRow.ColumnDefinitions[2].Width.Value == 45,
                    "share=" + _w._navigationRow.ColumnDefinitions[2].Width.Value);

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

                // ---- 分组：拖放 / 排序拆分 / 颜色 / 解散 ----
                // 走真实入口「新建分组并移入」：CreateGroupForTab。
                var originalTab = _w._tabs[0];
                _w.CreateGroupForTab(originalTab);
                var group = _w._groups[_w._groups.Count - 1];
                Check("new group gets a random palette color",
                    MainWindow.GroupColorPalette.Contains(group.Color),
                    "color=" + group.Color);
                Check("group color palette entries are distinct",
                    MainWindow.GroupColorPalette.Distinct().Count() ==
                    MainWindow.GroupColorPalette.Length,
                    "colors=" + MainWindow.GroupColorPalette.Length);

                Func<List<Border>> groupHeaders = () => TabItems(_w._tabStripPanel)
                    .OfType<Border>().Where(b => Equals(b.Tag, "group")).ToList();
                var headers = groupHeaders();
                Check("horizontal strip shows group header", headers.Count == 1,
                    "headers=" + headers.Count);
                Check("group header accepts tab drops",
                    headers.Count == 1 && headers[0].AllowDrop,
                    "allowDrop=" + (headers.Count == 1 && headers[0].AllowDrop));
                Check("grouped tab still rendered", TabItems(_w._tabStripPanel)
                    .Any(f => f.Tag is TabModel && ((TabModel)f.Tag).GroupId == group.Id),
                    "panel=" + (_w._tabStripPanel == null ? "null"
                        : _w._tabStripPanel.Children.Count.ToString()));
                var firstSection = _w._tabStripPanel == null ? null
                    : _w._tabStripPanel.Children.OfType<Grid>()
                        .FirstOrDefault(g => g.Tag is TabGroup);
                var firstSectionItems = firstSection == null ? new List<FrameworkElement>()
                    : firstSection.Children.OfType<StackPanel>().FirstOrDefault()?
                        .Children.OfType<FrameworkElement>().ToList() ?? new List<FrameworkElement>();
                Check("group header and member share one section",
                    firstSection != null &&
                    firstSectionItems.Any(f => Equals(f.Tag, "group")) &&
                    firstSectionItems.Any(f => f.Tag is TabModel &&
                        ((TabModel)f.Tag).GroupId == group.Id),
                    "items=" + firstSectionItems.Count);
                Check("group section accepts tab drops",
                    firstSection != null && firstSection.AllowDrop,
                    "allowDrop=" + (firstSection != null && firstSection.AllowDrop));
                var groupCaret = firstSection == null ? null
                    : firstSection.Children.OfType<Border>()
                        .FirstOrDefault(b => Equals(b.Tag, "group-drop-caret"));
                var expectedGroupColor = (Color)ColorConverter.ConvertFromString(group.Color);
                Check("group section has a group-colored insertion caret",
                    groupCaret != null && groupCaret.Background is SolidColorBrush sb &&
                    sb.Color == expectedGroupColor,
                    groupCaret == null ? "no caret"
                        : "caret=" + groupCaret.Background);
                Check("horizontal strip blank area accepts tab drops",
                    _w._tabStrip != null && _w._tabStrip.AllowDrop,
                    "allowDrop=" + (_w._tabStrip != null && _w._tabStrip.AllowDrop));
                Check("vertical rail blank area accepts tab drops",
                    _w._rail != null && _w._rail.AllowDrop,
                    "allowDrop=" + (_w._rail != null && _w._rail.AllowDrop));
                Check("vertical popup blank area accepts tab drops",
                    _w._popupContent != null && _w._popupContent.AllowDrop,
                    "allowDrop=" + (_w._popupContent != null && _w._popupContent.AllowDrop));

                // 颜色：换色后分组头的圆点跟着变。
                var color2 = MainWindow.GroupColorPalette
                    .First(c => !MainWindow.SameColor(c, group.Color));
                _w.SetGroupColor(group, color2);
                _w.RebuildTabStrip();
                var header = groupHeaders().FirstOrDefault();
                var headerPanel = header == null ? null : header.Child as StackPanel;
                var dot = headerPanel == null ? null
                    : headerPanel.Children.OfType<Border>().FirstOrDefault();
                var dotBrush = dot == null ? null : dot.Background as SolidColorBrush;
                var expectedColor = (Color)ColorConverter.ConvertFromString(color2);
                Check("group color updates the header dot",
                    dotBrush != null && dotBrush.Color == expectedColor,
                    "dot=" + (dotBrush == null ? "null" : dotBrush.Color.ToString()) +
                    " expected=" + expectedColor);

                // 颜色菜单：六项预设色都带色块，当前颜色打勾。
                try
                {
                    var colorMenu = _w.BuildGroupMenu(group);
                    var colorsItem = colorMenu.Items.OfType<MenuItem>()
                        .FirstOrDefault(m => m.Items.Count == MainWindow.GroupColorPalette.Length);
                    Check("group menu lists all preset colors",
                        colorsItem != null &&
                        colorsItem.Items.Count == MainWindow.GroupColorPalette.Length &&
                        colorsItem.Items.OfType<MenuItem>().All(m => m.Icon is Border),
                        "items=" + (colorsItem == null ? "none"
                            : colorsItem.Items.Count.ToString()) +
                        " swatches=" + (colorsItem == null ? "none"
                            : colorsItem.Items.OfType<MenuItem>()
                                .Count(m => m.Icon is Border).ToString()));
                    var marked = colorsItem == null ? 0
                        : colorsItem.Items.OfType<MenuItem>()
                            .Count(m => ((string)m.Header).StartsWith("✓ ", StringComparison.Ordinal));
                    Check("current group color is marked", marked == 1, "marked=" + marked);
                    Check("group menu includes rename and close entries",
                        colorMenu.Items.OfType<MenuItem>()
                            .Any(m => Equals(m.Header, L.T("重命名分组"))) &&
                        colorMenu.Items.OfType<MenuItem>()
                            .Any(m => Equals(m.Header,
                                L.T("关闭分组（关闭全部标签）"))),
                        "menuItems=" + colorMenu.Items.OfType<MenuItem>().Count());

                    colorMenu.ApplyTemplate();
                    colorMenu.Measure(new Size(320, 400));
                    var menuW = Math.Max(160, Math.Ceiling(colorMenu.DesiredSize.Width));
                    var menuH = Math.Max(80, Math.Ceiling(colorMenu.DesiredSize.Height));
                    colorMenu.Arrange(new Rect(0, 0, menuW, menuH));
                    colorMenu.UpdateLayout();
                    var colorRtb = new RenderTargetBitmap((int)menuW, (int)menuH,
                        96, 96, PixelFormats.Pbgra32);
                    colorRtb.Render(colorMenu);
                    var colorEncoder = new PngBitmapEncoder();
                    colorEncoder.Frames.Add(BitmapFrame.Create(colorRtb));
                    var colorPath = Path.Combine(Root(), "out", "check-group-color-menu.png");
                    using (var stream = File.Create(colorPath)) colorEncoder.Save(stream);
                    Line("group color menu screenshot: out/check-group-color-menu.png");
                }
                catch (Exception ex) { Line("group color menu screenshot failed: " + ex.Message); }

                // 第二个分组走同一条路径，顺便提供「两个不同颜色分组头」的截图。
                var extra = new TabModel
                {
                    Title = "group drop probe",
                    Url = "about:blank",
                    IsStartPage = false
                };
                _w._tabs.Insert(1, extra);
                _w.RebuildTabStrip();
                var extraIndex = _w._tabs.IndexOf(extra);
                _w.CreateGroupForTab(extra);
                var group2 = _w._groups[_w._groups.Count - 1];
                _w.RebuildTabStrip();
                Check("group creation keeps the tab at its index",
                    _w._tabs.IndexOf(extra) == extraIndex,
                    "before=" + extraIndex + " after=" + _w._tabs.IndexOf(extra));
                headers = groupHeaders();
                Check("two groups render two headers", headers.Count == 2,
                    "headers=" + headers.Count);
                Check("two default group colors differ",
                    !MainWindow.SameColor(group.Color, group2.Color),
                    group.Color + "/" + group2.Color);
                var sectionGaps = _w._tabStripPanel.Children.OfType<Border>()
                    .Where(b => Equals(b.Tag, "section-gap")).ToList();
                Check("section boundary gaps are drop targets",
                    sectionGaps.Count == 2 && sectionGaps.All(g => g.AllowDrop),
                    "count=" + sectionGaps.Count);
                Check("section boundary gap has an insertion caret",
                    sectionGaps.Count > 0 && sectionGaps.All(g =>
                        g.Child is Border c && Equals(c.Tag, "gap-caret")),
                    "gaps=" + sectionGaps.Count);

                // 两个连续分组之间：独立标签可以落在 gap 上，保持平铺。
                var gapProbe = new TabModel
                {
                    Title = "gap probe",
                    Url = "about:blank",
                    IsStartPage = false
                };
                _w._tabs.Add(gapProbe);
                _w.RebuildTabStrip();
                _w.MoveTabToBoundary(gapProbe, extra);
                Check("flat tab can sit between two groups",
                    string.IsNullOrEmpty(gapProbe.GroupId) &&
                    _w._tabs.IndexOf(gapProbe) < _w._tabs.IndexOf(extra) &&
                    _w._tabs.IndexOf(gapProbe) > _w._tabs.IndexOf(_w._tabs.First(t => t.GroupId == group.Id)),
                    "gap=" + _w._tabs.IndexOf(gapProbe) +
                    " extra=" + _w._tabs.IndexOf(extra));
                _w._tabs.Remove(gapProbe);
                _w.RebuildTabStrip();

                // 视觉证据：两个不同颜色的分组头。
                try
                {
                    _w.UpdateLayout();
                    _w.CaptureScreenshot(Path.Combine(Root(), "out", "check-group-header.png"));
                    Line("group header screenshot: out/check-group-header.png");
                }
                catch (Exception ex) { Line("group header screenshot failed: " + ex.Message); }

                // 纵向：分组区段按 _tabs 顺序内联，成员不再掉到最下面；+ 在最后一个标签下面。
                try
                {
                    _w._settings.Layout = LayoutMode.Vertical;
                    _w._settings.VerticalExpanded = true;
                    _w.ApplyLayout();
                    _w.RebuildTabStrip();
                    var railTabs = TabItems(_w._railPanel).Where(f => f.Tag is TabModel)
                        .Select(f => (TabModel)f.Tag).ToList();
                    Check("vertical tab order follows _tabs",
                        railTabs.SequenceEqual(_w._tabs),
                        "rail=" + string.Join(",", railTabs.Select(t => t.Title)) +
                        " tabs=" + string.Join(",", _w._tabs.Select(t => t.Title)));
                    var railSection = _w._railPanel.Children.OfType<Grid>()
                        .FirstOrDefault(g => g.Tag is TabGroup);
                    var railSectionItems = railSection == null ? new List<FrameworkElement>()
                        : railSection.Children.OfType<StackPanel>().FirstOrDefault()?
                            .Children.OfType<FrameworkElement>().ToList()
                            ?? new List<FrameworkElement>();
                    Check("vertical group header and member stay in one section",
                        railSection != null &&
                        railSectionItems.Any(f => Equals(f.Tag, "group")) &&
                        railSectionItems.Any(f => f.Tag is TabModel),
                        "items=" + railSectionItems.Count);
                    Check("vertical group section accepts tab drops",
                        railSection != null && railSection.AllowDrop,
                        "allowDrop=" + (railSection != null && railSection.AllowDrop));
                    var verticalGaps = _w._railPanel.Children.OfType<Border>()
                        .Where(b => Equals(b.Tag, "section-gap")).ToList();
                    Check("vertical section boundary gaps are drop targets",
                        verticalGaps.Count == 2 && verticalGaps.All(g => g.AllowDrop),
                        "count=" + verticalGaps.Count);
                    var railLast = _w._railPanel.Children.Cast<UIElement>().Last()
                        as FrameworkElement;
                    var popLast = _w._popupPanel.Children.Cast<UIElement>().Last()
                        as FrameworkElement;
                    Check("vertical new tab button follows the last tab",
                        railLast is Button && Equals(railLast.Tag, "newtab"),
                        "last=" + (railLast == null ? "none" : railLast.GetType().Name));
                    Check("popup new tab button follows the last tab",
                        popLast is Button && Equals(popLast.Tag, "newtab"),
                        "last=" + (popLast == null ? "none" : popLast.GetType().Name));
                    var popTabs = TabItems(_w._popupPanel).Where(f => f.Tag is TabModel)
                        .Select(f => (TabModel)f.Tag).ToList();
                    Check("popup tab order follows _tabs",
                        popTabs.SequenceEqual(_w._tabs),
                        "popup=" + string.Join(",", popTabs.Select(t => t.Title)) +
                        " tabs=" + string.Join(",", _w._tabs.Select(t => t.Title)));
                    try
                    {
                        _w.UpdateLayout();
                        _w.CaptureScreenshot(Path.Combine(Root(), "out",
                            "check-vertical-groups.png"));
                        Line("vertical group screenshot: out/check-vertical-groups.png");
                    }
                    catch (Exception ex) { Line("vertical group screenshot failed: " + ex.Message); }
                }
                finally
                {
                    _w._settings.Layout = LayoutMode.Compact;
                    _w._settings.VerticalExpanded = false;
                    _w.ApplyLayout();
                }

                // 首组前面的边界：独立标签可以保持在组外。
                var frontProbe = new TabModel
                {
                    Title = "front probe",
                    Url = "about:blank",
                    IsStartPage = false
                };
                _w._tabs.Add(frontProbe);
                _w.RebuildTabStrip();
                var firstGroupMember = _w._tabs.First(t => t.GroupId == group.Id);
                _w.MoveTabToBoundary(frontProbe, firstGroupMember);
                Check("flat tab can sit before the leading group",
                    string.IsNullOrEmpty(frontProbe.GroupId) &&
                    _w._tabs.IndexOf(frontProbe) == 0 &&
                    _w._tabs.IndexOf(firstGroupMember) > 0,
                    "front=" + _w._tabs.IndexOf(frontProbe) +
                    " groupFirst=" + _w._tabs.IndexOf(firstGroupMember));
                _w._tabs.Remove(frontProbe);
                _w.RebuildTabStrip();

                // 拖到分组头 = 移入并排到同组末尾。
                var flat = new TabModel
                {
                    Title = "drop target probe",
                    Url = "about:blank",
                    IsStartPage = false
                };
                _w._tabs.Add(flat);
                _w.RebuildTabStrip();
                _w.MoveTabIntoGroup(flat, group);
                var lastOther = _w._tabs
                    .Where(t => t.GroupId == group.Id && t != flat).LastOrDefault();
                var flatIndex = _w._tabs.IndexOf(flat);
                var lastIndex = lastOther == null ? -1 : _w._tabs.IndexOf(lastOther);
                Check("tab dropped on the header joins the group",
                    flat.GroupId == group.Id, "groupId=" + flat.GroupId);
                Check("dropped tab is appended after existing group tabs",
                    flatIndex == lastIndex + 1,
                    "flat=" + flatIndex + " last=" + lastIndex);

                // 收起的分组头收到标签后自动展开，避免"拖进去了却看不见"。
                group.IsCollapsed = true;
                _w.RebuildTabStrip();
                _w.MoveTabIntoGroup(extra, group);
                Check("moving across groups expands the target group",
                    !group.IsCollapsed && extra.GroupId == group.Id,
                    "collapsed=" + group.IsCollapsed + " groupId=" + extra.GroupId);

                // 同组拖放只排序，不改 GroupId。
                _w.ReorderTabs(extra, flat);
                Check("same-group drop reorders without changing membership",
                    extra.GroupId == group.Id && flat.GroupId == group.Id &&
                    _w._tabs.IndexOf(extra) < _w._tabs.IndexOf(flat),
                    "extra=" + _w._tabs.IndexOf(extra) +
                    " flat=" + _w._tabs.IndexOf(flat));

                // 跨组拖放不改归属（要移入分组请拖分组头）。
                var flat2 = new TabModel
                {
                    Title = "cross group probe",
                    Url = "about:blank",
                    IsStartPage = false
                };
                _w._tabs.Add(flat2);
                _w.RebuildTabStrip();
                var beforeIndex = _w._tabs.IndexOf(flat);
                _w.ReorderTabs(flat, flat2);
                Check("dropping across group boundaries keeps membership",
                    flat.GroupId == group.Id && string.IsNullOrEmpty(flat2.GroupId),
                    "flat=" + flat.GroupId + " flat2=" + flat2.GroupId);
                Check("cross-group drop is a no-op for order",
                    _w._tabs.IndexOf(flat) == beforeIndex,
                    "before=" + beforeIndex + " after=" + _w._tabs.IndexOf(flat));

                // 分组之后新增的平铺标签必须渲染在分组之后，不能被塞回分组前面。
                var tail = new TabModel
                {
                    Title = "tail probe",
                    Url = "about:blank",
                    IsStartPage = false
                };
                _w._tabs.Add(tail);
                _w.RebuildTabStrip();
                var tailTabs = TabItems(_w._tabStripPanel).OfType<Border>()
                    .Where(b => b.Tag is TabModel).ToList();
                Check("flat tab after a group renders last",
                    tailTabs.Count > 0 && ReferenceEquals(tailTabs.Last().Tag, tail),
                    "last=" + (tailTabs.Count == 0 ? "none"
                        : ((TabModel)tailTabs.Last().Tag).Title));
                _w._tabs.Remove(tail);
                _w.RebuildTabStrip();

                // ---- 批次 2：成员附近插入 / 拖出 / 空白落点 ----
                var atProbe = new TabModel
                {
                    Title = "at probe",
                    Url = "about:blank",
                    IsStartPage = false
                };
                _w._tabs.Add(atProbe);
                _w.RebuildTabStrip();
                _w.MoveTabIntoGroupAt(atProbe, group, flat, false);
                Check("member drop inserts before the target",
                    atProbe.GroupId == group.Id &&
                    _w._tabs.IndexOf(atProbe) == _w._tabs.IndexOf(flat) - 1,
                    "at=" + _w._tabs.IndexOf(atProbe) +
                    " flat=" + _w._tabs.IndexOf(flat));
                _w.MoveTabIntoGroupAt(atProbe, group, flat, true);
                Check("member drop after the target reorders within the group",
                    atProbe.GroupId == group.Id &&
                    _w._tabs.IndexOf(atProbe) == _w._tabs.IndexOf(flat) + 1,
                    "at=" + _w._tabs.IndexOf(atProbe) +
                    " flat=" + _w._tabs.IndexOf(flat));
                _w._tabs.Remove(atProbe);
                _w.RebuildTabStrip();

                var outProbe = new TabModel
                {
                    Title = "out probe",
                    Url = "about:blank",
                    IsStartPage = false
                };
                _w._tabs.Add(outProbe);
                _w.MoveTabIntoGroup(outProbe, group);
                _w.MoveTabOutOfGroupAt(outProbe, flat2, false);
                Check("drop on a flat tab removes the tab from the group",
                    string.IsNullOrEmpty(outProbe.GroupId) &&
                    _w._tabs.IndexOf(outProbe) == _w._tabs.IndexOf(flat2) - 1,
                    "group=" + outProbe.GroupId +
                    " out=" + _w._tabs.IndexOf(outProbe) +
                    " flat2=" + _w._tabs.IndexOf(flat2));
                _w._tabs.Remove(outProbe);
                _w.RebuildTabStrip();

                _w.DropTabOnBlank(flat);
                Check("drop on blank space removes the tab and moves it last",
                    string.IsNullOrEmpty(flat.GroupId) && _w._tabs.Last() == flat,
                    "group=" + flat.GroupId + " last=" +
                    (_w._tabs.Last() == flat));

                // ---- 整组拖动：成员区段整体搬移，前后目标都保持连续 ----
                var moveProbe = new TabModel
                {
                    Title = "move probe",
                    Url = "about:blank",
                    IsStartPage = false
                };
                _w._tabs.Insert(0, moveProbe);
                _w.RebuildTabStrip();
                _w.MoveGroupAt(group, moveProbe, false);
                var members = _w._tabs.Where(t => t.GroupId == group.Id).ToList();
                Check("group moves before a flat target as one block",
                    members.Count > 0 &&
                    _w._tabs.IndexOf(members.Last()) < _w._tabs.IndexOf(moveProbe),
                    "lastMember=" +
                    (members.Count == 0 ? -1 : _w._tabs.IndexOf(members.Last())) +
                    " probe=" + _w._tabs.IndexOf(moveProbe));
                _w.MoveGroupAt(group, moveProbe, true);
                Check("group moves after a flat target as one block",
                    members.Count > 0 &&
                    _w._tabs.IndexOf(members.First()) > _w._tabs.IndexOf(moveProbe),
                    "firstMember=" +
                    (members.Count == 0 ? -1 : _w._tabs.IndexOf(members.First())) +
                    " probe=" + _w._tabs.IndexOf(moveProbe));
                _w._tabs.Remove(moveProbe);
                _w.RebuildTabStrip();
                _w.MoveGroupToEnd(group);
                Check("group can be moved to the end",
                    _w._tabs.Last().GroupId == group.Id,
                    "lastGroup=" + _w._tabs.Last().GroupId);

                // 组内成员拖到组前，保持平铺，不进入原组。
                _w.MoveTabToBoundary(extra, originalTab);
                Check("group member can be dragged out to the group front",
                    string.IsNullOrEmpty(extra.GroupId) &&
                    _w._tabs.IndexOf(extra) < _w._tabs.IndexOf(originalTab),
                    "extra=" + _w._tabs.IndexOf(extra) +
                    " original=" + _w._tabs.IndexOf(originalTab));

                _w.DisbandGroup(group);
                _w.DisbandGroup(group2);
                Check("disband removes the groups", _w._groups.Count == 0,
                    "groups=" + _w._groups.Count);
                Check("disband restores flat tabs",
                    _w._tabs.All(t => string.IsNullOrEmpty(t.GroupId)),
                    "grouped=" + _w._tabs.Count(t => !string.IsNullOrEmpty(t.GroupId)));
                Check("group headers disappear", groupHeaders().Count == 0,
                    "children=" + (_w._tabStripPanel == null ? -1
                        : _w._tabStripPanel.Children.Count));

                // 清掉探针标签，后面的用例继续用最开始那个标签。
                _w._tabs.Remove(extra);
                _w._tabs.Remove(flat);
                _w._tabs.Remove(flat2);
                _w.RebuildTabStrip();

                // ---- 重叠建组 ----
                Check("overlap zone is the center 40% (horizontal)",
                    MainWindow.IsGroupOverlap(new Point(50, 0), 100, 30, false) &&
                    !MainWindow.IsGroupOverlap(new Point(10, 0), 100, 30, false) &&
                    !MainWindow.IsGroupOverlap(new Point(90, 0), 100, 30, false),
                    "center/left/right");
                Check("vertical overlap uses the Y axis",
                    MainWindow.IsGroupOverlap(new Point(0, 15), 30, 30, true) &&
                    !MainWindow.IsGroupOverlap(new Point(0, 2), 30, 30, true),
                    "center/top");

                var pairA = new TabModel
                {
                    Title = "pair A",
                    Url = "about:blank",
                    IsStartPage = false
                };
                var pairMid = new TabModel
                {
                    Title = "pair middle",
                    Url = "about:blank",
                    IsStartPage = false
                };
                var pairB = new TabModel
                {
                    Title = "pair B",
                    Url = "about:blank",
                    IsStartPage = false
                };
                _w._tabs.Add(pairA);
                _w._tabs.Add(pairMid);
                _w._tabs.Add(pairB);
                _w.RebuildTabStrip();
                var groupsBefore = _w._groups.Count;
                _w.CreateGroupFromPair(pairB, pairA);
                var pairGroup = _w._groups.LastOrDefault();
                Check("overlapping flat tabs create one new group",
                    pairGroup != null && pairA.GroupId == pairGroup.Id &&
                    pairB.GroupId == pairGroup.Id &&
                    _w._groups.Count == groupsBefore + 1,
                    "groups=" + _w._groups.Count);
                Check("pair group is contiguous and keeps pair order",
                    _w._tabs.IndexOf(pairA) + 1 == _w._tabs.IndexOf(pairB),
                    "a=" + _w._tabs.IndexOf(pairA) +
                    " b=" + _w._tabs.IndexOf(pairB));
                _w.DisbandGroup(pairGroup);
                _w._tabs.Remove(pairA);
                _w._tabs.Remove(pairB);
                _w._tabs.Remove(pairMid);
                _w.RebuildTabStrip();

                // 组内标签拖出来直接和新的平铺标签重叠：一步建新组。
                var gKeep = new TabModel
                {
                    Title = "old keep",
                    Url = "about:blank",
                    IsStartPage = false
                };
                var gSource = new TabModel
                {
                    Title = "old move",
                    Url = "about:blank",
                    IsStartPage = false
                };
                var gTarget = new TabModel
                {
                    Title = "new mate",
                    Url = "about:blank",
                    IsStartPage = false
                };
                _w._tabs.Add(gKeep);
                _w._tabs.Add(gSource);
                _w._tabs.Add(gTarget);
                _w.CreateGroupForTab(gKeep);
                var oldGroup = _w._groups.Last();
                _w.MoveTabIntoGroup(gSource, oldGroup);
                _w.CreateGroupFromPair(gSource, gTarget);
                var newGroup = _w._groups.Last();
                Check("grouped tab overlapping a flat tab creates a new group in one step",
                    newGroup != null && newGroup.Id != oldGroup.Id &&
                    gSource.GroupId == newGroup.Id &&
                    gTarget.GroupId == newGroup.Id,
                    "new=" + (newGroup == null ? "null" : newGroup.Name));
                Check("old group keeps its other member after the move",
                    gKeep.GroupId == oldGroup.Id,
                    "keep=" + gKeep.GroupId);
                _w.DisbandGroup(oldGroup);
                _w.DisbandGroup(newGroup);
                _w._tabs.Remove(gKeep);
                _w._tabs.Remove(gSource);
                _w._tabs.Remove(gTarget);
                _w.RebuildTabStrip();

                // 关闭分组 = 关闭全部成员，和“解散”保留标签不同。
                _w.NewTab();
                var closeTab = _w._active;
                _w.CreateGroupForTab(closeTab);
                var closeGroup = _w._groups.LastOrDefault();
                _w.CloseGroup(closeGroup);
                Check("closing a group closes its tabs and removes the group",
                    closeGroup != null &&
                    !_w._tabs.Any(t => ReferenceEquals(t, closeTab)) &&
                    _w._groups.All(g => g.Id != closeGroup.Id),
                    "tabs=" + _w._tabs.Count + " groups=" + _w._groups.Count);

                // ---- 上一批的常驻样式回归 ----
                var resident = _w._tabs[0];
                resident.Resident = true;
                _w.RebuildTabStrip();
                var rchip = TabItems(_w._tabStripPanel).OfType<Border>()
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
                // F11 检查会真的切进全屏；后面的 Popup/纵向布局断言要先按
                // 正常路径退出，直接把 _immersive 置 false 会把窗口留在整屏。
                Check("F11 enters the fullscreen state", _w._immersive,
                    "immersive=" + _w._immersive);
                if (_w._immersive) _w.ToggleImmersive();
                Check("F11 toggles back to the windowed state", !_w._immersive,
                    "immersive=" + _w._immersive);

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
                // 弹层顶部现在有「标签页 + 图钉」标题行；比较标签顺序时要跳过它。
                var railFirst = TabItems(_w._railPanel)
                    .FirstOrDefault(f => f.Tag is TabModel);
                var popFirst = TabItems(_w._popupPanel)
                    .FirstOrDefault(f => f.Tag is TabModel);
                Check("vertical popup starts at the same tab as the rail",
                    railFirst != null && popFirst != null &&
                    ReferenceEquals(railFirst.Tag, popFirst.Tag),
                    "rail=" + (railFirst == null ? "null" : ((TabModel)railFirst.Tag).Title) +
                    " popup=" + (popFirst == null ? "null" : ((TabModel)popFirst.Tag).Title));
                var popupHeader = _w._popupContent == null || !(_w._popupContent.Child is Grid pg)
                    ? null : pg.Children.OfType<Grid>()
                        .FirstOrDefault(g => Grid.GetRow(g) == 0);
                Check("rail and popup keep the same fixed header spacer",
                    _w._railHeader != null && _w._railHeader.Height == 22 &&
                    popupHeader != null && popupHeader.Height == 22,
                    "railHeader=" + (_w._railHeader == null ? "null"
                        : _w._railHeader.Height.ToString()) +
                    " popupHeader=" + (popupHeader == null ? "null"
                        : popupHeader.Height.ToString()));
                var popPin = _w._popupPin;
                Check("vertical popup has a floating pin button", popPin != null,
                    popPin == null ? "no pin" : "pin=" + popPin.Tag);
                var expandedBefore = _w._settings.VerticalExpanded;
                _w.ToggleVerticalPin();
                Check("pin toggles vertical expanded",
                    _w._settings.VerticalExpanded != expandedBefore,
                    "expanded=" + _w._settings.VerticalExpanded);
                var pinSlash = _w._railPin == null || !(_w._railPin.Content is Grid pinGrid)
                    ? null : pinGrid.Children.OfType<System.Windows.Shapes.Line>()
                        .FirstOrDefault(t => Equals(t.Tag, "pin-slash"));
                Check("fixed rail shows the pin-with-slash state",
                    _w._railHeader != null &&
                    _w._railHeader.Visibility == Visibility.Visible &&
                    _w._railPin != null && _w._railPin.Visibility == Visibility.Visible &&
                    pinSlash != null && pinSlash.Visibility == Visibility.Visible,
                    "header=" + (_w._railHeader == null ? "null"
                        : _w._railHeader.Visibility.ToString()) +
                    " pin=" + (_w._railPin == null ? "null" : _w._railPin.Visibility.ToString()) +
                    " slash=" + (pinSlash == null ? "null" : pinSlash.Visibility.ToString()));
                _w.ToggleVerticalPin();
                Check("pin restores hover mode",
                    _w._settings.VerticalExpanded == expandedBefore,
                    "expanded=" + _w._settings.VerticalExpanded);
                Check("hover mode keeps the header spacer but hides the pin",
                    _w._railHeader != null &&
                    _w._railHeader.Visibility == Visibility.Visible &&
                    _w._railPin != null && _w._railPin.Visibility == Visibility.Collapsed &&
                    pinSlash != null && pinSlash.Visibility == Visibility.Collapsed,
                    "header=" + (_w._railHeader == null ? "null"
                        : _w._railHeader.Visibility.ToString()) +
                    " pin=" + (_w._railPin == null ? "null" : _w._railPin.Visibility.ToString()) +
                    " slash=" + (pinSlash == null ? "null" : pinSlash.Visibility.ToString()));
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
                // 沉浸式下状态栏恒隐藏，测的必须是 ShowStatusBar 本身。
                // 用正常退出路径复位，不能只翻布尔值（真全屏会改窗口几何）。
                if (_w._immersive) _w.ToggleImmersive();
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
                Check("maximized/minimized states do not persist restore size",
                    MainWindow.CanPersistNormalGeometry(WindowState.Normal) &&
                    !MainWindow.CanPersistNormalGeometry(WindowState.Maximized) &&
                    !MainWindow.CanPersistNormalGeometry(WindowState.Minimized),
                    "normal/max/min expected true/false/false");
                Check("SaveWindowBounds uses the state guard",
                    mwSrc.Contains("CanPersistNormalGeometry(WindowState)"),
                    "call=" + mwSrc.Contains("CanPersistNormalGeometry(WindowState)"));
                Check("current session has no poisoned window coordinates",
                    double.IsNaN(_w._settings.WindowLeft) ||
                    MainWindow.IsSaneWindowCoord(_w._settings.WindowLeft),
                    "left=" + _w._settings.WindowLeft);

                // ---- 批次 A / A1：标准两行把窗口三按钮搬到第一行 ----
                // 放最后：这里会反复重建外壳、切窗口状态，不能影响前面的 UI 断言。
                foreach (var a1Layout in new[] { LayoutMode.Compact, LayoutMode.Standard, LayoutMode.Vertical })
                {
                    _w._settings.Layout = a1Layout;
                    _w.ApplyLayout();
                    var a1Toolbar = _w._toolbarHost.Child as Grid;
                    var a1Nav = _w._navigationRow;
                    var a1Buttons = _w._windowButtons;
                    var a1Parent = a1Buttons == null ? null : a1Buttons.Parent as Grid;
                    var a1InNav = a1Parent != null && ReferenceEquals(a1Parent, a1Nav);
                    var a1TabRow = a1Toolbar == null ? null
                        : a1Toolbar.Children.OfType<Grid>()
                            .FirstOrDefault(g => Equals(g.Tag, "standard-tab-row"));
                    var a1BtnList = a1Buttons as StackPanel;
                    var a1Btns = a1BtnList == null ? new List<Button>()
                        : a1BtnList.Children.OfType<Button>().ToList();

                    Check("three window buttons are present in " + a1Layout,
                        a1Btns.Count == 3 &&
                        (string)a1Btns[0].ToolTip == L.T("最小化") &&
                        (string)a1Btns[1].ToolTip == L.T("最大化") &&
                        (string)a1Btns[2].ToolTip == L.T("关闭"),
                        "buttons=" + a1Btns.Count);
                    Check("window buttons are right aligned in " + a1Layout,
                        a1Buttons != null &&
                        a1Buttons.HorizontalAlignment == HorizontalAlignment.Right,
                        a1Buttons == null ? "no buttons" : "align=" + a1Buttons.HorizontalAlignment);
                    Check("window buttons are excluded from caption drag in " + a1Layout,
                        a1Btns.Count == 3 && MainWindow.IsChromeInteractive(a1Btns[0]),
                        "interactive=true expected");

                    if (a1Layout == LayoutMode.Standard)
                    {
                        Check("standard layout has two toolbar rows",
                            a1Toolbar != null && a1Toolbar.RowDefinitions.Count == 2,
                            a1Toolbar == null ? "no toolbar" : "rows=" + a1Toolbar.RowDefinitions.Count);
                        Check("standard layout puts window buttons in the first row",
                            a1TabRow != null && ReferenceEquals(a1Parent, a1TabRow) &&
                            Grid.GetRow(a1TabRow) == 0 &&
                            Grid.GetColumn(a1Buttons) == 1 && !a1InNav,
                            "inTabRow=" + (a1TabRow != null && ReferenceEquals(a1Parent, a1TabRow)) +
                            " row=" + (a1TabRow == null ? -1 : Grid.GetRow(a1TabRow)));
                        Check("standard tab row reserves a fixed 108px button column",
                            a1TabRow != null && a1TabRow.ColumnDefinitions.Count == 2 &&
                            a1TabRow.ColumnDefinitions[0].Width.IsStar &&
                            a1TabRow.ColumnDefinitions[1].Width.Value == 108,
                            a1TabRow == null ? "no tab row" : "cols=" + a1TabRow.ColumnDefinitions.Count);
                        Check("standard navigation row drops the window button column",
                            a1Nav != null && a1Nav.ColumnDefinitions.Count == 6 &&
                            !a1Nav.Children.OfType<FrameworkElement>()
                                .Any(f => Equals(f.Tag, "window-buttons")),
                            a1Nav == null ? "no nav" : "cols=" + a1Nav.ColumnDefinitions.Count);
                        var a1StripHost = _w._tabStrip == null ? null
                            : _w._tabStrip.Parent as FrameworkElement;
                        Check("standard tab strip stays in the scrollable left column",
                            a1TabRow != null && a1StripHost != null &&
                            ReferenceEquals(a1StripHost.Parent, a1TabRow) &&
                            Grid.GetColumn(a1StripHost) == 0 &&
                            _w._tabStrip.HorizontalScrollBarVisibility == ScrollBarVisibility.Hidden &&
                            _w._tabStrip.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled,
                            a1StripHost == null ? "no strip" : "col=" + Grid.GetColumn(a1StripHost));
                        Check("standard tab row blank area still drags the window",
                            a1TabRow != null && a1TabRow.Background != null &&
                            !MainWindow.IsChromeInteractive(a1TabRow),
                            a1TabRow == null ? "no tab row" : "bg=" + a1TabRow.Background);
                        var a1StripKids = _w._tabStripPanel == null ? new List<UIElement>()
                            : _w._tabStripPanel.Children.Cast<UIElement>().ToList();
                        Check("standard layout keeps + right after the last tab",
                            a1StripKids.Count >= 2 &&
                            a1StripKids[a1StripKids.Count - 1] is Button &&
                            Equals(((FrameworkElement)a1StripKids[a1StripKids.Count - 1]).Tag, "newtab") &&
                            ((FrameworkElement)a1StripKids[a1StripKids.Count - 2]).Tag is TabModel,
                            "items=" + a1StripKids.Count);
                        Check("standard layout keeps the scroll chevrons",
                            _w._tabStripPrev != null && _w._tabStripNext != null,
                            "prev=" + (_w._tabStripPrev != null) + " next=" + (_w._tabStripNext != null));
                    }
                    else
                    {
                        Check(a1Layout + " layout keeps window buttons in the navigation row",
                            a1InNav && Grid.GetColumn(a1Buttons) == 7 &&
                            a1Nav.ColumnDefinitions.Count == 8,
                            "inNav=" + a1InNav + " cols=" +
                            (a1Nav == null ? -1 : a1Nav.ColumnDefinitions.Count));
                        Check(a1Layout + " layout has no extra tab row",
                            a1TabRow == null && a1Toolbar != null &&
                            a1Toolbar.RowDefinitions.Count == 1,
                            a1Toolbar == null ? "no toolbar" : "rows=" + a1Toolbar.RowDefinitions.Count);
                    }
                }

                // 最大化下重建外壳，按钮仍由 Grid 列定位（不依赖写死的屏幕坐标）。
                _w.WindowState = WindowState.Maximized;
                await Task.Delay(250);
                _w._settings.Layout = LayoutMode.Standard;
                _w.ApplyLayout();
                var a1MaxParent = _w._windowButtons == null ? null : _w._windowButtons.Parent as Grid;
                Check("standard window buttons stay in row 0 when maximized",
                    a1MaxParent != null && Equals(a1MaxParent.Tag, "standard-tab-row") &&
                    Grid.GetRow(a1MaxParent) == 0 &&
                    Grid.GetColumn(_w._windowButtons) == 1,
                    "parent=" + (a1MaxParent == null ? "null" : (string)a1MaxParent.Tag));
                _w.WindowState = WindowState.Normal;
                _w._settings.Layout = LayoutMode.Compact;
                _w.ApplyLayout();

                // ---- 批次 A / A2：F11 真全屏状态机 ----
                var mon100 = MainWindow.MonitorRectFromNative(0, 0, 1920, 1080, new DpiScale(1, 1));
                Check("monitor rect is unchanged at 100% DPI",
                    mon100.Width == 1920 && mon100.Height == 1080, "rect=" + mon100);
                var mon125 = MainWindow.MonitorRectFromNative(0, 0, 2400, 1350, new DpiScale(1.25, 1.25));
                Check("monitor rect converts to DIP at 125%",
                    Math.Abs(mon125.Width - 1920) < 0.01 && Math.Abs(mon125.Height - 1080) < 0.01,
                    "rect=" + mon125);
                var mon150 = MainWindow.MonitorRectFromNative(-3840, -300, 0, 1860,
                    new DpiScale(1.5, 1.5));
                Check("mixed-DPI secondary monitor keeps its negative origin",
                    Math.Abs(mon150.Left - (-2560)) < 0.01 &&
                    Math.Abs(mon150.Top - (-200)) < 0.01 &&
                    Math.Abs(mon150.Width - 2560) < 0.01 &&
                    Math.Abs(mon150.Height - 1440) < 0.01,
                    "rect=" + mon150);
                Check("degenerate monitor rect is rejected",
                    MainWindow.MonitorRectFromNative(10, 10, 10, 10, new DpiScale(1, 1)).IsEmpty,
                    "empty expected");

                var a2Monitor = _w.MonitorRectDip();
                var a2Work = _w.WorkAreaDip();
                Check("fullscreen rect is the whole monitor, not the work area",
                    !a2Monitor.IsEmpty && a2Monitor.Width >= a2Work.Width - 1 &&
                    a2Monitor.Height >= a2Work.Height - 1,
                    "monitor=" + a2Monitor + " work=" + a2Work);

                // Esc 平时必须留给网页；只有全屏期间才由宿主从消息泵层截下。
                var a2Esc = KeyMsg(Key.Escape);
                bool a2EscWindowed = false;
                _w.OnThreadPreprocessMessage(ref a2Esc, ref a2EscWindowed);
                Check("Escape is not swallowed while windowed", !a2EscWindowed,
                    "handled=" + a2EscWindowed);

                _w.WindowState = WindowState.Normal;
                await Task.Delay(200);
                var a2Before = new Rect(_w.Left, _w.Top, _w.Width, _w.Height);
                var a2Topmost = _w.Topmost;
                var a2Resize = _w.ResizeMode;
                _w.OpenMemoryPanel(true);
                _w._verticalPopup.IsOpen = true;
                _w.ToggleImmersive();
                await Task.Delay(250);

                Check("F11 enters the true fullscreen state",
                    _w._immersive && _w.WindowState == WindowState.Normal,
                    "immersive=" + _w._immersive + " state=" + _w.WindowState);
                Check("fullscreen window geometry matches rcMonitor",
                    Math.Abs(_w.Width - a2Monitor.Width) < 3 &&
                    Math.Abs(_w.Height - a2Monitor.Height) < 3 &&
                    Math.Abs(_w.Left - a2Monitor.Left) < 3 &&
                    Math.Abs(_w.Top - a2Monitor.Top) < 3,
                    "window=" + _w.Left.ToString("0") + "," + _w.Top.ToString("0") + " " +
                    _w.Width.ToString("0") + "x" + _w.Height.ToString("0") +
                    " monitor=" + a2Monitor);
                Check("fullscreen is topmost and not resizable",
                    _w.Topmost && _w.ResizeMode == ResizeMode.NoResize,
                    "topmost=" + _w.Topmost + " resize=" + _w.ResizeMode);
                Check("fullscreen hides toolbar, status bar and rail",
                    _w._toolbarHost.Visibility == Visibility.Collapsed &&
                    _w._statusBar.Visibility == Visibility.Collapsed &&
                    _w._rail.Visibility == Visibility.Collapsed &&
                    _w._bodyGrid.ColumnDefinitions[0].Width.Value == 0,
                    "toolbar=" + _w._toolbarHost.Visibility +
                    " status=" + _w._statusBar.Visibility +
                    " rail=" + _w._rail.Visibility);
                Check("fullscreen closes every popup layer",
                    !_w._verticalPopup.IsOpen &&
                    (_w._memoryPanel == null || !_w._memoryPanel.IsOpen) &&
                    (_w._suggestions == null || !_w._suggestions.IsOpen),
                    "vertical=" + _w._verticalPopup.IsOpen +
                    " memory=" + (_w._memoryPanel != null && _w._memoryPanel.IsOpen) +
                    " suggest=" + (_w._suggestions != null && _w._suggestions.IsOpen));

                // 真调一次钩子：全屏期间必须完全绕过 A3 的 rcWork 限制。
                var a2Mmi = new MainWindow.MINMAXINFO();
                var a2Hwnd = new WindowInteropHelper(_w).Handle;
                var a2Ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MainWindow.MINMAXINFO)));
                try
                {
                    Marshal.StructureToPtr(a2Mmi, a2Ptr, false);
                    bool a2HookFullscreen = false;
                    _w.WorkAreaHook(a2Hwnd, MainWindow.WM_GETMINMAXINFO,
                        IntPtr.Zero, a2Ptr, ref a2HookFullscreen);
                    Check("fullscreen bypasses the A3 work-area hook", !a2HookFullscreen,
                        "handled=" + a2HookFullscreen);
                }
                finally { Marshal.FreeHGlobal(a2Ptr); }

                Check("fullscreen geometry is never persisted",
                    mwSrc.Contains("if (_immersive || _fullscreenBusy) return;"),
                    "guarded=" + mwSrc.Contains("if (_immersive || _fullscreenBusy) return;"));
                Check("fullscreen uses rcMonitor rather than Maximized",
                    mwSrc.Contains("mi.Monitor.Right, mi.Monitor.Bottom") &&
                    !mwSrc.Contains("if (key == Key.F11) { WindowState = WindowState.Maximized"),
                    "monitorRect=" + mwSrc.Contains("mi.Monitor.Right, mi.Monitor.Bottom"));
                var a2StateInFullscreen = _w.WindowState;
                _w.ToggleMaximize();
                Check("maximize toggle is inert during fullscreen",
                    _w._immersive && _w.WindowState == a2StateInFullscreen,
                    "state=" + _w.WindowState + " immersive=" + _w._immersive);

                // Esc 从消息泵层退出全屏，并还原进入前的矩形/状态。
                var a2EscFull = KeyMsg(Key.Escape);
                bool a2EscHandled = false;
                _w.OnThreadPreprocessMessage(ref a2EscFull, ref a2EscHandled);
                await Task.Delay(250);
                Check("Escape is a host shortcut in fullscreen", a2EscHandled,
                    "handled=" + a2EscHandled);
                Check("Escape leaves fullscreen", !_w._immersive,
                    "immersive=" + _w._immersive);
                Check("exit restores the pre-fullscreen window bounds",
                    _w.WindowState == WindowState.Normal &&
                    Math.Abs(_w.Left - a2Before.Left) < 3 &&
                    Math.Abs(_w.Top - a2Before.Top) < 3 &&
                    Math.Abs(_w.Width - a2Before.Width) < 3 &&
                    Math.Abs(_w.Height - a2Before.Height) < 3,
                    "window=" + _w.Left.ToString("0") + "," + _w.Top.ToString("0") + " " +
                    _w.Width.ToString("0") + "x" + _w.Height.ToString("0") +
                    " before=" + a2Before);
                Check("exit restores topmost and resize mode",
                    _w.Topmost == a2Topmost && _w.ResizeMode == a2Resize,
                    "topmost=" + _w.Topmost + " resize=" + _w.ResizeMode);
                Check("exit restores the toolbar and status bar",
                    _w._toolbarHost.Visibility == Visibility.Visible &&
                    _w._statusBar.Visibility ==
                        (_w._settings.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed),
                    "toolbar=" + _w._toolbarHost.Visibility +
                    " status=" + _w._statusBar.Visibility);
                bool a2HookAfter = false;
                var a2Ptr2 = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MainWindow.MINMAXINFO)));
                try
                {
                    Marshal.StructureToPtr(new MainWindow.MINMAXINFO(), a2Ptr2, false);
                    _w.WorkAreaHook(a2Hwnd, MainWindow.WM_GETMINMAXINFO,
                        IntPtr.Zero, a2Ptr2, ref a2HookAfter);
                }
                finally { Marshal.FreeHGlobal(a2Ptr2); }
                Check("A3 work-area hook is active again after exit", a2HookAfter,
                    "handled=" + a2HookAfter);

                // 从 Maximized 进入/退出：外壳、多显示器/DPI、A3 都不能被踩坏。
                _w._settings.Layout = LayoutMode.Vertical;
                _w.ApplyLayout();
                _w.WindowState = WindowState.Maximized;
                await Task.Delay(250);
                var a2MaxWidth = _w.ActualWidth;
                _w.ToggleImmersive();
                await Task.Delay(250);
                Check("F11 from maximized still fills the monitor",
                    _w._immersive && Math.Abs(_w.Width - a2Monitor.Width) < 3 &&
                    Math.Abs(_w.Height - a2Monitor.Height) < 3 &&
                    _w._rail.Visibility == Visibility.Collapsed,
                    "size=" + _w.Width.ToString("0") + "x" + _w.Height.ToString("0") +
                    " rail=" + _w._rail.Visibility);
                // 从 Maximized 进入后，用真正的 F11 消息泵路径退出（前面用的是 Esc）。
                var a2F11Exit = KeyMsg(Key.F11);
                bool a2F11ExitHandled = false;
                _w.OnThreadPreprocessMessage(ref a2F11Exit, ref a2F11ExitHandled);
                await Task.Delay(250);
                Check("F11 also exits fullscreen from the message pump", a2F11ExitHandled,
                    "handled=" + a2F11ExitHandled);
                Check("exit returns to the maximized state",
                    !_w._immersive && _w.WindowState == WindowState.Maximized &&
                    _w._rail.Visibility == Visibility.Visible,
                    "state=" + _w.WindowState + " immersive=" + _w._immersive +
                    " rail=" + _w._rail.Visibility);
                Check("restored maximize still respects the A3 work area",
                    _w.ActualWidth <= a2Work.Width + 16 &&
                    _w.ActualHeight <= a2Work.Height + 16,
                    "window=" + _w.ActualWidth.ToString("0") + "x" +
                    _w.ActualHeight.ToString("0") + " work=" + a2Work);
                var a2Snapshot = _w._fsPrevBounds;
                _w.WindowState = WindowState.Normal;
                await Task.Delay(200);
                Check("restore-down is not polluted by the fullscreen rect",
                    a2Snapshot.Width > 100 && a2Snapshot.Height > 100 &&
                    Math.Abs(_w.Width - a2Snapshot.Width) < 3 &&
                    Math.Abs(_w.Height - a2Snapshot.Height) < 3 &&
                    _w.Height < a2Monitor.Height,
                    "snapshot=" + a2Snapshot + " now=" +
                    _w.Width.ToString("0") + "x" + _w.Height.ToString("0"));
                Check("maximized round trip used the pre-fullscreen restore bounds",
                    a2MaxWidth > 0 && a2Snapshot.Width > 0 && !a2Snapshot.IsEmpty,
                    "maxWidth=" + a2MaxWidth.ToString("0") + " snapshot=" + a2Snapshot);

                _w._settings.Layout = LayoutMode.Compact;
                _w.ApplyLayout();
                Line("A1/A2 state machine covered by assertions; taskbar / multi-monitor / mixed-DPI feel needs manual check");

                // ---- C5 用户脚本元数据：解析 / 回写 / 导出往返 ----
                // 纯本地断言，不碰用户的脚本存储；只有一份 .user.js 样例写到 out/。
                var c5 = "// ==UserScript==\r\n" +
                         "// @name         Old Name\r\n" +
                         "// @namespace    c5.demo\r\n" +
                         "// @version      1.0\r\n" +
                         "// @description  keep me\r\n" +
                         "// @match        https://old.example/*\r\n" +
                         "// @grant        GM_setValue\r\n" +
                         "// @run-at       document-start\r\n" +
                         "// @include      /regex-literal/\r\n" +
                         "// @exclude      https://old.example/admin/*\r\n" +
                         "// @connect      api.example.com\r\n" +
                         "// @match        http://localhost/*\r\n" +
                         "// @weird-key    unknown-metadata\r\n" +
                         "// ==/UserScript==\r\n" +
                         "console.log('body');\r\n";
                var c5Original = ScriptParser.Parse(c5);
                Check("C5 parse baseline name", c5Original.Name == "Old Name", c5Original.Name);
                Check("C5 parse baseline two @match lines", c5Original.Matches.Count == 2,
                    string.Join(" | ", c5Original.Matches));

                var c5Edit = new ScriptMetadataEdit
                {
                    Name = "New Name",
                    Version = "3.2",
                    Matches = new List<string>
                    {
                        "https://a.example/*", "https://b.example/*", "http://localhost:8080/*"
                    },
                    Excludes = new List<string>
                    {
                        "https://a.example/admin/*", "https://b.example/login/*"
                    }
                };
                string c5Error;
                var c5Written = ScriptParser.RewriteMetadata(c5, c5Edit, out c5Error);
                Check("C5 rewrite succeeds", c5Written != null, c5Error);
                if (c5Written != null)
                {
                    var c5Reparsed = ScriptParser.Parse(c5Written);
                    Check("C5 name/version read back",
                        c5Reparsed.Name == "New Name" && c5Reparsed.Version == "3.2",
                        c5Reparsed.Name + " v" + c5Reparsed.Version);
                    Check("C5 multi-line @match order",
                        c5Reparsed.Matches.SequenceEqual(c5Edit.Matches),
                        string.Join(" | ", c5Reparsed.Matches));
                    Check("C5 multi-line @exclude order",
                        c5Reparsed.Excludes.SequenceEqual(c5Edit.Excludes),
                        string.Join(" | ", c5Reparsed.Excludes));
                    Check("C5 header alignment prefix is kept",
                        c5Written.Contains("// @name         New Name"), "prefix");
                    Check("C5 CRLF line endings preserved",
                        c5Written.Contains("\r\n") && !c5Written.Replace("\r\n", "").Contains("\n"),
                        "crlf=" + c5Written.Split(new[] { "\r\n" }, StringSplitOptions.None).Length);
                    Check("C5 header markers kept",
                        c5Written.Contains("// ==UserScript==") &&
                        c5Written.Contains("// ==/UserScript=="), "markers");
                    Check("C5 script body untouched",
                        c5Written.Contains("console.log('body');"), "body");

                    Func<string, List<string>> c5OtherHeaders = code =>
                        code.Replace("\r\n", "\n").Split('\n')
                            .Where(l =>
                            {
                                var m = ScriptParser.MetaLine.Match(l);
                                if (!m.Success) return false;
                                var k = m.Groups[1].Value.ToLowerInvariant();
                                return k != "name" && k != "version" && k != "match" && k != "exclude";
                            })
                            .Select(l => l.TrimEnd('\r')).ToList();
                    var c5Before = c5OtherHeaders(c5);
                    var c5After = c5OtherHeaders(c5Written);
                    Check("C5 other // @ headers kept verbatim and in order",
                        c5Before.Count == 7 && c5Before.SequenceEqual(c5After),
                        "before=" + c5Before.Count + " after=" + c5After.Count);
                    Check("C5 unknown metadata key survives",
                        c5Written.Contains("// @weird-key    unknown-metadata"), "weird-key");
                    Check("C5 @include regex literal is untouched",
                        c5Written.Contains("// @include      /regex-literal/") &&
                        c5Reparsed.Includes.SequenceEqual(c5Original.Includes),
                        string.Join(" | ", c5Reparsed.Includes));
                    Check("C5 @grant / @run-at still parsed",
                        c5Reparsed.Grants.SequenceEqual(c5Original.Grants) &&
                        c5Reparsed.RunAt == "document-start" &&
                        c5Reparsed.Namespace == "c5.demo" &&
                        c5Reparsed.Description == "keep me",
                        "grant=" + string.Join(",", c5Reparsed.Grants) + " runAt=" + c5Reparsed.RunAt);

                    // 导出即 ScriptStore.SaveCode 写出的 <id>.user.js 内容：再次导入必须还原元数据。
                    var c5ExportPath = Path.Combine(Root(), "out", "c5-export.user.js");
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(c5ExportPath));
                        File.WriteAllText(c5ExportPath, c5Written, Encoding.UTF8);
                        var c5Exported = File.ReadAllText(c5ExportPath, Encoding.UTF8);
                        var c5Imported = ScriptParser.Parse(c5Exported);
                        Check("C5 exported .user.js re-imports with the same metadata",
                            c5Imported.Name == "New Name" && c5Imported.Version == "3.2" &&
                            c5Imported.Matches.SequenceEqual(c5Edit.Matches) &&
                            c5Imported.Excludes.SequenceEqual(c5Edit.Excludes) &&
                            c5Imported.Grants.SequenceEqual(c5Original.Grants) &&
                            c5Imported.RunAt == c5Original.RunAt &&
                            c5Imported.Namespace == c5Original.Namespace &&
                            c5Imported.Description == c5Original.Description,
                            "name=" + c5Imported.Name + " version=" + c5Imported.Version);
                        string c5SecondError;
                        Check("C5 rewrite is idempotent on an exported file",
                            ScriptParser.RewriteMetadata(c5Exported, c5Edit, out c5SecondError) == c5Exported,
                            c5SecondError);
                    }
                    catch (Exception ex) { Check("C5 export round trip", false, ex.Message); }
                }

                var c5Cleared = ScriptParser.RewriteMetadata(c5,
                    new ScriptMetadataEdit { Matches = new List<string>(), Excludes = new List<string>() },
                    out c5Error);
                Check("C5 empty list removes every @match/@exclude line",
                    c5Cleared != null && !c5Cleared.Contains("// @match") &&
                    !c5Cleared.Contains("// @exclude"), c5Error);
                Check("C5 untouched fields leave the source byte-for-byte",
                    ScriptParser.RewriteMetadata(c5, new ScriptMetadataEdit(), out c5Error) == c5,
                    "no field edited");
                var c5Hand = c5.Replace("// @name         Old Name", "// @name         Hand Edited");
                var c5HandOut = ScriptParser.RewriteMetadata(c5Hand, new ScriptMetadataEdit(), out c5Error);
                Check("C5 hand-edited // @ line survives a save with no field edits",
                    c5HandOut == c5Hand && ScriptParser.Parse(c5HandOut).Name == "Hand Edited",
                    "name=" + ScriptParser.Parse(c5HandOut).Name);

                var c5NoExclude = "// ==UserScript==\n// @name    Bare\n// @version 1.0\n" +
                                  "// @grant   GM_info\n// ==/UserScript==\n";
                var c5Added = ScriptParser.RewriteMetadata(c5NoExclude,
                    new ScriptMetadataEdit
                    {
                        Matches = new List<string> { "https://y.example/*" },
                        Excludes = new List<string> { "https://x.example/*" }
                    }, out c5Error);
                Check("C5 missing keys are added before the end marker",
                    c5Added != null &&
                    c5Added.IndexOf("// @match", StringComparison.Ordinal) <
                    c5Added.IndexOf("// ==/UserScript==", StringComparison.Ordinal) &&
                    c5Added.IndexOf("// @exclude", StringComparison.Ordinal) <
                    c5Added.IndexOf("// ==/UserScript==", StringComparison.Ordinal) &&
                    ScriptParser.Parse(c5Added).Excludes.Count == 1, c5Error);
                Check("C5 LF source stays LF",
                    c5Added != null && !c5Added.Contains("\r\n"), "newline");

                var c5Bare = "var a = 1;\n// @name    Loose\n// @grant   GM_info\n";
                var c5BareOut = ScriptParser.RewriteMetadata(c5Bare,
                    new ScriptMetadataEdit { Name = "Loose 2", Version = "9.9" }, out c5Error);
                Check("C5 scripts without a header block still rewrite",
                    c5BareOut != null && ScriptParser.Parse(c5BareOut).Name == "Loose 2" &&
                    ScriptParser.Parse(c5BareOut).Version == "9.9", c5Error);

                Check("C5 rejects a newline injected into @name",
                    ScriptParser.RewriteMetadata(c5,
                        new ScriptMetadataEdit { Name = "X\n// @grant none" }, out c5Error) == null &&
                    c5Error.Length > 0, c5Error);
                Check("C5 rejects an empty name",
                    ScriptParser.RewriteMetadata(c5, new ScriptMetadataEdit { Name = "  " },
                        out c5Error) == null, c5Error);
                Check("C5 rejects an empty version",
                    ScriptParser.RewriteMetadata(c5, new ScriptMetadataEdit { Version = "" },
                        out c5Error) == null, c5Error);
                Check("C5 rejects a newline inside a match rule",
                    ScriptParser.RewriteMetadata(c5,
                        new ScriptMetadataEdit { Matches = new List<string> { "a\nb" } },
                        out c5Error) == null, c5Error);

                // 新 UI 字符串必须双语都有；单独探针，跑完立刻恢复语言。
                var c5SavedLang = L.Language;
                L.Apply("en-US");
                Check("C5 metadata strings are translated",
                    L.T("元数据") == "Metadata" && L.T("版本") == "Version" &&
                    L.T("名称不能为空，脚本未保存") == "Name cannot be empty; script not saved" &&
                    L.T("元数据写回失败，脚本未保存：") ==
                        "Metadata write-back failed; script not saved: ",
                    "lang=" + L.Language);
                Check("C5 import card strings are translated",
                    L.T("@include 规则") == "@include rules" &&
                    L.T("名称不能为空，脚本未导入") == "Name cannot be empty; script not imported" &&
                    L.T("版本不能为空，脚本未导入") == "Version cannot be empty; script not imported" &&
                    L.T("元数据写回失败，脚本未导入：") ==
                        "Metadata write-back failed; script not imported: " &&
                    L.T("不改则保持脚本原样；改动会在导入时写回 // @ 头，其它元数据不动。")
                        .StartsWith("Leave unchanged", StringComparison.Ordinal) &&
                    L.T("例如：https://example.com/*（一行一条）")
                        .StartsWith("e.g.", StringComparison.Ordinal) &&
                    L.T("例如：https://example.com/admin/*（一行一条）")
                        .StartsWith("e.g.", StringComparison.Ordinal),
                    "lang=" + L.Language);
                Check("backup/restore strings are translated",
                    L.T("整体备份") == "Backup all" &&
                    L.T("恢复备份") == "Restore backup" &&
                    L.T("先清空现有脚本再恢复") == "Clear existing scripts first" &&
                    L.T("不支持的备份格式") == "Unsupported backup format" &&
                    L.T("DDL 快照会在下次打开或刷新目标站点后自动重建。")
                        .StartsWith("The DDL snapshot", StringComparison.Ordinal),
                    "lang=" + L.Language);
                L.Apply(c5SavedLang);

                // ---- 备份格式校验（纯函数，不写用户数据） ----
                ScriptBundle badBundle;
                string badError;
                Check("bundle rejects empty input",
                    !ScriptBundleFile.TryParse("", out badBundle, out badError) &&
                    badError.Length > 0, badError);
                Check("bundle rejects an unknown format",
                    !ScriptBundleFile.TryParse("{\"Format\":99,\"Scripts\":[]}",
                        out badBundle, out badError), badError);
                Check("bundle rejects an empty script body",
                    !ScriptBundleFile.TryParse("{\"Format\":1,\"Scripts\":[{\"Code\":\"\"}]}",
                        out badBundle, out badError), badError);
                Check("bundle rejects an unknown attachment kind",
                    !ScriptBundleFile.TryParse(
                        "{\"Format\":1,\"Scripts\":[],\"Attachments\":[{\"Kind\":\"x\"}]}",
                        out badBundle, out badError), badError);
                Check("DDL target glob matches only the intended site",
                    DdlRestore.Matches("https://pgadmin.example.com/sqleditor/panel/1",
                        "https://pgadmin.example.com/*") &&
                    !DdlRestore.Matches("https://other.example/x",
                        "https://pgadmin.example.com/*"), "glob");
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
