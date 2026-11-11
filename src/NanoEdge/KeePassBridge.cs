using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NanoEdge
{
    // =====================================================================
    // KeePassXC 直连桥（SPEC 8.x）
    //
    // 为什么不用 KeePassXC-Browser 扩展：WebView2 不实现 native messaging，
    // 也不给扩展任何 UI，装了也连不上数据库。这里改成宿主自己扮演「浏览器」：
    // 拉起官方 keepassxc-proxy.exe（它的 stdin/stdout 就是 native messaging 通道），
    // 走 change-public-keys → associate → get-logins，中间用 crypto_box 加密。
    //
    // 一句话：密码始终待在 KeePassXC 里，NanoEdge 只在填充的那一瞬间拿到明文。
    // =====================================================================

    internal sealed class KeePassCredential
    {
        public string Name = "";
        public string Login = "";
        public string Password = "";
        public string Uuid = "";
        public string Group = "";
    }

    internal enum KeePassStatus
    {
        Ok,
        NotInstalled,      // 找不到 keepassxc-proxy.exe
        NotRunning,        // proxy 拉不起来
        IntegrationOff,    // KeePassXC 没开「浏览器集成」，管道不存在
        NotAssociated,     // 尚未关联，需要先走一次 associate
        Timeout,
        Error
    }

    internal sealed class KeePassResult
    {
        public KeePassStatus Status = KeePassStatus.Error;
        public string Message = "";
        public List<KeePassCredential> Entries = new List<KeePassCredential>();

        public bool Ok { get { return Status == KeePassStatus.Ok; } }
    }

    // 持久化的关联信息。ConnectionId 必须原样回填到后续每个请求的 id 字段，
    // 用 clientID 顶替会得到 errorCode 15「未找到登录信息」，非常容易误判。
    internal sealed class KeePassConfig
    {
        public int Version = 1;
        public string ConnectionId = "";
        public string IdPublic = "";
        public string IdSecret = "";     // base64(DPAPI 密文)
    }

    internal static class KeePassBridge
    {
        private const int KeyBytes = 32;
        private const int NonceBytes = 24;
        private const int MacBytes = 16;

        internal static string ConfigPath = Path.Combine(AppPaths.Roaming, "keepassxc.json");
        internal static string ProxyOverride { get; set; }   // 自测/兜底：用户手选路径
        internal static bool Persist = true;           // 自测置 false，绝不碰真实配置

        private static readonly JavaScriptSerializer Serializer =
            new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };

        // ---------------- libsodium ----------------

        [DllImport("libsodium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sodium_init();

        [DllImport("libsodium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int crypto_box_keypair(byte[] pk, byte[] sk);

        [DllImport("libsodium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int crypto_box_easy(byte[] c, byte[] m, ulong mlen,
                                                 byte[] n, byte[] pk, byte[] sk);

        [DllImport("libsodium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int crypto_box_open_easy(byte[] m, byte[] c, ulong clen,
                                                       byte[] n, byte[] pk, byte[] sk);

        [DllImport("libsodium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void randombytes_buf(byte[] buf, UIntPtr size);

        private static bool _sodiumReady;
        private static string _sodiumError = "";

        private static bool EnsureSodium()
        {
            if (_sodiumReady) return true;
            if (_sodiumError.Length > 0) return false;
            try
            {
                if (sodium_init() < 0) { _sodiumError = "sodium_init failed"; return false; }
                _sodiumReady = true;
                return true;
            }
            catch (Exception ex)
            {
                _sodiumError = ex.Message;
                return false;
            }
        }

        private static void RandomBytes(byte[] buffer)
        {
            randombytes_buf(buffer, (UIntPtr)buffer.Length);
        }

        private static void BoxKeyPair(out byte[] pk, out byte[] sk)
        {
            pk = new byte[KeyBytes];
            sk = new byte[KeyBytes];
            crypto_box_keypair(pk, sk);
        }

        private static byte[] BoxSeal(byte[] message, byte[] nonce, byte[] peerPk, byte[] mySk)
        {
            var cipher = new byte[message.Length + MacBytes];
            if (crypto_box_easy(cipher, message, (ulong)message.Length, nonce, peerPk, mySk) != 0)
                throw new CryptographicException("crypto_box_easy failed");
            return cipher;
        }

        private static byte[] BoxOpen(byte[] cipher, byte[] nonce, byte[] peerPk, byte[] mySk)
        {
            if (cipher == null || cipher.Length < MacBytes) return null;
            var plain = new byte[cipher.Length - MacBytes];
            if (crypto_box_open_easy(plain, cipher, (ulong)cipher.Length, nonce, peerPk, mySk) != 0)
                return null;
            return plain;
        }

        // ---------------- 配置 ----------------

        internal static KeePassConfig LoadConfig()
        {
            var config = Json.Read(ConfigPath, (KeePassConfig)null);
            if (config == null) return null;
            if (string.IsNullOrEmpty(config.ConnectionId) || string.IsNullOrEmpty(config.IdSecret)) return null;
            return config;
        }

        internal static string DecodeSecret(KeePassConfig config)
        {
            if (config == null || string.IsNullOrEmpty(config.IdSecret)) return null;
            try
            {
                var raw = Convert.FromBase64String(config.IdSecret);
                var plain = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(plain);
            }
            catch { return null; }   // 换用户/换机器：当作没关联，不要覆盖掉文件
        }

        internal static void SaveConfig(string connectionId, byte[] idPk, byte[] idSk)
        {
            if (!Persist) return;
            try
            {
                var protectedKey = ProtectedData.Protect(idSk, null, DataProtectionScope.CurrentUser);
                Json.Write(ConfigPath, new KeePassConfig
                {
                    ConnectionId = connectionId,
                    IdPublic = Convert.ToBase64String(idPk),
                    IdSecret = Convert.ToBase64String(protectedKey)
                });
            }
            catch (Exception ex) { Diagnostics.Log("keepassxc save config failed: " + ex.Message); }
        }

        internal static void ForgetConfig()
        {
            try { if (File.Exists(ConfigPath)) File.Delete(ConfigPath); }
            catch (Exception ex) { Diagnostics.Log("keepassxc forget failed: " + ex.Message); }
        }

        // ---------------- 环境探测 ----------------

        // KeePassXC 2.8 的管道名带用户名后缀：\\.\pipe\org.keepassxc.KeePassXC.BrowserServer_<user>
        internal static bool IntegrationRunning()
        {
            try
            {
                foreach (var pipe in Directory.GetFiles(@"\\.\pipe\"))
                    if (pipe.IndexOf("org.keepassxc.KeePassXC.BrowserServer",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
            }
            catch (Exception ex) { Diagnostics.Log("pipe enum failed: " + ex.Message); }
            return false;
        }

        internal static string FindProxy()
        {
            if (!string.IsNullOrEmpty(ProxyOverride) && File.Exists(ProxyOverride)) return ProxyOverride;

            // 1) 官方 manifest 里记的路径最准（用户可能装在非默认位置）
            var manifest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "keepassxc", "org.keepassxc.keepassxc_browser.json");
            try
            {
                if (File.Exists(manifest))
                {
                    var map = Serializer.Deserialize<Dictionary<string, object>>(
                        File.ReadAllText(manifest, Encoding.UTF8));
                    object path;
                    if (map != null && map.TryGetValue("path", out path))
                    {
                        var candidate = Convert.ToString(path);
                        if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch (Exception ex) { Diagnostics.Log("read keepassxc manifest failed: " + ex.Message); }

            // 2) 默认安装位置
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var candidate = Path.Combine(root, "KeePassXC", "keepassxc-proxy.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        // ---------------- 会话 ----------------

        private sealed class Session : IDisposable
        {
            public Process Process;
            public Stream Input;
            public Stream Output;
            public byte[] ClientPk;
            public byte[] ClientSk;
            public byte[] HostPk;
            public string ClientId;

            public void Dispose()
            {
                try { if (Process != null && !Process.HasExited) Process.Kill(); } catch { }
                try { if (Process != null) Process.Dispose(); } catch { }
                Process = null;
            }
        }

        private static Session StartSession(out KeePassStatus status, out string message)
        {
            status = KeePassStatus.Ok;
            message = "";

            if (!EnsureSodium())
            {
                status = KeePassStatus.Error;
                message = "缺少 libsodium：" + _sodiumError;
                return null;
            }
            if (!IntegrationRunning())
            {
                status = KeePassStatus.IntegrationOff;
                message = "KeePassXC 未开启「浏览器集成」";
                return null;
            }
            var proxy = FindProxy();
            if (string.IsNullOrEmpty(proxy))
            {
                status = KeePassStatus.NotInstalled;
                message = "找不到 keepassxc-proxy.exe";
                return null;
            }

            var info = new ProcessStartInfo(proxy)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process process;
            try { process = Process.Start(info); }
            catch (Exception ex)
            {
                status = KeePassStatus.NotRunning;
                message = "启动 proxy 失败：" + ex.Message;
                return null;
            }
            if (process == null)
            {
                status = KeePassStatus.NotRunning;
                message = "启动 proxy 失败";
                return null;
            }

            var session = new Session
            {
                Process = process,
                Input = process.StandardInput.BaseStream,
                Output = process.StandardOutput.BaseStream,
                ClientId = NewClientId()
            };
            return session;
        }

        // clientID 只是本会话的标识，随便什么稳定字符串都行；
        // 真正要跟库里一致的是 associate 返回的 id。
        private static string NewClientId()
        {
            return "NanoEdge-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static void WriteFrame(Stream stream, object payload)
        {
            var json = Serializer.Serialize(payload);
            var body = Encoding.UTF8.GetBytes(json);
            var header = new byte[4];
            header[0] = (byte)(body.Length & 0xFF);
            header[1] = (byte)((body.Length >> 8) & 0xFF);
            header[2] = (byte)((body.Length >> 16) & 0xFF);
            header[3] = (byte)((body.Length >> 24) & 0xFF);
            stream.Write(header, 0, 4);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) return null;
                offset += read;
            }
            return buffer;
        }

        // proxy 在 KeePassXC 没响应时不会报错，只会一直不出声，所以必须自带超时。
        private static Dictionary<string, object> ReadFrame(Session session, TimeSpan timeout)
        {
            var task = Task.Run(() =>
            {
                var header = ReadExact(session.Output, 4);
                if (header == null) return null;
                var length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
                if (length <= 0 || length > 8 * 1024 * 1024) return null;
                var body = ReadExact(session.Output, length);
                if (body == null) return null;
                return Serializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
            });
            if (!task.Wait(timeout)) return null;
            try { return task.Result; }
            catch { return null; }
        }

        private static bool Handshake(Session session, TimeSpan timeout)
        {
            byte[] pk, sk;
            BoxKeyPair(out pk, out sk);
            session.ClientPk = pk;
            session.ClientSk = sk;

            var nonce = new byte[NonceBytes];
            RandomBytes(nonce);

            WriteFrame(session.Input, new Dictionary<string, object>
            {
                { "action", "change-public-keys" },
                { "publicKey", Convert.ToBase64String(pk) },
                { "nonce", Convert.ToBase64String(nonce) },
                { "clientID", session.ClientId }
            });

            var response = ReadFrame(session, timeout);
            if (response == null) return false;
            object hostPk;
            if (!response.TryGetValue("publicKey", out hostPk)) return false;
            try { session.HostPk = Convert.FromBase64String(Convert.ToString(hostPk)); }
            catch { return false; }
            return session.HostPk != null && session.HostPk.Length == KeyBytes;
        }

        // 发一条加密请求并解出响应明文；errorCode 会在解析层转成可读状态。
        private static Dictionary<string, object> SendEncrypted(Session session, string action,
            Dictionary<string, object> payload, TimeSpan timeout, out string error)
        {
            error = null;
            var nonce = new byte[NonceBytes];
            RandomBytes(nonce);
            var json = Serializer.Serialize(payload);
            var cipher = BoxSeal(Encoding.UTF8.GetBytes(json), nonce, session.HostPk, session.ClientSk);

            WriteFrame(session.Input, new Dictionary<string, object>
            {
                { "action", action },
                { "message", Convert.ToBase64String(cipher) },
                { "nonce", Convert.ToBase64String(nonce) },
                { "clientID", session.ClientId }
            });

            var response = ReadFrame(session, timeout);
            if (response == null) { error = "timeout"; return null; }

            object messageField;
            if (!response.TryGetValue("message", out messageField))
            {
                // 未加密的错误响应（例如未关联 / 参数不对）
                error = ResponseError(response);
                return response;
            }

            try
            {
                var cipherBytes = Convert.FromBase64String(Convert.ToString(messageField));
                var responseNonce = Convert.FromBase64String(Convert.ToString(response["nonce"]));
                var plain = BoxOpen(cipherBytes, responseNonce, session.HostPk, session.ClientSk);
                if (plain == null) { error = "decrypt failed"; return null; }
                return Serializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(plain));
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        private static string ResponseError(Dictionary<string, object> response)
        {
            if (response == null) return "";
            object code, text;
            response.TryGetValue("errorCode", out code);
            response.TryGetValue("error", out text);
            var codeText = code == null ? "" : Convert.ToString(code);
            var messageText = text == null ? "" : Convert.ToString(text);
            return string.IsNullOrEmpty(codeText) ? messageText : ("[" + codeText + "] " + messageText);
        }

        // ---------------- 对外 API ----------------

        // 自测用：只验证 libsodium 能加载、加解密自洽，不碰任何外部进程。
        internal static bool SelfTestCrypto(out string detail)
        {
            detail = "";
            if (!EnsureSodium()) { detail = "libsodium 加载失败：" + _sodiumError; return false; }
            try
            {
                byte[] aPk, aSk, bPk, bSk;
                BoxKeyPair(out aPk, out aSk);
                BoxKeyPair(out bPk, out bSk);
                var nonce = new byte[NonceBytes];
                RandomBytes(nonce);
                const string sample = "{\"t\":\"中文也要能过\"}";
                var message = Encoding.UTF8.GetBytes(sample);
                var cipher = BoxSeal(message, nonce, bPk, aSk);
                var plain = BoxOpen(cipher, nonce, aPk, bSk);
                if (plain == null) { detail = "解密返回 null"; return false; }
                var text = Encoding.UTF8.GetString(plain);
                if (text != sample) { detail = "往返不一致：" + text; return false; }
                detail = "ok, cipher=" + cipher.Length + "B";
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // 首次关联：KeePassXC 会弹窗要求填连接名并点「保存并允许访问」。
        // 这一步没法静默完成，必须有用户在场。
        internal static Task<KeePassResult> AssociateAsync(string connectionName, TimeSpan timeout)
        {
            return Task.Run(() =>
            {
                var result = new KeePassResult();
                KeePassStatus status;
                string message;
                using (var session = StartSession(out status, out message))
                {
                    if (session == null)
                    {
                        result.Status = status;
                        result.Message = message;
                        return result;
                    }
                    try
                    {
                        if (!Handshake(session, TimeSpan.FromSeconds(10)))
                        {
                            result.Status = KeePassStatus.NotRunning;
                            result.Message = "与 KeePassXC 握手失败";
                            return result;
                        }

                        byte[] idPk, idSk;
                        BoxKeyPair(out idPk, out idSk);

                        string error;
                        var response = SendEncrypted(session, "associate", new Dictionary<string, object>
                        {
                            { "action", "associate" },
                            { "key", Convert.ToBase64String(session.ClientPk) },
                            { "idKey", Convert.ToBase64String(idPk) }
                        }, timeout, out error);

                        if (response == null)
                        {
                            result.Status = string.Equals(error, "timeout", StringComparison.Ordinal)
                                ? KeePassStatus.Timeout : KeePassStatus.Error;
                            result.Message = string.Equals(error, "timeout", StringComparison.Ordinal)
                                ? "等待确认超时（请在 KeePassXC 弹窗里点「保存并允许访问」）"
                                : (error ?? "");
                            return result;
                        }
                        if (!IsSuccess(response))
                        {
                            result.Status = KeePassStatus.Error;
                            result.Message = ResponseError(response);
                            return result;
                        }

                        object id;
                        response.TryGetValue("id", out id);
                        var grantedId = id == null ? connectionName : Convert.ToString(id);
                        if (string.IsNullOrEmpty(grantedId)) grantedId = connectionName;

                        SaveConfig(grantedId, idPk, idSk);
                        result.Status = KeePassStatus.Ok;
                        result.Message = grantedId;
                        return result;
                    }
                    finally { session.Dispose(); }
                }
            });
        }

        internal static Task<KeePassResult> GetLoginsAsync(string url)
        {
            return Task.Run(() => GetLoginsCore(url));
        }

        private static KeePassResult GetLoginsCore(string url)
        {
            var result = new KeePassResult();

            var config = LoadConfig();
            if (config == null)
            {
                result.Status = KeePassStatus.NotAssociated;
                result.Message = "尚未与 KeePassXC 建立关联";
                return result;
            }
            var secret = DecodeSecret(config);
            if (string.IsNullOrEmpty(secret))
            {
                result.Status = KeePassStatus.NotAssociated;
                result.Message = "关联密钥无法解密，需要重新关联";
                return result;
            }

            KeePassStatus status;
            string message;
            using (var session = StartSession(out status, out message))
            {
                if (session == null)
                {
                    result.Status = status;
                    result.Message = message;
                    return result;
                }
                try
                {
                    if (!Handshake(session, TimeSpan.FromSeconds(10)))
                    {
                        result.Status = KeePassStatus.NotRunning;
                        result.Message = "与 KeePassXC 握手失败";
                        return result;
                    }

                    string error;

                    // 必须先在本次会话里 test-associate 确认身份，否则直接 get-logins
                    // 会被 KeePassXC 判成「未关联」回 errorCode 8（实测）。
                    var probe = SendEncrypted(session, "test-associate", new Dictionary<string, object>
                    {
                        { "action", "test-associate" },
                        { "id", config.ConnectionId },
                        { "key", config.IdPublic }
                    }, TimeSpan.FromSeconds(15), out error);

                    if (!IsSuccess(probe))
                    {
                        result.Status = KeePassStatus.NotAssociated;
                        result.Message = string.IsNullOrEmpty(error) ? ResponseError(probe) : error;
                        return result;
                    }

                    var response = SendEncrypted(session, "get-logins", new Dictionary<string, object>
                    {
                        { "action", "get-logins" },
                        { "url", url },
                        { "submitUrl", "" },
                        { "httpAuth", "" },
                        { "keys", new object[]
                            {
                                new Dictionary<string, object>
                                {
                                    // 必须是 associate 返回的 id，不能是 clientID。
                                    { "id", config.ConnectionId },
                                    { "key", config.IdPublic }
                                }
                            }
                        }
                    }, TimeSpan.FromSeconds(20), out error);

                    if (response == null)
                    {
                        result.Status = string.Equals(error, "timeout", StringComparison.Ordinal)
                            ? KeePassStatus.Timeout : KeePassStatus.Error;
                        result.Message = error ?? "";
                        return result;
                    }
                    if (!IsSuccess(response))
                    {
                        var code = ErrorCode(response);
                        result.Message = ResponseError(response);
                        // 15 = 没有匹配这个网址的条目，属于正常结果而不是故障。
                        result.Status = code == 15 ? KeePassStatus.Ok : KeePassStatus.NotAssociated;
                        return result;
                    }

                    object entriesField;
                    if (response.TryGetValue("entries", out entriesField))
                    {
                        // JavaScriptSerializer 把 JSON 数组给成 ArrayList，不是 object[]；
                        // 按 IEnumerable 取才不会静默拿到 0 条。
                        var list = entriesField as System.Collections.IEnumerable;
                        if (list != null)
                        {
                            foreach (var item in list)
                            {
                                var map = item as Dictionary<string, object>;
                                if (map == null) continue;
                                result.Entries.Add(new KeePassCredential
                                {
                                    Name = Field(map, "name"),
                                    Login = Field(map, "login"),
                                    Password = Field(map, "password"),
                                    Uuid = Field(map, "uuid"),
                                    Group = Field(map, "group")
                                });
                            }
                        }
                    }
                    result.Status = KeePassStatus.Ok;
                    return result;
                }
                finally { session.Dispose(); }
            }
        }

        private static bool IsSuccess(Dictionary<string, object> response)
        {
            object value;
            if (response == null || !response.TryGetValue("success", out value)) return false;
            return string.Equals(Convert.ToString(value), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static int ErrorCode(Dictionary<string, object> response)
        {
            object value;
            if (response == null || !response.TryGetValue("errorCode", out value)) return 0;
            int code;
            return int.TryParse(Convert.ToString(value), out code) ? code : 0;
        }

        private static string Field(Dictionary<string, object> map, string key)
        {
            object value;
            return map.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
        }

        internal static string StatusText(KeePassStatus status)
        {
            switch (status)
            {
                case KeePassStatus.Ok: return L.T("已连接 KeePassXC");
                case KeePassStatus.NotInstalled: return L.T("未安装 KeePassXC");
                case KeePassStatus.NotRunning: return L.T("KeePassXC 未运行");
                case KeePassStatus.IntegrationOff: return L.T("请先在 KeePassXC 设置里开启「浏览器集成」");
                case KeePassStatus.NotAssociated: return L.T("尚未关联 KeePassXC");
                case KeePassStatus.Timeout: return L.T("KeePassXC 没有响应");
                default: return L.T("KeePassXC 操作失败");
            }
        }
    }
}
