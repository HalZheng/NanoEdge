using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;

namespace NanoEdge
{
    // 生成注入到页面里的 JS。引导器只有一份，每个用户脚本单独一条 wrapper：
    // 用户代码是「字面量」放进函数体，不用 eval / new Function，避开页面 CSP 的 eval 限制。
    public static class ScriptEngine
    {
        public static string Bootstrap()
        {
            return @"(function(){
if (window.__NE__) return;
var V = " + ScriptStore.ValuesAsJson() + @";
var SITES = " + ScriptStore.DisabledSitesAsJson() + @";
var seq = 1, pending = {};
function post(o){ try { window.chrome.webview.postMessage(o); } catch(e){} }
function hit(list, url){ for(var i=0;i<list.length;i++){ try{ if(new RegExp(list[i]).test(url)) return true; }catch(e){} } return false; }
var NE = window.__NE__ = {
  shouldRun: function(D){
    var host = (location.host || '').toLowerCase();
    var name = (location.hostname || '').toLowerCase();
    for (var i=0;i<SITES.length;i++){ var s=SITES[i].toLowerCase(); if(s===host||s===name) return false; }
    var url = location.href;
    if (D.match.length && !hit(D.match, url)) return false;
    if (hit(D.exclude, url)) return false;
    return true;
  },
  schedule: function(runAt, fn){
    if (runAt === 'document-start') { fn(); return; }
    if (runAt === 'document-end') {
      if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', fn, {once:true});
      else fn();
      return;
    }
    var go = function(){ if (window.requestIdleCallback) requestIdleCallback(fn,{timeout:1500}); else setTimeout(fn,0); };
    if (document.readyState === 'complete') go();
    else window.addEventListener('load', go, {once:true});
  },
  apiFor: function(D){
    var has = function(g){ return D.grants.indexOf(g) >= 0; };
    var key = function(k){ return D.ns + '\u0001' + k; };
    var GM = {
      info: has('GM_info') ? { script:{ name:D.name, namespace:D.ns, version:D.version, description:D.description }, scriptHandler:'NanoEdge', version:'0.1' } : undefined,
      getValue: has('GM_getValue') ? function(k,d){ var raw=V[key(k)]; return raw===undefined?d:JSON.parse(raw); } : undefined,
      setValue: has('GM_setValue') ? function(k,v){ V[key(k)]=JSON.stringify(v); post({__ne:'setValue', id:D.id, ns:D.ns, key:k, value:v}); } : undefined,
      deleteValue: has('GM_deleteValue') ? function(k){ delete V[key(k)]; post({__ne:'deleteValue', id:D.id, ns:D.ns, key:k}); } : undefined,
      // 同步返回本页加载时的值 + 本页写入过的键；其它标签页之后写入的键要刷新后才可见。
      listValues: has('GM_listValues') ? function(){
        var p = D.ns + '\u0001', out = [];
        for (var k in V) { if (k.indexOf(p) === 0) out.push(k.substring(p.length)); }
        return out;
      } : undefined,
      // 剪贴板由宿主写入：页面上下文没有 clipboard 权限，绕开 navigator.clipboard 的授权弹条。
      setClipboard: has('GM_setClipboard') ? function(text, type){
        post({__ne:'clipboard', id:D.id, text:String(text == null ? '' : text), type:String(type || 'text')});
      } : undefined,
      addStyle: has('GM_addStyle') ? function(css){
        var add=function(){ var s=document.createElement('style'); s.setAttribute('data-nanoedge','1'); s.textContent=css; (document.head||document.documentElement).appendChild(s); };
        if (document.head||document.documentElement) add(); else document.addEventListener('DOMContentLoaded',add,{once:true});
      } : undefined,
      notification: has('GM_notification') ? function(text,title){
        post({__ne:'notification', id:D.id, text:String(text||''), title:String(title||D.name)});
      } : undefined,
      openInTab: has('GM_openInTab') ? function(url){ post({__ne:'openInTab', id:D.id, url:String(url||'')}); } : undefined,
      xmlhttpRequest: has('GM_xmlhttpRequest') ? function(details){
        var id = seq++; pending[id] = details || {};
        post({__ne:'xhr', id:id, scriptId:D.id, ns:D.ns, url:(details||{}).url||'',
              method:(details||{}).method||'GET', headers:(details||{}).headers||{},
              data:(details||{}).data||'', timeout:(details||{}).timeout||15000});
      } : undefined
    };
    return GM;
  },
  reportRun: function(id){ post({__ne:'run', id:id}); },
  reportError: function(id,msg){ post({__ne:'error', id:id, message:String(msg||'')}); },
  xhrDone: function(m){
    var d = pending[m.id]; if(!d) return; delete pending[m.id];
    var r = { status:m.status||0, statusText:m.statusText||'', responseText:m.text||'', response:m.text||'',
              responseHeaders:m.headers||'', readyState:4, finalUrl:d.url, error:m.error||'' };
    try { if(m.ok && d.onload) d.onload(r); else if(!m.ok && d.onerror) d.onerror(r); } catch(e){}
    try { if(d.onloadend) d.onloadend(r); } catch(e){}
  }
};
try { window.chrome.webview.addEventListener('message', function(e){ var d=e.data; if(d && d.__ne==='xhr') NE.xhrDone(d); }); } catch(e){}
})();";
        }

        public static string Wrap(UserScript script)
        {
            var descriptor = new Dictionary<string, object>
            {
                { "id", script.Id },
                { "name", script.Name },
                { "ns", script.Namespace },
                { "version", script.Version },
                { "description", script.Description },
                { "runAt", script.RunAt },
                { "grants", script.Grants },
                { "match", ScriptParser.MatchRegexes(script) },
                { "exclude", ScriptParser.ExcludeRegexes(script) }
            };
            var json = new JavaScriptSerializer().Serialize(descriptor);

            // @require 正文按脚本头部顺序拼在最前，与主脚本共享同一个函数作用域
            //（Tampermonkey 的 require 也是同一脚本上下文，库里的 var 对主脚本可见）。
            // 每个依赖之间插入独立分号，避免上个文件末尾缺分号时被 ASI 拼坏。
            var requires = new List<string>();
            if (script.RequireCode != null)
                foreach (var code in script.RequireCode)
                    if (!string.IsNullOrEmpty(code)) requires.Add(code);
            var requireBlock = requires.Count == 0
                ? ""
                : string.Join("\n;\n", requires) + "\n;\n";

            return @"(function(){
var D = " + json + @";
if (!window.__NE__ || !window.__NE__.shouldRun(D)) return;
var GM = window.__NE__.apiFor(D);
var run = function(){
  try {
    (function(GM, GM_info, GM_setValue, GM_getValue, GM_deleteValue, GM_listValues, GM_addStyle, GM_notification, GM_openInTab, GM_setClipboard, GM_xmlhttpRequest, unsafeWindow){
" + requireBlock + (script.Code ?? "") + @"
    })(GM, GM.info, GM.setValue, GM.getValue, GM.deleteValue, GM.listValues, GM.addStyle, GM.notification, GM.openInTab, GM.setClipboard, GM.xmlhttpRequest, window);
    window.__NE__.reportRun(D.id);
  } catch(e) { window.__NE__.reportError(D.id, String((e && e.stack) || e)); }
};
window.__NE__.schedule(D.runAt, run);
})();";
        }
    }

    // 页面 -> 宿主的消息在这里落地。@grant 在 JS 侧和宿主侧各拦一次（7.3 的双端拒绝）。
    public sealed class ScriptBridge
    {
        public event Action<string> Notify;
        public event Action<string> OpenTab;
        public event Action<string> Clipboard;
        public event Action<string> Log;

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        public bool HasGrant(string scriptId, string api)
        {
            var script = ScriptStore.Find(scriptId);
            return script != null && script.Enabled && script.Grants.Contains(api);
        }

        public void Handle(CoreWebView2 core, string raw)
        {
            Dictionary<string, object> msg;
            try { msg = _json.DeserializeObject(raw) as Dictionary<string, object>; }
            catch { return; }
            if (msg == null) return;

            var kind = Str(msg, "__ne");
            var id = Str(msg, "id") ?? Str(msg, "scriptId");
            switch (kind)
            {
                case "run":
                    if (ScriptStore.Find(id) != null) ScriptStore.MarkRun(id);
                    break;
                case "error":
                    Emit("[script] " + id + " threw: " + Str(msg, "message"));
                    break;
                case "setValue":
                    if (!Granted(id, "GM_setValue")) break;
                    ScriptStore.SetValue(Str(msg, "ns"), Str(msg, "key"), Val(msg, "value"));
                    break;
                case "deleteValue":
                    if (!Granted(id, "GM_deleteValue")) break;
                    ScriptStore.DeleteValue(Str(msg, "ns"), Str(msg, "key"));
                    break;
                case "notification":
                    if (!Granted(id, "GM_notification")) break;
                    var handler = Notify;
                    if (handler != null) handler(Str(msg, "title") + "：" + Str(msg, "text"));
                    break;
                case "openInTab":
                    if (!Granted(id, "GM_openInTab")) break;
                    var url = Str(msg, "url");
                    if (string.IsNullOrWhiteSpace(url)) break;
                    var open = OpenTab;
                    if (open != null) open(url);
                    break;
                case "clipboard":
                    if (!Granted(id, "GM_setClipboard")) break;
                    var clip = Clipboard;
                    if (clip != null) clip(Str(msg, "text") ?? "");
                    break;
                case "xhr":
                    var unused = XhrAsync(core, msg);
                    break;
            }
        }

        private bool Granted(string id, string api)
        {
            if (HasGrant(id, api)) return true;
            Emit("[script] denied " + api + " for " + id + " (no @grant)");
            return false;
        }

        private async Task XhrAsync(CoreWebView2 core, Dictionary<string, object> msg)
        {
            var id = MsgId(msg);
            var scriptId = Str(msg, "scriptId") ?? Str(msg, "id");
            var url = Str(msg, "url") ?? "";
            var reply = new Dictionary<string, object> { { "__ne", "xhr" }, { "id", id } };

            if (!HasGrant(scriptId, "GM_xmlhttpRequest"))
            {
                Emit("[script] denied GM_xmlhttpRequest for " + scriptId + " (no @grant)");
                Fail(reply, "GM_xmlhttpRequest requires @grant");
            }
            else if (!ScriptHttp.IsAllowed(ScriptStore.Find(scriptId), url))
            {
                Emit("[script] denied GM_xmlhttpRequest to " + url + " (not in @connect)");
                Fail(reply, "target not allowed by @connect");
            }
            else
            {
                var headers = new Dictionary<string, string>();
                var rawHeaders = Dict(msg, "headers");
                if (rawHeaders != null)
                    foreach (var pair in rawHeaders) headers[pair.Key] = Convert.ToString(pair.Value);
                var timeout = 15000;
                try { timeout = Convert.ToInt32(Val(msg, "timeout")); } catch { }

                var result = await ScriptHttp.SendAsync(core, url, Str(msg, "method"),
                    headers, Str(msg, "data"), timeout);
                reply["ok"] = result.Ok;
                reply["status"] = result.Status;
                reply["statusText"] = result.StatusText;
                reply["text"] = result.Text;
                reply["error"] = result.Error;
                reply["headers"] = result.Headers;
            }

            if (core == null) return;
            try { core.PostWebMessageAsJson(_json.Serialize(reply)); }
            catch (Exception ex) { Emit("[script] post xhr reply failed: " + ex.Message); }
        }

        private static void Fail(Dictionary<string, object> reply, string reason)
        {
            reply["ok"] = false;
            reply["status"] = 0;
            reply["statusText"] = "";
            reply["text"] = "";
            reply["error"] = reason;
            reply["headers"] = new Dictionary<string, string>();
        }

        private void Emit(string text)
        {
            Diagnostics.Log(text);
            var handler = Log;
            if (handler != null) handler(text);
        }

        private static object MsgId(Dictionary<string, object> msg)
        {
            object value;
            return msg.TryGetValue("id", out value) ? value : 0;
        }

        private static string Str(Dictionary<string, object> msg, string key)
        {
            object value;
            if (!msg.TryGetValue(key, out value) || value == null) return null;
            return Convert.ToString(value);
        }

        private static object Val(Dictionary<string, object> msg, string key)
        {
            object value;
            return msg.TryGetValue(key, out value) ? value : null;
        }

        private static Dictionary<string, object> Dict(Dictionary<string, object> msg, string key)
        {
            object value;
            if (!msg.TryGetValue(key, out value)) return null;
            return value as Dictionary<string, object>;
        }
    }
}
