using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace NanoEdge
{
    public sealed class UserScript
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Name = "";
        public string Namespace = "";
        public string Version = "";
        public string Description = "";
        public List<string> Matches = new List<string>();
        public List<string> Includes = new List<string>();
        public List<string> Excludes = new List<string>();
        public string RunAt = "document-idle";
        public List<string> Grants = new List<string>();
        public List<string> Connects = new List<string>();
        // @require 地址；正文在导入时下载并固化到 scripts\<id>.require.<n>.js，运行期不再联网。
        public List<string> Requires = new List<string>();
        public bool Enabled = true;
        public DateTime AddedUtc = DateTime.UtcNow;
        public DateTime LastRunUtc = DateTime.MinValue;

        // 代码以 scripts\<id>.user.js 为准，不进索引文件，避免元数据里塞几 KB 正文。
        [ScriptIgnore] public string Code = "";
        [ScriptIgnore] public string FilePath = "";
        [ScriptIgnore] public List<string> RequireCode = new List<string>();

        public string MatchLabel
        {
            get
            {
                var all = Matches.Concat(Includes).ToList();
                if (all.Count == 0) return L.T("（所有站点）");
                return all.Count == 1 ? all[0] : all[0] + L.T(" 等 ") + all.Count + L.T(" 条");
            }
        }

        public string GrantLabel
        {
            get { return Grants.Count == 0 ? L.T("无（仅沙箱）") : string.Join(L.T("、"), Grants); }
        }
    }

    // 批次 B / C5：脚本管理页可编辑的四个元数据字段。
    // null 表示「这次没改这个字段」，回写时必须原样保留源码里对应的 // @ 行。
    public sealed class ScriptMetadataEdit
    {
        public string Name;
        public string Version;
        public List<string> Matches;
        public List<string> Excludes;
    }

    // 元数据解析 + 匹配规则编译。C# 只产出「JS 正则源码字符串」，真正拿 location.href
    // 去匹配的是注入到页面里的引导器，这样规则只有一份实现，不用在 C#/JS 两边各写一遍。
    public static class ScriptParser
    {
        public static readonly string[] KnownGrants =
        {
            "GM_setValue", "GM_getValue", "GM_deleteValue", "GM_listValues", "GM_addStyle",
            "GM_notification", "GM_openInTab", "GM_setClipboard", "GM_xmlhttpRequest",
            "GM_info", "unsafeWindow"
        };

        // Parse 与回写共用同一条规则，保证写进去的值能被原样读回来。
        internal static readonly Regex MetaLine =
            new Regex(@"^\s*//\s*@([\w-]+)\s+(.+?)\s*$", RegexOptions.Multiline);

        public static UserScript Parse(string code)
        {
            var script = new UserScript { Code = code ?? "" };
            foreach (Match m in MetaLine.Matches(script.Code))
            {
                var key = m.Groups[1].Value.ToLowerInvariant();
                var value = m.Groups[2].Value.Trim();
                switch (key)
                {
                    case "name": script.Name = value; break;
                    case "namespace": script.Namespace = value; break;
                    case "version": script.Version = value; break;
                    case "description": script.Description = value; break;
                    case "match": script.Matches.Add(value); break;
                    case "include": script.Includes.Add(value); break;
                    case "exclude": script.Excludes.Add(value); break;
                    case "run-at":
                        if (value == "document-start" || value == "document-end" || value == "document-idle")
                            script.RunAt = value;
                        break;
                    // Tampermonkey 用 @grant none 表示「不申请任何权限」，不能记成一个未知权限。
                    case "grant":
                        if (!value.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                            !script.Grants.Contains(value)) script.Grants.Add(value);
                        break;
                    case "connect": if (!script.Connects.Contains(value)) script.Connects.Add(value); break;
                    case "require":
                        if (!string.IsNullOrWhiteSpace(value) && !script.Requires.Contains(value))
                            script.Requires.Add(value);
                        break;
                }
            }
            if (string.IsNullOrWhiteSpace(script.Name)) script.Name = L.T("未命名脚本");
            if (string.IsNullOrWhiteSpace(script.Namespace)) script.Namespace = "local";
            if (string.IsNullOrWhiteSpace(script.Version)) script.Version = "1.0";
            return script;
        }

        // 把可编辑字段写回 // @ 头（C5）。约束：
        //  1) 只动 name/version/match/exclude；其它 // @ 行（@grant / @run-at / @include /
        //     @require / @namespace / 未知键）连同顺序、缩进、对齐空白全部原样保留；
        //  2) 同一键的多行合并到它第一次出现的位置，一行一个值；原本没有的键补在头块结束标记前；
        //  3) 与 Parse 共用 MetaLine，写回后重新解析必须能还原这四个字段；
        //  4) 值里的换行一律拒绝，避免用名称/版本伪造出新的 // @ 行；
        //  5) 字段为 null 时不参与，用户直接改源码里的 // @ 行不会被结构化字段覆盖。
        // 失败返回 null 并给出原因，调用方必须把原因显示给用户。
        public static string RewriteMetadata(string code, ScriptMetadataEdit edit, out string error)
        {
            error = "";
            if (code == null || edit == null)
            {
                error = L.T("元数据写回失败：内容为空");
                return null;
            }

            var name = NormalizeScalar(edit.Name);
            var version = NormalizeScalar(edit.Version);
            var matches = NormalizeList(edit.Matches);
            var excludes = NormalizeList(edit.Excludes);
            if ((edit.Name != null && name == null) || (edit.Version != null && version == null))
            {
                error = L.T("名称和版本不能为空，也不能包含换行");
                return null;
            }
            if ((edit.Matches != null && matches == null) || (edit.Excludes != null && excludes == null))
            {
                error = L.T("匹配和排除规则不能包含换行");
                return null;
            }

            var wanted = new Dictionary<string, List<string>>();
            if (edit.Name != null) wanted["name"] = new List<string> { name };
            if (edit.Version != null) wanted["version"] = new List<string> { version };
            if (edit.Matches != null) wanted["match"] = matches;
            if (edit.Excludes != null) wanted["exclude"] = excludes;
            if (wanted.Count == 0) return code;   // 只改了源码框：一个字节都不动

            var newline = code.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            var lines = code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var output = new List<string>();
            var written = new HashSet<string>();

            foreach (var line in lines)
            {
                var m = MetaLine.Match(line);
                var key = m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
                if (key == null || !wanted.ContainsKey(key)) { output.Add(line); continue; }
                if (!written.Add(key)) continue;   // 该键其余旧行整体丢弃
                var prefix = line.Substring(0, m.Groups[2].Index);
                foreach (var value in wanted[key]) output.Add(prefix + value);
            }

            // 源码里原本没有的键：按 name/version/match/exclude 顺序一次性补齐。
            var extra = new List<string>();
            foreach (var key in new[] { "name", "version", "match", "exclude" })
            {
                if (written.Contains(key) || !wanted.ContainsKey(key)) continue;
                foreach (var value in wanted[key]) extra.Add(DefaultPrefix(key) + value);
            }
            if (extra.Count > 0) output.InsertRange(InsertionPoint(output), extra);

            var result = string.Join(newline, output.ToArray());
            var check = Parse(result);
            var consistent =
                (!wanted.ContainsKey("name") || check.Name == wanted["name"][0]) &&
                (!wanted.ContainsKey("version") || check.Version == wanted["version"][0]) &&
                (!wanted.ContainsKey("match") || check.Matches.SequenceEqual(wanted["match"])) &&
                (!wanted.ContainsKey("exclude") || check.Excludes.SequenceEqual(wanted["exclude"]));
            if (!consistent)
            {
                error = L.T("写回后重新解析不一致");
                return null;
            }
            return result;
        }

        private static string NormalizeScalar(string value)
        {
            if (value == null) return null;
            var text = value.Trim();
            if (text.Length == 0 || text.IndexOfAny(new[] { '\r', '\n' }) >= 0) return null;
            return text;
        }

        private static List<string> NormalizeList(List<string> values)
        {
            if (values == null) return null;
            var result = new List<string>();
            foreach (var raw in values)
            {
                if (raw == null) continue;
                var text = raw.Trim();
                if (text.Length == 0) continue;
                if (text.IndexOfAny(new[] { '\r', '\n' }) >= 0) return null;
                result.Add(text);
            }
            return result;
        }

        // 新增行沿用项目里 // @ 头的对齐风格。
        private static string DefaultPrefix(string key)
        {
            switch (key)
            {
                case "name": return "// @name         ";
                case "version": return "// @version      ";
                case "match": return "// @match        ";
                default: return "// @exclude      ";
            }
        }

        private static int InsertionPoint(List<string> lines)
        {
            for (var i = 0; i < lines.Count; i++)
                if (Regex.IsMatch(lines[i], @"^\s*//\s*==/UserScript==\s*$")) return i;
            for (var i = lines.Count - 1; i >= 0; i--)
                if (MetaLine.IsMatch(lines[i])) return i + 1;
            for (var i = 0; i < lines.Count; i++)
                if (Regex.IsMatch(lines[i], @"^\s*//\s*==UserScript==\s*$")) return i + 1;
            return 0;
        }

        public static List<string> MatchRegexes(UserScript script)
        {
            return script.Matches.Concat(script.Includes)
                .Select(ToRegexSource).Where(s => !string.IsNullOrEmpty(s)).ToList();
        }

        public static List<string> ExcludeRegexes(UserScript script)
        {
            return script.Excludes.Select(ToRegexSource)
                .Where(s => !string.IsNullOrEmpty(s)).ToList();
        }

        // @include 支持 /regex/ 字面量；其余（含 @match / @exclude）走通配转换。
        public static string ToRegexSource(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return "";
            var text = pattern.Trim();
            if (text.Length > 2 && text[0] == '/' && text[text.Length - 1] == '/')
                return text.Substring(1, text.Length - 2);
            return GlobToRegexSource(text);
        }

        public static string GlobToRegexSource(string pattern)
        {
            var text = pattern.Trim();
            var scheme = "*";
            var rest = text;
            var split = text.IndexOf("://", StringComparison.Ordinal);
            if (split > 0)
            {
                scheme = text.Substring(0, split);
                rest = text.Substring(split + 3);
            }
            else
            {
                // 没写 scheme 的规则按任意 scheme 处理。
                scheme = "*";
            }

            var slash = rest.IndexOf('/');
            var host = slash >= 0 ? rest.Substring(0, slash) : rest;
            var path = slash >= 0 ? rest.Substring(slash) : "/*";

            var schemeSource = scheme == "*" ? "[a-z][a-z0-9+.-]*" : Regex.Escape(scheme);
            if (string.Equals(scheme, "file", StringComparison.OrdinalIgnoreCase))
                return "^file://.*$";

            string hostSource;
            if (host == "*") hostSource = "[^/]*";
            else if (host.StartsWith("*."))
                // 与 Chrome match pattern 一致：*.example.com 同时匹配裸域和任意深度子域。
                hostSource = "(?:[^/]+\\.)?" + EscapeGlob(host.Substring(2));
            else hostSource = EscapeGlob(host);

            var pathSource = EscapeGlob(path);
            return "^" + schemeSource + "://" + hostSource + pathSource + "$";
        }

        // 先转义正则元字符，再把通配 * 放开成 .*。
        private static string EscapeGlob(string text)
        {
            var sb = new StringBuilder();
            foreach (var c in text)
            {
                if (c == '*') sb.Append(".*");
                else if (c == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            return sb.ToString();
        }

        // 导入时展示用的权限清单；未知 @grant 一律当未知权限，宿主侧会拒绝。
        public static string DescribeGrants(UserScript script)
        {
            return script.Grants.Count == 0
                ? L.T("不请求任何 GM 权限（只能访问页面 DOM）")
                : string.Join("\n", script.Grants.Select(g => "· " + g +
                    (KnownGrants.Contains(g) ? "" : L.T("（未支持，将被拒绝）"))));
        }
    }

    // 脚本索引 + 正文文件 + GM 值存储。管理页改动后抛 Changed，BrowserHost 收到后重注入。
    public static class ScriptStore
    {
        public static string IndexPath = AppPaths.ScriptIndex;
        public static string ValuesPath = AppPaths.ScriptValues;
        public static string Folder = AppPaths.Scripts;
        public static bool Persist = true;

        public static event Action Changed;

        private static readonly List<UserScript> Items = new List<UserScript>();
        private static readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        private static readonly List<string> DisabledHosts = new List<string>();
        private static bool _loaded;

        public static IReadOnlyList<UserScript> All()
        {
            EnsureLoaded();
            return Items;
        }

        public static UserScript Find(string id)
        {
            EnsureLoaded();
            return Items.FirstOrDefault(s => s.Id == id);
        }

        public static IReadOnlyList<string> DisabledSites
        {
            get { EnsureLoaded(); return DisabledHosts; }
        }

        public static bool IsSiteDisabled(string url)
        {
            return IsSiteDisabledKey(ZoomStore.SiteOf(url));
        }

        // 站点键（host[:port]）直接版本：管理页拿到的就是键，不该再反解 URL。
        public static bool IsSiteDisabledKey(string site)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(site)) return false;
            return DisabledHosts.Any(h => string.Equals(h, site, StringComparison.OrdinalIgnoreCase));
        }

        public static void ToggleSite(string url)
        {
            ToggleSiteKey(ZoomStore.SiteOf(url));
        }

        public static void ToggleSiteKey(string site)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(site)) return;
            var existed = DisabledHosts.Any(h => string.Equals(h, site, StringComparison.OrdinalIgnoreCase));
            DisabledHosts.RemoveAll(h => string.Equals(h, site, StringComparison.OrdinalIgnoreCase));
            if (!existed) DisabledHosts.Add(site);   // 临时开关，故意不落盘
            NotifyChanged();
        }

        public static void SetEnabled(string id, bool enabled)
        {
            var script = Find(id);
            if (script == null || script.Enabled == enabled) return;
            script.Enabled = enabled;
            Flush();
            NotifyChanged();
        }

        public static void MarkRun(string id)
        {
            var script = Find(id);
            if (script == null) return;
            script.LastRunUtc = DateTime.UtcNow;
            // 运行时间写得很频繁，只更新内存；索引在其它改动/退出时一并落盘。
        }

        public static UserScript Add(string code, bool enabled, List<string> requireCode = null)
        {
            EnsureLoaded();
            var script = ScriptParser.Parse(code);
            script.Enabled = enabled;
            script.RequireCode = requireCode ?? new List<string>();
            Items.Insert(0, script);
            SaveCode(script);
            SaveRequires(script);
            Flush();
            NotifyChanged();
            return script;
        }

        public static void Update(UserScript script, List<string> requireCode = null)
        {
            if (script == null) return;
            var parsed = ScriptParser.Parse(script.Code);
            script.Name = parsed.Name;
            script.Namespace = parsed.Namespace;
            script.Version = parsed.Version;
            script.Description = parsed.Description;
            script.Matches = parsed.Matches;
            script.Includes = parsed.Includes;
            script.Excludes = parsed.Excludes;
            script.RunAt = parsed.RunAt;
            script.Grants = parsed.Grants;
            script.Connects = parsed.Connects;
            script.Requires = parsed.Requires;
            // 依赖正文只由导入/保存流程传入；没有依赖时清空并删除旧文件。
            if (requireCode != null || script.Requires.Count == 0)
                script.RequireCode = requireCode ?? new List<string>();
            SaveCode(script);
            SaveRequires(script);
            Flush();
            NotifyChanged();
        }

        public static void Remove(string id)
        {
            var script = Find(id);
            if (script == null) return;
            Items.Remove(script);
            try { if (File.Exists(script.FilePath)) File.Delete(script.FilePath); } catch { }
            DeleteRequires(script);
            Flush();
            NotifyChanged();
        }

        // ---------- GM_setValue / getValue / deleteValue ----------

        private static string ValueKey(string ns, string key) { return (ns ?? "") + "\u0001" + (key ?? ""); }

        public static object GetValue(string ns, string key, object fallback)
        {
            EnsureLoaded();
            string raw;
            if (!Values.TryGetValue(ValueKey(ns, key), out raw)) return fallback;
            try { return new JavaScriptSerializer().DeserializeObject(raw); }
            catch { return fallback; }
        }

        public static void SetValue(string ns, string key, object value)
        {
            EnsureLoaded();
            Values[ValueKey(ns, key)] = new JavaScriptSerializer().Serialize(value);
            Flush();
        }

        public static void DeleteValue(string ns, string key)
        {
            EnsureLoaded();
            if (Values.Remove(ValueKey(ns, key))) Flush();
        }

        // 引导器里预载的值：Dictionary<key, jsonText>，JSON 文本同时也是合法 JS 对象字面量。
        public static string ValuesAsJson()
        {
            EnsureLoaded();
            return new JavaScriptSerializer().Serialize(Values);
        }

        public static string DisabledSitesAsJson()
        {
            EnsureLoaded();
            return new JavaScriptSerializer().Serialize(DisabledHosts);
        }

        public static void Flush()
        {
            if (!Persist) return;
            try
            {
                Directory.CreateDirectory(Folder);
                Json.Write(IndexPath, Items.ToList());
                Json.Write(ValuesPath, Values);
            }
            catch (Exception ex) { Diagnostics.Log("script save failed: " + ex.Message); }
        }

        public static void Reload()
        {
            _loaded = false;
            Items.Clear();
            Values.Clear();
            DisabledHosts.Clear();
            EnsureLoaded();
        }

        private static void NotifyChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }

        private static void SaveCode(UserScript script)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                script.FilePath = Path.Combine(Folder, script.Id + ".user.js");
                File.WriteAllText(script.FilePath, script.Code ?? "", Encoding.UTF8);
            }
            catch (Exception ex) { Diagnostics.Log("script code save failed: " + ex.Message); }
        }

        // ---------- @require 固化文件 ----------

        private static string RequirePath(UserScript script, int index)
        {
            return Path.Combine(Folder, script.Id + ".require." + index + ".js");
        }

        private static void SaveRequires(UserScript script)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var codes = script.RequireCode ?? new List<string>();
                for (var i = 0; i < codes.Count; i++)
                    File.WriteAllText(RequirePath(script, i), codes[i] ?? "", Encoding.UTF8);
                PruneRequires(script, codes.Count);
            }
            catch (Exception ex) { Diagnostics.Log("script require save failed: " + ex.Message); }
        }

        private static void PruneRequires(UserScript script, int keep)
        {
            try
            {
                var prefix = script.Id + ".require.";
                foreach (var file in Directory.GetFiles(Folder, prefix + "*.js"))
                {
                    var name = Path.GetFileName(file);
                    if (name.Length <= prefix.Length) continue;
                    var number = name.Substring(prefix.Length);
                    var dot = number.LastIndexOf('.');
                    if (dot >= 0) number = number.Substring(0, dot);
                    int index;
                    if (int.TryParse(number, out index) && index >= keep) File.Delete(file);
                }
            }
            catch { }
        }

        private static void LoadRequires(UserScript script)
        {
            var codes = new List<string>();
            for (var i = 0; i < script.Requires.Count; i++)
            {
                try
                {
                    var path = RequirePath(script, i);
                    if (!File.Exists(path))
                    {
                        Diagnostics.Log("script require missing: " + path);
                        codes.Add(null);   // 保持位置对应，注入时跳过
                        continue;
                    }
                    codes.Add(File.ReadAllText(path, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("script require load failed: " + ex.Message);
                    codes.Add(null);
                }
            }
            script.RequireCode = codes;
        }

        private static void DeleteRequires(UserScript script)
        {
            try
            {
                var prefix = script.Id + ".require.";
                foreach (var file in Directory.GetFiles(Folder, prefix + "*.js"))
                    File.Delete(file);
            }
            catch { }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var saved = Json.Read(IndexPath, (List<UserScript>)null);
                if (saved != null)
                {
                    foreach (var item in saved)
                    {
                        if (item == null || string.IsNullOrEmpty(item.Id)) continue;
                        item.FilePath = Path.Combine(Folder, item.Id + ".user.js");
                        try
                        {
                            if (File.Exists(item.FilePath))
                                item.Code = File.ReadAllText(item.FilePath, Encoding.UTF8);
                        }
                        catch { }
                        LoadRequires(item);
                        Items.Add(item);
                    }
                }
                var values = Json.Read(ValuesPath, (Dictionary<string, string>)null);
                if (values != null)
                    foreach (var pair in values) Values[pair.Key] = pair.Value;
            }
            catch (Exception ex) { Diagnostics.Log("scripts load failed: " + ex.Message); }
        }
    }

    // @require：只在导入/保存时下载一次并固化到 scripts 目录，运行期不再联网。
    // 与 GM_xmlhttpRequest 不同：不带 Cookie、不走 @connect（地址写在脚本头部，导入时由用户确认）。
    public sealed class ScriptRequireResult
    {
        public bool Ok = true;
        public List<string> Codes = new List<string>();
        public List<string> Failed = new List<string>();
    }

    public static class ScriptRequires
    {
        public const int MaxCount = 10;
        public const int MaxBytes = 5 * 1024 * 1024;   // 单个依赖上限 5MB

        public static async System.Threading.Tasks.Task<ScriptRequireResult> DownloadAsync(
            IList<string> urls)
        {
            var result = new ScriptRequireResult();
            if (urls == null || urls.Count == 0) return result;
            if (urls.Count > MaxCount)
            {
                result.Ok = false;
                result.Failed.Add(L.T("依赖超过 10 个上限"));
                return result;
            }

            foreach (var raw in urls)
            {
                var url = (raw ?? "").Trim();
                if (!IsHttp(url))
                {
                    result.Ok = false;
                    result.Failed.Add(url + L.T("：只支持 http(s) 地址"));
                    continue;
                }
                try { result.Codes.Add(await FetchAsync(url)); }
                catch (Exception ex)
                {
                    result.Ok = false;
                    result.Failed.Add(url + "：" + ex.Message);
                }
            }

            // 任何一个失败都不固化，避免半套依赖让脚本以「看似正常」的状态运行。
            if (!result.Ok) result.Codes.Clear();
            return result;
        }

        private static bool IsHttp(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }

        private static async System.Threading.Tasks.Task<string> FetchAsync(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.AllowAutoRedirect = true;
            request.UserAgent = "NanoEdge/0.1 (@require)";

            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (var stream = response.GetResponseStream())
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException("HTTP " + (int)response.StatusCode);

                var encoding = Encoding.UTF8;
                try { if (!string.IsNullOrEmpty(response.CharacterSet)) encoding = Encoding.GetEncoding(response.CharacterSet); }
                catch { }

                var buffer = new byte[8192];
                using (var memory = new MemoryStream())
                {
                    while (true)
                    {
                        var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (read <= 0) break;
                        if (memory.Length + read > MaxBytes)
                            throw new InvalidOperationException(L.T("依赖文件超过 5MB 上限"));
                        memory.Write(buffer, 0, read);
                    }
                    return encoding.GetString(memory.ToArray());
                }
            }
        }
    }

    // GM_xmlhttpRequest 的宿主代发。带当前 WebView2 profile 的 Cookie，且必须过 @connect 白名单。
    public static class ScriptHttp
    {
        public static bool IsAllowed(UserScript script, string url)
        {
            if (script == null || script.Connects.Count == 0) return false;
            string host;
            try { host = new Uri(url).Host.ToLowerInvariant(); }
            catch { return false; }
            return script.Connects.Any(c =>
                c == "*" || string.Equals(c, host, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c, "*." + host, StringComparison.OrdinalIgnoreCase) ||
                (c.StartsWith("*.") && host.EndsWith(c.Substring(1), StringComparison.OrdinalIgnoreCase)));
        }

        public static async System.Threading.Tasks.Task<ScriptHttpResult> SendAsync(
            Microsoft.Web.WebView2.Core.CoreWebView2 core, string url, string method,
            Dictionary<string, string> headers, string body, int timeoutMs)
        {
            var result = new ScriptHttpResult();
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant();
                request.Timeout = Math.Max(1000, Math.Min(60000, timeoutMs));
                request.AllowAutoRedirect = true;
                request.UserAgent = "NanoEdge/0.1 (GM_xmlhttpRequest)";

                if (headers != null)
                    foreach (var pair in headers)
                    {
                        var key = pair.Key.ToLowerInvariant();
                        if (key == "user-agent") request.UserAgent = pair.Value;
                        else if (key == "content-type") request.ContentType = pair.Value;
                        else if (key == "accept") request.Accept = pair.Value;
                        else if (key == "referer") request.Referer = pair.Value;
                        else if (key != "cookie" && key != "content-length" && key != "host")
                            request.Headers[pair.Key] = pair.Value;
                    }

                // 用 WebView2 的 CookieManager 取当前会话 Cookie，代发请求才像"在页面里发的"。
                // Cookie 走 CookieContainer 而不是手写 Header：HttpWebRequest 把 Cookie 当受限头。
                if (core != null && core.CookieManager != null)
                {
                    try
                    {
                        var cookies = await core.CookieManager.GetCookiesAsync(url);
                        if (cookies != null && cookies.Count > 0)
                        {
                            var host = new Uri(url).Host;
                            var jar = new CookieContainer();
                            foreach (var cookie in cookies)
                            {
                                try
                                {
                                    var domain = string.IsNullOrEmpty(cookie.Domain) ? host : cookie.Domain;
                                    var path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path;
                                    jar.Add(new Cookie(cookie.Name, cookie.Value, path, domain));
                                }
                                catch { }
                            }
                            request.CookieContainer = jar;
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(body) && request.Method != "GET" && request.Method != "HEAD")
                {
                    var bytes = Encoding.UTF8.GetBytes(body);
                    request.ContentLength = bytes.Length;
                    using (var stream = await request.GetRequestStreamAsync())
                    {
                        await stream.WriteAsync(bytes, 0, bytes.Length);
                    }
                }

                using (var response = (HttpWebResponse)await request.GetResponseAsync())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    result.Ok = true;
                    result.Status = (int)response.StatusCode;
                    result.StatusText = response.StatusDescription;
                    result.Text = await reader.ReadToEndAsync();
                    result.Headers = response.Headers.AllKeys
                        .ToDictionary(k => k, k => response.Headers[k]);
                }
            }
            catch (WebException ex)
            {
                result.Error = ex.Message;
                var response = ex.Response as HttpWebResponse;
                if (response != null) result.Status = (int)response.StatusCode;
            }
            catch (Exception ex) { result.Error = ex.Message; }
            return result;
        }
    }

    public sealed class ScriptHttpResult
    {
        public bool Ok;
        public int Status;
        public string StatusText = "";
        public string Text = "";
        public string Error = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
    }
}
