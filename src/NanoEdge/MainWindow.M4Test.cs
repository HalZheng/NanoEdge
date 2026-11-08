using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace NanoEdge
{
    public sealed partial class MainWindow
    {
        // --m4test：M4 脚本引擎验收。所有断言都跑在真实的 WebView2 上：
        // 本机 127.0.0.1 临时 HTTP 服务提供页面与 GM_xmlhttpRequest 的靶子，
        // 脚本走 AddScriptToExecuteOnDocumentCreatedAsync 真注入，不 mock 任何事件。
        // 数据全部落临时目录，跑完检查 %APPDATA%\NanoEdge 没多出脚本存储。
        private sealed class M4SelfTest
        {
            private readonly MainWindow _w;
            private readonly StringBuilder _report = new StringBuilder();
            private readonly List<string> _failures = new List<string>();
            private readonly object _reportLock = new object();
            private int _passes;
            private string _root;
            private string _temp;

            private TcpListener _server;
            private volatile bool _serverRunning;
            private int _port;
            private string _base;

            private bool _appIndexExisted;
            private bool _appValuesExisted;

            public M4SelfTest(MainWindow window) { _w = window; }

            public async Task RunAsync()
            {
                _root = FindRoot();
                Line("M4 userscript engine self-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Line("test data dir = " + Path.Combine(Path.GetTempPath(), "nanoedge-m4"));
                try
                {
                    Setup();
                    await RunCoreAsync();
                }
                catch (Exception ex) { Fail("unhandled", ex.ToString()); }
                finally
                {
                    StopServer();
                    Line("");
                    Line("appdata scripts.json        = " + AppPaths.ScriptIndex +
                         " (exists=" + File.Exists(AppPaths.ScriptIndex) + ")");
                    Line("appdata script-values.json  = " + AppPaths.ScriptValues +
                         " (exists=" + File.Exists(AppPaths.ScriptValues) + ")");
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
                _temp = Path.Combine(Path.GetTempPath(), "nanoedge-m4");
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

                // 备份/恢复的待导入状态也落在临时目录，别碰用户真实数据。
                DdlRestore.PendingPath = Path.Combine(_temp, "ddl-pending.json");
                DdlRestore.LastTargetPath = Path.Combine(_temp, "ddl-target.txt");
                DdlRestore.Clear();

                StartServer();
                _base = "http://127.0.0.1:" + _port;
            }

            // ---------- 自测主体 ----------

            private async Task RunCoreAsync()
            {
                // ---------- 1. 元数据解析 ----------
                var sample = @"// ==UserScript==
// @name         Sample Script
// @namespace    local.sample
// @version      2.3
// @description  demo script
// @match        https://*.example.com/*
// @match        http://localhost:3000/*
// @include      /^https?:\/\/127\.0\.0\.1:\d+\/include/
// @exclude      https://example.com/admin/*
// @run-at       document-start
// @grant        GM_setValue
// @grant        GM_getValue
// @grant        GM_getValue
// @connect      api.example.com
// ==/UserScript==
console.log('hello');";
                var parsed = ScriptParser.Parse(sample);
                Check("parse @name", parsed.Name == "Sample Script", parsed.Name);
                Check("parse @namespace", parsed.Namespace == "local.sample", parsed.Namespace);
                Check("parse @version", parsed.Version == "2.3", parsed.Version);
                Check("parse two @match lines", parsed.Matches.Count == 2, string.Join(" | ", parsed.Matches));
                Check("parse @include regex literal kept raw",
                    parsed.Includes.Count == 1 && parsed.Includes[0].StartsWith("/^https?"),
                    parsed.Includes.Count == 0 ? "(none)" : parsed.Includes[0]);
                Check("parse @exclude", parsed.Excludes.Count == 1, string.Join(" | ", parsed.Excludes));
                Check("parse @run-at document-start", parsed.RunAt == "document-start", parsed.RunAt);
                Check("duplicate @grant de-duped", parsed.Grants.Count(g => g == "GM_getValue") == 1,
                    string.Join(",", parsed.Grants));
                Check("parse @connect", parsed.Connects.SequenceEqual(new[] { "api.example.com" }),
                    string.Join(",", parsed.Connects));
                Check("unknown @run-at falls back to document-idle",
                    ScriptParser.Parse("// @name X\n// @run-at document-foo").RunAt == "document-idle",
                    ScriptParser.Parse("// @name X\n// @run-at document-foo").RunAt);
                Check("nameless script gets a default name",
                    ScriptParser.Parse("var a = 1;").Name == "未命名脚本",
                    ScriptParser.Parse("var a = 1;").Name);
                var noneGrant = ScriptParser.Parse("// @name N\n// @grant none");
                Check("@grant none is not a permission", noneGrant.Grants.Count == 0,
                    "grants=" + noneGrant.Grants.Count);

                // ---------- 2. 匹配规则（C# 侧只验证生成的 JS 正则源码） ----------
                var sub = ScriptParser.GlobToRegexSource("https://*.example.com/*");
                Check("wildcard subdomain matches the apex",
                    Regex.IsMatch("https://example.com/a", sub), sub);
                Check("wildcard subdomain matches one level",
                    Regex.IsMatch("https://a.example.com/a", sub), sub);
                Check("wildcard subdomain matches a deep subdomain",
                    Regex.IsMatch("https://a.b.example.com/a", sub), sub);
                Check("wildcard subdomain rejects lookalike host",
                    !Regex.IsMatch("https://badexample.com/a", sub), sub);

                var portSource = ScriptParser.GlobToRegexSource(_base + "/*");
                Check("match pattern keeps the explicit port",
                    Regex.IsMatch(_base + "/page", portSource), portSource);
                var otherPort = _port == 65535 ? _port - 1 : _port + 1;
                Check("match pattern rejects another port",
                    !Regex.IsMatch("http://127.0.0.1:" + otherPort + "/page", portSource), portSource);
                Check("match pattern rejects the default port",
                    !Regex.IsMatch("http://127.0.0.1/page", portSource), portSource);
                Check("wildcard port matches any port",
                    Regex.IsMatch("http://127.0.0.1:9999/x",
                        ScriptParser.GlobToRegexSource("http://127.0.0.1:*/x")),
                    ScriptParser.GlobToRegexSource("http://127.0.0.1:*/x"));
                Check("file:// pattern covers local files",
                    ScriptParser.ToRegexSource("file:///*") == "^file://.*$",
                    ScriptParser.ToRegexSource("file:///*"));

                // ---------- 3. 注入页 ----------
                string notification = null;
                Action<string> onNotify = text =>
                {
                    if (text != null && text.IndexOf("hello from userscript", StringComparison.Ordinal) >= 0)
                        notification = text;
                };
                _w._browser.Scripts.Notify += onNotify;

                var start = AddScript("M4 start", new[] { _base + "/*" }, null, null,
                    "document-start", new[] { "GM_info" },
                    @"window.__m4 = window.__m4 || {};
__m4.startRan = true;
__m4.startState = document.readyState;
__m4.startOrder = (typeof __m4.pageStart === 'undefined') ? 'before-page' : 'after-page';
__m4.gmInfoName = (GM_info && GM_info.script && GM_info.script.name) || '';
if (location.search.indexOf('opened=') < 0) {
  document.addEventListener('DOMContentLoaded', function () {
    if (!document.body) return;
    var b = document.createElement('div');
    b.id = 'm4-banner';
    b.textContent = 'NanoEdge 注入成功 · document-start';
    b.style.cssText = 'margin:0 0 12px 0;padding:6px 10px;background:#FF4D9D;color:#111;' +
                      'font:13px sans-serif;border-radius:4px';
    document.body.insertBefore(b, document.body.firstChild);
  });
}");

                var end = AddScript("M4 end", new[] { _base + "/page*" }, null, null,
                    "document-end", new[] { "GM_info" },
                    @"window.__m4 = window.__m4 || {};
__m4.endRan = true;
__m4.endState = document.readyState;
__m4.endSawTarget = !!document.getElementById('m4-target');");

                var gm = AddScript("M4 gm probe", new[] { _base + "/page*" }, null, null,
                    "document-start",
                    new[] { "GM_setValue", "GM_getValue", "GM_addStyle", "GM_notification",
                            "GM_openInTab", "GM_xmlhttpRequest", "GM_info" },
                    @"if (location.search.indexOf('opened=') < 0) {
  window.__m4 = window.__m4 || {};
  __m4.apiTypes = [typeof GM_setValue, typeof GM_getValue, typeof GM_addStyle,
                   typeof GM_notification, typeof GM_openInTab, typeof GM_xmlhttpRequest,
                   typeof GM_info].join(',');
  GM_addStyle('#m4-target{color:rgb(255,120,0)}');
  GM_setValue('probeKey', 42);
  GM_notification('hello from userscript', 'M4');
  GM_openInTab('" + _base + @"/page?opened=1');
  GM_xmlhttpRequest({
    method: 'GET',
    url: '" + _base + @"/echo?tag=allowed',
    onload: function (r) { __m4.xhrAllowed = r.status + ':' + r.responseText; },
    onerror: function (r) { __m4.xhrAllowedErr = String(r.error); }
  });
  GM_xmlhttpRequest({
    method: 'GET',
    url: 'http://localhost:" + _port + @"/echo?tag=denied',
    onload: function (r) { __m4.xhrDenied = r.status + ':' + r.responseText; },
    onerror: function (r) { __m4.xhrDeniedErr = String(r.error); }
  });
}", new[] { "127.0.0.1" });

                var exclude = AddScript("M4 exclude", new[] { _base + "/*" }, null,
                    new[] { _base + "/excluded*" }, "document-start", null,
                    @"window.__m4 = window.__m4 || {};
__m4.excludeRan = true;");

                var include = AddScript("M4 include", null,
                    new[] { "/^https?:\\/\\/127\\.0\\.0\\.1:\\d+\\/include/" }, null,
                    "document-start", null,
                    @"window.__m4 = window.__m4 || {};
__m4.includeRan = true;");

                var toggle = AddScript("M4 toggle", new[] { _base + "/toggle*" }, null, null,
                    "document-start", null,
                    @"window.__m4 = window.__m4 || {};
__m4.toggleRan = true;");

                var site = AddScript("M4 site", new[] { _base + "/site*" }, null, null,
                    "document-start", null,
                    @"window.__m4 = window.__m4 || {};
__m4.siteRan = true;");

                var noGrant = AddScript("M4 no grant", new[] { _base + "/nogrant*" }, null, null,
                    "document-start", null,
                    @"window.__m4 = window.__m4 || {};
__m4.noGrantApi = typeof GM_setValue;
__m4.noGrantStyle = typeof GM_addStyle;
var m = /[?&]sid=([^&]+)/.exec(location.search || '');
try {
  window.chrome.webview.postMessage({ __ne: 'setValue', id: m ? m[1] : 'missing',
                                      ns: 'm4nogrant', key: 'leak', value: 'x' });
  __m4.forgedPosted = true;
} catch (e) { __m4.forgedPosted = String(e); }");

                Check("store holds all 8 scripts", ScriptStore.All().Count == 8,
                    "count=" + ScriptStore.All().Count);
                Line("[inject] " + string.Join(" | ", ScriptStore.All()
                    .Select(s => s.Name + " -> " + string.Join(",", ScriptParser.MatchRegexes(s)))));

                // ---------- 4. 真实页面：document-start / document-end / GM API ----------
                var page1 = await OpenPageAsync(_base + "/page?v=1");
                var state1 = await StateAsync(page1);
                Check("document-start script ran", B(state1, "startRan"), Dump(state1));
                Check("document-start ran before the page's own script",
                    S(state1, "startOrder") == "before-page", S(state1, "startOrder"));
                Check("page script saw the injected flag", B(state1, "pageSawStart"),
                    "pageSawStart=" + S(state1, "pageSawStart"));
                Check("document-start readyState is loading", S(state1, "startState") == "loading",
                    S(state1, "startState"));
                Check("document-end script ran", B(state1, "endRan"), Dump(state1));
                Check("document-end runs after DOMContentLoaded",
                    S(state1, "endState") != "loading", S(state1, "endState"));
                Check("document-end sees the DOM", B(state1, "endSawTarget"),
                    "endSawTarget=" + S(state1, "endSawTarget"));
                Check("GM_info exposes the metadata", S(state1, "gmInfoName") == "M4 start",
                    S(state1, "gmInfoName"));
                Check("declared GM APIs are all present",
                    S(state1, "apiTypes") == "function,function,function,function,function,function,object",
                    S(state1, "apiTypes"));

                var styleTags = await EvalStringAsync(page1, "String(document.querySelectorAll('style[data-nanoedge]').length)");
                Check("GM_addStyle inserted a style tag", styleTags == "1", "style[data-nanoedge]=" + styleTags);
                var color = await EvalStringAsync(page1,
                    "String(getComputedStyle(document.getElementById('m4-target')).color)");
                Check("GM_addStyle took effect on the DOM", color == "rgb(255, 120, 0)", "color=" + color);

                var valueStored = await WaitForAsync(
                    () => Equals(ScriptStore.GetValue("m4", "probeKey", null), 42), 8000);
                Check("GM_setValue reached the host store", valueStored,
                    "value=" + (ScriptStore.GetValue("m4", "probeKey", null) ?? "null"));

                var xhrState = await WaitForStateAsync(page1,
                    s => S(s, "xhrAllowed") != null && S(s, "xhrDeniedErr") != null, 10000);
                Check("GM_xmlhttpRequest allowed by @connect",
                    S(xhrState, "xhrAllowed") == "200:{\"ok\":true,\"tag\":\"allowed\"}",
                    S(xhrState, "xhrAllowed") ?? "(none)");
                Check("GM_xmlhttpRequest denied when host is not in @connect",
                    (S(xhrState, "xhrDeniedErr") ?? "").IndexOf("@connect", StringComparison.OrdinalIgnoreCase) >= 0,
                    S(xhrState, "xhrDeniedErr") ?? "(none)");
                Check("denied request never reached the network",
                    S(xhrState, "xhrDenied") == null, "xhrDenied=" + S(xhrState, "xhrDenied"));

                var notified = await WaitForAsync(() => notification != null, 6000);
                Check("GM_notification reached the host bridge", notified, notification ?? "(none)");
                _w._browser.Scripts.Notify -= onNotify;

                var opened = await WaitForAsync(
                    () => _w._tabs.Any(t => (t.Url ?? "").Contains("opened=1")), 8000);
                Check("GM_openInTab opened a real tab", opened, "tabs=" + _w._tabs.Count);

                // WebView2 抓图：RenderTargetBitmap 抓不到 HwndHost，这里用官方 CapturePreview。
                _w.SelectTab(page1);
                await Task.Delay(500);
                await CapturePreviewAsync(page1, Path.Combine(_root, "out", "m4-injected.png"));
                Check("injected page captured to png",
                    new FileInfo(Path.Combine(_root, "out", "m4-injected.png")).Length > 1024,
                    "out/m4-injected.png");

                Check("GM value persisted to script-values.json",
                    File.Exists(ScriptStore.ValuesPath),
                    "bytes=" + (File.Exists(ScriptStore.ValuesPath)
                        ? new FileInfo(ScriptStore.ValuesPath).Length : 0));

                // ---------- 5. GM 值跨导航 ----------
                var readBack = AddScript("M4 values read", new[] { _base + "/page2*" }, null, null,
                    "document-start", new[] { "GM_getValue" },
                    @"window.__m4 = window.__m4 || {};
__m4.readBack = GM_getValue('probeKey', null);
__m4.readMissing = GM_getValue('nope', 'fallback');");

                var page2 = await OpenPageAsync(_base + "/page2?nav=1");
                var state2 = await StateAsync(page2);
                Check("GM_getValue returns the value after navigation",
                    I(state2, "readBack") == 42, "readBack=" + S(state2, "readBack"));
                Check("GM_getValue falls back for a missing key",
                    S(state2, "readMissing") == "fallback", S(state2, "readMissing"));

                // ---------- 6. @exclude ----------
                var pageEx = await OpenPageAsync(_base + "/excluded?v=1");
                var stateEx = await StateAsync(pageEx);
                Check("@exclude blocks the script", !H(stateEx, "excludeRan"), Dump(stateEx));
                Check("other scripts still run on the excluded page", B(stateEx, "startRan"),
                    "startRan=" + S(stateEx, "startRan"));

                // ---------- 7. @include 正则字面量 ----------
                var pageIn = await OpenPageAsync(_base + "/include?v=1");
                Check("@include regex literal matched the include page",
                    B(await StateAsync(pageIn), "includeRan"), Dump(await StateAsync(pageIn)));
                var pageNo = await OpenPageAsync(_base + "/page?v=include-neg");
                Check("@include regex literal does not match other paths",
                    !H(await StateAsync(pageNo), "includeRan"), Dump(await StateAsync(pageNo)));

                // ---------- 8. 启用开关 ----------
                ScriptStore.SetEnabled(toggle.Id, false);
                var pageOff = await OpenPageAsync(_base + "/toggle?off=1");
                Check("disabled script does not run", !H(await StateAsync(pageOff), "toggleRan"),
                    "enabled=" + ScriptStore.Find(toggle.Id).Enabled);
                ScriptStore.SetEnabled(toggle.Id, true);
                var pageOn = await OpenPageAsync(_base + "/toggle?on=1");
                Check("re-enabled script runs again",
                    B(await StateAsync(pageOn), "toggleRan"),
                    "enabled=" + ScriptStore.Find(toggle.Id).Enabled);

                // ---------- 9. 按站点禁用 ----------
                var siteKey = ZoomStore.SiteOf(_base + "/site");
                ScriptStore.ToggleSiteKey(siteKey);
                Check("site toggle recorded as a host[:port] key",
                    ScriptStore.IsSiteDisabledKey(siteKey), siteKey);
                var pageSiteOff = await OpenPageAsync(_base + "/site?d=1");
                var stateSiteOff = await StateAsync(pageSiteOff);
                Check("all scripts skip a disabled site",
                    !H(stateSiteOff, "siteRan") && !H(stateSiteOff, "startRan"), Dump(stateSiteOff));

                ScriptStore.ToggleSiteKey(siteKey);
                var pageSiteOn = await OpenPageAsync(_base + "/site?d=2");
                Check("scripts resume after re-enabling the site",
                    B(await StateAsync(pageSiteOn), "siteRan"), "site=" + siteKey);

                // ---------- 10. @grant 双端拒绝 ----------
                var pageNg = await OpenPageAsync(_base + "/nogrant?sid=" + noGrant.Id + "&v=1");
                var stateNg = await StateAsync(pageNg);
                Check("undeclared GM_setValue is undefined in the page",
                    S(stateNg, "noGrantApi") == "undefined", S(stateNg, "noGrantApi"));
                Check("undeclared GM_addStyle is undefined in the page",
                    S(stateNg, "noGrantStyle") == "undefined", S(stateNg, "noGrantStyle"));
                Check("page managed to post a forged GM message",
                    B(stateNg, "forgedPosted"), S(stateNg, "forgedPosted"));
                await Task.Delay(500);
                Check("host rejects the same API without @grant",
                    ScriptStore.GetValue("m4nogrant", "leak", null) == null,
                    "value=" + (ScriptStore.GetValue("m4nogrant", "leak", null) ?? "null"));

                // ---------- 11. 管理页 ----------
                Check("route nano://scripts",
                    InternalPages.Normalize("nano://scripts") == InternalPages.Scripts,
                    InternalPages.Normalize("nano://scripts"));
                Check("scripts page title", InternalPages.TitleOf("nano://scripts") == "用户脚本",
                    InternalPages.TitleOf("nano://scripts"));
                Check("unknown nano:// still falls back to start",
                    InternalPages.Normalize("nano://whatever") == InternalPages.Start,
                    InternalPages.Normalize("nano://whatever"));

                var c5Messages = new List<string>();
                var scriptsPage = new ScriptsPage(m => c5Messages.Add(m), () => siteKey);
                Check("scripts page is pure WPF",
                    InternalPages.Create("nano://scripts", _w._settings, (u, n) => { }, m => { },
                        () => siteKey) is ScriptsPage, "type=ScriptsPage");
                Check("list renders one row per script",
                    scriptsPage.RenderedRowCount == ScriptStore.All().Count,
                    "rows=" + scriptsPage.RenderedRowCount + " store=" + ScriptStore.All().Count);
                RenderPage(scriptsPage, Path.Combine(_root, "out", "m4-scripts.png"), 980, 560);
                Check("scripts list rendered", File.Exists(Path.Combine(_root, "out", "m4-scripts.png")),
                    "out/m4-scripts.png");

                Theme.Apply("light");
                var lightPage = new ScriptsPage(m => { }, () => siteKey);
                RenderPage(lightPage, Path.Combine(_root, "out", "m4-scripts-light.png"), 980, 560);
                Theme.Apply("dark");
                Check("light theme scripts page rendered",
                    File.Exists(Path.Combine(_root, "out", "m4-scripts-light.png")),
                    "out/m4-scripts-light.png");

                // 站点禁用态的可视证据
                ScriptStore.ToggleSiteKey(siteKey);
                var offPage = new ScriptsPage(m => { }, () => siteKey);
                RenderPage(offPage, Path.Combine(_root, "out", "m4-scripts-site-off.png"), 980, 560);
                Check("site-disabled state rendered",
                    File.Exists(Path.Combine(_root, "out", "m4-scripts-site-off.png")),
                    "out/m4-scripts-site-off.png");

                // 站点禁用只存内存：重新加载索引后应清空
                ScriptStore.Reload();
                Check("site disable is memory-only (cleared by reload)",
                    ScriptStore.DisabledSites.Count == 0,
                    "disabled=" + ScriptStore.DisabledSites.Count);

                // 编辑器（等宽 + 行号 + C5 可编辑元数据）
                var editTarget = ScriptStore.All().First(s => s.Name == "M4 start");
                scriptsPage.BeginEdit(editTarget);
                RenderPage(scriptsPage, Path.Combine(_root, "out", "m4-scripts-editor.png"), 1200, 780);
                Check("editor rendered with line numbers",
                    File.Exists(Path.Combine(_root, "out", "m4-scripts-editor.png")),
                    "out/m4-scripts-editor.png");
                Check("C5 editor exposes the current name",
                    scriptsPage.MetaNameForTest == "M4 start",
                    "field=" + scriptsPage.MetaNameForTest);

                // ---------- 11a. C5：改四个字段 → 回写 // @ 头 → 导出 → 再导入 ----------
                Func<string, List<string>> c5Headers = code =>
                    (code ?? "").Replace("\r\n", "\n").Split('\n')
                        .Where(l =>
                        {
                            var m = ScriptParser.MetaLine.Match(l);
                            if (!m.Success) return false;
                            var k = m.Groups[1].Value.ToLowerInvariant();
                            return k != "name" && k != "version" && k != "match" && k != "exclude";
                        }).ToList();
                var c5OldHeaders = c5Headers(editTarget.Code);

                scriptsPage.SetMetadataForTest("M4 renamed", "4.5",
                    "https://c5.example/*\nhttp://127.0.0.1:*/c5/*",
                    "https://c5.example/admin/*");
                RenderPage(scriptsPage, Path.Combine(_root, "out", "m4-scripts-meta.png"), 1200, 780);
                Check("C5 metadata editor rendered",
                    File.Exists(Path.Combine(_root, "out", "m4-scripts-meta.png")),
                    "out/m4-scripts-meta.png");
                scriptsPage.SaveEditorForTest();

                // 重新打开编辑器：字段与源码头都必须已经是保存后的值。
                scriptsPage.BeginEdit(editTarget);
                RenderPage(scriptsPage, Path.Combine(_root, "out", "m4-scripts-meta-saved.png"), 1200, 780);
                Check("C5 saved editor shows the rewritten // @ header",
                    File.Exists(Path.Combine(_root, "out", "m4-scripts-meta-saved.png")) &&
                    (scriptsPage.EditorCodeForTest ?? "").Contains("// @name         M4 renamed") &&
                    (scriptsPage.EditorCodeForTest ?? "").Contains("https://c5.example/*"),
                    "out/m4-scripts-meta-saved.png");

                Check("C5 save rewrites the four // @ fields in the source",
                    editTarget.Code.Contains("M4 renamed") && editTarget.Code.Contains("4.5") &&
                    editTarget.Code.Contains("https://c5.example/*") &&
                    editTarget.Code.Contains("http://127.0.0.1:*/c5/*") &&
                    editTarget.Code.Contains("https://c5.example/admin/*") &&
                    !editTarget.Code.Contains("M4 start"),
                    "head=" + editTarget.Code.Split('\n')[1]);
                Check("C5 store metadata refreshed after save",
                    editTarget.Name == "M4 renamed" && editTarget.Version == "4.5" &&
                    editTarget.Matches.Count == 2 && editTarget.Excludes.Count == 1,
                    editTarget.Name + " v" + editTarget.Version +
                    " matches=" + editTarget.Matches.Count + " excludes=" + editTarget.Excludes.Count);
                Check("C5 other // @ headers kept verbatim and in order",
                    c5OldHeaders.Count > 0 && c5OldHeaders.SequenceEqual(c5Headers(editTarget.Code)),
                    "before=" + c5OldHeaders.Count + " after=" + c5Headers(editTarget.Code).Count);
                Check("C5 list refreshed after save",
                    scriptsPage.RenderedRowCount == ScriptStore.All().Count,
                    "rows=" + scriptsPage.RenderedRowCount + " store=" + ScriptStore.All().Count);

                var c5File = File.ReadAllText(editTarget.FilePath, Encoding.UTF8);
                Check("C5 .user.js on disk equals the rewritten source",
                    c5File == editTarget.Code, "file=" + editTarget.FilePath);
                var c5FromFile = ScriptParser.Parse(c5File);
                Check("C5 exported .user.js re-imports with the same metadata",
                    c5FromFile.Name == "M4 renamed" && c5FromFile.Version == "4.5" &&
                    c5FromFile.Matches.SequenceEqual(editTarget.Matches) &&
                    c5FromFile.Excludes.SequenceEqual(editTarget.Excludes),
                    "name=" + c5FromFile.Name + " v" + c5FromFile.Version);

                var c5CountBefore = ScriptStore.All().Count;
                scriptsPage.StageImport(c5File, "c5-exported.user.js");
                Check("C5 import card shows the edited name",
                    (scriptsPage.LastImportSummary ?? "").Contains("M4 renamed"),
                    (scriptsPage.LastImportSummary ?? "").Replace("\n", " / "));
                scriptsPage.ConfirmImport();
                var c5Reimported = ScriptStore.All().FirstOrDefault(
                    s => s.Name == "M4 renamed" && !ReferenceEquals(s, editTarget));
                Check("C5 re-imported script keeps all four fields",
                    c5Reimported != null && c5Reimported.Version == "4.5" &&
                    c5Reimported.Matches.SequenceEqual(editTarget.Matches) &&
                    c5Reimported.Excludes.SequenceEqual(editTarget.Excludes),
                    c5Reimported == null ? "not found" : c5Reimported.Name);
                if (c5Reimported != null) scriptsPage.RemoveScript(c5Reimported);
                Check("C5 re-import cleanup keeps the store count",
                    ScriptStore.All().Count == c5CountBefore,
                    "count=" + ScriptStore.All().Count);

                // 只改源码、字段没动 → 源码里的 // @ 行不能被旧字段值覆盖回去。
                var c5Hand = editTarget.Code.Replace("M4 renamed", "M4 hand-edited");
                scriptsPage.BeginEdit(editTarget);
                scriptsPage.SetCodeForTest(c5Hand);
                scriptsPage.SaveEditorForTest();
                Check("C5 hand-editing the source is not overwritten by stale fields",
                    editTarget.Code == c5Hand && editTarget.Name == "M4 hand-edited",
                    "name=" + editTarget.Name);

                // 失败必须有可见反馈，且不落盘。
                scriptsPage.BeginEdit(editTarget);
                c5Messages.Clear();
                scriptsPage.SetMetadataForTest("", null, null, null);
                scriptsPage.SaveEditorForTest();
                Check("C5 empty name shows a visible error and does not save",
                    c5Messages.Any(m => m.Contains("名称")) && editTarget.Code == c5Hand,
                    "messages=" + string.Join(" / ", c5Messages));

                // 导入确认：先展示 @grant，确认前不入库
                var beforeImport = ScriptStore.All().Count;
                var importCode = @"// ==UserScript==
// @name         Imported Sample
// @namespace    m4.import
// @version      1.0
// @match        https://example.org/*
// @grant        GM_setValue
// @grant        GM_xmlhttpRequest
// @connect      example.org
// ==/UserScript==
GM_setValue('imported', true);";
                scriptsPage.StageImport(importCode, "self-test.user.js");
                Check("import shows the grant list before writing",
                    scriptsPage.HasPendingImport && ScriptStore.All().Count == beforeImport,
                    "summary=" + (scriptsPage.LastImportSummary ?? "").Replace("\n", " / "));
                Check("grant list mentions both permissions",
                    (scriptsPage.LastImportSummary ?? "").Contains("GM_setValue") &&
                    (scriptsPage.LastImportSummary ?? "").Contains("GM_xmlhttpRequest"),
                    (scriptsPage.LastImportSummary ?? "").Replace("\n", " / "));
                RenderPage(scriptsPage, Path.Combine(_root, "out", "m4-import.png"), 1200, 780);
                Check("import confirmation rendered",
                    File.Exists(Path.Combine(_root, "out", "m4-import.png")), "out/m4-import.png");

                scriptsPage.ConfirmImport();
                var imported = ScriptStore.All().FirstOrDefault(s => s.Name == "Imported Sample");
                Check("confirmed import is written to the store",
                    imported != null && ScriptStore.All().Count == beforeImport + 1,
                    "count=" + ScriptStore.All().Count);
                Check("imported code file exists",
                    imported != null && File.Exists(imported.FilePath), imported == null ? "-" : imported.FilePath);

                scriptsPage.RemoveScript(imported);
                Check("delete removes the script and its code file",
                    ScriptStore.All().Count == beforeImport &&
                    (imported == null || !File.Exists(imported.FilePath)),
                    "count=" + ScriptStore.All().Count);

                // ---------- 11c. C5：导入确认卡直接配置元数据 ----------
                var c5CardCode = "// ==UserScript==\r\n" +
                                 "// @name         Card Original\r\n" +
                                 "// @namespace    c5.card\r\n" +
                                 "// @version      0.1\r\n" +
                                 "// @match        https://old.example/*\r\n" +
                                 "// @grant        GM_setValue\r\n" +
                                 "// @run-at       document-start\r\n" +
                                 "// @include      /regex-literal/\r\n" +
                                 "// @exclude      https://old.example/admin/*\r\n" +
                                 "// ==/UserScript==\r\n" +
                                 "console.log('card');\r\n";
                var c5CardBefore = ScriptStore.All().Count;
                scriptsPage.StageImport(c5CardCode, "c5-card.user.js");
                Check("C5 import card prefills the parsed name",
                    scriptsPage.ImportNameForTest == "Card Original",
                    "field=" + scriptsPage.ImportNameForTest);
                Check("C5 import card still waits for confirmation",
                    scriptsPage.HasPendingImport && ScriptStore.All().Count == c5CardBefore,
                    "count=" + ScriptStore.All().Count);

                scriptsPage.SetImportMetadataForTest("Card Renamed", "2.0",
                    "https://a.example/*\nhttps://b.example/*",
                    "https://a.example/admin/*\nhttps://b.example/login/*");
                RenderPage(scriptsPage, Path.Combine(_root, "out", "m4-import-meta.png"), 1200, 780);
                Check("C5 editable import card rendered",
                    File.Exists(Path.Combine(_root, "out", "m4-import-meta.png")),
                    "out/m4-import-meta.png");

                scriptsPage.ConfirmImport();
                var c5Card = ScriptStore.All().FirstOrDefault(s => s.Name == "Card Renamed");
                Check("C5 import card edits are stored",
                    c5Card != null && c5Card.Version == "2.0" &&
                    c5Card.Matches.Count == 2 && c5Card.Excludes.Count == 2,
                    c5Card == null ? "not found" : c5Card.Name + " v" + c5Card.Version +
                    " matches=" + c5Card.Matches.Count + " excludes=" + c5Card.Excludes.Count);
                Check("C5 import card writes the // @ header into the .user.js file",
                    c5Card != null && File.Exists(c5Card.FilePath) &&
                    File.ReadAllText(c5Card.FilePath, Encoding.UTF8) == c5Card.Code &&
                    c5Card.Code.Contains("// @name         Card Renamed") &&
                    c5Card.Code.Contains("https://a.example/*") &&
                    !c5Card.Code.Contains("Card Original"),
                    c5Card == null ? "no script" : "file=" + c5Card.FilePath);
                Check("C5 import card keeps @grant / @run-at / @namespace / @include",
                    c5Card != null && c5Card.Namespace == "c5.card" &&
                    c5Card.RunAt == "document-start" &&
                    c5Card.Grants.Count == 1 && c5Card.Grants[0] == "GM_setValue" &&
                    c5Card.Includes.SequenceEqual(new[] { "/regex-literal/" }),
                    c5Card == null ? "no script" : "grant=" + string.Join(",", c5Card.Grants));
                if (c5Card != null) scriptsPage.RemoveScript(c5Card);

                // 字段没改 → 原样导入，一个字节都不动。
                scriptsPage.StageImport(c5CardCode, "c5-untouched.user.js");
                scriptsPage.ConfirmImport();
                var c5Untouched = ScriptStore.All().FirstOrDefault(s => s.Name == "Card Original");
                Check("C5 untouched import card keeps the source byte-for-byte",
                    c5Untouched != null && c5Untouched.Code == c5CardCode,
                    c5Untouched == null ? "not found" :
                        "same=" + (c5Untouched.Code == c5CardCode));
                if (c5Untouched != null) scriptsPage.RemoveScript(c5Untouched);

                // 名称清空：可见反馈、不入库、保留待导入状态可以改完再点。
                scriptsPage.StageImport(c5CardCode, "c5-invalid.user.js");
                c5Messages.Clear();
                scriptsPage.SetImportMetadataForTest("", null, null, null);
                scriptsPage.ConfirmImport();
                Check("C5 empty name on the import card is rejected",
                    c5Messages.Any(m => m.Contains("名称")) &&
                    ScriptStore.All().Count == c5CardBefore && scriptsPage.HasPendingImport,
                    "messages=" + string.Join(" / ", c5Messages));
                scriptsPage.CloseOverlayForTest();
                Check("C5 import card cleanup keeps the store count",
                    ScriptStore.All().Count == c5CardBefore,
                    "count=" + ScriptStore.All().Count);

                // ---------- 11d. 删除确认按钮必须留在行内且点得到 ----------
                Button deleteBtn = null;
                WalkVisual(scriptsPage, o =>
                {
                    var b = o as Button;
                    if (b != null && Equals(b.ToolTip, L.T("删除脚本"))) deleteBtn = b;
                });
                Check("row delete button exists", deleteBtn != null,
                    deleteBtn == null ? "not found" : "width=" + deleteBtn.ActualWidth);
                if (deleteBtn != null)
                {
                    deleteBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    scriptsPage.Measure(new Size(1200, 780));
                    scriptsPage.Arrange(new Rect(0, 0, 1200, 780));
                    scriptsPage.UpdateLayout();
                    RenderPage(scriptsPage, Path.Combine(_root, "out", "m4-delete-confirm.png"), 1200, 780);
                    var rowGrid = deleteBtn.Parent as Grid;
                    var origin = rowGrid == null ? new Point(-1, -1)
                        : deleteBtn.TransformToVisual(rowGrid).Transform(new Point(0, 0));
                    Check("confirm button stays inside the row and is wide enough to click",
                        Equals(deleteBtn.Content, L.T("再点一次")) &&
                        Grid.GetColumnSpan(deleteBtn) >= 2 &&
                        deleteBtn.ActualWidth >= 70 &&
                        rowGrid != null && origin.X >= 0 &&
                        origin.X + deleteBtn.ActualWidth <= rowGrid.ActualWidth + 1,
                        "content=" + deleteBtn.Content + " width=" + deleteBtn.ActualWidth +
                        " span=" + Grid.GetColumnSpan(deleteBtn) +
                        " x=" + origin.X.ToString("0") +
                        " rowWidth=" + (rowGrid == null ? -1 : rowGrid.ActualWidth));
                    Check("delete confirmation screenshot written",
                        File.Exists(Path.Combine(_root, "out", "m4-delete-confirm.png")),
                        "out/m4-delete-confirm.png");
                }

                // ---------- 11e. 空匹配/排除框的格式示例 placeholder ----------
                var plainCode = "// ==UserScript==\n// @name    No Matches\n" +
                                "// @version 1.0\n// ==/UserScript==\nconsole.log('x');";
                scriptsPage.StageImport(plainCode, "c5-plain.user.js");
                var hints = TaggedTextBlocks(scriptsPage, "meta-placeholder");
                Check("empty match/exclude boxes show format hints",
                    hints.Count == 2 && hints.All(h => h.Visibility == Visibility.Visible),
                    "count=" + hints.Count +
                    " visible=" + hints.Count(h => h.Visibility == Visibility.Visible));
                Check("placeholder text contains a format example",
                    hints.Count == 2 && hints.All(h => (h.Text ?? "").Contains("https://")),
                    hints.Count == 0 ? "none" : hints[0].Text);
                RenderPage(scriptsPage, Path.Combine(_root, "out", "m4-import-placeholder.png"), 1200, 780);
                Check("placeholder screenshot written",
                    File.Exists(Path.Combine(_root, "out", "m4-import-placeholder.png")),
                    "out/m4-import-placeholder.png");
                scriptsPage.SetImportMetadataForTest(null, null, "https://x.example/*", null);
                var hintsAfter = TaggedTextBlocks(scriptsPage, "meta-placeholder");
                Check("placeholder hides once the box has content",
                    hintsAfter.Count == 2 &&
                    hintsAfter.Count(h => h.Visibility == Visibility.Visible) == 1,
                    "visible=" + hintsAfter.Count(h => h.Visibility == Visibility.Visible));
                scriptsPage.CloseOverlayForTest();

                // ---------- 11f. 整体备份 / 恢复 ----------
                var bundleBefore = ScriptStore.All().Count;
                const string ddlText = "CREATE TABLE public.demo (id int);";
                var bundle = ScriptBundleFile.FromStore(new[]
                {
                    new ScriptBundleAttachment
                    {
                        Kind = ScriptBundleFile.DdlKind,
                        Name = "schema.sql",
                        Text = ddlText,
                        Target = _base + "/page*"
                    }
                });
                Check("bundle captures every script and the DDL attachment",
                    bundle.Scripts.Count == bundleBefore && bundle.Attachments.Count == 1,
                    "scripts=" + bundle.Scripts.Count + " attachments=" + bundle.Attachments.Count);
                ScriptBundle parsedBundle;
                string bundleError;
                Check("bundle json round-trips",
                    ScriptBundleFile.TryParse(ScriptBundleFile.ToJson(bundle),
                        out parsedBundle, out bundleError) &&
                    parsedBundle.Scripts.Count == bundleBefore &&
                    parsedBundle.Attachments.Count == 1 &&
                    parsedBundle.Attachments[0].Text == ddlText,
                    bundleError);
                var mergePlan = ScriptBundleFile.Plan(parsedBundle, false);
                Check("default restore merges (no duplicates)",
                    mergePlan.Added == 0 && mergePlan.Updated == bundleBefore,
                    "added=" + mergePlan.Added + " updated=" + mergePlan.Updated);
                var clearPlan = ScriptBundleFile.Plan(parsedBundle, true);
                Check("clear-first restore adds everything",
                    clearPlan.Added == bundleBefore && clearPlan.Updated == 0,
                    "added=" + clearPlan.Added + " updated=" + clearPlan.Updated);

                scriptsPage.StageBundleForTest(parsedBundle, "c5-bundle.json");
                RenderPage(scriptsPage, Path.Combine(_root, "out", "m4-bundle-restore.png"), 1200, 780);
                Check("restore confirmation card rendered",
                    File.Exists(Path.Combine(_root, "out", "m4-bundle-restore.png")),
                    "out/m4-bundle-restore.png");
                scriptsPage.CommitRestoreForTest();
                Check("merged restore keeps the store count",
                    ScriptStore.All().Count == bundleBefore,
                    "count=" + ScriptStore.All().Count);

                var pending = DdlRestore.Load();
                Check("DDL pending state is written after restore",
                    pending != null && pending.Text == ddlText &&
                    pending.Target == _base + "/page*" &&
                    !string.IsNullOrEmpty(pending.Token),
                    pending == null ? "no pending" : "name=" + pending.Name);
                Check("DDL target only matches the intended site",
                    pending != null &&
                    DdlRestore.Matches(_base + "/page?v=1", pending.Target) &&
                    !DdlRestore.Matches("https://other.example/x", pending.Target),
                    "target=" + (pending == null ? "-" : pending.Target));
                var ddlScript = pending == null ? "" : DdlRestore.BuildScript(pending);
                Check("DDL inject script waits for __pg4, de-dupes and reports back",
                    ddlScript.Contains("__pg4") && ddlScript.Contains("listSnapshots") &&
                    ddlScript.Contains("importDdlText") && ddlScript.Contains("ddlDone") &&
                    pending != null && ddlScript.Contains(pending.Token),
                    "len=" + ddlScript.Length);
                DdlRestore.Complete("forged-token");
                Check("forged ddlDone does not clear pending",
                    DdlRestore.Load() != null, "still pending");
                DdlRestore.Complete(pending == null ? "" : pending.Token);
                Check("valid ddlDone clears pending",
                    DdlRestore.Load() == null, "cleared");

                scriptsPage.ShowBackupCardForTest();
                RenderPage(scriptsPage, Path.Combine(_root, "out", "m4-bundle-backup.png"), 1200, 780);
                Check("backup card rendered",
                    File.Exists(Path.Combine(_root, "out", "m4-bundle-backup.png")),
                    "out/m4-bundle-backup.png");
                scriptsPage.CloseOverlayForTest();

                // ---------- 11g. 真实闭环：导出文件 → 读回恢复 → 页面重建 → 回执 → 幂等 ----------
                const string chainDdl = "CREATE TABLE public.chain_demo (id int, note text);";
                var chainBundle = ScriptBundleFile.FromStore(new[]
                {
                    new ScriptBundleAttachment
                    {
                        Kind = ScriptBundleFile.DdlKind,
                        Name = "chain.sql",
                        Text = chainDdl,
                        Target = _base + "/ddl-page*"
                    }
                });
                var chainPath = Path.Combine(_root, "out", "m4-bundle-chain.json");
                File.WriteAllText(chainPath, ScriptBundleFile.ToJson(chainBundle),
                    new UTF8Encoding(false));
                var chainBytes = new FileInfo(chainPath).Length;
                Check("real backup file written", File.Exists(chainPath) && chainBytes > 100,
                    "bytes=" + chainBytes + " path=" + chainPath);

                ScriptBundle chainRead;
                string chainError;
                Check("backup file re-read into a bundle",
                    ScriptBundleFile.TryParse(File.ReadAllText(chainPath, Encoding.UTF8),
                        out chainRead, out chainError) &&
                    chainRead.Scripts.Count == bundleBefore &&
                    chainRead.Attachments.Count == 1 &&
                    chainRead.Attachments[0].Text == chainDdl,
                    chainError);

                var chainPage = new ScriptsPage(m => { }, () => "");
                chainPage.StageBundleForTest(chainRead, "m4-bundle-chain.json");
                chainPage.CommitRestoreForTest();
                Check("restore from file wrote the pending DDL state",
                    DdlRestore.Load() != null && DdlRestore.HasPending,
                    "pending=" + DdlRestore.HasPending);

                var chainTab = await OpenPageAsync(_base + "/ddl-page?v=1");
                var chainCore = _w._browser.Core(chainTab);
                Check("DDL probe page has a live WebView2", chainCore != null, "core");
                var rebuilt = await WaitForAsync(() => DdlRestore.Load() == null, 12000);
                Check("ddlDone receipt cleared the pending state", rebuilt,
                    "pending=" + (DdlRestore.Load() == null ? "cleared" : "still there"));
                if (chainCore != null)
                {
                    var calls = Num(await chainCore.ExecuteScriptAsync("String(window.__ddlCalls||0)"));
                    var count = Num(await chainCore.ExecuteScriptAsync(
                        "String(JSON.parse(localStorage.getItem('__neDdlSnapshots')||'[]').length)"));
                    var len = Num(await chainCore.ExecuteScriptAsync(
                        "String((JSON.parse(localStorage.getItem('__neDdlSnapshots')||'[]')[0]||{}).bytes||0)"));
                    Check("page imported the DDL exactly once",
                        calls == "1" && count == "1" && len == chainDdl.Length.ToString(),
                        "calls=" + calls + " snapshots=" + count + " bytes=" + len);
                    var status = await chainCore.ExecuteScriptAsync(
                        "document.getElementById('ddl-status').textContent");
                    Check("page shows the rebuilt snapshot",
                        (status ?? "").Contains("chain.sql"), status ?? "");
                    await CapturePreviewAsync(chainTab,
                        Path.Combine(_root, "out", "m4-bundle-chain-page.png"));
                    Check("DDL chain page screenshot written",
                        File.Exists(Path.Combine(_root, "out", "m4-bundle-chain-page.png")),
                        "out/m4-bundle-chain-page.png");
                }

                // 回执丢了但 pending 还在：同一页面再注入一次，必须被幂等检查挡住。
                DdlRestore.Save(new DdlPending
                {
                    Name = "chain.sql",
                    Text = chainDdl,
                    Target = _base + "/ddl-page*",
                    Token = "chain-idempotent"
                });
                var idempotent = await WaitForAsync(() => DdlRestore.Load() == null, 8000);
                if (chainCore != null)
                {
                    var calls2 = Num(await chainCore.ExecuteScriptAsync("String(window.__ddlCalls||0)"));
                    Check("second inject is a no-op (idempotent)",
                        idempotent && calls2 == "1",
                        "calls=" + calls2 + " cleared=" + idempotent);
                }
                DdlRestore.Clear();

                // 主菜单入口 → 真实窗口
                _w.OpenScripts();
                Check("menu entry opens nano://scripts", _w._active.InternalPage == "scripts",
                    "tab=" + _w._active.InternalPage + " url=" + _w._active.Url);
                Check("scripts page has no WebView2", !_w._browser.HasLiveView(_w._active),
                    "live=" + _w._browser.HasLiveView(_w._active));

                // 复用已开的 scripts 标签时，「当前站点」要跟着当前网页换掉，不能停在旧站点。
                _w.NewTab();
                var strayTab = _w._active;
                strayTab.Url = "https://other.example/x";
                _w.OpenScripts();
                var reused = _w._tabs.FirstOrDefault(t => t.InternalPage == "scripts");
                var reusedView = reused == null ? null : _w._browser.ViewOf(reused) as ScriptsPage;
                Check("reopening the scripts tab rebinds the current site",
                    reusedView != null && reusedView.CurrentSiteForTest == "other.example",
                    reusedView == null ? "no view" : reusedView.CurrentSiteForTest);
                Check("still a single scripts tab",
                    _w._tabs.Count(t => t.InternalPage == "scripts") == 1,
                    "count=" + _w._tabs.Count(t => t.InternalPage == "scripts"));

                // 回到真实站点，截图里显示真实站点而不是假域名。
                var realTab = _w._tabs.FirstOrDefault(t => (t.Url ?? "").StartsWith(_base));
                if (realTab != null) { _w.SelectTab(realTab); _w.OpenScripts(); }
                var liveView = _w._browser.ViewOf(_w._active) as ScriptsPage;
                Check("site bar shows the real probe site",
                    liveView != null && liveView.CurrentSiteForTest == "127.0.0.1:" + _port,
                    liveView == null ? "no view" : liveView.CurrentSiteForTest);

                _w.WindowState = WindowState.Normal;
                _w.Width = 1100;
                _w.Height = 720;
                await Task.Delay(400);
                _w.UpdateLayout();
                _w.CaptureScreenshot(Path.Combine(_root, "out", "m4-window.png"));
                Check("live window screenshot written",
                    File.Exists(Path.Combine(_root, "out", "m4-window.png")), "out/m4-window.png");

                // ---------- 12. 临时目录确实被使用 ----------
                Check("scripts.json written to the temp store", File.Exists(ScriptStore.IndexPath),
                    ScriptStore.IndexPath);
                Check("script code files written to the temp folder",
                    ScriptStore.All().All(s => File.Exists(s.FilePath)),
                    ScriptStore.Folder);
                Check("no scripts.json created under %APPDATA%",
                    File.Exists(AppPaths.ScriptIndex) == _appIndexExisted,
                    AppPaths.ScriptIndex);
                Check("no script-values.json created under %APPDATA%",
                    File.Exists(AppPaths.ScriptValues) == _appValuesExisted,
                    AppPaths.ScriptValues);

                Line("");
                Line("store = " + ScriptStore.IndexPath);
                Line("values = " + ScriptStore.ValuesPath);
                Line("folder = " + ScriptStore.Folder);
            }

            // ---------- 页面与脚本构造 ----------

            private UserScript AddScript(string name, string[] matches, string[] includes,
                string[] excludes, string runAt, string[] grants, string body, string[] connects = null)
            {
                return ScriptStore.Add(ScriptText(name, matches, includes, excludes, runAt, grants, connects, body), true);
            }

            private static string ScriptText(string name, string[] matches, string[] includes,
                string[] excludes, string runAt, string[] grants, string[] connects, string body)
            {
                var sb = new StringBuilder();
                sb.AppendLine("// ==UserScript==");
                sb.AppendLine("// @name         " + name);
                sb.AppendLine("// @namespace    m4");
                sb.AppendLine("// @version      1.0");
                if (matches != null) foreach (var m in matches) sb.AppendLine("// @match        " + m);
                if (includes != null) foreach (var i in includes) sb.AppendLine("// @include      " + i);
                if (excludes != null) foreach (var e in excludes) sb.AppendLine("// @exclude      " + e);
                sb.AppendLine("// @run-at       " + runAt);
                if (grants != null) foreach (var g in grants) sb.AppendLine("// @grant        " + g);
                if (connects != null) foreach (var c in connects) sb.AppendLine("// @connect      " + c);
                sb.AppendLine("// ==/UserScript==");
                sb.Append(body);
                return sb.ToString();
            }

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
                    var raw = await core.ExecuteScriptAsync("JSON.stringify(window.__m4 || {})");
                    var json = new JavaScriptSerializer().Deserialize<string>(raw);
                    return new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>
                           ?? new Dictionary<string, object>();
                }
                catch { return new Dictionary<string, object>(); }
            }

            private async Task<Dictionary<string, object>> WaitForStateAsync(TabModel tab,
                Func<Dictionary<string, object>, bool> predicate, int timeoutMs)
            {
                var started = Environment.TickCount;
                var state = new Dictionary<string, object>();
                while (Environment.TickCount - started < timeoutMs)
                {
                    state = await StateAsync(tab);
                    if (predicate(state)) return state;
                    await Task.Delay(150);
                }
                return state;
            }

            private async Task<string> EvalStringAsync(TabModel tab, string expression)
            {
                var core = _w._browser.Core(tab);
                if (core == null) return null;
                try
                {
                    var raw = await core.ExecuteScriptAsync(expression);
                    return new JavaScriptSerializer().Deserialize<string>(raw);
                }
                catch (Exception ex) { Line("[eval] " + ex.Message); return null; }
            }

            private async Task CapturePreviewAsync(TabModel tab, string path)
            {
                var core = _w._browser.Core(tab);
                if (core == null) return;
                try
                {
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    using (var stream = File.Create(path))
                        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                }
                catch (Exception ex) { Line("[capture] " + ex.Message); }
            }

            private static bool H(Dictionary<string, object> state, string key)
            {
                object value;
                return state.TryGetValue(key, out value) && value != null;
            }

            private static string S(Dictionary<string, object> state, string key)
            {
                object value;
                if (!state.TryGetValue(key, out value) || value == null) return null;
                return Convert.ToString(value);
            }

            private static bool B(Dictionary<string, object> state, string key)
            {
                object value;
                if (!state.TryGetValue(key, out value) || value == null) return false;
                try { return Convert.ToBoolean(value); } catch { return false; }
            }

            private static int I(Dictionary<string, object> state, string key)
            {
                object value;
                if (!state.TryGetValue(key, out value) || value == null) return int.MinValue;
                try { return Convert.ToInt32(value); } catch { return int.MinValue; }
            }

            private static string Dump(Dictionary<string, object> state)
            {
                if (state == null || state.Count == 0) return "{}";
                return "{" + string.Join(", ", state.Select(p => p.Key + "=" +
                    (p.Value == null ? "null" : p.Value.ToString()))) + "}";
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

                        if (path.StartsWith("/echo", StringComparison.OrdinalIgnoreCase))
                        {
                            var tag = "none";
                            var index = path.IndexOf("tag=", StringComparison.OrdinalIgnoreCase);
                            if (index >= 0)
                            {
                                tag = path.Substring(index + 4);
                                var amp = tag.IndexOf('&');
                                if (amp >= 0) tag = tag.Substring(0, amp);
                            }
                            Write(stream, "200 OK", "application/json; charset=utf-8",
                                "{\"ok\":true,\"tag\":\"" + tag + "\"}");
                            return;
                        }

                        // 备份恢复链路：模拟 pg4-assist 的 __pg4 接口，验证 DDL 自动重建。
                        if (path.StartsWith("/ddl-page", StringComparison.OrdinalIgnoreCase))
                        {
                            Write(stream, "200 OK", "text/html; charset=utf-8", DdlPage());
                            return;
                        }

                        if (path.StartsWith("/page", StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith("/excluded", StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith("/include", StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith("/toggle", StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith("/site", StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith("/nogrant", StringComparison.OrdinalIgnoreCase))
                        {
                            Write(stream, "200 OK", "text/html; charset=utf-8", Page(path));
                            return;
                        }

                        Write(stream, "404 Not Found", "text/plain; charset=utf-8", "not found");
                    }
                    catch (IOException) { }   // 页面/抓图提前断开连接是正常的
                    catch (Exception ex) { Line("[server] " + ex.Message); }
                }
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

            // 只实现链路需要的两个接口：listSnapshots() 做幂等，importDdlText() 重建。
            // 快照放 localStorage，刷新后仍在，能验证重复注入不会重复导入。
            private static string DdlPage()
            {
                return "<!doctype html><html><head><meta charset=\"utf-8\">" +
                       "<title>DDL restore probe</title><script>" +
                       "var KEY='__neDdlSnapshots';window.__ddlCalls=0;" +
                       "function loadSnaps(){try{return JSON.parse(localStorage.getItem(KEY)||'[]');}catch(e){return [];}}" +
                       "function saveSnaps(l){try{localStorage.setItem(KEY,JSON.stringify(l));}catch(e){}}" +
                       "var core={" +
                       "listSnapshots:function(){return Promise.resolve(loadSnaps());}," +
                       "importDdlText:function(text,name,fileName){" +
                       "var l=loadSnaps();l.push({name:name,bytes:text.length,text:text});saveSnaps(l);" +
                       "window.__ddlCalls=(window.__ddlCalls||0)+1;" +
                       "var el=document.getElementById('ddl-status');" +
                       "if(el){el.textContent='imported '+name+' ('+text.length+' B)';}" +
                       "return Promise.resolve({meta:{name:name}});}" +
                       "};window.__pg4={core:core,importDdlText:core.importDdlText};" +
                       "</script></head><body style=\"margin:0;padding:18px;background:#111;" +
                       "color:#eee;font:14px 'Segoe UI',sans-serif\">" +
                       "<h1 style=\"font-size:18px;margin:0 0 12px 0\">NanoEdge · DDL 快照重建链路</h1>" +
                       "<div id=\"ddl-status\" style=\"font-size:16px;padding:8px 0\">waiting…</div>" +
                       "<p style=\"color:#888;font-size:12px\">模拟 pg4-assist 的 __pg4 接口</p>" +
                       "</body></html>";
            }

            private static string Num(string raw)
            {
                return (raw ?? "").Trim('"');
            }

            private static string Page(string path)
            {
                var name = path.TrimStart('/');
                var slash = name.IndexOf('?');
                if (slash >= 0) name = name.Substring(0, slash);
                return "<!doctype html><html><head><meta charset=\"utf-8\">" +
                       "<title>m4 " + name + "</title>" +
                       "<script>window.__m4 = window.__m4 || {}; __m4.pageStart = 1;" +
                       "__m4.pageSawStart = !!__m4.startRan;</script>" +
                       "</head><body style=\"margin:0;padding:18px;background:#111;color:#eee;" +
                       "font:14px 'Segoe UI',sans-serif\">" +
                       "<h1 id=\"m4-title\" style=\"font-size:18px;margin:0 0 10px 0\">NanoEdge M4 · " + name + "</h1>" +
                       "<div id=\"m4-target\" style=\"font-size:20px;padding:8px 0\">target text</div>" +
                       "<p style=\"color:#888;font-size:12px\">local probe page served by 127.0.0.1</p>" +
                       "</body></html>";
            }

            // ---------- 报告 ----------

            private async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
            {
                var started = Environment.TickCount;
                while (Environment.TickCount - started < timeoutMs)
                {
                    if (condition()) return true;
                    await Task.Delay(100);
                }
                return condition();
            }

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
                // HTTP 服务在后台线程上写日志，StringBuilder 必须上锁。
                lock (_reportLock) _report.AppendLine(text);
                Diagnostics.Log("[m4test] " + text);
            }

            private void WriteReport()
            {
                try
                {
                    var path = Path.Combine(_root ?? Path.GetTempPath(), "out", "m4-report.txt");
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.WriteAllText(path, _report.ToString(), Encoding.UTF8);
                }
                catch { }
            }

            private static void WalkVisual(DependencyObject root, Action<DependencyObject> visit)
            {
                if (root == null) return;
                visit(root);
                var count = VisualTreeHelper.GetChildrenCount(root);
                for (var i = 0; i < count; i++)
                    WalkVisual(VisualTreeHelper.GetChild(root, i), visit);
            }

            private static List<TextBlock> TaggedTextBlocks(DependencyObject root, string tag)
            {
                var result = new List<TextBlock>();
                WalkVisual(root, o =>
                {
                    var block = o as TextBlock;
                    if (block != null && Equals(block.Tag, tag)) result.Add(block);
                });
                return result;
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
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
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
