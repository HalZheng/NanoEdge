using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace NanoEdge
{
    // =====================================================================
    // pgAdmin 一键重登 / 自动重登 —— 基础层
    //
    // 约定（与 SPEC 的差异已在需求对齐时说明）：
    //  * JSON 用项目现有的 JavaScriptSerializer，不引 Newtonsoft.Json。
    //  * 文案走 L.T(中文原文)，不建 .resx。
    //  * 站点永不写死，配置里也不存固定站点：运行时直接复用用户脚本的 @match
    //    匹配逻辑，找「当前页面命中的那条规则」，用页面 origin + 该规则的路径前缀
    //    拼出 BaseUrl。凭据按「脚本」存一组，跟着脚本匹配到哪个页面就用在哪个页面。
    // =====================================================================

    // ---------- 5.3 配置：落在 scripts.json 的脚本记录里 ----------
    public sealed class PgAdminReloginSettings
    {
        public bool Enabled;
        public bool AutoRelogin = true;
    }

    // 解析后的站点：所有 URL 都由它拼出来，别在别处再拼一次。
    internal sealed class PgAdminSiteConfig
    {
        public UserScript Script;
        public PgAdminReloginSettings Settings;
        public string BaseUrl = "";
        public string Origin = "";
        public string BasePath = "";

        public string LoginUrl { get { return BaseUrl + "/login"; } }
        public string AuthUrl { get { return BaseUrl + "/authenticate/login"; } }
        public string BrowserUrl { get { return BaseUrl + "/browser/"; } }
        public string LoginPath { get { return BasePath + "/login"; } }
        public string BrowserPath { get { return BasePath + "/browser"; } }
    }

    // ---------- 15.1 站点推导 ----------
    internal static class PgAdminSiteResolver
    {
        private static readonly Regex MatchPattern = new Regex(
            @"^(\*|https?)://([^/]+)(/.*)?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // 无法确定唯一站点（scheme 为 *、host 含通配符、格式非法）时返回 null。
        public static string TryGetOrigin(string pattern)
        {
            var m = MatchPattern.Match((pattern ?? string.Empty).Trim());
            if (!m.Success) return null;

            var scheme = m.Groups[1].Value.ToLowerInvariant();
            var hostPort = m.Groups[2].Value;
            if (scheme == "*" || hostPort.IndexOf('*') >= 0) return null;

            var origin = scheme + "://" + hostPort.ToLowerInvariant();
            Uri parsed;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out parsed)) return null;
            if (parsed.Scheme != "http" && parsed.Scheme != "https") return null;
            if (string.IsNullOrEmpty(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo)) return null;
            return parsed.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        }

        // "/pgadmin/*" -> "/pgadmin"；根路径返回空字符串。
        public static string GetPathPrefix(string pattern)
        {
            var m = MatchPattern.Match((pattern ?? string.Empty).Trim());
            if (!m.Success || !m.Groups[3].Success) return string.Empty;
            var path = m.Groups[3].Value.TrimEnd('*').TrimEnd('/');
            return path.Length == 0 || path[0] != '/' ? string.Empty : path;
        }

        public static void Decompose(string baseUrl, out string origin, out string basePath)
        {
            origin = "";
            basePath = "";
            Uri parsed;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out parsed)) return;
            origin = parsed.GetLeftPart(UriPartial.Authority);
            var path = parsed.AbsolutePath.TrimEnd('/');
            basePath = path == "/" ? "" : path;
        }

        // 复用用户脚本功能那套 Chrome 匹配规则（ScriptParser.ToRegexSource），
        // 不另写一份。@match 的路径是 /*，所以校验时补一个 / 才是完整 URL。
        public static bool MatchesAny(IEnumerable<string> patterns, string url)
        {
            if (patterns == null || string.IsNullOrEmpty(url)) return false;
            foreach (var pattern in patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                try
                {
                    var source = ScriptParser.ToRegexSource(pattern);
                    if (string.IsNullOrEmpty(source)) continue;
                    if (Regex.IsMatch(url, source, RegexOptions.IgnoreCase)) return true;
                }
                catch { }
            }
            return false;
        }

        public static string PathOf(string url)
        {
            Uri parsed;
            return Uri.TryCreate(url, UriKind.Absolute, out parsed) ? parsed.AbsolutePath : "";
        }

        // 直接复用用户脚本的匹配规则：找出「当前页面命中的那条 @match」，
        // 用当前页面的 origin + 那条规则的路径前缀拼出 BaseUrl。
        // 站点完全由运行时决定，配置里不需要存任何固定地址，也不需要用户去选。
        public static string DeriveBaseUrl(IEnumerable<string> patterns, string pageUrl)
        {
            Uri page;
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out page)) return null;
            if (page.Scheme != "http" && page.Scheme != "https") return null;
            if (string.IsNullOrEmpty(page.Host) || patterns == null) return null;
            foreach (var pattern in patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                if (!MatchesAny(new[] { pattern }, pageUrl)) continue;
                return page.GetLeftPart(UriPartial.Authority) + GetPathPrefix(pattern);
            }
            return null;
        }
    }

    // ---------- 站点解析：先看当前页面，再取列表第一个匹配的脚本 ----------
    internal static class PgAdminReloginConfig
    {
        public static bool AnyEnabled()
        {
            foreach (var script in ScriptStore.All())
            {
                if (!script.Enabled) continue;
                var cfg = script.PgadminRelogin;
                if (cfg != null && cfg.Enabled) return true;
            }
            return false;
        }

        // ScriptStore.All() 就是脚本列表顺序：取第一个「匹配当前页面且启用重登」的脚本。
        // 站点由 DeriveBaseUrl 从命中的 @match 现场推导，配置里没有任何固定地址。
        public static PgAdminSiteConfig ForUrl(string pageUrl)
        {
            if (string.IsNullOrEmpty(pageUrl)) return null;
            foreach (var script in ScriptStore.All())
            {
                if (!script.Enabled) continue;
                var config = script.PgadminRelogin;
                if (config == null || !config.Enabled) continue;
                var baseUrl = PgAdminSiteResolver.DeriveBaseUrl(script.Matches, pageUrl);
                if (string.IsNullOrEmpty(baseUrl)) continue;
                string origin, path;
                PgAdminSiteResolver.Decompose(baseUrl, out origin, out path);
                if (string.IsNullOrEmpty(origin)) continue;
                return new PgAdminSiteConfig
                {
                    Script = script,
                    Settings = config,
                    BaseUrl = baseUrl,
                    Origin = origin,
                    BasePath = path
                };
            }
            return null;
        }
    }

    // 编辑页保存凭据后通知宿主：解除自动重登的熄灭状态（7.3）。
    internal static class PgAdminReloginEvents
    {
        public static event Action Saved;
        public static void RaiseSaved()
        {
            var handler = Saved;
            if (handler != null) handler();
        }
    }

    // ---------- 15.2 SecureString -> UTF-8 byte[] ----------
    internal static class SecureBytes
    {
        public static byte[] ToUtf8(SecureString secure)
        {
            if (secure == null || secure.Length == 0) return new byte[0];
            var ptr = Marshal.SecureStringToGlobalAllocUnicode(secure);
            var chars = new char[secure.Length];
            try
            {
                Marshal.Copy(ptr, chars, 0, chars.Length);
                return Encoding.UTF8.GetBytes(chars);
            }
            finally
            {
                Array.Clear(chars, 0, chars.Length);
                Marshal.ZeroFreeGlobalAllocUnicode(ptr);
            }
        }
    }

    // ---------- 15.4 RFC 3986 表单编码 ----------
    internal static class PgAdminFormEncoding
    {
        private const string Hex = "0123456789ABCDEF";

        private static bool IsUnreserved(byte b)
        {
            return (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') ||
                   (b >= '0' && b <= '9') || b == '-' || b == '.' || b == '_' || b == '~';
        }

        // 测试向量：a b&c+d%e#f=g/h?i -> a%20b%26c%2Bd%25e%23f%3Dg%2Fh%3Fi
        internal static byte[] Encode(byte[] utf8)
        {
            if (utf8 == null) return new byte[0];
            var length = 0;
            foreach (var b in utf8) length += IsUnreserved(b) ? 1 : 3;
            var output = new byte[length];
            var pos = 0;
            foreach (var b in utf8)
            {
                if (IsUnreserved(b)) { output[pos++] = b; continue; }
                output[pos++] = (byte)'%';
                output[pos++] = (byte)Hex[b >> 4];
                output[pos++] = (byte)Hex[b & 0x0F];
            }
            return output;
        }

        internal static string EncodeToString(string text)
        {
            return Encoding.ASCII.GetString(Encode(Encoding.UTF8.GetBytes(text ?? "")));
        }

        // 密码按字节编码，全程不经过 string。返回值用完必须 Array.Clear。
        internal static byte[] BuildFormBody(string csrfToken, string language,
                                             string userName, byte[] password)
        {
            var prefix = Encoding.ASCII.GetBytes(
                "csrf_token=" + EncodeToString(csrfToken) +
                "&email=" + EncodeToString(userName) + "&password=");
            var suffix = Encoding.ASCII.GetBytes(
                "&language=" + EncodeToString(string.IsNullOrEmpty(language) ? "en" : language) +
                "&internal_button=Login");

            var pw = password ?? new byte[0];
            var encodedLength = 0;
            foreach (var b in pw) encodedLength += IsUnreserved(b) ? 1 : 3;

            var body = new byte[prefix.Length + encodedLength + suffix.Length];
            Buffer.BlockCopy(prefix, 0, body, 0, prefix.Length);
            var pos = prefix.Length;
            foreach (var b in pw)
            {
                if (IsUnreserved(b)) { body[pos++] = b; continue; }
                body[pos++] = (byte)'%';
                body[pos++] = (byte)Hex[b >> 4];
                body[pos++] = (byte)Hex[b & 0x0F];
            }
            Buffer.BlockCopy(suffix, 0, body, pos, suffix.Length);
            return body;
        }
    }

    // ---------- 5.1 DPAPI 凭据存储 ----------
    internal sealed class PgAdminCredentialStore
    {
        // 自测注入点：指向临时文件，绝不碰用户真实凭据。
        internal static string PathOverride { get; set; }

        private static string FilePath
        {
            get
            {
                return string.IsNullOrEmpty(PathOverride)
                    ? Path.Combine(AppPaths.Local, "secure", "pgadmin-relogin.bin")
                    : PathOverride;
            }
        }

        private static string SecureDir { get { return Path.GetDirectoryName(FilePath); } }

        // v2：一组凭据跟着「脚本」走，不再按站点分条。站点是运行时按 @match
        // 从当前页面推导的，保存时根本不需要知道具体地址。
        private sealed class Entry
        {
            public string ScriptId = "";
            public string UserName = "";
            public string Password = "";
        }

        private sealed class Document
        {
            public int Version = 2;
            public List<Entry> Entries = new List<Entry>();
        }

        // 凭据一取出来就把密码转成 byte[]，字符串引用随即丢弃。
        internal sealed class Credential
        {
            public string UserName = "";
            public byte[] Password = new byte[0];   // 用完 Array.Clear
        }

        // 文件不存在 / 解密失败在调用方分别处理：解密失败必须提示并保留文件，绝不覆盖。
        private static List<Entry> ReadAll()
        {
            if (!File.Exists(FilePath)) return new List<Entry>();
            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            try
            {
                var doc = new JavaScriptSerializer().Deserialize<Document>(Encoding.UTF8.GetString(plain));
                // 旧版 v1 按站点存，无法映射到脚本：视为无凭据、保留文件、不自动删除。
                if (doc == null || doc.Version != 2 || doc.Entries == null) return new List<Entry>();
                return doc.Entries;
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        internal static bool Has(string scriptId)
        {
            return Find(scriptId) != null;
        }

        private static Entry Find(string scriptId)
        {
            if (string.IsNullOrEmpty(scriptId)) return null;
            foreach (var entry in ReadAll())
                if (entry != null && string.Equals(entry.ScriptId, scriptId, StringComparison.Ordinal))
                    return entry;
            return null;
        }

        internal static Credential TryGet(string scriptId)
        {
            var entry = Find(scriptId);
            if (entry == null) return null;
            return new Credential
            {
                UserName = entry.UserName ?? "",
                Password = Encoding.UTF8.GetBytes(entry.Password ?? "")
            };
        }

        internal static void Save(string scriptId, string userName, byte[] passwordUtf8)
        {
            var entries = ReadAll();
            entries.RemoveAll(e => e == null ||
                string.Equals(e.ScriptId, scriptId, StringComparison.Ordinal));
            // JSON 序列化需要 string，这是唯一的字符串副本。
            entries.Add(new Entry
            {
                ScriptId = scriptId,
                UserName = userName ?? "",
                Password = Encoding.UTF8.GetString(passwordUtf8 ?? new byte[0])
            });
            WriteAll(entries);
        }

        internal static void Clear(string scriptId)
        {
            var entries = ReadAll();
            var removed = entries.RemoveAll(e => e == null ||
                string.Equals(e.ScriptId, scriptId, StringComparison.Ordinal));
            if (removed == 0) return;
            if (entries.Count == 0)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return;
            }
            WriteAll(entries);
        }

        private static void WriteAll(List<Entry> entries)
        {
            EnsureSecureDir();
            var plain = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(
                new Document { Version = 2, Entries = entries }));
            byte[] cipher;
            try
            {
                cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }

            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, cipher);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }

        private static void EnsureSecureDir()
        {
            if (Directory.Exists(SecureDir)) return;
            var parent = Path.GetDirectoryName(SecureDir);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User;
            if (user != null)
                acl.AddAccessRule(new FileSystemAccessRule(
                    user, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(SecureDir, acl);
        }
    }

    // ---------- 10 日志：只写事件 / 路径 / 状态码，绝不写凭据 ----------
    internal static class PgAdminLog
    {
        internal static string PathOverride { get; set; }

        private static readonly object Gate = new object();
        private static readonly string[] Sensitive =
        {
            "password", "passwd", "pwd", "csrf", "token", "cookie", "authorization",
            "set-cookie", "form body", "formbody", "username", "user name", "email"
        };

        private static string FilePath
        {
            get
            {
                return string.IsNullOrEmpty(PathOverride)
                    ? Path.Combine(AppPaths.Logs, "pgadmin-relogin.log")
                    : PathOverride;
            }
        }

        public static void Event(string trigger, string result, double seconds)
        {
            Write("RELOGIN | Trigger=" + SafeWord(trigger) + " | Result=" + SafeWord(result) +
                  " | Duration=" + seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s");
        }

        // detail 只允许是路径或状态码，不许传 URL / 响应正文。
        public static void Stage(string stage, string detail)
        {
            Write("STAGE | " + SafeWord(stage) + " | " + SafeWord(detail));
        }

        public static void Exception(string stage, Exception ex)
        {
            if (ex == null) return;
            var message = Sanitize(ex.Message);
            Write("ERROR | " + SafeWord(stage) + " | " + ex.GetType().Name +
                  (message.Length == 0 ? "" : " | " + message));
        }

        private static string SafeWord(string text)
        {
            return string.IsNullOrEmpty(text) ? "-" : text.Replace("\r", " ").Replace("\n", " ");
        }

        private static string Sanitize(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            var lower = message.ToLowerInvariant();
            foreach (var word in Sensitive)
                if (lower.Contains(word)) return "";      // 可疑消息整条丢掉，只留异常类型名
            var text = Regex.Replace(message, @"https?://[^\s]+", "[url]");
            text = Regex.Replace(text, @"[?&][^\s]+", "[query]");
            if (text.Length > 200) text = text.Substring(0, 200);
            return text.Replace("\r", " ").Replace("\n", " ");
        }

        private static void Write(string line)
        {
            lock (Gate)
            {
                try
                {
                    var dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine,
                        Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}
