using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        // --logintest：登录表单识别与填充的端到端验证。
        // 页面是真的 WebView2 渲染，脚本走 AddScriptToExecuteOnDocumentCreated 真注入，
        // 断言全部落在 DOM 上（读回 input.value、数 input 事件次数），不 mock 任何东西。
        private sealed class LoginSelfTest
        {
            private readonly MainWindow _w;
            private readonly StringBuilder _report = new StringBuilder();
            private readonly List<string> _failures = new List<string>();
            private int _passes;
            private string _root;
            private CoreWebView2 _core;
            private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

            public LoginSelfTest(MainWindow window) { _w = window; }

            public async Task RunAsync()
            {
                _root = FindRoot();
                // 用 --kpxctest=associate 产出的测试配置，不读用户真实的 keepassxc.json
                KeePassBridge.ConfigPath = Path.Combine(_root ?? Path.GetTempPath(), "out", "kpxc-test-config.json");
                SettingsStore.Persist = false;   // 自测改设置但不落盘
                Line("NanoEdge login fill self-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                try { await RunCoreAsync(); await RunLateScanTestAsync(); }
                catch (Exception ex) { Fail("unhandled", ex.ToString()); }
                finally
                {
                    Line("");
                    Line(_failures.Count == 0
                        ? "RESULT: PASS (" + _passes + " checks)"
                        : "RESULT: FAIL " + string.Join(", ", _failures));
                    WriteReport();
                    try { Application.Current.Shutdown(); } catch { }
                }
            }

            private async Task RunCoreAsync()
            {
                var html = @"<!doctype html><html><head><meta charset=""utf-8""><title>login fixture</title>
<style>body{font:14px system-ui;padding:24px}input{display:block;margin:6px 0;padding:6px;width:260px}
.hidden{display:none}</style></head><body>
<h3>1. 标准表单</h3>
<form id=""f1"" action=""/login"">
  <input name=""username"" type=""text"">
  <input name=""password"" type=""password"">
</form>
<h3>2. 受控组件（只有 input 事件会更新 state）</h3>
<form id=""f2"">
  <input id=""c-user"" name=""cuser"" type=""email"">
  <input id=""c-pass"" name=""cpass"" type=""password"">
</form>
<h3>3. 没有用户名框</h3>
<form id=""f3""><input name=""otp"" type=""password""></form>
<h3>4. 隐藏的密码框（不该被扫到）</h3>
<form id=""f4"" class=""hidden""><input name=""ghost"" type=""password""></form>
<h3>5. 同源 iframe</h3>
<iframe id=""fr"" style=""width:320px;height:130px;border:1px solid #999""
 srcdoc=""<body style='margin:8px;font:13px system-ui'><input name='if-user' type='text'><input name='if-pass' type='password'></body>""></iframe>
<script>
window.__events = { user: 0, pass: 0 };
document.getElementById('c-pass').addEventListener('input', function(){ window.__events.pass++; });
document.getElementById('c-user').addEventListener('input', function(){ window.__events.user++; });
window.__lateReady = false;
setTimeout(function(){
  var f = document.createElement('form');
  f.id = 'f5';
  f.innerHTML = '<input name=""late-user"" type=""text""><input name=""late-pass"" type=""password"">';
  document.body.appendChild(f);
  window.__lateReady = true;
}, 700);
</script></body></html>";

                // 测试页伪装成 https://example.com：
                // 一是不用起本地服务器，二是 KeePassXC 里的测试条目就是按这个域名建的，
                // 界面层的取密码流程才走得到真实数据。
                // 不带 URL 的 NewTab 会打开内部起始页（纯 WPF，没有 WebView2）；
                // about:blank 会被 URL 解析器当成搜索词。用一个必然连不上的本地地址
                // 把内核建起来，再注册拦截、指向 example.com。
                _w.NewTab("http://127.0.0.1:1/");
                var tab = _w._active;
                Check(tab != null, "新建标签");
                if (tab == null) return;

                var core = await WaitForCoreAsync(tab);
                Check(core != null, "拿到 CoreWebView2");
                if (core == null) return;
                _core = core;

                // 第一次导航（127.0.0.1:1 必然连不上）还在跑的时候再 Navigate 会被吞掉，
                // 等它收尾再注册拦截、指向 example.com。
                await Task.Delay(1500);

                // 第二个测试页：登录表单在 3 秒后才插入 DOM，专门验证 5 秒兜底扫描。
                // 600ms / 2000ms 那两次必然看不到它，只有 5000ms 那次能扫出来。
                var lateHtml = @"<!doctype html><html><head><meta charset=""utf-8""><title>late fixture</title>
<style>body{font:14px system-ui;padding:24px}input{display:block;margin:6px 0;padding:6px;width:260px}</style>
</head><body><h3>loading…</h3><script>
setTimeout(function(){
  var f = document.createElement('form');
  f.innerHTML = '<input name=""late2-user"" type=""text""><input name=""late2-pass"" type=""password"">';
  document.body.appendChild(f);
  window.__late2Ready = true;
}, 3000);
</script></body></html>";

                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                var intercepted = 0;
                core.WebResourceRequested += (s, e) =>
                {
                    if (!e.Request.Uri.StartsWith("https://example.com", StringComparison.OrdinalIgnoreCase)) return;
                    intercepted++;
                    var body = Encoding.UTF8.GetBytes(
                        e.Request.Uri.StartsWith("https://example.com/late", StringComparison.OrdinalIgnoreCase)
                            ? lateHtml : html);
                    e.Response = core.Environment.CreateWebResourceResponse(
                        new MemoryStream(body), 200, "OK", "Content-Type: text/html; charset=utf-8");
                };
                core.Navigate("https://example.com/");

                await WaitForAsync(core, "window.__lateReady === true", 8000);
                // srcdoc 的 iframe 文档是异步就绪的，等它真有了密码框再扫
                await WaitForAsync(core,
                    "(function(){var f=document.getElementById('fr');" +
                    "return !!(f&&f.contentDocument&&f.contentDocument.querySelector('input[name=if-pass]'));})()",
                    5000);
                var pageTitle = await EvalAsync(core, "document.title");
                var bodyLen = await EvalAsync(core, "document.body ? String(document.body.innerHTML.length) : '-1'");
                Line("拦截次数=" + intercepted + " 标题=" + pageTitle + " body长度=" + bodyLen +
                     " source=" + (core.Source ?? ""));
                Check(pageTitle == "login fixture", "测试页加载完成（https://example.com/ 拦截返回）");

                // 注入是由 BrowserHost.ApplyScriptsAsync 完成的；这里确认脚本真的在页面里
                var installed = await EvalAsync(core, "typeof window.__neLogin === 'object'");
                Check(installed == "true", "注入脚本已安装 window.__neLogin");

                // ---------- 扫描 ----------
                var fields = await LoginFill.ScanAsync(core);
                Line("扫描到密码框 " + fields.Count + " 个");
                foreach (var f in fields)
                    Line("  · index=" + f.index + " user=" + f.userField + " pass=" + f.passField +
                         " action=" + f.formAction + " ready=" + f.ready);

                Check(fields.Count == 5, "隐藏的密码框被跳过，主文档 4 个 + 同源 iframe 1 个（实际 " + fields.Count + "）");

                var f1 = fields.Find(x => x.index == 0);
                Check(f1 != null && f1.userField == "username", "标准表单识别出用户名框 username");
                var f2 = fields.Find(x => x.index == 1);
                Check(f2 != null && f2.userField == "cuser", "受控组件识别出用户名框 cuser");
                var f3 = fields.Find(x => x.index == 2);
                Check(f3 != null && f3.userField == "", "无用户名框时 userField 为空而不是乱抓");
                // 隐藏的密码框虽然不返回，但仍占用 querySelectorAll 的原始索引，
                // 所以动态表单是 index=4 而不是 3。fill 用的是同一套原始索引，能对上。
                var f5 = fields.Find(x => x.index == 4);
                Check(f5 != null && f5.userField == "late-user", "动态渲染出来的表单被扫到");

                // ---------- 填充：标准表单 ----------
                var tricky = "p@ss\"w\\ord<>&'` 中文";
                var ok1 = await LoginFill.FillAsync(core, 0, "alice", tricky);
                Check(ok1, "填充标准表单返回 ok");
                var v1 = await EvalAsync(core,
                    "document.querySelector('#f1 input[name=username]').value");
                var v2 = await EvalAsync(core,
                    "document.querySelector('#f1 input[name=password]').value");
                Check(v1 == "alice", "用户名写入正确（读回 " + v1 + "）");
                Check(v2 == tricky, "密码含引号/反斜杠/尖括号/中文仍逐字一致");

                // ---------- 填充：受控组件 ----------
                var ok2 = await LoginFill.FillAsync(core, 1, "bob", "hunter2");
                Check(ok2, "填充受控组件返回 ok");
                var ev = await EvalAsync(core, "window.__events.user + ',' + window.__events.pass");
                Check(ev == "1,1", "受控组件收到 input 事件（user,pass = " + ev + "）");
                var cv = await EvalAsync(core, "document.getElementById('c-pass').value");
                Check(cv == "hunter2", "受控组件的值真的变了（读回 " + cv + "）");

                // ---------- 填充：动态表单 ----------
                var ok3 = await LoginFill.FillAsync(core, 4, "carol", "latepass");
                Check(ok3, "填充后出现的表单返回 ok");
                var lv = await EvalAsync(core, "document.querySelector('#f5 input[name=late-pass]').value");
                Check(lv == "latepass", "后渲染的密码框也填进去了");

                // ---------- 越界索引 ----------
                var ok4 = await LoginFill.FillAsync(core, 99, "x", "y");
                Check(!ok4, "越界索引返回失败而不是抛异常");

                // ---------- 同源 iframe ----------
                var fIframe = fields.Find(x => x.index == 5);
                Check(fIframe != null && fIframe.userField == "if-user", "同源 iframe 里的表单被扫到");
                var okIframe = await LoginFill.FillAsync(core, 5, "dave", "iframe-pass");
                Check(okIframe, "填充 iframe 表单返回 ok");
                var ifValue = await EvalAsync(core,
                    "document.getElementById('fr').contentDocument.querySelector('input[name=if-pass]').value");
                Check(ifValue == "iframe-pass", "iframe 里的密码框真的填进去了（读回 " + ifValue + "）");

                // ---------- 多表单：焦点优先 ----------
                await core.ExecuteScriptAsync("document.getElementById('c-user').focus()");
                var okFocus = await LoginFill.FillActiveAsync(core, "eve", "focus-pass");
                Check(okFocus, "fillActive 返回 ok");
                var focusValue = await EvalAsync(core, "document.getElementById('c-pass').value");
                Check(focusValue == "focus-pass", "焦点所在的表单被优先填充（读回 " + focusValue + "）");
                var firstUntouched = await EvalAsync(core,
                    "document.querySelector('#f1 input[name=password]').value");
                Check(firstUntouched == tricky, "没抢着填第一个表单（值仍是之前的 " + firstUntouched.Length + " 字符）");

                // ---------- 设置页：显式关联入口（不依赖 KeePassXC 是否已配好） ----------
                // 用一个绝不存在的配置路径，强制页面走「未关联」分支；不碰真实配置。
                var savedConfigPath = KeePassBridge.ConfigPath;
                KeePassBridge.ConfigPath = Path.Combine(_root ?? Path.GetTempPath(), "out",
                    "kpxc-none-" + Guid.NewGuid().ToString("N") + ".json");
                var settingsPage = new SettingsPage(_w._settings);
                var assocButton = settingsPage.AssociateButtonForTest;
                Check(assocButton != null, "设置页未关联时提供「立即关联」按钮");
                Check(assocButton != null && Convert.ToString(assocButton.Content) == L.T("立即关联"),
                      "按钮文案 = " + L.T("立即关联"));
                RenderElement(settingsPage,
                    Path.Combine(_root ?? Path.GetTempPath(), "out", "login-settings-link.png"));

                var savedLanguage = L.Language;
                L.Apply("en-US");
                var enPage = new SettingsPage(_w._settings);   // 换语言必须重新 new
                var enButton = enPage.AssociateButtonForTest;
                Check(enButton != null && Convert.ToString(enButton.Content) == "Link now",
                      "英文界面按钮文案 = Link now");
                RenderElement(enPage,
                    Path.Combine(_root ?? Path.GetTempPath(), "out", "login-settings-link-en.png"));
                L.Apply(savedLanguage);
                KeePassBridge.ConfigPath = savedConfigPath;

                // ---------- 页面内图标 + 切标签补扫 ----------
                var icons0 = await LoginFill.IconCountAsync(core);
                Check(icons0 == 5, "用户名输入框内装了 5 个图标（实际 " + icons0 + "）");
                var iconInside = await EvalAsync(core,
                    "(function(){var i=document.querySelector('.__neLoginIcon');" +
                    "if(!i)return 'no-icon';var r=i.getBoundingClientRect();" +
                    "var u=document.querySelector('#f1 input[name=username]').getBoundingClientRect();" +
                    "return (r.right<=u.right+2 && r.right>=u.right-26 &&" +
                    " Math.abs((r.top+r.bottom)/2-(u.top+u.bottom)/2)<12)?'yes':'no';})()");
                Check(iconInside == "yes", "图标贴在用户名输入框内部右侧（不是地址栏）");
                var iconSrc = await EvalAsync(core,
                    "(function(){var i=document.querySelector('.__neLoginIcon');" +
                    "return i?i.style.backgroundImage:'';})()");
                Check(iconSrc.IndexOf("data:image/png;base64", StringComparison.Ordinal) >= 0,
                      "图标用的是 KeePassXC 官方 logo（内联 data URI）");

                await LoginFill.RemoveIconsAsync(core);
                Check(await LoginFill.IconCountAsync(core) == 0, "前置：手动移除页面内图标");
                _w.NewTab();          // 起始页没有 WebView2
                _w.SelectTab(tab);    // 切回 fixture 页，SelectTab 里补扫一次
                var restored = false;
                for (var t = 0; t < 40; t++)
                {
                    if (await LoginFill.IconCountAsync(core) == 5) { restored = true; break; }
                    await Task.Delay(100);
                }
                Check(restored, "切回标签时补扫，输入框内图标恢复");

                // ---------- 界面层：单账号直填 / 多账号下拉 ----------
                // 自测配置来自 --kpxctest=associate 的产物；没有就跳过界面断言，
                // 免得在没配好 KeePassXC 的机器上卡在关联弹窗里。
                var hasConfig = KeePassBridge.LoadConfig() != null;
                if (!hasConfig)
                {
                    Line("跳过凭据相关断言：没有关联配置（先跑 --kpxctest=associate）");
                    return;
                }

                // 真点 DOM 图标，走完整的 click -> postMessage -> 宿主取凭据链路。
                await core.ExecuteScriptAsync(
                    "(function(){var a=document.querySelectorAll('.__neLoginIcon');if(a.length)a[0].click();})()");
                var filledByIcon = false;
                for (var t = 0; t < 120; t++)
                {
                    var v = await EvalAsync(core, "document.querySelector('#f1 input[name=password]').value");
                    if (v == "testpass123") { filledByIcon = true; break; }
                    await Task.Delay(100);
                }
                Check(filledByIcon, "点输入框内图标：只有 1 个账号时直接填充");
                Check(!await LoginFill.PickerVisibleAsync(core), "单账号不弹下拉（官方插件手感）");
                var filledUser = await EvalAsync(core, "document.querySelector('#f1 input[name=username]').value");
                var filledPass = await EvalAsync(core, "document.querySelector('#f1 input[name=password]').value");
                Check(filledUser == "testuser", "用户名落进页面（读回 " + filledUser + "）");
                Check(filledPass == "testpass123", "密码落进页面（长度 " + filledPass.Length + "）");
                await CapturePageAsync("login-icons.png");

                // ---------- 多个账号：下拉贴在输入框下方 ----------
                _w._loginEntries = new List<KeePassCredential>
                {
                    new KeePassCredential { Name = "spike-test", Login = "testuser", Password = "testpass123" },
                    new KeePassCredential { Name = "second-account", Login = "bob", Password = "bob-secret" }
                };
                await LoginFill.ShowPickerAsync(core, 0, _w._loginEntries, true,
                    L.T("选择要填充的账号"), L.T("重新加载"));
                Check(await LoginFill.PickerVisibleAsync(core), "多个账号时弹出下拉");
                await LoginFill.InstallIconsAsync(core);   // 模拟导航后 5 秒那次延迟扫描
                Check(await LoginFill.PickerVisibleAsync(core),
                      "延迟扫描重装图标时不会把下拉收起");
                var pickerText = await EvalAsync(core,
                    "(function(){var p=document.getElementById('__neLoginPicker');return p?p.textContent:'';})()");
                Line("下拉内容：" + pickerText);
                Check(pickerText.IndexOf("second-account", StringComparison.Ordinal) >= 0, "下拉列出条目名");
                Check(pickerText.IndexOf("bob", StringComparison.Ordinal) >= 0, "下拉列出用户名");
                Check(pickerText.IndexOf("testpass123", StringComparison.Ordinal) < 0 &&
                      pickerText.IndexOf("bob-secret", StringComparison.Ordinal) < 0,
                      "下拉里没有密码明文");
                var pickerBelow = await EvalAsync(core,
                    "(function(){var p=document.getElementById('__neLoginPicker');" +
                    "if(!p)return 'no';var r=p.getBoundingClientRect();" +
                    "var u=document.querySelector('#f1 input[name=username]').getBoundingClientRect();" +
                    "return (r.top>=u.bottom-2 && Math.abs(r.left-u.left)<4)?'yes':'no';})()");
                Check(pickerBelow == "yes", "下拉锚定在用户名输入框下方（不是地址栏）");
                await CapturePageAsync("login-picker.png");

                // 点第 2 条 -> WebMessage -> 宿主按索引把该账号填进表单
                await core.ExecuteScriptAsync(
                    "(function(){var a=document.querySelectorAll('.__neLoginRow');if(a.length>1)a[1].click();})()");
                var picked = false;
                for (var t = 0; t < 80; t++)
                {
                    var v = await EvalAsync(core, "document.querySelector('#f1 input[name=username]').value");
                    if (v == "bob") { picked = true; break; }
                    await Task.Delay(100);
                }
                Check(picked, "点下拉第 2 条：宿主按索引把选中账号填进表单");
                var pickedPass = await EvalAsync(core, "document.querySelector('#f1 input[name=password]').value");
                Check(pickedPass == "bob-secret", "填充的是选中账号的密码");
                Check(!await LoginFill.PickerVisibleAsync(core), "选中后下拉收起");

                // ---------- 设置开关 ----------
                _w._settings.PasswordFill = false;
                _w.OnSettingChanged("password");
                await Task.Delay(400);
                var iconsOff = await LoginFill.IconCountAsync(core);
                Check(iconsOff == 0, "关掉「填充账号密码」后页面内图标被移除");
                var scriptGone = await EvalAsync(core, "typeof window.__neLogin");
                Line("关掉后页面里的 window.__neLogin = " + scriptGone +
                     "（已注入的文档不会回收，新导航才彻底清除）");

                _w._settings.PasswordFill = true;
                _w.OnSettingChanged("password");
                await Task.Delay(300);
            }

            // 5 秒兜底扫描：表单在导航约 3 秒后才渲染，600/2000ms 两次必然落空，
            // 只有新增的 5000ms 那次能把图标点亮。这是 Kibana 类 SPA 的复现场景。
            private async Task RunLateScanTestAsync()
            {
                if (_core == null) { Line("跳过 5 秒扫描断言：没有 CoreWebView2"); return; }
                _w.HideLoginIcons();
                _core.Navigate("https://example.com/late");
                await Task.Delay(2600);
                var earlyIcons = await LoginFill.IconCountAsync(_core);
                Check(earlyIcons == 0, "晚渲染页：2 秒扫描窗口过后还没有图标（表单尚未出现）");

                var shown = false;
                for (var t = 0; t < 90; t++)
                {
                    if (await LoginFill.IconCountAsync(_core) >= 1) { shown = true; break; }
                    await Task.Delay(100);
                }
                Check(shown, "5 秒兜底扫描把 3 秒后才渲染的登录框扫出来，图标亮起");

                var lateFields = await LoginFill.ScanAsync(_core);
                Check(lateFields.Count >= 1, "晚渲染页确实有密码框（scan=" + lateFields.Count + "）");
                await CapturePageAsync("login-late-icon.png");
            }

            private async Task<CoreWebView2> WaitForCoreAsync(TabModel tab)
            {
                for (var i = 0; i < 200; i++)
                {
                    var core = _w._browser.Core(tab);
                    if (core != null) return core;
                    await Task.Delay(100);
                }
                return null;
            }

            private static async Task WaitForAsync(CoreWebView2 core, string expression, int timeoutMs)
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        var value = await core.ExecuteScriptAsync(expression);
                        if (value == "true") return;
                    }
                    catch { }
                    await Task.Delay(120);
                }
            }

            private static async Task<string> EvalAsync(CoreWebView2 core, string expression)
            {
                try
                {
                    var raw = await core.ExecuteScriptAsync(expression);
                    if (string.IsNullOrEmpty(raw)) return "";
                    // 字符串结果被 JSON 编码成 "..."；布尔/数字是裸值，不能当字符串解。
                    if (raw[0] == '"')
                        return new JavaScriptSerializer().Deserialize<string>(raw) ?? "";
                    return raw;
                }
                catch { return ""; }
            }

            // 图标和下拉都在网页里，抓 WebView2 自己的预览才看得见。
            private async Task CapturePageAsync(string fileName)
            {
                try
                {
                    var dir = Path.Combine(_root ?? Path.GetTempPath(), "out");
                    Directory.CreateDirectory(dir);
                    using (var stream = new MemoryStream())
                    {
                        await _core.CapturePreviewAsync(
                            CoreWebView2CapturePreviewImageFormat.Png, stream);
                        var bytes = stream.ToArray();
                        File.WriteAllBytes(Path.Combine(dir, fileName), bytes);
                        Line("页面截图 " + fileName + " = " + bytes.Length + " 字节");
                    }
                }
                catch (Exception ex) { Line("页面截图失败：" + ex.Message); }
            }

            private void RenderElement(FrameworkElement element, string path)
            {
                try
                {
                    if (element == null) { Line("截图跳过（元素为空）：" + Path.GetFileName(path)); return; }
                    element.UpdateLayout();
                    var w = (int)Math.Round(element.ActualWidth);
                    var h = (int)Math.Round(element.ActualHeight);
                    if (w <= 0 || h <= 0)
                    {
                        // Popup 的 Child 可能还没量过尺寸，手动量一次
                        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        element.Arrange(new Rect(new Point(0, 0), element.DesiredSize));
                        w = (int)Math.Round(element.ActualWidth);
                        h = (int)Math.Round(element.ActualHeight);
                    }
                    if (w <= 0 || h <= 0) { Line("截图跳过（尺寸为 0）：" + Path.GetFileName(path)); return; }

                    var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(element);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                    using (var stream = File.Create(path)) encoder.Save(stream);
                    Line("截图 " + Path.GetFileName(path) + " = " + new FileInfo(path).Length +
                         " 字节 (" + w + "x" + h + ")");
                }
                catch (Exception ex) { Line("截图失败 " + Path.GetFileName(path) + "：" + ex.Message); }
            }

            private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs)
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    if (condition()) return true;
                    await Task.Delay(100);
                }
                return false;
            }

            private static string CollectText(DependencyObject root)
            {
                var text = new StringBuilder();
                Collect(root, text);
                return text.ToString();
            }

            private static void Collect(DependencyObject node, StringBuilder text)
            {
                if (node == null) return;
                var block = node as TextBlock;
                if (block != null) { text.AppendLine(block.Text); return; }
                var count = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < count; i++) Collect(VisualTreeHelper.GetChild(node, i), text);
            }

            private void Check(bool condition, string text)
            {
                if (condition) { _passes++; Line("PASS  " + text); }
                else { Fail(text, ""); }
            }

            private void Fail(string text, string detail)
            {
                _failures.Add(text);
                Line("FAIL  " + text + (string.IsNullOrEmpty(detail) ? "" : ("\n      " + detail)));
            }

            private void Line(string text)
            {
                lock (_report) _report.AppendLine(text);
            }

            private void WriteReport()
            {
                try
                {
                    var dir = Path.Combine(_root ?? Path.GetTempPath(), "out");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "login-report.txt"),
                        _report.ToString(), new UTF8Encoding(false));
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
