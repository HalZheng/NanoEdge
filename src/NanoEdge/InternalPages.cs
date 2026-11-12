using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace NanoEdge
{
    // 内部页：纯 WPF，不创建 WebView2（SPEC 7.8 的延伸）。
    // 打开 nano://history 或 nano://bookmarks 时不花任何 renderer 内存。
    public static class InternalPages
    {
        public const string History = "nano://history";
        public const string Bookmarks = "nano://bookmarks";
        public const string Downloads = "nano://downloads";
        public const string Scripts = "nano://scripts";
        public const string Settings = "nano://settings";
        public const string Start = "nano://start";

        public static bool IsInternal(string url)
        {
            return !string.IsNullOrEmpty(url) &&
                   url.StartsWith("nano://", StringComparison.OrdinalIgnoreCase);
        }

        public static string Normalize(string url)
        {
            if (string.IsNullOrEmpty(url)) return Start;
            var path = url.Substring("nano://".Length).Trim('/').ToLowerInvariant();
            switch (path)
            {
                case "history": return History;
                case "bookmarks": return Bookmarks;
                case "downloads": return Downloads;
                case "scripts": return Scripts;
                case "settings": return Settings;
                default: return Start;
            }
        }

        public static string TitleOf(string url)
        {
            switch (Normalize(url))
            {
                case History: return L.T("历史记录");
                case Bookmarks: return L.T("书签");
                case Downloads: return L.T("下载");
                case Scripts: return L.T("用户脚本");
                case Settings: return L.T("设置");
                default: return L.T("新标签页");
            }
        }

        // currentSite 只在「用户脚本」页用到：返回打开本页时所在网页的站点键（host[:port]）。
        // 保持可选参数，现有 4 参调用（历史/书签/下载、自测）不需要改。
        public static FrameworkElement Create(string url, AppSettings settings,
            Action<string, bool> open, Action<string> notify, Func<string> currentSite = null)
        {
            switch (Normalize(url))
            {
                case History: return new HistoryPage(open, notify);
                case Bookmarks: return new BookmarksPage(open, notify);
                case Downloads: return new DownloadsPage(notify);
                case Scripts: return new ScriptsPage(notify, currentSite);
                case Settings: return new SettingsPage(settings, notify);
                default: return new StartPage(settings);
            }
        }
    }

    // 所有内部页共用的骨架：标题 + 计数 + 搜索框 + 右侧动作 + 可滚动列表。
    public abstract class InternalPage : UserControl
    {
        private readonly TextBox _search;
        private readonly TextBlock _count;
        private readonly StackPanel _actions;
        private readonly Grid _root;
        protected readonly StackPanel Body;

        protected InternalPage(string title, string searchHint)
        {
            this.SetResourceReference(BackgroundProperty, "CanvasBrush");

            // Grid 用 Center 时宽度由内容决定，会缩成一条窄栏；显式算宽度：min(980, 可用宽-64)。
            _root = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 28, 0, 0)
            };
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleBlock = new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold };
            titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            header.Children.Add(titleBlock);

            _count = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 3)
            };
            _count.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetColumn(_count, 1);
            header.Children.Add(_count);
            Grid.SetRow(header, 0);
            _root.Children.Add(header);

            var bar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var searchBorder = new Border
            {
                Height = 34,
                MaxWidth = 460,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1)
            };
            searchBorder.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            searchBorder.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");

            var searchGrid = new Grid();
            _search = new TextBox
            {
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Margin = new Thickness(11, 0, 11, 0)
            };
            _search.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
            _search.SetResourceReference(TextBox.CaretBrushProperty, "AccentBrush");
            var placeholder = new TextBlock
            {
                Text = searchHint,
                FontSize = 12,
                Margin = new Thickness(12, 0, 11, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            placeholder.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _search.TextChanged += (s, e) =>
            {
                placeholder.Visibility = string.IsNullOrEmpty(_search.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
                Refresh();
            };
            searchGrid.Children.Add(_search);
            searchGrid.Children.Add(placeholder);
            searchBorder.Child = searchGrid;
            bar.Children.Add(searchBorder);

            _actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_actions, 1);
            bar.Children.Add(_actions);
            Grid.SetRow(bar, 1);
            _root.Children.Add(bar);

            Body = new StackPanel { Margin = new Thickness(0, 4, 0, 24) };
            var scroll = new ScrollViewer
            {
                Content = Body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 2);
            _root.Children.Add(scroll);

            Content = _root;
            SizeChanged += (s, e) => UpdateWidth();
            UpdateWidth();
        }

        private void UpdateWidth()
        {
            var available = ActualWidth > 0 ? ActualWidth : 980;
            _root.Width = Math.Min(980, Math.Max(360, available - 64));
        }

        protected string Query { get { return (_search.Text ?? "").Trim(); } }

        protected Button AddAction(string text, Action action)
        {
            var button = new Button
            {
                Content = text,
                Height = 30,
                FontSize = 11,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(8, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            button.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
            button.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            button.Click += (s, e) => action();
            _actions.Children.Add(button);
            return button;
        }

        protected abstract void FillBody();
        protected abstract string CountLabel();

        public void Refresh()
        {
            Body.Children.Clear();
            FillBody();
            _count.Text = CountLabel();
        }

        protected FrameworkElement SectionHeader(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                Margin = new Thickness(2, 16, 0, 4)
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return block;
        }

        protected FrameworkElement EmptyHint(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Margin = new Thickness(2, 30, 0, 0)
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return block;
        }

        protected FrameworkElement MakeRow(string title, string subtitle, string meta,
            Action<bool> open, Action remove)
        {
            var row = new Border
            {
                Height = 46,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Colors.Transparent)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 10, 0) };
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            text.Children.Add(titleBlock);
            var sub = new TextBlock
            {
                Text = subtitle,
                FontSize = 9.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            text.Children.Add(sub);
            grid.Children.Add(text);

            var metaBlock = new TextBlock
            {
                Text = meta,
                FontSize = 9.5,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 6, 0)
            };
            metaBlock.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetColumn(metaBlock, 1);
            grid.Children.Add(metaBlock);

            var removeButton = new Button
            {
                Content = "✕",
                Width = 22,
                Height = 22,
                FontSize = 10,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed,
                ToolTip = L.T("删除")
            };
            removeButton.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
            removeButton.Click += (s, e) =>
            {
                e.Handled = true;
                remove();
            };
            Grid.SetColumn(removeButton, 2);
            grid.Children.Add(removeButton);

            row.Child = grid;
            row.MouseEnter += (s, e) =>
            {
                row.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
                removeButton.Visibility = Visibility.Visible;
            };
            row.MouseLeave += (s, e) =>
            {
                row.Background = new SolidColorBrush(Colors.Transparent);
                removeButton.Visibility = Visibility.Collapsed;
            };
            row.MouseLeftButtonUp += (s, e) =>
            {
                var newTab = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                open(newTab);
            };
            return row;
        }

        protected static bool Matches(string query, string title, string url)
        {
            if (query.Length == 0) return true;
            return (title ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                   (url ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }
    }

    public sealed class HistoryPage : InternalPage
    {
        private readonly Action<string, bool> _open;
        private readonly Action<string> _notify;
        private readonly Button _clearButton;
        private bool _confirmingClear;

        public HistoryPage(Action<string, bool> open, Action<string> notify)
            : base(L.T("历史记录"), L.T("搜索历史（标题或网址）"))
        {
            _open = open;
            _notify = notify;
            _clearButton = AddAction(L.T("清空历史"), OnClear);
            Refresh();
        }

        protected override string CountLabel()
        {
            return HistoryStore.All().Count.ToString("N0") + L.T(" 条 · 保留 90 天");
        }

        protected override void FillBody()
        {
            var entries = HistoryStore.All()
                .OrderByDescending(e => e.LastVisitUtc)
                .Where(e => Matches(Query, e.Title, e.Url))
                .ToList();

            if (entries.Count == 0)
            {
                Body.Children.Add(EmptyHint(Query.Length == 0 ? L.T("还没有历史记录") : L.T("没有匹配的记录")));
                return;
            }

            foreach (var day in entries.GroupBy(e => DayLabel(e.LastVisitUtc)))
            {
                Body.Children.Add(SectionHeader(day.Key));
                foreach (var entry in day)
                {
                    var item = entry;
                    Body.Children.Add(MakeRow(
                        string.IsNullOrEmpty(item.Title) ? item.Url : item.Title,
                        item.Url,
                        item.LastVisitUtc.ToLocalTime().ToString("HH:mm") +
                        (item.Visits > 1 ? " · " + item.Visits + L.T(" 次") : ""),
                        newTab => { if (newTab) _open(item.Url, true); else _open(item.Url, false); },
                        () =>
                        {
                            HistoryStore.Remove(item.Url);
                            Refresh();
                        }));
                }
            }
        }

        private void OnClear()
        {
            if (!_confirmingClear)
            {
                _confirmingClear = true;
                _clearButton.Content = L.T("再点一次清空");
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    _confirmingClear = false;
                    _clearButton.Content = L.T("清空历史");
                };
                timer.Start();
                return;
            }

            _confirmingClear = false;
            _clearButton.Content = L.T("清空历史");
            HistoryStore.Clear();
            Refresh();
            if (_notify != null) _notify(L.T("历史记录已清空"));
        }

        private static string DayLabel(DateTime utc)
        {
            var local = utc.ToLocalTime().Date;
            var today = DateTime.Today;
            if (local == today) return L.T("今天");
            if (local == today.AddDays(-1)) return L.T("昨天");
            return local.Year == today.Year
                ? local.ToString(L.T("M月d日"))
                : local.ToString(L.T("yyyy年M月d日"));
        }
    }

    public sealed class BookmarksPage : InternalPage
    {
        private readonly Action<string, bool> _open;
        private readonly Action<string> _notify;

        // C6：页内向导浮层。overlay 只建一次、切步骤只换卡片内容；
        // Refresh() 只清 Body.Children，不会把它清掉。
        private Border _overlay;
        private Border _overlayCard;
        private ScrollViewer _wizardScroll;

        private List<ChromiumProfile> _profiles;
        private int _selected;
        private int _step;                 // 0=关闭 1=选来源 2=预览 3=结果
        private bool _skipExisting = true;
        private List<BookmarkCandidate> _candidates;
        private ImportOutcome _preview;
        private ImportOutcome _result;
        private string _resultError = "";
        private Button _importButton;
        private TextBlock[] _numberBlocks;

        // 自测读数：不模拟鼠标，直接驱动真实的步骤方法。
        internal int WizardStepForTest { get { return _step; } }
        internal int SourceCountForTest { get { return _profiles == null ? 0 : _profiles.Count; } }
        internal int SelectedIndexForTest { get { return _selected; } }
        internal bool WizardVisibleForTest
        {
            get { return _overlay != null && _overlay.Visibility == Visibility.Visible; }
        }
        internal bool SkipExistingForTest { get { return _skipExisting; } }
        internal int PreviewSampleCountForTest { get; private set; }
        internal ImportOutcome PreviewForTest { get { return _preview; } }
        internal ImportOutcome ResultForTest { get { return _result; } }
        internal string ResultErrorForTest { get { return _resultError; } }
        internal FrameworkElement WizardCardForTest { get { return _overlayCard; } }
        internal ChromiumProfile ProfileForTest(int index)
        {
            return _profiles != null && index >= 0 && index < _profiles.Count
                ? _profiles[index] : null;
        }
        internal string SourceLabelForTest(int index)
        {
            var profile = ProfileForTest(index);
            return profile == null ? "" : SourceLabel(profile);
        }

        private static string SourceLabel(ChromiumProfile profile)
        {
            return profile.Label + " · " + (profile.ReadFailed
                ? L.T("读取失败")
                : string.Format(L.T("{0} 条"), profile.Count));
        }

        public BookmarksPage(Action<string, bool> open, Action<string> notify)
            : base(L.T("书签"), L.T("搜索书签（标题或网址）"))
        {
            _open = open;
            _notify = notify;
            AddAction(L.T("导出 HTML"), Export);
            AddAction(L.T("导入 HTML"), ImportHtml);
            AddAction(L.T("从 Chrome/Edge 导入"), OpenWizard);
            Refresh();
        }

        protected override string CountLabel()
        {
            return BookmarkStore.All().Count.ToString("N0") + L.T(" 个");
        }

        protected override void FillBody()
        {
            var items = BookmarkStore.All()
                .Where(b => Matches(Query, b.Title, b.Url))
                .ToList();

            if (items.Count == 0)
            {
                Body.Children.Add(EmptyHint(Query.Length == 0
                    ? L.T("还没有书签 —— 在网页上按 Ctrl+D 收藏当前页面")
                    : L.T("没有匹配的书签")));
                return;
            }

            foreach (var folder in items.GroupBy(b => string.IsNullOrEmpty(b.Folder) ? L.T("未分组") : b.Folder)
                         .OrderBy(g => g.Key == L.T("未分组") ? 1 : 0).ThenBy(g => g.Key))
            {
                Body.Children.Add(SectionHeader(folder.Key));
                foreach (var bookmark in folder)
                {
                    var item = bookmark;
                    Body.Children.Add(MakeRow(
                        string.IsNullOrEmpty(item.Title) ? Urls.Host(item.Url) : item.Title,
                        item.Url,
                        item.AddedUtc.ToLocalTime().ToString(L.T("M月d日")),
                        newTab => { if (newTab) _open(item.Url, true); else _open(item.Url, false); },
                        () =>
                        {
                            BookmarkStore.Remove(item.Url);
                            Refresh();
                        }));
                }
            }
        }

        // ---------- C6：三步页内导入向导 ----------

        private void OpenWizard()
        {
            EnsureOverlay();
            _profiles = BookmarkStore.DiscoverChromiumProfiles();
            _selected = 0;
            _skipExisting = true;
            _candidates = null;
            _preview = null;
            _result = null;
            _resultError = "";
            _step = 1;
            ShowWizardCard(BuildSourcesCard());
        }

        private ChromiumProfile SelectedProfile
        {
            get
            {
                return _profiles != null && _selected >= 0 && _selected < _profiles.Count
                    ? _profiles[_selected] : null;
            }
        }

        // 把 Content 外面包一层 Grid，卡片放在同层 Border 里（不是 Popup / 新 Window），
        // 因此卡片在可视树内，RenderTargetBitmap 能截图。
        private void EnsureOverlay()
        {
            if (_overlay != null) return;
            var inner = Content as UIElement;
            Content = null;

            var wrapper = new Grid();
            if (inner != null) wrapper.Children.Add(inner);

            _overlay = new Border { Visibility = Visibility.Collapsed };
            _overlay.SetResourceReference(Border.BackgroundProperty, "CanvasBrush");

            _overlayCard = new Border
            {
                Margin = new Thickness(24),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(18),
                VerticalAlignment = VerticalAlignment.Top
            };
            _overlayCard.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            _overlayCard.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

            _wizardScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 540
            };
            _overlayCard.Child = _wizardScroll;
            _overlay.Child = _overlayCard;

            wrapper.Children.Add(_overlay);
            Content = wrapper;
        }

        private void ShowWizardCard(FrameworkElement content)
        {
            _wizardScroll.Content = content;
            _overlay.Visibility = Visibility.Visible;
        }

        private void CloseWizard()
        {
            _step = 0;
            _profiles = null;
            _candidates = null;
            _preview = null;
            _result = null;
            _resultError = "";
            _importButton = null;
            _numberBlocks = null;
            if (_wizardScroll != null) _wizardScroll.Content = null;
            if (_overlay != null) _overlay.Visibility = Visibility.Collapsed;
        }

        // 第 1 步：选来源。一行一个 RadioButton，默认选中第一个。
        private FrameworkElement BuildSourcesCard()
        {
            var panel = new StackPanel();
            panel.Children.Add(WizTitle(L.T("从 Chrome/Edge 导入书签")));
            panel.Children.Add(WizSub(L.T("第 1 步：选择来源")));

            if (_profiles.Count == 0)
            {
                panel.Children.Add(WizHint(L.T("没找到 Chrome / Edge 的书签文件")));
                panel.Children.Add(WizHint(L.T("可以改用「导入 HTML」。")));
                panel.Children.Add(WizButtons(WizButton(L.T("取消"), CancelWizard, false)));
                return panel;
            }

            panel.Children.Add(WizHint(L.T("只会导入选中的这一个来源；确认之前不会写入书签。")));
            var list = new StackPanel { Margin = new Thickness(0, 6, 0, 10) };
            for (var i = 0; i < _profiles.Count; i++)
            {
                var index = i;
                var radio = new RadioButton
                {
                    GroupName = "bm-wizard-source",
                    IsChecked = i == 0,
                    Margin = new Thickness(0, 0, 0, 6),
                    Cursor = Cursors.Hand,
                    Content = SourceRow(_profiles[i])
                };
                radio.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                radio.Checked += (s, e) => { _selected = index; };
                list.Children.Add(radio);
            }
            panel.Children.Add(new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 320
            });
            panel.Children.Add(WizButtons(
                WizButton(L.T("取消"), CancelWizard, false),
                WizButton(L.T("下一步"), GoPreview, true)));
            return panel;
        }

        private FrameworkElement SourceRow(ChromiumProfile profile)
        {
            var stack = new StackPanel { Margin = new Thickness(8, 2, 0, 2) };
            var main = new TextBlock
            {
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = SourceLabel(profile)
            };
            main.SetResourceReference(TextBlock.ForegroundProperty,
                profile.ReadFailed ? "WarnBrush" : "TextBrush");
            stack.Children.Add(main);

            var sub = new TextBlock
            {
                FontSize = 9.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = (profile.ReadFailed ? WizardErrorText(profile) + " · " : "") + profile.FilePath,
                ToolTip = profile.FilePath
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            stack.Children.Add(sub);
            return stack;
        }

        private void GoSources()
        {
            _step = 1;
            ShowWizardCard(BuildSourcesCard());
        }

        private void GoPreview()
        {
            var profile = SelectedProfile;
            if (profile == null) return;
            // 与发现阶段同一份候选列表；预览和落库都用它，不再各解析一遍。
            _candidates = profile.Candidates;
            _step = 2;
            ShowWizardCard(BuildPreviewCard());
        }

        // 第 2 步：预览。四个数字 + 前 5 条示例 + 「跳过已存在的书签」开关。
        private FrameworkElement BuildPreviewCard()
        {
            var profile = SelectedProfile;
            var panel = new StackPanel();
            panel.Children.Add(WizTitle(L.T("从 Chrome/Edge 导入书签")));
            panel.Children.Add(WizSub(L.T("第 2 步：预览")));
            panel.Children.Add(WizLine(L.T("来源：") + (profile == null ? "" : profile.Label)));

            if (profile != null && profile.ReadFailed)
            {
                var error = new TextBlock
                {
                    Text = L.T("读取失败") + " · " + WizardErrorText(profile),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 12)
                };
                error.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
                panel.Children.Add(error);
            }

            _preview = BookmarkStore.PreviewImport(_candidates, _skipExisting);
            TextBlock[] blocks;
            panel.Children.Add(BuildNumberRow(
                new[] { L.T("总数"), L.T("将新增"), L.T("将跳过"), L.T("无效") },
                new[] { _preview.Total, _preview.Added, _preview.Skipped, _preview.Invalid },
                out blocks));
            _numberBlocks = blocks;

            var samples = (_candidates ?? new List<BookmarkCandidate>())
                .Where(c => c.IsValid).Take(5).ToList();
            PreviewSampleCountForTest = samples.Count;
            if (samples.Count > 0)
            {
                var sampleTitle = new TextBlock
                {
                    Text = string.Format(L.T("示例（前 {0} 条）"), samples.Count),
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 12, 0, 4)
                };
                sampleTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                panel.Children.Add(sampleTitle);
                foreach (var candidate in samples) panel.Children.Add(SampleRow(candidate));
            }

            panel.Children.Add(BuildSkipToggle());
            panel.Children.Add(WizHint(L.T("文件夹分组会保留。")));

            var actionText = (profile != null && profile.ReadFailed)
                ? L.T("查看结果")
                : string.Format(L.T("导入 {0} 条"), _preview.Added);
            _importButton = WizButton(actionText, CommitImport, true);
            panel.Children.Add(WizButtons(
                WizButton(L.T("上一步"), GoSources, false),
                WizButton(L.T("取消"), CancelWizard, false),
                _importButton));
            return panel;
        }

        private FrameworkElement SampleRow(BookmarkCandidate candidate)
        {
            var title = string.IsNullOrWhiteSpace(candidate.Title)
                ? L.T("（无标题）") : candidate.Title.Trim();
            var folder = string.IsNullOrWhiteSpace(candidate.Folder)
                ? L.T("未分组") : candidate.Folder;
            var block = new TextBlock
            {
                Text = title + " · " + Urls.Host(candidate.Url) + " · " + folder,
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 2),
                ToolTip = candidate.Url
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return block;
        }

        // 主题化的开关（不用系统 CheckBox：暗色下会露白），照 ScriptsPage.MakeToggle 的做法。
        private FrameworkElement BuildSkipToggle()
        {
            var grid = new Grid { Margin = new Thickness(0, 12, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = L.T("跳过已存在的书签"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            grid.Children.Add(label);

            var host = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var state = new TextBlock
            {
                Text = _skipExisting ? L.T("开") : L.T("关"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            state.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            host.Children.Add(state);

            var track = new Border
            {
                Width = 26,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            track.SetResourceReference(Border.BackgroundProperty,
                _skipExisting ? "AccentBrush" : "StateDiscardedBrush");
            var knob = new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = _skipExisting
                    ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(2, 0, 2, 0)
            };
            track.Child = knob;
            track.MouseLeftButtonUp += (s, e) =>
            {
                _skipExisting = !_skipExisting;
                state.Text = _skipExisting ? L.T("开") : L.T("关");
                track.SetResourceReference(Border.BackgroundProperty,
                    _skipExisting ? "AccentBrush" : "StateDiscardedBrush");
                knob.HorizontalAlignment = _skipExisting
                    ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                UpdatePreviewNumbers();
            };
            host.Children.Add(track);
            Grid.SetColumn(host, 1);
            grid.Children.Add(host);
            return grid;
        }

        // 开关变化时只重算同一份候选，四个数字与按钮文案跟着变。
        private void UpdatePreviewNumbers()
        {
            _preview = BookmarkStore.PreviewImport(_candidates, _skipExisting);
            if (_numberBlocks == null || _numberBlocks.Length < 4) return;
            var values = new[] { _preview.Total, _preview.Added, _preview.Skipped, _preview.Invalid };
            for (var i = 0; i < 4; i++) _numberBlocks[i].Text = values[i].ToString("N0");
            if (_importButton != null && (SelectedProfile == null || !SelectedProfile.ReadFailed))
                _importButton.Content = string.Format(L.T("导入 {0} 条"), _preview.Added);
        }

        // 全批唯一落库点：只有点「导入 N 条」/「查看结果」才会走到这里。
        private void CommitImport()
        {
            var profile = SelectedProfile;
            if (profile != null && profile.ReadFailed)
            {
                _result = new ImportOutcome { Failed = 1 };
                _resultError = WizardErrorText(profile);   // 不写库
            }
            else
            {
                _result = BookmarkStore.ApplyImport(_candidates, _skipExisting);
                _resultError = _result.Error ?? "";
            }
            _step = 3;
            ShowWizardCard(BuildResultCard());
        }

        // 第 3 步：结果。常驻显示，点「完成」才返回书签列表并刷新。
        private FrameworkElement BuildResultCard()
        {
            var panel = new StackPanel();
            panel.Children.Add(WizTitle(L.T("从 Chrome/Edge 导入书签")));
            panel.Children.Add(WizSub(L.T("第 3 步：导入结果")));
            var result = _result ?? new ImportOutcome();
            TextBlock[] unused;
            panel.Children.Add(BuildNumberRow(
                new[] { L.T("新增"), L.T("跳过"), L.T("无效"), L.T("失败") },
                new[] { result.Added, result.Skipped, result.Invalid, result.Failed },
                out unused));

            if (!string.IsNullOrEmpty(_resultError))
            {
                var reason = new TextBlock
                {
                    Text = L.T("失败原因：") + _resultError,
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 6)
                };
                reason.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
                panel.Children.Add(reason);
            }
            panel.Children.Add(WizHint(L.T("结果会一直显示，点「完成」返回书签列表。")));
            panel.Children.Add(WizButtons(WizButton(L.T("完成"), FinishWizard, true)));
            return panel;
        }

        private void FinishWizard()
        {
            CloseWizard();
            Refresh();
        }

        private void CancelWizard()
        {
            CloseWizard();   // 不 Refresh：任何一步取消都不写库、列表没变
        }

        // ---------- 向导通用件（全部走 Theme 资源引用） ----------

        private FrameworkElement BuildNumberRow(string[] labels, int[] values, out TextBlock[] blocks)
        {
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 4) };
            for (var i = 0; i < 4; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(1, GridUnitType.Star) });
            blocks = new TextBlock[4];
            for (var i = 0; i < 4; i++)
            {
                var border = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 8, 8, 8),
                    Margin = new Thickness(0, 0, 8, 0)
                };
                border.SetResourceReference(Border.BackgroundProperty, "InputBrush");
                var stack = new StackPanel();
                var number = new TextBlock
                {
                    Text = values[i].ToString("N0"),
                    FontSize = 19,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                number.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                var label = new TextBlock
                {
                    Text = labels[i],
                    FontSize = 9.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                stack.Children.Add(number);
                stack.Children.Add(label);
                border.Child = stack;
                Grid.SetColumn(border, i);
                grid.Children.Add(border);
                blocks[i] = number;
            }
            return grid;
        }

        private static string WizardErrorText(ChromiumProfile profile)
        {
            var prefix = profile.ErrorKind == ChromiumProfileError.Io
                ? L.T("文件读不到（可能被浏览器占用）")
                : L.T("书签文件 JSON 损坏");
            return string.IsNullOrEmpty(profile.ErrorDetail)
                ? prefix : prefix + " · " + profile.ErrorDetail;
        }

        private static TextBlock WizTitle(string text)
        {
            var block = new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return block;
        }

        private static TextBlock WizSub(string text)
        {
            var block = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(0, 4, 0, 10) };
            block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return block;
        }

        private static TextBlock WizLine(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return block;
        }

        private static TextBlock WizHint(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4)
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return block;
        }

        private static Button WizButton(string text, Action action, bool primary)
        {
            var button = new Button
            {
                Content = text,
                Height = 28,
                MinWidth = 62,
                FontSize = 11,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(8, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            button.SetResourceReference(Control.BackgroundProperty,
                primary ? "AccentSoftBrush" : "HoverBrush");
            button.SetResourceReference(Control.ForegroundProperty,
                primary ? "AccentBrush" : "TextBrush");
            if (action != null) button.Click += (s, e) => action();
            return button;
        }

        private static FrameworkElement WizButtons(params FrameworkElement[] items)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            foreach (var item in items) stack.Children.Add(item);
            return stack;
        }

        // ---------- 自测驱动入口 ----------

        internal void OpenWizardForTest() { OpenWizard(); }
        internal void SelectSourceForTest(int index)
        {
            if (_profiles == null || index < 0 || index >= _profiles.Count) return;
            _selected = index;
        }
        internal void NextForTest() { GoPreview(); }
        internal void BackForTest() { GoSources(); }
        internal void CancelForTest() { CancelWizard(); }
        internal void FinishForTest() { FinishWizard(); }
        internal void CommitForTest() { CommitImport(); }
        internal void SetSkipExistingForTest(bool on)
        {
            _skipExisting = on;
            if (_step == 2) ShowWizardCard(BuildPreviewCard());   // 走真实预览重算
        }

        private void ImportHtml()
        {
            var dialog = new OpenFileDialog
            {
                Filter = L.T("书签 HTML (*.html;*.htm)|*.html;*.htm|所有文件|*.*"),
                Title = L.T("导入书签")
            };
            if (dialog.ShowDialog() != true) return;
            var added = BookmarkStore.ImportNetscapeHtml(dialog.FileName);
            Refresh();
            if (_notify != null) _notify(string.Format(L.T("导入 {0} 条书签"), added));
        }

        private void Export()
        {
            var dialog = new SaveFileDialog
            {
                Filter = L.T("书签 HTML (*.html)|*.html"),
                FileName = "nanoedge-bookmarks.html",
                Title = L.T("导出书签")
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                BookmarkStore.ExportNetscapeHtml(dialog.FileName);
                if (_notify != null) _notify(L.T("已导出到 ") + dialog.FileName);
            }
            catch (Exception ex)
            {
                if (_notify != null) _notify(L.T("导出失败：") + ex.Message);
            }
        }
    }
}
