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
            Extract("NanoEdge.Native.WebView2Loader.dll", "WebView2Loader.dll");
            // libsodium 只在密码填充用到；缺了不该拦住浏览器启动，由 KeePassBridge 自行报错。
            Extract("NanoEdge.Native.libsodium.dll", "libsodium.dll");
            if (!SetDllDirectory(AppPaths.Native))
                throw new InvalidOperationException("SetDllDirectory failed");
        }

        // 内容长度一致就跳过重写：libsodium 有 341KB，没必要每次启动都落一遍盘；
        // 也让「上一个进程还没退、DLL 被占用」这种情况不至于直接失败。
        private static void Extract(string resourceName, string fileName)
        {
            var target = Path.Combine(AppPaths.Native, fileName);
            using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (source == null) return;
                var info = new FileInfo(target);
                if (info.Exists && info.Length == source.Length) return;
                try
                {
                    using (var destination = File.Create(target))
                        source.CopyTo(destination);
                }
                catch (IOException)
                {
                    // 文件被占用：如果磁盘上已经有一份同尺寸的，就继续用。
                    info.Refresh();
                    if (!info.Exists || info.Length != source.Length) throw;
                }
            }
        }
    }
}
