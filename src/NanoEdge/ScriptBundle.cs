using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace NanoEdge
{
    // 整体备份里的一个脚本。真正的身份以 Code 解析出来的 @namespace + @name 为准，
    // 下面的 Name/Namespace/Version 只是给确认卡展示用的冗余信息。
    public sealed class ScriptBundleEntry
    {
        public string Name = "";
        public string Namespace = "";
        public string Version = "";
        public bool Enabled = true;
        public string Code = "";
        public List<string> Requires = new List<string>();
    }

    // 备份里的附加数据。v1 只有一种：pg4-assist 的 DDL 源文本。
    public sealed class ScriptBundleAttachment
    {
        public string Kind = "";
        public string Name = "";
        public string Text = "";
        public string Target = "";
    }

    public sealed class ScriptBundle
    {
        public int Format = 1;
        public string App = "NanoEdge";
        public string CreatedUtc = "";
        public List<ScriptBundleEntry> Scripts = new List<ScriptBundleEntry>();
        public List<ScriptBundleAttachment> Attachments = new List<ScriptBundleAttachment>();
    }

    public sealed class ScriptBundlePlan
    {
        public int Added;
        public int Updated;
        public List<string> Names = new List<string>();
    }

    // 备份文件的读写 + 合并/覆盖。全是纯逻辑，不碰 UI，方便自测直接断言。
    public static class ScriptBundleFile
    {
        public const int FormatVersion = 1;
        public const string DdlKind = "pg4-ddl";
        public const int MaxBytes = 64 * 1024 * 1024;

        private static readonly JavaScriptSerializer Serializer =
            new JavaScriptSerializer { MaxJsonLength = MaxBytes };

        public static ScriptBundle FromStore(IEnumerable<ScriptBundleAttachment> attachments = null)
        {
            var bundle = new ScriptBundle { CreatedUtc = DateTime.UtcNow.ToString("o") };
            foreach (var script in ScriptStore.All())
            {
                bundle.Scripts.Add(new ScriptBundleEntry
                {
                    Name = script.Name,
                    Namespace = script.Namespace,
                    Version = script.Version,
                    Enabled = script.Enabled,
                    Code = script.Code ?? "",
                    Requires = (script.RequireCode ?? new List<string>()).ToList()
                });
            }
            if (attachments != null) bundle.Attachments = attachments.ToList();
            return bundle;
        }

        public static string ToJson(ScriptBundle bundle)
        {
            return Serializer.Serialize(bundle);
        }

        public static bool TryParse(string json, out ScriptBundle bundle, out string error)
        {
            bundle = null;
            error = "";
            if (string.IsNullOrWhiteSpace(json))
            {
                error = L.T("备份文件为空");
                return false;
            }
            if (json.Length > MaxBytes)
            {
                error = L.T("备份文件过大");
                return false;
            }
            try { bundle = Serializer.Deserialize<ScriptBundle>(json); }
            catch (Exception ex)
            {
                error = L.T("备份文件解析失败：") + ex.Message;
                return false;
            }
            if (bundle == null || bundle.Format != FormatVersion)
            {
                error = L.T("不支持的备份格式");
                return false;
            }
            if (bundle.Scripts == null) bundle.Scripts = new List<ScriptBundleEntry>();
            if (bundle.Attachments == null) bundle.Attachments = new List<ScriptBundleAttachment>();
            foreach (var entry in bundle.Scripts)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Code))
                {
                    error = L.T("备份里有脚本内容为空");
                    return false;
                }
            }
            foreach (var att in bundle.Attachments)
            {
                if (att != null && att.Kind != DdlKind)
                {
                    error = L.T("备份里有不支持的附件类型");
                    return false;
                }
            }
            return true;
        }

        // 只算不写，给确认卡显示「将新增几个 / 覆盖几个」。
        public static ScriptBundlePlan Plan(ScriptBundle bundle, bool clearFirst)
        {
            var plan = new ScriptBundlePlan();
            var existing = ScriptStore.All().ToList();
            foreach (var entry in bundle.Scripts)
            {
                var parsed = ScriptParser.Parse(entry.Code);
                var hit = !clearFirst && existing.Any(s =>
                    s.Name == parsed.Name && s.Namespace == parsed.Namespace);
                if (hit) plan.Updated++; else plan.Added++;
                plan.Names.Add(parsed.Name);
            }
            return plan;
        }

        public static void Apply(ScriptBundle bundle, bool clearFirst, out int added, out int updated)
        {
            added = 0;
            updated = 0;
            if (clearFirst)
                foreach (var script in ScriptStore.All().ToList()) ScriptStore.Remove(script.Id);

            foreach (var entry in bundle.Scripts)
            {
                var parsed = ScriptParser.Parse(entry.Code);
                var requires = entry.Requires ?? new List<string>();
                var hit = ScriptStore.All().FirstOrDefault(s =>
                    s.Name == parsed.Name && s.Namespace == parsed.Namespace);
                if (hit != null)
                {
                    hit.Code = entry.Code;
                    hit.Enabled = entry.Enabled;
                    ScriptStore.Update(hit, requires);
                    updated++;
                }
                else
                {
                    ScriptStore.Add(entry.Code, entry.Enabled, requires);
                    added++;
                }
            }
        }
    }

    // 恢复后待重建的 DDL 快照。文本只以独立文件躺在磁盘上，
    // 绝不进 GM 值或 .user.js（那两者会整包注入到每一个页面）。
    public sealed class DdlPending
    {
        public string Name = "";
        public string Text = "";
        public string Target = "";
        public string Token = "";
    }

    public static class DdlRestore
    {
        public static string PendingPath = Path.Combine(AppPaths.Roaming, "ddl-pending.json");
        public static string LastTargetPath = Path.Combine(AppPaths.Roaming, "ddl-target.txt");

        // 恢复后通知主窗口：已经打开的匹配页面可以立刻试一次，不必等下次导航。
        public static event Action PendingChanged;

        // 导航完成是高频路径：没有待导入状态时连磁盘都不碰。
        // 静态构造里读一次，保证「恢复后没打开目标站点就重启」仍能继续重建。
        private static bool _hasPending;
        public static bool HasPending { get { return _hasPending; } }

        static DdlRestore()
        {
            try
            {
                var pending = Json.Read(PendingPath, (DdlPending)null);
                _hasPending = pending != null && !string.IsNullOrEmpty(pending.Text);
            }
            catch { _hasPending = false; }
        }

        public static DdlPending Load()
        {
            var pending = Json.Read(PendingPath, (DdlPending)null);
            _hasPending = pending != null && !string.IsNullOrEmpty(pending.Text);
            return pending;
        }

        public static void Save(DdlPending pending)
        {
            Json.Write(PendingPath, pending);
            _hasPending = pending != null && !string.IsNullOrEmpty(pending.Text);
            var handler = PendingChanged;
            if (handler != null) handler();
        }

        public static void Clear()
        {
            try { if (File.Exists(PendingPath)) File.Delete(PendingPath); }
            catch { }
            _hasPending = false;
        }

        public static string LastTarget()
        {
            try
            {
                return File.Exists(LastTargetPath)
                    ? File.ReadAllText(LastTargetPath, Encoding.UTF8).Trim() : "";
            }
            catch { return ""; }
        }

        public static void SaveLastTarget(string target)
        {
            try { File.WriteAllText(LastTargetPath, (target ?? "").Trim(), new UTF8Encoding(false)); }
            catch { }
        }

        public static bool Matches(string url, string target)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(target)) return false;
            try
            {
                var source = ScriptParser.ToRegexSource(target);
                return Regex.IsMatch(url, source, RegexOptions.IgnoreCase);
            }
            catch { return false; }
        }

        // 注入到页面的一段自包含脚本：等 __pg4 就绪 → 幂等检查 → 重新导入 → 回执。
        public static string BuildScript(DdlPending pending)
        {
            var js = new JavaScriptSerializer();
            var text = js.Serialize(pending.Text ?? "");
            var name = js.Serialize(pending.Name ?? "");
            var token = js.Serialize(pending.Token ?? "");
            return "(function(){var TEXT=" + text + ",NAME=" + name + ",TOKEN=" + token + ";" +
                   "var tries=0;" +
                   "function done(){try{window.chrome.webview.postMessage({__ne:'ddlDone',token:TOKEN});}catch(e){}}" +
                   "function go(){" +
                   "var api=window.__pg4;" +
                   "if(!api||!api.importDdlText||!api.core||!api.core.listSnapshots){" +
                   "if(++tries<40){setTimeout(go,250);}return;}" +
                   "api.core.listSnapshots().then(function(list){" +
                   "list=list||[];" +
                   "var dup=list.some(function(s){return s&&s.name===NAME&&s.bytes===TEXT.length;});" +
                   "if(dup){done();return;}" +
                   "return api.importDdlText(TEXT,NAME,NAME).then(done);" +
                   "}).catch(function(e){});" +
                   "}go();})();";
        }

        public static async Task TryInjectAsync(CoreWebView2 core, string url)
        {
            if (core == null) return;
            var pending = Load();
            if (pending == null || string.IsNullOrEmpty(pending.Text)) return;
            if (!Matches(url, pending.Target)) return;
            try { await core.ExecuteScriptAsync(BuildScript(pending)); }
            catch (Exception ex) { Diagnostics.Log("ddl restore inject failed: " + ex.Message); }
        }

        // 只有带正确 token 的回执才清标记，页面伪造不了。
        public static void Complete(string token)
        {
            var pending = Load();
            if (pending == null || string.IsNullOrEmpty(pending.Token)) return;
            if (!string.Equals(pending.Token, token, StringComparison.Ordinal)) return;
            Clear();
            Diagnostics.Log("ddl snapshot rebuilt from backup");
        }
    }
}
