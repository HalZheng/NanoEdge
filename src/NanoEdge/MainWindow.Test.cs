using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
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
    }
}
