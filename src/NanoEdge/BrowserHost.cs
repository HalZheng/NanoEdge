using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace NanoEdge
{
    public static class UrlParser
    {
        // Resolution order follows SPEC 7.2. Kept deliberately small; the search
        // keyword prefixes are configurable in settings later.
        public static string Resolve(string input, AppSettings settings)
        {
            if (string.IsNullOrWhiteSpace(input)) return "nano://start";
            var text = input.Trim();

            if (text.StartsWith("nano://", StringComparison.OrdinalIgnoreCase)) return text;
            if (text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return text;
            if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return text;

            // 带了空格就整句当搜索词，交给当前默认搜索引擎。
            // 0.19 之前的 b/g/gh/npm 前缀已移除，改用起始页和设置页的下拉切换。
            if (text.Contains(" ")) return Search(text, settings);

            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return text;

            if (IsLocalHost(text)) return "http://" + text;

            if (!text.Contains(" ") && text.Contains(".")) return "https://" + text;
            return Search(text, settings);
        }

        private static bool IsLocalHost(string text)
        {
            var host = text.Split('/')[0].ToLowerInvariant();
            return host == "localhost" || host.StartsWith("localhost:") ||
                   host.StartsWith("127.0.0.1") || host.StartsWith("192.168.") ||
                   host.StartsWith("10.") || host.StartsWith("[");
        }

        // 能不能当网址打开（决定地址栏第一行是不是"打开 xxx"）。
        public static bool LooksLikeUrl(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            var text = Normalize(input);
            if (text.Contains(" ")) return false;
            if (HasScheme(text)) return true;
            if (IsLocalHost(text)) return true;
            return text.Contains(".");
        }

        public static string NormalizeUrl(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var text = Normalize(input);
            if (text.Contains(" ")) return null;
            if (HasScheme(text)) return text;
            if (IsLocalHost(text)) return "http://" + text;
            if (text.Contains(".")) return "https://" + text;
            return null;
        }

        // Ctrl+Enter：把 "github" 补成 "github.com"。
        public static string CompleteDotCom(string input)
        {
            var text = Normalize(input);
            if (text.Length == 0 || text.Contains(" ")) return text;
            if (HasScheme(text) || IsLocalHost(text)) return text;
            var host = text.Split('/')[0];
            return host.Contains(".") ? text : text + ".com";
        }

        // 粘贴进来的常见脏东西：前后空白、包裹的引号、零宽字符。
        private static string Normalize(string input)
        {
            return (input ?? "").Trim()
                .Trim('"', '\'', '\u201c', '\u201d', '\u2018', '\u2019')
                .Replace("\u200b", "").Replace("\ufeff", "")
                .Trim();
        }

        private static bool HasScheme(string text)
        {
            return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("nano://", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("about:", StringComparison.OrdinalIgnoreCase);
        }

        private static string Search(string text, AppSettings settings)
        {
            var template = settings.SearchEngine;
            if (string.IsNullOrWhiteSpace(template))
                template = "https://www.bing.com/search?q={q}";
            return template.Replace("{q}", Uri.EscapeDataString(text));
        }
    }

    // BrowserHost owns the per-tab WebView2 instances and is the only place that
    // touches WebView2 memory APIs. The scheduling decisions live in MemoryGovernor
    // (SPEC 4.1/4.2); everything here is "just do it".
    public sealed class BrowserHost : IMemoryTabOps
    {
        private readonly Grid _host;
        private readonly AppSettings _settings;
        private readonly Dictionary<string, FrameworkElement> _views = new Dictionary<string, FrameworkElement>();
        private readonly Dictionary<string, WebView2> _webviews = new Dictionary<string, WebView2>();
        private Task<CoreWebView2Environment> _environmentTask;

        public event Action<TabModel> TitleChanged;
        public event Action<TabModel> UrlChanged;
        public event Action<TabModel> FaviconChanged;             // 页面图标（仅内存）
        public event Action<string> OpenInNewTab;
        public event Action<TabModel> FlagsChanged;              // audio / downloads / devtools
        public event Action<TabModel, bool> RestoreCompleted;    // snapshot applied?
        public event Action<TabModel, string, bool> NavigationDone; // tab, url, success
        public event Action<TabModel, string, int> HttpResponse;    // tab, request uri, status code
        public event Action<TabModel, double> ZoomChanged;       // 供状态栏显示当前档位
        public event Action<TabModel, string, bool> ProcessFailed;  // tab, message, offerReload
        public event Action<TabModel, string> LoginUiMessage;    // 页面内登录图标/下拉的消息（不是用户脚本）
        public event Action BrowserExited;                       // 整个浏览器进程没了
        public Func<TabModel, FrameworkElement> PlaceholderFactory;

        // 宿主写入 ZoomFactor 时屏蔽 ZoomFactorChanged 回调，避免自激。
        private bool _applyingZoom;

        // 每个 CoreWebView2 当前挂着的注入脚本 id，改动脚本后要先移除再重加。
        private readonly Dictionary<CoreWebView2, List<string>> _injected =
            new Dictionary<CoreWebView2, List<string>>();
        // 同一次导航只跑一条 favicon 重试链：FaviconChanged 可能连发多次。
        private readonly HashSet<string> _faviconBusy = new HashSet<string>();
        private bool _applyingScripts;
        private bool _reapplyScripts;
        public ScriptBridge Scripts { get; private set; }

        // 自测用：确认 Ctrl+F5 真的走了 CDP 硬刷新，而不是普通 Reload。
        internal int HardReloads { get; private set; }

        // ---------- 网页语言（与外壳界面语言无关） ----------
        // 当前生效值。显式档才注册过滤器；system 档不注册，保持 WebView2 原生行为。
        private string _webLanguage;
        private readonly HashSet<CoreWebView2> _languageFilters = new HashSet<CoreWebView2>();
        private bool _headerFailureLogged;

        // 只拦主文档和网站用来做语言判断的接口请求，图片/字体/样式不进托管回调。
        private static readonly CoreWebView2WebResourceContext[] LanguageContexts =
        {
            CoreWebView2WebResourceContext.Document,
            CoreWebView2WebResourceContext.XmlHttpRequest,
            CoreWebView2WebResourceContext.Fetch
        };

        internal string CurrentWebLanguage { get { return _webLanguage; } }
        internal int LanguageFilterCount { get { return _languageFilters.Count; } }

        public BrowserHost(Grid host, AppSettings settings)
        {
            _host = host;
            _settings = settings;
            _webLanguage = WebLanguage.Normalize(settings.WebLanguage);
            Scripts = new ScriptBridge();
            ScriptStore.Changed += RefreshScripts;
        }

        public Task<CoreWebView2Environment> EnvironmentAsync()
        {
            if (_environmentTask == null)
            {
                _environmentTask = CoreWebView2Environment.CreateAsync(
                    null, AppPaths.UserData, new CoreWebView2EnvironmentOptions());
            }
            return _environmentTask;
        }

        public CoreWebView2 Core(TabModel tab)
        {
            WebView2 view;
            return _webviews.TryGetValue(tab.Id, out view) && view != null ? view.CoreWebView2 : null;
        }

        // 自测用：把真实键盘焦点设到 WebView2 的子 HWND 上。
        internal IntPtr WindowHandleOf(TabModel tab)
        {
            WebView2 view;
            if (!_webviews.TryGetValue(tab.Id, out view) || view == null) return IntPtr.Zero;
            try { return view.Handle; } catch { return IntPtr.Zero; }
        }

        public bool HasLiveView(TabModel tab)
        {
            return Core(tab) != null;
        }

        public void RegisterStartPage(TabModel tab, FrameworkElement view)
        {
            _views[tab.Id] = view;
            _host.Children.Add(view);
        }

        public void ReplaceWithWebView(TabModel tab)
        {
            FrameworkElement old;
            if (_views.TryGetValue(tab.Id, out old))
            {
                _host.Children.Remove(old);
                _views.Remove(tab.Id);
            }

            // ZoomFactor 是 DP，控制器还没建也能先写，初始化后会自动套上去。
            var view = new WebView2 { Tag = tab.Id, ZoomFactor = tab.Zoom };
            view.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(
                Theme.IsDark ? "#0E1116" : "#F3F5F9");
            view.Visibility = Visibility.Collapsed;
            _host.Children.Add(view);
            _views[tab.Id] = view;
            _webviews[tab.Id] = view;
            tab.IsStartPage = false;
            tab.InternalPage = "";
            tab.ZoomSite = "";
        }

        // 把标签切到纯 WPF 内部页：销毁已有 WebView2（renderer 随之退出），再挂上新视图。
        public void ShowInternal(TabModel tab, FrameworkElement view)
        {
            FrameworkElement old;
            if (_views.TryGetValue(tab.Id, out old))
            {
                _host.Children.Remove(old);
                _views.Remove(tab.Id);
                var web = old as WebView2;
                if (web != null)
                {
                    ForgetScripts(tab);
                    _webviews.Remove(tab.Id);
                    try { web.Dispose(); }
                    catch (Exception ex) { Diagnostics.Log("dispose on internal failed: " + ex.Message); }
                }
            }

            if (view == null) return;
            view.Visibility = Visibility.Collapsed;
            _views[tab.Id] = view;
            _host.Children.Add(view);
            tab.IsStartPage = false;
            tab.MemoryState = TabMemoryState.Active;
            tab.ZoomSite = "";   // 内部页不缩放；回到网页时重新套用该站点的记忆
        }

        // 换掉一个纯 WPF 视图（起始页/内部页），不碰标签的 IsStartPage/InternalPage 标记。
        // 双语切换时用它整块重建本地页；网页标签不走这里。
        public void ReplaceLocalView(TabModel tab, FrameworkElement view)
        {
            FrameworkElement old;
            if (_views.TryGetValue(tab.Id, out old))
            {
                _host.Children.Remove(old);
                _views.Remove(tab.Id);
                var web = old as WebView2;
                if (web != null)
                {
                    ForgetScripts(tab);
                    _webviews.Remove(tab.Id);
                    try { web.Dispose(); }
                    catch (Exception ex) { Diagnostics.Log("dispose on local replace: " + ex.Message); }
                }
            }

            if (view == null) return;
            view.Visibility = Visibility.Collapsed;
            _views[tab.Id] = view;
            _host.Children.Add(view);
        }

        public async Task NavigateAsync(TabModel tab, string input, TabSnapshot restore = null)
        {
            var url = UrlParser.Resolve(input, _settings);
            if (url == "nano://start" || url.StartsWith("about:"))
                return;

            if (!_webviews.ContainsKey(tab.Id)) ReplaceWithWebView(tab);
            var view = _webviews[tab.Id];

            var environment = await EnvironmentAsync();
            if (view.CoreWebView2 == null)
            {
                try
                {
                    await view.EnsureCoreWebView2Async(environment);
                }
                catch (Exception ex)
                {
                    // 关窗口时控制器创建会被中止（E_ABORT），这里吞掉，别让退出路径崩。
                    Diagnostics.Log("ensure webview failed: " + ex.Message);
                    return;
                }
                if (view.CoreWebView2 == null) return;
                // 必须 await 完注入再 Navigate，否则首个页面会漏掉脚本。
                await ConfigureAsync(view, view.CoreWebView2, tab);
            }

            tab.Url = url;
            tab.MemoryState = TabMemoryState.Active;
            tab.Snapshot = null;

            if (restore != null && restore.Restorable)
                AttachRestore(view.CoreWebView2, tab, restore);

            ApplyZoomForSite(tab);
            view.CoreWebView2.Navigate(url);
            // 不要在这里 Show(tab)：后台标签加载完成时会把当前标签的视图抢成 Collapsed。
            // 可见性统一由 MainWindow 在确认 tab 仍是 active 后刷新。
        }

        private void AttachRestore(CoreWebView2 core, TabModel tab, TabSnapshot snapshot)
        {
            EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = null;
            handler = async (s, e) =>
            {
                core.NavigationCompleted -= handler;
                var applied = false;
                if (e.IsSuccess)
                {
                    try
                    {
                        await core.ExecuteScriptAsync(SnapshotScript.Restore(snapshot));
                        applied = true;
                    }
                    catch (Exception ex) { Diagnostics.Log("restore script failed: " + ex.Message); }
                }
                RestoreCompleted?.Invoke(tab, applied);
            };
            core.NavigationCompleted += handler;
        }

        private async Task ConfigureAsync(WebView2 view, CoreWebView2 core, TabModel tab)
        {
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            // Ctrl+滚轮缩放仍交给 WebView2；键盘 Ctrl+±/0 由 MainWindow 的消息泵统一分发，
            // 因为站点记忆要求我们自己掌握档位（否则 Ctrl+0 只回到站点默认值，不是 100%）。
            core.Settings.IsZoomControlEnabled = true;

            // 网页语言：过滤器必须在首次导航之前就挂好（WebView2 文档明确要求）。
            // 事件处理器常驻，但只有显式档注册了过滤器时才会被调用。
            core.WebResourceRequested += OnWebResourceRequested;
            SyncLanguageFilter(core);

            view.ZoomFactorChanged += (s, e) =>
            {
                if (_applyingZoom) return;
                var factor = ZoomLevels.Clamp(view.ZoomFactor);
                if (Math.Abs(factor - tab.Zoom) < 0.0001) return;
                tab.Zoom = factor;
                ZoomStore.SetForUrl(core.Source ?? tab.Url, factor);
                ZoomChanged?.Invoke(tab, factor);
            };

            core.DocumentTitleChanged += (s, e) =>
            {
                tab.Title = core.DocumentTitle;
                TitleChanged?.Invoke(tab);
            };
            // 图标变化只在页面真的换了 favicon 时触发；这里不做网络请求，
            // 取的是内核已经拿到的图标（SPEC 7.8）。
            core.FaviconChanged += async (s, e) => await UpdateFaviconAsync(core, tab);
            // 浏览器自己下载图标时截获那次响应：不再额外发一次请求（SPEC 7.8）。
            core.WebResourceResponseReceived += async (s, e) => await OnFaviconResponseAsync(core, tab, e);
            // pgAdmin 掉线检测（7.1 条件 2）：只转发请求 URL 与状态码，不读响应正文。
            core.WebResourceResponseReceived += (s, e) =>
            {
                var handler = HttpResponse;
                if (handler == null) return;
                try
                {
                    var uri = e.Request == null ? "" : e.Request.Uri;
                    var status = e.Response == null ? 0 : e.Response.StatusCode;
                    if (!string.IsNullOrEmpty(uri)) handler(tab, uri, status);
                }
                catch { }
            };
            core.SourceChanged += (s, e) =>
            {
                if (string.IsNullOrEmpty(core.Source)) return;
                tab.Url = core.Source;
                // 跨站导航先换成目标站已有的图标（没有就清空），避免沿用上一站。
                tab.Favicon = Favicons.Get(tab.Url);
                ApplyZoomForSite(tab);
                UrlChanged?.Invoke(tab);
            };
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                OpenInNewTab?.Invoke(e.Uri);
            };
            core.NavigationCompleted += async (s, e) =>
            {
                // 密封标签的 URL 不能进日志文件。
                Diagnostics.Log("nav " + (e.IsSuccess ? "ok" : "fail") + " " +
                                (tab.IsSecret ? "(secret)" : core.Source));
                NavigationDone?.Invoke(tab, core.Source, e.IsSuccess);
                // 兜底：占位图/晚到的图标靠短重试补齐，不额外发网络请求。
                if (e.IsSuccess) await FetchFaviconWithRetryAsync(core, tab);
                // 备份恢复后的 DDL 快照：有待导入状态且命中目标站点才尝试重建。
                // 平时 HasPending=false，导航完成路径零额外开销。
                if (e.IsSuccess && DdlRestore.HasPending)
                    await DdlRestore.TryInjectAsync(core, core.Source);
            };

            // SPEC 4.3 豁免来源：这些标志由内核事件维护，不靠轮询。
            core.IsDocumentPlayingAudioChanged += (s, e) =>
            {
                tab.IsPlayingAudio = core.IsDocumentPlayingAudio;
                FlagsChanged?.Invoke(tab);
            };
            core.DownloadStarting += (s, e) =>
            {
                tab.ActiveDownloads++;
                FlagsChanged?.Invoke(tab);

                var op = e.DownloadOperation;
                var path = ChooseDownloadPath(e.ResultFilePath, op);
                e.ResultFilePath = path;
                e.Handled = true;   // 隐藏 WebView2 默认下载弹窗，nano://downloads 就是 UI
                DownloadManager.Start(op, path, tab.IsSecret ? "" : Urls.Host(tab.Url));

                op.StateChanged += (s2, e2) =>
                {
                    var state = op.State;
                    if (state == CoreWebView2DownloadState.Completed ||
                        state == CoreWebView2DownloadState.Interrupted)
                    {
                        tab.ActiveDownloads = Math.Max(0, tab.ActiveDownloads - 1);
                        FlagsChanged?.Invoke(tab);
                    }
                };
            };

            // 密封标签的页面通知开关：只拦 Notifications，其它权限不干预。
            // 关闭时直接 Deny 并 Handled，页面不会看到权限弹条。
            core.PermissionRequested += (s, e) =>
            {
                if (!tab.IsSecret || e.PermissionKind != CoreWebView2PermissionKind.Notifications) return;
                e.State = _settings.SecretNotifications
                    ? CoreWebView2PermissionState.Allow
                    : CoreWebView2PermissionState.Deny;
                // 不写进 profile：一条「某 origin 可以发通知」的授权记录，本身就是
                // 「这个站点来过」的磁盘痕迹，密封标签不该留下它。
                e.SavesInProfile = false;
                e.Handled = true;
            };

            // 授权是按 origin 记在 profile 里的，站点被放行过一次之后就不会再走
            // PermissionRequested —— 于是一个已经放行过的站点，在把开关关掉之后
            // 仍然能弹通知（--m5test 里量到过）。所以展示前再兜一层：开关关着，
            // 密封标签的通知一律不展示。只动密封标签，普通标签默认展示不受影响。
            core.NotificationReceived += (s, e) =>
            {
                if (tab.IsSecret && !_settings.SecretNotifications) e.Handled = true;
            };

            // 崩溃恢复（SPEC 7.5 / M6）：渲染进程崩溃重载一次，无响应给手动重载。
            core.ProcessFailed += (s, e) => OnProcessFailed(tab, core, e);

            // 用户脚本的 GM_* 调用都从这里进来（7.3）。
            core.WebMessageReceived += (s, e) =>
            {
                try
                {
                    var json = e.WebMessageAsJson ?? "";
                    // 页面内 KeePassXC 图标 / 账号下拉的消息归宿主 UI 层处理。
                    // 它不走用户脚本的 @grant 判定，否则会被当成无权限脚本丢掉。
                    if (json.IndexOf("\"neLogin\"", StringComparison.Ordinal) >= 0)
                    {
                        var ui = LoginUiMessage;
                        if (ui != null) ui(tab, json);
                        return;
                    }
                    Scripts.Handle(core, json);
                }
                catch (Exception ex) { Diagnostics.Log("script message failed: " + ex.Message); }
            };

            await ApplyScriptsAsync(core);
        }

        // ---------- 网页语言 ----------

        // 设置页改动后调用：新请求立刻带上新头；已打开文档里的 navigator.language
        // 要等下一次导航（重新注册 document-created 脚本只影响之后的文档）。
        // 不重启浏览器进程、不重建 WebView2、不重载任何标签。
        public void ApplyWebLanguage()
        {
            _webLanguage = WebLanguage.Normalize(_settings.WebLanguage);
            foreach (var view in _webviews.Values.ToList())
                SyncLanguageFilter(view == null ? null : view.CoreWebView2);
            // 语言脚本和用户脚本共用一套注册链：一起移除、一起重加。
            RefreshScripts();
            Diagnostics.Log("web language = " + _webLanguage + ", filters=" + _languageFilters.Count);
        }

        private void SyncLanguageFilter(CoreWebView2 core)
        {
            if (core == null) return;
            var enabled = WebLanguage.IsOverride(_webLanguage);
            if (enabled)
            {
                if (!_languageFilters.Add(core)) return;
                foreach (var context in LanguageContexts)
                {
                    try
                    {
                        core.AddWebResourceRequestedFilter("*", context,
                            CoreWebView2WebResourceRequestSourceKinds.All);
                    }
                    catch (Exception ex) { Diagnostics.Log("add language filter failed: " + ex.Message); }
                }
            }
            else
            {
                if (!_languageFilters.Remove(core)) return;
                foreach (var context in LanguageContexts)
                {
                    try
                    {
                        core.RemoveWebResourceRequestedFilter("*", context,
                            CoreWebView2WebResourceRequestSourceKinds.All);
                    }
                    catch { }   // 过滤器本来就没加上时移除会报 E_INVALIDARG，忽略
                }
            }
        }

        private void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var header = WebLanguage.AcceptLanguage(_webLanguage);
            if (string.IsNullOrEmpty(header) || e.Request == null) return;
            try { e.Request.Headers.SetHeader("Accept-Language", header); }
            catch (Exception ex)
            {
                if (!_headerFailureLogged)
                {
                    _headerFailureLogged = true;
                    Diagnostics.Log("set Accept-Language failed: " + ex.Message);
                }
            }
        }

        // ---------- favicon（SPEC 7.8：不为了图标发网络请求） ----------

        // 本机实测 WebView2 的 GetFaviconAsync 只给 1×1 占位图，但 FaviconUri 是对的。
        // 所以三条路都走，取的都是页面自己已经拿到的字节，绝不额外发请求：
        //   1) FaviconUri 是 data URI —— 直接解 base64；
        //   2) WebResourceResponseReceived 里浏览器下载图标的那次响应；
        //   3) GetFaviconAsync（官方 API，能用就用）。
        private void ApplyFavicon(TabModel tab, string source, ImageSource image)
        {
            if (tab == null || image == null) return;
            if (string.IsNullOrEmpty(source) || !Urls.Same(tab.Url, source)) return;
            Favicons.Put(source, image);
            tab.Favicon = image;
            Diagnostics.Log("favicon applied " + Favicons.Key(source));
            var handler = FaviconChanged;
            if (handler != null) handler(tab);
        }

        private async Task OnFaviconResponseAsync(CoreWebView2 core, TabModel tab,
            CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                if (tab == null || tab.IsSecret || tab.IsStartPage || tab.Favicon != null) return;
                // 先按 URL 粗筛，避免每个资源都去查一次 FaviconUri（那是 COM 调用）。
                string requestUri;
                try { requestUri = e.Request.Uri; } catch { return; }
                if (string.IsNullOrEmpty(requestUri)) return;
                if (requestUri.IndexOf("favicon", StringComparison.OrdinalIgnoreCase) < 0 &&
                    !requestUri.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) return;
                if (e.Response == null || e.Response.StatusCode < 200 || e.Response.StatusCode >= 300) return;
                try
                {
                    if (e.Response.Headers.Contains("Content-Type") &&
                        e.Response.Headers.GetHeader("Content-Type").IndexOf("image", StringComparison.OrdinalIgnoreCase) < 0)
                        return;
                }
                catch { }

                string faviconUri = null;
                try { faviconUri = core.FaviconUri; } catch { }
                // FaviconUri 有时还是空的（图标尚未登记），这时仍按 URL 接受。
                if (!string.IsNullOrEmpty(faviconUri) &&
                    !string.Equals(faviconUri, requestUri, StringComparison.OrdinalIgnoreCase)) return;

                using (var stream = await e.Response.GetContentAsync())
                {
                    if (stream == null) return;
                    var bytes = Favicons.ReadBounded(stream, 256 * 1024);
                    if (bytes == null) return;
                    var source = core.Source;
                    using (var buffer = new MemoryStream(bytes))
                    {
                        var image = Favicons.Decode(buffer);
                        if (image != null) ApplyFavicon(tab, source, image);
                    }
                }
            }
            catch (Exception ex) { Diagnostics.Log("favicon response failed: " + ex.Message); }
        }

        // 导航结束后做几次短重试：FaviconChanged 可能在图标到位之前就发完了。
        private async Task FetchFaviconWithRetryAsync(CoreWebView2 core, TabModel tab)
        {
            if (core == null || tab == null) return;
            if (!_faviconBusy.Add(tab.Id)) return;
            try
            {
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    if (tab.Favicon != null || tab.IsSecret) return;
                    if (attempt > 0) await Task.Delay(500);
                    if (await UpdateFaviconAsync(core, tab)) return;
                }
            }
            finally { _faviconBusy.Remove(tab.Id); }
        }

        // 返回 true 表示拿到了可用的图标。
        private async Task<bool> UpdateFaviconAsync(CoreWebView2 core, TabModel tab)
        {
            try
            {
                if (core == null || tab == null) return false;
                // 密封标签不留图标：图标本身也可能泄露站点身份。
                if (tab.IsSecret || tab.IsStartPage) return false;
                var source = core.Source;
                if (string.IsNullOrEmpty(source) || !Urls.Same(tab.Url, source)) return false;
                if (string.IsNullOrEmpty(Favicons.Key(source))) return false;

                // 1) 内联图标（data URI），零网络请求。
                var dataImage = Favicons.DecodeDataUri(core.FaviconUri);
                if (dataImage != null)
                {
                    ApplyFavicon(tab, source, dataImage);
                    return true;
                }

                // 2) 官方 API；拿不到真图标时由响应截获路径兜底。
                using (var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png))
                {
                    var image = Favicons.Decode(stream);
                    if (image == null) return false;
                    ApplyFavicon(tab, source, image);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("favicon failed: " + ex.Message);
                return false;
            }
        }

        // ---------- 用户脚本注入（SPEC 7.3） ----------

        // 脚本增删改后调用：先移除旧 id 再重新注入，避免同一脚本被执行两次。
        public void RefreshScripts()
        {
            if (_applyingScripts) { _reapplyScripts = true; return; }
            var unused = RefreshScriptsAsync();
        }

        private async Task RefreshScriptsAsync()
        {
            _applyingScripts = true;
            try
            {
                foreach (var core in _injected.Keys.ToList())
                    await ApplyScriptsAsync(core);
            }
            catch (Exception ex) { Diagnostics.Log("refresh scripts failed: " + ex.Message); }
            finally
            {
                _applyingScripts = false;
                if (_reapplyScripts)
                {
                    _reapplyScripts = false;
                    var unused = RefreshScriptsAsync();
                }
            }
        }

        private async Task ApplyScriptsAsync(CoreWebView2 core)
        {
            if (core == null) return;

            List<string> old;
            if (_injected.TryGetValue(core, out old))
            {
                foreach (var id in old)
                {
                    try { core.RemoveScriptToExecuteOnDocumentCreated(id); }
                    catch (Exception ex) { Diagnostics.Log("remove script failed: " + ex.Message); }
                }
                _injected.Remove(core);
            }

            var scripts = ScriptStore.All().Where(s => s.Enabled).ToList();
            var sources = new List<string>();
            // 语言脚本排在最前：页面自己的脚本、用户脚本读 navigator.language 时
            // 拿到的已经是设置值。system 档返回空串，不注入。
            var languageScript = WebLanguage.Script(_webLanguage);
            if (!string.IsNullOrEmpty(languageScript)) sources.Add(languageScript);
            sources.Add(ScriptEngine.Bootstrap());
            // 登录表单识别：只暴露 scan / fill 两个方法，不常驻观察器（内存口径见 SPEC 2）。
            // 设置页关掉「填充账号密码」后连脚本都不注入，页面里不留痕迹。
            if (_settings.PasswordFill) sources.Add(LoginFill.ScriptFor(LoginFill.IconDataUri()));
            foreach (var script in scripts) sources.Add(ScriptEngine.Wrap(script));
            // 只有开了重登才注入这段引导器；它内部按站点 + 10 分钟标记双重判定，
            // 其它站点的页面即使被注入也不会有任何动作（F-12）。
            if (PgAdminReloginConfig.AnyEnabled()) sources.Add(PgAdminReloginScripts.Document());

            var ids = new List<string>();
            foreach (var source in sources)
            {
                try { ids.Add(await core.AddScriptToExecuteOnDocumentCreatedAsync(source)); }
                catch (Exception ex) { Diagnostics.Log("inject script failed: " + ex.Message); }
            }
            _injected[core] = ids;
            Diagnostics.Log("scripts injected: " + scripts.Count + " script(s), " + ids.Count + " block(s)");
        }

        private void ForgetScripts(TabModel tab)
        {
            var core = Core(tab);
            if (core == null) return;
            _injected.Remove(core);
            _languageFilters.Remove(core);
        }

        // ---------- 崩溃恢复 ----------

        private void OnProcessFailed(TabModel tab, CoreWebView2 core, CoreWebView2ProcessFailedEventArgs e)
        {
            Diagnostics.Log("process failed: " + e.ProcessFailedKind + " tab=" + tab.Id);

            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                var exited = BrowserExited;
                if (exited != null) exited();
                return;
            }

            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                Raise(tab, L.T("页面无响应"), true);
                return;
            }

            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
            {
                // 同一标签 20 秒内只自动重载一次，避免损坏页面把 CPU 打满。
                var now = DateTime.UtcNow;
                if ((now - tab.LastCrashReloadUtc).TotalSeconds < 20)
                {
                    Raise(tab, L.T("页面反复崩溃，已停止自动重载"), true);
                    return;
                }
                tab.LastCrashReloadUtc = now;
                try { core.Reload(); }
                catch (Exception ex) { Diagnostics.Log("crash reload failed: " + ex.Message); }
                Raise(tab, L.T("页面崩溃，已自动重新加载"), false);
                return;
            }

            Raise(tab, L.T("页面进程异常：") + e.ProcessFailedKind, true);
        }

        private void Raise(TabModel tab, string message, bool offerReload)
        {
            var handler = ProcessFailed;
            if (handler != null) handler(tab, message, offerReload);
        }

        // 浏览器进程整个退出后，所有 CoreWebView2 都作废：拆掉视图、标成已丢弃、
        // 清空环境缓存，调用方再重新 SelectTab 让活动标签走一遍懒加载即可。
        public void ResetAfterBrowserCrash(IEnumerable<TabModel> tabs)
        {
            _injected.Clear();
            _languageFilters.Clear();
            foreach (var tab in tabs.ToList())
            {
                FrameworkElement view;
                if (_views.TryGetValue(tab.Id, out view))
                {
                    _host.Children.Remove(view);
                    _views.Remove(tab.Id);
                    var web = view as WebView2;
                    if (web != null)
                    {
                        try { web.Dispose(); }
                        catch (Exception ex) { Diagnostics.Log("dispose after crash: " + ex.Message); }
                    }
                }
                _webviews.Remove(tab.Id);
                tab.MemoryState = TabMemoryState.Discarded;
                tab.Snapshot = null;
                tab.ZoomSite = "";

                if (PlaceholderFactory != null)
                {
                    var placeholder = PlaceholderFactory(tab);
                    if (placeholder != null)
                    {
                        placeholder.Visibility = Visibility.Collapsed;
                        _views[tab.Id] = placeholder;
                        _host.Children.Add(placeholder);
                    }
                }
            }
            _environmentTask = null;   // 下次 Ensure 时重新建环境（旧 browser 进程已经没了）
            Diagnostics.Log("browser process crash: views reset");
        }

        // ---------- 缩放（SPEC 5.6 / P0-11） ----------

        public double GetZoom(TabModel tab) { return tab == null ? ZoomLevels.Default : tab.Zoom; }

        // 读的是 WebView2 控件实际持有的 ZoomFactor，自测用它验证 API 真的被写进去了。
        public double ReadZoom(TabModel tab)
        {
            WebView2 view;
            return _webviews.TryGetValue(tab.Id, out view) && view != null
                ? view.ZoomFactor : double.NaN;
        }

        public void ZoomIn(TabModel tab) { StepZoom(tab, 1); }
        public void ZoomOut(TabModel tab) { StepZoom(tab, -1); }
        public void ZoomReset(TabModel tab) { SetZoom(tab, ZoomLevels.Default); }

        private void StepZoom(TabModel tab, int direction)
        {
            if (tab == null) return;
            var current = tab.Zoom > 0 ? tab.Zoom : ZoomLevels.Default;
            SetZoom(tab, direction > 0 ? ZoomLevels.Next(current) : ZoomLevels.Previous(current));
        }

        public void SetZoom(TabModel tab, double factor)
        {
            if (tab == null) return;
            factor = ZoomLevels.Clamp(factor);
            tab.Zoom = factor;
            WriteZoom(tab, factor);
            var site = ZoomStore.SiteOf(tab.Url);
            if (!string.IsNullOrEmpty(site))
            {
                // 密封标签的缩放只在本次会话生效，不把域名写进 zoom.json。
                if (!tab.IsSecret)
                {
                    if (Math.Abs(factor - ZoomLevels.Default) < 0.0001) ZoomStore.Forget(site);
                    else ZoomStore.Put(site, factor);
                }
                tab.ZoomSite = site;
            }
            ZoomChanged?.Invoke(tab, factor);
        }

        // 换站点（含新建/恢复后的 WebView2）时套用该站点记忆的档位。
        private void ApplyZoomForSite(TabModel tab)
        {
            var site = ZoomStore.SiteOf(tab.Url);
            if (string.IsNullOrEmpty(site)) return;
            if (string.Equals(site, tab.ZoomSite, StringComparison.OrdinalIgnoreCase)) return;
            tab.ZoomSite = site;
            var factor = ZoomStore.Get(site);
            tab.Zoom = factor;
            WriteZoom(tab, factor);
            ZoomChanged?.Invoke(tab, factor);
        }

        private void WriteZoom(TabModel tab, double factor)
        {
            WebView2 view;
            if (!_webviews.TryGetValue(tab.Id, out view) || view == null) return;
            _applyingZoom = true;
            try { view.ZoomFactor = factor; }
            catch (Exception ex) { Diagnostics.Log("set zoom failed: " + ex.Message); }
            finally { _applyingZoom = false; }
        }

        // ---------- 下载落盘路径 ----------

        // 统一落到 DownloadsFolder，重名自动加 (1)/(2)。WebView2 给的默认路径
        // 可能带自己生成的后缀，这里只取文件名，落点由我们决定（便于便携与自测）。
        private static string ChooseDownloadPath(string proposed, CoreWebView2DownloadOperation op)
        {
            string name = null;
            try { name = Path.GetFileName(proposed ?? ""); } catch { }
            if (string.IsNullOrWhiteSpace(name))
            {
                try
                {
                    var uri = new Uri(op.Uri);
                    name = Path.GetFileName(uri.LocalPath);
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(name)) name = "download.bin";
            var invalid = Path.GetInvalidFileNameChars();
            name = new string(name.Select(c => invalid.Contains(c) || c < ' ' ? '_' : c).ToArray()).Trim();
            if (string.IsNullOrEmpty(name)) name = "download.bin";

            try { Directory.CreateDirectory(AppPaths.DownloadsFolder); } catch { }
            var path = Path.Combine(AppPaths.DownloadsFolder, name);
            var counter = 1;
            while (File.Exists(path))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                path = Path.Combine(AppPaths.DownloadsFolder, stem + " (" + counter + ")" + ext);
                counter++;
            }
            return path;
        }

        public void OpenDevTools(TabModel tab)
        {
            var core = Core(tab);
            if (core == null) return;
            tab.HasOpenedDevTools = true;
            try { core.OpenDevToolsWindow(); }
            catch (Exception ex) { Diagnostics.Log("devtools failed: " + ex.Message); }
        }

        // ---------- IMemoryTabOps (SPEC 4.1 三态模型) ----------

        public void SetLow(TabModel tab)
        {
            var core = Core(tab);
            if (core == null) return;
            try
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                tab.MemoryState = TabMemoryState.Low;
            }
            catch (Exception ex) { Diagnostics.Log("set Low failed: " + ex.Message); }
        }

        public void SetNormal(TabModel tab)
        {
            var core = Core(tab);
            if (core == null) return;
            try
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                tab.MemoryState = TabMemoryState.Active;
            }
            catch (Exception ex) { Diagnostics.Log("set Normal failed: " + ex.Message); }
        }

        public void SetVisible(TabModel tab, bool visible)
        {
            FrameworkElement view;
            if (!_views.TryGetValue(tab.Id, out view)) return;
            view.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public async Task<TabSnapshot> CaptureSnapshotAsync(TabModel tab)
        {
            var core = Core(tab);
            var snapshot = new TabSnapshot { Url = tab.Url, Title = tab.Title, Zoom = tab.Zoom };
            if (core == null) return snapshot;

            try
            {
                var raw = await core.ExecuteScriptAsync(SnapshotScript.Capture);
                var inner = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<string>(raw);
                var parsed = TabSnapshot.FromJson(inner);
                if (parsed != null)
                {
                    parsed.Url = string.IsNullOrEmpty(parsed.Url) ? tab.Url : parsed.Url;
                    parsed.Title = string.IsNullOrEmpty(parsed.Title) ? tab.Title : parsed.Title;
                    parsed.Zoom = tab.Zoom;
                    snapshot = parsed;
                }
            }
            catch (Exception ex) { Diagnostics.Log("snapshot failed: " + ex.Message); }

            snapshot.Bytes = SnapshotScript.Measure(snapshot);
            if (SnapshotScript.TooBig(snapshot))
            {
                snapshot.Restorable = false;
                snapshot.Forms.Clear();
                snapshot.Bytes = SnapshotScript.Measure(snapshot);
            }
            return snapshot;
        }

        public void Discard(TabModel tab, TabSnapshot snapshot)
        {
            tab.Snapshot = snapshot;
            FrameworkElement view;
            if (_views.TryGetValue(tab.Id, out view))
            {
                _host.Children.Remove(view);
                _views.Remove(tab.Id);
            }
            WebView2 web;
            if (_webviews.TryGetValue(tab.Id, out web))
            {
                ForgetScripts(tab);
                _webviews.Remove(tab.Id);
                try { web.Dispose(); } catch (Exception ex) { Diagnostics.Log("dispose failed: " + ex.Message); }
            }
            tab.MemoryState = TabMemoryState.Discarded;
            tab.ZoomSite = "";   // 重建 WebView2 时重新套用站点档位

            if (PlaceholderFactory != null)
            {
                var placeholder = PlaceholderFactory(tab);
                if (placeholder != null)
                {
                    placeholder.Visibility = Visibility.Collapsed;
                    _views[tab.Id] = placeholder;
                    _host.Children.Add(placeholder);
                }
            }
        }

        public void Show(TabModel active)
        {
            foreach (var pair in _views)
                pair.Value.Visibility = pair.Key == active.Id ? Visibility.Visible : Visibility.Collapsed;
        }

        public void Remove(TabModel tab)
        {
            FrameworkElement view;
            if (_views.TryGetValue(tab.Id, out view))
            {
                _host.Children.Remove(view);
                _views.Remove(tab.Id);
                var web = view as WebView2;
                if (web != null)
                {
                    ForgetScripts(tab);
                    try { web.Dispose(); } catch { }
                    _webviews.Remove(tab.Id);
                }
            }
        }

        // 退出前把控制器全部释放，别让 msedgewebview2 子进程比窗口活得更久。
        // 可重复调用：自测里先显式释放一次，Application.Shutdown 时再来一次。
        public void DisposeAll()
        {
            foreach (var id in _views.Keys.ToList())
            {
                FrameworkElement view;
                if (!_views.TryGetValue(id, out view)) continue;
                try { _host.Children.Remove(view); } catch { }
                var web = view as WebView2;
                if (web == null) continue;
                try { web.Dispose(); }
                catch (Exception ex) { Diagnostics.Log("dispose on exit failed: " + ex.Message); }
            }
            _views.Clear();
            _webviews.Clear();
            _injected.Clear();
            _languageFilters.Clear();
        }

        public void Back(TabModel tab) { NavigateBuiltIn(tab, true); }
        public void Forward(TabModel tab) { NavigateBuiltIn(tab, false); }
        public void Reload(TabModel tab)
        {
            WebView2 view;
            if (_webviews.TryGetValue(tab.Id, out view) && view.CoreWebView2 != null)
                view.CoreWebView2.Reload();
        }

        // Ctrl+F5：CDP 的 Page.reload(ignoreCache) 才是真硬刷新；
        // CoreWebView2.Reload() 和 F5 一样仍吃缓存。
        public void HardReload(TabModel tab)
        {
            WebView2 view;
            if (!_webviews.TryGetValue(tab.Id, out view) || view.CoreWebView2 == null)
            {
                Reload(tab);
                return;
            }
            HardReloads++;
            var unused = HardReloadAsync(view.CoreWebView2);
        }

        private async Task HardReloadAsync(CoreWebView2 core)
        {
            try
            {
                await core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("hard reload failed: " + ex.Message);
                try { core.Reload(); } catch { }
            }
        }
        public void Stop(TabModel tab)
        {
            WebView2 view;
            if (_webviews.TryGetValue(tab.Id, out view) && view.CoreWebView2 != null)
                view.CoreWebView2.Stop();
        }

        private void NavigateBuiltIn(TabModel tab, bool back)
        {
            WebView2 view;
            if (!_webviews.TryGetValue(tab.Id, out view) || view.CoreWebView2 == null) return;
            if (back && view.CoreWebView2.CanGoBack) view.CoreWebView2.GoBack();
            if (!back && view.CoreWebView2.CanGoForward) view.CoreWebView2.GoForward();
        }

        public void RefreshBackground()
        {
            var color = System.Drawing.ColorTranslator.FromHtml(Theme.IsDark ? "#0E1116" : "#F3F5F9");
            foreach (var view in _webviews.Values)
                view.DefaultBackgroundColor = color;
        }

        public bool HasWebView(TabModel tab) { return _webviews.ContainsKey(tab.Id); }

        // 只读访问器：自测需要拿到内部页实例核对状态，不参与任何运行时决策。
        internal FrameworkElement ViewOf(TabModel tab)
        {
            FrameworkElement view;
            return _views.TryGetValue(tab.Id, out view) ? view : null;
        }

        public void FocusContent(TabModel tab)
        {
            WebView2 view;
            if (_webviews.TryGetValue(tab.Id, out view) && view.Visibility == Visibility.Visible)
            {
                try { view.Focus(); } catch { }
            }
        }

        // FrameId -> tabId。Phase 0 已验证 FrameId 与 CoreWebView2.FrameId 同一套 ID。
        public Dictionary<ulong, string> FrameMap(IEnumerable<TabModel> tabs)
        {
            var map = new Dictionary<ulong, string>();
            foreach (var tab in tabs)
            {
                var core = Core(tab);
                if (core == null) continue;
                try
                {
                    var frameId = core.FrameId;
                    if (!map.ContainsKey(frameId)) map[frameId] = tab.Id;
                }
                catch { }
            }
            return map;
        }

        public int LiveViewCount(IEnumerable<TabModel> tabs)
        {
            var count = 0;
            foreach (var tab in tabs) if (HasLiveView(tab)) count++;
            return count;
        }
    }
}
