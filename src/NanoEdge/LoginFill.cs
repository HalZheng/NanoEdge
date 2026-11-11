using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;

namespace NanoEdge
{
    internal sealed class LoginField
    {
        public int index { get; set; }
        public string userField { get; set; }
        public string passField { get; set; }
        public string formAction { get; set; }
        public bool ready { get; set; }
    }

    // =====================================================================
    // 登录表单识别与填充（SPEC 8.x）
    //
    // 只做两件事：找出「密码框 + 它前面的用户名框」，以及把值填进去。
    // 不常驻 MutationObserver —— 每次导航完成扫一次、点钥匙图标时再扫一次，
    // 内存不随页面存活时间增长（SPEC 2 的内存口径）。
    //
    // 同源 iframe 的 contentDocument 可以直接访问，所以递归进来一起扫；
    // 跨域 iframe 抛异常直接跳过——跨域登录框（第三方 SSO / 广告位）本来
    // 就不该用本站密码去填，那是个现成的钓鱼面。
    // =====================================================================
    internal static class LoginFill
    {
        private static readonly JavaScriptSerializer Serializer =
            new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };

        internal const string Script = @"(function(){
if (window.__neLogin) return 'exists';

function visible(el){
  if (!el || el.disabled) return false;
  var r = el.getBoundingClientRect();
  if (r.width <= 0 || r.height <= 0) return false;
  var s = null;
  try { s = getComputedStyle(el); } catch(e){ return true; }
  if (!s) return true;
  return s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0';
}

// React/Vue 的受控组件只认『原生 setter + 手动派发事件』：
// 直接 el.value = x 会被框架当成没变化，state 不更新，提交时还是空值。
function setValue(el, value){
  var proto = Object.getPrototypeOf(el);
  var desc = proto && Object.getOwnPropertyDescriptor(proto, 'value');
  if (desc && desc.set) desc.set.call(el, value); else el.value = value;
  // React 16+ 用 _valueTracker 去重；不重置它，input 事件会被忽略。
  if (el._valueTracker) { try { el._valueTracker.setValue(''); } catch(e){} }
  el.dispatchEvent(new Event('input', { bubbles: true }));
  el.dispatchEvent(new Event('change', { bubbles: true }));
}

function isCandidate(el){
  var t = (el.type || 'text').toLowerCase();
  if (t === 'password' || t === 'hidden' || t === 'submit' || t === 'button' ||
      t === 'checkbox' || t === 'radio' || t === 'file' || t === 'image' ||
      t === 'reset' || t === 'range' || t === 'color') return false;
  return visible(el) && !el.readOnly;
}

function findUser(pw){
  // 用 ownerDocument 而不是全局 document：iframe 里的密码框要在它自己的文档里找。
  var doc = pw.ownerDocument || document;
  var scope = pw.form || doc;
  var all = scope.querySelectorAll('input');
  var idx = Array.prototype.indexOf.call(all, pw);
  var i;
  for (i = idx - 1; i >= 0; i--) if (isCandidate(all[i])) return all[i];
  for (i = idx + 1; i < all.length; i++) if (isCandidate(all[i])) return all[i];
  return null;
}

function collectDocs(doc, depth, out){
  if (!doc || depth > 3) return;
  out.push(doc);
  var frames;
  try { frames = doc.querySelectorAll('iframe, frame'); } catch(e){ return; }
  for (var i = 0; i < frames.length; i++) {
    var sub = null;
    try { sub = frames[i].contentDocument; } catch(e){ sub = null; }
    if (sub) collectDocs(sub, depth + 1, out);
  }
}

// 扁平索引：主文档的密码框在前，同源 iframe 的依次排后。
// scan 和 fill 都用这一个顺序，索引才对得上。
function allPasswords(){
  var docs = [];
  collectDocs(document, 0, docs);
  var out = [];
  for (var d = 0; d < docs.length; d++) {
    var list;
    try { list = docs[d].querySelectorAll('input[type=password]'); } catch(e){ continue; }
    for (var i = 0; i < list.length; i++) out.push(list[i]);
  }
  return out;
}

function inViewport(el){
  try {
    var r = el.getBoundingClientRect();
    var h = window.innerHeight || 1e9;
    return r.bottom > 0 && r.top < h;
  } catch(e){ return false; }
}

function scan(){
  var list = allPasswords();
  var out = [];
  for (var i = 0; i < list.length; i++) {
    var pw = list[i];
    if (!visible(pw)) continue;
    var user = findUser(pw);
    out.push({
      index: i,
      userField: user ? String(user.name || user.id || user.type || '') : '',
      passField: String(pw.name || pw.id || ''),
      formAction: String((pw.form && pw.form.action) || ''),
      ready: !pw.disabled && !pw.readOnly
    });
  }
  return out;
}

function fillAt(index, user, pass){
  var list = allPasswords();
  var pw = list[index];
  if (!pw) return { ok: false, error: 'field-not-found' };
  if (pw.disabled || pw.readOnly) return { ok: false, error: 'field-locked' };
  var u = findUser(pw);
  if (u && user) setValue(u, user);
  setValue(pw, pass);
  try { pw.focus(); } catch(e){}
  return { ok: true, userFilled: !!u };
}

// 一个页面有多个登录表单时（页头登录 + 页尾订阅），按「焦点 > 视口内 > 第一个」挑。
function fillActive(user, pass){
  var list = allPasswords();
  if (!list.length) return { ok: false, error: 'no-field' };
  var active = document.activeElement;
  var pick = -1, i, pw;
  if (active) {
    for (i = 0; i < list.length; i++) {
      pw = list[i];
      if (pw === active || findUser(pw) === active) { pick = i; break; }
    }
  }
  if (pick < 0) {
    for (i = 0; i < list.length; i++) if (visible(list[i]) && inViewport(list[i])) { pick = i; break; }
  }
  if (pick < 0) {
    for (i = 0; i < list.length; i++) if (visible(list[i])) { pick = i; break; }
  }
  if (pick < 0) pick = 0;
  return fillAt(pick, user, pass);
}

window.__neLogin = { scan: scan, fill: fillAt, fillActive: fillActive };
return 'installed';
})()";

        internal static async Task<List<LoginField>> ScanAsync(CoreWebView2 core)
        {
            var result = new List<LoginField>();
            if (core == null) return result;
            try
            {
                var json = await core.ExecuteScriptAsync(
                    "window.__neLogin ? window.__neLogin.scan() : []");
                var list = Serializer.Deserialize<List<LoginField>>(json);
                if (list != null) result = list;
            }
            catch (Exception ex) { Diagnostics.Log("login scan failed: " + ex.Message); }
            return result;
        }

        internal static Task<bool> FillAsync(CoreWebView2 core, int index, string user, string password)
        {
            return InvokeAsync(core, string.Format("window.__neLogin.fill({0},{1},{2})",
                index, JsString(user), JsString(password)));
        }

        // 界面层用这个：不用关心是第几个框，按焦点/视口自动选。
        internal static Task<bool> FillActiveAsync(CoreWebView2 core, string user, string password)
        {
            return InvokeAsync(core, string.Format("window.__neLogin.fillActive({0},{1})",
                JsString(user), JsString(password)));
        }

        private static async Task<bool> InvokeAsync(CoreWebView2 core, string expression)
        {
            if (core == null) return false;
            try
            {
                // 用参数化调用而不是拼字符串：密码里可能有引号、反斜杠、换行。
                var script =
                    "(function(){ if(!window.__neLogin) return {ok:false,error:'not-installed'};" +
                    "return " + expression + "; })()";
                var json = await core.ExecuteScriptAsync(script);
                var map = Serializer.Deserialize<Dictionary<string, object>>(json);
                object ok;
                return map != null && map.TryGetValue("ok", out ok) &&
                       string.Equals(Convert.ToString(ok), "true", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("login fill failed: " + ex.Message);
                return false;
            }
        }

        // JSON 转义后直接当 JS 字面量用；Serializer 的输出是合法 JSON，
        // 而 JSON 字符串字面量本身就是合法的 JS 表达式。
        private static string JsString(string value)
        {
            return Serializer.Serialize(value ?? "");
        }
    }
}
