using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NanoEdge
{
    // 已丢弃标签的占位视图（纯 WPF，零 WebView2）。SPEC 4.5 第一步。
    public sealed class DiscardedPlaceholder : Border
    {
        public event Action RestoreRequested;

        public DiscardedPlaceholder(TabModel tab)
        {
            this.SetResourceReference(BackgroundProperty, "CanvasBrush");

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 420
            };

            var badge = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(12),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            if (tab.Favicon != null)
            {
                var image = new Image { Source = tab.Favicon, Width = 30, Height = 30, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                badge.Child = image;
            }
            else
            {
                badge.SetResourceReference(BackgroundProperty, "AccentSoftBrush");
                var letter = new TextBlock
                {
                    Text = string.IsNullOrEmpty(tab.DisplayTitle) ? "N" : tab.DisplayTitle.Substring(0, 1).ToUpperInvariant(),
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                letter.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                badge.Child = letter;
            }
            stack.Children.Add(badge);

            var title = new TextBlock
            {
                Text = tab.DisplayTitle,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            stack.Children.Add(title);

            if (!string.IsNullOrEmpty(tab.Host))
            {
                var host = new TextBlock
                {
                    Text = tab.Host,
                    FontSize = 11,
                    Margin = new Thickness(0, 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                host.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                stack.Children.Add(host);
            }

            var state = new TextBlock
            {
                Text = L.T("已释放内存 · 页面状态已保存"),
                FontSize = 12,
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            state.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            stack.Children.Add(state);

            var button = new Border
            {
                Height = 32,
                Padding = new Thickness(18, 0, 18, 0),
                Margin = new Thickness(0, 18, 0, 0),
                CornerRadius = new CornerRadius(8),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = L.T("恢复页面"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            button.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            ((TextBlock)button.Child).SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            button.MouseEnter += (s, e) => button.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            button.MouseLeave += (s, e) => button.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            button.MouseLeftButtonUp += (s, e) =>
            {
                var handler = RestoreRequested;
                if (handler != null) handler();
            };
            stack.Children.Add(button);

            var note = new TextBlock
            {
                Text = L.T("renderer 进程已退出，工作集归零；提交内存随进程消失才真正归还"),
                FontSize = 10,
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            stack.Children.Add(note);

            Child = stack;
        }
    }
}
