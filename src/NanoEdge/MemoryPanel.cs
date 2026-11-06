using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NanoEdge
{
    // SPEC 4.9 内存悬浮面板（P0-17）。纯 WPF Popup，零 WebView2 开销；
    // 只在打开时 1s 采样一次，关闭即停。
    public sealed class MemoryPanel
    {
        private const double PanelWidth = 332;

        private readonly Popup _popup;
        private readonly Border _root;
        private readonly StackPanel _tabRows;
        private readonly TextBlock _totalText, _selfText, _commonText, _estimateText;
        private readonly Button _releaseButton;
        private readonly DispatcherTimer _refreshTimer;
        private readonly MemoryGovernor _governor;
        private readonly Func<List<TabModel>> _tabs;
        private readonly Func<TabModel> _active;
        private readonly Func<TabModel, string> _exemption;
        private readonly Action<TabModel> _selectTab;
        private readonly Action<TabModel> _toggleResident;
        private readonly Action<string> _notify;
        private bool _sampling;

        public bool IsPinned;
        public event Action CursorEntered;
        public event Action CursorLeft;

        public MemoryPanel(FrameworkElement anchor, Panel host, MemoryGovernor governor,
            Func<List<TabModel>> tabs, Func<TabModel> active, Func<TabModel, string> exemption,
            Action<TabModel> selectTab, Action<TabModel> toggleResident, Action<string> notify)
        {
            _governor = governor;
            _tabs = tabs;
            _active = active;
            _exemption = exemption;
            _selectTab = selectTab;
            _toggleResident = toggleResident;
            _notify = notify;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // note
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // common
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // tabs
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // actions
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // footnote

            var header = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _totalText = Text(L.T("进程组 — MB"), 13, "TextBrush", true);
            header.Children.Add(_totalText);

            _selfText = Text(L.T("外壳 — MB"), 11, "MutedBrush", false);
            _selfText.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_selfText, 1);
            header.Children.Add(_selfText);
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            var note = Text(L.T("工作集下降 = 物理页被换出，提交内存不变"), 10, "MutedBrush", false);
            note.Margin = new Thickness(0, 3, 0, 10);
            Grid.SetRow(note, 1);
            grid.Children.Add(note);

            var common = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            common.Children.Add(SectionLabel(L.T("公共开销")));
            _commonText = Text("—", 11, "MutedBrush", false);
            _commonText.Margin = new Thickness(0, 3, 0, 0);
            common.Children.Add(_commonText);
            Grid.SetRow(common, 2);
            grid.Children.Add(common);

            var tabsSection = new StackPanel();
            tabsSection.Children.Add(SectionLabel(L.T("标签页")));
            _tabRows = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            var scroll = new ScrollViewer
            {
                Content = _tabRows,
                MaxHeight = 268,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            tabsSection.Children.Add(scroll);
            Grid.SetRow(tabsSection, 3);
            grid.Children.Add(tabsSection);

            var actions = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _releaseButton = MakeAction(L.T("释放后台"));
            _releaseButton.Click += async (s, e) =>
            {
                Disable(L.T("释放中…"));
                var result = await _governor.ReleaseBackgroundAsync();
                if (_notify != null)
                    _notify(result.Count == 0
                        ? L.T("没有可释放的后台标签")
                        : L.T("已释放 ") + result.Count + L.T(" 个后台标签 · 工作集 -") +
                          result.ActualDeltaMB.ToString("0") + L.T("MB / 提交 -") +
                          result.ActualPrivateDeltaMB.ToString("0") + "MB");
                await RefreshAsync();
            };
            actions.Children.Add(_releaseButton);
            Grid.SetRow(actions, 4);
            grid.Children.Add(actions);

            _estimateText = Text(L.T("只释放后台标签，点击标签即可恢复页面"), 9, "MutedBrush", false);
            _estimateText.Margin = new Thickness(0, 8, 0, 0);
            _estimateText.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(_estimateText, 5);
            grid.Children.Add(_estimateText);

            _root = new Border
            {
                Width = PanelWidth,
                Padding = new Thickness(14, 12, 14, 12),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Child = grid
            };
            _root.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            _root.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            _root.MouseEnter += (s, e) => { if (CursorEntered != null) CursorEntered(); };
            _root.MouseLeave += (s, e) => { if (CursorLeft != null) CursorLeft(); };

            _popup = new Popup
            {
                Placement = PlacementMode.Relative,
                PlacementTarget = anchor,
                // 点面板以外任何地方就收起；以前 StaysOpen=true 会一直挂着。
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Focusable = false,
                Child = _root
            };
            // 挂进可视树，Popup 才会以主窗口为 Owner：切到别的软件或最小化
            // 时会跟着隐藏，而不是独立置顶在屏幕上（B3）。
            if (host != null) host.Children.Add(_popup);
            // 自动关闭也要停采样、清掉钉住状态，否则下次打开状态残留。
            _popup.Closed += (s, e) =>
            {
                if (_refreshTimer != null) _refreshTimer.Stop();
                IsPinned = false;
            };

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _refreshTimer.Tick += async (s, e) => await RefreshAsync();
        }

        public bool IsOpen { get { return _popup.IsOpen; } }
        public FrameworkElement Visual { get { return _root; } }
        // 自测用：确认 Popup 挂进了可视树（才有 Owner）且未钉死。
        public Popup HostPopup { get { return _popup; } }

        public void SetAnchor(FrameworkElement anchor)
        {
            if (anchor != null) _popup.PlacementTarget = anchor;
        }

        public void Open()
        {
            Reposition();
            _popup.IsOpen = true;
            _refreshTimer.Start();
            var unused = RefreshAsync();
        }

        // 窗口移动或改尺寸后锚点位置会变，重新算一次偏移。
        public void Reposition()
        {
            var anchor = _popup.PlacementTarget as FrameworkElement;
            if (anchor == null) return;
            _popup.HorizontalOffset = anchor.ActualWidth - PanelWidth;
            _popup.VerticalOffset = anchor.ActualHeight + 8;
        }

        public void Close()
        {
            _refreshTimer.Stop();
            _popup.IsOpen = false;
            IsPinned = false;
        }

        public async Task RefreshAsync()
        {
            if (_sampling) return;
            _sampling = true;
            try
            {
                var snapshot = await _governor.SampleAsync();
                if (_popup.IsOpen && snapshot != null) Refresh(snapshot);
            }
            finally { _sampling = false; }
        }

        public void Refresh(MemorySnapshot snapshot)
        {
            if (snapshot == null) return;

            _totalText.Text = L.T("进程组 ") + snapshot.GroupWorkingSetMB.ToString("0") + " MB";
            _selfText.Text = L.T("外壳 ") + snapshot.HostWorkingSetMB.ToString("0") + " MB";

            var parts = new List<string>();
            AddKind(parts, snapshot, "Browser", "Browser");
            AddKind(parts, snapshot, "Gpu", "GPU");
            AddKind(parts, snapshot, "Utility", "Utility");
            AddKind(parts, snapshot, "SandboxHelper", "Sandbox");
            _commonText.Text = parts.Count == 0 ? "—" : string.Join(" · ", parts);

            var active = _active();
            _tabRows.Children.Clear();
            var tabs = _tabs();
            foreach (var tab in tabs)
                _tabRows.Children.Add(MakeRow(tab, snapshot));

            var releasable = tabs.Count(t =>
                !ReferenceEquals(t, active) && t.MemoryState != TabMemoryState.Discarded &&
                snapshot.TabWorkingSetMB(t.Id) > 0);
            var candidates = tabs
                .Where(t => !ReferenceEquals(t, active) && t.MemoryState != TabMemoryState.Discarded &&
                            _exemption(t) == null)
                .ToList();
            var estimated = candidates.Sum(t => snapshot.TabWorkingSetMB(t.Id));
            var estimatedPrivate = candidates.Sum(t => snapshot.TabPrivateMB(t.Id));

            _releaseButton.Content = estimated >= 1
                ? L.T("释放后台 · 约 ") + estimated.ToString("0") + "MB"
                : L.T("释放后台");
            _releaseButton.IsEnabled = true;
            _estimateText.Text = estimated >= 1
                ? L.T("只释放后台标签（") + releasable + L.T(" 个），点击标签即可恢复页面")
                : estimatedPrivate >= 1
                    ? L.T("后台工作集已接近 0，释放仍可回收约 ") + estimatedPrivate.ToString("0") + L.T("MB 提交内存")
                    : L.T("后台标签已全部释放，没有可回收的 renderer");
        }

        private static void AddKind(List<string> parts, MemorySnapshot snapshot, string kind, string label)
        {
            var mb = snapshot.KindWorkingSetMB(kind);
            if (mb >= 1) parts.Add(label + " " + mb.ToString("0") + "MB");
        }

        private FrameworkElement MakeRow(TabModel tab, MemorySnapshot snapshot)
        {
            var active = ReferenceEquals(tab, _active());
            var hasView = tab.MemoryState != TabMemoryState.Discarded;
            var ws = hasView ? snapshot.TabWorkingSetMB(tab.Id) : 0;
            var priv = hasView ? snapshot.TabPrivateMB(tab.Id) : 0;
            var measured = hasView && (ws > 0 || priv > 0);

            var row = new Border
            {
                Height = 38,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Colors.Transparent)
            };
            row.SetResourceReference(Border.BackgroundProperty, active ? "HoverBrush" : "SurfaceBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });

            var dot = new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center
            };
            dot.SetResourceReference(Border.BackgroundProperty, DotKey(tab.MemoryState));
            grid.Children.Add(dot);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) };
            var title = Text(tab.DisplayTitle, 11.5, "TextBrush", false);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            if (tab.IsSecret) title.ToolTip = null;
            text.Children.Add(title);
            var sub = Text(tab.IsStartPage ? L.T("起始页") : (string.IsNullOrEmpty(tab.Host) ? L.T("未加载") : tab.Host),
                9, "MutedBrush", false);
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(sub);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var state = Text(_governor.StateName(tab.MemoryState), 10, "MutedBrush", false);
            state.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(state, 2);
            grid.Children.Add(state);

            var numbers = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            var wsText = Text(measured ? ws.ToString("0") + " MB" : "—", 10.5, "TextBrush", false);
            wsText.HorizontalAlignment = HorizontalAlignment.Right;
            numbers.Children.Add(wsText);
            var privText = Text(measured && priv > 0 ? L.T("提交 ") + priv.ToString("0") : "", 8.5, "MutedBrush", false);
            privText.HorizontalAlignment = HorizontalAlignment.Right;
            numbers.Children.Add(privText);
            Grid.SetColumn(numbers, 3);
            grid.Children.Add(numbers);

            var reason = _exemption(tab);
            var residentOn = tab.Resident;
            var residentForced = reason != null && !tab.Resident;
            var resident = new Border
            {
                Height = 17,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Opacity = residentOn ? 1.0 : 0.45,
                Background = new SolidColorBrush(Colors.Transparent),
                Child = new TextBlock
                {
                    Text = L.T("内存常驻"),
                    FontSize = 9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            if (residentOn)
            {
                resident.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
                resident.Opacity = 1.0;
            }
            ((TextBlock)resident.Child).SetResourceReference(TextBlock.ForegroundProperty,
                residentOn ? "AccentBrush" : "MutedBrush");
            resident.ToolTip = residentForced ? L.T("自动豁免：") + reason : L.T("内存常驻：不自动释放这个站点的标签");
            if (tab.MemoryState == TabMemoryState.Discarded) resident.Visibility = Visibility.Collapsed;
            else if (!residentOn) resident.Visibility = Visibility.Collapsed;
            resident.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (residentForced) return;
                if (_toggleResident != null) _toggleResident(tab);
            };
            Grid.SetColumn(resident, 4);
            grid.Children.Add(resident);

            row.Child = grid;
            row.MouseEnter += (s, e) =>
            {
                if (residentForced || !residentOn) resident.Visibility = Visibility.Visible;
            };
            row.MouseLeave += (s, e) =>
            {
                if (!residentOn) resident.Visibility = Visibility.Collapsed;
            };
            row.MouseLeftButtonUp += (s, e) => { if (_selectTab != null) _selectTab(tab); };
            row.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                // A5：第三处菜单也走 Theme 工厂，否则内存面板右键在暗色下仍露白。
                var menu = Theme.StyleMenu(new ContextMenu());

                var focus = Theme.StyleMenuItem(new MenuItem { Header = L.T("切换到此标签"), FontSize = 12 });
                focus.Click += (s2, e2) => { if (_selectTab != null) _selectTab(tab); };
                menu.Items.Add(focus);

                var release = Theme.StyleMenuItem(
                    new MenuItem { Header = L.T("立即释放此标签"), FontSize = 12, IsEnabled = hasView });
                release.Click += async (s2, e2) =>
                {
                    if (await _governor.DiscardTabAsync(tab))
                    {
                        if (_notify != null) _notify(L.T("已释放「") + tab.DisplayTitle + "」");
                    }
                    await RefreshAsync();
                };
                menu.Items.Add(release);
                menu.PlacementTarget = row;
                menu.IsOpen = true;
            };
            return row;
        }

        private static string DotKey(TabMemoryState state)
        {
            switch (state)
            {
                case TabMemoryState.Active: return "StateActiveBrush";
                case TabMemoryState.Low: return "StateLowBrush";
                default: return "StateDiscardedBrush";
            }
        }

        private TextBlock SectionLabel(string text)
        {
            return Text(text, 9.5, "MutedBrush", false);
        }

        private static TextBlock Text(string value, double size, string brushKey, bool bold)
        {
            var block = new TextBlock
            {
                Text = value,
                FontSize = size,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return block;
        }

        private static Button MakeAction(string text)
        {
            var button = new Button
            {
                Content = text,
                Height = 28,
                FontSize = 11,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            button.SetResourceReference(Control.BackgroundProperty, "HoverBrush");
            button.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            return button;
        }

        private void Disable(string text)
        {
            _releaseButton.Content = text;
            _releaseButton.IsEnabled = false;
        }
    }
}
