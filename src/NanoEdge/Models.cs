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
                    case MemoryPreset.Conservative: return L.T("少重载");
                    case MemoryPreset.Aggressive: return L.T("省内存");
                    default: return L.T("标准");
                }
            }
        }
    }

    public sealed class AppSettings
    {
        public int SchemaVersion = 3;
        public string Language = "system";           // 外壳界面语言：zh-CN | en-US | system（跟随系统）
        // 网页语言：网站据此决定默认展示哪一版，与上面的界面语言完全独立。
        // 这里写字面量而不是 WebLanguage.System：字段名与静态类同名，字段初始值
        // 里的同名标识符会被解析成这个实例字段（CS0236）。
        public string WebLanguage = "system";
        public string Theme = "dark";               // dark | light | system（主题的跟随系统，和语言是两回事）
        public LayoutMode Layout = LayoutMode.Compact;
        public bool VerticalExpanded;
        public bool ShowStatusBar = false;           // 默认隐藏，设置页可重新打开
        // 密封标签是否允许页面通知（默认关：不弹、不留权限记录）。
        public bool SecretNotifications;
        // 密封标签切换键（默认 Alt+`），格式见 Hotkey.cs；设置页「隐私」可重新录入。
        public string SecretHotkey = Hotkey.Default;
        // 登录表单自动填充：密码始终留在 KeePassXC，NanoEdge 只在填充那一瞬间取明文。
        // 关掉后不再注入扫描脚本，地址栏也不会出现钥匙图标。
        public bool PasswordFill = true;
        public string SearchEngine = SearchEngineOptions.Google;
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

    public sealed class SearchEngineOption
    {
        public readonly string Name;
        public readonly string Template;

        public SearchEngineOption(string name, string template)
        {
            Name = name;
            Template = template;
        }
    }

    // 默认搜索引擎只在这里定义一份：地址栏、起始页下拉、设置页下拉都读它。
    public static class SearchEngineOptions
    {
        public const string Google = "https://www.google.com/search?q={q}";
        public const string Bing = "https://www.bing.com/search?q={q}";
        public const string Baidu = "https://www.baidu.com/s?wd={q}";
        public const string DuckDuckGo = "https://duckduckgo.com/?q={q}";

        // 0.19 之前的默认值，迁移时用来判断"用户有没有手动改过"。
        public const string LegacyBing = Bing;

        public static readonly SearchEngineOption[] All =
        {
            new SearchEngineOption("Google", Google),
            new SearchEngineOption("Bing", Bing),
            new SearchEngineOption("百度", Baidu),
            new SearchEngineOption("DuckDuckGo", DuckDuckGo)
        };

        public static string NameOf(string template)
        {
            foreach (var option in All)
                if (string.Equals(option.Template, template, StringComparison.OrdinalIgnoreCase))
                    return L.T(option.Name);
            return L.T(All[0].Name);
        }

        // 配置里存了不认识的模板（手改过 settings.json）时退回默认，别让搜索框变哑。
        public static string Normalize(string template)
        {
            if (string.IsNullOrWhiteSpace(template)) return Google;
            foreach (var option in All)
                if (string.Equals(option.Template, template, StringComparison.OrdinalIgnoreCase))
                    return option.Template;
            return Google;
        }
    }

    // 网页语言（网站检测后默认展示哪一版）的唯一口径。
    // 它和 L / AppSettings.Language 没有任何关系：界面语言只翻外壳的文案，
    // 网站只看 Accept-Language 请求头和页面里的 navigator.language。
    // system = 不干预 WebView2 原生行为（默认，零开销、零行为变化）；
    // 显式档 = 用 WebResourceRequested 改写请求头 + 文档创建时对齐 navigator.language。
    internal static class WebLanguage
    {
        public const string System = "system";
        public const string Chinese = "zh-CN";
        public const string English = "en-US";

        public static string Normalize(string value)
        {
            if (string.Equals(value, Chinese, StringComparison.OrdinalIgnoreCase)) return Chinese;
            if (string.Equals(value, English, StringComparison.OrdinalIgnoreCase)) return English;
            // 空值、手改坏的值都当作「跟随系统」，而不是把请求头写成乱码。
            return System;
        }

        public static bool IsOverride(string value)
        {
            return !string.Equals(Normalize(value), System, StringComparison.OrdinalIgnoreCase);
        }

        // 返回空串表示不覆盖（跟随系统）。
        public static string AcceptLanguage(string value)
        {
            switch (Normalize(value))
            {
                case Chinese: return "zh-CN,zh;q=0.9,en;q=0.8,en-US;q=0.7";
                // 强制英文时不要再带中文兜底，否则有些站点仍会判成中文用户。
                case English: return "en-US,en;q=0.9";
                default: return "";
            }
        }

        public static string NavigatorLanguage(string value)
        {
            switch (Normalize(value))
            {
                case Chinese: return Chinese;
                case English: return English;
                default: return "";
            }
        }

        public static string NavigatorLanguagesJson(string value)
        {
            switch (Normalize(value))
            {
                case Chinese: return "[\"zh-CN\",\"zh\",\"en-US\",\"en\"]";
                case English: return "[\"en-US\",\"en\"]";
                default: return "[]";
            }
        }

        // 在页面自己的脚本之前执行（AddScriptToExecuteOnDocumentCreated）。
        // navigator.language 是原型上的 getter，在实例上重定义即可；不做任何 DOM
        // 或 Intl 的改写，避免影响站点自己的格式化逻辑。
        public static string Script(string value)
        {
            var language = NavigatorLanguage(value);
            var list = NavigatorLanguagesJson(value);
            if (string.IsNullOrEmpty(language)) return "";
            return "(function(){try{" +
                   "Object.defineProperty(navigator,'language',{configurable:true,get:function(){return '" +
                   language + "';}});" +
                   "var a=" + list + ";" +
                   "Object.defineProperty(navigator,'languages',{configurable:true,get:function(){return a.slice();}});" +
                   "}catch(e){}})();";
        }
    }
}
