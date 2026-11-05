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
                case Settings: return new SettingsPage(settings);
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

        public BookmarksPage(Action<string, bool> open, Action<string> notify)
            : base(L.T("书签"), L.T("搜索书签（标题或网址）"))
        {
            _open = open;
            _notify = notify;
            AddAction(L.T("导出 HTML"), Export);
            AddAction(L.T("导入 HTML"), ImportHtml);
            AddAction(L.T("从 Chrome/Edge 导入"), ImportChromium);
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

        private void ImportChromium()
        {
            var result = BookmarkStore.ImportChromiumProfiles();
            Refresh();
            if (_notify == null) return;
            if (result.Files == 0)
                _notify(L.T("没找到 Chrome / Edge 的书签文件"));
            else
                _notify(string.Format(L.T("从 {0} 个配置文件导入 {1} 条书签"), result.Files, result.Added));
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
