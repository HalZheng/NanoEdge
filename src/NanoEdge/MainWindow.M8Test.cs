using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace NanoEdge
{
    public sealed partial class MainWindow
    {
        // --m8test：0.19 用户脚本能力补齐验收。
        // 覆盖 @require 导入固化/注入顺序、GM_listValues、GM_setClipboard、unsafeWindow、
        // 依赖下载失败不落库、没有 @grant 时宿主拒绝伪造消息、删除脚本清理依赖文件。
        // 全部跑在真实 WebView2 与本机 127.0.0.1 服务上，不 mock 注入链路。
        private sealed class M8SelfTest
        {
            private readonly MainWindow _w;
            private readonly StringBuilder _report = new StringBuilder();
            private readonly List<string> _failures = new List<string>();
            private int _passes;
            private string _root;
            private string _temp;

            private TcpListener _server;
            private volatile bool _serverRunning;
            private int _port;
            private string _base;
            private readonly List<string> _requests = new List<string>();

            private bool _appIndexExisted;
            private bool _appValuesExisted;
            private bool _clipboardHadText;
            private string _clipboardBackup;

            private const string ClipText = "nanoedge-m8-clip";

            public M8SelfTest(MainWindow window) { _w = window; }

            public async Task RunAsync()
            {
                _root = FindRoot();
                Line("M8 userscript self-test (0.19 @require / GM_listValues / GM_setClipboard / unsafeWindow) " +
                     DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Line("test data dir = " + Path.Combine(Path.GetTempPath(), "nanoedge-m8"));
                try
                {
                    Setup();
                    await RunCoreAsync();
                }
                catch (Exception ex) { Fail("unhandled", ex.ToString()); }
                finally
                {
                    StopServer();
                    RestoreClipboard();
                    Line("");
                    Line(_failures.Count == 0
                        ? "RESULT: PASS (" + _passes + " checks)"
                        : "RESULT: FAIL " + string.Join(", ", _failures));
                    WriteReport();
                    try { Application.Current.Shutdown(); } catch { }
                }
            }

            private void Setup()
            {
                _temp = Path.Combine(Path.GetTempPath(), "nanoedge-m8");
                if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
                Directory.CreateDirectory(_temp);

                _appIndexExisted = File.Exists(AppPaths.ScriptIndex);
                _appValuesExisted = File.Exists(AppPaths.ScriptValues);

                ScriptStore.IndexPath = Path.Combine(_temp, "scripts.json");
                ScriptStore.ValuesPath = Path.Combine(_temp, "script-values.json");
                ScriptStore.Folder = Path.Combine(_temp, "scripts");
                Directory.CreateDirectory(ScriptStore.Folder);
                ScriptStore.Persist = true;
                ScriptStore.Reload();

                try
                {
                    if (Clipboard.ContainsText())
                    {
                        _clipboardBackup = Clipboard.GetText();
                        _clipboardHadText = true;
                    }
                }
                catch { }

                StartServer();
                _base = "http://127.0.0.1:" + _port;
            }

            // ---------- 自测主体 ----------

            private async Task RunCoreAsync()
            {
                // ---------- 1. 解析与注入字符串 ----------
                var importCode = @"// ==UserScript==
// @name         M8 require + gm
// @namespace    m8
// @version      1.0
// @match        " + _base + @"/page*
// @run-at       document-start
// @grant        GM_setValue
// @grant        GM_getValue
// @grant        GM_listValues
// @grant        GM_setClipboard
// @grant        GM_info
// @require      " + _base + @"/lib1.js
// @require      " + _base + @"/lib2.js
// ==/UserScript==
window.__m8 = window.__m8 || {};
__m8.lib1 = window.__m8Lib1 || '';
__m8.lib2 = window.__m8Lib2 || '';
__m8.requireOrder = (window.__m8RequireOrder || []).join(',');
__m8.requireBeforeUser = !!window.__m8RequireRanBeforeUser;
__m8.helper = (typeof m8Helper === 'function') ? m8Helper() : '';
__m8.libVar = typeof __m8LibVar;
__m8.unsafeEq = (unsafeWindow === window);
__m8.unsafeType = typeof unsafeWindow;
__m8.listType = typeof GM_listValues;
__m8.clipType = typeof GM_setClipboard;
GM_setValue('m8a', 1);
GM_setValue('m8b', 'two');
__m8.keys = GM_listValues().sort().join(',');
__m8.valueB = GM_getValue('m8b', null);
GM_setClipboard('" + ClipText + @"');
document.addEventListener('DOMContentLoaded', function () {
  if (!document.body) return;
  var d = document.createElement('div');
  d.id = 'm8-banner';
  d.textContent = 'require: ' + ((window.__m8RequireOrder || []).join(' -> ')) +
                  ' · unsafeWindow=' + (unsafeWindow === window);
  d.style.cssText = 'margin:0 0 12px 0;padding:8px 12px;background:#FF4D9D;color:#111;' +
                    'font:13px sans-serif;border-radius:4px';
  document.body.insertBefore(d, document.body.firstChild);
});";

                var parsed = ScriptParser.Parse(importCode);
                Check("parse two @require lines in order",
                    parsed.Requires.Count == 2 &&
                    parsed.Requires[0] == _base + "/lib1.js" &&
                    parsed.Requires[1] == _base + "/lib2.js",
                    string.Join(" | ", parsed.Requires));
                var dup = ScriptParser.Parse("// @name D\n// @require " + _base + "/lib1.js\n" +
                                             "// @require " + _base + "/lib1.js");
                Check("duplicate @require is de-duped", dup.Requires.Count == 1,
                    "count=" + dup.Requires.Count);
                Check("new grants are listed as known",
                    ScriptParser.KnownGrants.Contains("GM_listValues") &&
                    ScriptParser.KnownGrants.Contains("GM_setClipboard") &&
                    ScriptParser.KnownGrants.Contains("unsafeWindow"),
                    string.Join(",", ScriptParser.KnownGrants));

                var wrapperProbe = ScriptParser.Parse(
                    "// @name W\n// @match https://x/*\nwindow.__m8UserCode = 1;");
                wrapperProbe.RequireCode = new List<string> { "window.__m8Lib1='lib1';" };
                var wrapped = ScriptEngine.Wrap(wrapperProbe);
                Check("wrapper embeds @require code before the main script",
                    wrapped.IndexOf("window.__m8Lib1", StringComparison.Ordinal) >= 0 &&
                    wrapped.IndexOf("window.__m8Lib1", StringComparison.Ordinal) <
                    wrapped.IndexOf("window.__m8UserCode", StringComparison.Ordinal),
                    "order checked");
                var bootstrap = ScriptEngine.Bootstrap();
                Check("bootstrap exposes listValues and setClipboard",
                    bootstrap.Contains("listValues") && bootstrap.Contains("setClipboard"),
                    "bootstrap");

                // 另一个命名空间的值：GM_listValues 只能看到自己的键。
                ScriptStore.SetValue("other", "m8c", 1);

                // ---------- 2. 导入卡片 + 真实下载 ----------
                var notify = new List<string>();
                var scriptsPage = new ScriptsPage(m => { lock (notify) notify.Add(m); }, () => "");
                var before = ScriptStore.All().Count;
                scriptsPage.StageImport(importCode, "m8.user.js");
                Check("import waits for confirmation before writing",
                    scriptsPage.HasPendingImport && ScriptStore.All().Count == before,
                    "count=" + ScriptStore.All().Count);
                Check("import summary lists the dependencies",
                    (scriptsPage.LastImportSummary ?? "").Contains("依赖：2"),
                    (scriptsPage.LastImportSummary ?? "").Replace("\n", " / "));
                RenderPage(scriptsPage, Path.Combine(_root, "out", "m8-import.png"), 980, 640);
                Check("import confirmation rendered with dependencies",
                    File.Exists(Path.Combine(_root, "out", "m8-import.png")), "out/m8-import.png");

                var importedOk = await scriptsPage.ConfirmImportAsync();
                Check("confirmed import downloads and writes the script", importedOk,
                    "ok=" + importedOk);
                var importedItem = ScriptStore.All().FirstOrDefault(s => s.Name == "M8 require + gm");
                var imported = importedItem == null ? null : ScriptStore.Find(importedItem.Id);
                Check("script is in the store", imported != null, "count=" + ScriptStore.All().Count);
                Check("both @require files were fetched",
                    CountRequests("/lib1.js") == 1 && CountRequests("/lib2.js") == 1,
                    string.Join(" | ", SnapshotRequests()));
                Check("require bodies are cached in memory",
                    imported != null && imported.RequireCode.Count == 2 &&
                    imported.RequireCode.All(c => !string.IsNullOrEmpty(c)),
                    imported == null ? "no script" : "codes=" + imported.RequireCode.Count);
                Check("require files are stored next to the script",
                    imported != null && RequireFiles(imported).Length == 2,
                    imported == null ? "no script" : string.Join(",", RequireFiles(imported)));

                // C5：只改元数据、没动 @require 时不能重新联网下载，已固化的本地正文继续用。
                // 故意不改名，后面第 3 节还要按原名字查重载后的脚本。
                scriptsPage.BeginEdit(imported);
                scriptsPage.SetMetadataForTest(null, "2.0", null, null);
                scriptsPage.SaveEditorForTest();
                Check("C5 metadata-only save does not re-download @require",
                    CountRequests("/lib1.js") == 1 && CountRequests("/lib2.js") == 1,
                    string.Join(" | ", SnapshotRequests()));
                Check("C5 @require bodies survive a metadata-only save",
                    imported.RequireCode.Count == 2 &&
                    imported.RequireCode.All(c => !string.IsNullOrEmpty(c)) &&
                    RequireFiles(imported).Length == 2,
                    "codes=" + imported.RequireCode.Count);
                Check("C5 version is written back into the // @ header",
                    imported.Version == "2.0" && (imported.Code ?? "").Contains("2.0"),
                    "version=" + imported.Version);

                // ---------- 3. 重启（重载索引）后依赖仍在 ----------
                ScriptStore.Reload();
                var reloaded = ScriptStore.All().FirstOrDefault(s => s.Name == "M8 require + gm");
                Check("reload keeps the @require URLs",
                    reloaded != null && reloaded.Requires.Count == 2,
                    reloaded == null ? "no script" : "count=" + reloaded.Requires.Count);
                Check("reload keeps the cached dependency code",
                    reloaded != null && reloaded.RequireCode.Count == 2 &&
                    reloaded.RequireCode.Any(c => c != null && c.Contains("__m8Lib1")),
                    reloaded == null ? "no script" : "codes=" + reloaded.RequireCode.Count);

                // ---------- 4. 真实页面：依赖先跑、unsafeWindow、GM 新 API ----------
                var page = await OpenPageAsync(_base + "/page?v=1");
                var state = await StateAsync(page);
                Check("@require lib1 executed", S(state, "lib1") == "lib1", Dump(state));
                Check("@require lib2 executed", S(state, "lib2") == "lib2", Dump(state));
                Check("@require order follows the metadata", S(state, "requireOrder") == "lib1,lib2",
                    S(state, "requireOrder"));
                Check("@require runs before the user script", B(state, "requireBeforeUser"),
                    "before=" + S(state, "requireBeforeUser"));
                Check("functions from a required lib are visible",
                    S(state, "helper") == "helper-from-lib", S(state, "helper"));
                Check("var declarations from a required lib share the scope",
                    S(state, "libVar") == "string", S(state, "libVar"));
                Check("unsafeWindow is the page window",
                    B(state, "unsafeEq") && S(state, "unsafeType") == "object",
                    "eq=" + S(state, "unsafeEq") + " type=" + S(state, "unsafeType"));
                Check("GM_listValues and GM_setClipboard are functions",
                    S(state, "listType") == "function" && S(state, "clipType") == "function",
                    "list=" + S(state, "listType") + " clip=" + S(state, "clipType"));

                var valuesOk = await WaitForAsync(
                    () => Equals(ScriptStore.GetValue("m8", "m8a", null), 1), 6000);
                Check("GM_setValue still reaches the host store", valuesOk,
                    "m8a=" + (ScriptStore.GetValue("m8", "m8a", null) ?? "null"));
                Check("GM_getValue returns the local write",
                    S(state, "valueB") == "two", S(state, "valueB"));
                Check("GM_listValues lists only this script's keys",
                    S(state, "keys") == "m8a,m8b", S(state, "keys"));
                Check("GM_listValues ignores other namespaces",
                    !(S(state, "keys") ?? "").Contains("m8c"), S(state, "keys"));

                var clipped = await WaitForAsync(() => ClipboardText() == ClipText, 6000);
                Check("GM_setClipboard reaches the real clipboard", clipped,
                    "clipboard=" + ClipboardText());

                _w.SelectTab(page);
                await Task.Delay(500);
                await CapturePreviewAsync(page, Path.Combine(_root, "out", "m8-injected.png"));
                Check("injected page captured with the require banner",
                    new FileInfo(Path.Combine(_root, "out", "m8-injected.png")).Length > 1024,
                    "out/m8-injected.png");

                // ---------- 5. 没有 @grant：API 不存在，伪造消息也被宿主拒绝 ----------
                var denied = ScriptStore.Add(ScriptText("M8 denied", _base + "/deny*",
                    new[] { "window.__m8 = window.__m8 || {};",
                            "__m8.forged = true;",
                            "__m8.listType = typeof GM_listValues;",
                            "__m8.clipType = typeof GM_setClipboard;",
                            "__m8.unsafeType = typeof unsafeWindow;",
                            "var m = /[?&]sid=([^&]+)/.exec(location.search || '');",
                            "window.chrome.webview.postMessage({__ne:'clipboard', id: m ? m[1] : 'missing', text:'forged-clip'});" }),
                    true);
                SetClipboard("before-forgery");
                var deniedPage = await OpenPageAsync(_base + "/deny?sid=" + denied.Id);
                var deniedState = await StateAsync(deniedPage);
                Check("undeclared GM_listValues is undefined",
                    S(deniedState, "listType") == "undefined", S(deniedState, "listType"));
                Check("undeclared GM_setClipboard is undefined",
                    S(deniedState, "clipType") == "undefined", S(deniedState, "clipType"));
                Check("unsafeWindow needs no @grant",
                    S(deniedState, "unsafeType") == "object", S(deniedState, "unsafeType"));
                Check("page managed to post a forged clipboard message",
                    B(deniedState, "forged"), S(deniedState, "forged"));
                await Task.Delay(800);
                Check("host rejects the forged clipboard write",
                    ClipboardText() == "before-forgery", "clipboard=" + ClipboardText());

                // ---------- 6. 依赖下载失败：不写库、保留待导入状态 ----------
                var failCode = @"// ==UserScript==
// @name         M8 bad require
// @namespace    m8
// @version      1.0
// @match        " + _base + @"/page*
// @require      " + _base + @"/missing.js
// ==/UserScript==
window.__m8Bad = true;";
                var countBeforeFail = ScriptStore.All().Count;
                scriptsPage.StageImport(failCode, "bad.user.js");
                Check("failed import keeps the dependency count visible",
                    (scriptsPage.LastImportSummary ?? "").Contains("依赖：1"),
                    (scriptsPage.LastImportSummary ?? "").Replace("\n", " / "));
                var failOk = await scriptsPage.ConfirmImportAsync();
                Check("failed dependency download rejects the import", !failOk, "ok=" + failOk);
                Check("failed import never writes the script",
                    ScriptStore.All().Count == countBeforeFail,
                    "count=" + ScriptStore.All().Count);
                Check("failed import keeps the pending card for a retry",
                    scriptsPage.HasPendingImport, "pending=" + scriptsPage.HasPendingImport);
                Check("failure was surfaced to the user",
                    notify.Any(m => m != null && m.Contains("依赖下载失败")),
                    string.Join(" / ", notify));
                Check("failed dependency is never cached",
                    ScriptStore.All().All(s => !s.Requires.Any(r => r.Contains("missing.js"))),
                    "scripts=" + ScriptStore.All().Count);

                // ---------- 7. 删除脚本同时清理依赖文件 ----------
                var requireFiles = RequireFiles(imported);
                scriptsPage.RemoveScript(imported);
                Check("delete removes the script's require files",
                    requireFiles.Length == 2 && requireFiles.All(f => !File.Exists(f)),
                    "files=" + requireFiles.Length);
                Check("delete keeps the other script",
                    ScriptStore.All().Any(s => s.Name == "M8 denied"),
                    "count=" + ScriptStore.All().Count);

                // ---------- 8. 数据隔离 ----------
                Check("scripts.json written to the temp store", File.Exists(ScriptStore.IndexPath),
                    ScriptStore.IndexPath);
                Check("no scripts.json created under %APPDATA%",
                    File.Exists(AppPaths.ScriptIndex) == _appIndexExisted, AppPaths.ScriptIndex);
                Check("no script-values.json created under %APPDATA%",
                    File.Exists(AppPaths.ScriptValues) == _appValuesExisted, AppPaths.ScriptValues);

                Line("");
                Line("store   = " + ScriptStore.IndexPath);
                Line("values  = " + ScriptStore.ValuesPath);
                Line("folder  = " + ScriptStore.Folder);
                Line("requests= " + string.Join(" | ", SnapshotRequests()));
            }

            // ---------- 脚本构造 ----------

            private static string ScriptText(string name, string match, string[] bodyLines)
            {
                var sb = new StringBuilder();
                sb.AppendLine("// ==UserScript==");
                sb.AppendLine("// @name         " + name);
                sb.AppendLine("// @namespace    m8");
                sb.AppendLine("// @version      1.0");
                sb.AppendLine("// @match        " + match);
                sb.AppendLine("// @run-at       document-start");
                sb.AppendLine("// ==/UserScript==");
                foreach (var line in bodyLines) sb.AppendLine(line);
                return sb.ToString();
            }

            private string[] RequireFiles(UserScript script)
            {
                if (script == null) return new string[0];
                return Directory.GetFiles(ScriptStore.Folder, script.Id + ".require.*.js");
            }

            private int CountRequests(string suffix)
            {
                lock (_requests) return _requests.Count(p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            }

            private string[] SnapshotRequests()
            {
                lock (_requests) return _requests.ToArray();
            }

            // ---------- 剪贴板 ----------

            private static string ClipboardText()
            {
                try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
                catch { return ""; }
            }

            private static void SetClipboard(string text)
            {
                try { Clipboard.SetText(text); } catch { }
            }

            private void RestoreClipboard()
            {
                try
                {
                    if (_clipboardHadText) Clipboard.SetText(_clipboardBackup);
                    else if (ClipboardText() == ClipText || ClipboardText() == "before-forgery")
                        Clipboard.Clear();
                }
                catch { }
            }

            // ---------- WebView2 读写 ----------

            private async Task<TabModel> OpenPageAsync(string url)
            {
                _w.NewTab(url);
                var tab = _w._active;
                var ready = await WaitReadyAsync(tab, url, 20000);
                if (!ready) Fail("open " + url, "page did not reach readyState=complete");
                return tab;
            }

            private async Task<bool> WaitReadyAsync(TabModel tab, string url, int timeoutMs)
            {
                var started = Environment.TickCount;
                while (Environment.TickCount - started < timeoutMs)
                {
                    var core = _w._browser.Core(tab);
                    if (core != null)
                    {
                        try
                        {
                            var raw = await core.ExecuteScriptAsync("location.href");
                            var href = new JavaScriptSerializer().Deserialize<string>(raw);
                            if (href != null && href.StartsWith(url, StringComparison.OrdinalIgnoreCase))
                            {
                                var stateRaw = await core.ExecuteScriptAsync("document.readyState");
                                if (new JavaScriptSerializer().Deserialize<string>(stateRaw) == "complete")
                                    return true;
                            }
                        }
                        catch { }
                    }
                    await Task.Delay(100);
                }
                return false;
            }

            private async Task<Dictionary<string, object>> StateAsync(TabModel tab)
            {
                var core = _w._browser.Core(tab);
                if (core == null) return new Dictionary<string, object>();
                try
                {
                    var raw = await core.ExecuteScriptAsync("JSON.stringify(window.__m8 || {})");
                    var json = new JavaScriptSerializer().Deserialize<string>(raw);
                    return new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>
                           ?? new Dictionary<string, object>();
                }
                catch { return new Dictionary<string, object>(); }
            }

            private async Task CapturePreviewAsync(TabModel tab, string path)
            {
                var core = _w._browser.Core(tab);
                if (core == null) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var stream = File.Create(path))
                        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                }
                catch (Exception ex) { Line("[capture] " + ex.Message); }
            }

            private static bool B(Dictionary<string, object> state, string key)
            {
                object value;
                if (!state.TryGetValue(key, out value) || value == null) return false;
                try { return Convert.ToBoolean(value); } catch { return false; }
            }

            private static string S(Dictionary<string, object> state, string key)
            {
                object value;
                if (!state.TryGetValue(key, out value) || value == null) return null;
                return Convert.ToString(value);
            }

            private static string Dump(Dictionary<string, object> state)
            {
                if (state == null || state.Count == 0) return "{}";
                return "{" + string.Join(", ", state.Select(p => p.Key + "=" +
                    (p.Value == null ? "null" : p.Value.ToString()))) + "}";
            }

            private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
            {
                var started = Environment.TickCount;
                while (Environment.TickCount - started < timeoutMs)
                {
                    if (condition()) return true;
                    await Task.Delay(100);
                }
                return condition();
            }

            // ---------- 本机 HTTP 服务 ----------

            private void StartServer()
            {
                _server = new TcpListener(IPAddress.Loopback, 0);
                _server.Start();
                _port = ((IPEndPoint)_server.LocalEndpoint).Port;
                _serverRunning = true;
                Task.Run(async () =>
                {
                    while (_serverRunning)
                    {
                        TcpClient client;
                        try { client = await _server.AcceptTcpClientAsync(); }
                        catch { break; }
                        var captured = client;
                        var ignored = Task.Run(() => Serve(captured));
                    }
                });
            }

            private void StopServer()
            {
                _serverRunning = false;
                try { if (_server != null) _server.Stop(); } catch { }
            }

            private void Serve(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[8192];
                        var read = stream.Read(buffer, 0, buffer.Length);
                        var request = Encoding.ASCII.GetString(buffer, 0, Math.Max(0, read));
                        var parts = request.Split(' ');
                        var path = parts.Length > 1 ? parts[1] : "/";
                        lock (_requests) _requests.Add(path);

                        if (path.StartsWith("/lib1.js", StringComparison.OrdinalIgnoreCase))
                        {
                            Write(stream, "200 OK", "application/javascript; charset=utf-8", Lib1());
                            return;
                        }
                        if (path.StartsWith("/lib2.js", StringComparison.OrdinalIgnoreCase))
                        {
                            Write(stream, "200 OK", "application/javascript; charset=utf-8", Lib2());
                            return;
                        }
                        if (path.StartsWith("/page", StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith("/deny", StringComparison.OrdinalIgnoreCase))
                        {
                            Write(stream, "200 OK", "text/html; charset=utf-8", Page(path));
                            return;
                        }

                        Write(stream, "404 Not Found", "text/plain; charset=utf-8", "not found");
                    }
                    catch (IOException) { }
                    catch (Exception ex) { Line("[server] " + ex.Message); }
                }
            }

            private static string Lib1()
            {
                return "window.__m8Lib1 = 'lib1';\n" +
                       "window.__m8RequireOrder = (window.__m8RequireOrder || []).concat('lib1');\n" +
                       "window.__m8RequireRanBeforeUser = true;\n" +
                       "function m8Helper(){ return 'helper-from-lib'; }\n" +
                       "var __m8LibVar = 'visible-to-user';\n";
            }

            private static string Lib2()
            {
                return "window.__m8Lib2 = 'lib2';\n" +
                       "window.__m8RequireOrder = (window.__m8RequireOrder || []).concat('lib2');\n";
            }

            private static void Write(NetworkStream stream, string status, string contentType, string body)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                var header = "HTTP/1.1 " + status + "\r\n" +
                             "Content-Type: " + contentType + "\r\n" +
                             "Content-Length: " + bytes.Length + "\r\n" +
                             "Cache-Control: no-store\r\n" +
                             "Connection: close\r\n\r\n";
                var head = Encoding.ASCII.GetBytes(header);
                stream.Write(head, 0, head.Length);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }

            private static string Page(string path)
            {
                var name = path.TrimStart('/');
                var slash = name.IndexOf('?');
                if (slash >= 0) name = name.Substring(0, slash);
                return "<!doctype html><html><head><meta charset=\"utf-8\">" +
                       "<title>m8 " + name + "</title></head>" +
                       "<body style=\"margin:0;padding:18px;background:#111;color:#eee;" +
                       "font:14px 'Segoe UI',sans-serif\">" +
                       "<h1 id=\"m8-title\" style=\"font-size:18px;margin:0 0 10px 0\">NanoEdge M8 · " + name + "</h1>" +
                       "<p style=\"color:#888;font-size:12px\">local probe page served by 127.0.0.1</p>" +
                       "</body></html>";
            }

            // ---------- 报告 ----------

            private void Check(string name, bool passed, string detail)
            {
                if (passed) _passes++;
                else _failures.Add(name);
                Line((passed ? "[PASS] " : "[FAIL] ") + name + "  " + (detail ?? ""));
            }

            private void Fail(string name, string detail)
            {
                _failures.Add(name);
                Line("[FAIL] " + name + "  " + detail);
            }

            private void Line(string text)
            {
                lock (_report) _report.AppendLine(text);
                Diagnostics.Log("[m8test] " + text);
            }

            private void WriteReport()
            {
                try
                {
                    var path = Path.Combine(_root ?? Path.GetTempPath(), "out", "m8-report.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, _report.ToString(), Encoding.UTF8);
                }
                catch { }
            }

            private static void RenderPage(FrameworkElement page, string path, double width, double height)
            {
                page.Width = width;
                page.Height = height;
                page.Measure(new Size(width, height));
                page.Arrange(new Rect(0, 0, width, height));
                page.UpdateLayout();

                var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(page);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var stream = File.Create(path)) encoder.Save(stream);
            }

            private static string FindRoot()
            {
                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "SPEC.md")))
                    dir = dir.Parent;
                return dir == null ? Path.GetTempPath() : dir.FullName;
            }
        }
    }
}
