using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace NanoEdge
{
    public sealed partial class MainWindow
    {
        // =====================================================================
        // 登录填充的界面层（SPEC 8.x）
        // 地址栏右侧的钥匙图标 + 账号列表弹层。密码只在「填充」这一瞬间经过宿主，
        // 弹层里只显示用户名和条目名，不显示密码。
        // 颜色一律走 SetResourceReference：切主题时不重建 UI 也能跟着变。
        // =====================================================================

        private Border _loginButton;
        private Popup _loginPopup;
        private List<LoginField> _loginFields = new List<LoginField>();
        private bool _loginBusy;

        private FrameworkElement BuildLoginButton()
        {
            var icon = MakeKeyIcon();
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;

            _loginButton = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed,
                ToolTip = L.T("填充账号密码"),
                Tag = "interactive",
                Background = Brushes.Transparent,
                Child = icon
            };
            _loginButton.MouseEnter += (s, e) =>
                _loginButton.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            _loginButton.MouseLeave += (s, e) =>
                _loginButton.Background = Brushes.Transparent;
            _loginButton.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                OnLoginClick();
            };
            return _loginButton;
        }

        // 用 KeePassXC 官方 logo（和 Chrome 的 KeePassXC-Browser 插件一致），
        // 用户一眼就知道「这是 KeePassXC 在填」。资源缺失时退回手画矢量钥匙。
        private static FrameworkElement MakeKeyIcon()
        {
            var source = LoadEmbeddedIcon("NanoEdge.Native.keepassxc.png");
            if (source == null) return MakeFallbackKeyIcon();
            return new Image
            {
                Width = 15,
                Height = 15,
                Stretch = Stretch.Uniform,
                Source = source
            };
        }

        private static System.Windows.Media.ImageSource LoadEmbeddedIcon(string resourceName)
        {
            try
            {
                using (var stream = System.Reflection.Assembly.GetExecutingAssembly()
                           .GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return null;
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();     // 冻结后才能跨线程共享
                    return bitmap;
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("load keepassxc icon failed: " + ex.Message);
                return null;
            }
        }

        private static FrameworkElement MakeFallbackKeyIcon()
        {
            var key = new System.Windows.Shapes.Path
            {
                // 钥匙柄（两个半圆弧成整圆）+ 横杆 + 两个不等长的齿
                Data = Geometry.Parse(
                    "M2,6 A3,3 0 1 1 8,6 A3,3 0 1 1 2,6 M8,6 L18,6 M13,6 L13,9.2 M18,6 L18,10.6"),
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stroke = Brushes.Gray,
                Stretch = Stretch.Uniform,
                Width = 14,
                Height = 11
            };
            key.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "MutedBrush");
            return key;
        }

        // 导航完成后调用：扫一次页面，有密码框才亮图标。
        // 不常驻观察器 —— 慢渲染的 SPA 靠「点之前再扫一次」兜底。
        private async void RefreshLoginState(TabModel tab)
        {
            // 开关关掉后，导航时排下的延迟扫描仍会跑到这里，必须在这里也挡一道，
            // 否则图标会在关闭后闪回来。
            if (!_settings.PasswordFill) { HideLoginButton(); return; }
            if (tab == null || tab != _active || _loginButton == null) return;
            var core = _browser.Core(tab);
            if (core == null) { HideLoginButton(); return; }
            try
            {
                var fields = await LoginFill.ScanAsync(core);
                if (tab != _active) return;      // 扫描期间切走了
                _loginFields = fields;
                _loginButton.Visibility = fields.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { Diagnostics.Log("refresh login state failed: " + ex.Message); }
        }

        private void HideLoginButton()
        {
            _loginFields.Clear();
            if (_loginButton != null) _loginButton.Visibility = Visibility.Collapsed;
        }

        private async void OnLoginClick()
        {
            if (_loginBusy || !_settings.PasswordFill) return;
            var tab = _active;
            if (tab == null) return;
            var core = _browser.Core(tab);
            if (core == null) return;

            _loginBusy = true;
            try
            {
                // 点之前重扫：页面可能是导航之后才把表单渲染出来的
                var fields = await LoginFill.ScanAsync(core);
                if (tab != _active) return;
                _loginFields = fields;
                if (fields.Count == 0)
                {
                    HideLoginButton();
                    _toast.Show(L.T("这个页面没有找到登录框"));
                    return;
                }

                var url = tab.Url ?? "";
                var result = await KeePassBridge.GetLoginsAsync(url);
                if (tab != _active) return;

                if (result.Status == KeePassStatus.NotAssociated)
                {
                    // 首次使用：走一次关联，KeePassXC 会弹窗要用户点允许
                    _toast.Show(L.T("请在 KeePassXC 弹窗里允许 NanoEdge 访问"));
                    var assoc = await KeePassBridge.AssociateAsync("NanoEdge", TimeSpan.FromSeconds(120));
                    if (tab != _active) return;
                    if (assoc.Status != KeePassStatus.Ok)
                    {
                        _toast.Show(KeePassBridge.StatusText(assoc.Status) +
                                    (string.IsNullOrEmpty(assoc.Message) ? "" : "：" + assoc.Message));
                        return;
                    }
                    result = await KeePassBridge.GetLoginsAsync(url);
                    if (tab != _active) return;
                }

                if (!result.Ok)
                {
                    ShowLoginPopup(new List<KeePassCredential>(),
                        KeePassBridge.StatusText(result.Status));
                    return;
                }
                ShowLoginPopup(result.Entries, null);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("login click failed: " + ex.Message);
                _toast.Show(L.T("KeePassXC 操作失败"));
            }
            finally { _loginBusy = false; }
        }

        private void ShowLoginPopup(List<KeePassCredential> entries, string notice)
        {
            if (_loginButton == null) return;
            if (_loginPopup != null) { _loginPopup.IsOpen = false; _loginPopup = null; }

            var content = new StackPanel { Orientation = Orientation.Vertical };

            content.Children.Add(MakeText(_active == null ? "" : HostLabel(_active.Url), 12, "TextBrush",
                FontWeights.Medium, new Thickness(10, 8, 10, 6)));

            var separator = new Border { Height = 1, Margin = new Thickness(6, 0, 6, 4) };
            separator.SetResourceReference(Border.BackgroundProperty, "LineBrush");
            content.Children.Add(separator);

            if (!string.IsNullOrEmpty(notice))
            {
                content.Children.Add(MakeText(notice, 12, "MutedBrush", null,
                    new Thickness(10, 4, 10, 8), TextWrapping.Wrap, 260));
            }
            else if (entries.Count == 0)
            {
                content.Children.Add(MakeText(L.T("KeePassXC 里没有匹配这个站点的条目"), 12, "MutedBrush", null,
                    new Thickness(10, 4, 10, 8), TextWrapping.Wrap, 260));
            }
            else
            {
                foreach (var entry in entries) content.Children.Add(MakeCredentialRow(entry));
            }

            // 底部动作：重新扫描 + 重新取密码（对应 KeePassXC 插件的 reload）
            var footer = new Border
            {
                Padding = new Thickness(10, 6, 10, 8),
                Child = MakePopupButton(L.T("重新加载"), () =>
                {
                    if (_loginPopup != null) _loginPopup.IsOpen = false;
                    OnLoginClick();
                })
            };
            content.Children.Add(footer);

            var root = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2),
                Child = content
            };
            root.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            root.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

            _loginPopup = new Popup
            {
                Placement = PlacementMode.Bottom,
                PlacementTarget = _loginButton,
                VerticalOffset = 6,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = root
            };
            _loginPopup.IsOpen = true;
        }

        private FrameworkElement MakeCredentialRow(KeePassCredential entry)
        {
            var panel = new StackPanel { Orientation = Orientation.Vertical };
            panel.Children.Add(MakeText(
                string.IsNullOrEmpty(entry.Name) ? entry.Login : entry.Name, 12, "TextBrush"));
            if (!string.IsNullOrEmpty(entry.Login) &&
                !string.Equals(entry.Name, entry.Login, StringComparison.Ordinal))
                panel.Children.Add(MakeText(entry.Login, 11, "MutedBrush"));

            var row = new Border
            {
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(5),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                MinWidth = 200,
                Child = panel
            };
            row.MouseEnter += (s, e) => row.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                FillCredential(entry);
            };
            return row;
        }

        private FrameworkElement MakePopupButton(string text, Action action)
        {
            var label = MakeText(text, 11, "AccentBrush");
            label.HorizontalAlignment = HorizontalAlignment.Center;

            var button = new Border
            {
                Padding = new Thickness(8, 4, 8, 4),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = label
            };
            button.MouseEnter += (s, e) => button.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            button.MouseLeave += (s, e) => button.Background = Brushes.Transparent;
            button.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                if (action != null) action();
            };
            return button;
        }

        private async void FillCredential(KeePassCredential entry)
        {
            if (_loginPopup != null) _loginPopup.IsOpen = false;
            var tab = _active;
            if (tab == null || _loginFields.Count == 0) return;
            var core = _browser.Core(tab);
            if (core == null) return;

            var ok = await LoginFill.FillActiveAsync(core, entry.Login, entry.Password);
            _toast.Show(ok ? L.T("已填充账号密码") : L.T("填充失败，页面结构可能变了"));
            if (!ok) RefreshLoginState(tab);
        }

        private static TextBlock MakeText(string text, double size, string brushKey,
            FontWeight? weight = null, Thickness? margin = null,
            TextWrapping wrap = TextWrapping.NoWrap, double maxWidth = 0)
        {
            var block = new TextBlock { Text = text ?? "", FontSize = size };
            if (weight.HasValue) block.FontWeight = weight.Value;
            if (margin.HasValue) block.Margin = margin.Value;
            if (wrap != TextWrapping.NoWrap) block.TextWrapping = wrap;
            if (maxWidth > 0) block.MaxWidth = maxWidth;
            block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return block;
        }
    }
}
