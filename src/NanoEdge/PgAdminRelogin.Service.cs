using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;

namespace NanoEdge
{
    // =====================================================================
    // 页面侧辅助脚本：只做三件小事，密码永远不进页面。
    //  * 登录页读 csrf_token / language（隐藏视图里执行）
    //  * 主视图同步 XHR 探测会话（不刷新页面，Query Tool 的 SQL 保留）
    //  * 自动点掉 "lost the database connection" 的 Continue、宿主主动导航时放行离开
    // =====================================================================
    internal static class PgAdminReloginScripts
    {
        internal const string ReadForm = @"(() => {
  const form = document.querySelector(""form[action$='/authenticate/login']"");
  const field = (name) => form ? ((form.querySelector('[name=""' + name + '""]') || {}).value || null) : null;
  let token = field('csrf_token');
  if (!token) {
    for (const s of document.scripts) {
      const m = /""csrfToken"":\s*""([^""]+)""/.exec(s.textContent || '');
      if (m) { token = m[1]; break; }
    }
  }
  if (!token) {
    const any = document.querySelector('input[name=""csrf_token""]');
    if (any) token = any.value || null;
  }
  return token ? { token: token, language: field('language') } : null;
})()";

        internal const string ReadMessages = @"(() => {
  const out = [];
  const seen = new Set();
  const add = (s) => { if (s && !seen.has(s)) { seen.add(s); out.push(String(s).slice(0, 200)); } };
  try {
    for (const el of document.querySelectorAll('.alert, [role=""alert""], .flash, .message')) {
      add((el.textContent || '').trim());
    }
    for (const s of document.scripts) {
      const m = /""messages""\s*:\s*(\[[\s\S]*?\])/.exec(s.textContent || '');
      if (m) {
        try { for (const x of JSON.parse(m[1])) add(typeof x === 'string' ? x : (x && x.message)); }
        catch (e) { }
      }
    }
  } catch (e) { }
  return out;
})()";

        private const string ProbeTemplate = @"(() => {
  try {
    if (location.origin !== '__ORIGIN__') return 'other';
    const xhr = new XMLHttpRequest();
    xhr.open('HEAD', '__BASE__/browser/', false);
    xhr.send();
    if (xhr.status < 200 || xhr.status >= 300) return 'status ' + xhr.status;
    return new URL(xhr.responseURL).pathname;
  } catch (e) { return 'error'; }
})()";

        // 与 15.5 的差别：把「允许的站点」和「放行离开」一起装进同一个幂等引导器。
        // 这样既能 AddScriptToExecuteOnDocumentCreated，也能对已打开的文档立刻执行一次，
        // 解决「脚本只影响之后的导航」与 F-5「不刷新」的时序缺口。
        // 站点不再预枚举：宿主重登成功时把「当前页面的 origin」写进标记，
        // 自动确认只在这个页面 + 10 分钟内生效，别的站点天然不受影响（F-12）。
        private const string DocumentTemplate = @"(() => {
  if (!window.__pgReloginLeaveGuardInstalled) {
    window.__pgReloginLeaveGuardInstalled = true;
    window.addEventListener('beforeunload', (event) => {
      let allow = false;
      try { allow = window.top.__pgReloginAllowLeave === true; } catch (e) { }
      if (allow) event.stopImmediatePropagation();
    }, true);
  }
  if (window.__pgReloginAutoContinueInstalled) return;
  window.__pgReloginAutoContinueInstalled = true;
  const phrase = 'lost the database connection';
  const clicked = new WeakSet();
  const armed = () => {
    try {
      return window.top.__pgReloginAutoContinueOrigin === location.origin &&
             (window.top.__pgReloginAutoContinueUntil || 0) > Date.now();
    } catch (e) { return false; }
  };
  const scan = () => {
    if (!armed()) return;
    for (const button of document.querySelectorAll('button')) {
      if (clicked.has(button) || button.disabled) continue;
      if ((button.textContent || '').trim() !== 'Continue') continue;
      let node = button, found = false;
      for (let depth = 0; node && depth < 6; node = node.parentElement, depth++) {
        if ((node.textContent || '').indexOf(phrase) >= 0) { found = true; break; }
      }
      if (!found) continue;
      clicked.add(button);
      button.click();
    }
  };
  new MutationObserver(scan).observe(document.documentElement, { childList: true, subtree: true });
})()";

        public static string Probe(PgAdminSiteConfig cfg)
        {
            return ProbeTemplate.Replace("__ORIGIN__", cfg.Origin.ToLowerInvariant())
                                .Replace("__BASE__", cfg.BaseUrl);
        }

        public static string Document()
        {
            return DocumentTemplate;
        }
    }

    internal enum SignInOutcome { Succeeded, AlreadySignedIn, Rejected, Failed }
    internal enum PageSession { Valid, SignedOut, Unknown }

    internal sealed class SignInResult
    {
        public SignInOutcome Outcome;
        public string Message = "";
    }

    internal sealed class NavResult
    {
        public string Path;
        public bool ReachedBrowser;
        public string Error;
    }

    // ---------- 6.2 隐藏视图里的表单提交 ----------
    internal static class PgAdminLogin
    {
        public static async Task<SignInResult> SubmitAsync(CoreWebView2 core, PgAdminSiteConfig cfg,
                                                          string userName, byte[] password)
        {
            var loginNav = await NavigateAsync(core, cfg, () => core.Navigate(cfg.LoginUrl), false);
            PgAdminLog.Stage("get-login", loginNav.Path ?? "-");
            if (loginNav.ReachedBrowser) return Result(SignInOutcome.AlreadySignedIn, "");
            if (loginNav.Path == null ||
                !loginNav.Path.StartsWith(cfg.LoginPath, StringComparison.OrdinalIgnoreCase))
                return Result(SignInOutcome.Failed, L.T("网络错误，请检查连接"));

            var form = await ReadLoginFormAsync(core);
            if (form == null) return Result(SignInOutcome.Failed, L.T("未找到 pgAdmin 登录表单"));

            var token = Convert.ToString(form["token"]);
            var language = form.ContainsKey("language") ? Convert.ToString(form["language"]) : "";
            var body = PgAdminFormEncoding.BuildFormBody(token, language, userName, password);
            var stream = new MemoryStream(body, false);
            try
            {
                var request = core.Environment.CreateWebResourceRequest(
                    cfg.AuthUrl, "POST", stream,
                    "Content-Type: application/x-www-form-urlencoded\r\nReferer: " + cfg.LoginUrl + "\r\n");
                var post = await NavigateAsync(core, cfg,
                    () => core.NavigateWithWebResourceRequest(request), true);
                PgAdminLog.Stage("post-login", post.Path ?? "-");

                if (post.ReachedBrowser) return Result(SignInOutcome.Succeeded, "");
                if (post.Path == null) return Result(SignInOutcome.Failed, L.T("网络错误，请检查连接"));

                var messages = await ReadMessagesAsync(core);
                foreach (var message in messages)
                {
                    if (message.IndexOf("CSRF", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        message.IndexOf("referrer", StringComparison.OrdinalIgnoreCase) >= 0)
                        return Result(SignInOutcome.Failed, L.T("pgAdmin 拒绝了安全令牌"));
                }
                var text = messages.Count > 0
                    ? string.Join(" ", messages)
                    : L.T("用户名或密码错误");
                return Result(SignInOutcome.Rejected, Shorten(text));
            }
            finally
            {
                stream.Dispose();
                Array.Clear(body, 0, body.Length);
            }
        }

        private static async Task<Dictionary<string, object>> ReadLoginFormAsync(CoreWebView2 core)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    var json = await core.ExecuteScriptAsync(PgAdminReloginScripts.ReadForm);
                    var form = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                    if (form != null && form.ContainsKey("token") && form["token"] != null &&
                        !string.IsNullOrEmpty(Convert.ToString(form["token"])))
                    {
                        if (!form.ContainsKey("language") || form["language"] == null)
                            form["language"] = "en";
                        return form;
                    }
                }
                catch (Exception ex) { PgAdminLog.Exception("read-form", ex); }
                await Task.Delay(300);
            }
            return null;
        }

        private static async Task<List<string>> ReadMessagesAsync(CoreWebView2 core)
        {
            try
            {
                var json = await core.ExecuteScriptAsync(PgAdminReloginScripts.ReadMessages);
                var list = new JavaScriptSerializer().Deserialize<List<string>>(json);
                return list ?? new List<string>();
            }
            catch (Exception ex)
            {
                PgAdminLog.Exception("read-messages", ex);
                return new List<string>();
            }
        }

        private static async Task<NavResult> NavigateAsync(CoreWebView2 core, PgAdminSiteConfig cfg,
                                                          Action start, bool stopAtBrowser)
        {
            var done = new TaskCompletionSource<NavResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            ulong? navId = null;

            EventHandler<CoreWebView2NavigationStartingEventArgs> onStarting = (s, e) =>
            {
                if (navId == null && !e.IsRedirected) navId = e.NavigationId;
                if (e.NavigationId != navId || !e.IsRedirected) return;
                var path = PathOf(cfg, e.Uri);
                if (path != null && path.StartsWith(cfg.BrowserPath, StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = stopAtBrowser;
                    done.TrySetResult(new NavResult { Path = path, ReachedBrowser = true });
                }
            };
            EventHandler<CoreWebView2NavigationCompletedEventArgs> onCompleted = (s, e) =>
            {
                if (e.NavigationId != navId) return;
                if (!e.IsSuccess)
                {
                    done.TrySetResult(new NavResult { Error = e.WebErrorStatus.ToString() });
                    return;
                }
                var path = PathOf(cfg, core.Source);
                done.TrySetResult(new NavResult
                {
                    Path = path,
                    ReachedBrowser = path != null &&
                        path.StartsWith(cfg.BrowserPath, StringComparison.OrdinalIgnoreCase),
                    Error = path == null ? "other-origin" : null
                });
            };

            core.NavigationStarting += onStarting;
            core.NavigationCompleted += onCompleted;
            try
            {
                start();
                var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                if (finished != done.Task)
                    return new NavResult { Error = "timed out" };
                return await done.Task;
            }
            finally
            {
                core.NavigationStarting -= onStarting;
                core.NavigationCompleted -= onCompleted;
            }
        }

        private static string PathOf(PgAdminSiteConfig cfg, string url)
        {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return null;
            if (!string.Equals(parsed.GetLeftPart(UriPartial.Authority), cfg.Origin,
                    StringComparison.OrdinalIgnoreCase)) return null;
            return parsed.AbsolutePath;
        }

        private static SignInResult Result(SignInOutcome outcome, string message)
        {
            return new SignInResult { Outcome = outcome, Message = message };
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length > 200 ? text.Substring(0, 200) : text;
        }
    }

    // ---------- 7.2 主视图会话探测（不刷新） ----------
    internal static class PgAdminSessionProbe
    {
        public static async Task<PageSession> ProbeAsync(CoreWebView2 core, PgAdminSiteConfig cfg)
        {
            if (core == null) return PageSession.Unknown;
            try
            {
                var task = core.ExecuteScriptAsync(PgAdminReloginScripts.Probe(cfg));
                if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15))) != task)
                    return PageSession.Unknown;
                var raw = await task;
                var path = new JavaScriptSerializer().Deserialize<string>(raw) ?? "";
                if (path.StartsWith(cfg.BrowserPath, StringComparison.OrdinalIgnoreCase)) return PageSession.Valid;
                if (path.StartsWith(cfg.LoginPath, StringComparison.OrdinalIgnoreCase)) return PageSession.SignedOut;
                return PageSession.Unknown;
            }
            catch (Exception ex)
            {
                PgAdminLog.Exception("probe", ex);
                return PageSession.Unknown;
            }
        }
    }

    // ---------- 6 / F-3 单飞锁 + 隐藏视图生命周期 ----------
    internal enum ReloginOutcome
    {
        Succeeded, AlreadySignedIn, NoCredentials, Corrupt, Rejected, Failed,
        Busy, RefreshCancelled, Refreshed, NoSite
    }

    internal sealed class ReloginResult
    {
        public ReloginOutcome Outcome;
        public string Message = "";
    }

    internal sealed class ReloginRequest
    {
        public PgAdminSiteConfig Config;
        public CoreWebView2 Main;
        public string Trigger = "Manual";        // Manual | Auto
        // 自动触发时传 null：不弹模态框、不擅自刷新（7.4）。
        public Func<string, bool> ConfirmRefresh;
        public Action<CoreWebView2, string> Navigate;
    }

    internal sealed class PgAdminReloginService
    {
        private int _busy;
        private readonly Func<IntPtr> _parentHwnd;
        private readonly Func<Task<CoreWebView2Environment>> _environment;

        public bool IsBusy { get { return Volatile.Read(ref _busy) != 0; } }

        public PgAdminReloginService(Func<IntPtr> parentHwnd,
                                     Func<Task<CoreWebView2Environment>> environment)
        {
            _parentHwnd = parentHwnd;
            _environment = environment;
        }

        public async Task<ReloginResult> RunAsync(ReloginRequest request)
        {
            if (request == null || request.Config == null) return Finish(ReloginOutcome.NoSite, "");
            // F-3：忙则直接返回，流程进行中再次点击无效。
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                return Finish(ReloginOutcome.Busy, "");

            var sw = Stopwatch.StartNew();
            byte[] password = null;
            CoreWebView2Controller controller = null;
            ReloginOutcome outcome = ReloginOutcome.Failed;

            // 单飞锁保证同一时刻只有一个流程，这里的局部状态不会被并发踩到。
            ReloginResult Finish(ReloginOutcome value, string text)
            {
                outcome = value;
                return new ReloginResult { Outcome = value, Message = text };
            }

            try
            {
                PgAdminCredentialStore.Credential credential;
                try
                {
                    credential = PgAdminCredentialStore.TryGet(request.Config.Script.Id);
                }
                catch (Exception ex)
                {
                    // 9：凭据文件损坏 / 解密失败 —— 视为无凭据、保留文件、不自动删除。
                    PgAdminLog.Exception("credential-read", ex);
                    return Finish(ReloginOutcome.Corrupt, L.T("凭据无法读取，请重新保存"));
                }
                if (credential == null)
                    return Finish(ReloginOutcome.NoCredentials, "");

                password = credential.Password;
                var environment = await _environment();
                if (environment == null) return Finish(ReloginOutcome.Failed, L.T("网络错误，请检查连接"));

                // 与主视图同一个 environment（同一用户数据目录 / profile），cookie 才会互通。
                // 主视图没开 InPrivate，这里也不开。
                controller = await CreateHiddenControllerAsync(environment);
                if (controller == null) return Finish(ReloginOutcome.Failed, L.T("网络错误，请检查连接"));

                var signIn = await PgAdminLogin.SubmitAsync(
                    controller.CoreWebView2, request.Config, credential.UserName, password);

                if (signIn.Outcome == SignInOutcome.Rejected)
                    return Finish(ReloginOutcome.Rejected, signIn.Message);
                if (signIn.Outcome == SignInOutcome.Failed)
                    return Finish(ReloginOutcome.Failed, signIn.Message);

                // 8：在「主视图」验证隐藏视图拿到的会话是否真的生效。
                var session = await PgAdminSessionProbe.ProbeAsync(request.Main, request.Config);
                PgAdminLog.Stage("probe", session.ToString());
                if (session == PageSession.Valid)
                {
                    await ArmAutoContinueAsync(request.Main);
                    return Finish(signIn.Outcome == SignInOutcome.AlreadySignedIn
                        ? ReloginOutcome.AlreadySignedIn : ReloginOutcome.Succeeded, "");
                }
                if (session == PageSession.SignedOut)
                {
                    // 6.4：隐藏视图没共享 cookie。只在用户明确同意时刷新；自动触发不打扰用户。
                    if (request.ConfirmRefresh == null || request.Navigate == null)
                        return Finish(ReloginOutcome.RefreshCancelled,
                            L.T("需要刷新页面才能生效（未刷新，未保存的 SQL 已保留）"));
                    if (!request.ConfirmRefresh(
                            L.T("需要刷新页面才能生效，未保存的 SQL 可能丢失。是否继续？")))
                        return Finish(ReloginOutcome.RefreshCancelled, "");

                    await RefreshWithLeaveGuardAsync(request.Main, request.Config, request.Navigate);
                    return Finish(ReloginOutcome.Refreshed, "");
                }
                return Finish(ReloginOutcome.Failed, L.T("会话状态无法确认，页面未刷新"));
            }
            catch (Exception ex)
            {
                PgAdminLog.Exception("relogin", ex);
                return Finish(ReloginOutcome.Failed, L.T("网络错误，请检查连接"));
            }
            finally
            {
                if (password != null) Array.Clear(password, 0, password.Length);
                if (controller != null)
                {
                    try { controller.Close(); } catch { }
                }
                Interlocked.Exchange(ref _busy, 0);
                PgAdminLog.Event(request.Trigger, outcome.ToString(), sw.Elapsed.TotalSeconds);
            }
        }

        private async Task<CoreWebView2Controller> CreateHiddenControllerAsync(
            CoreWebView2Environment environment)
        {
            try
            {
                var options = environment.CreateCoreWebView2ControllerOptions();
                options.IsInPrivateModeEnabled = false;
                var controller = await environment.CreateCoreWebView2ControllerAsync(
                    _parentHwnd(), options);
                controller.IsVisible = false;
                return controller;
            }
            catch (Exception ex)
            {
                PgAdminLog.Exception("hidden-controller", ex);
                return null;
            }
        }

        private static async Task ArmAutoContinueAsync(CoreWebView2 main)
        {
            if (main == null) return;
            try
            {
                // 6.5 + 6.6：立刻在当前文档装一次（幂等），再设 10 分钟标记。
                await main.ExecuteScriptAsync(PgAdminReloginScripts.Document());
                await main.ExecuteScriptAsync(
                    "window.__pgReloginAutoContinueOrigin = location.origin;" +
                    "window.__pgReloginAutoContinueUntil = Date.now() + 600000;");
            }
            catch (Exception ex) { PgAdminLog.Exception("arm-auto-continue", ex); }
        }

        private static async Task RefreshWithLeaveGuardAsync(CoreWebView2 main,
            PgAdminSiteConfig cfg, Action<CoreWebView2, string> navigate)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = (s, e) => done.TrySetResult(true);
            try
            {
                // 当前文档里也要装一次（幂等），否则 beforeunload 守卫可能还没生效。
                await main.ExecuteScriptAsync(PgAdminReloginScripts.Document());
                await main.ExecuteScriptAsync("window.__pgReloginAllowLeave = true;");
                main.NavigationCompleted += handler;
                navigate(main, cfg.BrowserUrl);
                await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            }
            catch (Exception ex) { PgAdminLog.Exception("refresh-fallback", ex); }
            finally
            {
                main.NavigationCompleted -= handler;
                try { await main.ExecuteScriptAsync("window.__pgReloginAllowLeave = false;"); }
                catch { }
            }
        }

    }
}
