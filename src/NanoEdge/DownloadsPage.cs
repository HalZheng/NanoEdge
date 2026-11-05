using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NanoEdge
{
    // nano://downloads：纯 WPF 内部页，零 renderer。数据源是 DownloadManager
    // （WebView2 的 DownloadStarting / DownloadOperation 事件写进来的）。
    public sealed class DownloadsPage : InternalPage
    {
        private readonly Action<string> _notify;
        private Button _clearButton;
        private bool _confirming;

        public DownloadsPage(Action<string> notify)
            : base(L.T("下载"), L.T("搜索下载（文件名或网址）"))
        {
            _notify = notify;
            AddAction(L.T("打开下载文件夹"), () => DownloadManager.OpenFolder(null));
            _clearButton = AddAction(L.T("清除已结束"), OnClear);
            Loaded += (s, e) => DownloadManager.Changed += OnDownloadsChanged;
            Unloaded += (s, e) => DownloadManager.Changed -= OnDownloadsChanged;
            Refresh();
        }

        private void OnDownloadsChanged()
        {
            if (!IsLoaded) return;
            Refresh();
        }

        protected override string CountLabel()
        {
            var list = DownloadManager.All();
            var active = list.Count(i => i.State == DownloadStates.InProgress);
            var folder = AppPaths.DownloadsFolder;
            if (folder.Length > 46)
                folder = folder.Substring(0, 18) + "…" + folder.Substring(folder.Length - 26);
            return list.Count.ToString("N0") + L.T(" 项") +
                   (active > 0 ? " · " + active + L.T(" 项下载中") : "") +
                   L.T(" · 保存到 ") + folder;
        }

        protected override void FillBody()
        {
            var items = DownloadManager.All()
                .Where(i => Matches(Query, i.FileName, i.Url))
                .ToList();

            if (items.Count == 0)
            {
                Body.Children.Add(EmptyHint(Query.Length == 0
                    ? L.T("还没有下载记录 —— 下载的文件会保存到系统 Downloads 目录")
                    : L.T("没有匹配的下载")));
                return;
            }

            foreach (var item in items)
            {
                var copy = item;
                Body.Children.Add(MakeRow(copy));
            }
        }

        private FrameworkElement MakeRow(DownloadItem item)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 7, 4, 7),
                Margin = new Thickness(0, 1, 0, 1),
                Background = new SolidColorBrush(Colors.Transparent)
            };

            var stack = new StackPanel();
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 10, 0) };
            var name = new TextBlock
            {
                Text = item.FileName,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            text.Children.Add(name);

            var meta = new TextBlock
            {
                Text = RowMeta(item),
                FontSize = 9.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            meta.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            text.Children.Add(meta);
            top.Children.Add(text);

            var column = 1;
            if (item.State == DownloadStates.Completed)
            {
                top.Children.Add(At(SmallButton(L.T("打开"), () =>
                {
                    if (!DownloadManager.Open(item)) Notify(L.T("文件已不在原位置：") + item.Path);
                }), column++));
            }
            else if (item.State == DownloadStates.InProgress)
            {
                top.Children.Add(At(SmallButton(L.T("取消"), () =>
                {
                    if (!DownloadManager.Cancel(item.Id)) Notify(L.T("该下载已结束，无法取消"));
                }), column++));
            }
            top.Children.Add(At(SmallButton(L.T("文件夹"), () => DownloadManager.OpenFolder(item)), column++));

            var remove = SmallButton("✕", () =>
            {
                DownloadManager.Remove(item.Id);
                Refresh();
            });
            remove.ToolTip = L.T("从列表移除");
            Grid.SetColumn(remove, 4);
            top.Children.Add(remove);

            stack.Children.Add(top);

            if (item.State == DownloadStates.InProgress)
                stack.Children.Add(ProgressBar(item.Percent));

            // 已完成的行点击直接打开文件；其它状态点击只打开所在文件夹。
            row.MouseEnter += (s, e) => row.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            row.MouseLeave += (s, e) => row.Background = new SolidColorBrush(Colors.Transparent);
            row.MouseLeftButtonUp += (s, e) =>
            {
                if (item.State == DownloadStates.Completed)
                {
                    if (!DownloadManager.Open(item)) Notify(L.T("文件已不在原位置：") + item.Path);
                }
                else DownloadManager.OpenFolder(item);
            };
            row.Child = stack;
            return row;
        }

        private static string RowMeta(DownloadItem item)
        {
            var size = DownloadManager.FormatBytes(item.TotalBytes > 0 ? item.TotalBytes : item.ReceivedBytes);
            var time = (item.FinishedUtc > DateTime.MinValue ? item.FinishedUtc : item.StartedUtc)
                .ToLocalTime().ToString(L.T("M月d日 HH:mm"));
            var state = DownloadStates.Label(item.State);
            if (item.State == DownloadStates.InProgress)
            {
                var pct = item.Percent;
                state += pct >= 0 ? " " + pct + "%" : "";
                state += " · " + DownloadManager.FormatBytes(item.ReceivedBytes) +
                         (item.TotalBytes > 0 ? " / " + DownloadManager.FormatBytes(item.TotalBytes) : "");
            }
            else if (item.State == DownloadStates.Completed)
            {
                // 只有完成态才报总大小；取消/中断时 Content-Length 是声明值，不是实际收到的量。
                state += " · " + size;
            }
            if (!string.IsNullOrEmpty(item.Site)) state += " · " + item.Site;
            var reason = DownloadStates.ReasonLabel(item.Reason);
            if (!string.IsNullOrEmpty(reason)) state += " · " + reason;
            return state + " · " + time;
        }

        private static FrameworkElement ProgressBar(int percent)
        {
            var shown = percent >= 0 ? Math.Max(1, Math.Min(99, percent)) : 30;
            var grid = new Grid { Height = 3, Margin = new Thickness(6, 7, 8, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(shown, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - shown, GridUnitType.Star) });
            var fill = new Border { CornerRadius = new CornerRadius(2) };
            fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            grid.Children.Add(fill);
            var rest = new Border { CornerRadius = new CornerRadius(2) };
            rest.SetResourceReference(Border.BackgroundProperty, "LineBrush");
            Grid.SetColumn(rest, 1);
            grid.Children.Add(rest);
            return grid;
        }

        private static Button SmallButton(string text, Action action)
        {
            var button = new Button
            {
                Content = text,
                Height = 22,
                MinWidth = 26,
                FontSize = 10,
                Padding = new Thickness(7, 0, 7, 0),
                Margin = new Thickness(3, 0, 3, 0),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            button.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
            button.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
            button.Click += (s, e) => { e.Handled = true; action(); };
            return button;
        }

        private static FrameworkElement At(FrameworkElement element, int column)
        {
            Grid.SetColumn(element, column);
            return element;
        }

        private void OnClear()
        {
            if (!_confirming)
            {
                _confirming = true;
                _clearButton.Content = L.T("再点一次清除");
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    _confirming = false;
                    _clearButton.Content = L.T("清除已结束");
                };
                timer.Start();
                return;
            }
            _confirming = false;
            _clearButton.Content = L.T("清除已结束");
            DownloadManager.ClearEnded();
            Refresh();
            Notify(L.T("已清除已完成的下载记录"));
        }

        private void Notify(string message)
        {
            if (_notify != null) _notify(message);
        }
    }
}
