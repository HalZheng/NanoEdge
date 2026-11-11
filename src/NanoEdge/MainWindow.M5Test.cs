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

            // 通知探针：宿主观测点。只记录，不改 WebView2 的行为。
            private readonly List<string> _hostTitles = new List<string>();
            private readonly List<bool> _hostHandled = new List<bool>();
            private readonly object _hostLock = new object();
            private int _permRequests;
            private int _permSaves;

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
                _w._settings.SecretHotkey = Hotkey.Default;

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
                ProbeCore(secret);
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

                // ---------- 4. 通知默认关闭：不看返回值，看页面能不能真的弹出 ----------
                var denied = await WaitPermissionAsync(secret, 12000);
                Check("notifications are denied when the switch is off", denied == "denied", denied);

                // 规范写的是「permission=denied 时 new Notification() 抛 TypeError」，
                // 但 Chromium 实际不抛：构造会成功、只是永远不展示。所以判据不是抛不抛，
                // 而是「页面 onshow 有没有到 + 宿主有没有收到展示事件」。
                await EvalStringAsync(secret, "__m5.shown=false");
                var deniedCtor = await EvalStringAsync(secret, "window.__m5.notify('nanoedge m5 denied')");
                var deniedShown = await WaitPageFlagAsync(secret, "__m5.shown", 2500);
                Check("nothing is displayed when the switch is off",
                    !deniedShown && HostCount() == 0,
                    "ctor=" + deniedCtor + " shown=" + deniedShown + " hostEvents=" + HostCount() +
                    " err=" + await EvalStringAsync(secret, "String(__m5.err)"));

                // 控制组：普通标签导航会进历史
                var normalUrl = "http://127.0.0.1:" + _portDenied + "/page?normal=1";
                _w.NewTab(normalUrl);
                var normal = _w._active;
                await WaitReadyAsync(normal, normalUrl, 20000);
                ProbeCore(normal);   // 普通标签的核心也要挂探针，否则「没被误伤」无从证明
                Check("normal navigation is recorded in history", HistoryStore.All().Count == 1,
                    "entries=" + HistoryStore.All().Count);

                // ---------- 5. 同一标签的密封 / 解封 toggle ----------
                _w.SelectTab(normal);
                EnsurePlain(normal);
                var tabsAtToggle = _w._tabs.Count;
                var realTitle = normal.DisplayTitle;
                var realUrl = normal.Url;
                _w.HandleShortcut(System.Windows.Input.Key.OemTilde,
                    System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.None);
                Check("hotkey seals the current tab in place",
                    normal.IsSecret && ReferenceEquals(_w._active, normal) &&
                    _w._tabs.Count == tabsAtToggle,
                    "secret=" + normal.IsSecret + " tabs=" + _w._tabs.Count);
                Check("sealed tab is disguised as 文档 and hides its url",
                    normal.DisplayTitle == "文档" && _w._address.Text == "",
                    "title=" + normal.DisplayTitle + " address='" + _w._address.Text + "'");
                Check("sealing clears the site favicon", normal.Favicon == null,
                    normal.Favicon == null ? "null" : "present");

                _w.HandleShortcut(System.Windows.Input.Key.OemTilde,
                    System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.None);
                Check("second press unseals the same tab",
                    !normal.IsSecret && ReferenceEquals(_w._active, normal) &&
                    _w._tabs.Count == tabsAtToggle,
                    "secret=" + normal.IsSecret + " tabs=" + _w._tabs.Count);
                Check("unsealed tab shows its real title and url again",
                    normal.DisplayTitle == realTitle && _w._address.Text == realUrl,
                    "title=" + normal.DisplayTitle + " address='" + _w._address.Text + "'");

                // ---------- 5b. 真实输入路径（回归：普通标签上按键没反应） ----------
                // WPF 在 Alt 组合下投递的是 Key.System，真实按键在 SystemKey 里。
                _w.SelectTab(normal);
                EnsurePlain(normal);
                _w.HandleShortcut(System.Windows.Input.Key.System,
                    System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.OemTilde);
                Check("wpf Key.System path seals the current tab",
                    normal.IsSecret && ReferenceEquals(_w._active, normal),
                    "secret=" + normal.IsSecret);

                // 页面聚焦时只走线程消息泵：Alt+` 是 WM_SYSKEYDOWN(0x0104) + VK_OEM_3(0xC0)，
                // 此时 WPF 的 Keyboard.Modifiers 还没同步，不能靠它判断 Alt。
                _w.SelectTab(normal);
                EnsurePlain(normal);
                var sysMsg = new System.Windows.Interop.MSG
                {
                    message = 0x0104,
                    wParam = (IntPtr)0xC0
                };
                bool sysHandled = false;
                _w.OnThreadPreprocessMessage(ref sysMsg, ref sysHandled);
                Check("message pump WM_SYSKEYDOWN seals the current tab",
                    sysHandled && normal.IsSecret && ReferenceEquals(_w._active, normal),
                    "handled=" + sysHandled + " secret=" + normal.IsSecret);

                // ---------- 5c. 切换键可修改 ----------
                Check("default hotkey displays as Alt+`",
                    Hotkey.Display(Hotkey.Default) == "Alt+`", Hotkey.Display(Hotkey.Default));

                _w._settings.SecretHotkey = "Ctrl+Shift+S";
                _w.SelectTab(normal);
                EnsurePlain(normal);
                _w.HandleShortcut(System.Windows.Input.Key.S,
                    System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift,
                    System.Windows.Input.Key.None);
                Check("custom hotkey seals the current tab",
                    normal.IsSecret && ReferenceEquals(_w._active, normal),
                    "secret=" + normal.IsSecret);

                EnsurePlain(normal);
                _w.HandleShortcut(System.Windows.Input.Key.OemTilde,
                    System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.None);
                Check("old Alt+` stops working after the key changed",
                    !normal.IsSecret, "secret=" + normal.IsSecret);

                // 改过的键也必须能从页面聚焦时的消息泵路径进来。
                _w._settings.SecretHotkey = "Alt+Q";
                _w.SelectTab(normal);
                EnsurePlain(normal);
                var altQ = new System.Windows.Interop.MSG
                {
                    message = 0x0104,
                    wParam = (IntPtr)System.Windows.Input.KeyInterop.VirtualKeyFromKey(
                        System.Windows.Input.Key.Q)
                };
                bool altQHandled = false;
                _w.OnThreadPreprocessMessage(ref altQ, ref altQHandled);
                Check("custom hotkey works from the message pump path",
                    altQHandled && normal.IsSecret && ReferenceEquals(_w._active, normal),
                    "handled=" + altQHandled + " secret=" + normal.IsSecret);

                Check("capture normalizes WPF Key.System",
                    Hotkey.Capture(System.Windows.Input.Key.System,
                        System.Windows.Input.ModifierKeys.Alt,
                        System.Windows.Input.Key.Q) == "Alt+Q", "Alt+Q");
                Check("bare key is rejected",
                    Hotkey.Capture(System.Windows.Input.Key.S,
                        System.Windows.Input.ModifierKeys.None,
                        System.Windows.Input.Key.None) == null, "null");
                Check("reserved Ctrl+T is rejected",
                    Hotkey.Capture(System.Windows.Input.Key.T,
                        System.Windows.Input.ModifierKeys.Control,
                        System.Windows.Input.Key.None) == null, "null");
                Check("malformed setting falls back to default",
                    Hotkey.Normalize("not-a-chord") == Hotkey.Default,
                    Hotkey.Normalize("not-a-chord"));

                // 落盘往返：临时重定向，绝不碰用户自己的 settings.json。
                var savedPath = SettingsStore.FilePath;
                var savedPersist = SettingsStore.Persist;
                try
                {
                    SettingsStore.FilePath = Path.Combine(_temp, "settings.json");
                    SettingsStore.Persist = true;
                    _w._settings.SecretHotkey = "Ctrl+Shift+S";
                    SettingsStore.Save(_w._settings);
                    var reloaded = SettingsStore.Load();
                    Check("custom hotkey survives settings.json round-trip",
                        reloaded.SecretHotkey == "Ctrl+Shift+S", reloaded.SecretHotkey);
                }
                finally
                {
                    SettingsStore.FilePath = savedPath;
                    SettingsStore.Persist = savedPersist;
                }
                _w._settings.SecretHotkey = Hotkey.Default;

                // ---------- 5d. 设置页录入状态机 ----------
                var page = new SettingsPage(_w._settings);
                Check("settings page starts outside capture mode",
                    !SettingsPage.IsCapturing(page), "capturing=false");
                var picker = page.HotkeyButtonForTest;
                Check("hotkey row shows the current combination",
                    picker != null && picker.Content != null &&
                    picker.Content.ToString() == Hotkey.Display(Hotkey.Default),
                    picker == null ? "button not found" : picker.Content.ToString());
                if (picker != null)
                {
                    picker.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Check("clicking the button enters capture mode",
                        SettingsPage.IsCapturing(page), "capturing=true");
                    var esc = new System.Windows.Input.KeyEventArgs(
                        System.Windows.Input.Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(_w), 0,
                        System.Windows.Input.Key.Escape)
                    {
                        RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
                    };
                    page.RaiseEvent(esc);
                    Check("Esc leaves capture mode", !SettingsPage.IsCapturing(page), "capturing=false");
                }

                // ---------- 5e. 真实 Win32 消息端到端 ----------
                _w.SelectTab(normal);
                EnsurePlain(normal);
                var hwnd = new System.Windows.Interop.WindowInteropHelper(_w).Handle;
                PostMessage(hwnd, 0x0104, (IntPtr)0xC0, (IntPtr)0x20000000);
                await Task.Delay(300);
                Check("real WM_SYSKEYDOWN message seals the current tab",
                    normal.IsSecret && ReferenceEquals(_w._active, normal),
                    "secret=" + normal.IsSecret);

                // ---------- 5f. 消息泵不再读 Keyboard.Modifiers：状态必须干净 ----------
                // 真实按下 Ctrl/Shift 的读取依赖物理键盘，无前台焦点的自测进程验证不了；
                // 这里守住另一半：没有按键时不能带残留修饰位（残留会让组合键匹配失败）。
                Check("native modifiers are clean when nothing is held",
                    MainWindow.NativeModifiers() == System.Windows.Input.ModifierKeys.None,
                    "mods=" + MainWindow.NativeModifiers());

                // ---------- 6. 通知开 ----------
                _w._settings.SecretNotifications = true;
                _w.SelectTab(secret);
                var urlAllowed = "http://127.0.0.1:" + _portAllowed + "/page";
                _w.Navigate(secret, urlAllowed);
                var ready2 = await WaitReadyAsync(secret, urlAllowed, 20000);
                Check("secret page reloaded on a second origin", ready2, "ready=" + ready2);
                var allowed = await WaitPermissionAsync(secret, 12000);
                Check("notifications are allowed when the switch is on", allowed == "granted", allowed);

                // ---------- 6b. 「展示」到底发没发生 ----------
                // 页面真的 new 一个 Notification：宿主若收到 NotificationReceived 且没被
                // Handled 掉，再加上页面 onshow 触发，才算「这条通知真的发出去了」。
                await EvalStringAsync(secret, "__m5.shown=false");
                var allowCtor = await EvalStringAsync(secret, "window.__m5.notify('nanoedge m5 allow')");
                var hostFired = await WaitForAsync(() => HostCount() > 0, 5000);
                var shownOn = await WaitPageFlagAsync(secret, "__m5.shown", 4000);
                Check("the notification is displayed when the switch is on",
                    allowCtor == "ok" && hostFired && shownOn && !LastHandled(),
                    "ctor=" + allowCtor + " events=" + HostCount() + " shown=" + shownOn +
                    " handled=" + LastHandled() + " titles=" + HostTitles());
                CopyScreen(Path.Combine(_root, "out", "m5-toast.png"));

                // ---------- 6c. 把开关关回去：已经放行过的 origin 还能不能弹 ----------
                // 授权是按 origin 缓存在 profile 里的，重新导航未必还会触发 PermissionRequested，
                // 这条专门打「关了开关但缓存还在」这条路。
                var permBefore = await EvalStringAsync(secret, "Notification.permission");
                var requestsBefore = PermRequests();
                var hostBefore = HostCount();
                _w._settings.SecretNotifications = false;
                _w.Navigate(secret, urlAllowed);
                var ready3 = await WaitReadyAsync(secret, urlAllowed, 20000);
                var permAfter = await EvalStringAsync(secret, "Notification.permission");
                await EvalStringAsync(secret, "__m5.shown=false");
                var ctorAfter = await EvalStringAsync(secret, "window.__m5.notify('nanoedge m5 off-again')");
                var shownAfter = await WaitPageFlagAsync(secret, "__m5.shown", 2500);
                var askedAgain = PermRequests() - requestsBefore;
                var displayedAfter = HostCount() > hostBefore && !LastHandled();
                Line("[perm] reloaded=" + ready3 + " before=" + permBefore + " after=" + permAfter +
                     " askedAgain=" + askedAgain + " savesInProfile=" + _permSaves);
                Check("a previously granted origin stops displaying once the switch is off",
                    !shownAfter && !displayedAfter,
                    "permission=" + permAfter + " ctor=" + ctorAfter + " shown=" + shownAfter +
                    " askedAgain=" + askedAgain + " hostEvents=" + (HostCount() - hostBefore) +
                    " handled=" + LastHandled());

                // ---------- 6d. 兜底那层到底有没有用 ----------
                // 造一个「origin 早就被放行过」的局面（等价于：先当普通标签放过一次，
                // 再按快捷键把它密封）。这时 PermissionRequested 根本不会再触发，
                // 只能靠 NotificationReceived 那一层把展示拦下来。
                var secretOrigin = "http://127.0.0.1:" + _portAllowed;
                var preGranted = await PreGrantAsync(secret, secretOrigin);
                Line("[perm] pre-granted setup = " + preGranted);
                if (preGranted)
                {
                    var hostBefore2 = HostCount();
                    _w.Navigate(secret, urlAllowed);
                    await WaitReadyAsync(secret, urlAllowed, 20000);
                    var permCached = await EvalStringAsync(secret, "Notification.permission");
                    await EvalStringAsync(secret, "__m5.shown=false");
                    await EvalStringAsync(secret, "window.__m5.notify('nanoedge m5 pre-granted')");
                    var shownCached = await WaitPageFlagAsync(secret, "__m5.shown", 2500);
                    var fired = HostCount() > hostBefore2;
                    Check("a pre-granted secret origin still cannot display while the switch is off",
                        !shownCached && !(fired && !LastHandled()),
                        "permission=" + permCached + " hostEvents=" + (HostCount() - hostBefore2) +
                        " handled=" + LastHandled() + " shown=" + shownCached);
                }

                // ---------- 6e. 这层兜底不能误伤普通标签 ----------
                _w.SelectTab(normal);
                EnsurePlain(normal);
                var normalOrigin = "http://127.0.0.1:" + _portDenied;
                var normalGranted = await PreGrantAsync(normal, normalOrigin);
                Line("[perm] normal-tab pre-granted setup = " + normalGranted);
                if (normalGranted)
                {
                    _w.Navigate(normal, normalUrl);
                    await WaitReadyAsync(normal, normalUrl, 20000);
                    var normalPerm = await EvalStringAsync(normal, "Notification.permission");
                    var hostBefore3 = HostCount();
                    await EvalStringAsync(normal, "__m5.shown=false");
                    await EvalStringAsync(normal, "window.__m5.notify('nanoedge m5 normal')");
                    var normalShown = await WaitPageFlagAsync(normal, "__m5.shown", 3000);
                    Check("a normal tab is not suppressed by the secret-tab guard",
                        normalPerm == "granted" && HostCount() > hostBefore3 && !LastHandled(),
                        "permission=" + normalPerm + " hostEvents=" + (HostCount() - hostBefore3) +
                        " handled=" + LastHandled() + " shown=" + normalShown);
                }

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

                // ---------- 8. 一个密封标签都没有时，仍能就地密封当前标签 ----------
                _w.CloseTab(secret);
                _w.SelectTab(normal);
                EnsurePlain(normal);
                Check("no secret tab remains after closing it",
                    !_w._tabs.Any(t => t.IsSecret), "tabs=" + _w._tabs.Count);
                var countBefore = _w._tabs.Count;
                _w.HandleShortcut(System.Windows.Input.Key.OemTilde,
                    System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.None);
                Check("hotkey seals in place without creating a new tab",
                    normal.IsSecret && ReferenceEquals(_w._active, normal) &&
                    _w._tabs.Count == countBefore,
                    "secret=" + normal.IsSecret + " tabs=" + _w._tabs.Count);
                Check("sealed-in-place tab is disguised as 文档",
                    normal.DisplayTitle == "文档", normal.DisplayTitle);

                _w.HandleShortcut(System.Windows.Input.Key.OemTilde,
                    System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.None);
                Check("second press unseals it", !normal.IsSecret,
                    "secret=" + normal.IsSecret);

                // 内部页不能把标签留在「密封」状态，否则标题叫文档、内容却是设置页。
                _w.SelectTab(normal);
                EnsurePlain(normal);
                _w.HandleShortcut(System.Windows.Input.Key.OemTilde,
                    System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.None);
                _w.OpenInternalPage(normal, "nano://settings");
                Check("opening an internal page clears the secret flag",
                    !normal.IsSecret && normal.InternalPage == "settings",
                    "secret=" + normal.IsSecret + " page=" + normal.InternalPage);

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

            // ---------- 通知探针 ----------

            // 宿主观测点：只记录，不动 Handled，也不改 State —— 不干扰被测行为。
            private void ProbeCore(TabModel tab)
            {
                var core = _w._browser.Core(tab);
                if (core == null) { Line("[probe] no core"); return; }

                core.PermissionRequested += (s, e) =>
                {
                    if (e.PermissionKind != Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.Notifications)
                        return;
                    lock (_hostLock)
                    {
                        _permRequests++;
                        if (e.SavesInProfile) _permSaves++;
                    }
                    Line("[probe] permission requested " + e.PermissionKind + " " + e.Uri +
                         " savesInProfile=" + e.SavesInProfile);
                };

                core.NotificationReceived += (s, e) =>
                {
                    var title = "(none)";
                    try { title = e.Notification == null ? "(null)" : e.Notification.Title; }
                    catch { title = "(unreadable)"; }
                    lock (_hostLock)
                    {
                        _hostTitles.Add(title);
                        // 生产代码在同一个事件上兜了「开关关着就 Handled」这一层，
                        // 这里读到的是所有处理器跑完之后的最终决定。
                        _hostHandled.Add(e.Handled);
                    }
                    Line("[probe] NotificationReceived title='" + title + "' handled=" + e.Handled);
                };
            }

            // 把某个 origin 的通知授权直接写进 profile，模拟「这条来源早就被放行过」。
            // 生产代码只在 PermissionRequested 里管授权，这一手绕过了它，专门打兜底那层。
            private async Task<bool> PreGrantAsync(TabModel tab, string origin)
            {
                var core = _w._browser.Core(tab);
                if (core == null) return false;
                var any = false;
                foreach (var candidate in new[] { origin, origin + "/" })
                {
                    try
                    {
                        await core.Profile.SetPermissionStateAsync(
                            Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.Notifications,
                            candidate,
                            Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Allow);
                        any = true;
                        Line("[perm] SetPermissionStateAsync Allow " + candidate);
                    }
                    catch (Exception ex)
                    {
                        Line("[perm] SetPermissionStateAsync failed " + candidate + ": " + ex.Message);
                    }
                }
                return any;
            }

            private int HostCount() { lock (_hostLock) return _hostTitles.Count; }            private int PermRequests() { lock (_hostLock) return _permRequests; }
            private string HostTitles() { lock (_hostLock) return string.Join("|", _hostTitles); }
            private bool LastHandled() { lock (_hostLock) return _hostHandled.Count > 0 && _hostHandled[_hostHandled.Count - 1]; }

            private async Task<bool> WaitPageFlagAsync(TabModel tab, string expression, int timeoutMs)
            {
                var started = Environment.TickCount;
                while (Environment.TickCount - started < timeoutMs)
                {
                    var value = await EvalStringAsync(tab, "String(" + expression + ")");
                    if (value == "true") return true;
                    await Task.Delay(150);
                }
                return false;
            }

            // 真实桌面截图：toast 是独立 HWND，RenderTargetBitmap 抓不到，只能整屏拷贝。
            // 会话没有活动桌面时这里会拿不到画面（或全黑），只做尽力而为的证据。
            private void CopyScreen(string path)
            {
                try
                {
                    var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
                    using (var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height))
                    {
                        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size);
                        var directory = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Line("[screen] captured " + bounds.Width + "x" + bounds.Height + " → " + path);
                }
                catch (Exception ex) { Line("[screen] capture failed: " + ex.Message); }
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
                       "<script>window.__m5 = { perm: 'pending', ctor: 'none', err: '', shown: false };" +
                       "if (window.Notification && Notification.requestPermission) {" +
                       "  Notification.requestPermission().then(function (p) { __m5.perm = p; })" +
                       "    .catch(function (e) { __m5.perm = 'error:' + e; });" +
                       "} else { __m5.perm = 'unsupported'; }" +
                       // 真的去弹一个通知：能不能构造、onshow 有没有到，都记下来。
                       "window.__m5.notify = function (title) {" +
                       "  try {" +
                       "    var n = new Notification(title, { body: 'nanoedge m5test' });" +
                       "    n.onshow = function () { __m5.shown = true; };" +
                       "    __m5.ctor = 'ok';" +
                       "    return 'ok';" +
                       "  } catch (e) {" +
                       "    __m5.ctor = 'throw';" +
                       "    __m5.err = (e && e.name ? e.name + ': ' : '') + (e && e.message ? e.message : String(e));" +
                       "    return 'throw';" +
                       "  }" +
                       "};</script>" +
                       "</head><body style=\"margin:0;padding:18px;background:#111;color:#eee;" +
                       "font:14px 'Segoe UI',sans-serif\">" +
                       "<h1 style=\"font-size:18px;margin:0 0 8px 0\">M5 notification probe</h1>" +
                       "<p style=\"color:#888\">served by 127.0.0.1</p></body></html>";
            }

            // 真实消息路径：把 WM_SYSKEYDOWN 投进窗口消息队列，让 WPF 的线程泵去分发，
            // 而不是直接调 OnThreadPreprocessMessage（旧自测只做到了后者）。
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

            // 把被测标签复位成普通状态：直接翻转，不依赖当前快捷键设置。
            private void EnsurePlain(TabModel tab)
            {
                if (tab != null && tab.IsSecret) _w.ToggleSecret();
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
