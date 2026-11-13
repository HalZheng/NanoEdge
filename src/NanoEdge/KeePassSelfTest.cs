using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace NanoEdge
{
    // KeePassXC 直连自测。输出 out/kpxc-report.txt。
    // 参数：
    //   --kpxctest              基础检查（libsodium / 管道 / proxy / 配置）
    //   --kpxctest=associate    发起一次关联（KeePassXC 会弹窗，需要人点）
    //   --kpxctest=get          取 https://example.com 的凭据
    //   --kpxctest=get&url=...  取指定网址
    internal static class KeePassSelfTest
    {
        public static int Run(string[] args)
        {
            var root = FindRoot();
            var mode = ArgValue(args, "--kpxctest") ?? "";
            var report = new List<string>();
            var passed = 0;
            var failed = 0;

            Action<bool, string> check = (ok, text) =>
            {
                report.Add((ok ? "PASS  " : "FAIL  ") + text);
                if (ok) passed++; else failed++;
            };

            report.Add("NanoEdge KeePassXC 自测  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.Add("mode = " + (mode.Length == 0 ? "(基础)" : mode));
            report.Add("");

            // 凭据写到 out/ 下，不碰用户真实的 keepassxc.json；但允许写，
            // 否则 associate 出来的关联当场就丢，后面 get 必然失败。
            KeePassBridge.ConfigPath = Path.Combine(root, "out", "kpxc-test-config.json");

            // ---- 1. libsodium ----
            string detail;
            var cryptoOk = KeePassBridge.SelfTestCrypto(out detail);
            check(cryptoOk, "libsodium crypto_box 往返  " + detail);

            // ---- 2. 浏览器集成管道 ----
            var pipeOk = KeePassBridge.IntegrationRunning();
            check(pipeOk, "浏览器集成管道存在（\\\\.\\pipe\\org.keepassxc.KeepPassXC.BrowserServer*）");

            // ---- 3. proxy ----
            var proxy = KeePassBridge.FindProxy();
            check(!string.IsNullOrEmpty(proxy), "keepassxc-proxy.exe = " + (proxy ?? "(未找到)"));

            // ---- 4. 配置 ----
            var config = KeePassBridge.LoadConfig();
            check(true, "关联配置 = " + (config == null
                ? "(无，尚未关联)"
                : (config.ConnectionId + " / idPublic=" + config.IdPublic.Length + "B")));

            // ---- 5. set-login 请求构造（纯函数，不连 proxy、不写密码库） ----
            var setPayload = KeePassBridge.SetLoginPayload("https://example.com/",
                "alice", "s3cret", "https://example.com/login", "NanoEdge-Test");
            check((string)setPayload["action"] == "set-login" &&
                  (string)setPayload["id"] == "NanoEdge-Test" &&
                  (string)setPayload["url"] == "https://example.com/" &&
                  (string)setPayload["login"] == "alice" &&
                  (string)setPayload["password"] == "s3cret" &&
                  (string)setPayload["submitUrl"] == "https://example.com/login" &&
                  (string)setPayload["downloadFavicon"] == "false" &&
                  !setPayload.ContainsKey("keys"),
                  "set-login payload: 官方字段齐全，id 用 associate 连接名，不发图标请求");

            // ---- 6. 模式 ----
            if (mode.StartsWith("associate", StringComparison.OrdinalIgnoreCase))
            {
                var name = ArgValue(args, "--name") ?? "NanoEdge";
                report.Add("");
                report.Add("发起关联，连接名 = " + name + "（请在 KeePassXC 弹窗里点「保存并允许访问」）");
                var result = KeePassBridge.AssociateAsync(name, TimeSpan.FromSeconds(120)).Result;
                check(result.Status == KeePassStatus.Ok,
                    "associate -> " + result.Status + " " + result.Message);
            }
            else if (mode.StartsWith("set", StringComparison.OrdinalIgnoreCase))
            {
                // 手动 smoke test：这一步会真的往当前解锁的 KeePassXC 数据库写一条条目。
                // 因此默认绝不触发，且必须把三个参数都给齐；跑完请自行在 KeePassXC 里删掉。
                var url = ArgValue(args, "--url");
                var login = ArgValue(args, "--login");
                var password = ArgValue(args, "--password");
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(login) ||
                    string.IsNullOrEmpty(password))
                {
                    report.Add("跳过 set-login：需要 --url / --login / --password 三个参数。");
                    report.Add("注意：执行时会真的写入 KeePassXC 数据库，且可能弹出保存确认框。");
                }
                else
                {
                    report.Add("set-login（真机写入）url = " + url + " login = " + login);
                    var result = KeePassBridge.SetLoginAsync(url, login, password, "").Result;
                    check(result.Status == KeePassStatus.Ok,
                        "set-login -> " + result.Status + " " + result.Message);
                    report.Add("KeePassXC 若弹出「保存/更新条目」确认框，需要手动点一次；" +
                               "测试条目请在 KeePassXC 里删除。");
                }
            }
            else if (mode.StartsWith("get", StringComparison.OrdinalIgnoreCase))
            {
                var url = ArgValue(args, "--url") ?? "https://example.com/";
                report.Add("");
                report.Add("get-logins url = " + url);
                var result = KeePassBridge.GetLoginsAsync(url).Result;
                check(result.Status == KeePassStatus.Ok,
                    "get-logins -> " + result.Status + " " + result.Message);
                report.Add("条目数 = " + result.Entries.Count);
                foreach (var entry in result.Entries)
                {
                    report.Add("  · name=" + entry.Name + " login=" + entry.Login +
                               " password=" + (string.IsNullOrEmpty(entry.Password) ? "(空)" : new string('*', entry.Password.Length)) +
                               " uuid=" + entry.Uuid);
                }
                // 明文只在内存里比对，不落报告。默认 URL 沿用 spike 的固定凭据；
                // 自定义 URL 时用 --login / --password 比对，方便验证 set-login 的写入→读回闭环。
                var expectLogin = url == "https://example.com/"
                    ? "testuser" : (ArgValue(args, "--login") ?? "");
                var expectPassword = url == "https://example.com/"
                    ? "testpass123" : (ArgValue(args, "--password") ?? "");
                var hit = result.Entries.Find(e =>
                    e.Login == expectLogin && e.Password == expectPassword);
                check(hit != null, "取回的凭据与预期一致（" + expectLogin + "）");
            }

            report.Add("");
            report.Add("合计 " + passed + " PASS / " + failed + " FAIL");

            try
            {
                var outDir = Path.Combine(root, "out");
                Directory.CreateDirectory(outDir);
                File.WriteAllLines(Path.Combine(outDir, "kpxc-report.txt"), report, Encoding.UTF8);
            }
            catch { }

            return failed == 0 ? 0 : 1;
        }

        private static string ArgValue(string[] args, string name)
        {
            if (args == null) return null;
            foreach (var arg in args)
                if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return arg.Substring(name.Length + 1).Trim('"');
            return null;
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
