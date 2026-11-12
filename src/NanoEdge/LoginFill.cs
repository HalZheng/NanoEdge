using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
    // 登录表单识别与填充（SPEC 7.15）
    //
    // 和 KeePassXC-Browser 官方插件一样，图标画在「用户名输入框内部右侧」，
    // 下拉贴在输入框下方 —— 两者都是页面里的 DOM 元素。原因很直接：WebView2
    // 是 HwndHost，WPF 控件盖不到网页上；做成 DOM 才能跟着输入框滚动和换行。
    //
    // 密码绝不进页面脚本：下拉只收到条目名和用户名，选中后由宿主按索引
    // 调 fill() 把明文写进 input。
    //
    // 不常驻 MutationObserver：图标由宿主在既有扫描时机（导航后 600/2000/5000ms、
    // 切回标签、点图标）安装/重定位；只挂一个 scroll/resize 监听做位置刷新。
    // =====================================================================
    internal static class LoginFill
    {
        private static readonly JavaScriptSerializer Serializer =
            new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };

        private const string IconPlaceholder = "__NE_ICON_DATA_URI__";

        internal const string Script = @"(function(){
if (window.__neLogin) return 'exists';

var ICON = '__NE_ICON_DATA_URI__';
var ICON_HOST = '__neLoginIcons';
var PICKER_ID = '__neLoginPicker';
var icons = [];
var picker = null;
var listening = false;

function post(msg){
  try {
    var cw = (window.chrome && window.chrome.webview) ||
             (window.top && window.top.chrome && window.top.chrome.webview);
    if (cw) cw.postMessage(msg);
  } catch(e){}
}

function visible(el){
  if (!el || el.disabled) return false;
  var r = el.getBoundingClientRect();
  if (r.width <= 0 || r.height <= 0) return false;
  var s = null;
  try { s = getComputedStyle(el); } catch(e){ return true; }
  if (!s) return true;
  return s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0';
}

// React/Vue 的受控组件只认『原生 setter + 手动派发事件』
function setValue(el, value){
  var proto = Object.getPrototypeOf(el);
  var desc = proto && Object.getOwnPropertyDescriptor(proto, 'value');
  if (desc && desc.set) desc.set.call(el, value); else el.value = value;
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
// scan、图标、fill 都用这一个顺序，索引才对得上。
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

// ---------- 输入框内图标 ----------

function hostFor(doc){
  var host = doc.getElementById(ICON_HOST);
  if (!host) {
    host = doc.createElement('div');
    host.id = ICON_HOST;
    host.style.cssText = 'position:fixed;left:0;top:0;width:0;height:0;z-index:2147483000;';
    (doc.body || doc.documentElement).appendChild(host);
  }
  return host;
}

// fixed + 视口坐标：图标贴着输入框，滚动时按新的 rect 重算即可，
// 不用为每个输入框挂观察器。
function placeAll(){
  for (var i = 0; i < icons.length; i++) {
    var it = icons[i];
    var r = (it.input && it.input.isConnected) ? it.input.getBoundingClientRect() : null;
    if (!r || r.width <= 0 || r.height <= 0) { it.el.style.display = 'none'; continue; }
    it.el.style.display = 'block';
    it.el.style.left = Math.round(r.right - 20) + 'px';
    it.el.style.top = Math.round(r.top + (r.height - 16) / 2) + 'px';
  }
}

function listen(){
  if (listening) return;
  listening = true;
  try {
    window.addEventListener('scroll', placeAll, true);
    window.addEventListener('resize', placeAll, true);
  } catch(e){}
}

function clearIcons(){
  for (var i = 0; i < icons.length; i++) {
    try { if (icons[i].el.parentNode) icons[i].el.parentNode.removeChild(icons[i].el); } catch(e){}
  }
  icons = [];
  var docs = []; collectDocs(document, 0, docs);
  for (var d = 0; d < docs.length; d++) {
    var h = docs[d].getElementById(ICON_HOST);
    try { if (h && h.parentNode) h.parentNode.removeChild(h); } catch(e){}
  }
  // 不要顺手关掉下拉：导航后 5 秒那次扫描也会重装图标，
  // 若把 picker 一起收起，用户点开多账号列表两秒后列表会自己消失。
  return 0;
}

function installIcons(){
  // document-created 脚本会在每个 frame 各跑一次；只在主文档装，
  // iframe 里的表单由 collectDocs 递归覆盖，避免重复图标。
  if (window.top !== window) return 0;
  clearIcons();
  var list = allPasswords();
  for (var i = 0; i < list.length; i++) {
    var pw = list[i];
    if (!visible(pw)) continue;
    var input = findUser(pw) || pw;
    var doc = pw.ownerDocument || document;
    var el = doc.createElement('div');
    el.className = '__neLoginIcon';
    el.setAttribute('data-ne-index', String(i));
    el.setAttribute('title', 'KeePassXC');
    el.style.cssText = 'position:fixed;width:16px;height:16px;cursor:pointer;' +
      'z-index:2147483647;background-size:contain;background-repeat:no-repeat;' +
      'background-position:center;opacity:.85;border:0;padding:0;margin:0;';
    el.style.backgroundImage = 'url(""' + ICON + '"")';
    // 别让输入框失焦：填充要按图标对应的表单索引，不能退化成「猜焦点」。
    el.addEventListener('mousedown', function(ev){ ev.preventDefault(); ev.stopPropagation(); });
    (function(idx){
      el.addEventListener('click', function(ev){
        ev.preventDefault(); ev.stopPropagation();
        post({ neLogin: 'icon', index: idx });
      });
    })(i);
    hostFor(doc).appendChild(el);
    icons.push({ el: el, input: input, index: i });
  }
  if (icons.length) listen();
  placeAll();
  return icons.length;
}

// ---------- 输入框下方的账号下拉 ----------

function anchorRect(index){
  for (var i = 0; i < icons.length; i++)
    if (icons[i].index === index && icons[i].input) return icons[i].input.getBoundingClientRect();
  return null;
}

function onOutside(ev){
  if (!picker) { document.removeEventListener('mousedown', onOutside, true); return; }
  if (picker.contains(ev.target)) return;
  if (ev.target && ev.target.className === '__neLoginIcon') return;
  hidePicker();
  document.removeEventListener('mousedown', onOutside, true);
}

function hidePicker(){
  if (picker && picker.parentNode) picker.parentNode.removeChild(picker);
  picker = null;
  try { document.removeEventListener('mousedown', onOutside, true); } catch(e){}
  return 0;
}

// items 只有 {name, login}，永远不含 password。
function showPicker(index, items, dark, title, reloadLabel){
  hidePicker();
  if (window.top !== window) return 0;
  var bg = dark ? '#1F232A' : '#FFFFFF';
  var fg = dark ? '#E8EAED' : '#1D2A3E';
  var line = dark ? '#3A404A' : '#DFE3E8';
  var hover = dark ? '#2B313B' : '#F0F4FA';
  var box = document.createElement('div');
  box.id = PICKER_ID;
  box.style.cssText = 'position:fixed;z-index:2147483647;min-width:220px;max-width:320px;' +
    'background:' + bg + ';color:' + fg + ';border:1px solid ' + line + ';border-radius:8px;' +
    'box-shadow:0 8px 26px rgba(0,0,0,.28);padding:4px;' +
    'font:12px/1.5 system-ui,-apple-system,Segoe UI,sans-serif;';
  var r = anchorRect(index);
  box.style.left = (r ? Math.max(4, Math.round(r.left)) : 20) + 'px';
  box.style.top = (r ? Math.round(r.bottom + 4) : 20) + 'px';

  var head = document.createElement('div');
  head.textContent = title;
  head.style.cssText = 'padding:4px 8px 6px;font-weight:600;opacity:.7;' +
    'border-bottom:1px solid ' + line + ';margin-bottom:4px;';
  box.appendChild(head);

  for (var i = 0; i < items.length; i++) {
    (function(item, k){
      var row = document.createElement('div');
      row.className = '__neLoginRow';
      row.setAttribute('data-ne-entry', String(k));
      row.style.cssText = 'padding:6px 8px;border-radius:5px;cursor:pointer;';
      var n = document.createElement('div');
      n.textContent = item.name || item.login || '';
      n.style.fontWeight = '500';
      row.appendChild(n);
      if (item.login && item.login !== item.name) {
        var u = document.createElement('div');
        u.textContent = item.login;
        u.style.cssText = 'opacity:.65;font-size:11px;';
        row.appendChild(u);
      }
      row.addEventListener('mouseenter', function(){ row.style.background = hover; });
      row.addEventListener('mouseleave', function(){ row.style.background = 'transparent'; });
      row.addEventListener('mousedown', function(ev){ ev.preventDefault(); ev.stopPropagation(); });
      row.addEventListener('click', function(ev){
        ev.preventDefault(); ev.stopPropagation();
        post({ neLogin: 'pick', index: index, entry: k });
        hidePicker();
      });
      box.appendChild(row);
    })(items[i], i);
  }

  var foot = document.createElement('div');
  foot.textContent = reloadLabel;
  foot.style.cssText = 'padding:6px 8px;margin-top:4px;border-top:1px solid ' + line +
    ';cursor:pointer;opacity:.8;text-align:center;';
  foot.addEventListener('mousedown', function(ev){ ev.preventDefault(); ev.stopPropagation(); });
  foot.addEventListener('click', function(ev){
    ev.preventDefault(); ev.stopPropagation();
    post({ neLogin: 'reload', index: index });
    hidePicker();
  });
  box.appendChild(foot);

  document.body.appendChild(box);
  picker = box;
  setTimeout(function(){ document.addEventListener('mousedown', onOutside, true); }, 0);
  return items.length;
}

window.__neLogin = {
  scan: scan, fill: fillAt, fillActive: fillActive,
  installIcons: installIcons, removeIcons: clearIcons,
  iconCount: function(){ return icons.length; },
  showPicker: showPicker, hidePicker: hidePicker,
  pickerVisible: function(){ return !!picker; }
};
return 'installed';
})()";

        // 图标是嵌入资源，注入前替换成 data URI；脚本本体保持纯文本常量。
        internal static string ScriptFor(string iconUri)
        {
            return Script.Replace(IconPlaceholder, iconUri ?? "");
        }

        private static string _iconUri;

        internal static string IconDataUri()
        {
            if (_iconUri != null) return _iconUri;
            try
            {
                using (var stream = Assembly.GetExecutingAssembly()
                           .GetManifestResourceStream("NanoEdge.Native.keepassxc.png"))
                {
                    if (stream == null) { _iconUri = ""; return _iconUri; }
                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        _iconUri = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("load keepassxc icon failed: " + ex.Message);
                _iconUri = "";
            }
            return _iconUri;
        }

        // ---------------- 宿主 -> 页面 ----------------

        private static async Task<string> EvalAsync(CoreWebView2 core, string script)
        {
            if (core == null) return "";
            try { return await core.ExecuteScriptAsync(script) ?? ""; }
            catch (Exception ex) { Diagnostics.Log("login ui script failed: " + ex.Message); return ""; }
        }

        internal static async Task<int> InstallIconsAsync(CoreWebView2 core)
        {
            var raw = await EvalAsync(core,
                "(function(){ return window.__neLogin ? window.__neLogin.installIcons() : -1; })()");
            int n;
            return int.TryParse(raw, out n) ? n : 0;
        }

        internal static Task RemoveIconsAsync(CoreWebView2 core)
        {
            // 关掉「填充账号密码」时图标和下拉一起清；日常扫描重装图标则保留下拉。
            return EvalAsync(core,
                "(function(){ if(!window.__neLogin) return -1;" +
                " window.__neLogin.removeIcons(); window.__neLogin.hidePicker(); return 0; })()");
        }

        internal static async Task<int> IconCountAsync(CoreWebView2 core)
        {
            var raw = await EvalAsync(core,
                "(function(){ return window.__neLogin ? window.__neLogin.iconCount() : -1; })()");
            int n;
            return int.TryParse(raw, out n) ? n : 0;
        }

        // 只把条目名和用户名送进页面，密码留在宿主内存里。
        internal static Task ShowPickerAsync(CoreWebView2 core, int index,
            List<KeePassCredential> entries, bool dark, string title, string reloadLabel)
        {
            var items = new List<object>();
            foreach (var e in entries)
                items.Add(new Dictionary<string, object> { { "name", e.Name }, { "login", e.Login } });
            var json = Serializer.Serialize(items);
            return EvalAsync(core, string.Format(
                "(function(){{ return window.__neLogin ? window.__neLogin.showPicker({0},{1},{2},{3},{4}) : -1; }})()",
                index, json, dark ? "true" : "false", JsString(title), JsString(reloadLabel)));
        }

        internal static Task HidePickerAsync(CoreWebView2 core)
        {
            return EvalAsync(core,
                "(function(){ return window.__neLogin ? window.__neLogin.hidePicker() : -1; })()");
        }

        internal static async Task<bool> PickerVisibleAsync(CoreWebView2 core)
        {
            var raw = await EvalAsync(core,
                "(function(){ return window.__neLogin ? window.__neLogin.pickerVisible() : false; })()");
            return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- 扫描 / 填充 ----------------

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
