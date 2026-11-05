using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Windows.Media;

namespace NanoEdge
{
    public enum LayoutMode { Compact, Standard, Vertical }

    public enum TabMemoryState { Active, Low, Discarded }

    public enum MemoryPreset { Conservative, Balanced, Aggressive }

    public sealed class TabModel
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Url = "";
        public string Title = L.T("新标签页");
        public string GroupId = "";
        public bool IsPinned;
        public bool IsStartPage = true;
        public string InternalPage = "";   // "" | "history" | "bookmarks"，非空时显示纯 WPF 内部页
        public bool IsSecret;
        public bool KeepAlive;
        public double Zoom = 1.0;

        // Persisted (so the panel/status can be reconstructed after a restart).
        public TabMemoryState MemoryState = TabMemoryState.Active;
        public DateTime LastActiveUtc = DateTime.UtcNow;
        public bool Resident;

        // Runtime only — never written to session.json.
        [ScriptIgnore] public DateTime? BackgroundSinceUtc;
        [ScriptIgnore] public TabSnapshot Snapshot;
        [ScriptIgnore] public ImageSource Favicon;   // 仅内存：页面自身图标，跨站导航会换掉
        [ScriptIgnore] public int ActiveDownloads;
        [ScriptIgnore] public bool IsPlayingAudio;
        [ScriptIgnore] public bool HasOpenedDevTools;
        [ScriptIgnore] public string LastRecordedUrl;
        [ScriptIgnore] public DateTime LastRecordedUtc = DateTime.MinValue;
        [ScriptIgnore] public bool MemoryBusy;
        [ScriptIgnore] public string ZoomSite = "";   // 已经套用过记忆缩放的站点，避免每次导航重复写
        [ScriptIgnore] public DateTime LastCrashReloadUtc = DateTime.MinValue;   // 崩溃自动重载的冷却时间戳

        public string DisplayTitle
        {
            get { return IsSecret ? L.T("文档") : Title; }
        }

        public string Host
        {
            get
            {
                try
                {
                    if (string.IsNullOrEmpty(Url)) return "";
                    var uri = new Uri(Url);
                    return uri.IsFile ? L.T("本地文件") : uri.Host;
                }
                catch { return ""; }
            }
        }
    }

    public sealed class TabGroup
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Name = L.T("新建分组");
        public string Color = "#FF4D9D";
        public bool IsCollapsed;
    }

    public sealed class Shortcut
    {
        public string Title = "";
        public string Url = "";
    }

    // SPEC 4.2 三档预设（v1.3 三态模型）。阈值单位毫秒 / MB，int.MaxValue 表示"该档不启用"。
    public sealed class MemorySettings
    {
        public MemoryPreset Preset = MemoryPreset.Balanced;

        public int CoolDelayMs = 2000;            // 失焦/转入后台多久降到 Low
        public int BackgroundDiscardMs = 1800000; // 后台标签多久自动丢弃（30min）

        public int PressureLimitMB = 700;         // 进程组工作集上限
        public int PressureTargetMB = 550;        // 超限后按 LRU 丢到该值以下
        public int PressureMinTabs = 3;           // 至少保留几个有视图的标签
        public int ManyTabsCount = 12;            // 标签数超过该值…
        public int ManyTabsMemoryMB = 500;        // …且内存高于该值时立即丢 2 个

        public bool AutoDiscardEnabled = true;    // 自动丢弃开关
        public bool TrimWorkingSetOnRelease;      // 高级：EmptyWorkingSet，默认关
        public bool LogDiagnostics;

        public List<string> ResidentOrigins = new List<string>(); // 常驻站点（按 host）

        public void ApplyPreset(MemoryPreset preset)
        {
            Preset = preset;
            switch (preset)
            {
                case MemoryPreset.Conservative:
                    CoolDelayMs = 30000;
                    BackgroundDiscardMs = int.MaxValue;
                    PressureLimitMB = 1200;
                    PressureTargetMB = 1000;
                    PressureMinTabs = 3;
                    break;
                case MemoryPreset.Aggressive:
                    CoolDelayMs = 1000;
                    BackgroundDiscardMs = 300000;
                    PressureLimitMB = 450;
                    PressureTargetMB = 350;
                    PressureMinTabs = 2;
                    break;
                default:
                    CoolDelayMs = 2000;
                    BackgroundDiscardMs = 1800000;
                    PressureLimitMB = 700;
                    PressureTargetMB = 550;
                    PressureMinTabs = 3;
                    break;
            }
        }

        public string PresetName
        {
            get
            {
                switch (Preset)
                {
                    case MemoryPreset.Conservative: return L.T("保守");
                    case MemoryPreset.Aggressive: return L.T("激进");
                    default: return L.T("均衡");
                }
            }
        }
    }

    public sealed class AppSettings
    {
        public int SchemaVersion = 1;
        public string Language = "zh-CN";
        public string Theme = "dark";                 // dark | light | system
        public LayoutMode Layout = LayoutMode.Compact;
        public bool VerticalExpanded;
        public bool ShowStatusBar = true;
        // 密封标签是否允许页面通知（默认关：不弹、不留权限记录）。
        public bool SecretNotifications;
        public string SearchEngine = "https://www.bing.com/search?q={q}";
        public List<Shortcut> Shortcuts = new List<Shortcut>();
        public MemorySettings Memory = new MemorySettings();
        public double WindowLeft = double.NaN;
        public double WindowTop = double.NaN;
        public double WindowWidth = 1280;
        public double WindowHeight = 800;
        public bool WindowMaximized;
    }

    public sealed class SessionData
    {
        public List<TabModel> Tabs = new List<TabModel>();
        public List<TabGroup> Groups = new List<TabGroup>();
        public string ActiveTabId = "";
    }
}
