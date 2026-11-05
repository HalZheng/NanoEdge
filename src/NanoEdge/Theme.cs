using System;
using System.Windows;
using System.Windows.Controls;
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
