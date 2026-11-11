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

            // ---- 5. 模式 ----
            if (mode.StartsWith("associate", StringComparison.OrdinalIgnoreCase))
            {
                var name = ArgValue(args, "--name") ?? "NanoEdge";
                report.Add("");
                report.Add("发起关联，连接名 = " + name + "（请在 KeePassXC 弹窗里点「保存并允许访问」）");
                var result = KeePassBridge.AssociateAsync(name, TimeSpan.FromSeconds(120)).Result;
                check(result.Status == KeePassStatus.Ok,
                    "associate -> " + result.Status + " " + result.Message);
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
                // 明文只在内存里比对，不落报告
                var hit = result.Entries.Find(e => e.Login == "testuser" && e.Password == "testpass123");
                check(hit != null, "取回的凭据与预期一致（testuser / testpass123）");
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
