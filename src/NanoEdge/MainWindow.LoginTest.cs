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
            private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

            public LoginSelfTest(MainWindow window) { _w = window; }

            public async Task RunAsync()
            {
                _root = FindRoot();
                // 用 --kpxctest=associate 产出的测试配置，不读用户真实的 keepassxc.json
                KeePassBridge.ConfigPath = Path.Combine(_root ?? Path.GetTempPath(), "out", "kpxc-test-config.json");
                SettingsStore.Persist = false;   // 自测改设置但不落盘
                Line("NanoEdge login fill self-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                try { await RunCoreAsync(); }
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

                // 第一次导航（127.0.0.1:1 必然连不上）还在跑的时候再 Navigate 会被吞掉，
                // 等它收尾再注册拦截、指向 example.com。
                await Task.Delay(1500);

                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                var intercepted = 0;
                core.WebResourceRequested += (s, e) =>
                {
                    if (!e.Request.Uri.StartsWith("https://example.com", StringComparison.OrdinalIgnoreCase)) return;
                    intercepted++;
                    var body = Encoding.UTF8.GetBytes(html);
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

                // ---------- 界面层：图标 / 弹层 / 填充 ----------
                // 自测配置来自 --kpxctest=associate 的产物；没有就跳过界面断言，
                // 免得在没配好 KeePassXC 的机器上卡在关联弹窗里。
                var hasConfig = KeePassBridge.LoadConfig() != null;
                if (!hasConfig)
                {
                    Line("跳过界面层断言：没有关联配置（先跑 --kpxctest=associate）");
                    return;
                }

                var iconShown = await WaitUntil(
                    () => _w._loginButton != null && _w._loginButton.Visibility == Visibility.Visible, 8000);
                Check(iconShown, "钥匙图标在检测到密码框后出现");

                _w.OnLoginClick();
                var popupOpen = await WaitUntil(
                    () => _w._loginPopup != null && _w._loginPopup.IsOpen, 20000);
                Check(popupOpen, "点击钥匙后账号弹层打开");
                if (!popupOpen) return;

                await CaptureWindowAsync("login-popup.png");

                var popupText = CollectText((DependencyObject)_w._loginPopup.Child);
                Line("弹层内容：" + popupText.Replace("\n", " | "));
                Check(popupText.IndexOf("spike-test", StringComparison.Ordinal) >= 0, "弹层列出 KeePassXC 的条目名");
                Check(popupText.IndexOf("testuser", StringComparison.Ordinal) >= 0, "弹层列出用户名");
                Check(popupText.IndexOf("testpass123", StringComparison.Ordinal) < 0, "弹层不显示密码明文");

                // fillActive 按焦点挑表单，先把焦点移到第一个表单，确保填的是它
                await core.ExecuteScriptAsync(
                    "document.querySelector('#f1 input[name=password]').focus()");
                _w.FillCredential(new KeePassCredential
                {
                    Name = "spike-test",
                    Login = "testuser",
                    Password = "testpass123"
                });
                await Task.Delay(300);
                var filledUser = await EvalAsync(core, "document.querySelector('#f1 input[name=username]').value");
                var filledPass = await EvalAsync(core, "document.querySelector('#f1 input[name=password]').value");
                Check(filledUser == "testuser", "界面填充：用户名落进页面（读回 " + filledUser + "）");
                Check(filledPass == "testpass123", "界面填充：密码落进页面（长度 " + filledPass.Length + "）");

                // ---------- 设置开关 ----------
                _w._settings.PasswordFill = false;
                _w.OnSettingChanged("password");
                await Task.Delay(400);
                var hiddenAfterOff = _w._loginButton == null ||
                                      _w._loginButton.Visibility != Visibility.Visible;
                Check(hiddenAfterOff, "关掉「填充账号密码」后钥匙图标消失");
                var scriptGone = await EvalAsync(core, "typeof window.__neLogin");
                Line("关掉后页面里的 window.__neLogin = " + scriptGone +
                     "（已注入的文档不会回收，新导航才彻底清除）");

                _w._settings.PasswordFill = true;
                _w.OnSettingChanged("password");
                await Task.Delay(300);
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

            // WebView2 是 HwndHost，屏幕截图在这个会话里拿不到有效句柄（"句柄无效"），
            // 所以改成分别渲染 WPF 元素：要看的「钥匙图标」和「账号弹层」本来都是 WPF。
            private async Task CaptureWindowAsync(string fileName)
            {
                try
                {
                    await Task.Delay(300);
                    var dir = Path.Combine(_root ?? Path.GetTempPath(), "out");
                    Directory.CreateDirectory(dir);

                    RenderElement(_w._loginPopup == null ? null : _w._loginPopup.Child as FrameworkElement,
                        Path.Combine(dir, fileName));
                    RenderElement(_w._toolbarHost as FrameworkElement,
                        Path.Combine(dir, "login-toolbar.png"));
                }
                catch (Exception ex) { Line("截图失败：" + ex.Message); }
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
