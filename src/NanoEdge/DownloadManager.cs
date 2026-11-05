using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;

namespace NanoEdge
{
    // 下载状态用字符串落盘，不直接序列化 WebView2 的枚举（换 SDK 版本不会读坏旧记录）。
    public static class DownloadStates
    {
        public const string InProgress = "InProgress";
        public const string Completed = "Completed";
        public const string Interrupted = "Interrupted";
        public const string Canceled = "Canceled";

        public static string Label(string state)
        {
            switch (state)
            {
                case InProgress: return L.T("下载中");
                case Completed: return L.T("已完成");
                case Canceled: return L.T("已取消");
                default: return L.T("已中断");
            }
        }

        public static string ReasonLabel(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "";
            switch (reason)
            {
                case "UserCanceled": return L.T("用户取消");
                case "UserShutdown": return L.T("浏览器关闭");
                case "UserPaused": return L.T("已暂停");
                case "NetworkFailed": return L.T("网络中断");
                case "NetworkTimeout": return L.T("网络超时");
                case "FileAccessDenied": return L.T("无法写入文件");
                case "FileNoSpace": return L.T("磁盘空间不足");
                default: return reason;
            }
        }
    }

    public sealed class DownloadItem
    {
        public string Id = "";
        public string Url = "";
        public string Site = "";
        public string FileName = "";
        public string Path = "";
        public long TotalBytes;
        public long ReceivedBytes;
        public string State = DownloadStates.InProgress;
        public string Reason = "";
        public DateTime StartedUtc = DateTime.UtcNow;
        public DateTime FinishedUtc = DateTime.MinValue;

        [ScriptIgnore]
        public int Percent
        {
            get
            {
                if (TotalBytes <= 0) return -1;
                var value = (int)Math.Round(ReceivedBytes * 100.0 / TotalBytes);
                return Math.Max(0, Math.Min(100, value));
            }
        }
    }

    // 下载列表。WebView2 的 DownloadStarting 是唯一入口，操作对象是它给的
    // CoreWebView2DownloadOperation；列表本身是纯数据，nano://downloads 用它渲染。
    public static class DownloadManager
    {
        public static string FilePath = AppPaths.Downloads;
        public static bool Persist = true;
        public static int MaxEntries = 200;

        public static event Action Changed;

        private static readonly List<DownloadItem> Items = new List<DownloadItem>();
        private static readonly Dictionary<string, CoreWebView2DownloadOperation> Live =
            new Dictionary<string, CoreWebView2DownloadOperation>();
        private static bool _loaded;
        private static bool _dirty;
        private static DateTime _lastNotifyUtc = DateTime.MinValue;

        public static IReadOnlyList<DownloadItem> All()
        {
            EnsureLoaded();
            return Items;
        }

        public static int ActiveCount
        {
            get { EnsureLoaded(); return Items.Count(i => i.State == DownloadStates.InProgress); }
        }

        public static DownloadItem Find(string id)
        {
            EnsureLoaded();
            return Items.FirstOrDefault(i => i.Id == id);
        }

        // 由 BrowserHost 在 DownloadStarting 里调用（已设好 e.ResultFilePath / e.Handled）。
        public static DownloadItem Start(CoreWebView2DownloadOperation op, string path, string site)
        {
            EnsureLoaded();
            var item = new DownloadItem
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 10),
                Path = path,
                FileName = SafeName(Path.GetFileName(path)),
                Site = site ?? ""
            };
            try { item.Url = op.Uri ?? ""; } catch { }
            if (string.IsNullOrEmpty(item.Site)) item.Site = Urls.Host(item.Url);
            try { item.TotalBytes = ToLong(op.TotalBytesToReceive); } catch { }
            try { item.ReceivedBytes = op.BytesReceived; } catch { }

            Items.Insert(0, item);
            Live[item.Id] = op;
            Trim();

            try
            {
                op.BytesReceivedChanged += (s, e) =>
                {
                    try
                    {
                        item.ReceivedBytes = op.BytesReceived;
                        item.TotalBytes = ToLong(op.TotalBytesToReceive);
                    }
                    catch { }
                    Notify(false);
                };
                op.StateChanged += (s, e) => Update(op, item);
            }
            catch (Exception ex) { Diagnostics.Log("download hooks failed: " + ex.Message); }

            _dirty = true;
            Flush();
            Notify(true);
            return item;
        }

        private static void Update(CoreWebView2DownloadOperation op, DownloadItem item)
        {
            try
            {
                item.ReceivedBytes = op.BytesReceived;
                item.TotalBytes = ToLong(op.TotalBytesToReceive);
                var state = op.State;
                if (state == CoreWebView2DownloadState.Completed)
                {
                    item.State = DownloadStates.Completed;
                    item.ReceivedBytes = item.TotalBytes > 0 ? item.TotalBytes : item.ReceivedBytes;
                }
                else if (state == CoreWebView2DownloadState.Interrupted)
                {
                    var reason = op.InterruptReason;
                    item.State = reason == CoreWebView2DownloadInterruptReason.UserCanceled
                        ? DownloadStates.Canceled : DownloadStates.Interrupted;
                    try { item.Reason = reason.ToString(); } catch { }
                }
                else
                {
                    item.State = DownloadStates.InProgress;
                }

                if (item.State != DownloadStates.InProgress)
                {
                    item.FinishedUtc = DateTime.UtcNow;
                    Live.Remove(item.Id);
                }
            }
            catch (Exception ex)
            {
                item.State = DownloadStates.Interrupted;
                item.Reason = ex.Message;
                item.FinishedUtc = DateTime.UtcNow;
                Live.Remove(item.Id);
            }
            _dirty = true;
            Flush();
            Notify(true);
        }

        public static bool Cancel(string id)
        {
            CoreWebView2DownloadOperation op;
            if (!Live.TryGetValue(id, out op)) return false;
            try { op.Cancel(); return true; }
            catch (Exception ex)
            {
                Diagnostics.Log("download cancel failed: " + ex.Message);
                var item = Find(id);
                if (item != null)
                {
                    item.State = DownloadStates.Interrupted;
                    item.Reason = ex.Message;
                    item.FinishedUtc = DateTime.UtcNow;
                    Live.Remove(id);
                    _dirty = true;
                    Flush();
                    Notify(true);
                }
                return false;
            }
        }

        public static void Remove(string id)
        {
            EnsureLoaded();
            var item = Find(id);
            if (item == null) return;
            if (Live.ContainsKey(id)) Cancel(id);
            Items.Remove(item);
            _dirty = true;
            Flush();
            Notify(true);
        }

        // 清掉所有已结束的（完成 / 取消 / 中断），进行中的保留。
        public static void ClearEnded()
        {
            EnsureLoaded();
            Items.RemoveAll(i => i.State != DownloadStates.InProgress);
            _dirty = true;
            Flush();
            Notify(true);
        }

        public static bool Open(DownloadItem item)
        {
            if (item == null || item.State != DownloadStates.Completed || !File.Exists(item.Path)) return false;
            try
            {
                Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                return true;
            }
            catch { return false; }
        }

        public static void OpenFolder(DownloadItem item)
        {
            try
            {
                if (item != null && File.Exists(item.Path))
                    Process.Start("explorer.exe", "/select,\"" + item.Path + "\"");
                else
                    Process.Start("explorer.exe", "\"" + AppPaths.DownloadsFolder + "\"");
            }
            catch (Exception ex) { Diagnostics.Log("open folder failed: " + ex.Message); }
        }

        // WebView2 把长度暴露成 ulong?；-1（未知）统一折成 0，UI 显示 "—"。
        private static long ToLong(ulong? value)
        {
            if (!value.HasValue || value.Value > long.MaxValue) return 0;
            return (long)value.Value;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "—";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        }

        // 进度事件很密，页面刷新节流到 200ms；状态切换（完成/中断）立即刷新。
        private static void Notify(bool force)
        {
            var now = DateTime.UtcNow;
            if (!force && (now - _lastNotifyUtc).TotalMilliseconds < 200) return;
            _lastNotifyUtc = now;
            var handler = Changed;
            if (handler != null) handler();
        }

        public static void Flush()
        {
            if (!Persist || !_dirty) return;
            try
            {
                Json.Write(FilePath, Items.Take(MaxEntries).ToList());
                _dirty = false;
            }
            catch (Exception ex) { Diagnostics.Log("downloads save failed: " + ex.Message); }
        }

        public static void Reload()
        {
            _loaded = false;
            _dirty = false;
            _lastNotifyUtc = DateTime.MinValue;
            Items.Clear();
            Live.Clear();
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var saved = Json.Read(FilePath, (List<DownloadItem>)null);
                if (saved == null) return;
                foreach (var item in saved)
                {
                    if (item == null || string.IsNullOrEmpty(item.Id)) continue;
                    // 上次进程退出时没跑完的下载，操作对象已经没了，不能再显示成"下载中"。
                    if (item.State == DownloadStates.InProgress)
                    {
                        item.State = DownloadStates.Interrupted;
                        item.Reason = L.T("上次退出时未完成");
                        item.FinishedUtc = DateTime.UtcNow;
                    }
                    Items.Add(item);
                }
                Trim();
            }
            catch (Exception ex) { Diagnostics.Log("downloads load failed: " + ex.Message); }
        }

        private static void Trim()
        {
            if (Items.Count <= MaxEntries) return;
            Items.RemoveRange(MaxEntries, Items.Count - MaxEntries);
        }

        private static string SafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "download.bin";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => invalid.Contains(c) || c < ' ' ? '_' : c).ToArray();
            var result = new string(chars).Trim();
            return string.IsNullOrEmpty(result) ? "download.bin" : result;
        }
    }
}
