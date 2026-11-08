# NanoEdge — 产品与技术 SPEC

> 本文是 NanoEdge 的开发规格与变更记录，保留了从 Phase 0 到当前版本的设计取舍、实测数据和内部决策语境。文中的「用户」指项目发起人；「ZZZ」是一个本地参考项目的代号，仓库不包含其源码。

| 项 | 值 |
|---|---|
| 版本 | **v1.3（三态内存模型）** |
| 日期 | 2026-11-04 |
| 状态 | 决策已冻结；v1.3 三态内存模型；M1 / M2 / M3-a/b/c / **M4** / **M5（简化版）** / **M6-a（发布收尾）** / **M6-b（双语 + 设置）** / **M7（对照补齐）** / **M8（脚本能力）已实现**（0.7 / 0.8+0.9 / 0.10 / 0.11 / 0.12 / 0.14 / 0.15 / 0.16 / 0.17 / 0.18 / 0.19）；**M3-d 因无测试环境跳过**（0.13）；**P0 17 项全部完成** |
| 目标平台 | Windows 10 1809+ / Windows 11，x64 |
| 代号 | NanoEdge |

---

## 0. 已定决策

| 决策 | 结论 | 理由 |
|---|---|---|
| 外壳技术栈 | **WPF + .NET Framework 4.8 + WebView2** | 单 exe 2–3MB、零运行时安装、冷启动 ~0.5s、可直接借鉴 ZZZ 的成熟写法 |
| 内存策略 | **三级分级（Low / Suspend / Discard），默认「均衡」档** | 唯一能同时兼顾"隐藏即降内存"和"切回不难受"的方案 |
| 保密站点 | **密封标签：伪装 + `Alt+\`` 切换 + 通知开关**（0.15 简化：不做独立 Profile / 防截屏 / DPAPI） | 用户原话「不需要那么高级复杂」，只要"看着是文档、一键切走" |
| MVP 范围 | **内核 + 日常必需**（能当主力开发浏览器） | 只做内核无法日常使用；加广告拦截/分屏会显著拖长工期 |
| 窗口形态 | **单窗口多标签**，不做多窗口、不做标签拖出 | 多窗口会让内存治理与状态同步复杂一倍，收益低 |
| 扩展能力 | **只做内置脚本注入**，不做 CRX / Chrome 扩展 | 你的场景 90% 是改页面、加按钮、自动填表，脚本足够 |
| 广告拦截 | **不做** | WebView2 无扩展，只能做域名规则，效果一般还拖首屏；页面广告的内存由丢弃策略兜底 |
| 默认浏览器 | **提供按钮，显式点击才写注册表，不主动提醒** | 保留绿色属性 |

### 0.1 v0.2 相对 v0.1 的变更

- 删除多窗口 / 标签拖出（P1）、CRX 扩展（P2）、广告拦截（P2）、工作区（P2）。
- 起始页从 P1 提到 **P0**，并明确为纯 WPF 页面（不创建 WebView2），支持自定义快捷访问。
- 新增「设为默认浏览器」（P1，显式按钮，无提醒）。
- 图标定稿 GTA6 式渐变；UI 主色随之改为 `#FF4D9D`。
- 补充起始页、默认浏览器注册两个子系统设计（7.8 / 7.9）。

### 0.2 v1.0 相对 v0.2 的变更

- R1 失焦冻结 **60s**、R2 阈值 **700MB**、R3 起始页 **4×2 八格 + 搜索框**、Q5 默认 **AppData**，全部按建议定稿。
- R4 图标选 **C 折角 N**，修正居中；渐变改为 GTA6 的四段色序（蓝 → 紫 → 品红 → 橙）。
- 新增 **4.9 内存悬浮面板 + 一键释放**（用户设想，已论证可行，纳入 P0-17），并补充按标签归因的 API 依据与三个坑。
- Phase 0 增加第 7 项验证：`AssociatedFrameInfos` 能否把 renderer 对上标签。

### 0.3 v1.1 相对 v1.0 的变更（Phase 0 实测驱动）

**不改任何功能范围，只修正被实测推翻的三处假设：**

1. **省内存的主力是 `Low`，不是 `Suspend`**。实测 `MemoryUsageTargetLevel=Low` 让 5 个标签的工作集从 629MB 降到 408MB（-35%），其中 renderer 工作集 -82%；紧接着 `TrySuspendAsync` 后内存没有进一步下降。Suspend 的真实价值是停 JS/定时器（省 CPU 和电），不是省内存。v1.0 的四态表把降幅记错了位置。
2. **窗口最小化时 Windows 自动把整个进程组工作集压到 84.7MB**，提交内存不变（338.7MB）。所以"隐藏即降内存"有一半是操作系统免费给的，产品的差异化在"失焦但未最小化"时主动降冷，以及标签级精确丢弃。
3. **全部标签丢弃后整个 WebView2 进程组会退出**（进程数 10 → 1，只剩 HOST）。v1.0 在 4.9 写的"不能让 browser/GPU 进程消失"是错的：只要还有任意标签就不能，但全丢完它们会自己走。

另外：同源标签在 WebView2 多控件模型下**没有**共享 renderer（3 个同域标签各占一个，`FrameId` 1:1 精确匹配），4.9 坑 #1 降级为防御性说明；图标迭代方向见新增 5.5.1。

### 0.4 v1.2 相对 v1.1 的变更

**运行时缺失不做引导流程**（用户决定）：只支持已安装 WebView2 Runtime 的系统，检测不到就弹一个简单说明框然后退出。理由是 Win11 / 绝大多数 Win10 都预装了 WebView2，为极少数缺失场景做安装引导、Fixed Version 方案说明、版本升级提示都是净复杂度。连带删除干净虚拟机验收项。图标迭代见 5.5.1–5.5.4，不影响冻结。

### 0.5 图标 v3（同日迭代，仅设计稿，不影响功能冻结）

用户否掉 v2 的三点并给出新口径，SPEC 相应修正 5.5.1、把 5.5.2 标为废弃、新增 5.5.3：

- **字形**：不要菱形尖刺，要标准大写字母体、不倾斜、类 GTA6 LOGO 粗细 → 从「折线描边 + miter」改成「单个填充多边形 + bevel」。
- **底板**：不要太浅、亮度低一些、饱和度高一些 → 从浅粉蓝米色改成深紫→紫红→橙金的落日余晖。
- **字身**：饱和度只降一点点 → 四段色各约降 10%，不改色序。

矢量稿 `assets/nanoedge-icon-v3.svg`。v2 稿 `assets/nanoedge-icon-v2.svg` 保留作对照，不用于生成 ico。

### 0.6 图标 v4（同日迭代，仅设计稿，不影响功能冻结）

用户认可 v3 字形，另提四点：背景要折中、字形放大一点点、黑白描边收细、直角不倒角。SPEC 相应新增 5.5.4 并把 5.5.3 标为中间稿：

- 背景取 v2（过浅低饱和）与 v3（过暗高饱和）中值：`#6A5585 → #9C6A90 → #CE8497 → #EBA683 → #F4C68E`；
- 字形路径不变，整组 `scale(1.18)`；
- 描边黑层 stroke 14→8、白层 6→4.5；
- `bevel` → `miter` + `miterlimit=2`，直角保持尖角、锐角自动平切，避免重新出现菱形尖刺。

当前稿 `assets/nanoedge-icon-v4.svg`，预览 `assets/icon-v4-preview.png`。5.4 色板的 `BrandSky` 同步换值（`BrandSun` 在 0.7 的最终微调中删除）。功能范围、P0 17 项、内存模型全部不变。

### 0.7 图标定稿与 M1 实现（2026-11-04 17:33 起）

**图标 v4 最终微调并验收通过**（用户 95 分 → 定稿）：

- 白边要比黑边细一点点、黑边粗细不变。可见黑环 = `(B−W)/2 × 1.18`，可见白环 = `W/2 × 1.18`，所以两个 stroke 必须一起降：`B 8→6`、`W 4.5→2.5` → 黑环仍 2.065px、白环 1.475px。只改白层会让黑环反而变粗。
- 去掉 N 下方两条浅灰横条，只留纯粹字母 → 落日圆盘与三条切割横线一并移除，背景只剩纯净渐变。5.4 色板删掉 `BrandSun`，`BrandSky` 注明"无装饰元素"。
- 实渲染（256/64/32/16）目视确认后定稿。

**M1 代码已开始并实现主体**，项目在 `src/NanoEdge/`（net48 WPF，产物 `NanoEdge.exe` **523KB**，Costura 单文件 + 内嵌 loader）：

| M1 项 | 状态 |
|---|---|
| 窗口 chrome（自定义无边框、可拖动/缩放、min/max/close） | 完成 |
| 三种布局（紧凑单行 36px / 标准两行 / 垂直标签） | 完成 |
| 标签：新建、关闭、切换、拖拽排序（WPF DragDrop）、右键菜单 | 完成 |
| 分组：新建、折叠/展开、拖入/移出、色点 | 完成 |
| 垂直标签：44px 收起 + 150ms 悬浮 Popup 展开 220px + 400ms 收回；另加「固定展开」模式 | 完成 |
| 起始页：纯 WPF（**不创建 WebView2**）、搜索框占位、4×2 快捷方式 | 完成 |
| 暗色/亮色主题（`SetResourceReference` 动态资源，切换不重建 WebView2） | 完成 |
| 快捷键骨架（Ctrl+T/W/Tab/1-9/L、Alt+←→、F5、F11、Ctrl+Shift+M） | 完成 |
| 地址栏解析与 WebView2 导航、会话恢复（懒加载）、单实例、AppData 设置 | 完成 |

**验证结果**：`out/m1-compact.png` / `m1-vertical.png` / `m1-standard.png` / `m1-light.png` 四张实截图目视确认；`--open-url=file://...` 导航日志 `nav ok` 确认 WebView2 链路打通；把 exe 单独复制到无 DLL 目录仍能启动并截图（`m1-standalone.png`）。编译 0 警告 0 错误。

预览/调试参数（不写回用户配置）：`--screenshot=<path>`、`--layout=compact|standard|vertical`、`--theme=dark|light`、`--rail=expanded`、`--open-url=<url>`。

### 0.8 M2 内存治理实现（2026-11-04 18:20）

**范围**：内存状态机、`MemoryGovernor`、豁免规则、丢弃快照与恢复、内存悬浮面板与一键释放、三档预设。产物 **581KB**（+58KB），编译 0 警告 0 错误。（状态机最初按四态实现，同日 0.9 改为三态。）

| 新增文件 | 作用 |
|---|---|
| `MemoryGovernor.cs` | 三态状态机（Active/Low/Discarded）、豁免判定、失焦/最小化/后台计时、内存压力 LRU 丢弃、手动释放 |
| `MemoryProbe.cs` | `GetProcessInfos` + `GetProcessExtendedInfosAsync`、FrameId→标签归因、工作集/私有提交双口径 |
| `TabSnapshot.cs` | 快照模型 + 采集/恢复 JS（跳过 password/file，256KB 上限） |
| `MemoryPanel.cs` | P0-17 悬浮面板（Popup，打开时 1s 采样，关闭即停） |
| `TabViews.cs` | 丢弃标签占位视图（纯 WPF） |
| `Toast.cs` | Popup 轻提示（WebView2 之上唯一可行方案） |

`BrowserHost` 实现 `IMemoryTabOps`（唯一碰 WebView2 内存 API 的地方），`Models.cs` 增加 `MemorySettings`/三档预设与标签运行时字段，`Stores.cs` 增加 `SnapshotStore`。

**实现中做的判断（记录在案，不是随手改）**

1. **后台标签在转入后台 2s 后也降到 `Low`**（原文只写了窗口失焦）。`Low` 零副作用（JS/通知/定时器照常），纯赚。
2. ~~当前可见标签在仅失焦时不冻结~~ —— 该取舍随 0.9 取消 `Suspended` 后失效：现在失焦/最小化一律只降到 `Low`。
3. **恢复会话的标签直接标成 `Discarded`** 并显示占位页，而不是显示起始页再等点击 —— 面板数字诚实，且复用同一条恢复链路。
4. **`CoreWebView2.FrameId` 是 `ulong`**，映射表用 `Dictionary<ulong,string>`；主帧优先，没有主帧时才用 iframe 归因。
5. 面板「常驻」按 **host** 记账（写入 `Memory.ResidentOrigins`，见 4.3 第 5 条）；行内 chip 默认隐藏、悬停才出现，避免看起来所有标签都常驻。
6. **顺带修掉 M1 遗留的启动崩溃**：会话恢复在构造函数里就调 `EnsureCoreWebView2Async`，会抛 `cannot be used before the application's event loop has started running`（此前会话里只有起始页，所以没暴露）。加 `_shellReady` 标志，首次加载推迟到 `Loaded`；用真实 `file://` 会话回归，日志 `nav ok`（`out/m2-session.png`）。

**实测（本机，3 个本地测试页，均衡档，`--memtest` 自动执行，报告 `out/m2-memtest.txt`）**

| 阶段 | 进程组工作集 | 私有提交 | 状态 |
|---|---|---|---|
| 3 标签常态 | 449.1MB | 456.3MB | 3×Active |
| 失焦 1.2s → Low | **364.9MB（-19%）** | 457.1MB | 3×Low；renderer 工作集 103→13.0MB（-87%） |
| 回到前台 | 367.0MB | 457.3MB | 当前标签 Active，其余保持 Low |
| 一键释放 2 个后台 | **363.0MB（工作集 -4MB / 提交 -44MB）** | 413.5MB | 2×Discarded，进程 9→7 |
| 选中已丢弃标签 | 430.3MB | — | 重建 + 快照还原成功 |

- `Low` 拿下 ~19% 工作集、renderer 单项 -87%（与 Phase 0 的 -82% 同量级）。
- 单次运行示例，组总量波动约 ±10MB；后台标签可能已被 Windows 修剪到 `工作集=0`，此时「释放」的工作集收益很小、但提交内存照样归还（见上表 -44MB）——面板的预估与 toast 都如实分开报这两个数。
- 快照实测 319 字节（2 字段 + scrollY=1200），恢复后 `scrollY` 与 `textarea` 值均正确。
- 空载（1 个起始页、无 WebView2）徽标 87MB（1.2s 首次采样）/ 外壳工作集 107MB（6s 稳定值），符合 2.1 的 ≤120MB。

**未做 / 留给后续**：POST 结果页不可重建的检测（Chromium 不暴露，恢复只会重新 GET）；`EmptyWorkingSet` 高级开关只有字段无 UI；标签 chip 尚未显示内存状态点（面板内有）；Popup 的 display affinity 仍归 M5（当前 `IsSecret` 恒 false）。

### 0.9 v1.3：取消 `Suspended`，只保留三态（2026-11-04 20:20）

**用户决定**：直接抛弃冻结状态，只考虑 `Active` / `Low` / `Discarded`。

**理由（数据支持）**：冻结既不省内存，又要额外付复杂度——

| 观测 | 数据 |
|---|---|
| 5 标签 `Low` → `Suspend` | 工作集 408.3 → 416.5MB（**回升**） |
| 3 标签 `Low` → `Suspend` | 工作集 336.2 → 362.8MB（**回升**） |
| 冻结的真实收益 | 停 JS / 停定时器 / 省 CPU 与电量，不是内存 |
| 冻结的代价 | `TrySuspendAsync` 要求控件不可见（当前标签冻不了）→ 一整套"可见性 + 状态"守卫；与 `MemoryUsageTargetLevel` 串行互斥 → 再一套守卫；多一档状态色点与文案；通知要绕过"保活例外" |

**代码变更**：
- `TabMemoryState` 从 4 值改 3 值（`Active` / `Low` / `Discarded`）；
- `MemorySettings` 删 `UnfocusFreezeMs`、`HiddenFreezeMs`、`SuspendEnabled`，三档预设只保留「降冷延迟 / 自动丢弃 / 内存上限」；
- `IMemoryTabOps` 删 `IsSuspended` / `SuspendAsync` / `Resume`，`BrowserHost` 只剩 `SetLow` / `SetNormal` / `Discard`；
- 面板删掉「冻结后台」按钮，只剩「释放后台」，行状态改为三态色点；
- 状态栏「冻结 N」改为「降冷 N」；
- `--memtest` 第 3 段改成"回前台 → 当前标签 Normal、其余保持 Low"。

**文档影响**：4.1 改写为三态并说明原因、4.2 触发规则与三档预设删冻结列、4.3 标题改「永不被丢弃」、4.6 从"通知 vs 冻结"改为"通知不会因降内存丢失"、4.8 删一条反模式、4.9 面板示意与按钮改单按钮、8 数据模型枚举、11 风险表标记已消除、12 章 M2 验收表、13.2 R1 作废。0.3/2.1/Phase 0 记录保留原文（历史实测）。

**指标影响**：体积不变（581KB）；空载与启动不变；降内存能力不变（`Low` 本来就是主力）。**净减少**一个状态、约 90 行代码、一个按钮和一组守卫。

### 0.10 M3-a：地址栏与历史（2026-11-04 20:35）

**范围**：地址栏解析与清理、联想下拉、键盘操作、历史记录与落盘、导航失败重试。产物 **602KB**（较 M2 +21KB），0 警告 0 错误。

| 新增文件 | 作用 |
|---|---|
| `BrowseData.cs` | `HistoryStore` / `BookmarkStore` / `SuggestionEngine`（含排序权重） |
| `AddressSuggestions.cs` | 锚定地址栏的联想 Popup（最多 8 行、三来源记号、键盘高亮） |
| `MainWindow.NavTest.cs` | `--navtest` 自测（16 项断言 + 下拉渲染） |

`BrowserHost` 增加 `LooksLikeUrl` / `NormalizeUrl` / `CompleteDotCom`、`NavigationDone` 事件、`FocusContent`；`MainWindow` 增加 80ms 防抖、键盘分发、导航成功写历史、失败 Toast。

**实现判断（记录在案）**

1. **不自己造错误页**：WebView2 已有内置错误页（`ERR_CONNECTION_REFUSED` 等），再叠一层只会更差；只在底部 Toast 给「重试」。
2. **下拉宽度封顶 820px**：紧凑布局下地址栏可能 1400px 宽，下拉跟着拉满会成一条空横幅。
3. **历史 3s 节流 + 退出 Flush**：每次导航都写 5000 条 JSON 不划算；代价是崩溃最多丢 3s 记录。
4. **重定向去重**：2s 内同一 URL 的第二次 `NavigationCompleted` 不计访问次数（实测一次 Bing 搜索会连发两次）。
5. **书签权重高于历史**：同一 URL 同时存在时书签胜出（+25，且去重保留分高者）。

**验证**（`--navtest` 16/16 PASS，报告 `out/m3-navtest.txt`，下拉截图 `out/m3-suggest.png`）

- `git` 排序 github 224 > gitlab 215 > gitee 211（标题前缀 + 访问次数 + 新鲜度叠加）
- `mdn` / `local` 命中正确；输入本身像 URL 时「打开」置顶
- 书签候选胜出、同 URL 不重复
- localhost 补 `http://`、中文查询不当 URL、`Ctrl+Enter` 补 `.com`、粘贴清理去引号
- 真实运行：Bing 页面导航后 `history.json` 落盘 1 条、标题正确（不再把 URL 当标题）
- `--memtest` 回归 8/8 通过（没碰坏内存治理）

**未做（留给 M3-b/c/d）**：内部页 `nano://history` / `nano://bookmarks`、`Ctrl+D`、书签导入、查找、缩放、下载列表、崩溃恢复。

### 0.11 M3-b：内部页与收藏（2026-11-04 20:55）

**范围**：`nano://history` / `nano://bookmarks` 纯 WPF 内部页、`Ctrl+D` / `Ctrl+H` / `Ctrl+Shift+O`、Chrome/Edge 与 Netscape HTML 两种书签导入、URL 归一化比较。产物 **625KB**（较 M3-a +23KB），0 警告 0 错误。

| 新增文件 | 作用 |
|---|---|
| `InternalPages.cs` | `InternalPage` 骨架（标题/计数/搜索/动作/列表）+ `HistoryPage` + `BookmarksPage` |

`BrowseData.cs` 增加 `Urls.Same/Key/Host`、`BookmarkStore.ImportChromiumProfiles/ImportChromiumFile/ImportNetscapeHtml/ExportNetscapeHtml`；`BrowserHost` 增加 `ShowInternal`；`TabModel` 增加 `InternalPage`。

**实现判断（记录在案）**

1. **内部页必须纯 WPF**：打开历史/书签时不建 WebView2，renderer 增量为 0。代价是"网页 → 内部页"要 `Dispose()` 当前 WebView2；因此内部页不能当"后退目标"，切换是单向的。
2. **快捷键必须在消息泵层截**：WebView2 是独立 HWND，页面聚焦时 WPF 的 `PreviewKeyDown` 收不到按键（M1 的 Ctrl+T/W 其实只在地址栏聚焦时有效）。改成 `ComponentDispatcher.ThreadPreprocessMessage` + 统一的 `HandleShortcut(key, modifiers)`，WPF 输入和原始按键走同一个入口。
3. **历史按 URL 去重 + 访问次数**（与 Chrome 的"每次访问一行"不同）：列表更短，行尾显示"最后访问时间 · N 次"，搜索/排序语义不变。
4. **URL 归一化用 `Uri.Compare`**（参考 ZZZ）：忽略大小写、默认端口、末尾斜杠，修掉 `github.com` 与 `github.com/` 被当成两条的问题。
5. **顺手修掉 M1 遗留 bug**：后台标签导航完成时 `Show(tab)` 会把当前标签的视图抢成 `Collapsed`（切到内部页后页面被"清空"就是这个原因）。现在可见性只由 `MainWindow` 在确认 `tab == active` 后刷新。

**验证**（`--navtest` 30/30 PASS，报告 `out/m3-navtest.txt`）

- 内部页渲染：`out/m3-history.png`、`out/m3-bookmarks.png`
- 路由：`nano://history` / `nano://bookmarks` / 未知路径回落起始页
- 打开内部页后 `HasLiveView == false`（确实没建 WebView2）
- 地址栏输入 `nano://bookmarks` 走同一条路由
- 内部页 → 网页：视图被替换（`tabHost.Children == 1`）且导航写入历史
- Chrome JSON 导入 2 条（文件夹 `书签栏`、`书签栏 / 开发`）、Netscape HTML 导入 2 条（文件夹 `工具`）
- `Ctrl+D` 收藏/取消、`Ctrl+H` 历史、`Ctrl+Shift+O` 书签
- 真实运行：`--open-url=nano://history` 截图正常；关闭后会话写入 `InternalPage: "history"`，重启原样恢复（`out/m3-history-live.png` / `m3-restore.png`）
- `--memtest` 回归 8/8

**未做（M3-c/d）**：缩放、下载列表、崩溃恢复；书签拖拽排序、编辑标题；历史按天删除。页内查找不列入——用 WebView2 内置即可。

### 0.12 M3-c：缩放与下载（2026-11-04 21:17）

**范围**：`Ctrl+±/0` 缩放（按站点记忆）、`nano://downloads` 下载列表与 `Ctrl+J`。产物 **660KB**（较 M3-b +35KB），0 警告 0 错误。P0 17 项、三态内存模型、页内查找决策**均未改动**。

| 新增文件 | 作用 |
|---|---|
| `ZoomStore.cs` | 离散缩放阶梯 `ZoomLevels` + 按站点记忆（`%APPDATA%\NanoEdge\zoom.json`，懒加载） |
| `DownloadManager.cs` | 下载项模型 + 进度/状态机 + 落盘（`downloads.json`，200 条封顶）+ 打开/打开文件夹/取消/清除 |
| `DownloadsPage.cs` | `nano://downloads` 纯 WPF 内部页（复用 7.10 的页面骨架） |
| `MainWindow.M3CTest.cs` | `--m3ctest` 验收脚本（54 项断言 + 4 张截图） |

`BrowserHost` 增加 `ZoomFactor` 读写、`ZoomFactorChanged` 观测、`DownloadStarting` 落盘路径与事件接入；`MainWindow` 在统一快捷键入口加 `Ctrl+±/0` / `Ctrl+J`，状态栏显示当前缩放档位，主菜单补对应入口。

**实现判断（记录在案）**

1. **缩放用 WebView2 自带的 `ZoomFactor` API，不自研缩放 UI**；`IsZoomControlEnabled` 保持默认 `true`，`Ctrl+滚轮` 缩放仍由 WebView2 原生处理，宿主只监听 `ZoomFactorChanged` 把滚轮缩放一并记入站点。键盘 `Ctrl+±/0` 走 M3-b 建好的消息泵统一入口，理由是：把记忆档位写进 `ZoomFactor` 会变成 WebView2 的「默认缩放」，此时它自带的 `Ctrl+0` 只会回到站点默认值而不是 100%；由我们掌握档位才能让 `Ctrl+0` 一律复位并清掉该站点记忆。**这与「页内查找用内置、不自研」不冲突**：查找有现成 UI 可替换，缩放没有，用的是官方 API。页内查找一行代码未动。
2. **站点键用 `host[:port]`**，不是完整 origin：`http/https` 同一站点算一家；`localhost:3000` 与 `localhost:8080` 算两家（开发场景是两台不同服务）。`file:` 统一归「本地文件」，`nano://` 不参与缩放。档位是 Chrome 那套离散阶梯（25%…500%），不做 ±0.1 浮点累加。
3. **下载弹窗由 WebView2 让给我们**：`DownloadStarting` 里 `Handled = true` 隐藏它自带的下载对话框，`nano://downloads` 就是 UI；`ResultFilePath` 统一落到系统 Downloads 目录、重名自动加 `(1)`。进度/状态来自 `CoreWebView2DownloadOperation` 的 `BytesReceivedChanged` / `StateChanged`，页面刷新节流 200ms，状态切换立即刷新，没有常驻轮询。
4. **`Ctrl+J` 新开标签**（与 Chrome 一致），不让 `ShowInternal` 的 `Dispose()` 打断正在下载的页面；已有下载页时切过去，不重复开。SPEC 4.3「有未完成下载」豁免沿用 M2 已有的 `tab.ActiveDownloads` 计数，本批次只补了事件的记录端，未改治理逻辑。
5. **下载记录是可搬的普通 JSON**：上次进程退出时仍是 `InProgress` 的记录，启动加载时标成「已中断 · 上次退出时未完成」，不伪装成还在下载。取消态不显示 `Content-Length`（那是声明值，不是实际收到的量）。

**验证**（`--m3ctest` 54/54 PASS，报告 `out/m3c-report.txt`）

- 缩放阶梯上下限/夹取、站点键（大小写/端口/默认端口/本地文件/内部页）、`zoom.json` 落盘与重载、复位清记忆
- 真实 WebView2 上 `Ctrl+0 → 100%`、`Ctrl++ ×3 → 150%`（同时断言 `WebView2.ZoomFactor == 1.5`）、`Ctrl+- → 125%`、状态栏显示「缩放 150%」
- `nano://downloads` 路由 / 标题 / 纯 WPF（无 WebView2）/ 未知路径回落
- **两次真实下载**（本机 `127.0.0.1` 临时 HTTP 服务，不 mock WebView2 事件）：192KB 附件走完 `InProgress → Completed`、落盘字节数一致、完成后 `tab.ActiveDownloads` 归零；8MB 慢速附件停在 `InProgress` 后调用取消，落到 `Canceled`
- `Ctrl+J` 开页 / 复用已有页；`downloads.json` 落盘与重载；清除已结束
- 截图：`out/m3c-{zoom,zoom-toast,downloads,downloads-light,downloads-live,downloads-cli}.png`
- 回归：`--navtest` 30/30、`--memtest` 8/8

**指标影响**：体积 +35KB；新增 renderer 0（内部页纯 WPF）；启动与空载基线不变（两个 JSON 懒加载，缩放只在有 WebView2 的标签上写）；下载进度事件是事件驱动 + 200ms 节流，不增加 10s 治理采样之外的轮询。

**未做（M3-d）**：崩溃恢复、真实 dev 站点内存复测回填 2.1；下载暂停/续传属 P2，不在本版。

### 0.13 M3-d 跳过与后续路线（2026-11-04 21:35）

**用户决策**：「M3-d 没有测试环境跳过」。M3 到此收尾。

| 项 | 处置 |
|---|---|
| 真实 dev 站点内存复测（pgAdmin / eck）并回填 2.1 | **挂起**，不是通过。需要本机有这两个服务；2 章「8 个真实 dev 站点、失焦 60s 后 ≤900MB」仍标未验收 |
| 崩溃恢复（`BrowserProcessExited` / 渲染进程无响应自动重载） | **一并跳过**。它本身不依赖 dev 站点，但本批不单独开工；P0-15 的「单实例」已在 `Program.cs` 完成，崩溃恢复仍欠 |
| 页内查找 | 保持 WebView2 内置 `Ctrl+F`，不自研（0.11 决策不变） |

**遗留验收债**：2.1 的表目前只有本地轻量页与 `--memtest` 的 3 标签数据，真实 dev 站点的内存阶梯未测、900MB 目标未验收。以后有环境时单独补一轮，不阻塞后续里程碑。

**下一个里程碑按 12 章顺序是 M4 — 脚本引擎**（P0-6，7.3 油猴式注入 + 管理界面；M5 密封标签、M6 打磨发布依次在后）。M4 的自测可以用本地页 + 注入脚本完成，不依赖外部 dev 站点，符合当前环境约束。

**影响**：P0 17 项与三态内存模型均未改动；本条只记录跳过与遗留项，代码零改动；内存 / 体积 / 启动三项指标无变化。M3-d 的跳过不代表 2 章验收标准作废。

### 0.14 M4：脚本引擎与管理页（2026-11-04 22:05）

**范围**：`nano://scripts` 纯 WPF 管理页（列表 / 编辑器 / 导入确认 / 按站点临时禁用）、`InternalPages` 路由、主菜单入口、`--m4test` 真实注入验收。产物 **767,488 字节（约 749KB，较 M3-c +60,928 字节）**，0 警告 0 错误。P0 17 项、三态内存模型、页内查找决策**均未改动**。

| 新增文件 | 作用 |
|---|---|
| `ScriptsPage.cs` | `nano://scripts` 内部页：列表（启用开关 / 匹配站点 / 授权 / 最后运行时间）、等宽字体 + 行号编辑器、文件 / 剪贴板 / URL 导入 + `@grant` 确认、删除、打开脚本目录、当前站点临时禁用 |
| `MainWindow.M4Test.cs` | `--m4test` 验收（82 项断言 + 1 张 WebView2 抓图 + 6 张 WPF 渲染图） |

`InternalPages` 增加 `Scripts` 路由与 `Create(..., Func<string> currentSite = null)` 可选参数；`MainWindow` 增加「用户脚本」菜单项与 `OpenScripts()`，`NewTab / Navigate / OpenInternalPage / BuildInternalView` 追加可选的 `sourceSite` 参数；`UserScripts.cs` 修三处（深层子域、`@grant none`、站点键接口）；`ScriptHttp` 的 Cookie 改走 `CookieContainer`。

**实现判断（记录在案）**

1. **管理页是纯 WPF 内部页，编辑器用页内浮层而不是 Popup**：页里没有 HwndHost，Popup 的 airspace 理由不成立；浮层只是 `InternalPage` 内容外面套一层 `Grid`，不改基类骨架，也不影响其它内部页。
2. **「当前站点」必须在覆盖 `tab.Url` 之前取**：打开内部页会把地址改成 `nano://scripts`，之后再算站点就是空的。因此 `OpenInternalPage` 先 `ZoomStore.SiteOf(tab.Url)` 再改地址，站点键经 `NewTab(url, sourceSite)` → `BuildInternalView(tab, site)` 传到页面；从菜单进入时由 `OpenScripts()` 取当前标签的站点。会话恢复的 scripts 页没有来源网页，显示「当前站点：—」并隐藏开关，不猜。
3. **编辑器的行号用第二个只读 TextBox 镜像**：两个框同字体、同字号、同内边距，行数一致，监听代码框内部 `ScrollViewer.ScrollChanged` 同步 `ScrollToVerticalOffset`。比按行测量更稳，也不需要语法高亮。
4. **导入一律先解析再确认**：文件 / 剪贴板 / URL 都汇到 `StageImport`，展示名称、匹配站点、`@grant` 清单和安全提示，点「导入并启用」才写盘。`@grant none` 按 Tampermonkey 语义视为「不申请权限」，不再记成一个未知授权。
5. **按站点禁用只存内存**：`DisabledHosts` 故意不落盘，页面明写「仅本次运行，重启后恢复」；`Reload()` 后清空（自测断言）。
6. **不假装热更新**：脚本增删改只重新注册 `AddScriptToExecuteOnDocumentCreated`，已打开的文档不会重跑。列表页固定提示「只在下次导航或刷新后生效」，成功 toast 也统一带「刷新页面后生效」。
7. **补上两个原本没接线的 API**：`ScriptBridge.Notify / OpenTab` 此前没有订阅者，`GM_notification` 和 `GM_openInTab` 实际是空转。现在在 `BuildShell` 里接到 Toast 与 `NewTab`；自测分别断言到文案与新标签。
8. **`GM_xmlhttpRequest` 的 Cookie 改用 `CookieContainer`**：`Cookie` 是 `HttpWebRequest` 的受限头，直接写 `Headers` 有抛异常的风险；改为从 `CookieManager` 取 Cookie 后逐条塞进容器。
9. **`@match` 的 `*.example.com` 改为匹配任意深度子域**（`(?:[^/]+\.)?`）：原实现只允许一级，`a.b.example.com` 会漏，和 Chrome match pattern 不一致。
10. **退出时 `ScriptStore.Flush()`**：`LastRunUtc` 平时只改内存，原先只有其它改动才落盘，重启后「最后运行时间」会丢。现在关窗口统一 Flush（`_noPersist` 下依旧不写盘）。
11. **抓注入页的图用 `CoreWebView2.CapturePreviewAsync`**：`RenderTargetBitmap` 抓不到 HwndHost（M2 的坑），官方 API 能抓到真实页面，截图里能看到脚本插入的粉色横幅和被 `GM_addStyle` 改色的文本。
12. **复用已打开的 scripts 标签时要重绑站点**：从站点 B 再次点菜单，如果只是 `SelectTab` 旧页面，站点开关会仍指向站点 A，用户可能关错站点。现在会带上新站点重建该内部页（视图是纯 WPF，重建代价可忽略）；已经在 scripts 页上点菜单（站点为空）时不重建，避免无谓刷新。

**实测（`--m4test` 82/82 PASS，报告 `out/m4-report.txt`）**

| 组 | 覆盖 | 项数 |
|---|---|---|
| 元数据解析 | name / namespace / version / 多 `@match` / `@include` / `@exclude` / `@run-at` / `@grant` 去重 / `@connect` / 非法 run-at 回落 / 无名脚本 / `@grant none` | 12 |
| 匹配规则 | `*.example.com` 裸域 + 一级 + 深层、拒绝 `badexample.com`；显式端口命中 / 异端口拒绝 / 默认端口拒绝 / 端口通配；`file://` | 9 |
| 脚本登记 | 8 个脚本全部进 store，列表顺序与匹配正则符合预期 | 1 |
| 真实注入 | `document-start` 在页面自身脚本之前（`startOrder=before-page`，`readyState=loading`）；`document-end` 在 DOMContentLoaded 后（`endState=interactive`，能取到 `#m4-target`）；`GM_info`、7 个 API 类型、`GM_addStyle` 插入 `style[data-nanoedge]` 且计算色变 `rgb(255,120,0)`、`GM_setValue` 到宿主、`@connect` 内 200 / 外拒绝且未发请求、`GM_notification` 到宿主、`GM_openInTab` 真开标签、WebView2 抓图、值文件落盘 | 19 |
| GM 值跨导航 | 新文档 `GM_getValue` 读回 42；缺省值回落 | 2 |
| `@exclude` | 命中页脚本不跑，同页其它脚本照跑 | 2 |
| `@include` | 正则字面量命中 `/include`、不命中 `/page` | 2 |
| 启用开关 | 停用后新导航不跑、重新启用后又跑 | 2 |
| 按站点禁用 | 键为 `127.0.0.1:<port>`；该站点全部脚本（含 `@match /*`）都不注入；恢复后照常 | 3 |
| `@grant` 双端拒绝 | 页面侧 `typeof GM_setValue === 'undefined'`；伪造 `postMessage` 到宿主同样被拒，值没落库 | 4 |
| 管理页 / 入口 / 隔离 | 路由、标题、未知路径回落、纯 WPF（无 WebView2）、行数与 store 一致、明暗 + 禁用态 + 编辑器 + 导入确认四张渲染图、导入确认前不入库 / 确认后入库 / 删除连代码文件、菜单入口、复用标签重绑站点、窗口截图、临时目录与 `%APPDATA%` 隔离 | 26 |

截图：`out/m4-scripts.png`（暗）、`m4-scripts-light.png`（亮）、`m4-scripts-site-off.png`（按站点禁用态）、`m4-scripts-editor.png`（等宽 + 行号）、`m4-import.png`（授权确认）、`m4-injected.png`（WebView2 抓图：粉色注入横幅 + 被 GM_addStyle 改色的文本）、`m4-window.png`（真实窗口，内容区正常渲染，不是黑块）。

回归：`--m3ctest` 54/54、`--navtest` 30/30、`--memtest` 8/8。

**偏离与未做**

- **没有给 scripts 页加快捷键**：SPEC 5.6 没定义，不自造；入口只有主菜单（与「下载」同段）。
- **编辑器不做语法高亮**（按 7.3 原文），也不做自动补全、查找替换。
- **导入不支持「从 URL 自动更新脚本」**：URL 只用于一次性导入，符合 9 章「不做远程脚本自动更新」。
- **`document-idle` 未被端到端断言**：本批只按要求验证 `document-start` / `document-end`；idle 分支代码在，但没有专门的测试脚本。
- **`GM_deleteValue` 未单独断言**：实现与 `GM_setValue` 同一套宿主/授权路径，本批没为它单开用例。
- **没有按脚本粒度的站点例外**：只有全局「当前站点临时禁用」，够用且不做设置界面（M6）。

**指标影响**：体积 +59.5KB（749KB，仍远低于 3MB）；内部页 renderer 增量为 0；脚本存储懒加载，只在打开管理页或首次注入时读，启动与空载基线不变。

### 0.15 M5：密封标签（简化版，2026-11-04 22:35）

**口径变更**：用户明确「不需要那么高级复杂的功能，只需要伪装，以及快捷键切换通知开关」。M5 因此只做三件事，**原 7.4 的独立 Profile、防截屏、DPAPI、`ClearBrowsingDataAsync` 清理、通知重绘桥、诱饵页一律不做**。产物 **784,384 字节（约 766KB，较 M4 +16,896 字节）**，0 警告 0 错误。

| 项 | 落地 |
|---|---|
| 伪装 | 标签标题固定「文档」（`DisplayTitle`）、tooltip 不显示 URL、地址栏留空、不进历史、不进会话、不进地址栏联想、不能收藏 |
| 切换 | `Alt+\`` 在密封标签与「上一个普通标签」之间来回切；走应用内消息泵，不是 `RegisterHotKey` |
| 通知 | `AppSettings.SecretNotifications`（默认关）：关时对密封标签的 `Notification` 权限请求 `Deny + Handled`，开时 `Allow`；主菜单可切换 |
| 不留痕 | 缩放不写 `zoom.json`、丢弃快照不落盘、下载记录不写站点 host、导航日志写 `(secret)` 而不是 URL、导航失败 Toast 不显示 host |

**实现判断**

1. **不做独立 Profile**：Cookie / 缓存与主浏览器共享。用户要的是"屏幕上别露馅"，不是"数据隔离"；省掉 Profile 也省掉清理与 DPAPI 一整套。
2. **"全局热键"退化为应用内快捷键**：`RegisterHotKey` 要处理占用失败、注销、跨进程唤醒，用户明确说不要复杂功能；窗口在前台时 `Alt+\`` 已够用。SPEC 5.6 的键位不变，语义写清。
3. **不建诱饵页**：切走的目标就是"进入密封标签前的那个普通标签"，不需要新增 `nano://blank` 路由。
4. **伪装分两层堵**：屏幕上看得到的（标签、地址栏、Toast、内存面板）和磁盘上留下的（历史、会话、联想、缩放、快照、下载记录、`nanoedge.log`）逐项过了一遍，没有只改标题就交差。
5. **通知开关默认关**：密封标签的诉求是"别响、别弹"；要收通知时在菜单里开。页面拿到的是明确的 `denied` / `granted`，不是静默挂起。
6. **必须显式 `Handled = true`**：`PermissionRequested` 不设 State 时 WebView2 会弹自己的权限条，等于把"密封"暴露成一条系统提示。
7. 不碰 Ctrl+F、缩放、下载、脚本注入等既有能力。

**实测（`--m5test` 20/20 PASS，报告 `out/m5-report.txt`）**

| 组 | 覆盖 | 项数 |
|---|---|---|
| 创建与伪装 | 新建后 `IsSecret`、`DisplayTitle=文档`、导航后真实标题只存在 `Title` 里、标签仍显示「文档」、地址栏为空 | 6 |
| 不留痕 | 密封导航后历史 0 条；`BuildSession()` 不含密封标签、活动标签为空、不含真实 URL；`nanoedge.log` 新增段落不含密封 URL（导航日志写 `(secret)`） | 5 |
| 通知 | 开关关 → 页面 `Notification.requestPermission()` 得 `denied`；开关开 → 换一个 origin 得 `granted` | 2 |
| 快捷键 | `Alt+\`` 切到密封标签、再按切回刚才的普通标签 | 2 |
| 控制组 | 普通标签导航会进历史（证明上面不是"历史被整体关掉"） | 1 |
| 视觉证据 | `m5-window.png`（标签条「文档」+ 空地址栏）、`m5-tabstrip.png`（工具栏/标签条 WPF 渲染） | 2 |
| 页面加载 | 两个 origin 的页面都真实加载完成 | 2 |

截图：`out/m5-window.png`（窗口：`新标签页 / 文档(active) / m5 page`，地址栏空）、`out/m5-tabstrip.png`（同一区域的 WPF 渲染，HwndHost 内容区不参与）。

回归：`--m4test` 82/82、`--m3ctest` 54/54、`--navtest` 30/30、`--memtest` 8/8。

**偏离与未做**

- **没有全局热键**：只有应用内 `Alt+\``，窗口失焦时无效。
- **没有独立 Profile / 防截屏 / DPAPI / 清理 / 通知重绘 / 诱饵页**（用户口径，见 7.4 的"被砍掉"清单）。
- **关闭密封标签不会清 Cookie**：共享 Profile 的必然结果；需要彻底隔离请用独立 Windows 账户或虚拟机。
- **WebView2 自己的缓存 / History 数据库仍会记录访问过的 URL**：`UserDataFolder` 是共享的，不做独立 Profile 就清不掉。NanoEdge 只保证自己的 `history.json` / `session.json` / `zoom.json` / 快照 / 日志不写。
- **多密封标签不轮换**：`Alt+\`` 只切最近创建的那个，够用。
- **密封标签会出现在内存面板里**（标题「文档」）：有意保留，用户要能看到并释放它的内存。

**指标影响**：体积 +16.5KB（766KB）；密封标签只多一个 renderer，与普通标签相同，无额外进程；无启动开销。

### 0.16 M6-a：发布收尾（2026-11-04 22:55）

**范围**：图标落地、默认浏览器注册、外部唤起、崩溃恢复、Release 打包。**不含双语与设置界面**（仍属 M6-b，见 12 章）。产物 **854,016 字节（约 834KB，较 M5 +69,632 字节）**，`dist/NanoEdge-0.1-win64.zip` **556,322 字节**，0 警告 0 错误。

| 项 | 落地 |
|---|---|
| 图标 | `assets/nanoedge.ico`（16/32/48/64/256，PNG 帧）；`ApplicationIcon` 进 PE 资源（资源管理器/任务栏），`EmbeddedResource` 供 WPF `Window.Icon`（Alt+Tab） |
| 默认浏览器 | HKCU：ProgID `NanoEdgeHTML`、`Software\Clients\StartMenuInternet\NanoEdge\Capabilities`（http/https）、`RegisteredApplications`；菜单显式点击，随后打开系统「默认应用」 |
| 外部唤起 | 命名管道 `NanoEdge.Ipc.v1`：第二实例把 URL 转发给已运行实例后静默退出（exit 0）；无参数则只激活窗口 |
| 崩溃恢复 | 渲染进程退出 → 自动重载一次（20s 冷却）；无响应 → Toast 带「重新加载」；`BrowserProcessExited` → 拆掉全部视图、清环境缓存、重建活动标签 |
| 打包 | `dist/NanoEdge-0.1-win64.zip`，单 exe，`Compress-Archive` 生成 |

**实现判断**

1. **图标按目标尺寸逐档渲染，不是"一张 512 缩到 16"**：Edge headless 分别以 16/32/48/64/256 出图，小尺寸笔画更干净。headless 有最小窗口宽度，所以把图标钉在页面左上角、只截 `N×N`，避免 256 以下被裁成局部。
2. **ico 进两份资源**：PE 图标给资源管理器/任务栏，嵌入资源给 WPF 窗口。多花约 22KB，换"窗口图标一定不依赖 PE 资源加载成功"。
3. **默认浏览器只写 HKCU、只注册 http/https**：不碰 HKLM，不抢 `.htm/.html`。Win10+ 的 `UserChoice` 有哈希保护，程序无法静默生效，所以点击后直接打开「默认应用」并明说"请选择 NanoEdge"，不假装已生效。
4. **外部唤起用命名管道，不用 `WM_COPYDATA`**：不用找 HWND、不用枚举窗口；单实例 Mutex 仍在，拿不到锁的实例才走 IPC。管道服务在 `Loaded` 之后启动，避免在 `Application.Run` 之前建 WebView2。
5. **崩溃恢复分两级**：渲染进程崩溃只影响一个标签，自动重载一次并加 20s 冷却（防止坏页面反复崩）；浏览器进程退出会让所有 `CoreWebView2` 作废，只能拆视图 → 清 `_environmentTask` → 让活动标签重新走懒加载，其余标签标为已丢弃。
6. **截图模式强制 `Normal` + 1200×800**：会话记着「最大化」时，`--screenshot` 会截出 3456×1454 的图，缩到聊天窗口里没法看。

**实测（`--m6test` 23/23 PASS，报告 `out/m6-report.txt`）**

| 组 | 覆盖 | 项数 |
|---|---|---|
| 图标 | `ExtractAssociatedIcon` 拿到 32×32 真图标并存 `m6-exe-icon.png`；嵌入资源 22,225 字节 | 3 |
| 默认浏览器 | 注册成功、open command 带 `"%1"`、http/https 都映射到 `NanoEdgeHTML`、`RegisteredApplications` 指向 Capabilities、exe 路径正确 | 6 |
| 外部唤起 | 真起第二个 `NanoEdge.exe <url>`：主实例多出该标签、URL 真的加载、第二个进程 exit 0；`--open-url=<url>` 形式同样能转交 | 4 |
| 渲染崩溃 | kill 掉 renderer PID：日志出现 `process failed: RenderProcessExited`、自动重载、页面恢复可用 | 4 |
| 浏览器崩溃 | kill 掉 browser PID：日志出现 `browser process crash: views reset`、活动标签重新加载、有 live WebView2 | 4 |
| 截图 / 清理 | `m6-window.png`；注册表在测试后还原干净 | 2 |

截图：`out/m6-exe-icon.png`（从 exe 里抠出来的图标）、`out/m6-window.png`（崩溃恢复后的窗口）、`out/m6-packaged.png`（**解压 zip 后在临时目录启动**的截图，1200×800）。

回归：`--m5test` 20/20、`--m4test` 82/82、`--m3ctest` 54/54、`--navtest` 30/30、`--memtest` 8/8。

**偏离与未做**

- **没有注册 `.htm/.html` 文件关联**：只做 http/https，避免抢掉用户双击本地网页的默认程序。
- **没有「取消注册」菜单项**：7.9 提过"取消注册 = 删键"，本批只做了注册入口；需要时删三个 HKCU 键即可（清单见 7.9）。
- **`WM_COPYDATA` 改为命名管道**（见判断 4），启动参数同时接受裸 URL 与 `--open-url=<url>`。
- **双语（P0-13）、设置界面（P0-14）、首次运行说明未做**：属 M6-b，不在本批。
- **崩溃恢复不覆盖 GPU/Utility 进程**：只处理渲染进程与浏览器进程两类；其它类型给通用 Toast。

**指标影响**：+69,632 字节（834KB），其中图标两份额外占约 45KB，IPC / 默认浏览器 / 崩溃恢复约 24KB。无启动开销：IPC 只起一个后台管道循环，不轮询。

### 0.17 M6-b：双语 + 设置界面（2026-11-05 08:20）

**范围**：中/英双语（P0-13）与 `nano://settings` 设置页（P0-14）。用户明确「不需要首次运行说明、安全模式」，这两项不做。产物 **892,416 字节（约 872KB，较 M6-a +38,400 字节）**，`dist/NanoEdge-0.1-win64.zip` **567,738 字节**，0 警告 0 错误。

| 项 | 落地 |
|---|---|
| 双语 | `Locale.cs`：调用点写 `L.T("中文原文")`，字典 306 条英文，缺词回退中文；`AppSettings.Language` 持久化，`--lang=en` 可启动覆盖 |
| 切换 | 改语言 → 重建工具栏 / 标签条 / 所有内部页与起始页视图 / 标签标题；**WebView2 标签不动** |
| 设置页 | `nano://settings` 纯 WPF：外观（主题、语言）、布局（布局、固定展开、状态栏）、内存（三档 + 实时阈值）、隐私（密封标签通知）、关于（版本、数据目录、默认浏览器） |
| 数据隔离 | `SettingsStore.FilePath / Persist`，自测写临时文件，不碰用户的 `settings.json` |

**实现判断**

1. **不用 `ResourceDictionary` + `DynamicResource`**：整个 UI 是纯 C# 构建、没有 XAML，硬套资源字典等于再发明一套绑定。改为「中文原文当 key + 字典表」，调用点只多一层 `L.T(...)`，缺词回退中文、不会出现空白或 key。
2. **语言切换只重建本地视图**：WebView2 是 HwndHost，不能 reparent，也没必要为了 UI 语言重载网页。内部页/起始页整块换新视图，网页标签原样保留。
3. **持久化数据不翻译**：`zoom.json` 的站点键、脚本 namespace/key、`session.json` 的标题都是数据；只有显示层过 `ZoomStore.SiteLabel`（目前只有「本地文件」需要映射）。
4. **设置页不直接碰 WebView2**：页面只改 `AppSettings` 并抛 `Changed(kind)`，主题/布局/内存/语言由 `MainWindow.OnSettingChanged` 统一应用并落盘，避免页面持有外壳引用。
5. **自测强制中文**：所有 `--mXtest` 忽略用户语言（`IsTestRun()` → `zh-CN`），否则断言里的中文会随用户设置漂移；`--lang=` 仍可覆盖。
6. **设置页的内存行显示真实阈值**（失焦 2 秒 / 后台 30 分钟 / 上限 700MB），不是抽象的档位名。
7. **顺带修旧 bug**：`ApplyLayout` 重建工具栏后地址栏会被清空（切主题/布局都会触发），现在重建后补回 `AddressFor(active)`。

**实测（`--m6btest` 28/28 PASS，报告 `out/m6b-report.txt`）**

| 组 | 覆盖 | 项数 |
|---|---|---|
| 路由 | `nano://settings` 归一化、中文标题、`Create` 返回 `SettingsPage` | 3 |
| 中文页 | 标题「设置」、外观/布局/内存三段齐全、内存行显示 700MB、渲染成图 | 4 |
| 英文页 | 标题 Settings、Appearance/Layout/Memory 齐全、**扫描可视树无未翻译中文**、渲染成图 | 4 |
| 运行时切换 | 菜单打开设置页、切到 en-US、标签标题变 Settings、视图实例被替换、重建后仍全英文、状态栏英文 | 6 |
| 主题/布局/内存 | 切亮色即时生效、切标准布局后状态栏同步、激进档把上限改成 450MB | 3 |
| 落盘 | `settings.json` 写入临时目录；语言/主题/内存档位重新 Load 后仍在 | 4 |
| 截图 | `m6b-window-en.png` | 1 |
| 切回中文 | 标签标题回「设置」、状态栏回中文、设置页文本回中文 | 3 |

截图：`out/m6b-settings-zh.png` / `m6b-settings-en.png`（980×1000 整页）、`m6b-window-en.png`（真实窗口）、`m6b-start-en.png`（英文起始页，`--lang=en` 预览）。

回归：`--m6test` 23/23、`--m5test` 20/20、`--m4test` 82/82、`--m3ctest` 54/54、`--navtest` 30/30、`--memtest` 8/8。

**偏离与未做**

- **只有 zh-CN / en-US**：没有 resx / satellite 程序集，新增语言要改 `Locale.cs` 并补一列；不打算为两种语言上 i18n 工具链。
- **诊断日志仍是中文**（`nanoedge.log` 里的 reason、`Diagnostics.Log` 文案）：不是 UI，翻译收益低。
- **设置页没有快捷键自定义 / 脚本管理入口**：脚本仍在 `nano://scripts`；快捷键要录制式 UI，属 P1-7，本批不做。
- **首次运行说明 / 安全模式不做**（用户口径）。
- **静态文案之外的动态内容不翻**：网页标题、书签/历史里的用户数据原样显示。

**指标影响**：+38,400 字节（872KB）：Locale 字典（306 条）+ 设置页 + 自测。启动无额外开销，字典是静态只读表。

### 0.18 对照 ZZZ 后的克制补齐（2026-11-05 10:12）

**背景**：用户确认内部对照报告里的 B（P1）、C（可选）、D（不做）「感觉功能我都不用」，NanoEdge 的功能面刻意比 ZZZ 精简；只对**已实现的相似功能**克制借鉴。本批只补 P0 欠账与工程细节，不引入任何新的产品概念，也不动 P1/P2。

产物 **924,160 字节（约 903KB，较 0.17 +31,744 字节）**，0 警告 0 错误。

| 项 | 落地 | 借鉴来源 |
|---|---|---|
| 最近关闭 | 内存态 25 条（跳过密封/内部/起始页，仅 http/https/file）+ `Ctrl+Shift+T`；补 SPEC 5.6 的 P0 欠账 | ZZZ `TabServices.RecentlyClosed` |
| 标签操作 | 中键关闭、关闭右侧（按 `VisibleTabs()` 的可视顺序）、复制标签 | ZZZ `MainWindow` / `TabServices` |
| favicon | `Favicons.cs` 按 origin 的**纯内存**缓存（≤256 项）；标签 chip / 丢弃占位 / 起始页快捷方式优先显示图标，失败回首字母 | ZZZ `FaviconCacheService`（去掉网络补抓） |
| 快捷键 | `Ctrl+Shift+M` 内存面板（SPEC 5.6）、`Ctrl+F5` 真硬刷新（CDP `Page.reload ignoreCache:true`） | — |
| 起始页 | 快捷方式增删改：页内编辑浮层（标题/网址/保存/取消/删除）、`+` 磁贴、右键菜单、上限 24 条、URL 归一化、落盘 `settings.Shortcuts` | 补 SPEC 7.8 欠账 |
| 写入防抖 | session 150ms 合并写、窗口位置 500ms 合并写；`Closing` 时统一 flush | ZZZ `SessionService` / `QueueSessionSnapshot` |
| 退出释放 | `BrowserHost.DisposeAll()`：关窗前释放全部控制器，避免内核子进程比窗口活得更久 | ZZZ `CompleteShutdownAsync`（只取同步部分） |

**实现判断**

1. **标签宿主不动**：ZZZ 用 `ContentControl.Content` 换视图（HwndHost 会重建子 HWND），我们继续用「视图常驻同一 Grid + `Visibility` 切换」，这是 Phase 0 已验证的更稳方案。
2. **favicon 不学 ZZZ 的 HTTP 补抓**：SPEC 7.8 明确「不为了图标发网络请求」。本机实测 `GetFaviconAsync` 对 localhost 测试页与 Bing 都只返回 1×1 占位图，但 `FaviconUri` 是对的。因此三条零额外请求的路径：`data:` URI 直接解 base64 → 截获 `WebResourceResponseReceived` 里浏览器自己下载图标的那次响应 → 官方 `GetFaviconAsync`。拿不到图标的站点回首字母色块，不影响功能。
3. **快捷键冲突按 SPEC 裁决**：SPEC 5.6 把 `Ctrl+Shift+M` 给内存面板，代码原来用它轮换布局。本批按 SPEC 改正，布局轮换移到 `Ctrl+Shift+L`（`Ctrl+L` 仍聚焦地址栏，已加 `!shift` 守卫）。若不接受，改回只需动 `HandleShortcut` 两行。
4. **起始页编辑器是页内浮层，不是 Popup**：该页没有 WebView2，WPF 元素可以直接盖住；与 `nano://scripts` 的编辑器一致，也能被截图捕获。
5. **退出只做同步释放**：ZZZ 为了便携盘安全弹出会 Cancel 关窗、等子进程退出再 Close；我们数据目录在 AppData、不做便携，同步 `DisposeAll()` 就够，不需要两段式关窗。
6. **明确不抄**：在线搜索联想（破坏零遥测）、DPAPI 加密历史/书签（与「数据可搬」冲突）、MVVM/XAML 重写、自研页内查找、独立隐私 Profile、工作区、广告拦截、翻译。

**实测（`--m7test` 31/31 PASS，报告 `out/m7-report.txt`）**

| 组 | 覆盖 | 项数 |
|---|---|---|
| 写入防抖 | 6 次 `SaveSession` 只写一次、空 flush 不重复写 | 4 |
| 标签操作 | 关闭右侧、复制、中键关闭（真实 `MouseDown` 事件） | 5 |
| favicon | PNG 可解码、data URI 零请求解码、页面图标抓取、origin 缓存、标签 chip 渲染 Image | 5 |
| 最近关闭 | 关标签后 `Ctrl+Shift+T` 恢复 URL | 3 |
| 快捷键 | `Ctrl+F5` 走硬刷新、`Ctrl+Shift+M` 打开并钉住内存面板 | 2 |
| 起始页 | 编辑器可见、新增、URL 归一化、落盘、编辑替换、非法 URL 拒绝 | 7 |
| 退出释放 | 全部视图归零、tabHost 清空、重复调用幂等 | 3 |
| 截图 | `m7-window.png`、`m7-startpage-edit.png` | 2 |

回归：`--memtest` 8/8、`--navtest` 30/30、`--m3ctest` 54/54、`--m4test` 82/82、`--m5test` 20/20、`--m6test` 23/23、`--m6btest` 28/28，全部 PASS。

**指标影响**：+31,744 字节（903KB）。运行期：favicon 只占内存（≤256 个 origin，不落盘）；会话写盘从「每次标题变化一次」变成「150ms 合并一次」；退出时释放全部 WebView2 控制器。**`dist/NanoEdge-0.1-win64.zip` 仍是 M6-a 的旧包，本批未重新打包。**

**本批未做 / 文档债**

- 脚本 `@require` / 更多 GM API（对照报告 A-6）：涉及脚本加载链路，下一批再做。
- SPEC 第 9 章仍写「DPAPI 保护静态文件」「关闭自动填充/密码保存」，与 0.15 简化口径及当前 `ConfigureAsync` 不符，已在第 9 章注明实际状态。
- 5.1/5.2 的「跨断点提示一次」「<900px 标签条折叠」、5.6 的 `Ctrl+Shift+A` 标签搜索仍属未实现（前者视觉债，后者 P1）。
- 起始页快捷方式的「历史自动推荐 / 拖拽排序」未做（SPEC 7.8 写了，本批只补增删改）。

### 0.19 用户脚本能力补齐（2026-11-05 12:15）

**背景**：0.18 对照报告 A-6 留到下一批。用户点名「先处理脚本的功能改进」，本批只做 `@require` + `GM_listValues` + `GM_setClipboard` + `unsafeWindow` 四项，不引入其它 GM API，也不动 P1/P2。

产物 **960,512 字节（约 938KB，较 0.18 +36,352 字节）**，0 警告 0 错误。

| 项 | 落地 |
|---|---|
| `@require` | 解析 URL 列表；**只在导入/保存时下载一次**，正文固化到 `scripts\<id>.require.<n>.js`，运行期不再联网；单文件上限 5MB、最多 10 个、15s 超时；任一失败则整个导入/保存不写库，保留确认卡片可重试 |
| 注入顺序 | 依赖正文按头部顺序拼在主脚本之前，与主脚本共享同一个函数作用域（`var` 可见）；依赖之间插入独立分号防 ASI 拼接 |
| `GM_listValues` | 返回当前命名空间的键数组；数据源是本页加载时的快照 + 本页写入，**其它标签页之后写入的键要刷新后才可见** |
| `GM_setClipboard` | JS 侧 `postMessage` → 宿主 `ScriptBridge` → WPF `Clipboard`；只写纯文本，空串表示清空，第二个 `type` 参数忽略 |
| `unsafeWindow` | 直接传页面 `window`。注入走 `AddScriptToExecuteOnDocumentCreatedAsync`，脚本本来就在页面主世界，**无需 `@grant`**；声明 `@grant unsafeWindow` 也按已知权限显示 |
| 管理页 | 导入确认卡片列出依赖地址与「导入时下载」提示；编辑器元数据增加依赖数量；删除脚本连同依赖文件一起清理 |

**实现判断**

1. **不学 ZZZ 的运行时拉取**：每次导航都下载依赖既拖慢页面又违背 SPEC 9「不做远程脚本自动更新」。导入时固化最接近「装一次、之后离线可用」的产品口径。
2. **失败就整体拒绝**：不做「缺一个依赖也先导入」的部分成功，否则脚本以看似正常的状态运行，排查成本更高。确认卡片保留，用户可再点一次。
3. **依赖下载不带 Cookie、不走 `@connect`**：`@connect` 管的是运行时 `GM_xmlhttpRequest`；`@require` 地址写在脚本头部，用户点「导入并启用」时已看过清单，属于同一授权动作。请求 UA 为 `NanoEdge/0.1 (@require)`。
4. **`GM_setClipboard` 必须过 `@grant` 双端校验**：页面伪造 `{__ne:'clipboard', id}` 但脚本没有授权时，宿主直接拒绝（`--m8test` 已验证剪贴板保持原值）。
5. **`unsafeWindow` 不是新能力**：我们不做隔离世界，脚本本来就能摸到页面 `window`；这里只是提供 Tampermonkey 兼容别名。诚实边界写进 7.3。

**实测（`--m8test` 45/45 PASS，报告 `out/m8-report.txt`）**

| 组 | 覆盖 | 项数 |
|---|---|---|
| 解析/注入串 | 两条 `@require` 顺序、去重、已知权限、wrapper 中依赖先于主脚本、bootstrap 暴露新 API | 5 |
| 导入固化 | 确认前不写库、摘要显示依赖、卡片截图、真实下载两个依赖、内存缓存、依赖文件落盘、重载后仍在 | 10 |
| 运行时 | 依赖先执行、顺序正确、库函数/`var` 可见、`unsafeWindow===window`、两个 GM API 类型、`GM_listValues` 只含本命名空间、宿主落值、真实剪贴板、页面截图 | 14 |
| 权限拒绝 | 未声明 API 为 undefined、`unsafeWindow` 无授权仍可用、伪造消息已发出、宿主拒绝 | 5 |
| 失败/清理 | 404 依赖不写库、保留待导入、失败提示、不带缓存、删除脚本清理依赖文件 | 8 |
| 数据隔离 | 临时目录落盘、`%APPDATA%` 两个文件未新增 | 3 |

回归：`--memtest` 8/8、`--navtest` 30/30、`--m3ctest` 54/54、`--m4test` 82/82、`--m5test` 20/20、`--m6test` 23/23、`--m6btest` 28/28、`--m7test` 31/31，全部 PASS。

**指标影响**：+36,352 字节。运行期 `@require` 只在导入时发请求；注入字符串最多多出「10 × 5MB」，实际脚本通常几十 KB；`GM_listValues` 纯内存遍历，无额外进程。

**本批未做**：`GM_download` / `@resource` / `GM_registerMenuCommand` / `@noframes`（对照报告判定收益低）；`@require` 的运行时热更新；`unsafeWindow` 的隔离世界（那是架构变更，不做）。

---

## 1. 产品定义

### 1.1 一句话

一个 2–3MB 的单文件 Windows 浏览器，用系统自带的 WebView2 渲染，并且**把"不在看的页面"当成不存在的页面来管内存**。

### 1.2 目标场景（来自你的真实用法）

- 16GB 笔记本，Edge 和 VSCode 已经吃掉大半内存；
- 需要同时开着 pgAdmin4、eck 控制台、若干项目测试环境的 dev 网址；
- 需要给某些站点自动注入 JS（替代油猴）；
- 需要一个"尽量不可见、但要能收通知、能一键藏起来"的聊天页；
- 屏幕纵向空间紧张，要紧凑单行布局 + 竖排标签 + 分组折叠；
- 不接受安装包，不接受运行时依赖，不接受 100MB 的 exe。

### 1.3 设计原则

1. **单文件**：双击即用。除数据目录外不写系统任何位置（注册表零写入，除非用户主动点"设为默认浏览器"）。
2. **内存可预测**：任何页面在不需要时都能降到 0；不靠"感觉省了"，靠面板上可验证的数字。
3. **少而精**：不做账号同步、不做扩展商店、不做 AI 侧边栏。
4. **键盘优先**：所有高频操作都有快捷键，鼠标是补充。
5. **数据可搬**：设置、书签、脚本全是可读文件，压缩一下就能带走。

### 1.4 非目标（v1 明确不做）

- 账号体系与云同步（书签/历史/设置）
- 内置密码管理器（不存密码，交给 Bitwarden/KeePassXC）
- Chrome 扩展 / CRX 兼容
- 内置广告拦截（不做，页面广告的内存由丢弃策略兜底）
- macOS / Linux / ARM64（ARM64 只保证 x64 仿真可用）
- 多窗口与标签拖出（v1 只做单窗口多标签）

---

## 2. 硬约束与验收指标

**口径统一**：内存一律同时记录 `PrivateMemorySize64`（私有提交）和 `WorkingSet64`（工作集，即任务管理器显示的数字）。v1.0 只按 Private 估算，导致对"用户看到的内存下降"判断失真；v1.1 起**验收以工作集为准**。

| 指标 | 目标值 | 测量方式 |
|---|---|---|
| 分发物 | 单个 `NanoEdge.exe`，≤ **3.0MB** | Release 产物大小 |
| 系统要求 | Win11 / Win10（**需已安装 WebView2 Runtime**）；.NET FW 4.8 | 启动自检：检测不到就提示并退出，不做安装引导 |
| 冷启动到外壳可交互 | ≤ **800ms**（目标 500ms） | 秒表 + ETW，取 5 次中位数 |
| 冷启动到首个网页可交互 | ≤ **1.8s**（本地 dev 站点） | 同上 |
| 空载基线（1 个空白新标签，无 WebView2） | ≤ **120MB** 工作集 | Phase 0 已测 86.5MB |
| 5 个轻量标签、常态 | ≤ **700MB** 工作集 | Phase 0 已测 629.4MB |
| 5 个轻量标签、失焦降冷后 | ≤ **450MB** 工作集 | Phase 0 已测 408.3MB |
| 5 个轻量标签、全部丢弃后 | ≤ **120MB** 工作集 | Phase 0 已测 86.5MB |
| 8 个真实 dev 站点、失焦 60s 后 | ≤ **900MB** 工作集 | 待 M3 用 pgAdmin / eck 复测 |
| Discarded → 可交互 | ≤ **1.5s**（局域网 dev 站点） | 本地页实测 119ms |
| 数据目录 | 设置 `%APPDATA%\NanoEdge`；缓存/Profile `%LOCALAPPDATA%\NanoEdge` | 代码审查 |
| 权限 | 全程普通用户，无 UAC | 干净账户测试 |

### 2.1 Phase 0 实测基线（2026-11-04，本机，5 个轻量本地页）

| 状态 | 私有提交 | 工作集 | renderer 工作集 | 进程数 |
|---|---|---|---|---|
| Normal（5 标签可见） | 358.6MB | **629.4MB** | 269.3MB（≈54MB/标签） | 10 |
| `Low`（全部降冷，JS 照常） | 345.6MB | **408.3MB** | 47.9MB（≈9.6MB/标签） | 10 |
| `Suspended`（折叠 + 冻结） | 343.8MB | 416.5MB | 55.5MB | 10 |
| 最小化（仍冻结） | 338.7MB | **84.7MB** | 0MB | 10 |
| 全部标签 `Discarded` | **58.8MB** | 86.5MB | 0MB | **1**（仅 HOST） |

固定开销拆解（私有提交）：WPF 外壳 52–59MB、GPU 进程 89–104MB、browser 进程 64MB、utility 2×11MB；每个轻量页 renderer 约 20–22MB。

关键读法：

- 工作集下降主要是**物理页被换出**，私有提交几乎不变。任务管理器数字会掉，但"提交大小"不会。这一点要在内存面板里如实标注，别让用户以为内存被"擦掉"了。
- GPU 进程是单项最大固定成本（≈95MB），但**关掉它会让滚动变卡**，不值得（见 4.8）。
- 真实 dev 站点（pgAdmin、eck）单标签内存会远高于本地测试页，M3 必须复测后再回填这张表。

> M2 实现后的端到端实测见 0.8（3 标签：常态 424.4MB → 失焦降冷 336.2MB → 释放后台 335.7MB），与上表同量级；私有提交直到丢弃才真正下降（295→256MB）。

> **未验收项（0.13）**：2 章「8 个真实 dev 站点、失焦 60s 后 ≤900MB」与 3.3 的「真实 dev 站点单标签 40–90MB」仍是预期值。M3-d 因本机无 pgAdmin / eck 环境跳过，这类页面尚未实测；上表所有数字只覆盖本地轻量页。

---

## 3. 技术选型

### 3.1 结论

```
WPF (net48) 外壳
  └── 每标签一个 WebView2 控件（共享同一个 CoreWebView2Environment）
        └── Evergreen WebView2 Runtime（系统自带，随 Edge 更新）
打包：Costura.Fody 嵌入托管 DLL + WebView2Loader.dll 作为嵌入资源，运行时释放
```

依赖白名单（**上限 4 个 NuGet**）：

| 包 | 用途 | 体积 |
|---|---|---|
| `Microsoft.Web.WebView2` | 内核 | ~1.2MB |
| `System.Text.Json` | 设置/会话/脚本 | ~700KB |
| `CommunityToolkit.Mvvm`（可选） | MVVM 样板代码 | ~150KB |
| `Costura.Fody` + `Fody`（构建期，不进产物） | 单文件打包 | 0 |

### 3.2 为什么不是别的

| 方案 | 否决原因 |
|---|---|
| Win32 C++ + WebView2 | 内存/启动最优，但标签拖拽、动画、IME、无障碍、高 DPI 全要自绘，工期约 3 倍，且没有成熟参考 |
| .NET 8/9 自包含单文件 | exe 70–150MB、首启解压 1s+，直接违背"轻量免安装" |
| Electron / CEF | 自带 Chromium，150MB+，与本需求相反 |
| WebView2 Fixed Version | 不依赖系统运行时，但要随包分发 ~180MB Runtime，违背单文件 |
| WebView2CompositionControl | 能解决 airspace，但走屏幕捕获合成：**掉帧 + DRM 视频无法播放**，浏览器不能接受 |

### 3.3 进程模型（决定了内存下限）

WebView2 的进程组由 **user data folder** 决定：

```
NanoEdge.exe（WPF 外壳，~50–80MB）
└── msedgewebview2.exe 进程组（同一个 UserDataFolder）
    ├── browser 进程        1 个   ~60–90MB
    ├── GPU 进程            1 个   ~50–90MB
    ├── network/utility     1–2 个 ~20–40MB
    └── renderer 进程       每标签 1 个  轻量页实测 ~20MB 私有 / ~54MB 工作集；真实 dev 站点预计 40–90MB，M3 复测
```

结论：**browser/GPU/network 是固定成本，只有 renderer 随标签增长**。所以：

- 省内存的主战场是 **renderer**（降冷 / 丢弃）；
- 密封标签用**独立 Profile 但仍属同一进程组**，只多一个 renderer，不另起 browser 进程；
- 隐私标签若用独立 UserDataFolder 会另起整套进程（+150MB），v1 不这么做。

### 3.4 Airspace 策略（重要）

WebView2 是 HWND，永远盖在 WPF 之上。因此：

- **所有浮层**（地址栏联想、右键菜单、下拉、Toast、悬浮展开的竖排标签）用 **WPF Popup**——它本身是独立顶层窗口，天然在 WebView2 之上；
- **侧栏/面板不覆盖网页**：要么压缩 WebView 宽度（重排），要么用 Popup 覆盖（不重排）；
- 绝不为浮层引入 `WebView2CompositionControl`。

### 3.5 单文件打包细节

- `WebView2Loader.dll` 按架构作为 `EmbeddedResource` 嵌入；
- 首次运行释放到 `%LOCALAPPDATA%\NanoEdge\native\<version>\`，校验长度后 `SetDllDirectory`；
- 托管 DLL 由 Costura 在内存中加载，不落盘；
- 多语言不用卫星程序集（会生成子目录），用嵌入的 XAML ResourceDictionary。

---

## 4. 内存治理（核心）

### 4.1 三态模型（v1.3：取消 `Suspended`）

| 状态 | 操作 | 页面状态 | 实测工作集（5 标签） | 恢复 |
|---|---|---|---|---|
| `Active` | 无 | 全速 | 629MB（100%） | — |
| `Low` | `MemoryUsageTargetLevel = Low` | 保留；JS / WebSocket / 定时器照常 | **408MB（65%）**，renderer 单独 -82% | 即时 |
| `Discarded` | `Dispose()` + 快照 | 进程消失，只剩元数据 | 86MB（14%）；全部丢弃后进程组退出 | 本地页 119ms；真实站点待测 |

**为什么砍掉 `Suspended`**：v1.0 以为「35–55% 降幅」来自冻结，Phase 0 实测证明降幅全部来自 `Low`；冻结后工作集反而**回升**（5 标签 408→416MB；M2 三标签 336→363MB）。它唯一的价值是停 JS / 停定时器 / 省 CPU，代价却是：`TrySuspendAsync` 要求控件不可见（当前可见标签没法冻）、与 `MemoryUsageTargetLevel` 必须串行互斥（多一组状态守卫）、面板多一档状态与文案、通知要靠"保活例外"绕开。收益远小于复杂度，v1.3 直接删除，只保留两档降内存 + 一档释放。

- `Low` 是"失焦/转后台即降内存"的主力，且**零副作用**——页面还活着，JS 定时器、WebSocket、通知都不会断；
- `Discarded` 是唯一真正归还内存（进程消失）的手段；
- 窗口最小化时 Windows 会自动把整个进程组工作集压到 ~85MB，我们只需要把标签降到 `Low`，不需要额外动作，但要在面板里说明"提交内存没变"。

> 私有提交（Private）在各档几乎不变（358 → 346MB，全丢弃才降到 59MB）。所有"降内存"话术必须注明是**工作集**。

**M2/三态实现备注**：`Low/Normal` 只通过 `MemoryUsageTargetLevel` 切换，代码里不再出现 `TrySuspendAsync/Resume`；标签的 `MemoryState` 以 API 调用成功后的实际结果为准，失败时保持原状态并在诊断日志里记一条。

### 4.2 触发规则（默认「均衡」档）

| 事件 | 延迟 | 动作 |
|---|---|---|
| 窗口失焦 / 最小化 | 2s | 全部标签（含当前标签）→ `Low` |
| 标签进入后台 | 2s | 该标签 → `Low`（页面继续跑 JS） |
| 标签进入后台 | 累计 30min 未访问 | → `Discarded`（先抓快照） |
| 进程组总内存 > 700MB | 每 10s 检查 | 按 LRU 丢弃后台标签，直到 < 550MB，最少保留 3 个 |
| 标签数 > 12 且总内存 > 500MB | 即时 | 提前丢弃最久未用的 2 个 |
| 窗口重新激活 | 0ms | 当前标签 → `Normal`；其余保持 `Low` |
| 切换到某标签 | 0ms | 该标签 → `Normal`；被丢弃的走重建流程 |

> 原稿的"失焦 60s 冻结"随 `Suspended` 一起删除（见 4.1）。想省 CPU 的用户可以把「均衡」改成「保守」，降冷延后到 30s 即可，不必再引入冻结。

三档预设（设置里可切）：

| 档位 | 失焦降冷 | 自动丢弃 | 内存上限 |
|---|---|---|---|
| 保守 | 30s | 从不 | 1200MB |
| **均衡（默认）** | 2s | 后台 30min | 700MB |
| 激进 | 1s | 后台 5min | 450MB |

### 4.3 豁免规则（永不被丢弃，最多降到 `Low`）

1. `IsDocumentPlayingAudio == true`（在放音频/视频）
2. 有未完成下载
3. 固定标签（pinned）
4. 密封标签且开启"保活以接收通知"（只降到 `Low`，页面照常收消息）
5. 用户标记为"常驻"的站点（按 origin 记，如 eck 控制台）
6. 打开了 DevTools 的标签（无法可靠检测，按"用户手动打开过"记账，直到标签关闭）

### 4.4 丢弃快照

```jsonc
{
  "tabId": "t_7f3a",
  "url": "https://pgadmin.local/browser/",
  "title": "pgAdmin 4",
  "faviconKey": "pgadmin.local",
  "scroll": { "x": 0, "y": 1240, "ratio": 0.42 },
  "zoom": 1.0,
  "historyIndex": 3,
  "forms": [
    { "sel": "#query-editor", "kind": "textarea", "value": "SELECT * FROM ..." }
  ],
  "savedAt": "2026-11-04T14:30:00+08:00",
  "restorable": true
}
```

规则：

- 只存 `input` / `textarea` / `contenteditable`，**跳过 `type=password`、`type=file`**；
- 只存顶层文档（iframe 内的表单不存）；
- 单标签快照上限 256KB，超出截断并标记 `restorable=false`；
- POST 结果页无法重建 → 恢复时显示"该页面由表单提交产生，无法自动恢复"+ 后退按钮。

### 4.5 恢复流程

```
点击已丢弃标签
 → 立即显示占位（favicon + 标题 + 骨架）
 → 创建 WebView2（复用 Environment，~150ms）
 → Navigate(url)
 → DOMContentLoaded 后注入恢复脚本：scrollTo + 填表单
 → 顶部 2s 轻提示"已恢复页面状态"，带"重新加载"按钮
```

### 4.6 通知不会因为降内存而丢（v1.3 已不成问题）

原方案担心"冻结停 JS，聊天页收不到消息"，需要保活例外。取消 `Suspended` 后这个矛盾自然消失：`Low` **不影响 JS、定时器、WebSocket**，页面照常跑。

- **A. 降冷即保活**：所有标签最浅只降到 `Low`，通知链路不需要任何特例。
- **B. 系统通知桥**：`PermissionRequested(Notification)` 显式允许 → `NotificationReceived` 拦截 → 自绘 Toast。窗口隐藏时 Toast 只显示"NanoEdge · 有新消息"，不显示站点名和正文（可配置）。
- C. 依赖站点 Web Push（Service Worker + 持久通知）。不保证所有站点支持，作为补充不依赖。

> 唯一会丢通知的情形是标签被 `Discarded`（进程退出）。所以聊天类站点应该加到「常驻」，或者靠 4.3 的音频/下载/固定等豁免规则保护。

### 4.7 观测

- 数据源：`CoreWebView2Environment.GetProcessInfos()` 拿到进程 Id 和 Kind，再用 `Process.WorkingSet64 / PrivateMemorySize64` 汇总；
- 面板：`nano://memory`（WPF 窗口，不是网页），列出每个进程的 Kind、PID、工作集、所属标签；
- 顶部内存徽标：显示进程组总内存，> 阈值变琥珀色，点击展开；
- 每次降级/恢复写一条诊断日志（默认关闭，可在设置里开）。

### 4.8 反模式清单

- 不用 `--single-process`（不稳定，一崩全崩）
- 不把 Chromium 命令行开关当主路径（微软明确不保证生产可用）；`--renderer-process-limit`、`--enable-low-end-device-mode`、`msWebView2SimulateMemoryPressureWhenInactive` 只作为"实验性"开关，默认关闭，出问题可一键复位
- 不对 `IsDocumentPlayingAudio` 为真的标签做任何降级
- 不为了省内存去 `disable-gpu`（会让滚动变卡，GPU 进程那 60MB 不值得）

### 4.9 内存悬浮面板与一键释放（P0-17）

**状态：M2 已实现**（`MemoryPanel.cs`），实测截图为 `out/m2-panel.png`（暗）/ `m2-panel-light.png`（亮）。

**结论：设想合理，纳入 P0。** 成本约 2–3 天，且直接强化"内存可验证"这个产品核心。顶部 `62MB` 徽标悬停 300ms 展开，点击可钉住，Esc 收起。

**可行性依据（已核实，SDK 1.0.4078.44 均为稳定 API）**

| 能力 | API |
|---|---|
| 拿到全部子进程及类型 | `CoreWebView2Environment.GetProcessInfos()` → `ProcessId` + `Kind`（Browser / Renderer / GPU / Utility…） |
| 拿到 renderer 里的 frame 清单 | `CoreWebView2ProcessExtendedInfo.AssociatedFrameInfos` → `IReadOnlyList<CoreWebView2FrameInfo>` |
| 把 frame 对上标签 | `CoreWebView2.FrameId`、`CoreWebView2Frame.FrameId` 与 `CoreWebView2FrameInfo.FrameId` 是同一套 ID；`FrameInfo.Source` 可作兜底匹配 |
| 内存数值 | `Process.PrivateMemorySize64` 为主（WorkingSet 会把共享 DLL 重复计算），WorkingSet 仅作参考列 |

> 环境级取 ExtendedInfo 的方法名（`GetProcessInfosWithDetails` 的 .NET 投影）在 Phase 0 对照锁定的 SDK 版本确认，不凭记忆写死。

**面板内容（纯 WPF Popup，零 WebView2 开销）**

```
┌ NanoEdge · 内存 ──────────────────────┐
│ 进程组 462MB   应用自身 68MB           │
│ 工作集下降 = 物理页被换出，提交不变     │
│ ── 公共开销 ───────────────────────  │
│ Browser 78MB · GPU 62MB · Utility 34MB│
│ ── 标签页 ─────────────────────────  │
│ ● pgAdmin      94MB  活跃      [常驻] │
│   提交 120                            │
│ ● eck 控制台   11MB  降冷      [常驻] │
│ ● dev-api      —     已丢弃           │
│ ───────────────────────────────────  │
│      [ 释放后台 · 约 180MB ]          │
└──────────────────────────────────────┘
```

- 点某行切到该标签；右键"立即释放此标签"；`[常驻]` 开关直接决定该标签是否参与自动丢弃（chip 默认隐藏，悬停该行才出现）
- 状态色点：活跃 / 降冷 / 已丢弃三态，与 4.1 一致
- 每行同时显示**工作集 / 私有提交**两个数；顶部固定一行说明"工作集下降 = 物理页被换出，提交不变"，不把降冷说成"释放内存"
- 密封标签在面板里同样伪装（标题"文档"、无 favicon）

**"一键释放"的真实语义（不夸大）**

| | 说明 |
|---|---|
| 能做到 | 把所有非豁免后台标签直接推到 `Discarded`，renderer 进程消失，每标签的工作集归零；同时清 WPF 的 bitmap/资源缓存 |
| 实测补充 | Phase 0 把 5 个标签全部 Dispose 后，**整个 WebView2 进程组退出**（进程数 10 → 1，私有提交 358.6 → 58.8MB，工作集 86.5MB）。所以"一键释放"在后台全丢时是真能归零的，不是心理安慰 |
| 次要手段 | `EmptyWorkingSet` 只把物理页换出到页面文件，**不减少提交内存**，切回会卡一下 → 默认不做，仅放高级设置 |
| 做不到 | 只要还有任意标签存在，就不能单独干掉 browser / GPU 进程；不能让任务管理器"提交大小"下降（下降的是工作集）；不能释放当前活动标签 |
| 命名 | **"释放后台标签内存"**，附预估可省量；不叫"一键加速/清理"，不放进度条或数字动画（会误导成"擦除内存"） |

保护规则：活动标签、正在播放音频、固定标签、保活密封标签、常驻白名单，一律跳过。点击后 Toast 显示"已释放 6 个后台标签"，不弹确认框——点标签即可重载恢复，是可逆操作；首次点击给一次可关闭的说明。

**三个必须处理的坑**

1. **按标签归因实测是精确的**：Phase 0 用 `GetProcessExtendedInfosAsync()` 拿到 5 个 renderer，每个恰好 1 个 `MainFrame`，`FrameId` 与 `CoreWebView2.FrameId` 一一对应（1–5）；即使 3 个标签同域（`nano-a.local`），**也没有共享 renderer**。所以 v1.0 担心的"同源共享需均摊"在 WebView2 多控件模型下不成立。仍保留防御：若某天出现一个 PID 对应多个 frame（跨源 iframe、站点隔离），就合并成一行或均摊并标注"共享"，不伪装精确值。
2. **隐私泄露**：0.15 起不做防截屏，密封标签只靠伪装（标题「文档」、地址栏空、面板标题走 `DisplayTitle`）。屏幕共享/录屏仍能看到页面内容——这是明确接受的边界，不要按旧文档去设 display affinity。
3. **采样成本**：只在面板打开时 1s 采样一次，关闭即停；不常驻轮询（内存治理本身 10s 一次已足够）。取 `AssociatedFrameInfos` 是异步快照，不要在 UI 线程等。

**M2 落地细节（与上文的差异）**

- 徽标显示的是**进程组工作集**（含外壳），> 档位阈值变琥珀色；数字由治理器的 10s 采样刷新，面板打开时切到 1s。
- 每行两列数字：主 `工作集`、次 `提交`；没有 renderer 的标签（起始页 / 已丢弃）显示 `—`，不假装 0MB 是"省下来的"。
- `[常驻]` chip 默认隐藏，悬停该行才出现（否则整列"常驻"会被误读成所有标签都已常驻）；自动豁免的标签 chip 置灰并给出原因 tooltip。
- 「释放后台」按钮文案带预估量（`释放后台 · 约 19MB`），点击后 toast 报**实测进程组差值**，不报预估；不做进度条、不叫"清理/加速"。
- 面板开着时点某行会切标签并收起面板；右键行可"立即释放此标签"。
- 当前标签永不参与释放；「释放后台」跳过所有豁免标签。

---

## 5. UI 与布局规范

### 5.1 三种布局

| 模式 | 结构 | 适用 |
|---|---|---|
| **紧凑单行**（默认） | 一行 36px：导航按钮 + 标签条 + 地址栏 + 右侧按钮 | 宽度 ≥ 1100px |
| **标准两行** | 标签行 34px + 工具栏 40px | 760–1100px，或用户手动选 |
| **垂直标签** | 左侧 44px 窄条（悬停展开到 220px）+ 顶部 36px 工具栏 | 宽度 ≥ 1200px，或用户手动选 |

自动建议：窗口宽度跨过断点时提示一次"是否切换到垂直标签？"，不自动改，避免布局跳变。

### 5.2 紧凑单行（对标 Helium CAT）

```
┌────────────────────────────────────────────────────────────────────────────┐
│ ☰ │ ← → ⟳ │ ┌tab┐┌tab┐┌tab┐ │ [ 🔒 dev.local:8080/api ] │ ⧉ │ 62MB │ ⋯ │ ─ □ × │
└────────────────────────────────────────────────────────────────────────────┘
   32   90      标签条 flex        地址栏 flex                  30   44    30   120
                最小 160 / 最大 45%  最小 240
```

- 行高 **36px**，按钮命中区 30×30，标签 30px 高、圆角 6；
- 标签宽度 110–220px，超出后横向滚动（滚轮可切）；
- 地址栏获得焦点时**浮层展开**为 520px 宽的下拉（Popup），不挤压标签；
- 窗口 < 900px 时标签条自动折叠成"▾ 5 个标签"下拉；
- 右侧**内存徽标**（如 `62MB`）悬停 300ms 展开 4.9 的明细面板，点击钉住；超阈值变琥珀色。

### 5.3 垂直标签

| 元素 | 收起 | 展开 |
|---|---|---|
| 栏宽 | 44px | 220px |
| 标签项 | 30px 高，只显示 favicon | 30px 高，favicon + 标题 + 关闭 |
| 分组头 | 26px，只显示色点 | 26px，色点 + 名称 + 折叠箭头 + 数量 |
| 顶部 | 新建 + 收起按钮 30px | 同左，带文字 |

**悬浮展开时序（对标 Edge）**：

```
鼠标进入窄条 150ms → 弹出 220px 覆盖层（Popup，独立窗口，不重排页面）
鼠标离开覆盖层 400ms → 收回
覆盖层内有菜单打开 / 正在拖拽 → 不收回
窗口失焦 → 立即收回
```

为什么用 Popup：Edge 的悬浮展开是"覆盖不重排"，而覆盖 WebView2 只能靠独立 HWND。代价是要处理焦点与拖拽，Phase 0 验证。

备选：`推开内容`（改变 WebView 宽度，无 airspace 问题，但页面会重排）作为设置项保留，给不介意重排的人。

### 5.4 视觉规范

**暗色（默认）**

| Token | 值 | 用途 |
|---|---|---|
| `Canvas` | `#0E1116` | 窗口底 |
| `Chrome` | `#141922` | 标题栏/工具栏 |
| `Surface` | `#1A202B` | 激活标签、卡片 |
| `SurfaceHover` | `#202836` | 悬停 |
| `Line` | `#232B39` | 分隔线 |
| `Text` | `#E6EAF2` | 主文本 |
| `Muted` | `#98A2B3` | 次要文本 |
| `Accent` | `#FF4D9D` | 焦点、进度、选中指示（取渐变的品红段） |
| `AccentSoft` | `rgba(255,77,157,.14)` | 选中底 |
| `BrandGradient` | `#4F6FD8 → #8B57D6 → #DB5396 → #E38F4A`（自上而下，v3 微降饱和） | 仅字身与品牌位，不进 UI 控件 |
| `BrandSky` | `#6A5585 → #9C6A90 → #CE8497 → #EBA683 → #F4C68E`（自上而下，v4 折中值） | 图标底板的纯净余晖渐变，无任何装饰元素 |
| `Danger` | `#F87171` | 关闭悬停、错误 |

**亮色**：`Canvas #F3F5F9`、`Chrome #FFFFFF`、`Surface #EEF1F6`、`Line #DDE3EC`、`Text #1B2230`、`Muted #5B6678`、`Accent #D61F76`。

> 品红做主色偏张扬。若日常觉得刺眼，把 UI 主色降到 `#E23C82` 即可，图标仍保留完整渐变。

其他：字体 `Segoe UI Variable Text` → `Segoe UI` → `Microsoft YaHei UI`；基准字号 12.5px；圆角 6（控件）/ 10（卡片）；图标 16px 线性。

### 5.5 图标（结构与色序已定，字形迭代中）

配色依据：GTA VI 2023 年首支预告的 VI 标志，渐变自上而下为 **蓝 → 紫 → 品红 → 橙**（日落色）；2026 年官方封面把外框改成白色，主体仍是粉蓝渐变配深紫底。所以采用四段色序。

**仍然有效的规范**（与迭代无关）：

- 画布 88×88 视框、圆角 20；
- 字身渐变方向恒为**自上而下**，色序恒为蓝 → 紫 → 品红 → 橙（具体色值见 5.5.3）；
- 居中判据恒为「墨迹 alpha 重心与底板几何中心 `(44,44)` 重合，偏差 ≤1px」；
- 输出 `.ico` 含 16/20/24/32/48/64/128/256 八档，全部手工对齐像素网格；另存一份 SVG 源文件。

> 早期 C 稿的「折角 N + -10° 倾斜 + 深紫黑底 + 16px 单色剪影」、v2 稿的「miter 尖角 + 浅色底」、v3 稿的「过暗高饱和底 + 过宽描边」**均已废弃**。当前稿以 **5.5.4 / `assets/nanoedge-icon-v4.svg`** 为准。

#### 5.5.1 图标迭代方向（用户反馈，方向已按 v4 定稿，不阻塞开发）

最初 C 稿只解决"形状 + 居中 + 色序"，**字形风格没对**。用户分两轮给出最终口径：

| 维度 | 最初方向 | 最终方向（用户口径） |
|---|---|---|
| 字形 | 折线 + `miter` 尖角 | **标准大写字母体，不倾斜，不要菱形尖刺，类 GTA6 LOGO 的粗细**；改用填充多边形 |
| 描边 | 无 | 由外到内三层：**黑色外描边 → 白色内边 → 渐变填充** |
| 填充 | 四段渐变直接上色 | 顺序不变（蓝 → 紫 → 品红 → 橙，自上而下），**饱和度只降一点点**（约 10%） |
| 底板 | 低饱和、偏浅的余晖渐变 | **亮度低一些、饱和度高一些**，接近 GTA6 官网背景那种落日余晖 |
| 16px | 单色剪影 | 保留黑白两层轮廓，不简化成单色 |

UI 主色 `#FF4D9D` 不变（控件不跟着图标改）。图标迭代与代码解耦，任何阶段都能单独重做。

> 第二轮（v4）追加的修正：上表「底板」一行是相对 v2 的口径，v3 执行时压得过暗、饱和过高，v4 已在两者间折中；描边宽度也在 v4 收细，字形放大，直角改为不倒角。详见 5.5.4。

#### 5.5.2 图标 v2 设计稿（已废弃，被 v3 取代）

> 仅保留迭代记录，**不要按此实现**。被否的三点：① `miter` 尖角在转折处鼓成菱形，不像标准大写字母；② 底板太浅、饱和度太低；③ 字身渐变过艳（用户要求只降一点点饱和，不是换风格）。

按 5.5.1 出的第一版，取代 5.5 的细线 C 稿几何；居中原则不变。

| 维度 | v2 做法 |
|---|---|
| 字形 | 单条折线 N，局部坐标 `(-12,16) → (-12,-16) → (12,16) → (12,-16)`；`miter` 尖角 + `butt` 平头，无圆角，笔画粗 20 |
| 三层描边 | 同一路径画三遍同心描边：黑 `#101018` 宽 20 → 白 `#FFFFFF` 宽 14 → 渐变宽 10（`#3B6BFF → #8B3DFF → #FF2E93 → #FF8A2B`，自上而下） |
| 底板 | 88×88 圆角 20，低饱和浅色余晖渐变 `#DBE4F0 → #E8DEEA → #F3E1DA → #F7E9D2`；底部 3 条半透明细横线作落日余晖（纯色，不用模糊/发光） |
| 居中 | 折线关于原点中心对称，同心描边保持对称 → alpha 重心天然落在底板中心 `(44,44)`，偏差 ≤1px；墨迹约 44×52 |
| 小尺寸 | 32 / 24 / 16 同比例缩放三层；16px 仍保留黑白两层轮廓，不再退化成单色剪影 |
| 输出 | SVG 源文件 + 8 档 ico，不变 |

**待你确认的三点**：

1. 底板由深紫黑改成浅色后，任务栏 / 深色主题下图标会明显偏亮，是否接受（或给深色主题单独配一版底板）；
2. 字身要不要再加 -6° 倾斜更贴近 GTA 字标——会破坏中心对称，需要按 alpha 重心重新校准居中；
3. 16px 下黑边约 1.8px 是否够清晰，必要时 16/20 档单独加粗。

UI 主色 `#FF4D9D` 不变。

#### 5.5.3 图标 v3 设计稿（中间稿，已被 v4 取代）

> 标准大写 N 的**字形**在 v3 已经定下来并被用户认可，v4 完整沿用。这里只保留 v3 相对 v2 的字形改进记录；其「过暗高饱和背景、过宽描边、放大不足」三点已在 v4 修正。

矢量源文件：`assets/nanoedge-icon-v3.svg`。

| 维度 | v3 做法 |
|---|---|
| 字形 | **单个填充多边形**画标准大写 N，不倾斜。局部路径 `M-19,-22 L-5,-22 L5,8 L5,-22 L19,-22 L19,22 L5,22 L-5,-8 L-5,22 L-19,22 Z`；竖干宽 14 / 总宽 38（≈37%，接近 GTA6 粗体），斜笔画同宽 |
| 转角 | `stroke-linejoin="bevel"` 平切角，**不用 `miter`**——v2 的菱形尖刺就是 miter 在折线转折处外扩造成的 |
| 三层 | 同一路径叠三遍：黑 `#0C0A14`（fill + stroke 14）→ 白 `#FFFFFF`（fill + stroke 6）→ 渐变 fill（无 stroke）。最终黑环约 4px、白环约 3px、字身为渐变 |
| 字身渐变 | 自上而下 `#4F6FD8 → #8B57D6 → #DB5396 → #E38F4A`。相对 v2 的 `#3B6BFF / #8B3DFF / #FF2E93 / #FF8A2B` 约降 10% 饱和、微降亮度，**只调一点点** |
| 底板 | 88×88 圆角 20，暗调高饱和落日余晖：`#1B1038 → #5A1E62 → #A82C74 → #E3554A → #F59A3C`（自上而下）。整体亮度显著低于 v2 的浅色底 |
| 余晖细节 | 中下部一个 `#FFC978` 圆（opacity 0.28）+ 3 条深色半透明横线作复古落日切割；纯色扁平，不用模糊/发光/阴影 |
| 居中 | 该多边形关于原点**中心对称**，三层同心描边不破坏对称 → 几何中心即 alpha 重心，天然落在 `(44,44)`，偏差 ≤1px |
| 小尺寸 | 32 / 24 / 16 同比例缩放三层；16px 仍保留黑白两层轮廓 |
| 输出 | SVG 源文件 + 8 档 ico，不变 |

**当时的待确认项已在 v4 关闭**：暗底在深色任务栏是否糊成一片（v4 提亮背景后不再有此顾虑）；16px 描边清晰度（v4 收细后实渲染 16px 仍可辨 N）。

UI 主色 `#FF4D9D` 不变。

#### 5.5.4 图标 v4 设计稿（2026-11-04，已验收定稿）

矢量源文件：`assets/nanoedge-icon-v4.svg`；实渲染预览：`assets/icon-v4-preview.png`（256 / 64 / 32 / 16 四档，深色底）。已用 Edge 无头截图目视确认。

**包装（0.16 已完成）**：`assets/nanoedge.ico` 含 16/32/48/64/256 五档（PNG 帧），由 `assets/_icon-render.html` 按目标尺寸逐档渲染后打包；`ApplicationIcon` 进 PE 资源、`EmbeddedResource` 供 WPF 窗口图标。重新生成方式见 0.16 判断 1。

用户四点反馈与对应修正：

| 反馈 | v3 问题 | v4 做法 |
|---|---|---|
| 背景饱和度过高、亮度过暗，折中 | `#1B1038 → #F59A3C`，顶部近黑、色很纯 | `#6A5585 → #9C6A90 → #CE8497 → #EBA683 → #F4C68E`，亮度与饱和度都取 v2（过浅低饱和）与 v3（过暗高饱和）的中值 |
| 字形可以 | 标准大写 N 填充多边形 | **完全沿用**，路径不变，不倾斜 |
| 整体尺寸小了些，放大一点点 | 字身占底板约 43% | 整组 `scale(1.18)`，字身约占 51%，墨迹外缘仍留 12px 安全边距，不贴圆角 |
| 黑白描边过宽、细一些 | 黑环 4px / 白环 3px（stroke 14 / 6） | 黑层 stroke 6、白层 stroke 2.5 → **黑环约 2px（不变）/ 白环约 1.5px（比黑环细一点点）** |
| 直角位置不做倒角 | `bevel` 把四个外直角切成斜角 | 改 `stroke-linejoin="miter"` + **`stroke-miterlimit="2"`** |

**`miterlimit=2` 是这一版的关键**：直角（90°）的 miter 比例 1.414 < 2 → 保持尖角，不倒角；斜带上下端的锐角比例 > 2 → 自动退化为极小平切，不会拉出 v2 那种长菱形尖刺。第一版 v4 用 `miterlimit=10` 时上下各冒出一根尖刺，实渲染发现后改为 2 消除。

**最终微调（用户验收 95 分后的两点）**：

1. **白边比黑边细一点点，黑边粗细不变**：可见黑环宽度 = `(B - W) / 2 × 1.18`，可见白环 = `W / 2 × 1.18`。要让白环变细而黑环不动，两个 stroke 数值必须一起降：`B 8→6`、`W 4.5→2.5`，得黑环仍 2.065px、白环 1.475px。若只改白层，黑环会反而变粗。
2. **去掉 N 下方两条浅灰横条，只留纯粹字母**：落日圆盘和三条切割横线**一并移除**（横线本就是用来切割圆盘的，单独留圆盘会变成突兀色块）。现在背景只有纯净渐变。

其余细节：

- 三层叠放顺序不变：黑 `#0C0A14` → 白 `#FFFFFF` → 渐变字身；
- 字身渐变沿用 v3 的 `#4F6FD8 → #8B57D6 → #DB5396 → #E38F4A`（用户认可，不再调整）；
- 居中仍由多边形中心对称保证，重心落在 `(44,44)`；
- 输出规格不变：SVG 源文件 + 8 档 ico。

UI 主色 `#FF4D9D` 不变。**图标已验收通过，定稿。**

### 5.6 快捷键（P0）

| 键 | 功能 |
|---|---|
| `Ctrl+T` / `Ctrl+W` | 新建 / 关闭标签 |
| `Ctrl+Shift+T` | 恢复最近关闭 |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | 下一个 / 上一个标签 |
| `Ctrl+1..8` / `Ctrl+9` | 第 N 个 / 最后一个 |
| `Ctrl+L` / `Alt+D` | 聚焦地址栏 |
| `Alt+←` / `Alt+→` | 后退 / 前进 |
| `F5` / `Ctrl+F5` | 刷新 / 硬刷新 |
| `Ctrl+F` | 页内查找（用 WebView2 内置查找条，不自研 UI） |
| `Ctrl+0` / `+` / `-` | 缩放重置（一律 100%）/ 放大 / 缩小（M3-c 已实现，按站点记忆） |
| `F12` | DevTools |
| `Ctrl+D` | 收藏（M3-b 已实现） |
| `Ctrl+H` | 历史（M3-b 已实现） |
| `Ctrl+Shift+O` | 书签（M3-b 已实现） |
| `Ctrl+J` | 下载列表 `nano://downloads`（M3-c 已实现；新开标签，已有则切过去） |
| `Ctrl+Shift+A` | 标签搜索（P1，未实现） |
| `Alt+\`` | **密封标签：切走 / 恢复**（应用内，见 0.15） |
| `Ctrl+Shift+M` | 内存面板（**0.18 已实现**） |
| `Ctrl+Shift+L` | 布局轮换（0.18 从 `Ctrl+Shift+M` 让位；`Ctrl+L` 仍是聚焦地址栏） |
| `Ctrl+F5` | 硬刷新（**0.18 已实现**，走 CDP `ignoreCache`） |
| 鼠标中键点击标签 | 关闭该标签（**0.18 已实现**） |

> 0.18 补齐状态：`Ctrl+Shift+T`（恢复最近关闭）、`Ctrl+Shift+M`、`Ctrl+F5`、中键关闭均已落地；仅 `Ctrl+Shift+A` 仍属 P1。

---

## 6. 功能清单与优先级

### P0 — MVP（缺一个就不能当主力）

| # | 功能 | 为什么必须 | 成本 |
|---|---|---|---|
| 1 | 多标签 + 会话恢复 | 基本盘 | 中 |
| 2 | 地址栏：URL/搜索/历史联想/关键词搜索（`b `、`gh `、`npm `） | 每天用几百次 | 中 |
| 3 | 前进/后退/刷新/停止 + 上面那套快捷键 | 基本盘 | 低 |
| 4 | 紧凑单行 + 垂直标签 + 分组折叠 + 悬浮展开 | 你的核心布局诉求 | 高 |
| 5 | 三级内存治理 + 内存面板 | 产品的存在理由 | 高 |
| 6 | 脚本注入引擎 + 管理界面 **M4 已实现** | 替代油猴 | 高 |
| 7 | 密封标签：伪装 + `Alt+\`` 切换 + 通知开关 **M5 已完成（0.15，简化版）** | 你的隐私诉求 | 中 |
| 8 | 书签（含从 Edge/Chrome 导入 HTML） | 迁移成本 | 中 |
| 9 | 历史记录 | 地址栏联想依赖它 | 低 |
| 10 | 下载（进度、打开文件夹、取消）**M3-c 已实现** | 基本盘 | 低 |
| 11 | 缩放（按站点记忆）/ DevTools（页内查找用 WebView2 内置）**缩放 M3-c 已实现** | 开发必需 | 低 |
| 12 | 暗色/亮色 + 跟随系统 | 明确要求 | 低 |
| 13 | 中/英双语 **M6-b 已完成** | 明确要求 | 中 |
| 14 | 设置界面（WPF `nano://settings`）**M6-b 已完成** | 参数太多，必须可调 | 中 |
| 15 | 单实例 + 外部唤起 + 崩溃恢复 **M6-a 已完成** | 日常摩擦点 | 低 |
| 16 | 起始页（搜索框 + 自定义快捷访问） | 明确要求；纯 WPF，不建 WebView2，顺带把空载基线压到最低 | 低 |
| 17 | 内存悬浮面板 + 一键释放后台 | 让"省内存"从承诺变成可验证的数字；API 已核实可用 | 中 |

> **0.18 补齐的 P0 欠账**：`Ctrl+Shift+T` 恢复最近关闭（5.6 早已列为 P0）、中键关闭 / 关闭右侧 / 复制标签、favicon（5.3/7.8）、起始页快捷方式增删改（7.8）、`Ctrl+Shift+M`、`Ctrl+F5`。这些不是新功能，是把 SPEC 已承诺但没写的东西补上；P1/P2 一项未动。

### P1 — 第二版

| # | 功能 | 说明 |
|---|---|---|
| 1 | 阅读模式 | JS 提取正文 + 本地 CSS |
| 2 | 网页强制暗色 | `Emulation.setAutoDarkModeOverride` via CDP |
| 3 | 每站点自定义 CSS | 比脚本更轻的"改样式" |
| 4 | 截图（可视区 / 整页） | 整页用 CDP `captureBeyondViewport` |
| 5 | 打印 / 另存 PDF | WebView2 原生 API |
| 6 | 标签搜索面板（Ctrl+Shift+A） | 模糊匹配标题/URL |
| 7 | 快捷键自定义 | 录制式 |
| 8 | 便携模式 | `portable.flag` → 数据落到 exe 同级 `Data\` |
| 9 | 代理设置 | 系统代理 / 手动 |
| 10 | 设为默认浏览器 **M6-a 已完成（P0-15 顺带）** | 显式按钮，写 HKCU，无提醒 |

### P2 — 看心情

| # | 功能 | 备注 |
|---|---|---|
| 1 | 分屏 | 两个 WebView 并排 |
| 2 | 命令面板（Ctrl+K） | 地址栏已覆盖大半场景 |
| 3 | 网页翻译 | 依赖外部服务 |
| 4 | 更新检查 | 打开 Releases 页即可 |
| 5 | 标签悬停预览 | 截当前页缩略图，注意内存 |
| 6 | 下载增强 | 暂停/续传/限速 |

---

## 7. 关键子系统设计

### 7.1 标签宿主（最高风险项）

**问题**：WebView2 基于 `HwndHost`，从视觉树摘下再挂回会销毁 HWND，`CoreWebView2` 可能失效。ZZZ 的做法是切换标签时替换 `ContentControl.Content`，并在标签休眠时整个 Dispose。

**方案**：所有标签的视图**常驻同一个 Grid**，用 `Visibility` 切换：

```xml
<Grid x:Name="TabHost">
  <!-- 每个 tab 一个 BrowserTabView，全部是子元素 -->
</Grid>
```

- 切走：`view.Visibility = Collapsed`，再 `TrySuspendAsync()`；
- 切回：`Visibility = Visible`，`Resume()`；
- 只对已丢弃的标签才真正 `Dispose` 并移除子元素。

**Phase 0 必须验证**：`Collapsed` 之后 `TrySuspendAsync()` 是否成功（`IsSuspended` 是否为 true）。若失败，退路是显式把 `CoreWebView2Controller.IsVisible` 置 false —— 需要拿到 controller，可能要改用 WinForms 宿主或 `CoreWebView2CompositionController`。

### 7.2 地址栏（**M3-a 已实现**）

解析顺序（`UrlParser`）：

1. `nano://` / `about:` / `file:` → 原样处理（内部页在 M3-b 落地）
2. 命中关键词前缀（`b` / `g` / `bd` / `gh` / `npm` / `so` + 空格）→ 对应搜索引擎
3. `http(s)://` → 直接导航
4. `localhost` / `127.0.0.1` / `192.168.*` / `10.*` / `[` → 补 `http://`
5. 无空格且含 `.` → 补 `https://`
6. 其余 → 搜索

输入清理：去掉首尾空白、包裹的引号（`"` `'` `“”` `‘’`）、零宽字符。

**联想下拉**（Popup，锚定地址栏外框，宽度 = `min(820, 地址栏宽)`，最多 8 行，键盘焦点留在输入框）

| 来源 | 记号 | 加权 |
|---|---|---|
| 直接打开（输入本身像 URL） | `↵` | 固定置顶（1000） |
| 已开标签 | `▣` | +30 |
| 书签 | `★`（品牌粉） | +25 |
| 历史 | `◷` | 访问次数 log 加权（上限 +20）、7 天内 +15 |

排序：URL 前缀 +120 / 域名前缀 +100 / URL 含 +50；标题前缀 +90 / 标题含 +40；同一 URL 只出现一次。

键盘：`↑↓` 选择、`Enter` 打开、`Alt+Enter` 新标签、`Tab` 补全到地址栏、`Esc` 关闭、`Delete` 删除该条历史；`Ctrl+Enter` 把 `github` 补成 `github.com`。

**存储**：`%APPDATA%\NanoEdge\history.json` / `bookmarks.json`。历史按 URL 去重、5000 条 / 90 天封顶、3s 节流落盘、退出 `Flush`；重定向造成 2s 内同址重复导航不重复计数。

导航失败不自己造错误页：保留 WebView2 内置错误页，只在底部弹 2.6s Toast「无法打开 <host> · 重试」。

### 7.3 脚本注入引擎

**状态：M4 已实现**（`UserScripts.cs` / `ScriptEngine.cs` / `ScriptsPage.cs`），实现判断、实测与偏离见 **0.14**；0.19（M8）补齐 `@require` / `GM_listValues` / `GM_setClipboard` / `unsafeWindow`。下文为设计原文，实现细节以 0.14、0.19 为准。

**元数据解析**（兼容 Tampermonkey 子集）：

```
// @name         My Script
// @namespace    local.myscript
// @version      1.0
// @match        https://*.example.com/*
// @include      /^https?:\/\/localhost:\d+\//
// @exclude      https://example.com/admin/*
// @run-at       document-start | document-end | document-idle
// @grant        GM_setValue
// @grant        GM_listValues
// @grant        GM_setClipboard
// @connect      api.example.com
// @require      https://cdn.example.com/lib.js
```

**注入**：

- 每个匹配脚本在 `AddScriptToExecuteOnDocumentCreatedAsync` 里注入一个引导器；
- `document-start` 立即执行；`document-end` 挂 `DOMContentLoaded`；`document-idle` 挂 `requestIdleCallback`；
- 匹配用 `URL` 通配转换成正则，`@include` 支持正则字面量；
- 每个脚本包在 `try/catch` + 独立作用域里，异常写日志，不打断其他脚本；
- `@require` 正文按头部顺序拼在主脚本之前、同一函数作用域；正文在导入/保存时下载并固化，运行期不联网（0.19）。

**GM API（P0 子集 + 0.19 补齐）**：

| API | 实现 |
|---|---|
| `GM_setValue/getValue/deleteValue` | 宿主侧按 `namespace + key` 存 JSON，`WebMessage` 桥 |
| `GM_listValues` | 返回本命名空间键数组；本页快照 + 本页写入，跨标签的新写入要刷新后才可见 |
| `GM_addStyle` | 注入 `<style>` |
| `GM_notification` | 走统一通知服务 |
| `GM_openInTab` | 宿主开新标签 |
| `GM_setClipboard` | 宿主写 WPF 剪贴板（纯文本）；必须声明 `@grant`，页面伪造消息会被拒绝 |
| `GM_xmlhttpRequest` | 宿主代发（带当前会话 Cookie），需 `@connect` 白名单 |
| `GM_info` | 脚本元数据 |
| `unsafeWindow` | 等同页面 `window`。注入在页面主世界，**不声明 `@grant` 也可用**，只是兼容别名 |

**权限模型**：`@grant` 必须显式声明；未声明的 API 在 JS 侧和宿主侧**双重拒绝**（ZZZ 的做法，值得照搬）。导入脚本时展示它将获得的权限与 `@require` 地址，需确认。

**诚实边界（0.19）**：桥消息来自页面主世界，页面本身可以伪造 `__ne` 消息；宿主只按脚本 `@grant` 校验，无法区分「脚本发的」与「页面伪造的」。这与既有 `GM_setValue` / `GM_xmlhttpRequest` 的信任模型一致，不是 0.19 新增的问题；新增的剪贴板写入同样受 `@grant` 拦截（已实测）。

**存储**：`%APPDATA%\NanoEdge\scripts\<id>.user.js` + `<id>.require.<n>.js` + `scripts.json` 索引 + `script-values.json`。

**管理界面**：列表（启用开关、匹配站点、最后运行时间）、编辑器（等宽字体 + 行号，先不做语法高亮）、从文件/剪贴板/URL 导入、当前站点"临时禁用全部脚本"开关。

### 7.4 密封标签（M5 简化版，已实现）

**用户口径（2026-11-04）**：不要独立 Profile、防截屏、DPAPI、通知重绘桥，只保留三件事——**伪装、`Alt+\`` 切换、通知开关**。下表是实际落地的行为，原设计中被砍掉的部分列在后面备查。

| 维度 | 做法 |
|---|---|
| 入口 | 主菜单「新建密封标签」，不新增创建快捷键 |
| 伪装 | 标签标题固定「文档」（`TabModel.DisplayTitle`）、标签 tooltip 不显示 URL、地址栏留空、不进历史、不进会话恢复、不进地址栏联想、不能收藏 |
| 切换 | `Alt+\`` 在密封标签与「上一个普通标签」之间来回切；没有密封标签时 Toast 提示。走应用内消息泵，**不是 `RegisterHotKey` 全局热键**：窗口不在前台时不生效 |
| 通知 | 设置项 `SecretNotifications`（默认关）：关闭时对密封标签的 `Notification` 权限请求直接 `Deny + Handled`，打开时 `Allow`；主菜单里可切换 |
| 不留痕 | 缩放不写 `zoom.json`、丢弃快照不落盘、下载记录不写站点 host、导航日志写 `(secret)` 而不是 URL、导航失败 Toast 不显示 host |
| 多标签 | 可以开多个密封标签；`Alt+\`` 切到最近创建的那个，切回时回到进入前的普通标签 |
| 边界 | 不做独立 Profile（Cookie / 缓存与主浏览器共享）、不做防截屏（录屏和截图能看到页面）、不做静态加密。同机同账户的其他程序能看到浏览数据，不夸大 |

**原 7.4 中被砍掉的部分（0.15 决策，不要按旧文档实现）**：独立 `CoreWebView2Profile("secret")`、`WDA_EXCLUDEFROMCAPTURE` 防截屏 + Popup display affinity、`secrets.dat` DPAPI、`ClearBrowsingDataAsync` 清理、`NotificationReceived` 拦截后自绘 Toast、诱饵页 `nano://blank`、全局热键。

**替代关系**：需要彻底隔离时另开 Windows 用户 / 虚拟机；NanoEdge 的密封标签只解决"临时走开时屏幕上别露馅"，不是安全边界。

### 7.5 会话与崩溃恢复

**状态：崩溃恢复已在 M6-a（0.16）实现**：渲染进程崩溃自动重载一次（20s 冷却），无响应给「重新加载」，`BrowserProcessExited` 重建全部视图。**"连续两次启动失败进安全模式"未实现**（M6-b 候选）。

- `session.json`：标签列表（url/title/groupId/pinned/active）、分组、窗口位置、活动标签索引；
- 原子写：写 `session.json.tmp` → `File.Replace`；
- 每 30s 或标签变动时落盘；
- 启动时恢复标签**元数据**，只为活动标签创建 WebView2（其余点开才加载 → 启动快）；
- `ProcessFailed` 事件：渲染进程退出自动重载一次，同一标签 20s 内不重复；浏览器进程退出则拆掉所有视图、重建环境、重载活动标签；
- 连续两次启动失败 → 提供"安全模式"（禁用所有脚本启动）——**未实现**。

### 7.6 设置与数据

```
%APPDATA%\NanoEdge\
  settings.json          用户设置（schemaVersion 做迁移）
  bookmarks.json
  history.json           滚动写入 + 定期压缩
  zoom.json              按站点缩放（M3-c）
  downloads.json         下载记录，200 条封顶（M3-c）
  session.json
  scripts\
  script-values.json
  secrets.dat            （未实现：0.15 砍掉 DPAPI 静态存储）
%LOCALAPPDATA%\NanoEdge\
  WebView2\              UserDataFolder（缓存、Cookie、LocalStorage）
  native\<ver>\          WebView2Loader.dll 释放目录
  favicons\
  logs\
```

设置分区（M6-b 实际落地，见 0.17）：`外观（主题/语言）/ 布局（布局/固定展开/状态栏）/ 内存（三档 + 阈值）/ 隐私（密封标签通知）/ 关于（版本/数据目录/默认浏览器）`。脚本管理仍在 `nano://scripts`，不重复放进设置页。

**AppData 模式 vs 便携模式（Q5）**

| 维度 | AppData（默认） | 便携（`portable.flag`） |
|---|---|---|
| 数据位置 | 设置 `%APPDATA%\NanoEdge`，缓存/Profile `%LOCALAPPDATA%\NanoEdge` | 全部在 `exe同级\Data\` |
| exe 能放哪 | 任意目录（含只读的 Program Files） | 必须可写（U盘、D盘工具目录） |
| 换电脑 | 数据留在本机，除非域账户漫游 | 整个文件夹拷走，数据跟着走 |
| 多份配置 | 一台机器一份 | 每个文件夹一份，可并存 |
| 风险 | 无 | exe 目录被移动/改名后，绝对路径类缓存（favicon 索引）需要重建 |

结论：按你最初的要求，**默认 AppData**；便携模式不提供 GUI 开关，只在 exe 同级放一个空文件 `portable.flag` 就切换，设置页只读显示当前数据根目录 + "打开数据目录"按钮。不做自动迁移（迁移 = 手动拷贝，避免路径半迁移的坑）。

### 7.7 多语言（**M6-b 已实现**，0.17）

- UI 全部由 C# 构建、没有 XAML，所以**不走** `ResourceDictionary` + `DynamicResource`（那需要再造一套绑定）。
- `Locale.cs`：`L.T("中文原文")` 查字典，306 条英文；缺词回退中文，不会出现空白/key。
- `AppSettings.Language`（zh-CN / en-US）持久化；`--lang=` 可启动覆盖；所有 `--mXtest` 强制中文，保证断言稳定。
- 切换语言时重建工具栏、标签条、内部页/起始页视图与标签标题；**WebView2 不重载**。
- 新增语言 = 在 `Locale.cs` 的字典里补一列，调用点不需要改。

### 7.8 起始页（P0）

- 纯 WPF 控件（`StartPageView`），**不创建 WebView2**：新标签打开时内存增量为 0，这是空载基线 ≤120MB 的前提。
- 结构：居中搜索框（回车走默认搜索引擎）+ 快捷访问网格（默认 8 格，最多 24）+ 底部一行状态（进程组内存、当前布局模式）。
- 快捷访问：图标 + 标题 + URL；可从历史自动推荐（访问次数 ≥ 5 的 origin），也可手动增删改、拖拽排序。
- 图标：优先本地 favicon 缓存，其次按域名哈希取色的首字母色块；**不为了图标发网络请求**。
- 可配置：每行个数（4 / 6 / 8）、是否显示搜索框、背景（纯色 / 跟随主题 / 本地图片，≤2MB）。
- 搜索框与地址栏共用解析器，支持 `b ` / `gh ` 这类关键词前缀。

**0.18 实现状态**：搜索框、8 个默认快捷方式、纯 WPF 零 WebView2 已落地；本批补齐**增删改**——`+` 磁贴或右键「编辑 / 删除」，页内浮层编辑标题与网址（URL 归一化、非法输入拒绝、落盘 `settings.Shortcuts`），上限 24 条。图标来自 `Favicons` 内存缓存（不发额外网络请求），取不到用首字母色块。**仍未做**：历史自动推荐（访问次数 ≥5 的 origin）、拖拽排序、每行个数/背景等可配置项。

### 7.9 默认浏览器注册（**M6-a 已实现**，0.16）

只写 HKCU，不碰 HKLM，不需要管理员权限：

```
HKCU\Software\Classes\NanoEdgeHTML\shell\open\command   -> "NanoEdge.exe" "%1"
HKCU\Software\Classes\NanoEdgeHTML\DefaultIcon          -> "NanoEdge.exe",0
HKCU\Software\Clients\StartMenuInternet\NanoEdge\Capabilities
    ApplicationName / ApplicationDescription / ApplicationIcon
    URLAssociations(http, https) -> NanoEdgeHTML
HKCU\Software\RegisteredApplications                    -> NanoEdge = ...\Capabilities
```

- Win10/11 不允许程序静默抢默认，注册后仍需用户在「设置 → 默认应用」里确认；点击后直接打开 `ms-settings:defaultapps`，文案写明"请选择 NanoEdge"，**不假装已生效**。
- 启动参数支持裸 `"<url>"` 与 `--open-url=<url>`；已有实例时用**命名管道**（`NanoEdge.Ipc.v1`）转交，不是 `WM_COPYDATA`。
- 取消注册 = 删除上述键；本批只做了注册入口，没做菜单项。
- **不主动提醒**：任何启动都不弹"设为默认"，只在主菜单出现。
- **不注册 `.htm/.html`**：只接 http/https，避免抢本地网页的默认程序。

---

### 7.10 内部页：历史 / 书签（M3-b 已实现）

**设计原则：内部页是纯 WPF 控件，不创建 WebView2。** 打开 `nano://history` 或 `nano://bookmarks` 时 renderer 增量为 0；从网页切到内部页会 `Dispose()` 掉当前 WebView2，进程组工作集随之下降。

| 页 | 地址 | 内容 |
|---|---|---|
| 历史 | `nano://history` | 按「今天 / 昨天 / 日期」分组；每行标题 + URL + 最后访问时间 + 访问次数；搜索框按标题/URL 过滤；✕ 删单条；「清空历史」两段式确认（3s 内再点一次） |
| 书签 | `nano://bookmarks` | 按文件夹分组（未分组排最后）；每行标题 + URL + 收藏日期；✕ 删除；「从 Chrome/Edge 导入」「导入 HTML」「导出 HTML」 |

- 行点击在当前标签打开，`Ctrl+点击` 在新标签打开。
- 内部页地址写进 `tab.InternalPage`，随会话持久化，重启后原样恢复（不再走"已丢弃 + 占位"分支）。
- `nano://` 未知路径回落到起始页。
- 布局：内容列 `min(980, 可用宽-64)` 居中；标题 22px、搜索框 34px、行高 46px，与起始页同一套色板。

**书签导入（两种格式都支持）**

1. **Chrome / Edge 的 `Bookmarks`（JSON）**：自动扫描 `%LOCALAPPDATA%\Google\Chrome\User Data\*\Bookmarks`、`Microsoft\Edge\User Data\*\Bookmarks`、`Chromium`，递归 `roots → children`，文件夹路径用 ` / ` 拼接。
2. **Netscape 书签 HTML**：Chrome/Edge/Firefox 的导出格式，一次正则扫描维护 `<H3>` + `<DL>` 文件夹栈（思路参考 ZZZ 的 `ImportHtmlAsync`）。

导入统一过滤非 http(s)/file 的条目，并按归一化 URL 去重（`Urls.Same` 用 `Uri.Compare` 忽略大小写、默认端口、末尾斜杠差异 —— 这也修掉了 `github.com` 与 `github.com/` 被当成两条的老问题）。

### 7.11 缩放（M3-c 已实现）

| 维度 | 做法 |
|---|---|
| API | `WebView2.ZoomFactor`（官方 `CoreWebView2Controller.ZoomFactor` 的 WPF 投影），不自研缩放 UI |
| 档位 | Chrome 离散阶梯 25% / 33% / 50% / 67% / 75% / 80% / 90% / 100% / 110% / 125% / 150% / 175% / 200% / 250% / 300% / 400% / 500% |
| 快捷键 | `Ctrl++` / `Ctrl+-` / `Ctrl+0`（含小键盘 `Add` / `Subtract` / `NumPad0`），走 7.10 建立的 `ComponentDispatcher` 统一入口 |
| 记忆粒度 | 站点 = `host[:port]`；默认端口省略，`file:` 归「本地文件」，`nano://` 不参与 |
| 存储 | `%APPDATA%\NanoEdge\zoom.json`，`Dictionary<site, factor>`；设回 100% 即删掉该条目 |
| 生效时机 | 新建 / 丢弃后重建 WebView2 时、以及 `SourceChanged` 发现站点变化时套用；同站点内导航不重复写 |
| 滚轮 | `IsZoomControlEnabled` 保持 `true`，`Ctrl+滚轮` 由 WebView2 原生处理；宿主监听 `ZoomFactorChanged` 把它一并记入站点 |
| 反馈 | 状态栏显示「缩放 N%」；每次操作 1.2s Toast 显示档位与站点；内部页/起始页缩放给出「当前页面不能缩放」 |

**为什么键盘快捷键自己分发**：把记忆档位写进 `ZoomFactor` 后，它会成为该 WebView2 的「默认缩放」，WebView2 自带的 `Ctrl+0` 只会回到这个值而不是 100%。要在「按站点记忆」的同时保证 `Ctrl+0` 真复位，必须由宿主掌握档位。这不违反「浏览器基础能力优先用 WebView2 自带」——缩放没有现成 UI 可替换，用的是官方 API；页内查找有现成 UI，仍完整交给 `Ctrl+F`。

### 7.12 下载（M3-c 已实现）

| 维度 | 做法 |
|---|---|
| 事件源 | `CoreWebView2.DownloadStarting` → `CoreWebView2DownloadOperation` 的 `BytesReceivedChanged` / `StateChanged` |
| 弹窗 | `Handled = true` 隐藏 WebView2 默认下载对话框，`nano://downloads` 作为 UI |
| 落盘 | `ResultFilePath` 统一指向系统 Downloads 目录，重名加 `(1)/(2)`；自测可把目录改到临时路径 |
| 记录 | `%APPDATA%\NanoEdge\downloads.json`，200 条封顶；退出时未完成的记录下次启动标「已中断 · 上次退出时未完成」 |
| 列表页 | `nano://downloads` 纯 WPF 内部页（零 renderer）：搜索、进度条、打开 / 打开文件夹 / 取消 / 移除、清除已结束（3s 两段确认） |
| 刷新 | 事件驱动；进度 200ms 节流，状态切换立即刷新，无常驻轮询 |
| 内存豁免 | 沿用 4.3 第 2 条：`tab.ActiveDownloads > 0` 时最浅只降到 `Low`，由 M2 的 `MemoryGovernor.ExemptionReason` 判定 |
| 入口 | `Ctrl+J` 新开标签（已有下载页则切过去），地址栏 `nano://downloads`，主菜单「下载」 |

`Ctrl+J` 之所以新开标签而不是替换当前标签：7.10 的内部页会 `Dispose()` 当前 WebView2，正在下载的页面不该被这个动作打断。

### 7.13 用户脚本整体备份与恢复（0.1.4 新增）

**目标**：公司内部把「单 exe + 一个备份文件」交给同事，对方在 `nano://scripts` 点一次「恢复备份」即可拿到整套注入脚本（含每种类型的多站点匹配规则），不逐条重配。

| 维度 | 做法 |
|---|---|
| 格式 | 单个 UTF-8 无 BOM 的 `.json`：`Format=1` / `App` / `CreatedUtc` / `Scripts[]` / `Attachments[]`；不引入压缩库，exe 体积不变 |
| 脚本条目 | `Name/Namespace/Version` 仅用于展示，**身份以 `Code` 解析出的 `@namespace` + `@name` 为准**（本地随机 `Id` 换机无意义）；`Code` 是完整 `.user.js` 源码，`Requires[]` 存已固化的依赖正文 |
| 不含 | GM 运行值（`script-values.json`）、按站点临时禁用（本来就是内存态）、浏览器设置 / 会话 / Cookie、WebView2 用户数据目录 |
| 恢复语义 | 默认**合并**：同名更新、异名新增；确认卡给「先清空现有脚本再恢复」开关（默认关）用于完全还原 |
| 确认 | 恢复前必须过确认卡：新增 / 覆盖数、脚本名、权限清单、DDL 附件与目标站点；不允许静默写入 |
| 生效 | 沿用 7.3：脚本改动只在下次导航或刷新后生效，UI 写明，不假装热更新 |
| 入口 | `nano://scripts` 的两个按钮「整体备份」「恢复备份」；不做拖拽文件还原 |

**DDL 附件（v1 只支持 `pg4-assist`）**：`Attachments[]` 里 `Kind="pg4-ddl"`，带 `Name`（文件名）、`Text`（DDL 源文本）、`Target`（目标站点 glob）。不搬 IndexedDB——`pg4-assist` 的快照存在网页 IndexedDB、配置在 localStorage，按 origin 隔离且混有登录态，直接复制用户数据目录不可用；而它已暴露 `window.__pg4.importDdlText(文本, 名称, 文件名)`，喂回源文本即可重建，所以备份只带**原始 DDL 文本**，不带解析结果。

- 恢复时把文本固化成 `%APPDATA%\NanoEdge\ddl-pending.json`，只登记文件名、文本、目标站点和一次性 `Token`；**不进 GM 值、不进 `.user.js`**——7.3 的 GM 值与脚本源码会整包注入每个页面，几 MB 的 DDL 会拖慢所有标签。
- 触发时机：`CoreWebView2.NavigationCompleted` 且 URL 命中 `Target` 时，宿主注入一段自包含 JS：等 `window.__pg4` 就绪（40 × 250ms 重试）→ `core.listSnapshots()` 按 `name + bytes` 幂等检查 → `importDdlText()` 重新解析并激活 → 带 `Token` 发 `ddlDone` 回执。回执校验通过才清 pending，页面伪造不了。
- 已打开的匹配页面在恢复后通过 `DdlRestore.PendingChanged` 立刻试一次；没就绪就静默留到下次导航，不弹错误。备份时未填目标站点则只打包文本并提示，不自动重建。

**安全**：备份文件是纯文本，但恢复的脚本能读改页面、借登录态发请求；确认卡列出权限并提示只恢复可信来源。`Target` 由备份方填写并随文件传递，宿主不硬编码任何公司域名。

**明确不做**：拖拽文件自动还原（收益不抵 WebView2 HwndHost 的窗口级拖文件处理成本）；通用附件协议（v1 只有 `pg4-ddl` 一种）；云同步、自动更新、备份加密。

## 8. 数据模型（草图）

```csharp
enum TabMemoryState { Active, Low, Discarded }

sealed class TabModel {
    string Id;
    string Url, Title;
    bool IsPinned, IsSecret;
    string? GroupId;
    TabMemoryState MemoryState;
    DateTime LastActiveUtc;
    string? SnapshotPath;
    double Zoom;
    bool KeepAlive;          // 密封标签保活
}

sealed class TabGroup {
    string Id, Name;
    string Color;            // #RRGGBB
    bool IsCollapsed;
    int SortOrder;
}

sealed class UserScript {
    string Id, Name, Namespace, Version;
    string[] Matches, Includes, Excludes, Grants, Connects;
    RunAt RunAt;             // DocumentStart / DocumentEnd / DocumentIdle
    bool Enabled;
    string Code;
}

sealed class AppSettings {
    int SchemaVersion;
    string Language;         // auto | zh-CN | en-US
    string Theme;            // system | dark | light
    LayoutSettings Layout;   // mode, tabWidth, showStatusBar
    MemorySettings Memory;   // preset, thresholds, exemptions
    PrivacySettings Privacy; // secretHotkey, decoyUrl, blockCapture
    // ...
}
```

---

## 9. 安全与隐私

- **零遥测**：不向任何服务器发数据；更新检查只在你点按钮时打开浏览器。
- **脚本即代码**：注入的 JS 能读页面一切内容，导入时明确提示；不做"远程脚本自动更新"。
- **不存密码**：`input[type=password]` 不进入快照，不提供密码管理。
- **密封标签的诚实边界（0.15 简化口径，0.18 复核）**：历史 / 会话 / 联想 / 缩放 / 快照 / 下载站点这几类**应用层记录**一律不写，屏幕上的标题、地址栏、tooltip 也做伪装；但历史/书签/会话这些静态文件是**明文可读 JSON，没有 DPAPI**（0.15 按用户口径砍掉），且密封标签与普通标签**共享 WebView2 profile**，Cookie / 缓存仍会留在磁盘上。没有独立 Profile、防截屏、通知重绘、诱饵页。同一 Windows 账户下的其他程序仍可读 profile；运行中的页面内容在内存里。
- **WebView2 设置（0.18 复核）**：当前 `ConfigureAsync` 只显式设置 `AreDevToolsEnabled / AreDefaultContextMenusEnabled / IsStatusBarEnabled / IsWebMessageEnabled / IsZoomControlEnabled`，并拦截密封标签的通知权限。**SmartScreen、自动填充、密码保存提示沿用 WebView2 默认，未逐项显式设置**；摄像头/麦克风/位置/剪贴板也走 WebView2 默认权限弹条，未做全局策略页。

---

## 10. 构建与发布

`NanoEdge.csproj` 关键项：

```xml
<TargetFramework>net48</TargetFramework>
<PlatformTarget>x64</PlatformTarget>
<LangVersion>latest</LangVersion>
<UseWPF>true</UseWPF>
<ApplicationManifest>app.manifest</ApplicationManifest>  <!-- PerMonitorV2 -->
<PackageReference Include="Microsoft.Web.WebView2" Version="1.0.4078.44" GeneratePathProperty="true" />
<PackageReference Include="Costura.Fody" Version="6.2.0" PrivateAssets="all" />
<PackageReference Include="Fody" Version="6.9.3" PrivateAssets="all" />
<EmbeddedResource Include="$(PkgMicrosoft_Web_WebView2)\build\native\x64\WebView2Loader.dll"
                  LogicalName="NanoEdge.Native.x64.WebView2Loader.dll" />
```

发布流程：

1. `dotnet build -c Release`
2. 校验 `NanoEdge.exe` ≤ 3.0MB
3. 本机跑一次 `--smoke` 等价的启动自检（确认 Runtime 检测与提示分支）
4. 打 zip（exe + 可选 portable.flag + README）

运行时自检（**刻意保持最简单**）：

```
启动 → GetAvailableCoreWebView2BrowserVersionString()
  ├── 抛异常 / 返回空 → 一个说明框：
  │     「NanoEdge 需要 Microsoft Edge WebView2 Runtime，当前系统未检测到。
  │       Windows 11 通常已内置；请安装后重试。
  │       https://developer.microsoft.com/microsoft-edge/webview2/」
  │     [确定] → 退出
  └── 正常 → 继续（不校验最低版本，不区分 Evergreen / Fixed Version）
```

不做：下载器、安装器、版本升级提示、重启逻辑、Fixed Version 方案说明、多语言错误页。理由：Win11 与绝大多数 Win10 已预装，为极少数环境写引导流程是净复杂度；用户自行安装 Runtime 比我们在程序里处理更可靠。

---

## 11. 风险登记

| 风险 | 概率 | 影响 | 缓解 | 验证阶段 |
|---|---|---|---|---|
| WebView2 折叠后 `TrySuspendAsync` 不生效 | 中 | 高 | 改用 controller 级 `IsVisible`；最坏退到"隐藏窗口时丢弃当前标签" | Phase 0 |
| 标签视图不能反复挂载/卸载 | 中 | 高 | 常驻 Grid + Visibility，永不 reparent | Phase 0 |
| WPF 自身 40–60MB 固定开销 | 高 | 中 | 精简资源字典、延迟加载面板、不用重型控件 | Phase 0 |
| 悬浮 Popup 抢焦点 / 拖拽失败 | 中 | 中 | `WS_EX_NOACTIVATE` + 手动命中测试；备选"推开内容" | Phase 0 |
| 单文件 loader 释放被杀软拦截 | 低 | 高 | 固定路径 + 版本校验 + 可执行文件签名 | M6 |
| WebView2 Runtime 缺失（少数 Win10） | 低 | 低 | 提示并退出，不做引导（v1.2 决策） | M6 |
| ~~冻结导致通知丢失~~ | ~~高~~ | ~~中~~ | **已消除**：v1.3 取消冻结，`Low` 不停 JS（0.9） | M2 |
| 丢弃导致 SPA 状态丢失 | 高 | 中 | 站点白名单 + 表单快照 + "常驻"开关 | M2 |
| 标签多时内存仍超预期 | 中 | 高 | 实验性 `renderer-process-limit` + 降低丢弃阈值 | M2 |
| 工期失控（功能蔓延） | 高 | 高 | P0 清单冻结，P1/P2 一律不进首个版本 | 全程 |
| 按标签内存归因不准（同源共享 renderer） | 高 | 中 | 共享时合并成一行或均摊并标注"共享"，不伪装精确值 | Phase 0 第 7 项 |
| 内存面板 Popup 泄露密封标签标题 | 中 | 中 | 标题走 `DisplayTitle`（已实现）；不设 display affinity，录屏可见，接受 | M5（0.15 缓解） |
| "一键释放"被误解成清空内存 | 中 | 低 | 按钮命名、文案、无进度条（见 4.9） | M2 |

---

## 12. 里程碑

### Phase 0 — 技术验证（2–3 天，不写产品代码）

一个 200 行的 WPF 试验程序，只回答 7 个问题：

1. 5 个 WebView2 标签，总内存是多少？browser/GPU/renderer 各占多少？
2. `Collapsed` + `TrySuspendAsync()` 后，`IsSuspended` 是否为 true？内存降多少？
3. `Resume()` 后恢复是否完整（滚动位置、表单、播放状态）？
4. Dispose 后重建，单标签耗时多少？内存是否真的归零？
5. 垂直标签悬浮展开用 Popup，能否正常悬停/点击/拖拽，且不抢焦点？
6. 单文件 exe + 嵌入 loader，在无 DLL 的目录里能否启动？
7. `AssociatedFrameInfos` 是否非空？`FrameId` 能否对上 `CoreWebView2.FrameId`？同源两个标签是否共用同一个 renderer PID（决定 4.9 的归因要不要做分摊）？

产出：一页实测数据 + 必要时调整第 4 章阈值。

**状态：2026-11-04 已完成。** 代码在 `phase0/`（net48 WPF，516KB 单 exe），原始报告 `phase0/out/run1.txt`、`run2.txt`、`standalone-smoke.txt`。

| # | 验证项 | 结果 |
|---|---|---|
| 1 | 5 标签基线 | 358.6MB 私有 / 629.4MB 工作集；GPU 95MB、browser 64MB、WPF 外壳 52MB、renderer 5×20MB |
| 2 | `Collapsed` + `TrySuspendAsync()` | **通过**：5/5 返回 true、5/5 `IsSuspended=true` |
| 3 | `Resume()` 完整性 | **通过**：5/5 恢复 `scrollY=1200`、表单值、DOM；tick 计数证明冻结期间 JS 确实停了 |
| 4 | `Dispose()` + 重建 | **通过**：renderer 进程退出 1 个，重建 117–119ms |
| 5 | Popup 悬浮层 | **通过**：独立 HWND、不抢焦点、`WDA_EXCLUDEFROMCAPTURE` 可设置（密封标签防泄露方案成立） |
| 6 | 单文件 exe | **通过**：Costura 嵌入托管 DLL + 内嵌 loader 释放到 LOCALAPPDATA；目录里只有 exe 时 `--smoke` 正常启动 WebView2。Runtime 缺失分支按 v1.2 只做提示退出，无流程可验证，不再需要干净 VM |
| 7 | 按标签归因 | **通过**：`GetProcessExtendedInfosAsync()` 可用，5 个 renderer 各 1 个 MainFrame，`FrameId` 1:1 匹配；同域 3 标签未共享 renderer |
| 8 | 内存阶梯（首轮后追加） | 见 2.1；修正了 v1.0 的四态假设 |

**结论**：7.1 的"常驻 Grid + Visibility 切换"宿主方案成立，不需要换 WinForms / CompositionControl。唯一必须改的是内存模型（0.3），功能范围不变。M1 可以开始。

### M1 — 外壳与布局（**已完成 2026-11-04**，详见 0.7）

窗口 chrome、三种布局、标签拖拽排序、分组折叠、悬浮展开、**起始页（快捷访问 + 搜索框）**、暗色/亮色、快捷键骨架。

### M2 — 内存治理（**已完成 2026-11-04**，详见 0.8 / 0.9）

三态状态机（Active/Low/Discarded）、`MemoryGovernor`、豁免规则、快照与恢复、**内存悬浮面板与一键释放**、三档预设。

**验收**：`--memtest` 自动跑 8 项断言全部通过（`out/m2-memtest.txt`），面板/占位图渲染确认（`out/m2-panel*.png`、`m2-placeholder*.png`）。

| 项 | 结果 |
|---|---|
| 失焦 → 全标签 `Low` | 通过；449.1 → 364.9MB 工作集，renderer -87% |
| 回到前台 → 当前标签 `Normal`、其余保持 `Low` | 通过；三态机无冻结档 |
| 一键释放后台 | 通过；2 个 renderer 退出，工作集 -4MB / 提交 -44MB |
| 丢弃快照 | 通过；scrollY/表单/256KB 上限，实测 319 字节 |
| 选中已丢弃标签 → 重建 + 还原 | 通过；scrollY、textarea 值恢复，toast 提供"重新加载" |
| 内存面板 | 通过；7 行采样、工作集/提交双列、释放按钮（懒创建，不打开不采样） |
| 三档预设 | 通过；菜单切换即时生效并落盘（保守 / 均衡 / 激进） |
| 豁免规则 | 音频/下载/固定/常驻/DevTools/密封保活，最浅到 `Low` |

### M3 — 浏览基础（**M3-a/b/c 已完成，M3-d 跳过**，2026-11-04，详见 0.13）

地址栏与联想、导航、会话恢复、书签（含导入）、历史、下载、查找、缩放、DevTools、崩溃恢复。

| 批次 | 内容 | 状态 |
|---|---|---|
| M3-a | 地址栏解析与清理、联想下拉（历史/书签/已开标签）、键盘操作、`Ctrl+Enter` 补 `.com`、历史记录与落盘、导航失败 Toast 重试 | **完成**（0.10） |
| M3-b | 内部页 `nano://history` / `nano://bookmarks`（纯 WPF）、Ctrl+D 收藏、Chrome/Edge 与 HTML 书签导入、快捷键消息泵层接管 | **完成**（0.11） |
| M3-c | `Ctrl+±/0` 缩放（按站点记忆）、下载列表（`Ctrl+J`）。**页内查找不自研**：直接用 WebView2 内置查找条（`Ctrl+F` 现成可用） | **完成**（0.12，`--m3ctest` 54/54） |
| M3-d | 崩溃恢复（BrowserProcessExited / 渲染进程无响应自动重载）、真实站点内存复测回填 2.1 | **跳过**（无测试环境，0.13；2.1 对应验收项挂起） |

### M4 — 脚本引擎（**已完成 2026-11-04**，详见 0.14）

元数据解析、注入时机、GM API 子集、管理界面、权限确认、日志。

| 项 | 结果 |
|---|---|
| 元数据解析 | 通过；`@name/@namespace/@version/@match/@include/@exclude/@run-at/@grant/@connect` 全覆盖，非法 `@run-at` 回落 idle，`@grant none` 不算权限 |
| 注入时机 | 通过；`document-start` 在页面脚本之前（`readyState=loading`），`document-end` 在 DOMContentLoaded 之后且能取到 DOM |
| GM API 子集 | 通过；`GM_setValue/getValue/deleteValue/addStyle/notification/openInTab/xmlhttpRequest/info`，`@grant` 页面侧与宿主侧双重拒绝 |
| 值持久化 | 通过；跨导航读回 42，落 `script-values.json` |
| `@connect` | 通过；白名单内 200，名单外拒绝且不发请求 |
| 管理页 | 通过；`nano://scripts` 纯 WPF（零 renderer），列表开关/匹配/授权/最后运行时间，等宽+行号编辑器，文件/剪贴板/URL 导入且先确认 `@grant`，删除与打开目录，按站点临时禁用 |
| 自测 | `--m4test` **82/82 PASS**（`out/m4-report.txt` + 7 张截图） |

### M5 — 密封标签（**已完成 2026-11-04，简化版**，详见 0.15）

用户口径：只做伪装、`Alt+\`` 切换、通知开关；独立 Profile / 防截屏 / DPAPI / 清理 / 通知桥 / 诱饵页不做。

| 项 | 结果 |
|---|---|
| 伪装 | 通过；`DisplayTitle=文档`、tooltip 无 URL、地址栏空、不进历史/会话/联想、不能收藏 |
| 切换 | 通过；`Alt+\`` 在密封标签与上一个普通标签之间来回切（应用内） |
| 通知 | 通过；开关关 → `denied`，开 → `granted`（两个 origin 实测） |
| 不留痕 | 通过；`zoom.json` / 快照 / 下载站点 / 失败 Toast 都不带域名 |
| 自测 | `--m5test` **20/20 PASS**（`out/m5-report.txt` + `m5-window.png` / `m5-tabstrip.png`） |

### M6-a — 发布收尾（**已完成 2026-11-04**，详见 0.16）

图标落地、默认浏览器注册、外部唤起、崩溃恢复、Release 打包。

| 项 | 结果 |
|---|---|
| 图标 | 通过；`assets/nanoedge.ico`（16/32/48/64/256）+ PE 图标 + WPF 窗口图标，从 exe 抠出 32×32 验证 |
| 默认浏览器 | 通过；HKCU ProgID + Capabilities(http/https) + RegisteredApplications，测试后注册表还原干净 |
| 外部唤起 | 通过；真起第二个 `NanoEdge.exe <url>`，主实例多标签、第二进程 exit 0 |
| 崩溃恢复 | 通过；kill renderer → 自动重载；kill browser → 全部视图重建、活动标签恢复 |
| 打包 | `dist/NanoEdge-0.1-win64.zip`（556KB），解压后独立启动出图 |
| 自测 | `--m6test` **23/23 PASS**（`out/m6-report.txt` + 3 张截图） |

### M6-b — 双语 + 设置界面（**已完成 2026-11-05**，详见 0.17）

用户明确不做首次运行说明与安全模式，本批只做 P0-13 / P0-14。

| 项 | 结果 |
|---|---|
| 双语 | 通过；`L.T("中文原文")` + 276 条英文字典，`--lang=en` 全界面英文，切换即时重建本地视图 |
| 设置页 | 通过；`nano://settings` 纯 WPF，外观/布局/内存/隐私/关于五段，改动即时生效并落盘 |
| 自测 | `--m6btest` **28/28 PASS**（扫描可视树确认英文页无漏翻中文） |
| 首次运行说明 / 安全模式 | **不做**（用户口径） |

### M7 — 对照 ZZZ 后的克制补齐（**已完成 2026-11-05**，详见 0.18）

用户口径：P1/P2 与 ZZZ 独有功能「感觉功能我都不用」，只补 P0 欠账与已实现功能的工程细节。对照分析见内部对照报告（不随仓库发布）。

| 项 | 结果 |
|---|---|
| P0 欠账 | 最近关闭 + `Ctrl+Shift+T`、中键关闭 / 关闭右侧 / 复制、favicon、起始页快捷方式增删改、`Ctrl+Shift+M`、`Ctrl+F5` |
| 工程细节 | session 150ms / 设置 500ms 合并写、退出前 `DisposeAll()` 释放全部控制器 |
| 自测 | `--m7test` **31/31 PASS**，7 组历史自测全量回归 PASS |
| 体积 | 924,160 字节（903KB，+31,744） |
| 未做 | 脚本 `@require`（下一批）、全部 P1/P2、`dist` 重新打包 |

### M8 — 用户脚本能力补齐（**已完成 2026-11-05**，详见 0.19）

用户点名「先处理脚本的功能改进」，只做对照报告 A-6 的四项，不动 P1/P2。

| 项 | 结果 |
|---|---|
| `@require` | 导入/保存时下载固化到 `<id>.require.<n>.js`，运行期不联网；失败整体拒绝并保留确认卡片 |
| GM API | `GM_listValues`（本命名空间键）、`GM_setClipboard`（宿主剪贴板，`@grant` 拦截伪造消息） |
| `unsafeWindow` | 等同页面 `window`，无需 `@grant`（主世界注入） |
| 自测 | `--m8test` **45/45 PASS**，8 组历史自测（含 m7）全量回归 PASS |
| 体积 | 960,512 字节（938KB，+36,352） |
| 未做 | `GM_download` / `@resource` / `GM_registerMenuCommand` / 运行时依赖热更新 |

> 合计约 6 周业余时间。若只求"能日常用"，M1+M2+M3 是关键路径。

---

## 13. 决策记录与剩余待定

### 13.1 已确认（Q1–Q8）

| # | 问题 | 结论 | 落地位置 |
|---|---|---|---|
| Q1 | 多窗口 / 标签拖出 | **不做**，只做单窗口多标签 | 1.4 非目标、第 6 章 |
| Q2 | CRX / Chrome 扩展 | **不做**，只做内置脚本注入 | 1.4、7.3 |
| Q3 | 广告拦截 | **不做** | 1.4、第 6 章 P2 已删 |
| Q4 | 设为默认浏览器 | 做，显式按钮，**不主动提醒** | 7.9、P1-10 |
| Q5 | AppData 还是便携 | 默认 AppData，`portable.flag` 切便携 | 7.6 |
| Q6 | 图标配色与形状 | **C 折角 N + 四段渐变（蓝→紫→品红→橙）**，居中修正 | 5.4、5.5 |
| Q7 | 起始页 | 做，支持自定义快捷访问 | 7.8、P0-16 |
| Q8 | 悬浮展开 | Popup 覆盖为默认，"推开内容"作为设置项 | 5.3、7.1 |

### 13.2 已确认（R1–R4、Q5）

| # | 问题 | 结论 |
|---|---|---|
| R1 | 失焦冻结时机 | ~~60s~~ **已作废**：v1.3 取消冻结档（0.9） |
| R2 | 总内存阈值 | **700MB**，Phase 0 实测后回填校准 |
| R3 | 起始页 | **4×2 八格 + 显示搜索框** |
| R4 | 图标 | **C 折角 N**，修正居中，四段 GTA6 渐变 |
| Q5 | 数据位置 | **默认 AppData** + `portable.flag` 切便携 |
| 新增 | 内存悬浮面板 + 一键释放 | **纳入 P0-17**（4.9） |

### 13.3 冻结声明

本版为 **v1.0 决策冻结**。P0 共 17 项，不再新增。后续任何改动走变更记录，需明确写出：改了什么、为什么、对内存/启动/体积三项指标的影响。

v1.1 属于 Phase 0 实测驱动的数据修订（0.3），只改内存模型与验收口径，**不算重开讨论、不改 P0 范围**。

---

## 14. 参考项目对照

| 项目 | 可借鉴 | 不要学 |
|---|---|---|
| **ZZZ**（MIT） | 单文件打包与 loader 释放、用户脚本桥与 `@grant` 双端校验、DPAPI 数据保护、隐私 profile 清理、启动页与标签休眠思路 | 功能面铺太宽（翻译/广告拦截/工作区）、无隐藏降内存、无分组、无单行布局 |
| **MishaWeb** | 三层标签内存策略的分档思路、进程/标签生命周期建模 | .NET 10 依赖、WinForms 自绘 UI |
| **Helium** | CAT 单行布局的 flex 顺序与收缩优先级、竖排标签尺寸（30px 项高）、动态隐藏单标签的标签条 | 完整 Chromium 分叉的路线 |
| **Split Browser** | 用 WebView2 做到 <100MB 的极简验证 | Qt 依赖、无标签管理 |

逐子系统的实现对照、ZZZ 独有功能分级与「明确不做」清单，见**内部对照报告**（不随仓库发布；0.18 的取舍依据）。架构结论：标签宿主与内存治理我们更合理，ZZZ 只在功能完整度和工程细节上领先；领先项大多是低成本补齐，不是架构优势。

---

## 15. 下一步

1. Phase 0 **已完成**（第 12 章），M1 **已完成**（0.7），M2 **已完成**（0.8）；
2. **M3 — 浏览基础 收尾**：M3-a 地址栏（0.10）、M3-b 历史/书签内部页（0.11）、M3-c 缩放与下载（0.12）均已完成；**M3-d 因无测试环境跳过**（0.13），真实 dev 站点内存复测与崩溃恢复列为遗留项。页内查找保持 WebView2 内置，不自研；
3. **M4 — 脚本引擎 已完成**（0.14）：元数据解析、`AddScriptToExecuteOnDocumentCreatedAsync` 注入、GM API 子集与 `@grant` 双端拒绝、`nano://scripts` 管理页（列表/编辑器/导入确认/按站点禁用）、`--m4test` 82/82；
4. **M5 — 密封标签 已完成（简化版，0.15）**：只做伪装 + `Alt+\`` 切换 + 通知开关；独立 Profile、防截屏、DPAPI、清理、通知重绘、诱饵页按用户口径砍掉；
5. **M6-a 发布收尾 已完成**（0.16）：图标 ico 嵌入、默认浏览器注册、外部唤起（命名管道）、崩溃恢复（渲染进程 / 浏览器进程）、`dist/NanoEdge-0.1-win64.zip`；
6. **M6-b 双语 + 设置界面 已完成**（0.17）：中/英全界面、`nano://settings`（外观/布局/内存/隐私/关于）。首次运行说明与安全模式按用户口径不做；
7. **P0 17 项全部完成**；0.18 又补齐了 SPEC 自己写了却没实现的欠账（`Ctrl+Shift+T`、favicon、起始页编辑等），P1/P2 一项未动；
8. **M8 用户脚本能力补齐 已完成**（0.19）：`@require` 导入固化、`GM_listValues`、`GM_setClipboard`、`unsafeWindow`，`--m8test` 45/45；
9. **遗留验收债**：2 章 900MB 目标与 2.1 真实 dev 站点内存阶梯未验证，有 pgAdmin / eck 环境时单独补测回填；
10. **下一批候选**：起始页历史推荐与拖拽排序（7.8 剩余部分）；跨断点布局提示与 `<900px` 标签条折叠（5.1/5.2）；脚本侧 `GM_download` / `@resource` / `GM_registerMenuCommand`（对照报告判收益低，未排）。等用户点名再做；
11. **发布收尾**：`dist/NanoEdge-0.1-win64.zip` 仍是 M6-a 的旧包，需要发布时重新打包。
