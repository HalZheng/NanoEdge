using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NanoEdge
{
    // nano://settings：纯 WPF 设置页（SPEC 7.6 / P0-14）。
    // 页面只改 AppSettings 并抛 Changed，真正的应用（主题/布局/内存/语言）交给 MainWindow：
    // 语言切换要重建内部页视图，不能由页面自己干。
    public sealed class SettingsPage : UserControl
    {
        public event Action<string> Changed;              // theme | layout | memory | language | status | privacy
        public event Action OpenDataFolderRequested;
        public event Action SetDefaultBrowserRequested;

        private readonly AppSettings _settings;
        private readonly Grid _root;
        private readonly StackPanel _body;
        private TextBlock _memoryHint;

        public SettingsPage(AppSettings settings)
        {
            _settings = settings;
            this.SetResourceReference(BackgroundProperty, "CanvasBrush");

            _root = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 28, 0, 0)
            };
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var title = new TextBlock
            {
                Text = L.T("设置"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var subtitle = new TextBlock
            {
                Text = L.T("改动立即生效并自动保存"),
                FontSize = 10,
                Margin = new Thickness(0, 4, 0, 0)
            };
            subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(title);
            header.Children.Add(subtitle);
            _root.Children.Add(header);

            _body = new StackPanel { Margin = new Thickness(0, 0, 0, 28) };
            var scroll = new ScrollViewer
            {
                Content = _body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 1);
            _root.Children.Add(scroll);
            Content = _root;

            SizeChanged += (s, e) => UpdateWidth();
            UpdateWidth();
            Build();
        }

        private void UpdateWidth()
        {
            var available = ActualWidth > 0 ? ActualWidth : 980;
            _root.Width = Math.Min(980, Math.Max(360, available - 64));
        }

        private void Build()
        {
            _body.Children.Clear();

            // ---------- 外观 ----------
            _body.Children.Add(Section(L.T("外观")));
            _body.Children.Add(Row(L.T("主题"), L.T("暗色适合夜间，跟随系统会随 Windows 设置切换"),
                Segments(new[] { L.T("暗色"), L.T("亮色"), L.T("跟随系统") }, ThemeIndex(), index =>
                {
                    _settings.Theme = index == 0 ? "dark" : index == 1 ? "light" : "system";
                    Raise("theme");
                })));
            _body.Children.Add(Row(L.T("语言"), L.T("切换后界面立即重建，网页内容不受影响"),
                Segments(new[] { "中文", "English" }, L.IsEnglish ? 1 : 0, index =>
                {
                    _settings.Language = index == 1 ? "en-US" : "zh-CN";
                    Raise("language");
                })));

            // ---------- 布局 ----------
            _body.Children.Add(Section(L.T("布局")));
            _body.Children.Add(Row(L.T("标签布局"), L.T("紧凑单行把所有东西挤在一行，垂直标签在左侧"),
                Segments(new[] { L.T("紧凑单行"), L.T("标准两行"), L.T("垂直标签") }, (int)_settings.Layout, index =>
                {
                    _settings.Layout = (LayoutMode)index;
                    Raise("layout");
                })));
            _body.Children.Add(Row(L.T("垂直标签固定展开"), L.T("关闭时悬停才展开，节省横向空间"),
                Toggle(_settings.VerticalExpanded, on =>
                {
                    _settings.VerticalExpanded = on;
                    Raise("layout");
                })));
            _body.Children.Add(Row(L.T("显示状态栏"), L.T("底部一行：布局、主题、标签数、内存档位"),
                Toggle(_settings.ShowStatusBar, on =>
                {
                    _settings.ShowStatusBar = on;
                    Raise("status");
                })));

            // ---------- 内存 ----------
            _body.Children.Add(Section(L.T("内存")));
            _memoryHint = new TextBlock
            {
                Text = MemoryHintText(),
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap
            };
            _memoryHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _body.Children.Add(_memoryHint);
            _body.Children.Add(Row(L.T("档位"), L.T("只影响后台标签的降冷与丢弃，不改页面行为"),
                Segments(new[] { L.T("保守"), L.T("均衡"), L.T("激进") }, (int)_settings.Memory.Preset, index =>
                {
                    _settings.Memory.ApplyPreset((MemoryPreset)index);
                    if (_memoryHint != null) _memoryHint.Text = MemoryHintText();
                    Raise("memory");
                })));

            // ---------- 隐私 ----------
            _body.Children.Add(Section(L.T("隐私")));
            _body.Children.Add(Row(L.T("密封标签通知"), L.T("关闭时密封标签的页面通知请求会被直接拒绝"),
                Toggle(_settings.SecretNotifications, on =>
                {
                    _settings.SecretNotifications = on;
                    Raise("privacy");
                })));

            // ---------- 关于 ----------
            _body.Children.Add(Section(L.T("关于")));
            _body.Children.Add(Row(L.T("版本"), "NanoEdge " + Program.Version, null));
            _body.Children.Add(Row(L.T("数据目录"), AppPaths.Roaming,
                TextButton(L.T("打开"), () =>
                {
                    var handler = OpenDataFolderRequested;
                    if (handler != null) handler();
                })));
            _body.Children.Add(Row(L.T("默认浏览器"),
                L.T("只写注册表登记，Windows 会要求您在「默认应用」里确认"),
                TextButton(L.T("设为默认浏览器"), () =>
                {
                    var handler = SetDefaultBrowserRequested;
                    if (handler != null) handler();
                })));
        }

        private int ThemeIndex()
        {
            if (_settings.Theme == "light") return 1;
            if (_settings.Theme == "system") return 2;
            return 0;
        }

        private string MemoryHintText()
        {
            var memory = _settings.Memory;
            var discard = memory.BackgroundDiscardMs == int.MaxValue
                ? L.T("不自动丢弃")
                : string.Format(L.T("后台 {0} 分钟丢弃"), memory.BackgroundDiscardMs / 60000);
            return string.Format(L.T("失焦 {0} 秒降冷 · {1} · 工作集上限 {2}MB"),
                memory.CoolDelayMs / 1000, discard, memory.PressureLimitMB);
        }

        private void Raise(string kind)
        {
            // 落盘交给 MainWindow（它同时负责应用改动），页面只改值 + 通知。
            var handler = Changed;
            if (handler != null) handler(kind);
        }

        // ---------- 通用控件 ----------

        private FrameworkElement Section(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 20, 0, 6)
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return block;
        }

        private FrameworkElement Row(string label, string hint, FrameworkElement control)
        {
            var border = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(2, 10, 2, 10),
                MinHeight = 56
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            var labelBlock = new TextBlock { Text = label, FontSize = 12.5 };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            text.Children.Add(labelBlock);
            if (!string.IsNullOrEmpty(hint))
            {
                var hintBlock = new TextBlock
                {
                    Text = hint,
                    FontSize = 10,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxHeight = 30
                };
                hintBlock.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                text.Children.Add(hintBlock);
            }
            grid.Children.Add(text);

            if (control != null)
            {
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 1);
                grid.Children.Add(control);
            }

            border.Child = grid;
            return border;
        }

        private FrameworkElement Segments(string[] labels, int selected, Action<int> onPick)
        {
            var outer = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(3),
                BorderThickness = new Thickness(1)
            };
            outer.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            outer.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var items = new Border[labels.Length];

            for (var i = 0; i < labels.Length; i++)
            {
                var index = i;
                var text = new TextBlock
                {
                    Text = labels[i],
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var item = new Border
                {
                    Height = 24,
                    Padding = new Thickness(11, 0, 11, 0),
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    Child = text
                };
                item.MouseLeftButtonUp += (s, e) =>
                {
                    Select(items, index);
                    onPick(index);
                };
                items[i] = item;
                panel.Children.Add(item);
            }

            Select(items, selected);
            outer.Child = panel;
            return outer;
        }

        private static void Select(Border[] items, int selected)
        {
            for (var i = 0; i < items.Length; i++)
            {
                var text = items[i].Child as TextBlock;
                if (i == selected)
                {
                    items[i].SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
                    if (text != null) text.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                }
                else
                {
                    items[i].Background = new SolidColorBrush(Colors.Transparent);
                    if (text != null) text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                }
            }
        }

        private FrameworkElement Toggle(bool on, Action<bool> changed)
        {
            var track = new Border
            {
                Width = 34,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            track.SetResourceReference(Border.BackgroundProperty, on ? "AccentBrush" : "StateDiscardedBrush");

            var knob = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(2, 0, 2, 0)
            };
            track.Child = knob;

            var state = on;
            track.MouseLeftButtonUp += (s, e) =>
            {
                state = !state;
                track.SetResourceReference(Border.BackgroundProperty,
                    state ? "AccentBrush" : "StateDiscardedBrush");
                knob.HorizontalAlignment = state ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                changed(state);
            };
            return track;
        }

        private FrameworkElement TextButton(string text, Action action)
        {
            var button = new Button
            {
                Content = text,
                Height = 28,
                MinWidth = 64,
                FontSize = 11,
                Padding = new Thickness(12, 0, 12, 0),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            button.SetResourceReference(Control.BackgroundProperty, "HoverBrush");
            button.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            button.Click += (s, e) => action();
            return button;
        }
    }
}
