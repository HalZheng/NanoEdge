using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NanoEdge
{
    // 与 Chrome 一致的离散缩放阶梯。用阶梯而不是 ±0.1 浮点累加，避免出现
    // 109.9999% 这种档位，也让 Ctrl+0 / 站点记忆的比较可以用等值判断。
    public static class ZoomLevels
    {
        public const double Default = 1.0;

        public static readonly double[] Steps =
        {
            0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9,
            1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0
        };

        public static double Clamp(double value)
        {
            if (double.IsNaN(value) || value <= 0) return Default;
            var best = Steps[0];
            var distance = double.MaxValue;
            foreach (var step in Steps)
            {
                var d = Math.Abs(step - value);
                if (d < distance) { distance = d; best = step; }
            }
            return best;
        }

        public static double Next(double current)
        {
            var value = Clamp(current);
            for (var i = 0; i < Steps.Length; i++)
                if (Steps[i] > value + 0.0001) return Steps[i];
            return Steps[Steps.Length - 1];
        }

        public static double Previous(double current)
        {
            var value = Clamp(current);
            for (var i = Steps.Length - 1; i >= 0; i--)
                if (Steps[i] < value - 0.0001) return Steps[i];
            return Steps[0];
        }

        public static string Label(double value)
        {
            return Math.Round(value * 100).ToString("0") + "%";
        }
    }

    // 按站点（host[:port]）记忆缩放，落 %APPDATA%\NanoEdge\zoom.json。
    // 站点而不是完整 origin：同一站点的 http/https 页面算一家；localhost:3000
    // 与 localhost:8080 算两家（开发场景里这两台通常完全不同的页面）。
    public static class ZoomStore
    {
        public static string FilePath = AppPaths.Zoom;
        public static bool Persist = true;

        private static Dictionary<string, double> _sites = new Dictionary<string, double>();
        private static bool _loaded;
        private static bool _dirty;

        public static string SiteOf(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            if (url.StartsWith("nano://", StringComparison.OrdinalIgnoreCase)) return "";
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return "";
            if (uri.IsFile) return "本地文件";
            if (string.IsNullOrEmpty(uri.Host)) return "";
            var host = uri.Host.ToLowerInvariant();
            return uri.IsDefaultPort ? host : host + ":" + uri.Port;
        }

        // 站点键是持久化数据（zoom.json / scripts 的禁用列表），不能翻译；
        // 只有显示给用户时才过这个函数。
        public static string SiteLabel(string site)
        {
            if (string.IsNullOrEmpty(site)) return "";
            return site == "本地文件" ? L.T("本地文件") : site;
        }

        public static double Get(string site)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(site)) return ZoomLevels.Default;
            double value;
            return _sites.TryGetValue(site, out value) ? ZoomLevels.Clamp(value) : ZoomLevels.Default;
        }

        public static double ForUrl(string url) { return Get(SiteOf(url)); }

        public static void Put(string site, double value)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(site)) return;
            value = ZoomLevels.Clamp(value);
            if (Math.Abs(value - ZoomLevels.Default) < 0.0001)
            {
                if (_sites.Remove(site)) { _dirty = true; Flush(); }
                return;
            }
            double old;
            if (_sites.TryGetValue(site, out old) && Math.Abs(old - value) < 0.0001) return;
            _sites[site] = value;
            _dirty = true;
            Flush();
        }

        public static void Forget(string site)
        {
            EnsureLoaded();
            if (!string.IsNullOrEmpty(site) && _sites.Remove(site)) { _dirty = true; Flush(); }
        }

        public static void SetForUrl(string url, double value) { Put(SiteOf(url), value); }

        public static IReadOnlyDictionary<string, double> All()
        {
            EnsureLoaded();
            return _sites;
        }

        public static void Flush()
        {
            if (!Persist || !_dirty) return;
            try
            {
                Json.Write(FilePath, _sites
                    .Where(p => Math.Abs(p.Value - ZoomLevels.Default) > 0.0001)
                    .ToDictionary(p => p.Key, p => p.Value));
                _dirty = false;
            }
            catch (Exception ex) { Diagnostics.Log("zoom save failed: " + ex.Message); }
        }

        // 自测用：换文件后重新加载；正常启动只会加载一次。
        public static void Reload()
        {
            _loaded = false;
            _dirty = false;
            _sites = new Dictionary<string, double>();
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var map = Json.Read(FilePath, (Dictionary<string, double>)null);
                if (map == null) return;
                foreach (var pair in map)
                    if (!string.IsNullOrEmpty(pair.Key) && pair.Value > 0)
                        _sites[pair.Key] = ZoomLevels.Clamp(pair.Value);
            }
            catch (Exception ex) { Diagnostics.Log("zoom load failed: " + ex.Message); }
        }
    }
}
