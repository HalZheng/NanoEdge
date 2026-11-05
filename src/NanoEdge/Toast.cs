using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace NanoEdge
{
    // 轻提示。WebView2 是 HwndHost，任何覆盖在页面上的 WPF 元素都会被盖住，
    // 所以 toast 也必须走 Popup（独立 HWND）。SPEC 3.4。
    public sealed class Toast
    {
        private const double ToastWidth = 360;

        private readonly Popup _popup;
        private readonly Border _root;
        private readonly TextBlock _text;
        private readonly Button _action;
        private readonly DispatcherTimer _timer;
        private Action _actionHandler;

        public Toast(FrameworkElement anchor)
        {
            _text = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = ToastWidth - 130
            };
            _text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            _action = new Button
            {
                Content = L.T("重新加载"),
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(10, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Visibility = Visibility.Collapsed
            };
            _action.SetResourceReference(Button.BackgroundProperty, "AccentSoftBrush");
            _action.SetResourceReference(Button.ForegroundProperty, "AccentBrush");
            _action.Click += (s, e) =>
            {
                var handler = _actionHandler;
                _popup.IsOpen = false;
                if (handler != null) handler();
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(_text);
            panel.Children.Add(_action);

            _root = new Border
            {
                Width = ToastWidth,
                Padding = new Thickness(14, 9, 10, 9),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Child = panel
            };
            _root.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            _root.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

            _popup = new Popup
            {
                Placement = PlacementMode.Relative,
                PlacementTarget = anchor,
                StaysOpen = true,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Focusable = false,
                Child = _root
            };

            _timer = new DispatcherTimer();
            _timer.Tick += (s, e) =>
            {
                _timer.Stop();
                _popup.IsOpen = false;
            };
        }

        public void Show(string text, string actionText = null, Action action = null, int milliseconds = 2600)
        {
            _text.Text = text;
            _actionHandler = action;
            if (string.IsNullOrEmpty(actionText) || action == null)
            {
                _action.Visibility = Visibility.Collapsed;
            }
            else
            {
                _action.Content = actionText;
                _action.Visibility = Visibility.Visible;
            }

            var target = _popup.PlacementTarget as FrameworkElement;
            if (target != null)
            {
                var width = target.ActualWidth;
                _popup.HorizontalOffset = Math.Max(8, (width - ToastWidth) / 2);
                _popup.VerticalOffset = 10;
            }
            _popup.IsOpen = true;

            _timer.Stop();
            _timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            _timer.Start();
        }

        public void Hide()
        {
            _timer.Stop();
            _popup.IsOpen = false;
        }

        public FrameworkElement Visual { get { return _root; } }
    }
}
