using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NanoEdge
{
    internal static class NativeLoader
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string pathName);

        public static void Prepare()
        {
            Directory.CreateDirectory(AppPaths.Native);
            var target = Path.Combine(AppPaths.Native, "WebView2Loader.dll");
            using (var source = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream("NanoEdge.Native.WebView2Loader.dll"))
            {
                if (source == null)
                    throw new InvalidOperationException("Embedded WebView2Loader.dll was not found");
                using (var destination = File.Create(target))
                    source.CopyTo(destination);
            }
            if (!SetDllDirectory(AppPaths.Native))
                throw new InvalidOperationException("SetDllDirectory failed");
        }
    }
}
