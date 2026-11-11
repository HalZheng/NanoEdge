using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace NanoEdge
{
    public static class Program
    {
        // 版本号由 csproj 的 <Version> 生成到程序集，这里只读，不再硬编码。
        public static readonly string Version = ReadVersion();

        private static string ReadVersion()
        {
            var assembly = typeof(Program).Assembly;
            var info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                assembly, typeof(AssemblyInformationalVersionAttribute));
            if (info != null && !string.IsNullOrEmpty(info.InformationalVersion))
                return info.InformationalVersion;
            var version = assembly.GetName().Version;
            return version == null ? "0.0" : version.ToString(3);
        }
        private static string ArgValue(string[] args, string name)
        {
            if (args == null) return null;
            foreach (var arg in args)
                if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return arg.Substring(name.Length + 1).Trim('"');
            return null;
        }

        private static bool HasArg(string[] args, string name)
        {
            if (args == null) return false;
            foreach (var arg in args)
                if (arg == name || arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // 自测/截图进程使用独立的单实例名，避免把正在使用的 NanoEdge 顶掉，
        // 也避免自测参数被已有实例当成 URL 收走。
        private static bool IsSelfTest(string[] args)
        {
            if (args == null) return false;
            foreach (var arg in args)
            {
                if (arg.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase)) return true;
                if (arg.StartsWith("--kpxctest", StringComparison.OrdinalIgnoreCase)) return true;
                switch (arg)
                {
                    case "--fixtest":
                    case "--memtest":
                    case "--navtest":
                    case "--m3ctest":
                    case "--m4test":
                    case "--m5test":
                    case "--m6test":
                    case "--m6btest":
                    case "--m7test":
                    case "--m8test":
                    case "--m9test":
                    case "--m10test":
                    case "--logintest":
                    case "--menupreview":
                    case "--fullscreen":
                    case "--test-mutex":
                        return true;
                }
            }
            return false;
        }

        // 外部唤起时参数就是一个 URL；以 -- 开头的是我们自己的调试开关，跳过。
        private static string FirstUrlArg(string[] args)
        {
            if (args == null) return null;
            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg)) continue;
                if (arg.StartsWith("--open-url=", StringComparison.OrdinalIgnoreCase))
                    return arg.Substring("--open-url=".Length).Trim().Trim('"');
                if (arg.StartsWith("--", StringComparison.Ordinal)) continue;
                return arg.Trim().Trim('"');
            }
            return null;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            AppPaths.Ensure();

            // 语言要在任何 MessageBox 之前定下来。
            var settings = SettingsStore.Load();
            var langArg = ArgValue(args, "--lang");
            if (!string.IsNullOrEmpty(langArg)) settings.Language = langArg;
            L.Apply(settings.Language);

            bool ownsMutex;
            var mutexName = IsSelfTest(args)
                ? "NanoEdge.SingleInstance.SelfTest" : "NanoEdge.SingleInstance";
            using (var mutex = new Mutex(true, mutexName, out ownsMutex))
            {
                if (!ownsMutex)
                {
                    // 已经在运行：把 URL 交给它（默认浏览器点链接走这条路）。
                    var target = FirstUrlArg(args);
                    if (Ipc.TrySend(target ?? "")) return 0;

                    MessageBox.Show(L.T("NanoEdge 已经在运行。"), "NanoEdge",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return 1;
                }

                try
                {
                    NativeLoader.Prepare();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(L.T("无法初始化 WebView2 加载器：\n") + ex.Message,
                        "NanoEdge", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 2;
                }

                // KeePassXC 直连自测：纯后台逻辑，不需要 WebView2，也不需要主窗口。
                if (HasArg(args, "--kpxctest"))
                    return KeePassSelfTest.Run(args);

                // Per SPEC v1.2: no installer, no version gating, no guided flow.
                // Only a short explanation and exit.
                try
                {
                    var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                    if (string.IsNullOrEmpty(version)) throw new InvalidOperationException("runtime not found");
                }
                catch
                {
                    MessageBox.Show(
                        L.T("NanoEdge 需要 Microsoft Edge WebView2 Runtime，当前系统未检测到。\n") +
                        L.T("Windows 11 通常已内置；请安装后重试。\n\n") +
                        "https://developer.microsoft.com/microsoft-edge/webview2/",
                        "NanoEdge", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 3;
                }

                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                    Diagnostics.Log("[CRASH] " + e.ExceptionObject);

                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                Theme.Apply(settings.Theme);

                var window = new MainWindow(settings, args);
                app.MainWindow = window;
                window.Show();
                return app.Run();
            }
        }
    }
}
