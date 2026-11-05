using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NanoEdge
{
    // Pure WPF start page: opening a new tab creates ZERO WebView2 instances.
    // That is what keeps the empty-tab footprint at ~59MB instead of ~200MB.
    // 快捷方式可增删改（SPEC 7.8），编辑器是页内浮层而不是 Popup：
    // 这个页面本来就没有 WebView2，浮层叠在 WPF 上不会被 HwndHost 盖住。
    public sealed class StartPage : UserControl
    {
        public event Action<string> NavigateRequested;

        private const int Columns = 4;
        private const int MaxShortcuts = 24;

        private readonly AppSettings _settings;
        private readonly TextBox _search;
        private Grid _shortcutGrid;

        private Grid _overlay;
        private TextBlock _editorHeading;
        private TextBox _editorTitle;
        private TextBox _editorUrl;
        private TextBlock _editorError;
        private Border _deleteButton;
        private int _editingIndex = -1;

        public StartPage(AppSettings settings)
        {
            _settings = settings;
            this.SetResourceReference(BackgroundProperty, "CanvasBrush");

            var root = new Grid();
            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 580
            };

            var title = new TextBlock
            {
                Text = "NanoEdge",
                FontSize = 34,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 26)
            };
            var brand = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#4F6FD8"),
                (Color)ColorConverter.ConvertFromString("#E38F4A"),
                new Point(0, 0), new Point(1, 1));
            brand.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#8B57D6"), 0.4));
            brand.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#DB5396"), 0.7));
            title.Foreground = brand;
            stack.Children.Add(title);

            var searchBorder = new Border
            {
                Height = 42,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 28)
            };
            searchBorder.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            searchBorder.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");

            var searchGrid = new Grid();
            _search = new TextBox
            {
                Name = "SearchBox",
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 13,
                Margin = new Thickness(14, 0, 14, 0)
            };
            _search.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
            _search.SetResourceReference(TextBox.CaretBrushProperty, "AccentBrush");
            var searchHint = new TextBlock
            {
                Text = L.T("搜索或输入网址"),
                FontSize = 13,
                Margin = new Thickness(15, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            searchHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _search.TextChanged += (s, e) =>
                searchHint.Visibility = string.IsNullOrEmpty(_search.Text) ? Visibility.Visible : Visibility.Collapsed;
            _search.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_search.Text))
                {
                    NavigateRequested?.Invoke(_search.Text.Trim());
                    e.Handled = true;
                }
            };
            searchGrid.Children.Add(_search);
            searchGrid.Children.Add(searchHint);
            searchBorder.Child = searchGrid;
            stack.Children.Add(searchBorder);

            _shortcutGrid = new Grid();
            stack.Children.Add(_shortcutGrid);
            FillShortcuts();

            var hint = new TextBlock
            {
                Text = L.T("输入网址或搜索词 · 支持 b / g / gh / npm 前缀"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 24, 0, 0)
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            stack.Children.Add(hint);

            root.Children.Add(stack);
            root.Children.Add(BuildEditorOverlay());
            Content = root;

            Loaded += (s, e) => { if (_overlay.Visibility != Visibility.Visible) _search.Focus(); };
        }

        // ---------- 快捷方式网格 ----------

        private void FillShortcuts()
        {
            _shortcutGrid.Children.Clear();
            _shortcutGrid.ColumnDefinitions.Clear();
            _shortcutGrid.RowDefinitions.Clear();
            for (var i = 0; i < Columns; i++)
                _shortcutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var shortcuts = _settings.Shortcuts ?? (_settings.Shortcuts = new List<Shortcut>());
            var slots = shortcuts.Count + (shortcuts.Count < MaxShortcuts ? 1 : 0);
            var rows = Math.Max(1, (slots + Columns - 1) / Columns);
            for (var i = 0; i < rows; i++)
                _shortcutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(74) });

            for (var i = 0; i < shortcuts.Count; i++)
            {
                var tile = MakeShortcutTile(i, shortcuts[i]);
                Grid.SetRow(tile, i / Columns);
                Grid.SetColumn(tile, i % Columns);
                _shortcutGrid.Children.Add(tile);
            }

            if (shortcuts.Count < MaxShortcuts)
            {
                var add = MakeAddTile();
                Grid.SetRow(add, shortcuts.Count / Columns);
                Grid.SetColumn(add, shortcuts.Count % Columns);
                _shortcutGrid.Children.Add(add);
            }
        }

        private FrameworkElement MakeShortcutTile(int index, Shortcut item)
        {
            var border = new Border
            {
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = item.Url,
                ToolTip = string.IsNullOrEmpty(item.Url) ? L.T("添加快捷方式") : item.Url
            };
            border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 6, 0)
            };

            var badge = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(6)
            };
            var icon = Favicons.Get(item.Url);
            if (icon != null)
            {
                var image = new Image { Source = icon, Width = 22, Height = 22, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                badge.Child = image;
            }
            else
            {
                badge.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
                var initial = string.IsNullOrEmpty(item.Title) ? "+" : item.Title.Substring(0, 1).ToUpperInvariant();
                var letter = new TextBlock
                {
                    Text = initial,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                letter.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                badge.Child = letter;
            }

            var label = new TextBlock
            {
                Text = item.Title,
                FontSize = 11,
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            panel.Children.Add(badge);
            panel.Children.Add(label);
            border.Child = panel;

            border.MouseEnter += (s, e) => border.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            border.MouseLeave += (s, e) => border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            border.MouseLeftButtonUp += (s, e) =>
            {
                if (!string.IsNullOrEmpty(item.Url)) NavigateRequested?.Invoke(item.Url);
            };

            var menu = MakeMenu();
            menu.Items.Add(MakeMenuItem(L.T("编辑"), () => ShowEditor(index)));
            menu.Items.Add(MakeMenuItem(L.T("删除"), () =>
            {
                var list = _settings.Shortcuts;
                if (list == null || index < 0 || index >= list.Count) return;
                list.RemoveAt(index);
                SettingsStore.Save(_settings);
                FillShortcuts();
            }));
            border.ContextMenu = menu;
            return border;
        }

        private FrameworkElement MakeAddTile()
        {
            var border = new Border
            {
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Colors.Transparent)
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 6, 0)
            };
            var plus = new TextBlock { Text = "+", FontSize = 18, Width = 26, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            plus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var label = new TextBlock { Text = L.T("添加快捷方式"), FontSize = 11, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(plus);
            panel.Children.Add(label);
            border.Child = panel;

            border.MouseEnter += (s, e) => border.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            border.MouseLeave += (s, e) => border.Background = new SolidColorBrush(Colors.Transparent);
            border.MouseLeftButtonUp += (s, e) => ShowEditor(-1);
            return border;
        }

        // ---------- 页内编辑器 ----------

        private FrameworkElement BuildEditorOverlay()
        {
            _overlay = new Grid { Visibility = Visibility.Collapsed };
            _overlay.Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
            _overlay.MouseLeftButtonDown += (s, e) => { if (ReferenceEquals(e.OriginalSource, _overlay)) HideEditor(); };

            var card = new Border
            {
                Width = 440,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(20, 18, 20, 16)
            };
            card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

            var stack = new StackPanel();
            _editorHeading = new TextBlock { Text = L.T("添加快捷方式"), FontSize = 14, Margin = new Thickness(0, 0, 0, 14) };
            _editorHeading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            stack.Children.Add(_editorHeading);

            stack.Children.Add(FieldLabel(L.T("标题")));
            _editorTitle = MakeInput();
            stack.Children.Add(_editorTitle);

            stack.Children.Add(FieldLabel(L.T("网址")));
            _editorUrl = MakeInput();
            stack.Children.Add(_editorUrl);

            _editorError = new TextBlock { Text = "", FontSize = 10.5, Margin = new Thickness(2, 8, 0, 0) };
            _editorError.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            stack.Children.Add(_editorError);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            _deleteButton = MakeButton(L.T("删除"), false, () =>
            {
                var list = _settings.Shortcuts;
                if (list != null && _editingIndex >= 0 && _editingIndex < list.Count)
                    list.RemoveAt(_editingIndex);
                SettingsStore.Save(_settings);
                HideEditor();
                FillShortcuts();
            });
            _deleteButton.Margin = new Thickness(0, 0, 8, 0);
            _deleteButton.Visibility = Visibility.Collapsed;
            buttons.Children.Add(_deleteButton);
            var cancel = MakeButton(L.T("取消"), false, HideEditor);
            cancel.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(cancel);
            buttons.Children.Add(MakeButton(L.T("保存"), true, CommitEditor));
            stack.Children.Add(buttons);

            card.Child = stack;
            _overlay.Children.Add(card);

            _overlay.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { HideEditor(); e.Handled = true; }
            };
            return _overlay;
        }

        private TextBlock FieldLabel(string text)
        {
            var label = new TextBlock { Text = text, FontSize = 10.5, Margin = new Thickness(2, 8, 0, 4) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return label;
        }

        private TextBox MakeInput()
        {
            var box = new TextBox
            {
                Height = 32,
                FontSize = 12,
                Padding = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1)
            };
            box.SetResourceReference(TextBox.BackgroundProperty, "InputBrush");
            box.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
            box.SetResourceReference(TextBox.CaretBrushProperty, "AccentBrush");
            box.SetResourceReference(TextBox.BorderBrushProperty, "InputBorderBrush");
            return box;
        }

        private Border MakeButton(string text, bool primary, Action action)
        {
            var border = new Border
            {
                Height = 30,
                Padding = new Thickness(14, 0, 14, 0),
                CornerRadius = new CornerRadius(7),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 11.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            border.SetResourceReference(Border.BackgroundProperty, primary ? "AccentSoftBrush" : "HoverBrush");
            ((TextBlock)border.Child).SetResourceReference(TextBlock.ForegroundProperty, primary ? "AccentBrush" : "TextBrush");
            border.MouseLeftButtonUp += (s, e) => { e.Handled = true; action(); };
            return border;
        }

        private void ShowEditor(int index)
        {
            var list = _settings.Shortcuts;
            if (list == null) { list = new List<Shortcut>(); _settings.Shortcuts = list; }
            if (index < 0 && list.Count >= MaxShortcuts) return;

            _editingIndex = index;
            var item = index >= 0 && index < list.Count ? list[index] : new Shortcut();
            _editorHeading.Text = index >= 0 ? L.T("编辑快捷方式") : L.T("添加快捷方式");
            _editorTitle.Text = item.Title ?? "";
            _editorUrl.Text = item.Url ?? "";
            _editorError.Text = "";
            _deleteButton.Visibility = index >= 0 ? Visibility.Visible : Visibility.Collapsed;
            _overlay.Visibility = Visibility.Visible;
            _editorTitle.Focus();
            _editorTitle.SelectAll();
        }

        private void HideEditor()
        {
            _overlay.Visibility = Visibility.Collapsed;
            _editingIndex = -1;
            _search.Focus();
        }

        private void CommitEditor()
        {
            var list = _settings.Shortcuts;
            if (list == null) { list = new List<Shortcut>(); _settings.Shortcuts = list; }

            var url = NormalizeShortcutUrl(_editorUrl.Text);
            if (url == null)
            {
                _editorError.Text = L.T("请输入有效的网址");
                _editorUrl.Focus();
                return;
            }

            var title = (_editorTitle.Text ?? "").Trim();
            if (title.Length == 0) title = ShortcutTitle(url);
            var entry = new Shortcut { Title = title, Url = url };

            if (_editingIndex >= 0 && _editingIndex < list.Count) list[_editingIndex] = entry;
            else if (list.Count < MaxShortcuts) list.Add(entry);

            SettingsStore.Save(_settings);
            HideEditor();
            FillShortcuts();
        }

        private static string NormalizeShortcutUrl(string text)
        {
            var value = (text ?? "").Trim();
            if (value.Length == 0) return null;
            if (value.StartsWith("nano://", StringComparison.OrdinalIgnoreCase)) return value;
            if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return value;
            return UrlParser.NormalizeUrl(value);
        }

        private static string ShortcutTitle(string url)
        {
            try
            {
                var uri = new Uri(url);
                return uri.IsFile ? L.T("本地文件") : uri.Host;
            }
            catch { return url; }
        }

        private ContextMenu MakeMenu()
        {
            var menu = new ContextMenu();
            menu.SetResourceReference(ContextMenu.BackgroundProperty, "SurfaceBrush");
            menu.SetResourceReference(ContextMenu.ForegroundProperty, "TextBrush");
            menu.SetResourceReference(ContextMenu.BorderBrushProperty, "LineBrush");
            return menu;
        }

        private static MenuItem MakeMenuItem(string text, Action action)
        {
            var item = new MenuItem { Header = text, FontSize = 12 };
            item.SetResourceReference(MenuItem.ForegroundProperty, "TextBrush");
            item.Click += (s, e) => action();
            return item;
        }

        // ---------- 自测入口（不改运行路径） ----------

        internal bool EditorVisibleForTest { get { return _overlay.Visibility == Visibility.Visible; } }

        internal void BeginAddForTest() { ShowEditor(-1); }

        internal void BeginEditForTest(int index) { ShowEditor(index); }

        internal void SetEditorForTest(string title, string url)
        {
            _editorTitle.Text = title ?? "";
            _editorUrl.Text = url ?? "";
        }

        internal void CommitForTest() { CommitEditor(); }
    }
}
