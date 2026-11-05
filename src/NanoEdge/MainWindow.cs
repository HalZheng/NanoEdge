using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace NanoEdge
{
    public sealed partial class MainWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly string[] _args;
        private readonly List<TabModel> _tabs = new List<TabModel>();
        private readonly List<TabGroup> _groups = new List<TabGroup>();
        private TabModel _active;
        private BrowserHost _browser;

        private Grid _root, _bodyGrid, _tabHost;
        private Border _toolbarHost, _rail, _statusBar, _popupContent, _memoryBadgeBorder;
        private StackPanel _railPanel, _popupPanel;
        private ScrollViewer _tabStrip;
        private StackPanel _tabStripPanel;
        private TextBox _address;
        private Border _addressBorder;
        private AddressSuggestions _suggestions;
        private DispatcherTimer _suggestTimer, _suggestCloseTimer;
        private bool _suppressSuggest;
        private TextBlock _statusText, _memoryBadge, _statusRight;
        private Popup _verticalPopup;
        private DispatcherTimer _expandTimer, _collapseTimer, _badgeHoverTimer, _badgeCloseTimer;
        private TabModel _dragCandidate;
        private TabModel _secretReturnTab;   // Alt+` 从密封标签切回的那个普通标签
        private bool _immersive;
        private bool _shellReady;
        private string _screenshotPath;
        private bool _noPersist;

        private MemoryGovernor _governor;
        private MemoryPanel _memoryPanel;
        private Toast _toast;

        private const double ChromeHeight = 36;
        private const double RailWidth = 44;
        private const double ExpandedWidth = 220;

        private bool _ipcWired;
        private bool _recovering;

        // 会话 / 窗口位置都是高频变更：合并写，别每敲一次标题就同步落盘。
        private DispatcherTimer _sessionTimer, _settingsTimer;
        private bool _sessionDirty, _settingsDirty;
        private bool _shutdownStarted;

        // 最近关闭：只在内存里，最多 25 条；密封/内部/起始页不记账。
        private sealed class ClosedTab
        {
            public string Url = "";
            public string Title = "";
        }
        private const int ClosedTabLimit = 25;
        private readonly List<ClosedTab> _closedTabs = new List<ClosedTab>();

        public MainWindow(AppSettings settings, string[] args)
        {
            _settings = settings;
            _args = args;
            _screenshotPath = ArgValue("--screenshot");
            _noPersist = !string.IsNullOrEmpty(_screenshotPath) || HasArg("--memtest") ||
                         HasArg("--navtest") || HasArg("--m3ctest") || HasArg("--m4test") ||
                         HasArg("--m5test") || HasArg("--m6test") || HasArg("--m6btest") ||
                         HasArg("--m7test") || HasArg("--m8test");
            if (_noPersist)
            {
                // 自测/截图一律不写用户会话、历史、缩放和下载记录。
                HistoryStore.Enabled = false;
                ZoomStore.Persist = false;
                DownloadManager.Persist = false;
                ScriptStore.Persist = false;
                SettingsStore.Persist = false;
            }

            // 语言必须在 BuildShell 之前定：所有控件构造时就要拿到正确的文案。
            var langArg = ArgValue("--lang");
            if (!string.IsNullOrEmpty(langArg)) _settings.Language = langArg;
            else if (IsTestRun()) _settings.Language = "zh-CN";   // 自测断言按中文写死
            L.Apply(_settings.Language);
            ApplyPreviewArgs();

            Title = "NanoEdge";
            // 窗口/任务栏图标与 exe 图标同源；读不到就退回系统默认，不让启动失败。
            try
            {
                using (var iconStream = typeof(MainWindow).Assembly
                           .GetManifestResourceStream("NanoEdge.nanoedge.ico"))
                {
                    if (iconStream != null) Icon = BitmapFrame.Create(iconStream);
                }
            }
            catch (Exception ex) { Diagnostics.Log("window icon failed: " + ex.Message); }
            Width = settings.WindowWidth > 400 ? settings.WindowWidth : 1280;
            Height = settings.WindowHeight > 300 ? settings.WindowHeight : 800;
            if (!double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop))
            {
                Left = settings.WindowLeft;
                Top = settings.WindowTop;
            }
            WindowStartupLocation = double.IsNaN(settings.WindowLeft)
                ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
            if (settings.WindowMaximized) WindowState = WindowState.Maximized;
            // 截图模式固定 Normal + 常用尺寸：会话里记着「最大化」时，超宽屏会截出
            // 3454×1454，缩到聊天窗口里字全糊了。
            if (!string.IsNullOrEmpty(_screenshotPath))
            {
                WindowState = WindowState.Normal;
                Width = 1200;
                Height = 800;
            }
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            SnapsToDevicePixels = true;

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = false
            });
            this.SetResourceReference(BackgroundProperty, "CanvasBrush");

            BuildShell();
            RestoreSession();
            ApplyLayout();

            System.Windows.Interop.ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
            Closed += (s, e) =>
            {
                System.Windows.Interop.ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;
                Ipc.Stop();
                Ipc.Received -= OnIpcReceived;
            };

            Loaded += MainWindow_Loaded;
            Closing += (s, e) =>
            {
                // 关窗前把 WebView2 控制器全部释放：否则内核子进程可能比窗口
                // 活得更久，退出瞬间的下载/写盘会留下半截文件。
                // 这里全是同步操作，不需要 Cancel 再 Close 的两段式关闭。
                if (_shutdownStarted) return;
                _shutdownStarted = true;
                try
                {
                    if (_sessionTimer != null) _sessionTimer.Stop();
                    if (_settingsTimer != null) _settingsTimer.Stop();
                    WriteSessionNow();
                    HistoryStore.Flush();
                    ZoomStore.Flush();
                    DownloadManager.Flush();
                    ScriptStore.Flush();   // 最后运行时间只在内存里累加，退出时一并落盘
                    FlushSettings();
                    if (_governor != null) _governor.Stop();
                    if (_browser != null) _browser.DisposeAll();
                }
                catch (Exception ex) { Diagnostics.Log("shutdown failed: " + ex.Message); }
            };
            SizeChanged += (s, e) => SyncPopupSize();
            LocationChanged += (s, e) => SaveWindowBounds();
            Activated += (s, e) => { if (_governor != null) _governor.NotifyActivated(); };
            Deactivated += (s, e) => { if (_governor != null) _governor.NotifyDeactivated(); };
            StateChanged += (s, e) =>
            {
                if (_governor != null) _governor.NotifyMinimized(WindowState == WindowState.Minimized);
            };
        }

        private bool HasArg(string name)
        {
            foreach (var arg in _args)
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private bool IsTestRun()
        {
            return HasArg("--memtest") || HasArg("--navtest") || HasArg("--m3ctest") ||
                   HasArg("--m4test") || HasArg("--m5test") || HasArg("--m6test") ||
                   HasArg("--m6btest") || HasArg("--m7test") || HasArg("--m8test");
        }

        private void ApplyPreviewArgs()
        {
            var layout = ArgValue("--layout");
            if (layout == "vertical") _settings.Layout = LayoutMode.Vertical;
            else if (layout == "standard") _settings.Layout = LayoutMode.Standard;
            else if (layout == "compact") _settings.Layout = LayoutMode.Compact;
            if (ArgValue("--rail") == "expanded") _settings.VerticalExpanded = true;

            var theme = ArgValue("--theme");
            if (theme == "light" || theme == "dark")
            {
                _settings.Theme = theme;
                Theme.Apply(theme);
            }
        }

        private string ArgValue(string name)
        {
            foreach (var arg in _args)
                if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return arg.Substring(name.Length + 1).Trim('"');
            return null;
        }

        // ---------- shell ----------

        private void BuildShell()
        {
            _root = new Grid();
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _toolbarHost = new Border();
            _toolbarHost.SetResourceReference(Border.BackgroundProperty, "ChromeBrush");
            Grid.SetRow(_toolbarHost, 0);
            _root.Children.Add(_toolbarHost);

            _bodyGrid = new Grid();
            _bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
            _bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(_bodyGrid, 1);

            _rail = new Border { Width = RailWidth, ClipToBounds = true };
            _rail.SetResourceReference(Border.BackgroundProperty, "ChromeBrush");
            _rail.BorderThickness = new Thickness(0, 0, 1, 0);
            _rail.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            _railPanel = new StackPanel { Orientation = Orientation.Vertical };
            var railScroller = new ScrollViewer
            {
                Content = _railPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            _rail.Child = railScroller;
            Grid.SetColumn(_rail, 0);
            _bodyGrid.Children.Add(_rail);

            _tabHost = new Grid { ClipToBounds = true };
            _tabHost.SetResourceReference(Grid.BackgroundProperty, "CanvasBrush");
            Grid.SetColumn(_tabHost, 1);
            _bodyGrid.Children.Add(_tabHost);
            _root.Children.Add(_bodyGrid);

            _statusBar = new Border { Height = 22, Padding = new Thickness(10, 0, 10, 0) };
            _statusBar.SetResourceReference(Border.BackgroundProperty, "ChromeBrush");
            _statusBar.BorderThickness = new Thickness(0, 1, 0, 0);
            _statusBar.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            _statusText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            _statusText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _statusRight = new TextBlock
            {
                Text = "NanoEdge " + Program.Version,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            _statusRight.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var statusGrid = new Grid();
            statusGrid.Children.Add(_statusText);
            statusGrid.Children.Add(_statusRight);
            _statusBar.Child = statusGrid;
            Grid.SetRow(_statusBar, 2);
            _root.Children.Add(_statusBar);

            Content = _root;

            _browser = new BrowserHost(_tabHost, _settings);
            _browser.TitleChanged += t =>
            {
                HistoryStore.UpdateTitle(t.Url, t.Title);
                RebuildTabStrip();
                SaveSession();
            };
            _browser.UrlChanged += t =>
            {
                if (ReferenceEquals(t, _active)) SetAddress(AddressFor(t));
                SaveSession();
            };
            _browser.OpenInNewTab += url => NewTab(url);
            _browser.PlaceholderFactory = MakePlaceholder;
            _browser.NavigationDone += OnNavigationDone;
            _browser.RestoreCompleted += OnRestoreCompleted;
            _browser.ZoomChanged += (tab, factor) => { if (ReferenceEquals(tab, _active)) UpdateStatus(); };
            _browser.FaviconChanged += tab => RebuildTabStrip();

            _governor = new MemoryGovernor(() => _active, () => _tabs, _browser, _settings.Memory);
            _governor.Changed += UpdateMemoryBadge;
            _toast = new Toast(_tabHost);

            // 标题/URL 变化很密集：150ms 合并成一次 session 写；窗口位置 500ms。
            _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _sessionTimer.Tick += (s, e) => { _sessionTimer.Stop(); WriteSessionNow(); };
            _settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _settingsTimer.Tick += (s, e) => { _settingsTimer.Stop(); FlushSettings(); };

            // 用户脚本的 GM_notification / GM_openInTab / GM_setClipboard 在这里接到真实 UI。
            _browser.Scripts.Notify += text => _toast.Show(text);
            _browser.Scripts.OpenTab += url => NewTab(url);
            _browser.Scripts.Clipboard += text =>
            {
                try
                {
                    if (string.IsNullOrEmpty(text)) Clipboard.Clear();
                    else Clipboard.SetText(text);
                }
                catch (Exception ex) { Diagnostics.Log("clipboard write failed: " + ex.Message); }
            };

            // 崩溃恢复：单个渲染进程崩溃自动重载一次；整个内核退出则重建全部标签。
            _browser.ProcessFailed += (tab, message, offerReload) =>
            {
                if (!ReferenceEquals(tab, _active)) return;
                if (offerReload) _toast.Show(message, L.T("重新加载"), () => _browser.Reload(tab));
                else _toast.Show(message);
            };
            _browser.BrowserExited += RecoverFromBrowserCrash;

            BuildVerticalPopup();
        }

        private FrameworkElement MakePlaceholder(TabModel tab)
        {
            var placeholder = new DiscardedPlaceholder(tab);
            placeholder.RestoreRequested += () =>
            {
                if (ReferenceEquals(tab, _active)) RestoreTab(tab);
            };
            return placeholder;
        }

        private void OnRestoreCompleted(TabModel tab, bool applied)
        {
            if (!applied) return;
            if (!ReferenceEquals(tab, _active)) return;
            _toast.Show(L.T("已恢复页面状态"), L.T("重新加载"), () => _browser.Reload(tab));
        }

        private void EnsureMemoryPanel()
        {
            // 面板是懒创建的：不打开就不建控件、不采样（SPEC 4.9 采样成本）。
            if (_memoryBadgeBorder != null && _memoryPanel != null)
                _memoryPanel.SetAnchor(_memoryBadgeBorder);
        }

        private void OpenMemoryPanel(bool pin)
        {
            if (_memoryBadgeBorder == null || _governor == null) return;
            if (_memoryPanel == null)
            {
                _memoryPanel = new MemoryPanel(_memoryBadgeBorder, _governor, () => _tabs, () => _active,
                    tab => _governor.ExemptionReason(tab), SelectTab, ToggleResident,
                    message => _toast.Show(message));
                _memoryPanel.CursorEntered += () => { if (_badgeCloseTimer != null) _badgeCloseTimer.Stop(); };
                _memoryPanel.CursorLeft += () =>
                {
                    if (_badgeCloseTimer != null && !_memoryPanel.IsPinned) _badgeCloseTimer.Start();
                };
            }
            _memoryPanel.SetAnchor(_memoryBadgeBorder);
            if (pin) _memoryPanel.IsPinned = true;
            _memoryPanel.Open();
        }

        private void BuildVerticalPopup()
        {
            _expandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _expandTimer.Tick += (s, e) => { _expandTimer.Stop(); OpenVerticalPopup(); };
            _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _collapseTimer.Tick += (s, e) => { _collapseTimer.Stop(); _verticalPopup.IsOpen = false; };

            _suggestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _suggestTimer.Tick += (s, e) => { _suggestTimer.Stop(); UpdateSuggestions(); };
            _suggestCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _suggestCloseTimer.Tick += (s, e) => { _suggestCloseTimer.Stop(); CloseSuggestions(); };

            _badgeHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _badgeHoverTimer.Tick += (s, e) =>
            {
                _badgeHoverTimer.Stop();
                if (_memoryPanel == null || !_memoryPanel.IsOpen) OpenMemoryPanel(false);
            };
            _badgeCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            _badgeCloseTimer.Tick += (s, e) =>
            {
                _badgeCloseTimer.Stop();
                if (_memoryPanel != null && !_memoryPanel.IsPinned) _memoryPanel.Close();
            };

            _popupPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6) };
            _popupContent = new Border
            {
                Width = ExpandedWidth,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = new ScrollViewer
                {
                    Content = _popupPanel,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                }
            };
            _popupContent.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            _popupContent.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

            _verticalPopup = new Popup
            {
                Placement = PlacementMode.Relative,
                PlacementTarget = _rail,
                StaysOpen = true,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Focusable = false,
                Child = _popupContent
            };
            _root.Children.Add(_verticalPopup);

            _rail.MouseEnter += (s, e) =>
            {
                if (_settings.VerticalExpanded) return;
                _collapseTimer.Stop();
                _expandTimer.Start();
            };
            _rail.MouseLeave += (s, e) => ScheduleCollapse();
            _popupContent.MouseEnter += (s, e) => _collapseTimer.Stop();
            _popupContent.MouseLeave += (s, e) => ScheduleCollapse();
        }

        private void ScheduleCollapse()
        {
            _expandTimer.Stop();
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }

        private void OpenVerticalPopup()
        {
            if (_settings.Layout != LayoutMode.Vertical || _settings.VerticalExpanded) return;
            RebuildPopup();
            SyncPopupSize();
            _verticalPopup.IsOpen = true;
        }

        private void SyncPopupSize()
        {
            if (_popupContent == null) return;
            _popupContent.Height = Math.Max(120, _bodyGrid.ActualHeight);
        }

        // ---------- session ----------

        private void RestoreSession()
        {
            // 自测需要确定的初始状态，不加载用户会话。
            if (HasArg("--memtest") || HasArg("--navtest") || HasArg("--m3ctest") ||
                HasArg("--m4test") || HasArg("--m5test") || HasArg("--m6test") ||
                HasArg("--m6btest") || HasArg("--m7test") || HasArg("--m8test"))
            {
                NewTab();
                return;
            }

            var session = SessionStore.Load();
            _groups.Clear();
            if (session.Groups != null) _groups.AddRange(session.Groups);

            if (session.Tabs == null || session.Tabs.Count == 0)
            {
                NewTab();
                return;
            }

            foreach (var tab in session.Tabs)
            {
                _tabs.Add(tab);
                if (!string.IsNullOrEmpty(tab.InternalPage) ||
                    (tab.Url != null && tab.Url.StartsWith("nano://", StringComparison.OrdinalIgnoreCase)))
                {
                    // 内部页是纯 WPF，恢复时直接建视图，不占 renderer。
                    tab.MemoryState = TabMemoryState.Active;
                    if (string.IsNullOrEmpty(tab.InternalPage) && !string.IsNullOrEmpty(tab.Url))
                        tab.InternalPage = InternalPages.Normalize(tab.Url).Substring("nano://".Length);
                    if (tab.InternalPage == "start") tab.InternalPage = "";
                    _browser.RegisterStartPage(tab, BuildInternalView(tab));
                }
                else if (tab.IsStartPage || string.IsNullOrEmpty(tab.Url))
                {
                    tab.MemoryState = TabMemoryState.Active;
                    var start = new StartPage(_settings);
                    start.NavigateRequested += input => Navigate(tab, input);
                    _browser.RegisterStartPage(tab, start);
                }
                else
                {
                    // 恢复的标签先按"已丢弃"处理：零 WebView2，选中时才真正加载（SPEC 4.5/7.1）。
                    tab.MemoryState = TabMemoryState.Discarded;
                    _browser.RegisterStartPage(tab, MakePlaceholder(tab));
                }
            }
            SnapshotStore.Prune(_tabs.Select(t => t.Id));

            _active = _tabs.FirstOrDefault(t => t.Id == session.ActiveTabId) ?? _tabs[0];
            SelectTab(_active);
        }

        // 会话写盘做 150ms 合并：标题/URL 变化很密集，逐次全量写会卡 UI。
        // 热路径统一走这里；退出时用 WriteSessionNow 立刻落盘。
        private void SaveSession()
        {
            if (_noPersist) return;
            _sessionDirty = true;
            if (_sessionTimer == null) { WriteSessionNow(); return; }
            _sessionTimer.Stop();
            _sessionTimer.Start();
        }

        private void WriteSessionNow()
        {
            if (_noPersist || !_sessionDirty) return;
            _sessionDirty = false;
            SessionStore.Save(BuildSession());
            SaveWindowBounds();
        }

        // 密封标签不进会话恢复（7.4）：URL、标题、活动标签都不落盘。
        private SessionData BuildSession()
        {
            return new SessionData
            {
                Tabs = _tabs.Where(t => !t.IsSecret).ToList(),
                Groups = _groups.ToList(),
                ActiveTabId = _active == null || _active.IsSecret ? "" : _active.Id
            };
        }

        private void SaveWindowBounds()
        {
            if (_settings == null || _noPersist) return;
            _settings.WindowWidth = ActualWidth;
            _settings.WindowHeight = ActualHeight;
            if (WindowState == WindowState.Normal)
            {
                _settings.WindowLeft = Left;
                _settings.WindowTop = Top;
            }
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            ScheduleSettingsSave();
        }

        // 拖动窗口时 LocationChanged 是连续事件，不能每次都写 settings.json。
        private void ScheduleSettingsSave()
        {
            if (_settings == null) return;
            _settingsDirty = true;
            // 退出路径里定时器已经停了，别再把它启动起来；Closing 会统一 flush。
            if (_shutdownStarted || _settingsTimer == null) return;
            _settingsTimer.Stop();
            _settingsTimer.Start();
        }

        private void FlushSettings()
        {
            if (_settings == null || !_settingsDirty) return;
            _settingsDirty = false;
            SettingsStore.Save(_settings);
        }

        // ---------- layout ----------

        private void ApplyLayout()
        {
            _toolbarHost.Child = BuildToolbar();
            var vertical = _settings.Layout == LayoutMode.Vertical;
            var expanded = vertical && _settings.VerticalExpanded;
            var railWidth = expanded ? ExpandedWidth : RailWidth;
            _rail.Width = railWidth;
            _rail.Visibility = vertical && !_immersive ? Visibility.Visible : Visibility.Collapsed;
            _bodyGrid.ColumnDefinitions[0].Width = new GridLength(vertical && !_immersive ? railWidth : 0);
            if (!vertical || expanded) _verticalPopup.IsOpen = false;
            _statusBar.Visibility = _settings.ShowStatusBar && !_immersive ? Visibility.Visible : Visibility.Collapsed;
            _toolbarHost.Visibility = _immersive ? Visibility.Collapsed : Visibility.Visible;
            if (_immersive && _memoryPanel != null) _memoryPanel.Close();
            RebuildTabStrip();
            CloseSuggestions();
            EnsureMemoryPanel();
            UpdateMemoryBadge();
            UpdateStatus();
            // BuildToolbar 会重建 _address，不补这一行的话切主题/布局后地址栏会变空。
            SetAddress(AddressFor(_active));
        }

        private FrameworkElement BuildToolbar()
        {
            var grid = new Grid();
            if (_settings.Layout == LayoutMode.Standard)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });

                _tabStrip = MakeTabStrip();
                Grid.SetRow(_tabStrip, 0);
                grid.Children.Add(_tabStrip);
                var bar = BuildNavigationRow();
                Grid.SetRow(bar, 1);
                grid.Children.Add(bar);
            }
            else
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ChromeHeight) });
                var bar = BuildNavigationRow();
                Grid.SetRow(bar, 0);
                grid.Children.Add(bar);
            }
            return grid;
        }

        private FrameworkElement BuildNavigationRow()
        {
            var bar = new Grid { Height = _settings.Layout == LayoutMode.Standard ? 40 : ChromeHeight };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });   // layout
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });  // nav
            if (_settings.Layout == LayoutMode.Compact)
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });  // new tab
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });  // theme
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });  // memory
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });  // menu
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) }); // window

            var column = 0;
            Button layoutButton = null;
            layoutButton = IconButton("☰", L.T("切换布局"), () => ShowLayoutMenu(layoutButton));
            Grid.SetColumn(layoutButton, column++);
            bar.Children.Add(layoutButton);

            var nav = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            nav.Children.Add(IconButton("←", L.T("后退 (Alt+←)"), () => _browser.Back(_active)));
            nav.Children.Add(IconButton("→", L.T("前进 (Alt+→)"), () => _browser.Forward(_active)));
            nav.Children.Add(IconButton("⟳", L.T("刷新 (F5)"), () => _browser.Reload(_active)));
            Grid.SetColumn(nav, column++);
            bar.Children.Add(nav);

            if (_settings.Layout == LayoutMode.Compact)
            {
                _tabStrip = MakeTabStrip();
                Grid.SetColumn(_tabStrip, column++);
                bar.Children.Add(_tabStrip);
            }

            var addressBorder = new Border
            {
                Height = 26,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            addressBorder.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            addressBorder.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");
            _address = new TextBox
            {
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Margin = new Thickness(9, 0, 9, 0)
            };
            _address.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
            _address.SetResourceReference(TextBox.CaretBrushProperty, "AccentBrush");
            _address.GotFocus += (s, e) => _address.SelectAll();
            _address.PreviewKeyDown += OnAddressKeyDown;
            _address.TextChanged += (s, e) =>
            {
                if (_suppressSuggest) return;
                if (_suggestTimer == null) return;
                _suggestTimer.Stop();
                _suggestTimer.Start();
            };
            _address.LostFocus += (s, e) =>
            {
                if (_suggestCloseTimer != null) _suggestCloseTimer.Start();
            };
            addressBorder.Child = _address;
            _addressBorder = addressBorder;

            if (_suggestions != null) _suggestions.SetAnchor(_addressBorder ?? (FrameworkElement)_address);
            Grid.SetColumn(addressBorder, column++);
            bar.Children.Add(addressBorder);

            bar.Children.Add(At(IconButton("+", L.T("新建标签 (Ctrl+T)"), () => NewTab()), column++));
            bar.Children.Add(At(IconButton("◐", L.T("切换主题"), ToggleTheme), column++));

            _memoryBadge = new TextBlock
            {
                Text = "— MB",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _memoryBadge.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

            _memoryBadgeBorder = new Border
            {
                Height = 22,
                CornerRadius = new CornerRadius(5),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = L.T("内存占用（工作集口径）· 悬停查看明细"),
                Child = _memoryBadge
            };
            _memoryBadgeBorder.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            _memoryBadgeBorder.MouseEnter += (s, e) =>
            {
                if (_badgeHoverTimer == null) return;
                if (_badgeCloseTimer != null) _badgeCloseTimer.Stop();
                _badgeHoverTimer.Start();
            };
            _memoryBadgeBorder.MouseLeave += (s, e) =>
            {
                if (_badgeHoverTimer != null) _badgeHoverTimer.Stop();
                if (_badgeCloseTimer != null && _memoryPanel != null && !_memoryPanel.IsPinned)
                    _badgeCloseTimer.Start();
            };
            _memoryBadgeBorder.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (_memoryPanel != null && _memoryPanel.IsOpen && _memoryPanel.IsPinned)
                {
                    _memoryPanel.Close();
                    return;
                }
                OpenMemoryPanel(true);
            };
            bar.Children.Add(At(_memoryBadgeBorder, column++));

            Button menuButton = null;
            menuButton = IconButton("⋯", L.T("菜单"), () => ShowMainMenu(menuButton));
            bar.Children.Add(At(menuButton, column++));

            var windowButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            windowButtons.Children.Add(IconButton("─", L.T("最小化"), () => WindowState = WindowState.Minimized));
            windowButtons.Children.Add(IconButton("□", L.T("最大化"), ToggleMaximize));
            var close = IconButton("×", L.T("关闭"), Close);
            close.MouseEnter += (s, e) => close.SetResourceReference(Button.ForegroundProperty, "DangerBrush");
            close.MouseLeave += (s, e) => close.SetResourceReference(Button.ForegroundProperty, "TextBrush");
            windowButtons.Children.Add(close);
            bar.Children.Add(At(windowButtons, column));

            bar.PreviewMouseLeftButtonDown += (s, e) =>
            {
                var source = e.OriginalSource as DependencyObject;
                if (source == null) return;
                if (source is TextBox || FindParent<Button>(source) != null ||
                    FindParent<ScrollViewer>(source) != null) return;
                try { DragMove(); } catch { }
            };
            bar.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) ToggleMaximize(); };
            return bar;
        }

        // ---------- address bar (SPEC 7.2) ----------

        private void SetAddress(string text)
        {
            if (_address == null) return;
            _suppressSuggest = true;
            _address.Text = text ?? "";
            _suppressSuggest = false;
            CloseSuggestions();
        }

        private void OnAddressKeyDown(object sender, KeyEventArgs e)
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            var alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

            if (e.Key == Key.Down)
            {
                if (_suggestions != null && _suggestions.IsOpen) _suggestions.Move(1);
                else UpdateSuggestions();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Up)
            {
                if (_suggestions != null && _suggestions.IsOpen) _suggestions.Move(-1);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                if (_suggestions != null && _suggestions.IsOpen)
                {
                    _suggestions.Close();
                    e.Handled = true;
                }
                return;
            }
            if (e.Key == Key.Tab)
            {
                if (_suggestions != null && _suggestions.IsOpen && _suggestions.Selected != null)
                {
                    _suppressSuggest = true;
                    _address.Text = _suggestions.Selected.Url;
                    _address.CaretIndex = _address.Text.Length;
                    _suppressSuggest = false;
                    _suggestions.Close();
                    e.Handled = true;
                }
                return;
            }
            if (e.Key == Key.Delete)
            {
                // SPEC 7.2：Delete 删除单条历史（只有来自历史的候选可删）。
                var selected = _suggestions == null ? null : _suggestions.Selected;
                if (_suggestions != null && _suggestions.IsOpen && selected != null &&
                    selected.Kind == SuggestionKind.History)
                {
                    HistoryStore.Remove(selected.Url);
                    UpdateSuggestions();
                    e.Handled = true;
                }
                return;
            }
            if (e.Key != Key.Enter) return;

            e.Handled = true;
            if (_suggestions != null && _suggestions.IsOpen && _suggestions.Selected != null)
            {
                _suggestions.ChooseSelected(alt);
                return;
            }

            CloseSuggestions();
            var input = ctrl ? UrlParser.CompleteDotCom(_address.Text) : _address.Text;
            if (string.IsNullOrWhiteSpace(input)) return;
            if (alt) { NewTab(input); return; }
            Navigate(_active, input);
            _browser.FocusContent(_active);
        }

        // 自测用：绕过"地址栏必须聚焦"的前置条件，直接把下拉打开。
        private void ShowSuggestionsForTest(string text)
        {
            SetAddress(text);
            EnsureSuggestions();
            var items = SuggestionEngine.Query(text, _tabs, 8);
            _suggestions.Update(text, items);
            _suggestions.Visual.UpdateLayout();
        }

        private void UpdateSuggestions()
        {
            if (_address == null || !_address.IsKeyboardFocusWithin)
            {
                CloseSuggestions();
                return;
            }
            var text = _address.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                CloseSuggestions();
                return;
            }

            var items = SuggestionEngine.Query(text, _tabs, 8);
            if (items.Count == 0)
            {
                CloseSuggestions();
                return;
            }

            EnsureSuggestions();
            _suggestions.Update(text, items);
        }

        private void EnsureSuggestions()
        {
            var anchor = (FrameworkElement)_addressBorder ?? _address;
            if (_suggestions == null) _suggestions = new AddressSuggestions(anchor, OnSuggestionChosen);
            else _suggestions.SetAnchor(anchor);
        }

        private void CloseSuggestions()
        {
            if (_suggestions != null) _suggestions.Close();
        }

        private void OnSuggestionChosen(Suggestion item, bool newTab)
        {
            if (item == null) return;
            if (newTab) { NewTab(item.Url); return; }
            Navigate(_active, item.Url);
            _browser.FocusContent(_active);
        }

        private void OnNavigationDone(TabModel tab, string url, bool success)
        {
            if (success)
            {
                // 重定向 / 同址刷新会在几秒内连发两次 NavigationCompleted，别把访问次数灌水。
                var same = string.Equals(tab.LastRecordedUrl, url, StringComparison.OrdinalIgnoreCase) &&
                           (DateTime.UtcNow - tab.LastRecordedUtc).TotalSeconds < 2;
                tab.LastRecordedUrl = url;
                tab.LastRecordedUtc = DateTime.UtcNow;
                // 密封标签不进历史（7.4）；标题更新也走 HistoryStore.UpdateTitle 的 null 兜底。
                if (!tab.IsSecret && !same) HistoryStore.Record(url, tab.Title);
                return;
            }
            if (!ReferenceEquals(tab, _active)) return;
            _toast.Show(tab.IsSecret ? L.T("无法打开页面") : L.T("无法打开 ") + HostLabel(url), L.T("重试"), () => _browser.Reload(tab));
        }

        private static string HostLabel(string url)
        {
            try
            {
                var uri = new Uri(url);
                if (!string.IsNullOrEmpty(uri.Host)) return uri.Host;
            }
            catch { }
            return url != null && url.Length > 60 ? url.Substring(0, 59) + "…" : url;
        }

        private static FrameworkElement At(FrameworkElement element, int column)
        {
            Grid.SetColumn(element, column);
            return element;
        }

        private Button IconButton(string glyph, string tooltip, Action action)
        {
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = glyph,
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Width = 30,
                Height = 30,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent)
            };
            ((TextBlock)button.Content).SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            button.Click += (s, e) => action();
            return button;
        }

        private static T FindParent<T>(DependencyObject source) where T : DependencyObject
        {
            while (source != null)
            {
                if (source is T) return (T)source;
                source = VisualTreeHelper.GetParent(source);
            }
            return null;
        }

        private ScrollViewer MakeTabStrip()
        {
            _tabStripPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var scroller = new ScrollViewer
            {
                Content = _tabStripPanel,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0)
            };
            return scroller;
        }

        // ---------- tab rendering ----------

        private void RebuildTabStrip()
        {
            if (_tabStripPanel != null)
            {
                _tabStripPanel.Children.Clear();
                foreach (var tab in VisibleTabs())
                    _tabStripPanel.Children.Add(MakeTabChip(tab, true, true, 110, 200));
            }

            _railPanel.Children.Clear();
            var expanded = _settings.Layout == LayoutMode.Vertical && _settings.VerticalExpanded;
            if (expanded) _railPanel.Children.Add(MakeHeaderRow());
            foreach (var group in _groups)
                _railPanel.Children.Add(MakeGroupHeader(group, expanded));
            foreach (var tab in VisibleTabs())
                _railPanel.Children.Add(MakeTabChip(tab, expanded, expanded,
                    expanded ? ExpandedWidth - 24 : 36, expanded ? ExpandedWidth - 24 : 36));

            RebuildPopup();
            UpdateStatus();
        }

        private void RebuildPopup()
        {
            if (_popupPanel == null) return;
            _popupPanel.Children.Clear();
            _popupPanel.Children.Add(MakeHeaderRow());
            foreach (var group in _groups)
                _popupPanel.Children.Add(MakeGroupHeader(group, true));
            foreach (var tab in VisibleTabs())
                _popupPanel.Children.Add(MakeTabChip(tab, true, true, ExpandedWidth - 24, ExpandedWidth - 24));
        }

        private IEnumerable<TabModel> VisibleTabs()
        {
            foreach (var tab in _tabs.Where(t => string.IsNullOrEmpty(t.GroupId)))
                yield return tab;
            foreach (var group in _groups)
            {
                if (group.IsCollapsed) continue;
                foreach (var tab in _tabs.Where(t => t.GroupId == group.Id))
                    yield return tab;
            }
        }

        private FrameworkElement MakeHeaderRow()
        {
            var row = new Grid { Height = 30, Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = L.T("标签页"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            row.Children.Add(label);
            var add = IconButton("+", L.T("新建标签"), () => NewTab());
            Grid.SetColumn(add, 1);
            row.Children.Add(add);
            return row;
        }

        private FrameworkElement MakeGroupHeader(TabGroup group, bool withName)
        {
            var border = new Border
            {
                Height = 26,
                Margin = new Thickness(0, 2, 0, 2),
                CornerRadius = new CornerRadius(5),
                Cursor = Cursors.Hand,
                Tag = "group"
            };
            border.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 7, 0) };
            try { dot.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(group.Color)); }
            catch { dot.Background = Brushes.Gray; }
            panel.Children.Add(dot);
            if (withName)
            {
                var name = new TextBlock
                {
                    Text = (group.IsCollapsed ? "▸ " : "▾ ") + group.Name,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                panel.Children.Add(name);
                var count = new TextBlock
                {
                    Text = "  " + _tabs.Count(t => t.GroupId == group.Id),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                count.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                panel.Children.Add(count);
            }
            border.Child = panel;
            border.MouseLeftButtonUp += (s, e) =>
            {
                group.IsCollapsed = !group.IsCollapsed;
                RebuildTabStrip();
                SaveSession();
            };
            return border;
        }

        // 有 favicon 就用图标，没有就退回首字母色块（SPEC 7.8 的降级口径）。
        private FrameworkElement MakeTabBadge(TabModel tab, double size)
        {
            var badge = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(5),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            if (tab.Favicon != null)
            {
                var image = new Image
                {
                    Source = tab.Favicon,
                    Width = size - 2,
                    Height = size - 2,
                    Stretch = Stretch.Uniform
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                badge.Child = image;
                return badge;
            }

            badge.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            var initial = string.IsNullOrEmpty(tab.DisplayTitle) ? "N" : tab.DisplayTitle.Substring(0, 1).ToUpperInvariant();
            var letter = new TextBlock
            {
                Text = initial,
                FontSize = Math.Max(9, size * 0.55),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            letter.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            badge.Child = letter;
            return badge;
        }

        private FrameworkElement MakeTabChip(TabModel tab, bool showTitle, bool showClose, double minWidth, double maxWidth)
        {
            var active = ReferenceEquals(tab, _active);
            var border = new Border
            {
                Height = 30,
                MinWidth = minWidth,
                MaxWidth = maxWidth,
                Margin = new Thickness(2, 2, 2, 2),
                CornerRadius = new CornerRadius(6),
                Cursor = Cursors.Hand,
                Tag = tab,
                AllowDrop = true
            };
            border.SetResourceReference(Border.BackgroundProperty, active ? "SurfaceBrush" : "TabIdleBrush");
            if (active)
            {
                border.BorderThickness = new Thickness(0, 0, 0, 2);
                border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            if (showTitle)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (showClose)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });

            grid.Children.Add(MakeTabBadge(tab, 18));

            if (showTitle)
            {
                var title = new TextBlock
                {
                    Text = tab.DisplayTitle,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = tab.IsSecret ? "" : tab.Url
                };
                title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                Grid.SetColumn(title, 1);
                grid.Children.Add(title);
            }

            if (showClose)
            {
                var close = IconButton("×", L.T("关闭标签"), () => CloseTab(tab));
                close.Width = 20;
                close.Height = 20;
                Grid.SetColumn(close, showTitle ? 2 : 1);
                grid.Children.Add(close);
            }
            border.Child = grid;

            // 中键关闭标签：和主流浏览器一致。
            border.MouseDown += (s, e) =>
            {
                if (e.ChangedButton != MouseButton.Middle) return;
                e.Handled = true;
                CloseTab(tab);
            };

            border.MouseLeftButtonUp += (s, e) =>
            {
                if (_dragCandidate != null) return;
                SelectTab(tab);
            };
            border.MouseEnter += (s, e) =>
            {
                if (!active) border.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            };
            border.MouseLeave += (s, e) =>
            {
                if (!active) border.SetResourceReference(Border.BackgroundProperty, "TabIdleBrush");
            };
            border.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                ShowTabMenu(tab, border);
            };

            // WPF drag & drop: works both in the horizontal strip and the vertical popup.
            border.PreviewMouseLeftButtonDown += (s, e) => _dragCandidate = tab;
            border.MouseMove += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed && _dragCandidate == tab)
                {
                    _dragCandidate = null;
                    DragDrop.DoDragDrop(border, tab, DragDropEffects.Move);
                }
            };
            border.MouseLeftButtonUp += (s, e) => _dragCandidate = null;
            border.DragOver += (s, e) => e.Effects = DragDropEffects.Move;
            border.Drop += (s, e) =>
            {
                var source = e.Data.GetData(typeof(TabModel)) as TabModel;
                if (source != null && source != tab) ReorderTabs(source, tab);
                e.Handled = true;
            };
            return border;
        }

        private void ReorderTabs(TabModel source, TabModel target)
        {
            var from = _tabs.IndexOf(source);
            if (from < 0) return;
            _tabs.RemoveAt(from);
            var to = _tabs.IndexOf(target);
            _tabs.Insert(to < 0 ? _tabs.Count : to, source);
            source.GroupId = target.GroupId;
            RebuildTabStrip();
            SaveSession();
        }

        private void ShowTabMenu(TabModel tab, FrameworkElement target)
        {
            var menu = MakeMenu();
            menu.Items.Add(MakeMenuItem(tab.IsPinned ? L.T("取消固定") : L.T("固定标签"), () =>
            {
                tab.IsPinned = !tab.IsPinned;
                RebuildTabStrip();
                SaveSession();
            }));
            menu.Items.Add(MakeMenuItem(tab.Resident ? L.T("取消常驻") : L.T("标记为常驻"), () => ToggleResident(tab)));
            menu.Items.Add(MakeMenuItem(L.T("立即释放此标签内存"), () => ReleaseTab(tab)));
            menu.Items.Add(MakeMenuItem(L.T("新建分组并移入"), () =>
            {
                var group = new TabGroup { Name = L.T("分组 ") + (_groups.Count + 1) };
                _groups.Add(group);
                tab.GroupId = group.Id;
                RebuildTabStrip();
                SaveSession();
            }));
            if (!string.IsNullOrEmpty(tab.GroupId))
                menu.Items.Add(MakeMenuItem(L.T("移出分组"), () =>
                {
                    tab.GroupId = "";
                    RebuildTabStrip();
                    SaveSession();
                }));
            if (IsDuplicatable(tab))
                menu.Items.Add(MakeMenuItem(L.T("复制标签"), () => DuplicateTab(tab)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(L.T("关闭标签"), () => CloseTab(tab)));
            menu.Items.Add(MakeMenuItem(L.T("关闭其他标签"), () =>
            {
                foreach (var other in _tabs.Where(t => t != tab).ToList()) CloseTab(other);
            }));
            menu.Items.Add(MakeMenuItem(L.T("关闭右侧标签"), () => CloseTabsToRight(tab)));
            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        private ContextMenu MakeMenu()
        {
            var menu = new ContextMenu();
            menu.SetResourceReference(ContextMenu.BackgroundProperty, "SurfaceBrush");
            menu.SetResourceReference(ContextMenu.ForegroundProperty, "TextBrush");
            menu.SetResourceReference(ContextMenu.BorderBrushProperty, "LineBrush");
            return menu;
        }

        private MenuItem MakeMenuItem(string text, Action action)
        {
            var item = new MenuItem { Header = text, FontSize = 12 };
            item.SetResourceReference(MenuItem.ForegroundProperty, "TextBrush");
            item.Click += (s, e) => action();
            return item;
        }

        private void ShowLayoutMenu(FrameworkElement target)
        {
            var menu = MakeMenu();
            menu.Items.Add(MakeMenuItem(L.T("紧凑单行"), () => SetLayout(LayoutMode.Compact)));
            menu.Items.Add(MakeMenuItem(L.T("标准两行"), () => SetLayout(LayoutMode.Standard)));
            menu.Items.Add(MakeMenuItem(L.T("垂直标签"), () => SetLayout(LayoutMode.Vertical)));
            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        private void ShowMainMenu(FrameworkElement target)
        {
            var menu = MakeMenu();
            menu.Items.Add(MakeMenuItem(L.T("新建标签 (Ctrl+T)"), () => NewTab()));
            menu.Items.Add(MakeMenuItem(L.T("恢复最近关闭 (Ctrl+Shift+T)"), ReopenClosedTab));
            menu.Items.Add(MakeMenuItem(L.T("新建密封标签"), NewSecretTab));
            menu.Items.Add(MakeMenuItem(L.T("切换密封标签 (Alt+`)"), ToggleSecret));
            menu.Items.Add(MakeMenuItem(L.T("密封标签通知：") + (_settings.SecretNotifications ? L.T("允许") : L.T("关闭")),
                ToggleSecretNotifications));
            menu.Items.Add(MakeMenuItem(L.T("新建分组"), () =>
            {
                _groups.Add(new TabGroup { Name = L.T("分组 ") + (_groups.Count + 1) });
                RebuildTabStrip();
                SaveSession();
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(L.T("历史记录 (Ctrl+H)"), () => OpenInternalPage(_active, InternalPages.History)));
            menu.Items.Add(MakeMenuItem(L.T("书签 (Ctrl+Shift+O)"), () => OpenInternalPage(_active, InternalPages.Bookmarks)));
            menu.Items.Add(MakeMenuItem(L.T("下载 (Ctrl+J)"), OpenDownloads));
            menu.Items.Add(MakeMenuItem(L.T("用户脚本"), OpenScripts));
            menu.Items.Add(MakeMenuItem(L.T("设置"), OpenSettings));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(L.T("放大 (Ctrl++)"), () => Zoom(_browser.ZoomIn)));
            menu.Items.Add(MakeMenuItem(L.T("缩小 (Ctrl+-)"), () => Zoom(_browser.ZoomOut)));
            menu.Items.Add(MakeMenuItem(L.T("缩放重置 (Ctrl+0)"), () => Zoom(_browser.ZoomReset)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(L.T("内存明细面板 (Ctrl+Shift+M)"), () => OpenMemoryPanel(true)));
            var presets = new MenuItem
            {
                Header = L.T("内存档位：") + _settings.Memory.PresetName,
                FontSize = 12
            };
            presets.SetResourceReference(MenuItem.ForegroundProperty, "TextBrush");
            presets.Items.Add(MakeMenuItem((_settings.Memory.Preset == MemoryPreset.Conservative ? "● " : "　 ") + L.T("保守"),
                () => SetMemoryPreset(MemoryPreset.Conservative)));
            presets.Items.Add(MakeMenuItem((_settings.Memory.Preset == MemoryPreset.Balanced ? "● " : "　 ") + L.T("均衡"),
                () => SetMemoryPreset(MemoryPreset.Balanced)));
            presets.Items.Add(MakeMenuItem((_settings.Memory.Preset == MemoryPreset.Aggressive ? "● " : "　 ") + L.T("激进"),
                () => SetMemoryPreset(MemoryPreset.Aggressive)));
            menu.Items.Add(presets);
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(_settings.VerticalExpanded ? L.T("垂直标签：改为悬浮展开") : L.T("垂直标签：固定展开"), () =>
            {
                _settings.VerticalExpanded = !_settings.VerticalExpanded;
                SettingsStore.Save(_settings);
                ApplyLayout();
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(L.T("设为默认浏览器"), SetDefaultBrowser));
            menu.Items.Add(MakeMenuItem(L.T("切换主题"), ToggleTheme));
            menu.Items.Add(MakeMenuItem(L.T("沉浸式全屏 (F11)"), ToggleImmersive));
            menu.Items.Add(MakeMenuItem(_settings.ShowStatusBar ? L.T("隐藏状态栏") : L.T("显示状态栏"), () =>
            {
                _settings.ShowStatusBar = !_settings.ShowStatusBar;
                ApplyLayout();
                SettingsStore.Save(_settings);
            }));
            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        private void SetLayout(LayoutMode mode)
        {
            _settings.Layout = mode;
            SettingsStore.Save(_settings);
            ApplyLayout();
        }

        private void ToggleTheme()
        {
            _settings.Theme = Theme.IsDark ? "light" : "dark";
            Theme.Apply(_settings.Theme);
            _browser.RefreshBackground();
            SettingsStore.Save(_settings);
            ApplyLayout();
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void ToggleImmersive()
        {
            _immersive = !_immersive;
            ApplyLayout();
        }

        // ---------- tab lifecycle ----------

        private void NewTab(string url = null, string sourceSite = null, bool secret = false)
        {
            var tab = new TabModel { IsSecret = secret, Title = secret ? L.T("文档") : L.T("新标签页") };
            _tabs.Add(tab);
            var start = new StartPage(_settings);
            start.NavigateRequested += input => Navigate(tab, input);
            _browser.RegisterStartPage(tab, start);
            SelectTab(tab);
            if (!string.IsNullOrEmpty(url)) Navigate(tab, url, null, sourceSite);
        }

        // 密封标签：不建独立 Profile、不防截屏，只做伪装 + 快捷键切换（用户口径，见 SPEC 0.15）。
        private void NewSecretTab()
        {
            NewTab(null, null, true);
            if (_address != null) _address.Focus();
        }

        private void ToggleSecret()
        {
            var secret = _tabs.FirstOrDefault(t => t.IsSecret);
            if (secret == null)
            {
                _toast.Show(L.T("没有密封标签（菜单 → 新建密封标签）"));
                return;
            }

            if (ReferenceEquals(_active, secret))
            {
                var back = _secretReturnTab != null && _tabs.Contains(_secretReturnTab) && !_secretReturnTab.IsSecret
                    ? _secretReturnTab
                    : _tabs.FirstOrDefault(t => !t.IsSecret);
                if (back == null) { _toast.Show(L.T("没有可切回的标签")); return; }
                SelectTab(back);
                return;
            }

            SelectTab(secret);
        }

        private void ToggleSecretNotifications()
        {
            _settings.SecretNotifications = !_settings.SecretNotifications;
            SettingsStore.Save(_settings);
            _toast.Show(_settings.SecretNotifications
                ? L.T("密封标签：允许页面通知")
                : L.T("密封标签：已关闭页面通知"));
        }

        // 密封标签的地址栏一律留空，避免把真实 URL 露在屏幕上（也避免被录屏看到）。
        private static string AddressFor(TabModel tab)
        {
            if (tab == null || tab.IsSecret) return "";
            return tab.IsStartPage ? "" : (tab.Url ?? "");
        }

        private async void Navigate(TabModel tab, string input, TabSnapshot restore = null,
            string sourceSite = null)
        {
            if (tab == null || string.IsNullOrWhiteSpace(input)) return;

            var resolved = UrlParser.Resolve(input, _settings);
            if (InternalPages.IsInternal(resolved))
            {
                OpenInternalPage(tab, resolved, sourceSite);
                return;
            }

            await _browser.NavigateAsync(tab, input, restore);
            if (ReferenceEquals(tab, _active))
            {
                SetAddress(AddressFor(tab));
                _browser.Show(tab);   // 新视图默认 Collapsed，导航启动后按 active 显示
            }
            RebuildTabStrip();
            SaveSession();
        }

        // 切到纯 WPF 内部页：先销毁 WebView2（renderer 退出），再挂新视图。
        private void OpenInternalPage(TabModel tab, string url, string sourceSite = null)
        {
            if (tab == null) return;
            var normalized = InternalPages.Normalize(url);
            // 必须在覆盖 tab.Url 之前取站点：用户脚本页的「按站点禁用」要用它。
            var site = string.IsNullOrEmpty(sourceSite) ? ZoomStore.SiteOf(tab.Url) : sourceSite;
            tab.Url = normalized;
            tab.Title = InternalPages.TitleOf(normalized);
            tab.InternalPage = normalized == InternalPages.Start ? "" : normalized.Substring("nano://".Length);
            tab.IsStartPage = normalized == InternalPages.Start;

            var view = BuildInternalView(tab, site);
            _browser.ShowInternal(tab, view);
            Diagnostics.Log("[internal] " + normalized + " view=" + view.GetType().Name +
                            " tabHostChildren=" + _tabHost.Children.Count +
                            " state=" + tab.MemoryState);

            if (ReferenceEquals(tab, _active))
            {
                SetAddress(AddressFor(tab));
                _browser.Show(tab);
            }
            RebuildTabStrip();
            SaveSession();
        }

        private FrameworkElement BuildInternalView(TabModel tab, string sourceSite = null)
        {
            if (tab.IsStartPage || string.IsNullOrEmpty(tab.InternalPage))
            {
                var start = new StartPage(_settings);
                start.NavigateRequested += input => Navigate(tab, input);
                return start;
            }
            var site = sourceSite ?? "";
            var view = InternalPages.Create("nano://" + tab.InternalPage, _settings,
                OpenFromInternal, message => _toast.Show(message), () => site);

            // 设置页要通过这几条线去动主题/布局/语言/内存，页面本身不碰 WebView2。
            var settingsPage = view as SettingsPage;
            if (settingsPage != null)
            {
                settingsPage.Changed += OnSettingChanged;
                settingsPage.OpenDataFolderRequested += OpenDataFolder;
                settingsPage.SetDefaultBrowserRequested += SetDefaultBrowser;
            }
            return view;
        }

        private void OpenFromInternal(string url, bool newTab)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (newTab) NewTab(url);
            else Navigate(_active, url);
        }

        // Ctrl+J 新开标签：正在下载的页面不能被 ShowInternal 的 Dispose 打断。
        // 已经有下载页就切过去，不重复开。
        private void OpenDownloads()
        {
            var existing = _tabs.FirstOrDefault(t => t.InternalPage == "downloads");
            if (existing != null) { SelectTab(existing); return; }
            NewTab(InternalPages.Downloads);
        }

        // SPEC 5.6 没有给用户脚本页定义快捷键，这里只从主菜单进。
        // 打开时把当前网页的站点键带过去，「按站点临时禁用」才有对象。
        private void OpenScripts()
        {
            var site = _active == null ? "" : ZoomStore.SiteOf(_active.Url);
            var existing = _tabs.FirstOrDefault(t => t.InternalPage == "scripts");
            if (existing != null)
            {
                SelectTab(existing);
                // 从另一个站点再次打开时要把「当前站点」换掉，否则开关指向的是旧站点。
                if (!string.IsNullOrEmpty(site))
                    OpenInternalPage(existing, InternalPages.Scripts, site);
                return;
            }
            NewTab(InternalPages.Scripts, site);
        }

        private void OpenSettings()
        {
            var existing = _tabs.FirstOrDefault(t => t.InternalPage == "settings");
            if (existing != null) { SelectTab(existing); return; }
            NewTab(InternalPages.Settings);
        }

        // 设置页发来的改动。语言要重建所有本地视图，主题/布局只重建外壳。
        private void OnSettingChanged(string kind)
        {
            SettingsStore.Save(_settings);
            if (kind == "language")
            {
                L.Apply(_settings.Language);
                ApplyLayout();
                RebuildLocalViews();
                _toast.Show(L.T("语言已切换"));
                return;
            }
            if (kind == "theme")
            {
                Theme.Apply(_settings.Theme);
                _browser.RefreshBackground();
                ApplyLayout();
                return;
            }
            if (kind == "layout" || kind == "status")
            {
                ApplyLayout();
                return;
            }
            if (kind == "memory")
            {
                UpdateStatus();
                if (_memoryPanel != null && _memoryPanel.IsOpen) { var unused = _memoryPanel.RefreshAsync(); }
            }
        }

        // 双语切换后，内部页/起始页上的文案是构造时写死的，只能整块换视图。
        // 网页标签不动（WebView2 不能 reparent，也没必要重载）。
        private void RebuildLocalViews()
        {
            foreach (var tab in _tabs.ToList())
            {
                if (!string.IsNullOrEmpty(tab.InternalPage))
                {
                    tab.Title = InternalPages.TitleOf(tab.Url);
                    var site = ZoomStore.SiteOf(tab.Url);
                    _browser.ReplaceLocalView(tab, BuildInternalView(tab, site));
                }
                else if (tab.IsStartPage)
                {
                    tab.Title = L.T("新标签页");
                    var start = new StartPage(_settings);
                    start.NavigateRequested += input => Navigate(tab, input);
                    _browser.ReplaceLocalView(tab, start);
                }
            }
            if (_active != null) _browser.Show(_active);
            RebuildTabStrip();
            UpdateStatus();
        }

        private void OpenDataFolder()
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + AppPaths.Roaming + "\""); }
            catch (Exception ex) { _toast.Show(L.T("打开目录失败：") + ex.Message); }
        }

        // ---------- 外部唤起 / 崩溃恢复 / 默认浏览器 ----------

        private void OnIpcReceived(string url)
        {
            // 管道回调在后台线程，必须切回 UI 线程再动标签。
            Dispatcher.BeginInvoke(new Action(() => HandleIpcMessage(url)));
        }

        private void HandleIpcMessage(string url)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!IsExternalUrl(url))
            {
                Diagnostics.Log("ipc ignored: " + url);
                return;
            }
            NewTab(url);
        }

        private static bool IsExternalUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            var scheme = uri.Scheme.ToLowerInvariant();
            return scheme == "http" || scheme == "https" || scheme == "file";
        }

        private async void RecoverFromBrowserCrash()
        {
            if (_recovering) return;
            _recovering = true;
            try
            {
                var active = _active;
                _browser.ResetAfterBrowserCrash(_tabs);
                RebuildTabStrip();
                await System.Threading.Tasks.Task.Delay(400);
                if (active != null)
                {
                    SelectTab(active);
                    EnsureLoaded(active);
                }
                _toast.Show(L.T("浏览器内核已重启，标签正在重新加载"));
            }
            catch (Exception ex)
            {
                Diagnostics.Log("recover after crash failed: " + ex.Message);
                _toast.Show(L.T("恢复失败，请重启 NanoEdge"));
            }
            finally { _recovering = false; }
        }

        private void SetDefaultBrowser()
        {
            try
            {
                DefaultBrowser.Register();
                _toast.Show(L.T("已注册到 Windows；请在「默认应用」里选择 NanoEdge"), null, null, 5000);
                DefaultBrowser.OpenSettings();
            }
            catch (Exception ex)
            {
                _toast.Show(L.T("注册默认浏览器失败：") + ex.Message);
            }
        }

        private void Zoom(Action<TabModel> change)
        {
            var tab = _active;
            if (tab == null) return;
            if (tab.IsStartPage || !string.IsNullOrEmpty(tab.InternalPage) || !_browser.HasWebView(tab))
            {
                _toast.Show(L.T("当前页面不能缩放"));
                return;
            }
            change(tab);
            var site = ZoomStore.SiteOf(tab.Url);
            _toast.Show(L.T("缩放 ") + ZoomLevels.Label(tab.Zoom) +
                        (string.IsNullOrEmpty(site) ? "" : " · " + ZoomStore.SiteLabel(site)), null, null, 1200);
        }

        private void SelectTab(TabModel tab)
        {
            if (tab == null) return;
            // 记住"从哪个普通标签切到密封标签"，Alt+` 才能切回来；普通→普通也更新。
            if (_active != null && !_active.IsSecret && !ReferenceEquals(_active, tab))
                _secretReturnTab = _active;
            _active = tab;
            tab.LastActiveUtc = DateTime.UtcNow;
            EnsureLoaded(tab);
            if (_governor != null) _governor.NotifySelected(tab);
            _browser.Show(tab);
            SetAddress(AddressFor(tab));
            RebuildTabStrip();
            SaveSession();
        }

        // 懒加载 / 恢复入口：被丢弃或上次会话留下的标签在这里真正创建 WebView2。
        private void EnsureLoaded(TabModel tab)
        {
            // 构造函数里（Application.Run 之前）不能 EnsureCoreWebView2Async，
            // 会话恢复的首次加载统一推迟到 Loaded 之后。
            if (!_shellReady) return;
            if (tab == null || tab.IsStartPage) return;
            if (!string.IsNullOrEmpty(tab.InternalPage)) return;
            if (_browser.HasLiveView(tab)) return;
            if (string.IsNullOrEmpty(tab.Url)) return;

            var snapshot = tab.MemoryState == TabMemoryState.Discarded
                ? (tab.Snapshot ?? SnapshotStore.Load(tab.Id))
                : null;
            if (snapshot != null && !snapshot.Restorable) snapshot = null;

            var url = snapshot != null && !string.IsNullOrEmpty(snapshot.Url) ? snapshot.Url : tab.Url;
            Navigate(tab, url, snapshot);
            SnapshotStore.Delete(tab.Id);
        }

        private void RestoreTab(TabModel tab)
        {
            if (tab == null) return;
            if (!ReferenceEquals(tab, _active)) { SelectTab(tab); return; }
            EnsureLoaded(tab);
        }

        private void CloseTab(TabModel tab)
        {
            if (tab == null) return;
            if (_tabs.Count == 1) { Close(); return; }
            RememberClosedTab(tab);
            var index = _tabs.IndexOf(tab);
            _browser.Remove(tab);
            _tabs.Remove(tab);
            SnapshotStore.Delete(tab.Id);
            _groups.RemoveAll(g => g.Id == tab.GroupId && !_tabs.Any(t => t.GroupId == g.Id));
            SelectTab(_tabs[Math.Max(0, Math.Min(index, _tabs.Count - 1))]);
        }

        // ---------- 最近关闭（SPEC 5.6 / Ctrl+Shift+T） ----------

        // 只记 http/https/file；密封、内部页、起始页都不记账（和 ZZZ 的口径一致）。
        private void RememberClosedTab(TabModel tab)
        {
            if (tab == null || tab.IsSecret || tab.IsStartPage) return;
            if (!string.IsNullOrEmpty(tab.InternalPage)) return;
            if (string.IsNullOrEmpty(tab.Url)) return;
            Uri uri;
            if (!Uri.TryCreate(tab.Url, UriKind.Absolute, out uri)) return;
            var scheme = uri.Scheme.ToLowerInvariant();
            if (scheme != "http" && scheme != "https" && scheme != "file") return;

            _closedTabs.Add(new ClosedTab { Url = tab.Url, Title = tab.DisplayTitle });
            if (_closedTabs.Count > ClosedTabLimit) _closedTabs.RemoveAt(0);
        }

        private void ReopenClosedTab()
        {
            if (_closedTabs.Count == 0)
            {
                _toast.Show(L.T("没有可恢复的标签"));
                return;
            }
            var last = _closedTabs[_closedTabs.Count - 1];
            _closedTabs.RemoveAt(_closedTabs.Count - 1);
            NewTab(last.Url);
        }

        // 按用户看到的顺序算"右侧"，分组折叠时不会误关看不见的标签。
        private void CloseTabsToRight(TabModel tab)
        {
            if (tab == null) return;
            var visible = VisibleTabs().ToList();
            var index = visible.IndexOf(tab);
            if (index < 0 || index >= visible.Count - 1) return;
            foreach (var other in visible.Skip(index + 1).ToList()) CloseTab(other);
        }

        private void DuplicateTab(TabModel tab)
        {
            if (!IsDuplicatable(tab)) return;
            NewTab(tab.Url);
        }

        private static bool IsDuplicatable(TabModel tab)
        {
            if (tab == null || tab.IsSecret || tab.IsStartPage) return false;
            if (!string.IsNullOrEmpty(tab.InternalPage)) return false;
            return !string.IsNullOrEmpty(tab.Url) &&
                   !tab.Url.StartsWith("nano://", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateStatus()
        {
            if (_statusText == null) return;
            var layout = _settings.Layout == LayoutMode.Compact ? L.T("紧凑单行")
                : _settings.Layout == LayoutMode.Standard ? L.T("标准两行") : L.T("垂直标签");
            var snapshot = _governor == null ? null : _governor.LastSnapshot;
            var extra = "";
            if (snapshot != null) extra += L.T(" · 内存 ") + snapshot.GroupWorkingSetMB.ToString("0") + "MB";
            var cooled = _tabs.Count(t => t.MemoryState == TabMemoryState.Low);
            var discarded = _tabs.Count(t => t.MemoryState == TabMemoryState.Discarded);
            if (cooled > 0) extra += L.T(" · 降冷 ") + cooled;
            if (discarded > 0) extra += L.T(" · 已丢弃 ") + discarded;
            if (_active != null && Math.Abs(_active.Zoom - ZoomLevels.Default) > 0.0001)
                extra += L.T(" · 缩放 ") + ZoomLevels.Label(_active.Zoom);
            _statusText.Text = layout + " · " + ThemeText() + " · " + _tabs.Count + L.T(" 个标签 · ") +
                               _groups.Count + L.T(" 个分组 · 内存档 ") + _settings.Memory.PresetName + extra;
        }

        private void UpdateMemoryBadge()
        {
            if (_memoryBadge == null) return;
            var snapshot = _governor == null ? null : _governor.LastSnapshot;
            if (snapshot == null)
            {
                _memoryBadge.Text = "— MB";
                _memoryBadge.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                return;
            }
            _memoryBadge.Text = snapshot.GroupWorkingSetMB.ToString("0") + " MB";
            var over = _settings.Memory != null &&
                       snapshot.GroupWorkingSetMB > _settings.Memory.PressureLimitMB;
            _memoryBadge.SetResourceReference(TextBlock.ForegroundProperty,
                over ? "WarnBrush" : "AccentBrush");
            UpdateStatus();
        }

        private void ToggleResident(TabModel tab)
        {
            if (tab == null) return;
            var host = tab.Host;
            var next = !tab.Resident;
            tab.Resident = next;

            var list = _settings.Memory.ResidentOrigins;
            if (list == null)
            {
                list = new List<string>();
                _settings.Memory.ResidentOrigins = list;
            }
            list.RemoveAll(o => string.Equals(o, host, StringComparison.OrdinalIgnoreCase));
            if (next && !string.IsNullOrEmpty(host)) list.Add(host);

            if (!string.IsNullOrEmpty(host))
                foreach (var other in _tabs.Where(t => string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase)))
                    other.Resident = next;

            SettingsStore.Save(_settings);
            if (_memoryPanel != null) { var unused = _memoryPanel.RefreshAsync(); }
        }

        private void SetMemoryPreset(MemoryPreset preset)
        {
            _settings.Memory.ApplyPreset(preset);
            SettingsStore.Save(_settings);
            UpdateStatus();
            _toast.Show(L.T("内存档位：") + _settings.Memory.PresetName);
            if (_memoryPanel != null && _memoryPanel.IsOpen) { var unused = _memoryPanel.RefreshAsync(); }
        }

        private async void ReleaseTab(TabModel tab)
        {
            if (tab == null || _governor == null) return;
            if (ReferenceEquals(tab, _active))
            {
                _toast.Show(L.T("当前标签不参与释放，先切到其他标签"));
                return;
            }
            var reason = _governor.ExemptionReason(tab);
            if (reason != null)
            {
                _toast.Show(L.T("该标签被豁免：") + reason);
                return;
            }
            if (await _governor.DiscardTabAsync(tab)) _toast.Show(L.T("已释放「") + tab.DisplayTitle + "」");
            else _toast.Show(L.T("该标签没有可释放的页面"));
        }

        private string ThemeText() { return Theme.IsDark ? L.T("暗色") : L.T("亮色"); }

        // ---------- keyboard ----------

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (HandleShortcut(e.Key, Keyboard.Modifiers, e.SystemKey)) e.Handled = true;
        }

        // WebView2 是独立 HWND：页面聚焦时 WPF 收不到按键，必须在消息泵层面先截。
        private void OnThreadPreprocessMessage(ref System.Windows.Interop.MSG msg, ref bool handled)
        {
            if (handled) return;
            const int WM_KEYDOWN = 0x0100;
            const int WM_SYSKEYDOWN = 0x0104;
            if (msg.message != WM_KEYDOWN && msg.message != WM_SYSKEYDOWN) return;

            var key = KeyInterop.KeyFromVirtualKey((int)msg.wParam);
            var modifiers = Keyboard.Modifiers;
            // 只截带修饰键的组合和功能键，别吞掉正常打字。
            if (modifiers == ModifierKeys.None &&
                key != Key.F5 && key != Key.F11 && key != Key.F12) return;
            if (HandleShortcut(key, modifiers, Key.None)) handled = true;
        }

        private void ToggleBookmark()
        {
            var tab = _active;
            if (tab == null) return;
            if (tab.IsSecret)
            {
                _toast.Show(L.T("密封标签不能收藏"));
                return;
            }
            if (tab.IsStartPage || !string.IsNullOrEmpty(tab.InternalPage) ||
                string.IsNullOrEmpty(tab.Url) ||
                tab.Url.StartsWith("nano://", StringComparison.OrdinalIgnoreCase))
            {
                _toast.Show(L.T("当前页面不能收藏"));
                return;
            }
            var entry = BookmarkStore.Toggle(tab.Url, tab.Title);
            _toast.Show(entry == null ? L.T("已取消收藏") : L.T("已收藏：") + entry.Title);
        }

        // 快捷键统一入口：WPF 输入和 WebView2 的原始按键（页面聚焦时）都走这里。
        private bool HandleShortcut(Key key, ModifierKeys modifiers, Key systemKey)
        {
            var ctrl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            var shift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            var alt = (modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

            if (alt && (key == Key.Left || systemKey == Key.Left)) { _browser.Back(_active); return true; }
            if (alt && (key == Key.Right || systemKey == Key.Right)) { _browser.Forward(_active); return true; }
            // SPEC 5.6 / 7.4：Alt+` 在密封标签与刚才的普通标签之间切换。
            if (alt && key == Key.OemTilde) { ToggleSecret(); return true; }
            // SPEC 5.6：Ctrl+F5 是硬刷新（绕过缓存）；普通 F5 保持原生 Reload。
            if (key == Key.F5)
            {
                if (ctrl) _browser.HardReload(_active);
                else _browser.Reload(_active);
                return true;
            }
            if (ctrl && key == Key.R) { _browser.Reload(_active); return true; }
            if (key == Key.F11) { ToggleImmersive(); return true; }
            if (key == Key.F12) { _browser.OpenDevTools(_active); return true; }
            if (key == Key.Escape && _memoryPanel != null && _memoryPanel.IsOpen)
            {
                _memoryPanel.Close();
                return true;
            }
            if (key == Key.Escape && _suggestions != null && _suggestions.IsOpen)
            {
                _suggestions.Close();
                return true;
            }
            if (key == Key.Escape && _immersive) { ToggleImmersive(); return true; }
            if (key == Key.Escape && _address != null && _address.IsKeyboardFocusWithin)
            {
                // 地址栏里按 Esc：恢复当前 URL，焦点回到页面。
                SetAddress(AddressFor(_active));
                _browser.FocusContent(_active);
                return true;
            }

            if (!ctrl) return false;

            if (key == Key.T && !shift) { NewTab(); return true; }
            if (key == Key.T && shift) { ReopenClosedTab(); return true; }
            if (key == Key.W) { CloseTab(_active); return true; }
            if (key == Key.D && !shift) { ToggleBookmark(); return true; }
            if (key == Key.H && !shift) { OpenInternalPage(_active, InternalPages.History); return true; }
            if (key == Key.O && shift) { OpenInternalPage(_active, InternalPages.Bookmarks); return true; }
            if (key == Key.J && !shift) { OpenDownloads(); return true; }

            // 缩放档位必须由我们掌握：按站点记忆 + Ctrl+0 一律回到 100%。
            // 用的仍是 WebView2 自带的 ZoomFactor API，Ctrl+滚轮也保持原生。
            if (key == Key.D0 || key == Key.NumPad0) { Zoom(_browser.ZoomReset); return true; }
            if (key == Key.OemPlus || key == Key.Add) { Zoom(_browser.ZoomIn); return true; }
            if (key == Key.OemMinus || key == Key.Subtract) { Zoom(_browser.ZoomOut); return true; }
            if ((key == Key.L && !shift) || (alt && systemKey == Key.D))
            {
                _address.Focus();
                _address.SelectAll();
                return true;
            }
            if (key == Key.Tab) { CycleTab(!shift); return true; }
            // SPEC 5.6：Ctrl+Shift+M 是内存面板；布局轮换让位到 Ctrl+Shift+L（原键位与 SPEC 冲突）。
            if (key == Key.M && shift) { OpenMemoryPanel(true); return true; }
            if (key == Key.L && shift) { SetLayout(NextLayout()); return true; }
            if (key >= Key.D1 && key <= Key.D9)
            {
                var index = key - Key.D1;
                if (index == 8) index = _tabs.Count - 1;
                if (index >= 0 && index < _tabs.Count) SelectTab(_tabs[index]);
                return true;
            }
            return false;
        }

        private LayoutMode NextLayout()
        {
            return _settings.Layout == LayoutMode.Compact ? LayoutMode.Standard
                : _settings.Layout == LayoutMode.Standard ? LayoutMode.Vertical : LayoutMode.Compact;
        }

        private void CycleTab(bool forward)
        {
            if (_tabs.Count == 0) return;
            var index = _tabs.IndexOf(_active);
            index = (index + (forward ? 1 : -1) + _tabs.Count) % _tabs.Count;
            SelectTab(_tabs[index]);
        }

        // ---------- startup / screenshot ----------

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _shellReady = true;
            if (!_ipcWired)
            {
                _ipcWired = true;
                Ipc.Received += OnIpcReceived;
                Ipc.Start();
            }
            if (_active != null) EnsureLoaded(_active);

            var openUrl = ArgValue("--open-url");
            if (!string.IsNullOrEmpty(openUrl)) NewTab(openUrl);

            if (_governor != null)
            {
                _governor.Start();
                var unused = PrimeMemoryAsync();
            }

            if (HasArg("--memtest"))
            {
                var test = new MemorySelfTest(this);
                var unused = test.RunAsync();
                return;
            }

            if (HasArg("--navtest"))
            {
                var test = new NavSelfTest(this);
                var unused = test.RunAsync();
                return;
            }

            if (HasArg("--m3ctest"))
            {
                var test = new M3CSelfTest(this);
                var unused = test.RunAsync();
                return;
            }

            if (HasArg("--m4test"))
            {
                var test = new M4SelfTest(this);
                var unused = test.RunAsync();
                return;
            }

            if (HasArg("--m5test"))
            {
                var test = new M5SelfTest(this);
                var unused = test.RunAsync();
                return;
            }

            if (HasArg("--m6test"))
            {
                var test = new M6SelfTest(this);
                var unused = test.RunAsync();
                return;
            }

            if (HasArg("--m6btest"))
            {
                var test = new M6BSelfTest(this);
                var unused = test.RunAsync();
                return;
            }

            if (HasArg("--m7test"))
            {
                var test = new M7SelfTest(this);
                var unused = test.RunAsync();
                return;
            }

            if (HasArg("--m8test"))
            {
                var test = new M8SelfTest(this);
                var unused = test.RunAsync();
                return;
            }

            if (!string.IsNullOrEmpty(_screenshotPath))
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
                timer.Tick += (s, args) =>
                {
                    timer.Stop();
                    try { CaptureScreenshot(_screenshotPath); }
                    catch (Exception ex) { Diagnostics.Log("screenshot failed: " + ex); }
                    Application.Current.Shutdown();
                };
                timer.Start();
            }
        }

        private async System.Threading.Tasks.Task PrimeMemoryAsync()
        {
            await System.Threading.Tasks.Task.Delay(1200);
            if (_governor == null) return;
            await _governor.SampleAsync();
            UpdateMemoryBadge();
        }

        private void CaptureScreenshot(string path)
        {
            var width = (int)Math.Max(1, ActualWidth);
            var height = (int)Math.Max(1, ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
    }
}
