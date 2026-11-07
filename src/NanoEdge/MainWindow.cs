using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
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
        private Button _railPin, _popupPin;
        private FrameworkElement _railHeader;
        private StackPanel _railPanel, _popupPanel;
        private ScrollViewer _tabStrip;
        private StackPanel _tabStripPanel;
        private TextBox _address;
        private Border _addressBorder;
        private AddressSuggestions _suggestions;
        private DispatcherTimer _suggestTimer, _suggestCloseTimer;
        private bool _suppressSuggest;
        private TextBlock _statusText, _memoryBadge, _statusRight;
        private double _tabChipMin = 110;
        private double _tabChipMax = 200;
        private Popup _verticalPopup;
        private DispatcherTimer _expandTimer, _collapseTimer, _badgeHoverTimer, _badgeCloseTimer;
        private bool _verticalCollapseSuspended;
        private TabModel _dragCandidate;
        private Point _dragStart;
        private bool _dragInProgress;
        private TabGroup _groupDragCandidate;
        private Point _groupDragStart;
        private bool _groupDragInProgress;
        private DateTime _lastGroupDragUtc = DateTime.MinValue;
        private Grid _navigationRow;
        private FrameworkElement _windowButtons;
        private Button _tabStripPrev, _tabStripNext;
        private ScrollViewer _railScroller, _popupScroller;
        private ScrollViewer _dragScrollVerticalTarget;
        private DispatcherTimer _dragScrollTimer;
        private int _dragScrollDir;
        private int _dragScrollVerticalDir;
        private DateTime _lastChromeDownUtc = DateTime.MinValue;
        private Point _lastChromeDownPoint;
        private bool _chromeDragArmed;
        private TabModel _secretReturnTab;   // Alt+` 从密封标签切回的那个普通标签
        // _immersive 是「真全屏」的唯一状态位：外壳隐藏、A3 钩子让路、
        // 几何不落盘都由它决定。_fullscreenBusy 只挡进出全屏途中的中间几何。
        private bool _immersive;
        private bool _fullscreenBusy;
        private WindowState _fsPrevState;
        private Rect _fsPrevBounds;
        private bool _fsPrevTopmost;
        private ResizeMode _fsPrevResizeMode;
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
            _noPersist = !string.IsNullOrEmpty(_screenshotPath) || HasArg("--menupreview") ||
                         HasArg("--fullscreen") ||
                         HasArg("--memtest") || HasArg("--navtest") || HasArg("--m3ctest") ||
                         HasArg("--m4test") || HasArg("--m5test") || HasArg("--m6test") ||
                         HasArg("--m6btest") || HasArg("--m7test") || HasArg("--m8test") ||
                         HasArg("--fixtest");
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
            // 窗口最小化时 WPF 的 Left/Top 会变成 -32000（Windows 的最小化坐标）。
            // 老配置把这种值落盘后，Manual 定位会把窗口放到屏幕外，看起来就像
            // 「启动没窗口」。无效坐标一律退回居中，并把内存里的脏值清掉，
            // 后面任意一次窗口移动/关闭会把有效坐标写回去，配置自愈。
            bool hasUsableBounds = IsSaneWindowCoord(settings.WindowLeft) &&
                                   IsSaneWindowCoord(settings.WindowTop);
            if (hasUsableBounds)
            {
                Left = settings.WindowLeft;
                Top = settings.WindowTop;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                settings.WindowLeft = double.NaN;
                settings.WindowTop = double.NaN;
            }
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
            InstallWorkAreaHook();

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
            SizeChanged += (s, e) =>
            {
                SyncPopupSize();
                if (_memoryPanel != null && _memoryPanel.IsOpen) _memoryPanel.Reposition();
            };
            LocationChanged += (s, e) =>
            {
                SaveWindowBounds();
                if (_memoryPanel != null && _memoryPanel.IsOpen) _memoryPanel.Reposition();
            };
            Activated += (s, e) =>
            {
                if (_governor != null) _governor.NotifyActivated();
                // 真全屏靠临时 Topmost 盖住任务栏；切回来时重新压上去。
                if (_immersive && !_fullscreenBusy) Topmost = true;
            };
            Deactivated += (s, e) =>
            {
                if (_governor != null) _governor.NotifyDeactivated();
                // 切到别的软件窗口，面板不该还浮在屏幕上。
                if (_memoryPanel != null && _memoryPanel.IsOpen) _memoryPanel.Close();
                // 失焦时撤掉 Topmost，别让全屏窗口一直盖着别的软件。
                if (_immersive && !_fullscreenBusy) Topmost = false;
            };
            StateChanged += (s, e) =>
            {
                if (_governor != null) _governor.NotifyMinimized(WindowState == WindowState.Minimized);
                // 系统快捷键（Win+Up 等）在全屏里强行最大化时，退出全屏回到
                // 进入前的状态，别留下「外壳隐藏 + 最大化」这种半截状态。
                if (_immersive && !_fullscreenBusy && WindowState == WindowState.Maximized)
                    ExitFullscreen();
                if (WindowState == WindowState.Minimized && _memoryPanel != null)
                    _memoryPanel.Close();
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

            _rail = new Border { Width = RailWidth, ClipToBounds = true, AllowDrop = true };
            _rail.SetResourceReference(Border.BackgroundProperty, "ChromeBrush");
            _rail.BorderThickness = new Thickness(0, 0, 1, 0);
            _rail.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            _rail.DragOver += (s, e) =>
            {
                if (!e.Data.GetDataPresent(typeof(TabModel)) &&
                    !e.Data.GetDataPresent(typeof(TabGroup))) return;
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            };
            _rail.Drop += (s, e) =>
            {
                var tab = e.Data.GetData(typeof(TabModel)) as TabModel;
                if (tab != null) DropTabOnBlank(tab);
                else
                {
                    var group = e.Data.GetData(typeof(TabGroup)) as TabGroup;
                    if (group != null) MoveGroupToEnd(group);
                }
                e.Handled = true;
            };
            _railPanel = new StackPanel { Orientation = Orientation.Vertical };
            var railScroller = new ScrollViewer
            {
                Content = _railPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            _railScroller = railScroller;
            var railGrid = new Grid();
            railGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            railGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _railHeader = MakeHeaderRow(true);
            Grid.SetRow(_railHeader, 0);
            railGrid.Children.Add(_railHeader);
            Grid.SetRow(railScroller, 1);
            railGrid.Children.Add(railScroller);
            _rail.Child = railGrid;
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
                _memoryPanel = new MemoryPanel(_memoryBadgeBorder, _root, _governor, () => _tabs, () => _active,
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

            _popupPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0) };
            var popupScroller = new ScrollViewer
            {
                Content = _popupPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            _popupScroller = popupScroller;
            // 图钉放到顶部固定行，标签整体下移一行；左右不留空档，
            // 展开和收起时标签的左边缘保持同一位置。
            var popupGrid = new Grid();
            popupGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            popupGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var popupHeader = MakeHeaderRow(false);
            Grid.SetRow(popupHeader, 0);
            popupGrid.Children.Add(popupHeader);
            Grid.SetRow(popupScroller, 1);
            popupGrid.Children.Add(popupScroller);
            _popupContent = new Border
            {
                Width = ExpandedWidth,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                AllowDrop = true,
                Child = popupGrid
            };
            _popupContent.DragOver += (s, e) =>
            {
                if (!e.Data.GetDataPresent(typeof(TabModel)) &&
                    !e.Data.GetDataPresent(typeof(TabGroup))) return;
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            };
            _popupContent.Drop += (s, e) =>
            {
                var tab = e.Data.GetData(typeof(TabModel)) as TabModel;
                if (tab != null) DropTabOnBlank(tab);
                else
                {
                    var group = e.Data.GetData(typeof(TabGroup)) as TabGroup;
                    if (group != null) MoveGroupToEnd(group);
                }
                e.Handled = true;
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
            if (_verticalCollapseSuspended) return;
            _expandTimer.Stop();
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }

        // 右键菜单和拖放会让鼠标暂时离开悬浮面板，这段时间不能收起。
        private void SuspendVerticalCollapse()
        {
            _verticalCollapseSuspended = true;
            if (_collapseTimer != null) _collapseTimer.Stop();
        }

        private void ResumeVerticalCollapse()
        {
            _verticalCollapseSuspended = false;
            if (_verticalPopup != null && _verticalPopup.IsOpen && !_settings.VerticalExpanded)
                ScheduleCollapse();
        }

        private void OpenVerticalPopup()
        {
            if (_settings.Layout != LayoutMode.Vertical || _settings.VerticalExpanded) return;
            _verticalCollapseSuspended = false;
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
                HasArg("--m6btest") || HasArg("--m7test") || HasArg("--m8test") ||
                HasArg("--fixtest") || HasArg("--menupreview") || HasArg("--fullscreen"))
            {
                NewTab();
                return;
            }

            var session = SessionStore.Load();
            _groups.Clear();
            if (session.Groups != null)
            {
                // 旧版本可能在启动时塞一个「分组0」把所有标签包起来。这类空壳组
                // 不该再显示。同时把指向不存在分组的标签清回平铺，否则它们在
                // 垂直栏里会整个消失（VisibleTabs 只遍历 _groups 里的组）。
                _groups.AddRange(session.Groups.Where(g => g != null &&
                    !string.IsNullOrEmpty(g.Id) && g.Id != "0" &&
                    g.Name != "分组0" && g.Name != "Group 0"));
            }

            var tabs = session.Tabs ?? new List<TabModel>();
            foreach (var tab in tabs)
                if (!string.IsNullOrEmpty(tab.GroupId) && !_groups.Any(g => g.Id == tab.GroupId))
                    tab.GroupId = "";

            // 空分组没有可渲染的区段；只保留真正有成员的组。
            _groups.RemoveAll(g => !tabs.Any(t => t.GroupId == g.Id));

            if (tabs.Count == 0)
            {
                NewTab();
                return;
            }

            foreach (var tab in tabs)
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
                    _browser.RegisterStartPage(tab, MakeStartPage(tab));
                }
                else
                {
                    // 恢复的标签先按"已丢弃"处理：零 WebView2，选中时才真正加载（SPEC 4.5/7.1）。
                    tab.MemoryState = TabMemoryState.Discarded;
                    _browser.RegisterStartPage(tab, MakePlaceholder(tab));
                }
            }
            SnapshotStore.Prune(_tabs.Select(t => t.Id));
            NormalizeGroupRuns();

            _active = _tabs.FirstOrDefault(t => t.Id == session.ActiveTabId) ?? _tabs[0];
            SelectTab(_active);
        }

        // 分组必须是 _tabs 里的一段连续区段，否则渲染时会把成员从原位搬走。
        // 老会话可能留下交错的 GroupId，这里按第一次出现的位置收拢。
        private void NormalizeGroupRuns()
        {
            if (_tabs.Count < 2) return;
            var ordered = new List<TabModel>(_tabs.Count);
            var emitted = new HashSet<string>();
            foreach (var tab in _tabs)
            {
                if (!string.IsNullOrEmpty(tab.GroupId))
                {
                    if (!emitted.Add(tab.GroupId)) continue;
                    ordered.AddRange(_tabs.Where(t => t.GroupId == tab.GroupId));
                }
                else
                {
                    ordered.Add(tab);
                }
            }
            if (ordered.Count != _tabs.Count) return;
            _tabs.Clear();
            _tabs.AddRange(ordered);
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

        // 最小化时 WPF 会给 -32000；多显示器虚拟屏幕左侧可以是负数，但不会到万级。
        // 超出这个范围就当无效坐标：既不能拿来启动定位，也不该写进 settings.json。
        internal static bool IsSaneWindowCoord(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) &&
                   value > -10000 && value < 100000;
        }

        private void SaveWindowBounds()
        {
            if (_settings == null || _noPersist) return;
            // 真全屏的 rcMonitor 矩形不是用户的窗口尺寸，进出途中的中间状态
            // 也不是；一律不落盘，否则下次启动会变成一整块屏幕的普通窗口。
            if (_immersive || _fullscreenBusy) return;
            // 最大化/最小化时 ActualWidth/Height 不是用户设定的还原尺寸，
            // 写进去会把下次启动的"还原大小"永久记成满屏矩形。
            if (CanPersistNormalGeometry(WindowState))
            {
                _settings.WindowWidth = ActualWidth;
                _settings.WindowHeight = ActualHeight;
                // 状态切换瞬间 LocationChanged 可能先于 StateChanged 触发，此时
                // WindowState 还是 Normal 而 Left/Top 已经是 -32000，直接写就污染配置。
                if (IsSaneWindowCoord(Left) && IsSaneWindowCoord(Top))
                {
                    _settings.WindowLeft = Left;
                    _settings.WindowTop = Top;
                }
            }
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            ScheduleSettingsSave();
        }

        internal static bool CanPersistNormalGeometry(WindowState state)
        {
            return state == WindowState.Normal;
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
            if (_railPin != null)
                _railPin.Visibility = expanded && !_immersive
                    ? Visibility.Visible : Visibility.Collapsed;
            SetPinState(_railPin);
            SetPinState(_popupPin);
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
            // 整个工具栏统一接管拖拽/双击，标准两行模式的标签行也在里面。
            var grid = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
            // 双击自己计时：DragMove 会进模态循环，e.ClickCount 经常到不了 2。
            grid.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (IsChromeInteractive(e.OriginalSource as DependencyObject)) return;
                var now = DateTime.UtcNow;
                // 坐标统一相对窗口：还原时算横向比例、PointToScreen 都用这个基准。
                var point = e.GetPosition(this);
                if (IsDoubleClick(_lastChromeDownUtc, _lastChromeDownPoint, now, point))
                {
                    _lastChromeDownUtc = DateTime.MinValue;   // 三击别连着触发
                    _chromeDragArmed = false;
                    e.Handled = true;
                    ToggleMaximize();
                    return;
                }
                _lastChromeDownUtc = now;
                _lastChromeDownPoint = point;
                _chromeDragArmed = true;
            };
            // 拖拽等鼠标真的动了再启动：单击和双击全程不进 DragMove 的模态
            // 循环，第二次 down 才收得到，双击判定也不会被拖拽抢走。
            grid.PreviewMouseMove += (s, e) =>
            {
                if (!_chromeDragArmed || e.LeftButton != MouseButtonState.Pressed) return;
                if (!HandleChromeDragMove(_lastChromeDownPoint, e.GetPosition(this))) return;
                // 最大化时 HandleChromeDragMove 已经先把窗口还原并摆到鼠标底下；
                // 顺序不能反，DragMove() 在 Maximized 状态会抛异常被吞掉。
                try { DragMove(); } catch { }
            };
            grid.PreviewMouseLeftButtonUp += (s, e) => { _chromeDragArmed = false; };
            if (_settings.Layout == LayoutMode.Standard)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });

                // A1：三按钮和标签条同处第一行。用两列 Grid 而不是把按钮直接
                // 叠在标签条上，标签条的 ScrollViewer 才不会延伸到按钮底下，
                // 溢出箭头和「+」也仍留在标签区里。
                var tabRow = new Grid
                {
                    Tag = "standard-tab-row",
                    Background = new SolidColorBrush(Colors.Transparent)
                };
                tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
                var tabStripHost = MakeTabStrip();
                Grid.SetColumn(tabStripHost, 0);
                tabRow.Children.Add(tabStripHost);
                var windowButtons = MakeWindowButtons();
                Grid.SetColumn(windowButtons, 1);
                tabRow.Children.Add(windowButtons);
                Grid.SetRow(tabRow, 0);
                grid.Children.Add(tabRow);

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
            var standard = _settings.Layout == LayoutMode.Standard;
            var bar = new Grid { Height = standard ? 40 : ChromeHeight };
            _navigationRow = bar;
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });   // layout
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });  // nav
            if (_settings.Layout == LayoutMode.Compact)
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45, GridUnitType.Star) });
            else if (_settings.Layout == LayoutMode.Vertical)
                // 垂直布局的标签在左侧 rail，顶栏会全是控件；故意留一段空白当拖拽抓手。
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });  // theme
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });  // memory
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });  // menu
            // A1：标准模式的窗口三按钮已搬到第一行标签行，导航栏不再留这一列。
            if (!standard)
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
                var tabStripHost = MakeTabStrip();
                Grid.SetColumn(tabStripHost, column++);
                bar.Children.Add(tabStripHost);
            }
            else if (_settings.Layout == LayoutMode.Vertical)
                column++;   // 空档列不放元素，地址栏顺延到下一列

            var addressBorder = new Border
            {
                Height = 26,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = "interactive"
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
                Tag = "interactive",
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

            if (!standard) bar.Children.Add(At(MakeWindowButtons(), column));

            // 拖拽/双击统一由 BuildToolbar 的外层 grid 处理，这里只保留
            // 命中背景，让导航行自己的空白也能被外层事件经过。
            bar.Background = new SolidColorBrush(Colors.Transparent);
            return bar;
        }

        // 窗口三按钮只在这里构造：紧凑/纵向落在导航栏最后一列，标准落在第一行
        // 标签行右侧。两处共用同一套按钮，避免行为或文案走样。
        private FrameworkElement MakeWindowButtons()
        {
            var windowButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = "window-buttons",
                Background = new SolidColorBrush(Colors.Transparent)
            };
            windowButtons.Children.Add(IconButton("─", L.T("最小化"), () => WindowState = WindowState.Minimized));
            windowButtons.Children.Add(IconButton("□", L.T("最大化"), ToggleMaximize));
            var close = IconButton("×", L.T("关闭"), Close);
            close.MouseEnter += (s, e) => close.SetResourceReference(Button.ForegroundProperty, "DangerBrush");
            close.MouseLeave += (s, e) => close.SetResourceReference(Button.ForegroundProperty, "TextBrush");
            windowButtons.Children.Add(close);
            _windowButtons = windowButtons;
            return windowButtons;
        }

        // DragMove 会打断 WPF 的点击计数，所以双击自己按时间窗和位移判断。
        private static bool IsDoubleClick(DateTime last, Point lastPoint, DateTime now, Point point)
        {
            if (last == DateTime.MinValue) return false;
            return (now - last).TotalMilliseconds <= 500 &&
                   Math.Abs(point.X - lastPoint.X) < 8 &&
                   Math.Abs(point.Y - lastPoint.Y) < 8;
        }

        // 顶栏哪些区域不能拿来拖窗口。只排除真正要收点击的元素：
        // 旧版把整个 ScrollViewer 排除，标签少时标签条右侧一大片空白就永远拖不动（B1 返工）。
        private static bool IsChromeInteractive(DependencyObject src)
        {
            if (src == null) return false;
            if (src is TextBox) return true;
            if (FindParent<Button>(src) != null) return true;
            var node = src;
            while (node != null)
            {
                var fe = node as FrameworkElement;
                if (fe != null && (fe.Tag is TabModel ||
                                   fe.Tag is TabGroup ||
                                   Equals(fe.Tag, "group") ||
                                   Equals(fe.Tag, "interactive"))) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        // ---------- 最大化时拖顶栏：先还原，再继续跟随鼠标 ----------

        // 物理像素 → DIP。PerMonitorV2 下 PointToScreen / GetMonitorInfo 都是物理像素，
        // 而 Window.Left/Top/Width/Height 是 DIP；不换算在 125% 缩放下位置会明显偏移。
        internal static Point PhysicalToDip(Point physical, DpiScale dpi)
        {
            var sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            var sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
            return new Point(physical.X / sx, physical.Y / sy);
        }

        private static double ClampTo(double value, double lo, double hi)
        {
            if (hi < lo) hi = lo;
            return value < lo ? lo : (value > hi ? hi : value);
        }

        // RestoreBounds 可能是 Rect.Empty（窗口从未还原过），也可能被系统填成
        // 最大化矩形（无边框窗口、启动时就最大化时很常见）。后者等于没还原，
        // 两种都判为不可用，让调用方退回一个明显小于最大化尺寸的普通窗口。
        private static bool ValidRestoreSize(double value, double maximized)
        {
            if (!(value > 0) || double.IsNaN(value) || double.IsInfinity(value)) return false;
            return !(maximized > 0 && value >= maximized * 0.95);
        }

        // 纯几何：算出从最大化还原后窗口该摆在哪。抽成静态方法便于 --fixtest 断言。
        // anchor 是按下点在最大化窗口内的 DIP 坐标，cursor 是当前鼠标的屏幕 DIP 坐标。
        internal static Rect ComputeRestorePlacement(
            double maxWidth, double maxHeight,
            Point anchor, Point cursor,
            double restoreWidth, double restoreHeight,
            Rect work)
        {
            double w = ValidRestoreSize(restoreWidth, maxWidth)
                ? restoreWidth : Math.Max(640, maxWidth * 0.72);
            double h = ValidRestoreSize(restoreHeight, maxHeight)
                ? restoreHeight : Math.Max(480, maxHeight * 0.72);

            // 鼠标要停在标题栏原来的横向比例上，纵向保持按下时的标题栏内偏移。
            double ratio = maxWidth > 0 ? anchor.X / maxWidth : 0.5;
            ratio = ClampTo(ratio, 0.0, 1.0);
            double left = cursor.X - w * ratio;
            double top = cursor.Y - anchor.Y;

            // 左上角不跑出工作区；纵向至少留一条 36px 顶栏在屏幕内能抓回来。
            double minLeft = work.Left;
            double maxLeft = Math.Max(minLeft, work.Right - Math.Min(w, work.Width));
            double minTop = work.Top;
            double maxTop = Math.Max(minTop, work.Bottom - ChromeHeight);
            return new Rect(ClampTo(left, minLeft, maxLeft), ClampTo(top, minTop, maxTop), w, h);
        }

        // 当前显示器工作区（DIP）。rcWork 是物理像素，按窗口所在屏的 DPI 缩放换算；
        // 拿不到时退回 SystemParameters.WorkArea（主屏工作区，本身已是 DIP）。
        private Rect WorkAreaDip()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(monitor, ref mi))
                {
                    var rect = MonitorRectFromNative(mi.Work.Left, mi.Work.Top,
                        mi.Work.Right, mi.Work.Bottom, dpi);
                    if (!rect.IsEmpty) return rect;
                }
            }
            catch { }
            return SystemParameters.WorkArea;
        }

        // 原生矩形（物理像素）→ DIP。rcWork / rcMonitor 都走这里，混合 DPI、
        // 负坐标的副屏也按同一套换算；抽成纯函数便于 --fixtest 断言。
        internal static Rect MonitorRectFromNative(
            int left, int top, int right, int bottom, DpiScale dpi)
        {
            var tl = PhysicalToDip(new Point(left, top), dpi);
            var br = PhysicalToDip(new Point(right, bottom), dpi);
            return br.X > tl.X && br.Y > tl.Y ? new Rect(tl, br) : Rect.Empty;
        }

        // 全屏用的整屏矩形（含任务栏区域），不是 rcWork。取「窗口当前所在屏」，
        // 所以多显示器下按 F11 会铺满那一块屏，而不是主屏。
        private Rect MonitorRectDip()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(monitor, ref mi))
                {
                    var rect = MonitorRectFromNative(mi.Monitor.Left, mi.Monitor.Top,
                        mi.Monitor.Right, mi.Monitor.Bottom, dpi);
                    if (!rect.IsEmpty) return rect;
                }
            }
            catch { }
            // 拿不到显示器信息时退回主屏尺寸，至少仍是「一整块屏」而不是虚拟桌面。
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth,
                SystemParameters.PrimaryScreenHeight);
        }

        // 拖拽真正启动：超过 3px 阈值才进，单击/双击全程不进 DragMove 的模态循环。
        // 返回 true 表示调用方可以调 DragMove()。抽成方法是为了让自测能直接断言这条路径。
        private bool HandleChromeDragMove(Point down, Point current)
        {
            if (!_chromeDragArmed) return false;
            if (Math.Abs(current.X - down.X) < 3 && Math.Abs(current.Y - down.Y) < 3) return false;
            _chromeDragArmed = false;
            RestoreFromMaximized(down, current);
            return true;
        }

        // DragMove() 在 Maximized 下会抛异常，必须先还原并把窗口挪到鼠标底下。
        // 还原要在左键仍按下时同步完成，DragMove 才能接着进拖拽循环。
        private bool RestoreFromMaximized(Point anchor, Point current)
        {
            if (WindowState != WindowState.Maximized) return false;
            var dpi = VisualTreeHelper.GetDpi(this);
            var cursor = PhysicalToDip(PointToScreen(current), dpi);
            var bounds = RestoreBounds;
            double rw = bounds.IsEmpty ? Width : bounds.Width;
            double rh = bounds.IsEmpty ? Height : bounds.Height;
            var place = ComputeRestorePlacement(ActualWidth, ActualHeight, anchor, cursor,
                rw, rh, WorkAreaDip());

            WindowState = WindowState.Normal;
            Width = place.Width;
            Height = place.Height;
            Left = place.Left;
            Top = place.Top;
            return true;
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

        private FrameworkElement MakeTabStrip()
        {
            _tabStripPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _tabStrip = new ScrollViewer
            {
                Content = _tabStripPanel,
                Background = new SolidColorBrush(Colors.Transparent),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0)
            };
            // 隐藏水平滚动条后，标签过多只能靠滚轮横向滚动；Edge 也是滚轮/
            // 触控板横滑。这里不改变标签尺寸，避免紧凑模式又一次重排。
            _tabStrip.PreviewMouseWheel += (s, e) =>
            {
                if (e.Delta == 0) return;
                _tabStrip.ScrollToHorizontalOffset(_tabStrip.HorizontalOffset - e.Delta);
                UpdateTabStripScrollButtons();
                e.Handled = true;
            };
            // 标签条空白处也是落点：拖到分组外的空白 = 移出分组并落到末尾。
            _tabStrip.AllowDrop = true;
            _tabStrip.DragOver += (s, e) =>
            {
                PumpDragAutoScroll(e);
                if (!e.Data.GetDataPresent(typeof(TabModel)) &&
                    !e.Data.GetDataPresent(typeof(TabGroup))) return;
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            };
            _tabStrip.Drop += (s, e) =>
            {
                StopDragAutoScroll();
                var tab = e.Data.GetData(typeof(TabModel)) as TabModel;
                if (tab != null) DropTabOnBlank(tab);
                else
                {
                    var group = e.Data.GetData(typeof(TabGroup)) as TabGroup;
                    if (group != null) MoveGroupToEnd(group);
                }
                e.Handled = true;
            };
            _tabStrip.ScrollChanged += (s, e) => UpdateTabStripScrollButtons();
            _tabStrip.SizeChanged += (s, e) => UpdateTabStripScrollButtons();

            _tabStripPrev = MakeStripChevron("‹", L.T("向左滚动标签"), -1,
                HorizontalAlignment.Left);
            _tabStripNext = MakeStripChevron("›", L.T("向右滚动标签"), 1,
                HorizontalAlignment.Right);
            var host = new Grid();
            host.Children.Add(_tabStrip);
            host.Children.Add(_tabStripPrev);
            host.Children.Add(_tabStripNext);
            return host;
        }

        private Button MakeStripChevron(string glyph, string tip, int dir,
            HorizontalAlignment align)
        {
            var button = IconButton(glyph, tip, () => ScrollTabStrip(dir));
            button.Tag = dir < 0 ? "tab-scroll-left" : "tab-scroll-right";
            button.Width = 18;
            button.Height = 30;
            button.HorizontalAlignment = align;
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Visibility = Visibility.Collapsed;
            button.Margin = new Thickness(align == HorizontalAlignment.Left ? 2 : 0, 0,
                align == HorizontalAlignment.Right ? 2 : 0, 0);
            return button;
        }

        private void ScrollTabStrip(int dir)
        {
            if (_tabStrip == null) return;
            var step = Math.Max(80, _tabStrip.ViewportWidth / 2);
            _tabStrip.ScrollToHorizontalOffset(_tabStrip.HorizontalOffset + dir * step);
            UpdateTabStripScrollButtons();
        }

        private void UpdateTabStripScrollButtons()
        {
            if (_tabStrip == null || _tabStripPrev == null || _tabStripNext == null) return;
            var canLeft = _tabStrip.HorizontalOffset > 0.5;
            var canRight = _tabStrip.ScrollableWidth - _tabStrip.HorizontalOffset > 0.5;
            _tabStripPrev.Visibility = canLeft ? Visibility.Visible : Visibility.Collapsed;
            _tabStripNext.Visibility = canRight ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EnsureDragScrollTimer()
        {
            if (_dragScrollTimer != null) return;
            _dragScrollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(30)
            };
            _dragScrollTimer.Tick += (s, e) =>
            {
                if (_dragScrollVerticalTarget != null && _dragScrollVerticalDir != 0)
                {
                    _dragScrollVerticalTarget.ScrollToVerticalOffset(
                        _dragScrollVerticalTarget.VerticalOffset +
                        _dragScrollVerticalDir * 16);
                    return;
                }
                if (_dragScrollDir != 0 && _tabStrip != null && _tabStrip.IsVisible)
                {
                    _tabStrip.ScrollToHorizontalOffset(
                        _tabStrip.HorizontalOffset + _dragScrollDir * 16);
                    UpdateTabStripScrollButtons();
                    return;
                }
                StopDragAutoScroll();
            };
        }

        // 拖到标签条/侧栏边缘时自动滚动，才能把标签放到当前隐藏的位置。
        private void PumpDragAutoScroll(DragEventArgs e)
        {
            if (_tabStrip != null && _tabStrip.IsVisible)
            {
                _dragScrollVerticalTarget = null;
                _dragScrollVerticalDir = 0;
                var hp = e.GetPosition(_tabStrip);
                const double edge = 30;
                var hdir = 0;
                if (hp.X >= 0 && hp.X < edge) hdir = -1;
                else if (hp.X <= _tabStrip.ActualWidth &&
                    hp.X > _tabStrip.ActualWidth - edge) hdir = 1;
                _dragScrollDir = hdir;
                PumpTimer(hdir);
                return;
            }

            _dragScrollDir = 0;
            ScrollViewer vertical = null;
            if (_verticalPopup != null && _verticalPopup.IsOpen &&
                _popupScroller != null && _popupScroller.IsVisible)
                vertical = _popupScroller;
            else if (_railScroller != null && _railScroller.IsVisible)
                vertical = _railScroller;
            if (vertical == null)
            {
                StopDragAutoScroll();
                return;
            }
            var p = e.GetPosition(vertical);
            var dir = 0;
            if (p.Y >= 0 && p.Y < 30) dir = -1;
            else if (p.Y <= vertical.ActualHeight && p.Y > vertical.ActualHeight - 30)
                dir = 1;
            _dragScrollVerticalTarget = vertical;
            _dragScrollVerticalDir = dir;
            PumpTimer(dir);
        }

        private void PumpTimer(int dir)
        {
            if (dir != 0)
            {
                EnsureDragScrollTimer();
                if (!_dragScrollTimer.IsEnabled) _dragScrollTimer.Start();
            }
            else if (_dragScrollTimer != null)
            {
                _dragScrollTimer.Stop();
            }
        }

        private void StopDragAutoScroll()
        {
            _dragScrollDir = 0;
            _dragScrollVerticalDir = 0;
            _dragScrollVerticalTarget = null;
            if (_dragScrollTimer != null) _dragScrollTimer.Stop();
        }

        // ---------- tab rendering ----------

        // 三种布局共用同一顺序：_tabs。分组是 _tabs 里的一段连续区段，
        // 渲染时在区段第一个成员的位置插入“分组头 + 成员 + 同色线”。
        // 这样纵向分组成员不会再掉到最下面，新标签也永远在最后。
        private void RebuildTabStrip()
        {
            RebuildHorizontalStrip();
            RebuildVerticalPanels();
            UpdateStatus();
        }

        private void RebuildHorizontalStrip()
        {
            if (_tabStripPanel == null || _settings.Layout == LayoutMode.Vertical) return;
            UpdateTabChipMetrics();
            SyncCompactColumnShare();
            _tabStripPanel.Children.Clear();
            var rendered = new HashSet<string>();
            foreach (var tab in _tabs)
            {
                if (string.IsNullOrEmpty(tab.GroupId))
                {
                    _tabStripPanel.Children.Add(MakeTabChip(tab, true, true,
                        _tabChipMin, _tabChipMax));
                    continue;
                }
                if (!rendered.Add(tab.GroupId)) continue;
                var group = _groups.FirstOrDefault(g => g.Id == tab.GroupId);
                if (group == null)
                {
                    tab.GroupId = "";
                    _tabStripPanel.Children.Add(MakeTabChip(tab, true, true,
                        _tabChipMin, _tabChipMax));
                    continue;
                }
                var firstMember = _tabs.FirstOrDefault(t => t.GroupId == group.Id);
                _tabStripPanel.Children.Add(MakeBoundaryGap(firstMember, true));
                _tabStripPanel.Children.Add(MakeHorizontalGroupSection(group));
            }
            _tabStripPanel.Children.Add(MakeNewTabButton());
            ScrollActiveTabIntoView();
            Dispatcher.BeginInvoke(new Action(UpdateTabStripScrollButtons),
                DispatcherPriority.Loaded);
        }

        // 标签越多，chip 越窄；缩到 80px 后再靠横向滚动，和 Edge 的紧凑条一致。
        private void UpdateTabChipMetrics()
        {
            var count = _tabs.Count;
            if (count > 14) { _tabChipMin = 80; _tabChipMax = 110; }
            else if (count > 9) { _tabChipMin = 90; _tabChipMax = 130; }
            else if (count > 5) { _tabChipMin = 100; _tabChipMax = 160; }
            else { _tabChipMin = 110; _tabChipMax = 200; }
        }

        // 紧凑模式下把地址栏的一部分宽度让给标签条；超过 8 个标签开始让。
        private void SyncCompactColumnShare()
        {
            if (_settings.Layout != LayoutMode.Compact || _navigationRow == null) return;
            if (_navigationRow.ColumnDefinitions.Count < 5) return;
            var share = _tabs.Count > 8 ? 60.0 : 45.0;
            var tabColumn = _navigationRow.ColumnDefinitions[2];
            var addressColumn = _navigationRow.ColumnDefinitions[3];
            if (tabColumn.Width.IsStar && Math.Abs(tabColumn.Width.Value - share) < 0.1) return;
            tabColumn.Width = new GridLength(share, GridUnitType.Star);
            addressColumn.Width = new GridLength(100 - share, GridUnitType.Star);
        }

        private void RebuildVerticalPanels()
        {
            if (_railPanel == null) return;
            _railPanel.Children.Clear();
            var expanded = _settings.Layout == LayoutMode.Vertical && _settings.VerticalExpanded;
            BuildVerticalItems(_railPanel, expanded, expanded ? ExpandedWidth - 8 : 36);
            RebuildPopup();
        }

        private void RebuildPopup()
        {
            if (_popupPanel == null) return;
            _popupPanel.Children.Clear();
            BuildVerticalItems(_popupPanel, true, ExpandedWidth - 8);
        }

        // 纵向 rail 和 hover popup 用同一套顺序；宽度不同只影响 chip 尺寸。
        private void BuildVerticalItems(StackPanel panel, bool expanded, double chipWidth)
        {
            var rendered = new HashSet<string>();
            foreach (var tab in _tabs)
            {
                if (string.IsNullOrEmpty(tab.GroupId))
                {
                    panel.Children.Add(MakeTabChip(tab, expanded, expanded, chipWidth, chipWidth, true));
                    continue;
                }
                if (!rendered.Add(tab.GroupId)) continue;
                var group = _groups.FirstOrDefault(g => g.Id == tab.GroupId);
                if (group == null)
                {
                    tab.GroupId = "";
                    panel.Children.Add(MakeTabChip(tab, expanded, expanded, chipWidth, chipWidth, true));
                    continue;
                }
                var firstMember = _tabs.FirstOrDefault(t => t.GroupId == group.Id);
                panel.Children.Add(MakeBoundaryGap(firstMember, false));
                panel.Children.Add(MakeVerticalGroupSection(group, expanded, chipWidth,
                    expanded ? chipWidth + 4 : RailWidth));
            }
            panel.Children.Add(MakeNewTabButton());
        }

        private IEnumerable<TabModel> VisibleTabs()
        {
            // 顺序以 _tabs 为准；折叠分组只隐藏成员，不改变其他标签的位置。
            foreach (var tab in _tabs)
            {
                if (!string.IsNullOrEmpty(tab.GroupId))
                {
                    var group = _groups.FirstOrDefault(g => g.Id == tab.GroupId);
                    if (group != null && group.IsCollapsed) continue;
                }
                yield return tab;
            }
        }

        private Button MakeNewTabButton()
        {
            var button = IconButton("+", L.T("新建标签 (Ctrl+T)"), () => NewTab());
            button.Tag = "newtab";
            button.Margin = new Thickness(2);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            return button;
        }

        // 横向滚动后把当前标签滚进可视区；标题频繁变化时 Rebuild 也不会把人甩出去。
        private void ScrollActiveTabIntoView()
        {
            // 构造阶段（Loaded 之前）BringIntoView 会把布局树卡进循环；
            // 启动时的活动标签不需要滚动，等窗口真正显示后再跟随。
            if (!_shellReady) return;
            if (_tabStripPanel == null || _active == null) return;
            var chip = FindTabChip(_tabStripPanel, _active);
            if (chip != null) chip.BringIntoView();
        }

        private static FrameworkElement FindTabChip(StackPanel panel, TabModel tab)
        {
            if (panel == null) return null;
            foreach (var child in panel.Children)
            {
                var fe = child as FrameworkElement;
                if (fe == null) continue;
                if (fe.Tag is TabModel && ReferenceEquals(fe.Tag, tab)) return fe;
                var grid = fe as Grid;
                if (grid == null) continue;
                foreach (var stack in grid.Children.OfType<StackPanel>())
                {
                    var found = FindTabChip(stack, tab);
                    if (found != null) return found;
                }
            }
            return null;
        }

        // 分组区段之间的边界落点：把标签放成平铺状态，不强行加入相邻分组。
        // gap 只留 6px，细线居中，落点明确；分组内部落点才用分组自定义色。
        private FrameworkElement MakeBoundaryGap(TabModel anchor, bool horizontal)
        {
            var gap = new Border
            {
                Width = horizontal ? 6 : double.NaN,
                Height = horizontal ? double.NaN : 6,
                Background = Brushes.Transparent,
                AllowDrop = true,
                Tag = "section-gap"
            };
            var caret = new Border
            {
                Width = horizontal ? 2 : double.NaN,
                Height = horizontal ? double.NaN : 2,
                HorizontalAlignment = horizontal
                    ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
                VerticalAlignment = horizontal
                    ? VerticalAlignment.Stretch : VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Tag = "gap-caret"
            };
            caret.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            gap.Child = caret;
            gap.DragOver += (s, e) =>
            {
                PumpDragAutoScroll(e);
                var tab = e.Data.GetData(typeof(TabModel)) as TabModel;
                var group = e.Data.GetData(typeof(TabGroup)) as TabGroup;
                if (tab == null && group == null) return;
                if (group != null && anchor != null && group.Id == anchor.GroupId)
                {
                    caret.Visibility = Visibility.Collapsed;
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    return;
                }
                caret.Visibility = Visibility.Visible;
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            };
            gap.DragLeave += (s, e) =>
            {
                caret.Visibility = Visibility.Collapsed;
                StopDragAutoScroll();
            };
            gap.Drop += (s, e) =>
            {
                StopDragAutoScroll();
                caret.Visibility = Visibility.Collapsed;
                var tab = e.Data.GetData(typeof(TabModel)) as TabModel;
                if (tab != null) MoveTabToBoundary(tab, anchor);
                else
                {
                    var group = e.Data.GetData(typeof(TabGroup)) as TabGroup;
                    if (group != null && anchor != null) MoveGroupAt(group, anchor, false);
                }
                e.Handled = true;
            };
            return gap;
        }

        // 横向分组区段：分组头在左，成员在右，顶部一条同色线贯穿整个区段。
        private FrameworkElement MakeHorizontalGroupSection(TabGroup group)
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(MakeGroupHeader(group, true));
            if (!group.IsCollapsed)
            {
                foreach (var tab in _tabs.Where(t => t.GroupId == group.Id))
                    stack.Children.Add(MakeTabChip(tab, true, true,
                        _tabChipMin, _tabChipMax));
            }

            var grid = new Grid
            {
                Tag = group,
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(stack);
            var line = new Border
            {
                Height = 2,
                Margin = new Thickness(2, 0, 2, 0),
                Background = GroupBrush(group.Color),
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                CornerRadius = new CornerRadius(1),
                Opacity = 0.9
            };
            grid.Children.Add(line);
            AttachGroupSectionDropTarget(grid, line, group, true);
            return grid;
        }

        // 纵向分组区段：分组头在上，成员垂直堆叠，左侧一条同色线贯穿整个区段。
        private FrameworkElement MakeVerticalGroupSection(TabGroup group, bool expanded, double chipWidth, double sectionWidth)
        {
            var stack = new StackPanel { Orientation = Orientation.Vertical };
            stack.Children.Add(MakeGroupHeader(group, expanded));
            if (!group.IsCollapsed)
            {
                foreach (var tab in _tabs.Where(t => t.GroupId == group.Id))
                    stack.Children.Add(MakeTabChip(tab, expanded, expanded, chipWidth, chipWidth, true));
            }

            var grid = new Grid
            {
                Tag = group,
                Width = sectionWidth,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = Brushes.Transparent
            };
            grid.Children.Add(stack);
            var line = new Border
            {
                Width = 2,
                Margin = new Thickness(0, 2, 0, 2),
                Background = GroupBrush(group.Color),
                HorizontalAlignment = HorizontalAlignment.Left,
                IsHitTestVisible = false,
                CornerRadius = new CornerRadius(1),
                Opacity = 0.9
            };
            grid.Children.Add(line);
            AttachGroupSectionDropTarget(grid, line, group, false);
            return grid;
        }

        // 分组区段本身也是放置目标：落在分组线/成员之间的空白处 = 加入该组。
        // 成员 chip 的 Drop 会先截住；这里只处理冒泡上来的区段空白。
        private void AttachGroupSectionDropTarget(Panel grid, Border line,
            TabGroup group, bool horizontal)
        {
            grid.AllowDrop = true;
            var groupCaret = new Border
            {
                Width = horizontal ? 2 : double.NaN,
                Height = horizontal ? double.NaN : 2,
                HorizontalAlignment = horizontal
                    ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
                VerticalAlignment = horizontal
                    ? VerticalAlignment.Stretch : VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Tag = "group-drop-caret"
            };
            groupCaret.Background = GroupBrush(group.Color);
            grid.Children.Add(groupCaret);
            Action hideGroupCaret = () => groupCaret.Visibility = Visibility.Collapsed;
            Func<DragEventArgs, bool> overMember = e =>
            {
                var origin = e.OriginalSource as DependencyObject;
                while (origin != null && origin != grid)
                {
                    var fe = origin as FrameworkElement;
                    if (fe != null && fe.Tag is TabModel) return true;
                    try { origin = VisualTreeHelper.GetParent(origin); }
                    catch { origin = LogicalTreeHelper.GetParent(origin); }
                }
                return false;
            };

            grid.DragOver += (s, e) =>
            {
                PumpDragAutoScroll(e);
                var tabSource = e.Data.GetData(typeof(TabModel)) as TabModel;
                var groupSource = e.Data.GetData(typeof(TabGroup)) as TabGroup;
                if (tabSource == null && groupSource == null) return;
                if (groupSource != null && groupSource.Id == group.Id)
                {
                    hideGroupCaret();
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    return;
                }
                e.Effects = DragDropEffects.Move;
                grid.SetResourceReference(Panel.BackgroundProperty, "AccentSoftBrush");
                line.Opacity = 1;
                if (horizontal) line.Height = 3;
                else line.Width = 3;

                var after = horizontal
                    ? e.GetPosition(grid).X > grid.ActualWidth / 2
                    : e.GetPosition(grid).Y > grid.ActualHeight / 2;
                if (tabSource != null)
                {
                    // 落在成员上时由 chip 自己的线提示；落在组头/空白处追加到组尾。
                    if (overMember(e))
                    {
                        hideGroupCaret();
                    }
                    else
                    {
                        after = true;
                        if (horizontal)
                            groupCaret.HorizontalAlignment = HorizontalAlignment.Right;
                        else
                            groupCaret.VerticalAlignment = VerticalAlignment.Bottom;
                        groupCaret.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    if (horizontal)
                        groupCaret.HorizontalAlignment = after
                            ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                    else
                        groupCaret.VerticalAlignment = after
                            ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                    groupCaret.Visibility = Visibility.Visible;
                }
                e.Handled = true;
            };
            grid.DragLeave += (s, e) =>
            {
                hideGroupCaret();
                ResetGroupSectionHighlight(grid, line, horizontal);
                StopDragAutoScroll();
            };
            grid.Drop += (s, e) =>
            {
                StopDragAutoScroll();
                hideGroupCaret();
                ResetGroupSectionHighlight(grid, line, horizontal);
                var after = horizontal
                    ? e.GetPosition(grid).X > grid.ActualWidth / 2
                    : e.GetPosition(grid).Y > grid.ActualHeight / 2;
                var tabSource = e.Data.GetData(typeof(TabModel)) as TabModel;
                if (tabSource != null)
                {
                    if (tabSource.GroupId != group.Id) MoveTabIntoGroup(tabSource, group);
                }
                else
                {
                    var groupSource = e.Data.GetData(typeof(TabGroup)) as TabGroup;
                    if (groupSource != null && groupSource.Id != group.Id)
                    {
                        var anchor = _tabs.FirstOrDefault(t => t.GroupId == group.Id);
                        if (anchor != null) MoveGroupAt(groupSource, anchor, after);
                    }
                }
                e.Handled = true;
            };
        }

        private static void ResetGroupSectionHighlight(Panel grid, Border line,
            bool horizontal)
        {
            grid.Background = Brushes.Transparent;
            line.Opacity = 0.9;
            if (horizontal) line.Height = 2;
            else line.Width = 2;
        }

        // 顶部固定行：左边留空，右边图钉；固定态图钉带一条同色斜线，
        // 表示“划掉/取消固定”。收起和展开都保留这一行，标签位置不上下跳。
        private FrameworkElement MakeHeaderRow(bool forRail)
        {
            // 收起/展开都必须保留这一行，标签位置才不会上下跳。
            var row = new Grid { Height = 22, Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var pin = MakeVerticalPinButton();
            if (forRail) _railPin = pin; else _popupPin = pin;
            pin.HorizontalAlignment = HorizontalAlignment.Right;
            pin.VerticalAlignment = VerticalAlignment.Center;
            pin.Margin = new Thickness(0, 0, 4, 0);
            Grid.SetColumn(pin, 1);
            row.Children.Add(pin);
            SetPinState(pin);
            return row;
        }

        private Button MakeVerticalPinButton()
        {
            var content = new Grid { Width = 20, Height = 20 };
            var glyph = new TextBlock
            {
                Text = "📌",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            // 取消固定：图钉正中一条同色斜线划掉，而不是右上角红叉。
            var slash = new System.Windows.Shapes.Line
            {
                X1 = 2,
                Y1 = 2,
                X2 = 18,
                Y2 = 18,
                StrokeThickness = 1.5,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Tag = "pin-slash"
            };
            slash.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
                "TextBrush");
            content.Children.Add(glyph);
            content.Children.Add(slash);
            var button = IconButton("", L.T("固定垂直标签栏"), ToggleVerticalPin);
            button.Tag = "vertical-pin";
            button.Width = 20;
            button.Height = 20;
            button.Content = content;
            return button;
        }

        private void SetPinState(Button button)
        {
            if (button == null) return;
            button.ToolTip = L.T(_settings.VerticalExpanded
                ? "取消固定垂直标签栏" : "固定垂直标签栏");
            if (button.Content is Grid content)
            {
                var slash = content.Children.OfType<System.Windows.Shapes.Line>()
                    .FirstOrDefault(t => Equals(t.Tag, "pin-slash"));
                if (slash != null)
                    slash.Visibility = _settings.VerticalExpanded
                        ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ToggleVerticalPin()
        {
            _settings.VerticalExpanded = !_settings.VerticalExpanded;
            SettingsStore.Save(_settings);
            if (!_settings.VerticalExpanded && _verticalPopup != null)
                _verticalPopup.IsOpen = false;
            ApplyLayout();
        }

        private FrameworkElement MakeGroupHeader(TabGroup group, bool withName)
        {
            var border = new Border
            {
                Height = 26,
                Margin = new Thickness(0, 2, 0, 2),
                CornerRadius = new CornerRadius(5),
                Cursor = Cursors.Hand,
                Tag = "group",
                AllowDrop = true
            };
            border.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 7, 0),
                Background = GroupBrush(group.Color)
            };
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

            // 分组头的拖放交给外层分组区段统一处理：用区段的自定义色
            // 插入线和高亮判断落点，避免头/线两套提示打架。

            // 分组头也是整组拖动的抓手；单击仍然折叠/展开。
            border.PreviewMouseLeftButtonDown += (s, e) =>
            {
                _groupDragCandidate = group;
                _groupDragStart = e.GetPosition(null);
                _groupDragInProgress = false;
            };
            border.MouseMove += (s, e) =>
            {
                if (_groupDragInProgress || _groupDragCandidate != group ||
                    e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(null);
                if (Math.Abs(p.X - _groupDragStart.X) < 4 &&
                    Math.Abs(p.Y - _groupDragStart.Y) < 4) return;
                _groupDragInProgress = true;
                SuspendVerticalCollapse();
                try { DragDrop.DoDragDrop(border, group, DragDropEffects.Move); }
                finally
                {
                    _groupDragInProgress = false;
                    _groupDragCandidate = null;
                    _lastGroupDragUtc = DateTime.UtcNow;
                    StopDragAutoScroll();
                    ResumeVerticalCollapse();
                }
            };
            border.MouseLeftButtonUp += (s, e) =>
            {
                var dragged = _groupDragInProgress;
                _groupDragCandidate = null;
                _groupDragInProgress = false;
                // DoDragDrop 的 finally 可能早于 Up；刚拖完的 Up 不能当成折叠点击。
                if (dragged ||
                    (DateTime.UtcNow - _lastGroupDragUtc).TotalMilliseconds < 250) return;
                group.IsCollapsed = !group.IsCollapsed;
                RebuildTabStrip();
                SaveSession();
            };
            border.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                ShowGroupMenu(group, border);
            };
            return border;
        }

        private ContextMenu BuildGroupMenu(TabGroup group, FrameworkElement target = null)
        {
            var menu = MakeMenu();
            menu.Items.Add(MakeMenuItem(L.T("重命名分组"), () => BeginRenameGroup(group, target)));
            var colors = new MenuItem { Header = L.T("分组颜色"), FontSize = 12 };
            Theme.StyleMenuItem(colors);
            for (var i = 0; i < GroupColorPalette.Length; i++)
            {
                var color = GroupColorPalette[i];
                var label = L.T(GroupColorNames[i]);
                var item = MakeMenuItem(label, () => SetGroupColor(group, color));
                item.Icon = MakeColorSwatch(color);
                if (SameColor(group.Color, color)) item.Header = "✓ " + label;
                colors.Items.Add(item);
            }
            menu.Items.Add(colors);
            menu.Items.Add(Theme.StyleSeparator(new Separator()));
            menu.Items.Add(MakeMenuItem(L.T("关闭分组（关闭全部标签）"), () => CloseGroup(group)));
            menu.Items.Add(MakeMenuItem(L.T("解散分组"), () => DisbandGroup(group)));
            return menu;
        }

        private void ShowGroupMenu(TabGroup group, FrameworkElement target)
        {
            SuspendVerticalCollapse();
            var menu = BuildGroupMenu(group, target);
            menu.Closed += (s, e) => ResumeVerticalCollapse();
            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        private void SetGroupColor(TabGroup group, string color)
        {
            if (group == null || string.IsNullOrEmpty(color)) return;
            group.Color = color;
            RebuildTabStrip();
            SaveSession();
        }

        private void BeginRenameGroup(TabGroup group, FrameworkElement target)
        {
            if (group == null) return;
            SuspendVerticalCollapse();
            var box = new TextBox
            {
                Text = group.Name,
                Width = 190,
                FontSize = 12,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 8)
            };
            box.SetResourceReference(Control.BackgroundProperty, "CanvasBrush");
            box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            box.SetResourceReference(Control.BorderBrushProperty, "LineBrush");
            var ok = new Button
            {
                Content = L.T("确定"),
                Width = 64,
                Height = 26,
                FontSize = 11
            };
            ok.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
            ok.SetResourceReference(Control.ForegroundProperty, "CanvasBrush");
            var cancel = new Button
            {
                Content = L.T("取消"),
                Width = 64,
                Height = 26,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0)
            };
            cancel.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
            cancel.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            var label = new TextBlock
            {
                Text = L.T("分组名称"),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 6)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var panel = new StackPanel();
            panel.Children.Add(label);
            panel.Children.Add(box);
            panel.Children.Add(buttons);
            var content = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10),
                Child = panel
            };
            content.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            content.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            var popup = new Popup
            {
                Placement = PlacementMode.Bottom,
                PlacementTarget = target ?? _tabStripPanel,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = content
            };
            void Commit()
            {
                var name = box.Text == null ? "" : box.Text.Trim();
                if (!string.IsNullOrEmpty(name)) group.Name = name;
                popup.IsOpen = false;
                RebuildTabStrip();
                SaveSession();
            }
            ok.Click += (s, e) => Commit();
            cancel.Click += (s, e) => popup.IsOpen = false;
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                else if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; }
            };
            popup.Closed += (s, e) => ResumeVerticalCollapse();
            popup.IsOpen = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                box.Focus();
                box.SelectAll();
            }), DispatcherPriority.Input);
        }

        // 关闭分组 = 关闭组内全部标签，和“解散”（保留标签）不同。
        private void CloseGroup(TabGroup group)
        {
            if (group == null) return;
            var members = _tabs.Where(t => t.GroupId == group.Id).ToList();
            if (members.Count == 0) return;
            foreach (var member in members) CloseTab(member);
            _groups.RemoveAll(g => g.Id == group.Id);
            RebuildTabStrip();
            SaveSession();
            _toast.Show(L.T("已关闭分组：") + group.Name);
        }

        private static Brush GroupBrush(string color)
        {
            try
            {
                var parsed = ColorConverter.ConvertFromString(color);
                if (parsed is Color) return new SolidColorBrush((Color)parsed);
            }
            catch { }
            return Brushes.Gray;
        }

        private static Border MakeColorSwatch(string color)
        {
            return new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                Background = GroupBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static bool SameColor(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
                string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static readonly string[] GroupColorPalette =
        {
            "#FF4D9D", "#4D9DFF", "#4DD18C", "#FFB84D", "#B47DFF", "#E05B5B"
        };

        private static readonly string[] GroupColorNames =
        {
            "粉色", "蓝色", "绿色", "橙色", "紫色", "红色"
        };

        private static readonly Random GroupColorRandom = new Random();

        private string PickGroupColor()
        {
            var free = GroupColorPalette
                .Where(c => !_groups.Any(g => SameColor(g.Color, c)))
                .ToList();
            if (free.Count == 0) free.AddRange(GroupColorPalette);
            return free[GroupColorRandom.Next(free.Count)];
        }

        private TabGroup CreateGroup(string name = null)
        {
            var group = new TabGroup
            {
                Name = name ?? (L.T("分组 ") + (_groups.Count + 1)),
                Color = PickGroupColor()
            };
            _groups.Add(group);
            return group;
        }

        private void CreateGroupForTab(TabModel tab)
        {
            if (tab == null) return;
            // 原地建组：不把标签搬到末尾。原来在别的组里就先脱离，
            // 脱离逻辑会保持旧组区段连续；平铺标签则保持原索引。
            if (!string.IsNullOrEmpty(tab.GroupId)) DetachFromGroupCore(tab);
            var group = CreateGroup();
            tab.GroupId = group.Id;
            RebuildTabStrip();
            SaveSession();
            _toast.Show(L.T("已新建分组：") + group.Name);
        }

        private void DetachFromGroup(TabModel tab)
        {
            if (tab == null || string.IsNullOrEmpty(tab.GroupId)) return;
            DetachFromGroupCore(tab);
            RebuildTabStrip();
            SaveSession();
        }

        // 移出分组时保持原分组区段连续：优先放回原组末尾；原组因此解散则保持原索引。
        private void DetachFromGroupCore(TabModel tab)
        {
            var groupId = tab.GroupId;
            var index = _tabs.IndexOf(tab);
            if (index < 0 || string.IsNullOrEmpty(groupId)) { tab.GroupId = ""; return; }
            tab.GroupId = "";
            _tabs.RemoveAt(index);
            var last = -1;
            for (var i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].GroupId == groupId) last = i;
            }
            _tabs.Insert(last >= 0 ? last + 1 : Math.Min(index, _tabs.Count), tab);
            _groups.RemoveAll(g => g.Id == groupId && !_tabs.Any(t => t.GroupId == groupId));
        }

        // 拖到分组头：移入目标分组并排在同组末尾；收起的分组自动展开，保证操作看得见。
        internal void MoveTabIntoGroup(TabModel tab, TabGroup group, bool created = false)
        {
            if (tab == null || group == null || tab.GroupId == group.Id) return;
            var oldGroupId = tab.GroupId;
            _tabs.Remove(tab);
            tab.GroupId = group.Id;
            var last = -1;
            for (var i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].GroupId == group.Id) last = i;
            }
            _tabs.Insert(last < 0 ? _tabs.Count : last + 1, tab);
            if (!string.IsNullOrEmpty(oldGroupId))
                _groups.RemoveAll(g => g.Id == oldGroupId && !_tabs.Any(t => t.GroupId == oldGroupId));
            group.IsCollapsed = false;
            RebuildTabStrip();
            SaveSession();
            _toast.Show((created ? L.T("已新建分组：") : L.T("已移入分组：")) + group.Name);
        }

        // 分组误建后必须能删掉：组内标签恢复平铺，分组本身从会话里移除。
        private void DisbandGroup(TabGroup group)
        {
            if (group == null) return;
            var name = group.Name;
            foreach (var tab in _tabs.Where(t => t.GroupId == group.Id).ToList())
                tab.GroupId = "";
            _groups.Remove(group);
            RebuildTabStrip();
            SaveSession();
            _toast.Show(L.T("已解散分组：") + name);
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

        private FrameworkElement MakeTabChip(TabModel tab, bool showTitle, bool showClose,
            double minWidth, double maxWidth, bool verticalInsert = false)
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
            // 内存常驻要有看得见的区别：淡色底 + 左侧竖条 + 悬停说明。
            // 三处背景写入统一走 applyBackground，避免鼠标移开后样式丢失。
            Action applyBackground = () =>
                border.SetResourceReference(Border.BackgroundProperty,
                    tab.Resident ? "AccentSoftBrush" : (active ? "SurfaceBrush" : "TabIdleBrush"));
            applyBackground();
            if (tab.Resident)
            {
                border.BorderThickness = new Thickness(2, 0, 0, 0);
                border.SetResourceReference(Border.BorderBrushProperty, "WarnBrush");
                border.ToolTip = L.T("内存常驻：这个站点的标签不会被自动释放");
            }
            if (active)
            {
                border.BorderThickness = tab.Resident
                    ? new Thickness(2, 0, 0, 2) : new Thickness(0, 0, 0, 2);
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

            // 拖放插入提示：横向布局贴 chip 左/右边缘的竖线，纵向布局贴
            // chip 上/下边缘的横线；两种情况都不参与布局。
            var caret = new Border
            {
                Width = verticalInsert ? double.NaN : 2,
                Height = verticalInsert ? 2 : double.NaN,
                Margin = verticalInsert
                    ? new Thickness(2, 0, 2, 0) : new Thickness(0, 2, 0, 2),
                HorizontalAlignment = verticalInsert
                    ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
                VerticalAlignment = verticalInsert
                    ? VerticalAlignment.Top : VerticalAlignment.Stretch,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Tag = "drop-caret"
            };
            var caretGroup = string.IsNullOrEmpty(tab.GroupId) ? null
                : _groups.FirstOrDefault(g => g.Id == tab.GroupId);
            if (caretGroup != null) caret.Background = GroupBrush(caretGroup.Color);
            else caret.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            Grid.SetColumnSpan(caret, Math.Max(1, grid.ColumnDefinitions.Count));
            grid.Children.Add(caret);

            // 两个平铺标签中心重叠时的特殊高亮：整块描边，和插入线区分开。
            var halo = new Border
            {
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Tag = "group-halo"
            };
            halo.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            Grid.SetColumnSpan(halo, Math.Max(1, grid.ColumnDefinitions.Count));
            grid.Children.Add(halo);

            border.Child = grid;
            Action hideCaret = () =>
            {
                caret.Visibility = Visibility.Collapsed;
                halo.Visibility = Visibility.Collapsed;
            };

            // 中键关闭标签：和主流浏览器一致。
            border.MouseDown += (s, e) =>
            {
                if (e.ChangedButton != MouseButton.Middle) return;
                e.Handled = true;
                CloseTab(tab);
            };

            border.MouseLeftButtonUp += (s, e) =>
            {
                // 单个 Up 处理器：先取走状态再判断。旧版在隧道阶段置位、
                // 冒泡阶段判断，普通单击时 _dragCandidate 永远非空，
                // SelectTab 一次都走不到（B4）。
                var dragged = _dragInProgress;
                _dragCandidate = null;
                _dragInProgress = false;
                if (!dragged) SelectTab(tab);
            };
            border.MouseEnter += (s, e) =>
            {
                if (!active) border.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            };
            border.MouseLeave += (s, e) => applyBackground();
            border.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                ShowTabMenu(tab, border);
            };

            // 鼠标真的移动超过阈值才启动拖放，按下时的细微抖动不该把单击变成拖拽。
            border.PreviewMouseLeftButtonDown += (s, e) =>
            {
                _dragCandidate = tab;
                _dragStart = e.GetPosition(null);
                _dragInProgress = false;
            };
            border.MouseMove += (s, e) =>
            {
                if (_dragInProgress || _dragCandidate != tab ||
                    e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(null);
                if (Math.Abs(p.X - _dragStart.X) < 4 && Math.Abs(p.Y - _dragStart.Y) < 4) return;
                _dragInProgress = true;
                SuspendVerticalCollapse();
                try { DragDrop.DoDragDrop(border, tab, DragDropEffects.Move); }
                finally
                {
                    _dragInProgress = false;
                    _dragCandidate = null;
                    StopDragAutoScroll();
                    ResumeVerticalCollapse();
                }
            };
            Action<bool> showCaretAt = after =>
            {
                if (verticalInsert)
                    caret.VerticalAlignment = after ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                else
                    caret.HorizontalAlignment = after ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                caret.Visibility = Visibility.Visible;
            };
            Func<DragEventArgs, bool> afterPoint = e => verticalInsert
                ? e.GetPosition(border).Y > border.ActualHeight / 2
                : e.GetPosition(border).X > border.ActualWidth / 2;

            border.DragOver += (s, e) =>
            {
                PumpDragAutoScroll(e);
                var tabSource = e.Data.GetData(typeof(TabModel)) as TabModel;
                if (tabSource != null)
                {
                    if (tabSource == tab)
                    {
                        hideCaret();
                        e.Effects = DragDropEffects.None;
                        e.Handled = true;
                        return;
                    }
                    var point = e.GetPosition(border);
                    // 目标平铺时，无论源是平铺还是组内标签，中心重叠都建新组；
                    // 这样“从组里拖出来 + 和新标签重叠”可以一次完成。
                    var overlap = string.IsNullOrEmpty(tab.GroupId) &&
                                  IsGroupOverlap(point, border.ActualWidth,
                                      border.ActualHeight, verticalInsert);
                    if (overlap)
                    {
                        caret.Visibility = Visibility.Collapsed;
                        halo.Visibility = Visibility.Visible;
                        e.Effects = DragDropEffects.Move;
                        e.Handled = true;
                        return;
                    }
                    halo.Visibility = Visibility.Collapsed;
                    showCaretAt(afterPoint(e));
                    e.Effects = DragDropEffects.Move;
                    // 组内成员继续冒泡，让外层分组区段做整组高亮；平铺 chip 自己截住。
                    e.Handled = string.IsNullOrEmpty(tab.GroupId);
                    return;
                }

                var groupSource = e.Data.GetData(typeof(TabGroup)) as TabGroup;
                if (groupSource == null)
                {
                    hideCaret();
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    return;
                }
                halo.Visibility = Visibility.Collapsed;
                // 拖到组内成员时，由外层区段统一显示整组插入线。
                if (!string.IsNullOrEmpty(tab.GroupId))
                {
                    hideCaret();
                    e.Handled = false;
                    return;
                }
                showCaretAt(afterPoint(e));
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            };
            border.DragLeave += (s, e) =>
            {
                hideCaret();
                StopDragAutoScroll();
            };
            border.Drop += (s, e) =>
            {
                hideCaret();
                StopDragAutoScroll();
                var tabSource = e.Data.GetData(typeof(TabModel)) as TabModel;
                if (tabSource != null)
                {
                    if (tabSource != tab)
                    {
                        var dropPoint = e.GetPosition(border);
                        var overlap = string.IsNullOrEmpty(tab.GroupId) &&
                                      IsGroupOverlap(dropPoint, border.ActualWidth,
                                          border.ActualHeight, verticalInsert);
                        if (overlap)
                        {
                            CreateGroupFromPair(tabSource, tab);
                            e.Handled = true;
                            return;
                        }
                        var after = afterPoint(e);
                        if (!string.IsNullOrEmpty(tab.GroupId))
                        {
                            var group = _groups.FirstOrDefault(g => g.Id == tab.GroupId);
                            if (group != null)
                            {
                                if (tabSource.GroupId == group.Id)
                                    ReorderTabsAt(tabSource, tab, after);
                                else
                                    MoveTabIntoGroupAt(tabSource, group, tab, after);
                            }
                        }
                        else if (!string.IsNullOrEmpty(tabSource.GroupId))
                        {
                            // 拖到平铺标签 = 离开原分组，落在目标位置。
                            MoveTabOutOfGroupAt(tabSource, tab, after);
                        }
                        else
                        {
                            ReorderTabsAt(tabSource, tab, after);
                        }
                    }
                    e.Handled = true;
                    return;
                }

                var groupSource = e.Data.GetData(typeof(TabGroup)) as TabGroup;
                if (groupSource == null) { e.Handled = true; return; }
                // 组内目标由区段处理；平铺目标在这里整组落在它前/后。
                if (!string.IsNullOrEmpty(tab.GroupId)) return;
                MoveGroupAt(groupSource, tab, afterPoint(e));
                e.Handled = true;
            };
            return border;
        }

        // 标签之间的拖放只负责排序：同组内交换顺序，跨组不偷偷改 GroupId
        //（移入分组统一走分组头 / 区段的 Drop）。
        private void ReorderTabs(TabModel source, TabModel target)
        {
            ReorderTabsAt(source, target, false);
        }

        private void ReorderTabsAt(TabModel source, TabModel target, bool after)
        {
            if (source == null || target == null || source == target) return;
            if (source.GroupId != target.GroupId) return;
            var from = _tabs.IndexOf(source);
            if (from < 0) return;
            _tabs.RemoveAt(from);
            var to = _tabs.IndexOf(target);
            if (to < 0)
            {
                _tabs.Insert(from, source);
                return;
            }
            _tabs.Insert(after ? to + 1 : to, source);
            RebuildTabStrip();
            SaveSession();
        }

        // 成员附近落下：插到目标成员前/后，而不是一律追加到分组末尾。
        internal void MoveTabIntoGroupAt(TabModel tab, TabGroup group, TabModel target, bool after)
        {
            if (tab == null || group == null || tab == target) return;
            if (tab.GroupId == group.Id)
            {
                ReorderTabsAt(tab, target, after);
                return;
            }
            var oldGroupId = tab.GroupId;
            _tabs.Remove(tab);
            tab.GroupId = group.Id;
            var to = target == null || target.GroupId != group.Id
                ? _tabs.Count : _tabs.IndexOf(target);
            if (to < 0) to = _tabs.Count;
            else if (after) to++;
            _tabs.Insert(to, tab);
            if (!string.IsNullOrEmpty(oldGroupId))
                _groups.RemoveAll(g => g.Id == oldGroupId && !_tabs.Any(t => t.GroupId == oldGroupId));
            group.IsCollapsed = false;
            RebuildTabStrip();
            SaveSession();
            _toast.Show(L.T("已移入分组：") + group.Name);
        }

        // 拖到平铺标签：离开原分组，落在目标标签前/后；原组空了就删除。
        private void MoveTabOutOfGroupAt(TabModel tab, TabModel target, bool after)
        {
            if (tab == null || target == null || tab == target) return;
            if (string.IsNullOrEmpty(tab.GroupId))
            {
                ReorderTabsAt(tab, target, after);
                return;
            }
            var groupId = tab.GroupId;
            var from = _tabs.IndexOf(tab);
            if (from < 0) return;
            tab.GroupId = "";
            _tabs.RemoveAt(from);
            var to = _tabs.IndexOf(target);
            if (to < 0)
                _tabs.Insert(Math.Min(from, _tabs.Count), tab);
            else
                _tabs.Insert(after ? to + 1 : to, tab);
            _groups.RemoveAll(g => g.Id == groupId && !_tabs.Any(t => t.GroupId == groupId));
            RebuildTabStrip();
            SaveSession();
            _toast.Show(L.T("已移出分组"));
        }

        // 拖到标签条/侧栏空白：移出分组并落到末尾。
        private void DropTabOnBlank(TabModel tab)
        {
            if (tab == null) return;
            var groupId = tab.GroupId;
            if (!string.IsNullOrEmpty(groupId))
            {
                tab.GroupId = "";
                _groups.RemoveAll(g => g.Id == groupId && !_tabs.Any(t => t.GroupId == groupId));
                _toast.Show(L.T("已移出分组"));
            }
            _tabs.Remove(tab);
            _tabs.Add(tab);
            RebuildTabStrip();
            SaveSession();
        }

        // 落到分组区段边界：保持平铺（不加入任何组），插在 anchor 前面。
        // anchor 可能是源标签自己（把组首拖到组前），此时插到同组剩余成员前面。
        private void MoveTabToBoundary(TabModel tab, TabModel anchor)
        {
            if (tab == null) return;
            var groupId = tab.GroupId;
            var from = _tabs.IndexOf(tab);
            if (from < 0) return;
            var movedOut = !string.IsNullOrEmpty(groupId);
            tab.GroupId = "";
            _tabs.RemoveAt(from);
            var to = anchor == null ? _tabs.Count : _tabs.IndexOf(anchor);
            if (to < 0)
            {
                var remaining = string.IsNullOrEmpty(groupId) ? null
                    : _tabs.FirstOrDefault(t => t.GroupId == groupId);
                to = remaining != null ? _tabs.IndexOf(remaining)
                    : Math.Min(from, _tabs.Count);
            }
            _tabs.Insert(to, tab);
            if (movedOut)
            {
                _groups.RemoveAll(g => g.Id == groupId &&
                    !_tabs.Any(t => t.GroupId == groupId));
                _toast.Show(L.T("已移出分组"));
            }
            RebuildTabStrip();
            SaveSession();
        }

        // 两个平铺标签拖到彼此中心区域 = 新建分组。中心 40% 视为“高度重叠”，
        // 边缘留给正常的前/后排序。
        internal static bool IsGroupOverlap(Point point, double width, double height,
            bool vertical)
        {
            var size = vertical ? height : width;
            if (size <= 0) return false;
            var ratio = (vertical ? point.Y : point.X) / size;
            return ratio >= 0.3 && ratio <= 0.7;
        }

        internal void CreateGroupFromPair(TabModel source, TabModel target)
        {
            if (source == null || target == null || source == target) return;
            if (!string.IsNullOrEmpty(target.GroupId)) return;
            var sourceIndex = _tabs.IndexOf(source);
            var targetIndex = _tabs.IndexOf(target);
            if (sourceIndex < 0 || targetIndex < 0) return;
            var oldGroupId = source.GroupId;
            _tabs.Remove(source);
            var insertAt = _tabs.IndexOf(target);
            if (sourceIndex > targetIndex) insertAt++;
            _tabs.Insert(insertAt, source);
            var group = CreateGroup();
            source.GroupId = group.Id;
            target.GroupId = group.Id;
            if (!string.IsNullOrEmpty(oldGroupId))
                _groups.RemoveAll(g => g.Id == oldGroupId && !_tabs.Any(t => t.GroupId == oldGroupId));
            RebuildTabStrip();
            SaveSession();
            _toast.Show(L.T("已新建分组：") + group.Name);
        }

        // 整组拖动：以目标标签（或目标分组）为锚点，把成员区段整体搬过去。
        internal void MoveGroupAt(TabGroup source, TabModel targetTab, bool after)
        {
            if (source == null || targetTab == null) return;
            if (targetTab.GroupId == source.Id) return;
            var members = _tabs.Where(t => t.GroupId == source.Id).ToList();
            if (members.Count == 0) return;
            var targetGroupId = targetTab.GroupId;
            var anchor = targetTab;
            if (!string.IsNullOrEmpty(targetGroupId))
            {
                var targetMembers = _tabs.Where(t => t.GroupId == targetGroupId).ToList();
                if (targetMembers.Count == 0) return;
                anchor = after ? targetMembers[targetMembers.Count - 1] : targetMembers[0];
            }
            if (_tabs.IndexOf(anchor) < 0) return;
            foreach (var member in members) _tabs.Remove(member);
            var to = _tabs.IndexOf(anchor);
            if (to < 0) to = _tabs.Count;
            else if (after) to++;
            _tabs.InsertRange(to, members);
            RebuildTabStrip();
            SaveSession();
        }

        private void MoveGroupToEnd(TabGroup group)
        {
            if (group == null) return;
            var members = _tabs.Where(t => t.GroupId == group.Id).ToList();
            if (members.Count == 0) return;
            foreach (var member in members) _tabs.Remove(member);
            _tabs.AddRange(members);
            RebuildTabStrip();
            SaveSession();
        }

        private void ShowTabMenu(TabModel tab, FrameworkElement target)
        {
            SuspendVerticalCollapse();
            var menu = MakeMenu();
            menu.Closed += (s, e) => ResumeVerticalCollapse();
            menu.Items.Add(MakeMenuItem(tab.Resident ? L.T("取消内存常驻") : L.T("内存常驻（不自动释放）"), () => ToggleResident(tab)));
            menu.Items.Add(MakeMenuItem(L.T("立即释放此标签内存"), () => ReleaseTab(tab)));
            menu.Items.Add(MakeMenuItem(L.T("新建分组并移入"), () => CreateGroupForTab(tab)));
            if (!string.IsNullOrEmpty(tab.GroupId))
                menu.Items.Add(MakeMenuItem(L.T("移出分组"), () => DetachFromGroup(tab)));
            if (IsDuplicatable(tab))
                menu.Items.Add(MakeMenuItem(L.T("复制标签"), () => DuplicateTab(tab)));
            menu.Items.Add(Theme.StyleSeparator(new Separator()));
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
            // A5：菜单样式统一在 Theme 里定义，三处菜单共用，漏一处就会留白条。
            return Theme.StyleMenu(new ContextMenu());
        }

        private MenuItem MakeMenuItem(string text, Action action)
        {
            var item = Theme.StyleMenuItem(new MenuItem { Header = text, FontSize = 12 });
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
            menu.Items.Add(Theme.StyleSeparator(new Separator()));
            menu.Items.Add(MakeMenuItem(L.T("历史记录 (Ctrl+H)"), OpenHistory));
            menu.Items.Add(MakeMenuItem(L.T("书签 (Ctrl+Shift+O)"), OpenBookmarks));
            menu.Items.Add(MakeMenuItem(L.T("下载 (Ctrl+J)"), OpenDownloads));
            menu.Items.Add(MakeMenuItem(L.T("用户脚本"), OpenScripts));
            menu.Items.Add(MakeMenuItem(L.T("设置"), OpenSettings));
            menu.Items.Add(Theme.StyleSeparator(new Separator()));
            menu.Items.Add(MakeMenuItem(L.T("放大 (Ctrl++)"), () => Zoom(_browser.ZoomIn)));
            menu.Items.Add(MakeMenuItem(L.T("缩小 (Ctrl+-)"), () => Zoom(_browser.ZoomOut)));
            menu.Items.Add(MakeMenuItem(L.T("缩放重置 (Ctrl+0)"), () => Zoom(_browser.ZoomReset)));
            menu.Items.Add(Theme.StyleSeparator(new Separator()));
            menu.Items.Add(MakeMenuItem(L.T("内存明细面板 (Ctrl+Shift+M)"), () => OpenMemoryPanel(true)));
            var presets = new MenuItem
            {
                Header = L.T("内存档位：") + _settings.Memory.PresetName,
                FontSize = 12
            };
            Theme.StyleMenuItem(presets);
            presets.Items.Add(MakeMenuItem((_settings.Memory.Preset == MemoryPreset.Conservative ? "● " : "　 ") + L.T("少重载"),
                () => SetMemoryPreset(MemoryPreset.Conservative)));
            presets.Items.Add(MakeMenuItem((_settings.Memory.Preset == MemoryPreset.Balanced ? "● " : "　 ") + L.T("标准"),
                () => SetMemoryPreset(MemoryPreset.Balanced)));
            presets.Items.Add(MakeMenuItem((_settings.Memory.Preset == MemoryPreset.Aggressive ? "● " : "　 ") + L.T("省内存"),
                () => SetMemoryPreset(MemoryPreset.Aggressive)));
            menu.Items.Add(presets);
            menu.Items.Add(Theme.StyleSeparator(new Separator()));
            menu.Items.Add(MakeMenuItem(_settings.VerticalExpanded ? L.T("垂直标签：改为悬浮展开") : L.T("垂直标签：固定展开"), () =>
            {
                _settings.VerticalExpanded = !_settings.VerticalExpanded;
                SettingsStore.Save(_settings);
                ApplyLayout();
            }));
            menu.Items.Add(Theme.StyleSeparator(new Separator()));
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
            // 全屏期间不与最大化/还原互踩：外壳已隐藏，状态由全屏状态机独占。
            if (_immersive) return;
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        // ---------- A2：F11 真全屏（按当前显示器 rcMonitor 整屏）----------

        private void ToggleImmersive()
        {
            if (_immersive) ExitFullscreen();
            else EnterFullscreen();
        }

        // 所有浮层都是 Popup（独立 HWND），外壳 Collapsed 也盖不住它们。
        // 顺手停掉会重新打开浮层的定时器，避免全屏里又弹出地址栏联想/内存面板。
        private void CloseFloatingLayers()
        {
            foreach (var timer in new[] { _suggestTimer, _suggestCloseTimer,
                                          _expandTimer, _collapseTimer,
                                          _badgeHoverTimer, _badgeCloseTimer })
                if (timer != null) timer.Stop();
            try { if (_memoryPanel != null) _memoryPanel.Close(); } catch { }
            try { CloseSuggestions(); } catch { }
            if (_verticalPopup != null) _verticalPopup.IsOpen = false;
            try { if (_toast != null) _toast.Hide(); } catch { }
        }

        private void EnterFullscreen()
        {
            // 必须在改 WindowState 之前取：从 Maximized 还原时窗口会先回到
            // RestoreBounds，可能落到另一块屏，MonitorFromWindow 就选错屏了。
            var monitor = MonitorRectDip();
            _fsPrevState = WindowState;
            // Maximized 时 Left/Top/Width/Height 是最大化矩形，真正要恢复的
            // 是 RestoreBounds；否则退出全屏后会留下一个满屏的普通窗口。
            _fsPrevBounds = WindowState == WindowState.Maximized && !RestoreBounds.IsEmpty
                ? RestoreBounds : new Rect(Left, Top, Width, Height);
            _fsPrevTopmost = Topmost;
            _fsPrevResizeMode = ResizeMode;

            _fullscreenBusy = true;
            _immersive = true;   // A3 钩子与几何落盘同时让路
            try
            {
                ApplyLayout();                     // 收起工具栏 / 状态栏 / rail
                CloseFloatingLayers();             // 收起所有 Popup 浮层
                WindowState = WindowState.Normal;  // Maximized 下改不了窗口矩形
                ResizeMode = ResizeMode.NoResize;  // 边缘不能把全屏拖回普通尺寸
                Width = monitor.Width;
                Height = monitor.Height;
                Left = monitor.Left;
                Top = monitor.Top;
                Topmost = true;                    // 盖住任务栏（含非自动隐藏）
            }
            finally { _fullscreenBusy = false; }
        }

        private void ExitFullscreen()
        {
            _fullscreenBusy = true;
            try
            {
                Topmost = _fsPrevTopmost;
                ResizeMode = _fsPrevResizeMode;
                WindowState = WindowState.Normal;
                var b = _fsPrevBounds;
                if (b.Width > 100 && b.Height > 100 &&
                    !double.IsNaN(b.Width) && !double.IsNaN(b.Height) &&
                    !double.IsInfinity(b.Width) && !double.IsInfinity(b.Height))
                {
                    // 先把普通态矩形写回去，WPF 的 RestoreBounds 才不会被
                    // 全屏矩形污染（之后用户点「向下还原」才拿得到原尺寸）。
                    Width = b.Width;
                    Height = b.Height;
                    if (IsSaneWindowCoord(b.Left) && IsSaneWindowCoord(b.Top))
                    {
                        Left = b.Left;
                        Top = b.Top;
                    }
                }
                // 必须先退出全屏再还原 Maximized：A3 的 WM_GETMINMAXINFO
                // 要重新生效，否则最大化会按 rcMonitor 撑满并盖住任务栏。
                _immersive = false;
                if (_fsPrevState == WindowState.Maximized) WindowState = WindowState.Maximized;
                ApplyLayout();                     // 外壳恢复
            }
            finally { _fullscreenBusy = false; }
        }

        // ---------- tab lifecycle ----------

        private void NewTab(string url = null, string sourceSite = null, bool secret = false)
        {
            var tab = new TabModel { IsSecret = secret, Title = secret ? L.T("文档") : L.T("新标签页") };
            _tabs.Add(tab);
            _browser.RegisterStartPage(tab, MakeStartPage(tab));
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

        // 起始页统一在这里建：搜索引擎下拉的改动要抛回 MainWindow 落盘，
        // 页面本身不直接写 settings.json。
        private StartPage MakeStartPage(TabModel tab)
        {
            var start = new StartPage(_settings);
            start.NavigateRequested += input => Navigate(tab, input);
            start.SearchEngineChanged += OnSettingChanged;
            return start;
        }

        private FrameworkElement BuildInternalView(TabModel tab, string sourceSite = null)
        {
            if (tab.IsStartPage || string.IsNullOrEmpty(tab.InternalPage))
            {
                return MakeStartPage(tab);
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

        // 内部页统一口径：一律新开标签，已经开着就切过去，不重复创建。
        // 地址栏主动输入 nano://xxx 仍按普通导航覆盖当前页，那是用户显式导航。
        private void OpenInternalTab(string url, string pageKey)
        {
            var existing = _tabs.FirstOrDefault(t => t.InternalPage == pageKey);
            if (existing != null) { SelectTab(existing); return; }
            NewTab(url);
        }

        private void OpenHistory() { OpenInternalTab(InternalPages.History, "history"); }

        private void OpenBookmarks() { OpenInternalTab(InternalPages.Bookmarks, "bookmarks"); }

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
            // 搜索引擎下拉自己已经同步了所有实例，这里只需要落盘。
            if (kind == "search") return;
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
                    _browser.ReplaceLocalView(tab, MakeStartPage(tab));
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
            // 关掉最后一个标签时先补一个起始页，再走正常关闭流程。
            // 直接 Close() 会触发 ShutdownMode.OnMainWindowClose 把整个进程带走。
            if (_tabs.Count == 1) NewTab();
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
            // 没有分组时不再显示「0 个分组」：垂直栏里标签本来就是平铺的，
            // 这行字容易被读成「所有标签都包在分组 0 下面」。
            var groupPart = _groups.Count > 0
                ? _groups.Count + L.T(" 个分组 · 内存档 ") + _settings.Memory.PresetName
                : L.T("内存档 ") + _settings.Memory.PresetName;
            _statusText.Text = layout + " · " + ThemeText() + " · " + _tabs.Count + L.T(" 个标签 · ") +
                               groupPart + extra;
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
            // 同一站点的多个标签会一起改状态，必须重建标签条才能看到样式变化。
            RebuildTabStrip();
            SaveSession();
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
            // WPF 的 WebView2 控件会把加速键转发回 WPF 层，所以只放行消息泵
            // 是不够的：这里再截一次 F5 就会主动 Reload，页面热键照样被盖掉。
            // 两层都放行，页面 JS 才能拿到 keydown 并 preventDefault。
            if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None) return;
            if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control) return;
            if (HandleShortcut(e.Key, Keyboard.Modifiers, e.SystemKey)) e.Handled = true;
        }

        // WebView2 是独立 HWND：页面聚焦时 WPF 收不到按键，宿主快捷键只能在
        // 消息泵层面分发。但刷新类按键必须放给内核，见下面。
        private void OnThreadPreprocessMessage(ref System.Windows.Interop.MSG msg, ref bool handled)
        {
            if (handled) return;
            const int WM_KEYDOWN = 0x0100;
            const int WM_SYSKEYDOWN = 0x0104;
            if (msg.message != WM_KEYDOWN && msg.message != WM_SYSKEYDOWN) return;

            var key = KeyInterop.KeyFromVirtualKey((int)msg.wParam);
            var modifiers = Keyboard.Modifiers;
            var ctrl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            var shift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            var alt = (modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

            // 刷新键交给 WebView2：页面 JS 先收到 keydown，可以 preventDefault()
            // 改成自己的行为（pgadmin 的 F5 执行 SQL），页面不拦时内核自己刷新。
            // 宿主在消息泵层无条件吞掉 WM_KEYDOWN 的话，页面永远收不到（B7）。
            // Ctrl+F5 仍由宿主做硬刷新；F11/F12 仍是浏览器级行为。
            if (key == Key.F5 && !ctrl && !shift && !alt) return;
            if (key == Key.R && ctrl && !shift && !alt) return;

            // 只截带修饰键的组合和功能键，别吞掉正常打字。
            // Esc 只在真全屏期间截：普通浏览时网页要用 Esc 关自己的弹层，
            // 但全屏时外壳隐藏、WPF 收不到键，必须由消息泵层放行给宿主退出。
            if (modifiers == ModifierKeys.None &&
                key != Key.F11 && key != Key.F12 &&
                !(key == Key.Escape && _immersive)) return;
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
            if (key == Key.H && !shift) { OpenHistory(); return true; }
            if (key == Key.O && shift) { OpenBookmarks(); return true; }
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

            if (HasArg("--fixtest"))
            {
                var unusedFix = new FixSelfTest(this).RunAsync();
                return;
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

            // A5：菜单样式预览用 Canvas 覆盖层（ContextMenu 是独立 HWND，截图抓不到）。
            if (HasArg("--menupreview")) BuildMenuPreview();

            // A2：真全屏状态的截图证据；只用于 --screenshot 的预览进程。
            if (HasArg("--fullscreen")) ToggleImmersive();

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

        // A5 视觉验证：ContextMenu 是独立 HWND，RenderTargetBitmap.Render(window)
        // 抓不到。改用主窗口可视树里的 Canvas 覆盖层（不用 Popup）渲染同一套菜单
        // 画刷：Icon 列、普通行、高亮行、分隔线、二级菜单底都能被截到。
        // 预览用的是 Theme 里同一批主题键，配合 --fixtest 的画刷/模板断言。
        private void BuildMenuPreview()
        {
            var layer = new Canvas { IsHitTestVisible = false };
            Grid.SetRow(layer, 0);
            Grid.SetRowSpan(layer, 3);
            _root.Children.Add(layer);

            var card = new Border
            {
                Width = 300,
                Padding = new Thickness(6),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1)
            };
            card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

            var stack = new StackPanel();
            stack.Children.Add(MenuPreviewRow(L.T("菜单"), false));
            stack.Children.Add(MenuPreviewRow(L.T("设置"), true));   // 高亮态
            var separator = Theme.StyleSeparator(new Separator());
            separator.Margin = new Thickness(4, 3, 4, 3);
            stack.Children.Add(separator);
            stack.Children.Add(MenuPreviewRow(L.T("用户脚本"), false));

            // 二级菜单示例：模拟子菜单 Popup 的底和边框，暗色下不能是白底。
            var submenu = new Border
            {
                Margin = new Thickness(30, 4, 0, 2),
                Padding = new Thickness(4),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4)
            };
            submenu.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            submenu.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            submenu.Child = MenuPreviewRow(L.T("新建标签"), false);
            stack.Children.Add(submenu);
            card.Child = stack;

            Canvas.SetLeft(card, 300);
            Canvas.SetTop(card, 110);
            layer.Children.Add(card);
        }

        private static FrameworkElement MenuPreviewRow(string text, bool highlighted)
        {
            var row = new Border
            {
                Height = 28,
                Padding = new Thickness(6, 0, 6, 0),
                CornerRadius = new CornerRadius(3)
            };
            row.SetResourceReference(Border.BackgroundProperty,
                highlighted ? "HoverBrush" : "SurfaceBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); // Icon 列
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.SetResourceReference(Border.BackgroundProperty, "MutedBrush");
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var label = new TextBlock
            {
                Text = text,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);
            row.Child = grid;
            return row;
        }

        // ---------- A3：最大化按工作区，不盖任务栏 ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int Size;
            public NativeRect Monitor, Work;
            public int Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int WM_GETMINMAXINFO = 0x0024;

        private void InstallWorkAreaHook()
        {
            SourceInitialized += (s, e) =>
            {
                var source = (HwndSource)PresentationSource.FromVisual(this);
                if (source != null) source.AddHook(WorkAreaHook);
            };
        }

        // WindowStyle.None 的窗口由系统按整屏 rcMonitor 算最大化矩形，
        // 任务栏就被盖住了。这里把最大化尺寸和位置改成当前显示器的 rcWork。
        private IntPtr WorkAreaHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
            ref bool handled)
        {
            if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
            // 真全屏按 rcMonitor 自己摆窗口，不走 A3 的 rcWork 最大化矩形；
            // 否则全屏尺寸会被工作区限制，任务栏区域永远铺不满。
            // 退出全屏时会先清 _immersive 再还原 Maximized，钩子必须及时回来。
            if (_immersive) return IntPtr.Zero;
            var info = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(monitor, ref mi)) return IntPtr.Zero;

            // WM_GETMINMAXINFO 用物理像素；app.manifest 声明 PerMonitorV2，
            // GetMonitorInfo 返回的也是物理像素，两边一致，不用再换算 DPI。
            info.MaxPosition.X = mi.Work.Left - mi.Monitor.Left;
            info.MaxPosition.Y = mi.Work.Top - mi.Monitor.Top;
            info.MaxSize.X = mi.Work.Right - mi.Work.Left;
            info.MaxSize.Y = mi.Work.Bottom - mi.Work.Top;
            Marshal.StructureToPtr(info, lParam, true);
            handled = true;
            return IntPtr.Zero;
        }
    }
}
