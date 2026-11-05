using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace NanoEdge
{
    public sealed class ProcessRow
    {
        public int Pid;
        public string Kind = "";
        public double WorkingSetMB;
        public double PrivateMB;
        public List<string> TabIds = new List<string>();
        public bool Shared;

        public string TabId { get { return TabIds.Count > 0 ? TabIds[0] : ""; } }
    }

    // 一次进程组采样。口径遵循 SPEC 2.1：工作集是任务管理器显示的数字，
    // 私有提交才是真实占用；面板两列都显示。
    public sealed class MemorySnapshot
    {
        public List<ProcessRow> Rows = new List<ProcessRow>();
        public double GroupWorkingSetMB;
        public double GroupPrivateMB;
        public double HostWorkingSetMB;
        public double HostPrivateMB;
        public DateTime AtUtc = DateTime.UtcNow;

        public double TabWorkingSetMB(string tabId)
        {
            return Math.Round(Rows.Where(r => r.TabIds.Count > 0 && r.TabIds[0] == tabId)
                .Sum(r => r.WorkingSetMB), 1);
        }

        public double TabPrivateMB(string tabId)
        {
            return Math.Round(Rows.Where(r => r.TabIds.Count > 0 && r.TabIds[0] == tabId)
                .Sum(r => r.PrivateMB), 1);
        }

        public bool TabIsShared(string tabId)
        {
            return Rows.Any(r => r.Shared && r.TabIds.Contains(tabId));
        }

        public double KindWorkingSetMB(params string[] kinds)
        {
            return Math.Round(Rows.Where(r => kinds.Contains(r.Kind)).Sum(r => r.WorkingSetMB), 1);
        }

        public double KindPrivateMB(params string[] kinds)
        {
            return Math.Round(Rows.Where(r => kinds.Contains(r.Kind)).Sum(r => r.PrivateMB), 1);
        }

        public int Count(string kind)
        {
            return Rows.Count(r => r.Kind == kind);
        }
    }

    public static class MemoryProbe
    {
        // Phase 0 第 7 项已验证：GetProcessExtendedInfosAsync 可用，FrameId 与
        // CoreWebView2.FrameId 一一对应；同源多标签不共享 renderer。
        public static async Task<MemorySnapshot> SampleAsync(
            CoreWebView2Environment environment, Func<Dictionary<ulong, string>> frameMapFactory)
        {
            var snapshot = new MemorySnapshot();

            Dictionary<ulong, string> frameMap;
            try { frameMap = frameMapFactory() ?? new Dictionary<ulong, string>(); }
            catch { frameMap = new Dictionary<ulong, string>(); }

            if (environment != null)
            {
                IReadOnlyList<CoreWebView2ProcessExtendedInfo> extended = null;
                try { extended = await environment.GetProcessExtendedInfosAsync(); }
                catch (Exception ex) { Diagnostics.Log("probe extended failed: " + ex.Message); }

                if (extended != null)
                {
                    foreach (var item in extended) snapshot.Rows.Add(MakeRow(item, frameMap));
                }
                else
                {
                    // 老运行时兜底：只能拿到 PID + Kind，没有归因。
                    try
                    {
                        foreach (var info in environment.GetProcessInfos())
                            snapshot.Rows.Add(Measure(info.ProcessId, info.Kind.ToString()));
                    }
                    catch (Exception ex) { Diagnostics.Log("probe basic failed: " + ex.Message); }
                }
            }

            int selfPid;
            using (var current = Process.GetCurrentProcess()) selfPid = current.Id;
            var self = Measure(selfPid, "HOST");
            snapshot.HostWorkingSetMB = self.WorkingSetMB;
            snapshot.HostPrivateMB = self.PrivateMB;
            snapshot.Rows.Add(self);

            snapshot.GroupWorkingSetMB = Math.Round(snapshot.Rows.Sum(r => r.WorkingSetMB), 1);
            snapshot.GroupPrivateMB = Math.Round(snapshot.Rows.Sum(r => r.PrivateMB), 1);
            return snapshot;
        }

        private static ProcessRow MakeRow(CoreWebView2ProcessExtendedInfo item, Dictionary<ulong, string> frameMap)
        {
            var row = Measure(item.ProcessInfo.ProcessId, item.ProcessInfo.Kind.ToString());
            var tabIds = new List<string>();

            foreach (var frame in item.AssociatedFrameInfos)
            {
                string tabId;
                if (!frameMap.TryGetValue(frame.FrameId, out tabId)) continue;
                // MainFrame 优先；iframe 只在没有主帧时才作为归因依据。
                if (frame.FrameKind == CoreWebView2FrameKind.MainFrame)
                {
                    tabIds.Clear();
                    tabIds.Add(tabId);
                    break;
                }
                if (!tabIds.Contains(tabId)) tabIds.Add(tabId);
            }

            row.TabIds = tabIds;
            row.Shared = tabIds.Count > 1;
            return row;
        }

        private static ProcessRow Measure(int pid, string kind)
        {
            var row = new ProcessRow { Pid = pid, Kind = kind };
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    process.Refresh();
                    row.WorkingSetMB = Math.Round(process.WorkingSet64 / 1048576.0, 1);
                    row.PrivateMB = Math.Round(process.PrivateMemorySize64 / 1048576.0, 1);
                }
            }
            catch { }
            return row;
        }

        public static string KindLabel(string kind)
        {
            switch (kind)
            {
                case "Gpu": return "GPU";
                case "Browser": return "Browser";
                case "Utility": return "Utility";
                case "Renderer": return "Renderer";
                case "HOST": return L.T("外壳");
                case "SandboxHelper": return "Sandbox";
                case "PpapiPlugin": return "PPAPI";
                default: return kind;
            }
        }
    }
}
