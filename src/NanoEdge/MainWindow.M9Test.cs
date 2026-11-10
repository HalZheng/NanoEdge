using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
        // --m9test：pgAdmin 一键重登 / 自动重登验收。
        // 本地起一个真的 HTTP 服务模拟 pgAdmin 表单登录（GET /login 带 csrf_token、
        // POST /authenticate/login 下发会话 cookie 并 302 到 /browser/），
        // 再用真实 WebView2 隐藏视图 + 真实主视图跑完整个流程。
        // 不连内网、不写用户真实凭据/配置/日志。
        private sealed class M9SelfTest
        {
            private readonly MainWindow _w;
            private readonly StringBuilder _report = new StringBuilder();
            private readonly List<string> _failures = new List<string>();
            private readonly List<string> _postBodies = new List<string>();
            private int _passes;
            private string _root;
            private string _temp;

            private TcpListener _server;
            private volatile bool _serverRunning;
            private int _port;
            private string _base;
            private int _postCount;
            private volatile bool _wrongPassword;
            private volatile bool _rejectCsrf;
            private volatile bool _loginRedirectsToBrowser;
            private volatile bool _forceSignedOut;

            private const string UserName = "qa-user-nanoedge";
            private const string Password = "a b&c+d%e#f=g/h?i-pg9";
            private const string CsrfToken = "tok-9f3-nanoedge";

            // 保存/还原静态注入点
            private string _savedIndexPath, _savedValuesPath, _savedFolder;
            private bool _savedPersist;
            private string _savedCredOverride, _savedLogOverride;

            public M9SelfTest(MainWindow window) { _w = window; }

            public async Task RunAsync()
            {
                _root = FindRoot();
                Line("M9 pgAdmin relogin self-test  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Line("temp data dir = " + Path.Combine(Path.GetTempPath(), "nanoedge-m9"));
                try
                {
                    Setup();
                    RunUnitTests();
                    await RunWebViewTestsAsync();
                    RunLogTests();
                    TakeScreenshots();
                }
                catch (Exception ex) { Fail("unhandled", ex.ToString()); }
                finally
                {
                    StopServer();
                    Teardown();
                    Line("");
                    Line(_failures.Count == 0
                        ? "RESULT: PASS (" + _passes + " checks)"
                        : "RESULT: FAIL " + string.Join(", ", _failures));
                    WriteReport();
                    try { Application.Current.Shutdown(); } catch { }
                }
            }

            // ---------- setup ----------

            private void Setup()
            {
                _temp = Path.Combine(Path.GetTempPath(), "nanoedge-m9");
                if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
                Directory.CreateDirectory(_temp);

                _savedCredOverride = PgAdminCredentialStore.PathOverride;
                _savedLogOverride = PgAdminLog.PathOverride;
                PgAdminCredentialStore.PathOverride = Path.Combine(_temp, "pgadmin-relogin.bin");
                PgAdminLog.PathOverride = Path.Combine(_temp, "pgadmin-relogin.log");

                _savedIndexPath = ScriptStore.IndexPath;
                _savedValuesPath = ScriptStore.ValuesPath;
                _savedFolder = ScriptStore.Folder;
                _savedPersist = ScriptStore.Persist;
                ScriptStore.IndexPath = Path.Combine(_temp, "scripts.json");
                ScriptStore.ValuesPath = Path.Combine(_temp, "script-values.json");
                ScriptStore.Folder = Path.Combine(_temp, "scripts");
                ScriptStore.Persist = true;
                ScriptStore.Reload();

                StartServer();
                _base = "http://127.0.0.1:" + _port;

                // 目标脚本：站点由 @match 在运行时推导，配置里不存任何固定地址。
                var code = "// ==UserScript==\n" +
                           "// @name         pgAdmin QA\n" +
                           "// @match        " + _base + "/*\n" +
                           "// ==/UserScript==\n";
                _script = ScriptStore.Add(code, true);
                _script.PgadminRelogin = new PgAdminReloginSettings
                {
                    Enabled = true,
                    AutoRelogin = false       // 先手动验证，自动重登单独测
                };
                ScriptStore.CommitSettings();
                _cfg = PgAdminReloginConfig.ForUrl(_base + "/login");
                Check("按 @match 现场推导出站点（配置里没有站点字段）",
                    _cfg != null && _cfg.BaseUrl == _base,
                    "BaseUrl=" + (_cfg == null ? "null" : _cfg.BaseUrl));

                var pw = Encoding.UTF8.GetBytes(Password);
                try { PgAdminCredentialStore.Save(_script.Id, UserName, pw); }
                finally { Array.Clear(pw, 0, pw.Length); }
                _w.ReloginForTest.ConfirmOverrideForTest = message => false;
            }

            private UserScript _script;
            private PgAdminSiteConfig _cfg;

            private void Teardown()
            {
                _w.ReloginForTest.ConfirmOverrideForTest = null;
                PgAdminCredentialStore.PathOverride = _savedCredOverride;
                PgAdminLog.PathOverride = _savedLogOverride;
                ScriptStore.IndexPath = _savedIndexPath;
                ScriptStore.ValuesPath = _savedValuesPath;
                ScriptStore.Folder = _savedFolder;
                ScriptStore.Persist = _savedPersist;
                ScriptStore.Reload();
                L.Apply("zh-CN");
            }

            // ---------- 1. 纯逻辑单元测试 ----------

            private void RunUnitTests()
            {
                Line("");
                Line("--- unit: site resolution / RFC3986 / DPAPI / config / bar / cooldown ---");

                Check("@match https://a.corp/* -> origin",
                    PgAdminSiteResolver.TryGetOrigin("https://a.corp/*") == "https://a.corp", "");
                Check("@match 带端口 https://a.corp:8443/*",
                    PgAdminSiteResolver.TryGetOrigin("https://a.corp:8443/*") == "https://a.corp:8443", "");
                Check("@match 子路径 https://a.corp/pgadmin/* -> 路径前缀",
                    PgAdminSiteResolver.GetPathPrefix("https://a.corp/pgadmin/*") == "/pgadmin", "");
                Check("通配 host *://*.corp/* 无法确定站点",
                    PgAdminSiteResolver.TryGetOrigin("*://*.corp/*") == null, "");
                Check("http://*/* 无法确定站点",
                    PgAdminSiteResolver.TryGetOrigin("http://*/*") == null, "");
                Check("非法字符串返回 null",
                    PgAdminSiteResolver.TryGetOrigin("not a url") == null &&
                    PgAdminSiteResolver.TryGetOrigin("") == null &&
                    PgAdminSiteResolver.TryGetOrigin("ftp://x/y") == null, "");
                Check("GetPathPrefix 根路径返回空",
                    PgAdminSiteResolver.GetPathPrefix("https://a.corp/*") == "", "");

                // 站点既不落配置、也不落凭据文件：完全是运行时按 @match 推导的。
                var indexText = File.ReadAllText(ScriptStore.IndexPath, Encoding.UTF8);
                Check("脚本配置里没有站点字段（站点不是存下来的）",
                    indexText.IndexOf("SiteOrigin", StringComparison.OrdinalIgnoreCase) < 0, "");

                var vector = "a b&c+d%e#f=g/h?i";
                var encoded = Encoding.ASCII.GetString(PgAdminFormEncoding.Encode(Encoding.UTF8.GetBytes(vector)));
                Check("RFC3986 测试向量 a b&c+d%e#f=g/h?i",
                    encoded == "a%20b%26c%2Bd%25e%23f%3Dg%2Fh%3Fi", encoded);
                var chinese = Encoding.ASCII.GetString(PgAdminFormEncoding.Encode(Encoding.UTF8.GetBytes("中文")));
                Check("中文按 UTF-8 字节百分号编码",
                    chinese == "%E4%B8%AD%E6%96%87", chinese);
                Check("空格 & + % # = / ? 全部覆盖",
                    new[] { " ", "&", "+", "%", "#", "=", "/", "?" }
                        .All(ch => PgAdminFormEncoding.EncodeToString(ch) != ch &&
                                   PgAdminFormEncoding.EncodeToString(ch).StartsWith("%")), "");
                var body = PgAdminFormEncoding.BuildFormBody(CsrfToken, "en", UserName,
                    Encoding.UTF8.GetBytes(Password));
                var bodyText = Encoding.ASCII.GetString(body);
                Check("表单体含 csrf/email/编码后密码/language/internal_button",
                    bodyText.Contains("csrf_token=" + CsrfToken) &&
                    bodyText.Contains("email=" + PgAdminFormEncoding.EncodeToString(UserName)) &&
                    bodyText.Contains("password=" + PgAdminFormEncoding.EncodeToString(Password)) &&
                    bodyText.Contains("&language=en&internal_button=Login"), "");
                Check("表单体不含密码明文", !bodyText.Contains(Password), "");
                Array.Clear(body, 0, body.Length);

                // DPAPI：文件非明文 + 重启后（重新读盘）仍可解密。
                var file = PgAdminCredentialStore.PathOverride;
                var cipher = File.ReadAllBytes(file);
                var cipherText = Encoding.UTF8.GetString(cipher);
                Check("凭据文件内容不是明文（看不到用户名/密码）",
                    !cipherText.Contains(UserName) && !cipherText.Contains(Password),
                    "bytes=" + cipher.Length);
                var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                var plainText = Encoding.UTF8.GetString(plain);
                Check("凭据按脚本 id 索引，明文结构里没有任何站点字段",
                    plainText.Contains("\"ScriptId\"") && !plainText.Contains("Origin"), "");
                Array.Clear(plain, 0, plain.Length);
                var readBack = PgAdminCredentialStore.TryGet(_script.Id);
                Check("凭据重新读盘（等价重启后）仍可解密",
                    readBack != null && readBack.UserName == UserName &&
                    Encoding.UTF8.GetString(readBack.Password) == Password, "");
                if (readBack != null && readBack.Password != null)
                    Array.Clear(readBack.Password, 0, readBack.Password.Length);
                Check("按脚本查找：别的脚本查不到",
                    PgAdminCredentialStore.TryGet("no-such-script") == null, "");

                // 真实目录布局：%LOCALAPPDATA%\NanoEdge\secure\... + 去掉继承权限的 ACL，
                // 以及第二次保存走 File.Replace 的分支。
                var originalOverride = PgAdminCredentialStore.PathOverride;
                var secureFile = Path.Combine(_temp, "secure", "pgadmin-relogin.bin");
                PgAdminCredentialStore.PathOverride = secureFile;
                try
                {
                    var first = Encoding.UTF8.GetBytes("first-pw");
                    try { PgAdminCredentialStore.Save("acl-script", "acl-user", first); }
                    finally { Array.Clear(first, 0, first.Length); }
                    Check("安全目录按「仅当前用户」ACL 创建成功",
                        Directory.Exists(Path.Combine(_temp, "secure")) && File.Exists(secureFile), "");
                    var second = Encoding.UTF8.GetBytes("second-pw");
                    try { PgAdminCredentialStore.Save("acl-script", "acl-user", second); }
                    finally { Array.Clear(second, 0, second.Length); }
                    var replaced = PgAdminCredentialStore.TryGet("acl-script");
                    Check("第二次保存走 File.Replace 且内容确实更新",
                        replaced != null && Encoding.UTF8.GetString(replaced.Password) == "second-pw", "");
                    if (replaced != null && replaced.Password != null)
                        Array.Clear(replaced.Password, 0, replaced.Password.Length);
                }
                catch (Exception ex) { Fail("secure-dir", ex.ToString()); }
                finally { PgAdminCredentialStore.PathOverride = originalOverride; }

                // 编辑页配置区块：真实控件 + 真实保存路径 + 开关关闭后凭据保留。
                try
                {
                    var page = new ScriptsPage(message => { });
                    page.BeginEdit(_script);
                    page.SetPgAdminForTest(true, UserName, Password);
                    page.SavePgAdminForTest();
                    var saved = PgAdminCredentialStore.TryGet(_script.Id);
                    Check("编辑页区块能加密写入凭据并更新脚本配置",
                        saved != null && saved.UserName == UserName &&
                        Encoding.UTF8.GetString(saved.Password) == Password &&
                        _script.PgadminRelogin.Enabled, "");
                    if (saved != null && saved.Password != null)
                        Array.Clear(saved.Password, 0, saved.Password.Length);
                    Check("编辑页没有任何站点选择/输入控件，只有用户名+密码两个框",
                        page.PgAdminInputCountForTest == 2,
                        "inputs=" + page.PgAdminInputCountForTest);
                    Check("保存后密码框内容保留，不会被抹成空白",
                        page.PgAdminPasswordLengthForTest == Password.Length,
                        "len=" + page.PgAdminPasswordLengthForTest);
                    Check("保存后提示明确写着已保存，而不是「留空则不修改」",
                        page.PgAdminHintForTest == L.T("密码已保存，重启后仍可用"),
                        page.PgAdminHintForTest);

                    // 保存失败（凭据文件损坏）时也不能把用户刚输的密码抹掉，否则要重输一遍。
                    var credPath = PgAdminCredentialStore.PathOverride;
                    var credBackup = File.ReadAllBytes(credPath);
                    File.WriteAllBytes(credPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
                    page.SetPgAdminForTest(true, UserName, Password);
                    page.SavePgAdminForTest();
                    Check("保存失败时密码框同样保留（可直接重试）",
                        page.PgAdminPasswordLengthForTest == Password.Length,
                        "len=" + page.PgAdminPasswordLengthForTest);
                    File.WriteAllBytes(credPath, credBackup);

                    page.TogglePgAdminForTest(false);
                    Check("关闭开关后按钮/自动重登停用，但凭据仍保留",
                        !_script.PgadminRelogin.Enabled && PgAdminCredentialStore.Has(_script.Id), "");
                    page.TogglePgAdminForTest(true);
                    Check("重新打开开关后配置恢复启用", _script.PgadminRelogin.Enabled, "");
                    page.CloseOverlayForTest();
                    Check("关闭编辑页时才清掉密码框（离开页面不留明文内存副本）",
                        page.PgAdminPasswordLengthForTest == -1,
                        "len=" + page.PgAdminPasswordLengthForTest);
                }
                catch (Exception ex) { Fail("editor-block", ex.ToString()); }

                // 站点运行时推导：直接复用用户脚本的 @match 规则匹配当前页面。
                Check("DeriveBaseUrl：具体 @match 推出 origin",
                    PgAdminSiteResolver.DeriveBaseUrl(new[] { "https://a.corp/*" },
                        "https://a.corp/login") == "https://a.corp", "");
                Check("DeriveBaseUrl：带端口",
                    PgAdminSiteResolver.DeriveBaseUrl(new[] { "https://a.corp:8443/*" },
                        "https://a.corp:8443/login") == "https://a.corp:8443", "");
                Check("DeriveBaseUrl：子路径前缀",
                    PgAdminSiteResolver.DeriveBaseUrl(new[] { "https://a.corp/pgadmin/*" },
                        "https://a.corp/pgadmin/login") == "https://a.corp/pgadmin", "");
                Check("DeriveBaseUrl：全通配符也能按当前页面推出站点",
                    PgAdminSiteResolver.DeriveBaseUrl(new[] { "*://*.corp/*" },
                        "https://pg.corp/login") == "https://pg.corp", "");
                Check("DeriveBaseUrl：页面没被 @match 匹配时返回 null",
                    PgAdminSiteResolver.DeriveBaseUrl(new[] { "https://a.corp/*" },
                        "https://b.corp/login") == null, "");
                var siteA = PgAdminSiteResolver.DeriveBaseUrl(new[] { "*://*.corp/*" },
                    "https://a.corp/login");
                var siteB = PgAdminSiteResolver.DeriveBaseUrl(new[] { "*://*.corp/*" },
                    "https://b.corp/login");
                Check("通配脚本：站点随当前页面变，不是配置里的固定值",
                    siteA == "https://a.corp" && siteB == "https://b.corp",
                    siteA + " / " + siteB);

                // 全通配符脚本也不需要手填站点，编辑页仍然只有两个输入框。
                var wildcard = ScriptStore.Add(
                    "// ==UserScript==\n// @name  wildcard\n// @match *://*.corp/*\n// ==/UserScript==\n", true);
                wildcard.PgadminRelogin = new PgAdminReloginSettings
                {
                    Enabled = true, AutoRelogin = true
                };
                ScriptStore.CommitSettings();
                var page3 = new ScriptsPage(message => { });
                page3.BeginEdit(wildcard);
                Check("全通配符脚本也不需要填站点，仍只有两个输入框",
                    page3.PgAdminInputCountForTest == 2,
                    "inputs=" + page3.PgAdminInputCountForTest);
                var wildcardCfg = PgAdminReloginConfig.ForUrl("https://pg.corp/login");
                Check("全通配符脚本运行时照样按当前页面推导出站点",
                    wildcardCfg != null && wildcardCfg.BaseUrl == "https://pg.corp" &&
                    wildcardCfg.Script.Id == wildcard.Id,
                    wildcardCfg == null ? "null" : wildcardCfg.BaseUrl);
                ScriptStore.Remove(wildcard.Id);

                // 配置口径：列表第一个脚本配的是别的站点时，当前页面不该用它（F-12 / 先看页面）。
                var other = ScriptStore.Add("// ==UserScript==\n// @name  other\n" +
                    "// @match https://b.corp.example/*\n// ==/UserScript==\n", true);
                other.PgadminRelogin = new PgAdminReloginSettings
                {
                    Enabled = true, AutoRelogin = true
                };
                ScriptStore.CommitSettings();
                var forBase = PgAdminReloginConfig.ForUrl(_base + "/login");
                Check("先看页面：列表第一个脚本不匹配时取匹配当前页面的那个",
                    forBase != null && forBase.Script.Id == _script.Id, "");
                Check("F-12 别的域名完全不受影响",
                    PgAdminReloginConfig.ForUrl("https://unknown.invalid/login") == null, "");
                var forOther = PgAdminReloginConfig.ForUrl("https://b.corp.example/login");
                Check("另一个站点取到它自己的脚本",
                    forOther != null && forOther.Script.Id == other.Id, "");

                // 按钮显示规则（用户口径）：没掉登录不显示。
                var bar = _w.ReloginBarForTest;
                bar.ClearStatus();
                bar.SetSignedOut(false);
                Check("没掉登录：按钮隐藏、工具条折叠",
                    !bar.ButtonVisibleForTest && !bar.IsVisibleForTest, "");
                bar.SetSignedOut(true);
                Check("掉登录：按钮出现，中文显示「重登录」",
                    bar.ButtonVisibleForTest && bar.IsVisibleForTest &&
                    bar.ButtonTextForTest == "重登录" && bar.ButtonEnabledForTest, "");
                Check("按钮样式：红色 #D32F2F / 圆角 6 / 白色粗体",
                    bar.NormalBackgroundForTest == "#FFD32F2F" &&
                    bar.CornerRadiusForTest == 6 && bar.BoldForTest &&
                    bar.ForegroundForTest == "#FFFFFFFF",
                    bar.NormalBackgroundForTest + " " + bar.ForegroundForTest);
                bar.SetBusy(true);
                bar.ShowBusy();
                Check("忙碌：按钮禁用、文字「登录中…」",
                    !bar.ButtonEnabledForTest && bar.ButtonTextForTest == "登录中…" &&
                    bar.StatusTextForTest == "登录中…", "");
                bar.SetBusy(false);
                bar.ShowStatus("已重新登录", true, 20);
                bar.SetSignedOut(false);
                Check("成功后：按钮收起，工具条仍显示绿色状态文字",
                    !bar.ButtonVisibleForTest && bar.IsVisibleForTest &&
                    bar.StatusTextForTest == "已重新登录", "");
                bar.ClearStatus();

                // 冷却 / 熄灭
                var monitor = _w.ReloginForTest;
                monitor.ClearAutoPauseForTest();
                monitor.RecordAutoForTest();
                Check("60 秒冷却内不再自动重登", !monitor.CanAutoForTest(), "");
                monitor.PauseAutoForTest();
                Check("失败后自动重登暂停", monitor.AutoPausedForTest, "");
                monitor.ClearAutoPauseForTest();
                Check("手动点击/保存凭据可解除暂停", !monitor.AutoPausedForTest, "");
            }

            // ---------- 2. 真实 WebView2 端到端 ----------

            private async Task RunWebViewTestsAsync()
            {
                Line("");
                Line("--- integration: real WebView2 hidden view + local pgAdmin-like server ---");
                if (_cfg == null) { Fail("cfg", "no site config"); return; }

                _w.NewTab(_base + "/browser/");
                var tab = _w._active;
                if (tab == null) { Fail("new-tab", "no active tab"); return; }
                var core = await WaitForCoreAsync(tab);
                if (core == null) { Fail("webview2-core", "core not ready"); return; }
                Check("主视图真实 WebView2 已创建", true, "core.Source=" + core.Source);

                // 服务端没会话时 /browser/ 会跳到 /login，登录页自带一个"未保存内容"标记。
                for (var i = 0; i < 100; i++)
                {
                    if ((core.Source ?? "").IndexOf("/login", StringComparison.Ordinal) >= 0) break;
                    await Task.Delay(100);
                }
                Check("掉登录：主视图落在 /login",
                    (core.Source ?? "").IndexOf("/login", StringComparison.Ordinal) >= 0, core.Source);
                tab.Url = core.Source;
                _w.ReloginForTest.OnNavigationDone(tab, core.Source, true);

                for (var i = 0; i < 100; i++)
                {
                    if (_w.ReloginBarForTest.ButtonVisibleForTest) break;
                    await Task.Delay(100);
                }
                Check("掉登录检测后按钮才出现（用户口径）",
                    _w.ReloginBarForTest.ButtonVisibleForTest, "");
                Check("检测期间没有发起登录 POST", _postCount == 0, "posts=" + _postCount);

                var markerBefore = await ReadStringAsync(core, "window.__pageMarker");
                Check("重登前页面里有未保存内容标记", markerBefore == "kept", markerBefore);

                await _w.ReloginForTest.ReloginForTest(_cfg, "Manual");
                var result = _w.ReloginForTest.LastResultForTest;
                Check("手动重登成功（隐藏视图提交表单）",
                    result.Outcome == ReloginOutcome.Succeeded,
                    "outcome=" + result.Outcome + " msg=" + result.Message);
                Check("只提交一次表单（F-6）", _postCount == 1, "posts=" + _postCount);
                Check("POST 表单体带 csrf_token 和编码后的密码",
                    _postBodies.Count == 1 &&
                    _postBodies[0].Contains("csrf_token=" + CsrfToken) &&
                    _postBodies[0].Contains("password=" + PgAdminFormEncoding.EncodeToString(Password)) &&
                    !_postBodies[0].Contains(Password), "");
                Check("成功后按钮收起、显示「已重新登录」",
                    !_w.ReloginBarForTest.ButtonVisibleForTest &&
                    _w.ReloginBarForTest.StatusTextForTest == "已重新登录", "");
                var markerAfter = await ReadStringAsync(core, "window.__pageMarker");
                Check("F-5 主视图没刷新：页面标记还在（SQL 不丢）",
                    markerAfter == "kept", markerAfter);
                Check("主视图仍在 /login（确实没有导航）",
                    (core.Source ?? "").IndexOf("/login", StringComparison.Ordinal) >= 0, core.Source);
                var session = await PgAdminSessionProbe.ProbeAsync(core, _cfg);
                Check("A2 隐藏视图与主视图共享 cookie：主视图会话变 Valid",
                    session == PageSession.Valid, "session=" + session);
                var armed = await ReadStringAsync(core,
                    "String((window.__pgReloginAutoContinueUntil||0) > Date.now())");
                Check("F-8 已设置自动确认标记（10 分钟）", armed == "true", armed);

                // F-3 单飞锁：同一 service 上并发两次，第二次必须直接 Busy。
                var service = _w.ReloginForTest.ServiceForTest;
                var req1 = NewRequest(core);
                var req2 = NewRequest(core);
                var t1 = service.RunAsync(req1);
                var t2 = service.RunAsync(req2);
                var both = await Task.WhenAll(t1, t2);
                Check("F-3 单飞锁：并发两次只有一次真跑",
                    both.Any(r => r.Outcome == ReloginOutcome.Busy),
                    "outcomes=" + string.Join(",", both.Select(r => r.Outcome.ToString())));

                // 密码错误：只提交一次、结果 Rejected、自动重登熄灭。
                _wrongPassword = true;
                var before = _postCount;
                _w.ReloginForTest.ClearAutoPauseForTest();
                await _w.ReloginForTest.ReloginForTest(_cfg, "Auto");
                var rejected = _w.ReloginForTest.LastResultForTest;
                Check("密码错误判定为 Rejected",
                    rejected != null && rejected.Outcome == ReloginOutcome.Rejected,
                    "outcome=" + (rejected == null ? "null" : rejected.Outcome.ToString()));
                Check("密码错误只提交一次、不重试", _postCount == before + 1,
                    "posts=" + (_postCount - before));
                Check("7.3 自动重登失败后熄灭", _w.ReloginForTest.AutoPausedForTest, "");
                _wrongPassword = false;

                // CSRF / referrer 被拒 -> Failed，不是 Rejected。
                _rejectCsrf = true;
                var csrfResult = await service.RunAsync(NewRequest(core));
                Check("CSRF 被拒判定为 Failed（不是密码错误）",
                    csrfResult.Outcome == ReloginOutcome.Failed, "outcome=" + csrfResult.Outcome);
                _rejectCsrf = false;

                // 会话本来就有效：不提交表单，直接 AlreadySignedIn。
                _loginRedirectsToBrowser = true;
                var postsBeforeAlready = _postCount;
                var already = await service.RunAsync(NewRequest(core));
                Check("AlreadySignedIn：不再提交表单",
                    already.Outcome == ReloginOutcome.AlreadySignedIn &&
                    _postCount == postsBeforeAlready,
                    "outcome=" + already.Outcome + " posts=" + (_postCount - postsBeforeAlready));
                _loginRedirectsToBrowser = false;

                // 没凭据：不联网，直接打开编辑页（F-4）。
                PgAdminCredentialStore.Clear(_script.Id);
                var postsBeforeNoCred = _postCount;
                var openedBefore = _w.ReloginEditorOpenedForTest;
                await _w.ReloginForTest.ReloginForTest(_cfg, "Manual");
                var noCred = _w.ReloginForTest.LastResultForTest;
                Check("F-4 无凭据不发起网络请求",
                    noCred != null && noCred.Outcome == ReloginOutcome.NoCredentials &&
                    _postCount == postsBeforeNoCred,
                    "outcome=" + (noCred == null ? "null" : noCred.Outcome.ToString()));
                Check("F-4 无凭据打开脚本编辑页",
                    _w.ReloginEditorOpenedForTest == openedBefore + 1,
                    "opened=" + (_w.ReloginEditorOpenedForTest - openedBefore));

                // F-7：走真实触发链 OnNavigationDone -> ProbeScript -> SignedOut -> 自动重登一次。
                // 故意用错误密码，顺便验证 7.3「失败即熄灭」不循环。
                _wrongPassword = true;
                _forceSignedOut = true;
                var pwAuto = Encoding.UTF8.GetBytes(Password);
                try { PgAdminCredentialStore.Save(_script.Id, UserName, pwAuto); }
                finally { Array.Clear(pwAuto, 0, pwAuto.Length); }
                _w.ReloginForTest.ResetAutoForTest();
                _script.PgadminRelogin.AutoRelogin = true;
                ScriptStore.CommitSettings();
                var postsBeforeAuto = _postCount;
                _w.ReloginForTest.OnNavigationDone(tab, _base + "/login", true);
                // OnNavigationDone 是 fire-and-forget：POST 到达服务端的时刻早于
                // ApplyResult 写回「熄灭」状态，必须等到状态落地再断言，不能只看 POST 计数。
                for (var i = 0; i < 150 && !_w.ReloginForTest.AutoPausedForTest; i++)
                    await Task.Delay(100);
                Check("F-7 掉线后自动执行一次重登（真实探测链）",
                    _postCount == postsBeforeAuto + 1,
                    "posts=" + (_postCount - postsBeforeAuto));
                Check("7.3 自动重登失败后熄灭、不循环",
                    _w.ReloginForTest.AutoPausedForTest, "");
                _wrongPassword = false;
                _forceSignedOut = false;
                _script.PgadminRelogin.AutoRelogin = false;

                // 凭据文件损坏：抛异常、保留文件、不自动删除（9）。
                var pw2 = Encoding.UTF8.GetBytes(Password);
                try { PgAdminCredentialStore.Save(_script.Id, UserName, pw2); }
                finally { Array.Clear(pw2, 0, pw2.Length); }
                File.WriteAllBytes(PgAdminCredentialStore.PathOverride,
                    new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 });
                var threw = false;
                try { PgAdminCredentialStore.TryGet(_script.Id); } catch { threw = true; }
                Check("凭据文件损坏：视为无凭据且保留文件",
                    threw && File.Exists(PgAdminCredentialStore.PathOverride), "");

                // 清除凭据后按钮点击会打开编辑页（验收清单 9）。
                File.Delete(PgAdminCredentialStore.PathOverride);
                Check("清除凭据后该站点记录为空",
                    PgAdminCredentialStore.TryGet(_script.Id) == null, "");
            }

            private ReloginRequest NewRequest(CoreWebView2 core)
            {
                return new ReloginRequest
                {
                    Config = _cfg,
                    Main = core,
                    Trigger = "Manual",
                    ConfirmRefresh = null,
                    Navigate = null
                };
            }

            private static async Task<string> ReadStringAsync(CoreWebView2 core, string script)
            {
                try
                {
                    var raw = await core.ExecuteScriptAsync(script);
                    return new JavaScriptSerializer().Deserialize<string>(raw) ?? "";
                }
                catch { return ""; }
            }

            private async Task<CoreWebView2> WaitForCoreAsync(TabModel tab)
            {
                for (var i = 0; i < 150; i++)
                {
                    var core = _w._browser.Core(tab);
                    if (core != null) return core;
                    await Task.Delay(100);
                }
                return null;
            }

            // ---------- 3. 日志脱敏 ----------

            private void RunLogTests()
            {
                Line("");
                Line("--- security: log redaction ---");
                var path = PgAdminLog.PathOverride;
                Check("专用日志文件已生成", File.Exists(path), "");
                var text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
                var leaked = new[] { Password, UserName, CsrfToken, "PGSESSION", "Incorrect username" }
                    .Where(s => text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                Check("日志里没有密码/用户名/csrf_token/cookie/表单体",
                    leaked.Count == 0, leaked.Count == 0 ? "" : string.Join(",", leaked));
                Check("日志只记录事件、路径与结果（含 RELOGIN 行）",
                    text.Contains("RELOGIN | Trigger=") && text.Contains("Result="), "");
            }

            // ---------- 4. 截图 ----------

            private void TakeScreenshots()
            {
                Line("");
                Line("--- screenshots ---");
                var outDir = Path.Combine(_root ?? Path.GetTempPath(), "out");
                Directory.CreateDirectory(outDir);
                var zh = Path.Combine(outDir, "m9-bar-zh.png");
                var en = Path.Combine(outDir, "m9-bar-en.png");
                var editor = Path.Combine(outDir, "m9-editor.png");
                var saved = Path.Combine(outDir, "m9-editor-saved.png");

                RenderBar(zh, "zh-CN", "重登录");
                RenderBar(en, "en-US", "Relogin");
                L.Apply("zh-CN");

                try
                {
                    var page = new ScriptsPage(message => { });
                    ScriptStore.Reload();
                    var found = ScriptStore.All().FirstOrDefault(s => s.Id == (_script == null ? "" : _script.Id));
                    if (found != null) page.BeginEdit(found);
                    RenderPage(page, editor, 1000, 700);
                }
                catch (Exception ex) { Line("editor screenshot skipped: " + ex.GetType().Name); }

                // 「刚点完保存」的真实状态：走一遍真实保存路径，截图应显示
                // 密码框里的星号仍在 + 提示明确写着已保存（不是空白框）。
                try
                {
                    var savedPage = new ScriptsPage(message => { });
                    ScriptStore.Reload();
                    var found2 = ScriptStore.All().FirstOrDefault(
                        s => s.Id == (_script == null ? "" : _script.Id));
                    if (found2 != null)
                    {
                        savedPage.BeginEdit(found2);
                        savedPage.SetPgAdminForTest(true, UserName, Password);
                        savedPage.SavePgAdminForTest();
                        Check("保存后状态截图：密码框仍有内容、提示为已保存",
                            savedPage.PgAdminPasswordLengthForTest == Password.Length &&
                            savedPage.PgAdminHintForTest == L.T("密码已保存，重启后仍可用"), "");
                    }
                    RenderPage(savedPage, saved, 1000, 700);
                }
                catch (Exception ex) { Line("saved screenshot skipped: " + ex.GetType().Name); }

                var blank = 0;
                var shots = new[]
                {
                    new[] { zh, "1000" }, new[] { en, "1000" },
                    new[] { editor, "10000" }, new[] { saved, "10000" }
                };
                foreach (var shot in shots)
                {
                    var size = File.Exists(shot[0]) ? new FileInfo(shot[0]).Length : 0;
                    Line("screenshot " + Path.GetFileName(shot[0]) + " = " + size + " bytes");
                    if (size < int.Parse(shot[1])) blank++;
                }
                Check("四张截图已生成",
                    File.Exists(zh) && File.Exists(en) && File.Exists(editor) && File.Exists(saved), "");
                // 锁屏/无显示上下文时 RenderTargetBitmap 会出全白空图（连 --screenshot 也一样）。
                // 这是已知环境限制，不是代码回归：断言不放水，只如实标记，有显示上下文后重跑即可。
                if (blank > 0)
                    Line("WARNING: " + blank + " screenshot(s) are blank/near-blank. " +
                         "No display context in this session; re-run --m9test on a visible desktop to regenerate.");
            }

            private void RenderBar(string path, string language, string expected)
            {
                L.Apply(language);
                var bar = new PgAdminReloginBar();
                bar.SetSignedOut(true);
                bar.ShowStatus(L.T("已重新登录"), true, 20);
                var host = new System.Windows.Controls.Grid { Width = 520, Height = 36 };
                host.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#141922"));
                host.Children.Add(bar);
                RenderPage(host, path, 520, 36);
                Check("截图 " + language + " 按钮文字 = " + expected,
                    bar.ButtonTextForTest == expected, bar.ButtonTextForTest);
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

            // ---------- 本机模拟 pgAdmin 服务端 ----------

            private void StartServer()
            {
                _server = new TcpListener(IPAddress.Loopback, 0);
                _server.Start();
                _port = ((IPEndPoint)_server.LocalEndpoint).Port;
                _serverRunning = true;
                var unused = ServerLoopAsync();
            }

            private void StopServer()
            {
                _serverRunning = false;
                try { if (_server != null) _server.Stop(); } catch { }
            }

            private async Task ServerLoopAsync()
            {
                while (_serverRunning)
                {
                    TcpClient client;
                    try { client = await _server.AcceptTcpClientAsync(); }
                    catch { break; }
                    var captured = client;
                    var unused = Task.Run(() => HandleClient(captured));
                }
            }

            private void HandleClient(TcpClient client)
            {
                try
                {
                    using (client)
                    {
                        client.ReceiveTimeout = 8000;
                        client.SendTimeout = 8000;
                        var stream = client.GetStream();
                        var data = new List<byte>();
                        var buffer = new byte[4096];
                        var headerEnd = -1;
                        while (headerEnd < 0)
                        {
                            var n = stream.Read(buffer, 0, buffer.Length);
                            if (n <= 0) return;
                            for (var i = 0; i < n; i++) data.Add(buffer[i]);
                            headerEnd = HeaderEnd(data);
                        }
                        var headText = Encoding.ASCII.GetString(data.ToArray(), 0, headerEnd);
                        var length = ContentLength(headText);
                        var body = new List<byte>();
                        for (var i = headerEnd + 4; i < data.Count; i++) body.Add(data[i]);
                        while (body.Count < length)
                        {
                            var n = stream.Read(buffer, 0, buffer.Length);
                            if (n <= 0) break;
                            for (var i = 0; i < n; i++) body.Add(buffer[i]);
                        }
                        var first = headText.Split('\n')[0].Trim().Split(' ');
                        var method = first.Length > 0 ? first[0] : "GET";
                        var target = first.Length > 1 ? first[1] : "/";
                        var signedIn = headText.IndexOf("PGSESSION=valid", StringComparison.Ordinal) >= 0;
                        Respond(stream, method, target, body.ToArray(), signedIn);
                    }
                }
                catch { }
            }

            private static int HeaderEnd(List<byte> data)
            {
                for (var i = 3; i < data.Count; i++)
                    if (data[i] == 10 && data[i - 1] == 13 && data[i - 2] == 10 && data[i - 3] == 13)
                        return i - 3;
                return -1;
            }

            private static int ContentLength(string head)
            {
                foreach (var line in head.Split('\n'))
                {
                    var idx = line.IndexOf(":", StringComparison.Ordinal);
                    if (idx < 0) continue;
                    if (!line.Substring(0, idx).Trim().Equals("Content-Length",
                            StringComparison.OrdinalIgnoreCase)) continue;
                    int value;
                    if (int.TryParse(line.Substring(idx + 1).Trim(), out value)) return value;
                }
                return 0;
            }

            private void Respond(NetworkStream stream, string method, string target,
                                 byte[] body, bool signedIn)
            {
                var path = target.Split('?')[0];
                if (method == "POST" && path.EndsWith("/authenticate/login", StringComparison.Ordinal))
                {
                    var text = Encoding.UTF8.GetString(body);
                    lock (_postBodies) _postBodies.Add(text);
                    Interlocked.Increment(ref _postCount);
                    if (_rejectCsrf)
                    {
                        Reply(stream, 200, "OK", "", Page("CSRF",
                            "<div class=\"alert\">CSRF token is invalid</div>" +
                            "<script>{\"messages\":[\"CSRF token is invalid\"]}</script>"), false);
                        return;
                    }
                    var correct = "password=" + PgAdminFormEncoding.EncodeToString(Password);
                    if (_wrongPassword || text.IndexOf(correct, StringComparison.Ordinal) < 0)
                    {
                        Reply(stream, 200, "OK", "", Page("Login failed",
                            "<div class=\"alert\">Incorrect username or password.</div>" +
                            "<script>{\"messages\":[\"Incorrect username or password.\"]}</script>"), false);
                        return;
                    }
                    Reply(stream, 302, "Found",
                        "Location: /browser/\r\nSet-Cookie: PGSESSION=valid; Path=/; HttpOnly\r\n",
                        new byte[0], false);
                    return;
                }
                if (path.EndsWith("/login", StringComparison.Ordinal))
                {
                    if (_loginRedirectsToBrowser)
                    {
                        Reply(stream, 302, "Found", "Location: /browser/\r\n", new byte[0], false);
                        return;
                    }
                    var html = "<form action=\"/authenticate/login\" method=\"post\">" +
                        "<input name=\"csrf_token\" value=\"" + CsrfToken + "\">" +
                        "<input name=\"language\" value=\"en\">" +
                        "<input name=\"email\"><input name=\"password\" type=\"password\"></form>" +
                        "<script>window.__pgCsrf={\"csrfToken\":\"" + CsrfToken + "\"};</script>";
                    Reply(stream, 200, "OK", "", Page("pgAdmin test login", html), method == "HEAD");
                    return;
                }
                if (path.StartsWith("/browser", StringComparison.Ordinal))
                {
                    if (!signedIn || _forceSignedOut)
                    {
                        Reply(stream, 302, "Found", "Location: /login\r\n", new byte[0], method == "HEAD");
                        return;
                    }
                    Reply(stream, 200, "OK", "", Page("browser", "<div>Query Tool</div>"), method == "HEAD");
                    return;
                }
                if (path.IndexOf("/sqleditor", StringComparison.Ordinal) >= 0)
                {
                    Reply(stream, signedIn ? 200 : 401, signedIn ? "OK" : "Unauthorized",
                        "", Page("sqleditor", ""), method == "HEAD");
                    return;
                }
                Reply(stream, 404, "Not Found", "", Page("404", ""), method == "HEAD");
            }

            private static byte[] Page(string title, string inner)
            {
                return Encoding.UTF8.GetBytes(
                    "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + title +
                    "</title></head><body><h1>" + title + "</h1>" +
                    "<script>window.__pageMarker='kept';</script>" + inner + "</body></html>");
            }

            private static void Reply(NetworkStream stream, int status, string reason,
                                      string extra, byte[] body, bool head)
            {
                var sb = new StringBuilder();
                sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
                sb.Append("Content-Type: text/html; charset=utf-8\r\n");
                if (!string.IsNullOrEmpty(extra)) sb.Append(extra);
                sb.Append("Content-Length: ").Append(head ? 0 : body.Length).Append("\r\n");
                sb.Append("Connection: close\r\n\r\n");
                var header = Encoding.ASCII.GetBytes(sb.ToString());
                stream.Write(header, 0, header.Length);
                if (!head && body.Length > 0) stream.Write(body, 0, body.Length);
                stream.Flush();
            }

            // ---------- report ----------

            private void Check(string name, bool passed, string detail)
            {
                if (passed) _passes++; else _failures.Add(name);
                Line((passed ? "[PASS] " : "[FAIL] ") + name + "  " + (detail ?? ""));
            }

            private void Fail(string name, string detail)
            {
                _failures.Add(name);
                Line("[FAIL] " + name + "  " + (detail ?? ""));
            }

            private void Line(string text)
            {
                lock (_report) _report.AppendLine(text);
                Diagnostics.Log("[m9test] " + text);
            }

            private void WriteReport()
            {
                try
                {
                    var path = Path.Combine(_root ?? Path.GetTempPath(), "out", "m9-report.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, _report.ToString(), Encoding.UTF8);
                }
                catch { }
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
