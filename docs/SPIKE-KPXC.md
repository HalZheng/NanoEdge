# KeePassXC 直连 spike — 验证结果

日期：2026-11-11
环境：Windows / KeePassXC 2.8.0 / WebView2 宿主（本机 `C:\Program Files\KeePassXC`）
目的：验证「NanoEdge 不存密码、直连 KeePassXC 取凭据填充」这条路线是否可行。

## 结论

**可行，全链路已跑通，并且已经落到 C# 实现里。**

不依赖 WebView2 的扩展能力、不依赖 native messaging 注册表、不改 KeePassXC 的 manifest。
宿主进程自己拉起 `keepassxc-proxy.exe`，用标准 native messaging 帧 + crypto_box 加密即可完成关联与取密。

C# 侧（`KeePassBridge.cs` + `NativeLoader` 嵌入 `libsodium.dll`）已通过自测：

```
--kpxctest                      4 PASS / 0 FAIL
--kpxctest=associate --name=X   5 PASS / 0 FAIL
--kpxctest=get --url=...        6 PASS / 0 FAIL（取回 testuser / testpass123，明文比对一致）
```

产物 `NanoEdge.exe` = 1.63MB（原 938KB，libsodium 341KB 未压缩量级），0 警告 0 错误。

## 已验证的完整链路

| 环节 | 结果 | 备注 |
|---|---|---|
| 定位 proxy | ✅ | `%ProgramFiles%\KeePassXC\keepassxc-proxy.exe` |
| 命名管道 | ✅ | `\\.\pipe\org.keepassxc.KeepPassXC.BrowserServer_<用户名>`（2.8 起带用户名后缀） |
| native messaging 帧 | ✅ | 4 字节小端长度 + UTF-8 JSON |
| crypto_box（X25519 + XSalsa20-Poly1305） | ✅ | 用 PyNaCl 验证，C# 侧需等价实现 |
| `change-public-keys` | ✅ | 明文握手，返回 host 公钥 + version |
| `test-associate`（未关联） | ✅ | `errorCode 8` |
| `associate`（首次） | ✅ | 需用户在弹窗填连接名并点「保存并允许访问」 |
| `test-associate`（已关联，新进程） | ✅ | **0.11s，无弹窗** |
| `get-logins` | ✅ | 返回 `login` / `password` / `name` / `uuid` / `group` 明文 |
| identification key 持久化 | ✅ | 存文件后跨进程复用，免二次确认 |

实测返回样例：

```json
{"count": 1,
 "entries": [{"group": "根群组", "login": "testuser", "name": "spike-test",
              "password": "testpass123", "stringFields": [],
              "uuid": "9cb8517ae39c40878a2816c63b783c40"}],
 "hash": "c18355f7…", "success": "true", "version": "2.8.0"}
```

## 协议要点（按实现顺序）

1. 拉起 `keepassxc-proxy.exe`，stdin 写、stdout 读；帧格式 `uint32-LE 长度` + JSON。
2. 生成**会话**密钥对（临时，每次进程启动重新生成）。
3. 明文发 `change-public-keys`：
   `{"action","publicKey":b64(会话公钥),"nonce":b64(24B),"clientID":b64(24B)}`
   → 回 `publicKey`（host 公钥）+ `version`。
4. 之后所有请求都加密：
   ```json
   {"action": "...", "message": b64(crypto_box(payload, nonce, hostPub, clientSec)),
    "nonce": b64(nonce), "clientID": "..."}
   ```
   `message` 是**纯密文**，nonce 单独放字段，不要拼在密文前面。响应同理，用响应里的 `nonce` 解。
5. 发 `associate`：
   ```json
   {"action":"associate","key":b64(会话公钥),"idKey":b64(identification 公钥)}
   ```
   `key` 是**会话公钥**（服务端用来校验同一会话），`idKey` 是**长期身份公钥**。
6. `get-logins`：
   ```json
   {"action":"get-logins","url":"https://site/","submitUrl":"",
    "httpAuth":"","keys":[{"id":"<associate返回的id>","key":b64(identification公钥)}]}
   ```

## 踩坑记录（按踩到的顺序）

### 1. 浏览器集成总开关必须先开

KeePassXC 默认关闭。开之前管道 `\\.\pipe\org.keepassxc.KeePassXC.BrowserServer*` 根本不存在，
proxy 启动后空转 6 秒然后退出码 1，不给任何错误文案。
**判断方法**：枚举 `\\.\pipe\` 下是否有 `BrowserServer` 前缀的管道。
不需要勾选 Chrome/Edge/Firefox 那排复选框，那些只是用来写浏览器 manifest 的。

### 2. 回车键在关联弹窗里等于「拒绝」

`BrowserAccessControlDialog` 的默认焦点在拒绝侧，发 `WM_KEYDOWN/VK_RETURN` 会得到
`errorCode 6`（操作被取消或被拒绝）。必须真正点到「保存并允许访问」按钮。

### 3. Qt 的模态对话框在 UIA 树里挂在主窗口下面

`GetRootControl().GetChildren()` 只看得到主窗口 `密码 - KeePassXC`，
对话框（`KeePassXC - 新的密钥关联请求`）是它的后代，必须递归下钻才能拿到按钮。
另外 `WindowControl(Name=...)` 是**精确匹配**，标题带 `KeePassXC - ` 前缀时必须写全。

### 4. 连接名会要求填写，重名会二次确认

2.8 的关联弹窗里有一个输入框，提示「请为此连接提供唯一的名称或 ID」，
空名称无法保存。同名再次关联会弹「一个名为 X 的共享加密密钥已存在，确认要覆盖它吗？」。
自动化测试里要把这个二次确认一起处理掉。

### 5. 最关键：`keys[].id` 必须用 associate 返回的 id，不能用自己生成的 clientID

这是最容易误判的一条。associate 返回的 `id` 就是你填的**连接名**，不是 clientID。
拿 clientID 去查会返回 **`errorCode 15`「未找到登录信息」**，而不是「未关联」，
很容易误判成库或 URL 的问题（本次为此多花了几轮，甚至一度怀疑用户没填 URL 字段）。

对照实验（同一份库、同一个密码）：

| `keys[].id` | 结果 |
|---|---|
| `NanoEdge2`（associate 返回值） | ✅ `count: 1`，拿到密码 |
| `NanoEdge-Spike`（自造 clientID） | ❌ `errorCode 15` |

同理 `test-associate` 的 `id` 也要用这个值，否则回 `errorCode 8`。

### 6. `get-logins` 之前必须先在本次会话里 `test-associate`

Python 侧所有成功案例都先发了 `test-associate`，C# 第一次实现时省掉了这一步，直接 `get-logins`，
结果拿到 **`errorCode 8`「关联失败」**——明明库里的关联是好的（用同样的 id/key 单独发
`test-associate` 会返回 success）。

**正确顺序**：`change-public-keys` → `test-associate`（确认身份）→ `get-logins`。
把 `test-associate` 的结果当作「关联是否仍然有效」的判定，失败就直接提示用户重新关联，
比让 `get-logins` 报一个含糊的 8 要好。

### 7. `JavaScriptSerializer` 把 JSON 数组反序列化成 `ArrayList`，不是 `object[]`

```csharp
var list = entriesField as object[];   // 永远是 null
```

症状极具迷惑性：`success: true`、`count: 1`，但解析出来 0 条，看起来像「KeePassXC 没匹配到条目」。
实际是转型静默失败。改成 `as System.Collections.IEnumerable` 遍历即可。

**排查手法**：把 `count` 和 `entriesField.GetType().Name` 一起打出来——`count=1 entries=ArrayList parsed=0`
一眼就能看出是解析问题而不是数据问题。

## 对正式实现的约束

1. **必须持久化两样东西**：identification 私钥 + associate 返回的 id。
   存在 `%APPDATA%\NanoEdge\keepassxc.json`，建议用 DPAPI 保护私钥。
   不持久化就得每次开浏览器重新填名 + 点允许。
2. **连接名要让用户可编辑**，它会显示在 KeePassXC 的关联列表里；
   默认值可用 `NanoEdge`，重名时提示覆盖。
3. **会话密钥每次进程启动重新生成**，不要持久化。
4. `keepassxc-proxy.exe` 路径优先从 `%LOCALAPPDATA%\keepassxc\*.json` 的 `path` 读，
   拿不到再回落到 `%ProgramFiles%\KeePassXC`，最后让用户手选。
5. KeePassXC 未运行 / 未开浏览器集成时，proxy 是**静默空转**的，
   必须自己做超时（本 spike 用 6~10s）并转成可读提示。

## 待验证（本 spike 未覆盖）

- `set-login`（把页面新密码写回库）
- `get-totp`（两段验证码）
- Linux/macOS 路径差异（当前只验证 Windows）
- 密码库非活动 / 锁定状态下的错误码与提示文案

## 复现脚本

本次用 Python 验证，脚本在 `out/spike/`：

| 文件 | 作用 |
|---|---|
| `kpxc_chain.py` | 完整链路：握手 → 关联 → test-associate → get-logins |
| `kpxc_persist.py` | 验证持久化后免弹窗（0.11s） |
| `kpxc_final4.py` | 含 URL 多写法探测、覆盖确认处理 |

依赖：`pip install pynacl`（C# 侧需等价实现 crypto_box，见下）。

## C# 侧实现待选型（未定）

`crypto_box` 在 C# 里需要 X25519 + XSalsa20 + Poly1305，.NET 自带没有。候选：

| 方案 | 体积 | 备注 |
|---|---|---|
| P/Invoke `libsodium.dll` | +约 200KB（Costura 压缩后） | 最稳，API 现成 |
| 移植 tweetnacl（~600 行纯 C#） | 0 | 无依赖，但密码学代码自写风险高 |
| BouncyCastle | +1MB 以上 | 体积不可接受 |

倾向 libsodium，落在 3MB 预算内。
