using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NanoEdge
{
    // favicon 只放内存：SPEC 7.8 明确「不为了图标发网络请求」。
    // 图标来源有两条，都是页面自己已经拿到的字节，我们不会再发一次请求：
    //   1) 页面 <link rel=icon> 是 data URI —— 直接解 base64；
    //   2) 图标是普通 URL —— 从 WebResourceResponseReceived 里截获浏览器那次响应。
    // 键按 origin 归一，跨站导航不会沿用上一站的图标。
    public static class Favicons
    {
        private const int MaxEntries = 256;

        private static readonly Dictionary<string, ImageSource> Cache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> Order = new List<string>();

        public static string Key(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return "";
            if (uri.Scheme != "http" && uri.Scheme != "https") return "";
            var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
            return (uri.Scheme + "://" + uri.Host + port).ToLowerInvariant();
        }

        public static ImageSource Get(string url)
        {
            var key = Key(url);
            if (key.Length == 0) return null;
            ImageSource image;
            return Cache.TryGetValue(key, out image) ? image : null;
        }

        public static bool Has(string url) { return Get(url) != null; }

        public static void Put(string url, ImageSource image)
        {
            var key = Key(url);
            if (key.Length == 0 || image == null) return;
            if (!Cache.ContainsKey(key))
            {
                Order.Add(key);
                if (Order.Count > MaxEntries)
                {
                    Cache.Remove(Order[0]);
                    Order.RemoveAt(0);
                }
            }
            Cache[key] = image;
        }

        // data:image/png;base64,... 这类内联图标，零网络请求。
        public static ImageSource DecodeDataUri(string uri)
        {
            if (string.IsNullOrEmpty(uri) ||
                !uri.StartsWith("data:image", StringComparison.OrdinalIgnoreCase)) return null;
            var comma = uri.IndexOf(',');
            if (comma < 0 || uri.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) < 0) return null;
            try
            {
                var bytes = Convert.FromBase64String(uri.Substring(comma + 1));
                if (bytes.Length == 0 || bytes.Length > 256 * 1024) return null;
                using (var stream = new MemoryStream(bytes))
                    return Decode(stream);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("favicon data uri failed: " + ex.Message);
                return null;
            }
        }

        // WebView2 响应流可能有 100MB 级别的资源，图标只需要前面一小段。
        public static byte[] ReadBounded(Stream stream, int maxBytes)
        {
            if (stream == null) return null;
            try
            {
                using (var buffer = new MemoryStream())
                {
                    var chunk = new byte[8192];
                    int read;
                    while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (buffer.Length + read > maxBytes) return null;
                        buffer.Write(chunk, 0, read);
                    }
                    return buffer.ToArray();
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("favicon read failed: " + ex.Message);
                return null;
            }
        }

        // 只收合理尺寸的位图：太小是占位符，太大不是 favicon。
        public static ImageSource Decode(Stream stream)
        {
            if (stream == null) return null;
            try
            {
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                // WebView2 尚未拿到图标时会给 1×1 占位图，直接丢掉让上层重试。
                if (frame.PixelWidth < 8 || frame.PixelHeight < 8) return null;
                if (frame.PixelWidth > 512 || frame.PixelHeight > 512) return null;
                if (frame.CanFreeze) frame.Freeze();
                return frame;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("favicon decode failed: " + ex.Message);
                return null;
            }
        }

        public static void Clear()
        {
            Cache.Clear();
            Order.Clear();
        }
    }
}
