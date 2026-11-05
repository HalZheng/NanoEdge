using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace NanoEdge
{
    // 地址栏联想下拉。Popup 是独立 HWND，天然盖在 WebView2 之上（SPEC 3.4）。
    // 键盘交互由 MainWindow 转发：↑↓ 选择 / Enter 打开 / Tab 补全 / Esc 关闭。
    public sealed class AddressSuggestions
    {
        private readonly Popup _popup;
        private readonly Border _root;
        private readonly StackPanel _list;
        private readonly List<Suggestion> _items = new List<Suggestion>();
        private readonly List<Border> _rows = new List<Border>();
        private readonly Action<Suggestion, bool> _chosen;
        private FrameworkElement _anchor;
        private int _index = -1;

        public AddressSuggestions(FrameworkElement anchor, Action<Suggestion, bool> chosen)
        {
            _anchor = anchor;
            _chosen = chosen;

            _list = new StackPanel { Orientation = Orientation.Vertical };
            _root = new Border
            {
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Child = new ScrollViewer
                {
                    Content = _list,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    MaxHeight = 320
                }
            };
            _root.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            _root.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

            _popup = new Popup
            {
                Placement = PlacementMode.Relative,
                PlacementTarget = anchor,
                StaysOpen = true,
                AllowsTransparency = true,
                Focusable = false,
                Child = _root
            };
        }

        public bool IsOpen { get { return _popup.IsOpen; } }
        public int Count { get { return _items.Count; } }
        public FrameworkElement Visual { get { return _root; } }

        public Suggestion Selected
        {
            get { return _index >= 0 && _index < _items.Count ? _items[_index] : null; }
        }

        public void SetAnchor(FrameworkElement anchor)
        {
            if (anchor == null) return;
            _anchor = anchor;
            _popup.PlacementTarget = anchor;
        }

        public void Update(string input, List<Suggestion> items)
        {
            if (items == null || items.Count == 0)
            {
                Close();
                return;
            }

            _items.Clear();
            _items.AddRange(items);
            _index = 0;
            Build();

            // 地址栏在紧凑布局下可能上千像素宽，下拉不跟着无限拉长。
            _root.Width = Math.Min(820, Math.Max(320, _anchor.ActualWidth));
            _popup.HorizontalOffset = 0;
            _popup.VerticalOffset = _anchor.ActualHeight + 6;
            if (!_popup.IsOpen) _popup.IsOpen = true;
            Highlight();
        }

        public void Move(int delta)
        {
            if (_items.Count == 0) return;
            _index += delta;
            if (_index < 0) _index = _items.Count - 1;
            if (_index >= _items.Count) _index = 0;
            Highlight();
        }

        public bool ChooseSelected(bool newTab)
        {
            var item = Selected;
            if (item == null) return false;
            Close();
            if (_chosen != null) _chosen(item, newTab);
            return true;
        }

        public void Close()
        {
            _popup.IsOpen = false;
            _items.Clear();
            _rows.Clear();
            _index = -1;
            _list.Children.Clear();
        }

        private void Build()
        {
            _list.Children.Clear();
            _rows.Clear();
            foreach (var item in _items)
            {
                var row = MakeRow(item);
                _rows.Add(row);
                _list.Children.Add(row);
            }
        }

        private Border MakeRow(Suggestion item)
        {
            var row = new Border
            {
                Height = 36,
                CornerRadius = new CornerRadius(5),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Colors.Transparent)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

            var glyph = new TextBlock
            {
                Text = item.KindGlyph,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty,
                item.Kind == SuggestionKind.Bookmark ? "AccentBrush" : "MutedBrush");
            grid.Children.Add(glyph);

            var text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 1, 8, 1)
            };
            var title = new TextBlock
            {
                Text = item.Title,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            text.Children.Add(title);
            var url = new TextBlock
            {
                Text = Shorten(item.Url, 96),
                FontSize = 9.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            url.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            text.Children.Add(url);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var kind = new TextBlock
            {
                Text = item.KindName,
                FontSize = 9,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            kind.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetColumn(kind, 2);
            grid.Children.Add(kind);

            row.Child = grid;
            row.MouseEnter += (s, e) => { };
            row.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                var newTab = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                Close();
                if (_chosen != null) _chosen(item, newTab);
            };
            return row;
        }

        private void Highlight()
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (i == _index) _rows[i].SetResourceReference(Border.BackgroundProperty, "HoverBrush");
                else _rows[i].Background = new SolidColorBrush(Colors.Transparent);
            }
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
            return text.Substring(0, max - 1) + "…";
        }
    }
}
