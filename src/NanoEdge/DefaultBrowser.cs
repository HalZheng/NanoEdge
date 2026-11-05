using System;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace NanoEdge
{
    // SPEC 7.9 / M6：只在用户显式点「设为默认浏览器」时写 HKCU，不主动提醒。
    // Windows 10+ 不允许程序直接改 UserChoice（带哈希保护），所以这里只做"注册"，
    // 真正的选择要在系统「默认应用」里点一下——文案必须说清楚，不能假装已生效。
    public static class DefaultBrowser
    {
        public const string ProgId = "NanoEdgeHTML";
        public const string AppKey = @"Software\Clients\StartMenuInternet\NanoEdge";

        public static string ExePath
        {
            get
            {
                try
                {
                    var path = Assembly.GetEntryAssembly() == null
                        ? null : Assembly.GetEntryAssembly().Location;
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                catch { }
                try { return Process.GetCurrentProcess().MainModule.FileName; }
                catch { return ""; }
            }
        }

        public static void Register()
        {
            var exe = ExePath;
            if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException(L.T("拿不到 exe 路径"));

            using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                classes.SetValue(null, "NanoEdge Document");
                using (var icon = classes.CreateSubKey("DefaultIcon"))
                    icon.SetValue(null, exe + ",0");
                using (var command = classes.CreateSubKey(@"shell\open\command"))
                    command.SetValue(null, Quote(exe) + " \"%1\"");
            }

            using (var app = Registry.CurrentUser.CreateSubKey(AppKey))
            {
                app.SetValue(null, "NanoEdge");
                using (var capabilities = app.CreateSubKey("Capabilities"))
                {
                    capabilities.SetValue("ApplicationName", "NanoEdge");
                    capabilities.SetValue("ApplicationDescription", L.T("轻量 Windows 浏览器"));
                    capabilities.SetValue("ApplicationIcon", exe + ",0");
                    using (var urls = capabilities.CreateSubKey("URLAssociations"))
                    {
                        urls.SetValue("http", ProgId);
                        urls.SetValue("https", ProgId);
                    }
                }
                using (var command = app.CreateSubKey(@"shell\open\command"))
                    command.SetValue(null, Quote(exe) + " \"%1\"");
                using (var icon = app.CreateSubKey("DefaultIcon"))
                    icon.SetValue(null, exe + ",0");
            }

            using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                registered.SetValue("NanoEdge", AppKey + @"\Capabilities");
        }

        public static bool IsRegistered()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId + @"\shell\open\command"))
                {
                    var value = key == null ? null : key.GetValue(null) as string;
                    return !string.IsNullOrEmpty(value);
                }
            }
            catch { return false; }
        }

        public static void OpenSettings()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
            catch (Exception ex) { Diagnostics.Log("open default apps settings failed: " + ex.Message); }
        }

        private static string Quote(string path) { return "\"" + path + "\""; }
    }
}
