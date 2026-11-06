using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace NanoEdge
{
    // All brushes live in Application.Resources under stable keys. Controls use
    // SetResourceReference so a theme switch needs no UI rebuild (important:
    // WebView2 is an HwndHost and must never be reparented).
    public static class Theme
    {
        public static bool IsDark { get; private set; } = true;

        private static SolidColorBrush Make(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        public static void Apply(string theme)
        {
            var dark = theme == "dark" || (theme == "system" && SystemIsDark());
            IsDark = dark;
            var r = Application.Current.Resources;

            if (dark)
            {
                r["CanvasBrush"] = Make("#0E1116");
                r["ChromeBrush"] = Make("#141922");
                r["SurfaceBrush"] = Make("#1A202B");
                r["HoverBrush"] = Make("#202836");
                r["LineBrush"] = Make("#232B39");
                r["TextBrush"] = Make("#E6EAF2");
                r["MutedBrush"] = Make("#98A2B3");
                r["AccentBrush"] = Make("#FF4D9D");
                r["AccentSoftBrush"] = Make("#33202D");
                r["DangerBrush"] = Make("#F87171");
                r["InputBrush"] = Make("#10151C");
                r["InputBorderBrush"] = Make("#3A4759");
                r["StateActiveBrush"] = Make("#FF4D9D");
                r["StateLowBrush"] = Make("#F5A524");
                r["StateDiscardedBrush"] = Make("#4B5563");
                r["WarnBrush"] = Make("#F5A524");
            }
            else
            {
                r["CanvasBrush"] = Make("#F3F5F9");
                r["ChromeBrush"] = Make("#FFFFFF");
                r["SurfaceBrush"] = Make("#EEF1F6");
                r["HoverBrush"] = Make("#E3E8F0");
                r["LineBrush"] = Make("#DDE3EC");
                r["TextBrush"] = Make("#1B2230");
                r["MutedBrush"] = Make("#5B6678");
                r["AccentBrush"] = Make("#D61F76");
                r["AccentSoftBrush"] = Make("#FBE4EF");
                r["DangerBrush"] = Make("#D64545");
                r["InputBrush"] = Make("#FFFFFF");
                r["InputBorderBrush"] = Make("#C9D2DF");
                r["StateActiveBrush"] = Make("#D61F76");
                r["StateLowBrush"] = Make("#B8791A");
                r["StateDiscardedBrush"] = Make("#98A2B3");
                r["WarnBrush"] = Make("#B8791A");
            }

            r["TabActiveBrush"] = r["SurfaceBrush"];
            r["TabIdleBrush"] = new SolidColorBrush(Colors.Transparent);

            // A5：项目没有 XAML，ContextMenu / MenuItem 用的是 WPF 系统模板。
            // 系统模板里 Icon 列和子菜单 Popup 的 Border 取 SystemColors 的恒定
            // 浅色，文字却继承了 TextBrush，暗色下就成了白边 / 白底白字。
            // 把这些系统键指到主题画刷，系统模板才会跟着主题走。
            r[SystemColors.MenuBarBrushKey] = r["ChromeBrush"];
            r[SystemColors.MenuBrushKey] = r["SurfaceBrush"];
            r[SystemColors.MenuTextBrushKey] = r["TextBrush"];
            r[SystemColors.HighlightBrushKey] = r["HoverBrush"];
            r[SystemColors.HighlightTextBrushKey] = r["TextBrush"];
            r[SystemColors.ControlBrushKey] = r["SurfaceBrush"];
            r[SystemColors.ControlTextBrushKey] = r["TextBrush"];
            r[SystemColors.ControlDarkBrushKey] = r["LineBrush"];
            r[SystemColors.WindowBrushKey] = r["SurfaceBrush"];
            r[SystemColors.WindowTextBrushKey] = r["TextBrush"];

            InstallMenuTemplates(r);
        }

        // A5：三处菜单（MainWindow / StartPage / MemoryPanel）必须走同一个工厂，
        // 漏一处就会留白条。用 SetResourceReference，切主题不重建 UI、不重载页面。
        public static ContextMenu StyleMenu(ContextMenu menu)
        {
            menu.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
            menu.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            menu.SetResourceReference(Control.BorderBrushProperty, "LineBrush");
            return menu;
        }

        public static MenuItem StyleMenuItem(MenuItem item)
        {
            // MenuItem 的 Background 以前没设，系统模板外层 Border 就会退回
            // SystemColors 的浅色，这是「左侧白条」最直接的来源。
            item.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
            item.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            return item;
        }

        public static Separator StyleSeparator(Separator separator)
        {
            // 菜单里的 Separator 会走 MenuItem.SeparatorStyleKey，不吃隐式样式；
            // 直接把 Template 设成本地值才能彻底接管（否则系统模板的
            // #FFD7D7D7 边框还会留在模板树里）。
            separator.Template = MakeSeparatorTemplate();
            separator.SetResourceReference(Control.BackgroundProperty, "LineBrush");
            separator.SetResourceReference(Control.ForegroundProperty, "LineBrush");
            return separator;
        }

        // A5 第二轮：只覆盖 SystemColors 键治不了系统 MenuItem 模板。诊断里
        // templateRoot / GlyphPanel 的 BorderBrush 是硬编码的 #FF26A0DA / #FFD7D7D7，
        // 不读任何资源键，所以「每行内部深色了、边框还是白的」。
        // 改成注入完整 ControlTemplate：一级菜单、二级 Popup、每行边框、高亮、
        // 分隔线全部由主题画刷接管，彻底不依赖 Windows 主题字典。
        // 全部用 C# 的 FrameworkElementFactory 构建，不引入 XAML。
        private static void InstallMenuTemplates(ResourceDictionary r)
        {
            r[typeof(ContextMenu)] = MakeContextMenuStyle();
            r[typeof(MenuItem)] = MakeMenuItemStyle(r);
            r[typeof(Separator)] = MakeSeparatorStyle();
        }

        private static Style MakeContextMenuStyle()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "NanoMenuBorder";
            border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.PaddingProperty, new Thickness(3));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

            var scroller = new FrameworkElementFactory(typeof(ScrollViewer));
            scroller.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,
                ScrollBarVisibility.Disabled);
            scroller.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,
                ScrollBarVisibility.Auto);
            // ScrollViewer 的系统模板也会带浅色边框，一并归零。
            scroller.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            scroller.SetValue(Control.BorderBrushProperty, Brushes.Transparent);
            scroller.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            var items = new FrameworkElementFactory(typeof(ItemsPresenter));
            scroller.AppendChild(items);
            border.AppendChild(scroller);

            var style = new Style(typeof(ContextMenu));
            style.Setters.Add(new Setter(Control.TemplateProperty,
                new ControlTemplate(typeof(ContextMenu)) { VisualTree = border }));
            return style;
        }

        private static Style MakeMenuItemStyle(ResourceDictionary r)
        {
            // Grid 可以叠多个子元素：高亮层、内容层、二级 Popup 共用一个单元格。
            var root = new FrameworkElementFactory(typeof(Grid));
            root.Name = "NanoItemRoot";
            root.SetResourceReference(Panel.BackgroundProperty, "SurfaceBrush");
            root.SetValue(FrameworkElement.MinHeightProperty, 26d);
            root.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

            var highlight = new FrameworkElementFactory(typeof(Border));
            highlight.Name = "NanoHighlight";
            highlight.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            highlight.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            highlight.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            highlight.SetValue(Border.BorderThicknessProperty, new Thickness(0));
            highlight.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            root.AppendChild(highlight);

            var dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.SetValue(DockPanel.LastChildFillProperty, true);

            var arrow = new FrameworkElementFactory(typeof(TextBlock));
            arrow.Name = "NanoSubmenuArrow";
            arrow.SetValue(TextBlock.TextProperty, "▸");
            arrow.SetValue(TextBlock.FontSizeProperty, 10d);
            arrow.SetValue(DockPanel.DockProperty, Dock.Right);
            arrow.SetValue(FrameworkElement.WidthProperty, 18d);
            arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            arrow.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            dock.AppendChild(arrow);

            var icon = new FrameworkElementFactory(typeof(ContentPresenter));
            icon.SetValue(ContentPresenter.ContentSourceProperty, "Icon");
            icon.SetValue(DockPanel.DockProperty, Dock.Left);
            icon.SetValue(FrameworkElement.WidthProperty, 24d);
            icon.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            dock.AppendChild(icon);

            var header = new FrameworkElementFactory(typeof(ContentPresenter));
            header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            header.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            header.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 3, 6, 3));
            dock.AppendChild(header);
            root.AppendChild(dock);

            // 二级菜单：Popup 的 Child 是自定义 Border，白底白边从这里根治。
            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "NanoSubmenuPopup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Right);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(UIElement.FocusableProperty, false);
            popup.SetBinding(Popup.IsOpenProperty, new Binding("IsSubmenuOpen")
            {
                RelativeSource = RelativeSource.TemplatedParent,
                Mode = BindingMode.TwoWay
            });
            popup.SetBinding(Popup.PlacementTargetProperty,
                new Binding { RelativeSource = RelativeSource.TemplatedParent });

            var subBorder = new FrameworkElementFactory(typeof(Border));
            subBorder.Name = "NanoSubmenuBorder";
            subBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            subBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            subBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            subBorder.SetValue(Border.PaddingProperty, new Thickness(3));
            subBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            subBorder.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            var subItems = new FrameworkElementFactory(typeof(ItemsPresenter));
            subBorder.AppendChild(subItems);
            popup.AppendChild(subBorder);
            root.AppendChild(popup);

            var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = root };

            // Setter 里不能塞动态资源，所以高亮只切 Visibility（简单枚举值），
            // 画刷本身由高亮层的 SetResourceReference 跟随主题。
            var hi = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            hi.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible)
            {
                TargetName = "NanoHighlight"
            });
            template.Triggers.Add(hi);

            var noSub = new Trigger { Property = ItemsControl.HasItemsProperty, Value = false };
            noSub.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed)
            {
                TargetName = "NanoSubmenuArrow"
            });
            template.Triggers.Add(noSub);
            var hasSub = new Trigger { Property = ItemsControl.HasItemsProperty, Value = true };
            hasSub.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible)
            {
                TargetName = "NanoSubmenuArrow"
            });
            template.Triggers.Add(hasSub);

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            template.Triggers.Add(disabled);

            var style = new Style(typeof(MenuItem));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(Control.ForegroundProperty, r["TextBrush"]));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
            return style;
        }

        private static ControlTemplate MakeSeparatorTemplate()
        {
            var line = new FrameworkElementFactory(typeof(Border));
            line.Name = "NanoMenuSeparator";
            line.SetValue(FrameworkElement.NameProperty, "NanoMenuSeparator");
            line.SetValue(FrameworkElement.HeightProperty, 1d);
            line.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 4, 4, 4));
            line.SetResourceReference(Border.BackgroundProperty, "LineBrush");
            // 系统 Separator 模板带 #FFD7D7D7 的 BorderBrush，显式归零别留下隐患。
            line.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            line.SetValue(Border.BorderThicknessProperty, new Thickness(0));
            line.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            return new ControlTemplate(typeof(Separator)) { VisualTree = line };
        }

        private static Style MakeSeparatorStyle()
        {
            var style = new Style(typeof(Separator));
            style.Setters.Add(new Setter(Control.TemplateProperty, MakeSeparatorTemplate()));
            return style;
        }

        private static bool SystemIsDark()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return value is int && (int)value == 0;
                }
            }
            catch { return true; }
        }

        public static FrameworkElement Bind(FrameworkElement element, DependencyProperty property, string key)
        {
            element.SetResourceReference(property, key);
            return element;
        }

        public static Border Surface(double radius = 6)
        {
            var border = new Border { CornerRadius = new CornerRadius(radius) };
            border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            return border;
        }
    }
}
