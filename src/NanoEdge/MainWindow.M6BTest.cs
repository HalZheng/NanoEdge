using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NanoEdge
{
    public sealed partial class MainWindow
    {
        // --m6btest：双语 + 设置页验收。
        // 走真实的 MainWindow.OnSettingChanged 路径（改设置 → 应用 → 重建视图），
        // 不是只测字典；语言切换后扫描可视树，确认没有漏翻的中文。
        private sealed class M6BSelfTest
        {
            private static readonly Regex Cjk = new Regex("[\u4e00-\u9fff]");
            private static readonly string[] AllowedCjk = { "中文" };

            private readonly MainWindow _w;
            private readonly StringBuilder _report = new StringBuilder();
            private readonly List<string> _failures = new List<string>();
            private int _passes;
            private string _root;
            private string _temp;

            public M6BSelfTest(MainWindow window) { _w = window; }

            public async Task RunAsync()
            {
                _root = FindRoot();
                Line("M6-b i18n / settings self-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                try
                {
                    Setup();
                    await RunCoreAsync();
                }
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

            private void Setup()
            {
                _temp = Path.Combine(Path.GetTempPath(), "nanoedge-m6b");
                if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
                Directory.CreateDirectory(_temp);

                // 设置文件改用临时路径，避免把用户的 settings.json 改掉。
                SettingsStore.FilePath = Path.Combine(_temp, "settings.json");
                SettingsStore.Persist = true;

                _w._settings.Language = "zh-CN";
                _w._settings.Theme = "dark";
                _w._settings.Layout = LayoutMode.Compact;
                _w._settings.Memory.ApplyPreset(MemoryPreset.Balanced);
                L.Apply("zh-CN");
                Theme.Apply("dark");
                _w.ApplyLayout();
            }

            private async Task RunCoreAsync()
            {
                // ---------- 1. 路由 ----------
                Check("route nano://settings",
                    InternalPages.Normalize("nano://settings") == InternalPages.Settings,
                    InternalPages.Normalize("nano://settings"));
                Check("settings title (zh)", InternalPages.TitleOf("nano://settings") == "设置",
                    InternalPages.TitleOf("nano://settings"));
                Check("create returns SettingsPage",
                    InternalPages.Create("nano://settings", _w._settings, (u, n) => { }, m => { })
                    is SettingsPage, "type=SettingsPage");

                // ---------- 2. 中文设置页 ----------
                var zhPage = new SettingsPage(_w._settings);
                RenderPage(zhPage, Path.Combine(_root, "out", "m6b-settings-zh.png"), 980, 1000);
                var zhTexts = CollectTexts(zhPage);
                Check("zh page renders the 设置 title", zhTexts.Contains("设置"), "texts=" + zhTexts.Count);
                Check("zh page has 外观 / 布局 / 内存 sections",
                    zhTexts.Contains("外观") && zhTexts.Contains("布局") && zhTexts.Contains("内存"),
                    "sections ok");
                Check("zh page shows the memory numbers",
                    zhTexts.Any(t => t != null && t.Contains("700MB")), "700MB");
                Check("zh page has the secret tab hotkey row",
                    zhTexts.Contains("切换密封标签快捷键"), "row ok");
                Check("zh page explains secret tabs are not kept after restart",
                    zhTexts.Any(t => t != null && t.Contains("重启后")), "note ok");
                Check("zh page says allowing notifications does not guarantee a popup",
                    zhTexts.Any(t => t != null && t.Contains("注册过通知身份")), "note ok");
                Check("zh settings page rendered",
                    File.Exists(Path.Combine(_root, "out", "m6b-settings-zh.png")), "out/m6b-settings-zh.png");

                // ---------- 3. 英文设置页 ----------
                L.Apply("en-US");
                var enPage = new SettingsPage(_w._settings);
                RenderPage(enPage, Path.Combine(_root, "out", "m6b-settings-en.png"), 980, 1000);
                var enTexts = CollectTexts(enPage);
                Check("en page renders the Settings title", enTexts.Contains("Settings"), "texts=" + enTexts.Count);
                Check("en page has Appearance / Layout / Memory sections",
                    enTexts.Contains("Appearance") && enTexts.Contains("Layout") && enTexts.Contains("Memory"),
                    "sections ok");
                Check("en page has the secret tab hotkey row",
                    enTexts.Contains("Secret tab switch shortcut"), "row ok");
                Check("en page explains secret tabs are not kept after restart",
                    enTexts.Any(t => t != null && t.Contains("restart")), "note ok");
                Check("en page says allowing notifications does not guarantee a popup",
                    enTexts.Any(t => t != null && t.Contains("registered as a notification sender")), "note ok");
                var leaked = LeakedChinese(enTexts);
                Check("en settings page has no untranslated Chinese", leaked.Count == 0,
                    leaked.Count == 0 ? "none" : string.Join(" | ", leaked.Take(6)));
                Check("en settings page rendered",
                    File.Exists(Path.Combine(_root, "out", "m6b-settings-en.png")), "out/m6b-settings-en.png");

                // ---------- 4. 运行时切换语言（真实路径） ----------
                L.Apply("zh-CN");
                _w.OpenSettings();
                var tab = _w._active;
                Check("menu opens nano://settings", tab != null && tab.InternalPage == "settings",
                    tab == null ? "no tab" : tab.Url);
                var viewBefore = _w._browser.ViewOf(tab);

                _w._settings.Language = "en-US";
                _w.OnSettingChanged("language");
                await Task.Delay(200);
                _w.UpdateLayout();

                Check("language switched to English", L.IsEnglish, L.Language);
                Check("settings tab title is rebuilt in English", tab.Title == "Settings", tab.Title);
                var viewAfter = _w._browser.ViewOf(tab);
                Check("settings view was rebuilt on language change",
                    viewBefore != null && viewAfter != null && !ReferenceEquals(viewBefore, viewAfter),
                    "rebuilt=" + !ReferenceEquals(viewBefore, viewAfter));

                var afterTexts = CollectTexts(viewAfter as DependencyObject);
                var afterLeak = LeakedChinese(afterTexts);
                Check("rebuilt settings page is fully English", afterLeak.Count == 0,
                    afterLeak.Count == 0 ? "none" : string.Join(" | ", afterLeak.Take(6)));
                Check("status bar is English", !Cjk.IsMatch(_w._statusText.Text), _w._statusText.Text);

                // ---------- 5. 主题 / 布局 / 内存档位 ----------
                _w._settings.Theme = "light";
                _w.OnSettingChanged("theme");
                await Task.Delay(100);
                Check("theme change applies immediately", !Theme.IsDark, "IsDark=" + Theme.IsDark);

                _w._settings.Layout = LayoutMode.Standard;
                _w.OnSettingChanged("layout");
                await Task.Delay(100);
                Check("layout change applies immediately",
                    _w._settings.Layout == LayoutMode.Standard && _w._statusText.Text.Contains("Standard"),
                    _w._statusText.Text);

                _w._settings.Memory.ApplyPreset(MemoryPreset.Aggressive);
                _w.OnSettingChanged("memory");
                Check("memory preset applies to the governor settings",
                    _w._settings.Memory.Preset == MemoryPreset.Aggressive &&
                    _w._settings.Memory.PressureLimitMB == 450,
                    "limit=" + _w._settings.Memory.PressureLimitMB);

                // ---------- 6. 落盘 ----------
                Check("settings.json written to the temp store", File.Exists(SettingsStore.FilePath),
                    SettingsStore.FilePath);
                var reloaded = SettingsStore.Load();
                Check("language persisted", reloaded.Language == "en-US", reloaded.Language);
                Check("theme persisted", reloaded.Theme == "light", reloaded.Theme);
                Check("memory preset persisted", reloaded.Memory.Preset == MemoryPreset.Aggressive,
                    reloaded.Memory.Preset.ToString());

                // ---------- 7. 截图 ----------
                _w._settings.Theme = "dark";
                _w.OnSettingChanged("theme");
                _w.WindowState = WindowState.Normal;
                _w.Width = 1100;
                _w.Height = 780;
                await Task.Delay(400);
                _w.UpdateLayout();
                _w.CaptureScreenshot(Path.Combine(_root, "out", "m6b-window-en.png"));
                Check("english window screenshot written",
                    File.Exists(Path.Combine(_root, "out", "m6b-window-en.png")), "out/m6b-window-en.png");

                // ---------- 8. 切回中文 ----------
                _w._settings.Language = "zh-CN";
                _w.OnSettingChanged("language");
                await Task.Delay(200);
                _w.UpdateLayout();
                Check("switch back to Chinese restores the title", tab.Title == "设置", tab.Title);
                Check("status bar is Chinese again", Cjk.IsMatch(_w._statusText.Text), _w._statusText.Text);
                var backTexts = CollectTexts(_w._browser.ViewOf(tab) as DependencyObject);
                Check("settings page is Chinese again", backTexts.Contains("设置"), "count=" + backTexts.Count);

                Line("");
                Line("settings file = " + SettingsStore.FilePath);
                Line("zh texts = " + zhTexts.Count + ", en texts = " + enTexts.Count);
            }

            // ---------- 文本采集 ----------

            private static List<string> CollectTexts(DependencyObject root)
            {
                var texts = new List<string>();
                if (root == null) return texts;
                Walk(root, texts);
                return texts;
            }

            private static void Walk(DependencyObject node, List<string> texts)
            {
                var count = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);
                    var block = child as TextBlock;
                    if (block != null && !string.IsNullOrEmpty(block.Text)) texts.Add(block.Text);

                    var control = child as ContentControl;
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
                    if (text.Contains(":\\")) continue;   // 路径里可能有中文用户名
                    if (Cjk.IsMatch(text)) leaks.Add(text);
                }
                return leaks;
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
                Diagnostics.Log("[m6btest] " + text);
            }

            private void WriteReport()
            {
                try
                {
                    var path = Path.Combine(_root ?? Path.GetTempPath(), "out", "m6b-report.txt");
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
