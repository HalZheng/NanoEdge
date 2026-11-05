using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace NanoEdge
{
    public sealed class SnapField
    {
        public string Sel = "";
        public string Kind = "";   // value | ce | check
        public string Value = "";
    }

    // SPEC 4.4 丢弃快照。只存顶层文档的 input/textarea/contenteditable，
    // 跳过 password/file，单标签上限 256KB。
    public sealed class TabSnapshot
    {
        public string Url = "";
        public string Title = "";
        public int ScrollY;
        public double ScrollRatio;
        public double Zoom = 1.0;
        public List<SnapField> Forms = new List<SnapField>();
        public DateTime SavedAtUtc = DateTime.UtcNow;
        public bool Restorable = true;
        public int Bytes;

        public const int MaxBytes = 256 * 1024;

        public string ToJson()
        {
            return new JavaScriptSerializer { MaxJsonLength = MaxBytes * 2 }.Serialize(this);
        }

        public static TabSnapshot FromJson(string json)
        {
            try
            {
                var snapshot = new JavaScriptSerializer { MaxJsonLength = MaxBytes * 2 }
                    .Deserialize<TabSnapshot>(json);
                if (snapshot == null) return null;
                if (snapshot.Forms == null) snapshot.Forms = new List<SnapField>();
                return snapshot;
            }
            catch { return null; }
        }
    }

    internal static class SnapshotScript
    {
        // Returns a JSON string (JS-side JSON.stringify). The host reads the extra
        // level of string-encoding that ExecuteScriptAsync adds.
        public const string Capture = @"
(function () {
  function sel(el) {
    if (el.id) {
      try { if (document.querySelectorAll('#' + CSS.escape(el.id)).length === 1) return '#' + CSS.escape(el.id); } catch (e) {}
    }
    var n = el.getAttribute('name');
    if (n) return el.tagName.toLowerCase() + '[name=""' + n.replace(/""/g, '\\""') + '""]';
    var parts = [], node = el, guard = 0;
    while (node && node.nodeType === 1 && guard++ < 5) {
      var idx = 1, sib = node;
      while ((sib = sib.previousElementSibling)) { if (sib.tagName === node.tagName) idx++; }
      parts.unshift(node.tagName.toLowerCase() + ':nth-of-type(' + idx + ')');
      node = node.parentElement;
    }
    return parts.join('>');
  }
  var fields = [];
  var nodes = document.querySelectorAll('input,textarea,[contenteditable]');
  for (var i = 0; i < nodes.length && fields.length < 200; i++) {
    var el = nodes[i];
    var type = (el.getAttribute('type') || '').toLowerCase();
    if (type === 'password' || type === 'file' || type === 'hidden' || el.disabled) continue;
    if (type === 'checkbox' || type === 'radio') {
      if (el.checked) fields.push({ Sel: sel(el), Kind: 'check', Value: '1' });
      continue;
    }
    var value = el.isContentEditable ? (el.innerText || '') : el.value;
    if (!value) continue;
    fields.push({ Sel: sel(el), Kind: el.isContentEditable ? 'ce' : 'value', Value: String(value).slice(0, 2000) });
  }
  var doc = document.documentElement;
  var max = Math.max(0, doc.scrollHeight - window.innerHeight);
  return JSON.stringify({
    Url: location.href,
    Title: document.title,
    ScrollY: Math.round(window.scrollY || 0),
    ScrollRatio: max > 0 ? Math.round(window.scrollY / max * 1000) / 1000 : 0,
    Forms: fields
  });
})()
";

        public static string Restore(TabSnapshot snapshot)
        {
            var literal = new JavaScriptSerializer().Serialize(snapshot.ToJson());
            return @"
(function () {
  var d = JSON.parse(" + literal + @");
  var deadline = Date.now() + 3000;
  function apply() {
    var pending = 0;
    for (var i = 0; i < d.Forms.length; i++) {
      var f = d.Forms[i], el = null;
      try { el = document.querySelector(f.Sel); } catch (e) { el = null; }
      if (!el) { pending++; continue; }
      if (f.Kind === 'ce') { if (el.innerText !== f.Value) el.innerText = f.Value; continue; }
      if (f.Kind === 'check') { el.checked = true; continue; }
      var proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
      var setter = Object.getOwnPropertyDescriptor(proto, 'value');
      if (setter && setter.set) setter.set.call(el, f.Value); else el.value = f.Value;
      el.dispatchEvent(new Event('input', { bubbles: true }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
    }
    window.scrollTo(0, d.ScrollY);
    if (pending > 0 && Date.now() < deadline) setTimeout(apply, 200);
  }
  apply();
  setTimeout(function () { window.scrollTo(0, d.ScrollY); }, 500);
  'restored';
})()
";
        }

        public static bool TooBig(TabSnapshot snapshot)
        {
            return snapshot.Bytes > TabSnapshot.MaxBytes;
        }

        public static int Measure(TabSnapshot snapshot)
        {
            try { return Encoding.UTF8.GetByteCount(snapshot.ToJson()); }
            catch { return int.MaxValue; }
        }
    }
}
