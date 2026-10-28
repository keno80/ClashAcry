# ClashAcry v1 设计规格

日期：2026-10-26　状态：待审阅
范围：v1 = 核心闭环。phase 2 内容只记边界，不在本规格展开。

## 1. 目标与定位

基于原版 mihomo（Clash Meta）Go 内核的 Windows 代理客户端，自用工具定位：

- **透出引擎原生模型**：配置即 `config.yaml`，运行态即 `external-controller` REST；客户端不做"小白封装"，不重写内核已有能力。
- **视觉目标即原生 Fluent**：WinUI 3 + **整窗 Desktop Acrylic 为默认**，可切 Mica / 纯色。这是本项目存在的理由，视觉纯度优先于合成开销——开销用"可切换档位 + 不支持时自动降级"吸收，而不是把默认改到更省的那一档。
- **常驻开销是硬指标**：日常常驻形态 = Tray 进程 + mihomo 内核；UI 进程关窗即销毁，不留 XAML/渲染开销。
- **不引入 Rust**：数据面全在 Go 内核里，客户端剩下的活是编排（进程托管、配置注入、REST 消费）+ Windows 集成（注册表、托盘、提权）。这些 C# 原生覆盖且无性能诉求，插一层 Rust 只增加 C ABI/P/Invoke 边界与 cargo+MSVC 构建链，零收益。

## 2. 技术栈决策记录

| 组件 | 选择 | 理由 |
|---|---|---|
| 界面 | WinUI 3（C# / XAML） | 原生 Fluent 控件与 backdrop 是硬需求；XAML 体系只有 C# 与 C++/WinRT 两条路，后者开发成本对自用工具不划算 |
| 运行时 | .NET 10，Windows App SDK **2.5.1**（stable，2026-09-16） | 2.0.1 起有 `SystemBackdropElement`（backdrop 可置于 XAML 树任意位置并带 `CornerRadius`）；2.2.0 起有 `ApplicationData.GetForUnpackaged()`，免打包也能拿到 per-user 数据目录 |
| 部署 | unpackaged（免 MSIX），`--self-contained` + `WindowsAppSDKSelfContained=true` | 自用免安装；不赌目标机已装 Windows App Runtime |
| DPI | UI 进程带 `app.manifest` 声明 `PerMonitorV2`（Task 16 实测补，见 §8 末） | 缺它时进程按 unaware 跑：125% 屏上窗口矩形是 1250x850 而 XAML 只画左上 1000x680，其余由 DWM 拉伸——文字发虚、像素取样全错位 |
| 常驻宿主 | 独立轻量进程 `ClashAcry.Tray`（纯 Win32，零 XAML，手写 `Shell_NotifyIcon`） | 不用 WinForms 托盘：那会把整套 WinForms 运行时请进常驻进程，直接顶爆 §10 的开销线 |
| 后台能力 | `ClashAcry.Core` 托管类库，被 Tray 与 UI **in-proc 复用** | 无数据面代码，全部是胶水级 OS/HTTP 调用；拆 Rust/AOT 均无收益 |
| 内核 | 原版 mihomo Go 二进制，随包内置一个 + 应用内在线更新 | 不 fork、不魔改，行为跟随上游 |
| 特权守护 | **phase 2**（TUN / 免 UAC 才需要） | v1 走系统代理模式，全程不需要管理员权限 |

已否决路线：Rust 后台（见 §1）；MSIX 打包（v1 不做，免安装 zip 够用）；WinForms 托盘（开销）；Web 技术栈界面（本项目目标即原生 Fluent，不在范围内）。

NativeAOT 明确**不承诺**：WinUI 3 XAML 编译绑定依赖 CoreCLR，本项目按 CoreCLR 自包含发布。Tray 进程 trimming **空壳实测（2026-10-26，Task 2）**：`dotnet publish src/ClashAcry.Tray -c Release -r win-x64 --self-contained -p:PublishTrimmed=true` 删 obj/bin 全新还原后 0 警告 0 错误——**无任何 `IL2xxx`/`IL3xxx`**（全局 `TreatWarningsAsErrors=true`，有警告即构建失败，故"零警告"是构建强保证）；发布产物 20 MB（未 trim 86 MB），空壳常驻 WS 15.9 MB（未 trim 16.4 MB）。**是否最终启用留待 Task 17 复验**：届时 Core 已含 YamlDotNet/反射敏感代码，只要冒出一条 IL2xxx 就按既定策略放弃 trimming。**Task 17 复验结果＝不启用**：产物启动即 `InvalidOperationException: Reflection-based serialization has been disabled`（`SettingsStore.Load`），再加 `-p:JsonSerializerIsReflectionEnabledByDefault=true` 仍以同一异常退出（runtimeconfig 里那条确实是 true，但反射解析器已被裁掉）；要走通得给 STJ 上源生成再处理 YamlDotNet 两套反射面，而收益上限就是本节空壳量到的 0.5 MB WS。ReadyToRun 同理不采用（只影响启动耗时）。数字见 §10 的 Task 17 段④。

## 3. 进程架构与所有权

```
ClashAcry.Tray.exe        常驻托盘宿主（纯 Win32 消息循环，零 XAML）
  ├─ mihomo 生命周期唯一所有者：spawn(-d <runtime>) / 监护 / 崩溃重试 / 优雅停
  ├─ settings.json 与 runtime\config.yaml 的唯一写者
  ├─ 系统代理 HKCU 开关 + InternetSetOptionW 广播
  ├─ 内核在线更新（下载 / 解包 / .bak 轮换 / version.txt）
  ├─ 托盘：状态图标（运行·停止·异常）+ 菜单（打开界面 / 启停内核 / 切模式 / 退出）
  └─ 命名管道服务端：收命令、广播状态与进度事件
ClashAcry.UI.exe          WinUI 3 窗口，按需启动，关窗即退出进程
  ├─ 节点面板 / 订阅导入 / 开关与模式 / 更新入口 / 错误横幅 / 设置页
  ├─ mihomo REST 直连（查询、切节点、测延迟不走管道）
  └─ 界面偏好读写（LocalSettings，UI 独占）
mihomo.exe                原版 Go 内核，external-controller 127.0.0.1:9097
```

**所有权规则（不可协商的三条禁令）**：

1. UI **绝不**自行 spawn 或 kill 内核进程——一切启停/重载经命名管道命令交 Tray 执行。理由：两个进程抢一个内核的所有权，会出现端口双占、状态失真、退出钩子漏清。
2. UI **绝不**写 `settings.json`（改动经 `settings.update` 命令）。引擎/代理类设置唯一写者是 Tray。
3. Tray 侧每一次 `CoreState` 迁移**必须**广播 `core.state` 事件，且状态机迁移点集中在一处（`CoreManager.Transition`），不得有静默改写状态的分支。

关窗语义：UI 的窗口关闭 = 进程退出（`Application.Exit`），代理链路因内核归 Tray 而不中断。单实例：二次启动 UI 时经管道发 `ui.focus` 让既有窗口前置，自身退出。

**反向语义（裁定 R-20a，用户实测提出，2026-10-27）**：托盘「退出」= 整个应用退出，界面窗口跟着关。原来只有正向那半条，实测露出的形态是——托盘走了、内核停了，屏幕上还留着一格窗口，内容是「与托盘的连接中断：Pipe is broken.」+ 全置灰 + 一屏缓存节点值。这不是设计里的降级态（§3 只承诺过「UI 起来发现管道不存在 → 横幅 + 一键拉起」，那是 **UI 先起** 的场景），在用户眼里就是"没退干净"。落点：`TrayHost.ShutdownAsync` 在停内核之后、关管道之前遍历 `Process.GetProcessesByName("ClashAcry.UI")` 调 `CloseMainWindow()`，由 UI 自己的「关窗即退」把进程带走。

R-20a 的两条实现约束（真机核实过，不是推测）：① **只用 `CloseMainWindow()`，绝不 `Kill()`**——本机对真窗口实测 `MainWindowHandle=592352`（非 0，WinUI 3 unpackaged 窗口是标准 HWND）、`CloseMainWindow()` 返回 `True`、3 s 内进程消失；强杀会吞掉 `App.OnUnhandled` 的崩溃落盘，还可能连带杀掉 UI.Tests 的 FlaUI 窗口。② **关不掉就留着**：`CloseMainWindow()` 失败或窗口正忙时不重试、不硬来，退出钩子不为它发明第二种失败表达——留着的那扇窗显示断连横幅，不比今天更坏。

**Tray 退出路径实测（Task 10，2026-10-27）**：`OutputType=WinExe` 的进程**不继承父控制台**——从 Git Bash 起它之后 `kill -INT` 打不进去，`Console.CancelKeyPress` 与 `SetConsoleCtrlHandler` 两条都没被触发（进程照跑、管道照应答）。所以优雅收尾在发布形态下**只有一条真路**：托盘菜单「退出」→ `TrayHost.ShutdownAsync`（摘图标 → 关系统代理 → 停内核 → 关界面 → 关管道 → `Environment.Exit`；「关界面」是 R-20a 补的一格）。两条连带结论：① `--no-tray` 的 CI/FlaUI 形态因此没有优雅出口，收尾只能硬杀进程（Task 12 需要它自己可控地停宿主时再补一条命令）；② 计划里「WinExe 从终端起来时 CancelKeyPress 是可用的主路」这句被实测否证，代码注释已按实测改写。

**托盘两条尚未证的 Win32 细节（留给 Task 18 的人工点击）**：① `HWND_MESSAGE` 消息窗口上 `TrackPopupMenuEx(TPM_RETURNCMD)` 是否真能响应点击（消息窗口不能 `SetForegroundWindow`）；不响应就换成普通隐藏窗口（`WS_POPUP` 不显示）+ `SetForegroundWindow` + `GetWindowRect` 定位。② 图标在 Win11 默认落在**隐藏图标飞出区**，不在常驻可见区——冒烟清单要写清去哪儿找。

**tooltip 泵只在文案变化时才 `NIM_MODIFY`**（Task 10 实测订正）：无条件每 2 s 刷一次会把用户正悬停 reading 的提示气泡打断，等于「没人能读完 tooltip」；现在常态下这条循环什么都不做。

**发布产物的一条启动期地雷（Task 10 实测）**：`publish\` 目录里残留一份旧的 **trimmed** `System.Private.CoreLib.dll`（2.5 MB，Task 2 的 trim 实验留下）时，重发布**不会**覆盖它，产物一启动就是 `MissingMethodException`（先是 `MemoryExtensions.Contains`，改调用后换成 `Console.get_Error`——被裁掉的 BCL 成员一个接一个冒出来），而框架依赖的 `dotnet run` 一切正常，极易误判成代码 bug。规则：**发布前先清 `publish\` 目录**（Task 17 的发布脚本必须带上这一步）。

## 4. 契约

### 4.1 命名管道 `clashacry.tray.v1`

管道 ACL 限当前用户 SID（`FileSecurity` 显式授权，拒绝 Everyone）。**落地形态已实测修正（Task 9 派发前探针，.NET 10 / `net10.0-windows10.0.19041.0`）**：`NamedPipeServerStream` 在 .NET 10 **没有**带 `PipeSecurity` 的构造函数，且对已建实例调 `SetAccessControl` 抛 `UnauthorizedAccessException`，读回仍是默认 5 条 Allow（含 `S-1-1-0` Everyone）——即"先建后设"这条路等于没设；唯一可行入口是 `NamedPipeServerStreamAcl.Create(...)`（建实例时落 DACL，读回恰好 1 条 Allow = 当前用户 SID）。另外 `maxNumberOfServerInstances` 传 255 会抛 `ArgumentOutOfRangeException`，合法是 1–254 或 `MaxAllowedServerInstances`。
> **Task 9 实现期再修正（本机实测）**：① 授权掩码**不能只给 `PipeAccessRights.ReadWrite`**——它展开是 `0x2019B`，不含 `CreateNewInstance`(0x4)，而 Windows 对**每一个后续实例**都要拿「已存在的管道对象」的 DACL 查 `CREATE_PIPE_INSTANCE`，于是 accept 循环在服务完第一个客户端后建第二台实例时抛 `UnauthorizedAccessException: Access to the path is denied.`（连不带 SD 的裸 `new NamedPipeServerStream` 也照样拒绝，所以这不是我们设坏了，是设小了）。正确写法：`ReadWrite | CreateNewInstance`；读回仍恰好 1 条 Allow、只给当前用户 SID（SDDL `D:(A;;0x12019f;;;S-1-…-1000)`），secret 红线不受影响。**「只授权 ReadWrite」是安全评审容易通过、运行时却必炸的坑，回归锁是连接两次以上的那条用例。** ② `NamedPipeServerStream.WaitForConnectionAsync` **确实观察取消令牌**（已取消令牌 0 ms 返回；300 ms 处的取消 304 ms 返回），派发前那条「它不观察取消、必须靠 Dispose 打断」的预判不成立；实现里保留「取消时把正在等待的那台实例 Dispose 掉」——不是为了打断等待，而是关掉句柄才能让「服务端已消失」的断言成立。一帧一条 UTF-8 JSON 行：`{"id","kind":"command|event|reply","type","payload","seq","reply_to","error"}`。线键名一律小写/snake_case，且由 `ClashAcry.Contracts` 里每个跨进程结构体的显式 `[JsonPropertyName]` 钉死——**不靠序列化命名策略推断**（Task 3 落地：`Frame`、`HandshakeReply` 与三个事件 payload 全量标注，`FrameCodec` 不设命名策略）。

握手回包 `handshake` 的形状（UI 起 REST 客户端所需的全部参数，一次给齐）：`{state,host,port,secret,mixed_port,core_version?,delay_test_url}`——`delay_test_url` 走握手而非另开一条读设置的通路，UI 测速与设置页的测速地址下拉都用它。**secret 只在 `handshake` 的回包里下发，UI 不持久化、不展示。**

命令（UI → Tray）：`core.start`、`core.stop`、`core.reload`、`mode.set{mode}`、`sysproxy.set{enabled}`、`core.update`、`settings.update{patch}`、`settings.get`、`ui.focus`、`handshake`（返回 `CoreState` + controller 地址 + secret，供 UI 起 REST 客户端）。

> `settings.get` 是 Task 14 补的**只读**命令，回包就是同一个 `SettingsSnapshot`（不含 secret/core_path），**绝不落盘、绝不广播**。开它的理由：概览页要显示"系统代理现在开着没有"，而那一项只活在 Tray 的 `settings.json` 里；不开这条，UI 就只能发一条 no-op 的 `settings.update` 去骗取回包——那是写通路（违反"UI 不写引擎设置"），而且 no-op 也会广播一次 `settings.changed`，凭空造出一个没人改过的改动。这条命令的存在也让"读设置"与"改设置"在契约上是两条不同的路，回归锁分别钉住（`TrayCommandRoutingTests` 断言回包无 secret、`settings.json` 的 `LastWriteTimeUtc` 未变）。

事件（Tray → UI）：`core.state{state,reason?,stderr_tail?}`、`core.update.progress{received,total,pct}`、`core.update.result{result}`、`settings.changed{SettingsSnapshot 全量}`——**四个即全部**。日志文本不单独成事件（`log{level,msg}` 在预检评审中作为无广播方的死契约删除），内核输出随 `core.state` 的 `stderr_tail` 下发；`settings.changed` 恒为完整的 `SettingsSnapshot` 而非键增量，以免"同一个事件两种 payload"。（键名拼写以 `stderr_tail`/`SettingsSnapshot` 为准，Task 3 已实测落码。）

UI 起来发现管道不存在（Tray 未运行或被杀）→ 顶部横幅"托盘宿主未运行" + 一键拉起 Tray 进程。（**Task 14 落地口径**：横幅文案与"控制不了的东西一律置灰"已实现并由 `TrayAbsent_CoreToggleDisabled_AndBannerShown` 钉住。**按钮本身在第五轮接上（裁定 R-21b，见 §12 末）**：`MainWindow.xaml` 的 `InfoBar` 内容里一个「一键拉起托盘」按钮 → `TrayLink.EnsureTrayRunningAsync()`（UI 唯一允许的跨进程 `Process.Start`，且只起 Tray，禁令 1 不动）→ 成就地 `Overview.RefreshAsync()` 重握手 + 重读、不等下一个 2 s 周期，失败则文案落回「重试拉起托盘」并保留那句故障。进行中有反馈（§9）：按钮「拉起中…」+ 禁用，覆盖 500 ms + 3 s 的探活预算。可见判据是**够不够得着托盘**（有故障文案 **且** `_state is null`），不是"横幅开着"——内核崩溃时横幅也开着，那时托盘活得好好的，正确的动作是概览页那个内核开关（锁 `ShellTests.CoreCrashed_BannerShowsFault_ButNoRelaunchAction`）。轮询期间只做 `WaitUpAsync` 探活、绝不在轮询里 `Process.Start`。）

### 4.2 mihomo REST 面（v1 用到的）

鉴权一律 `Authorization: Bearer ${secret}`。

**下表全部为 2026-10-26 真机实测**（内核 `mihomo-windows-amd64-compatible` **v1.19.33**，`GET /version` → `{"meta":true,"version":"v1.19.33"}`；夹具 `packaging/imports-sample/minimal.yaml`，`external-controller 127.0.0.1:9097`、`mixed-port 7897`、`secret probe-secret`；复现：`powershell -File scripts/probe-mihomo-api.ps1`，完整转录当时打印在 `.superpowers/sdd/2026-10-26-clashacry-v1/probe-run1.txt` / `probe-run2.txt`，该目录 git-ignored、不在仓库里；要证据就重跑 `scripts/probe-mihomo-api.ps1`（两种模式各一次））。

| 用途 | 端点 | 实测 |
|---|---|---|
| 探活 / 版本 | `GET /version` | **200** `{"meta":true,"version":"v1.19.33"}` |
| 组与节点树（含 `now`、`all`、`history`、`hidden`） | `GET /proxies` | **200**，根键 `proxies`（对象表，键为节点/组名） |
| 选择成员 | `PUT /proxies/{name}` body `{"name":...}` | **204** 空体；body 换成 `{"proxy":...}` 实测 **400**——请求体只认 `name` 键，`proxy` 键不被接受（Task 6 必须发 `{"name":...}`） |
| 单节点测延迟 | `GET /proxies/{name}/delay?url=&timeout=` | 组与普通节点 **200** `{"delay":N}`；**provider 成员（`prov-node-1`）404** `{"message":"Resource not found"}` |
| Provider 列表 | `GET /providers/proxies` | **200**，响应根为对象 `{"providers": {...}}`——根键是复数 **`providers`**，其值是**以 provider 名为键的对象表**（不是数组）。表内除真实 provider 外还含每个组的隐式 Compatible provider 与 `default`，客户端须按 `vehicleType`（实测出现值：文件型 `"File"`、隐式 `"Compatible"`；HTTP 型未在 v1 探针内实测）过滤；成员条目带 `"provider-name"` 回指 |
| Provider 详情 | `GET /providers/proxies/{name}` | **200**，直接返回 provider 对象本身（**无** `providers` 外层包装），键为 `name/type/vehicleType/proxies/testUrl/expectedStatus/updatedAt`（详情端点打印同样被截断，这份键表取自同一 provider 对象在 `GET /providers/proxies` 全量结构打印里的形态） |
| Provider 手动刷新 | `PUT /providers/proxies/{name}` | **204** 空体（无需请求体） |
| 读当前运行配置 | `GET /configs` | **200**（受支持，返回全量运行配置 JSON；探针打印截断到 200 字符，逐字证到的键是 `port`/`socks-port`/`redir-port`/`tproxy-port`/`mixed-port`/`tun`。`mode` 在完整响应里但本次打印未证到——Task 6 的 `CoreConfigView.Mode` 若反序列化取不到值，以实测为准，别改端点） |
| 切模式（rule/global/direct） | `PATCH /configs {"mode":...}` | **204**；`PUT /configs {"mode":...}` 亦 **204**（两者皆可，v1 统一用 `PATCH`） |
| 热切配置文件 | `PATCH /configs {"path":...}` | **204**；`PUT` 同体亦 **204**；两者都会触发内核全量重载（stdout 出现第二次 `Start initial configuration`） |

provider 形态的两条硬结论（详证见 §12）：

- **provider 成员不出现在 `GET /proxies` 顶层表**（实测顶层键仅 `COMPATIBLE/DIRECT/GLOBAL/PASS/PASS-RULE/PROBE-AUTO/PROBE-SELECT/REJECT/REJECT-DROP/direct-node`），只出现在所属组的 `all` 数组与 `GET /providers/proxies` 的 `proxies` 列表里。
- **对成员直接测延迟 404**。故测 provider 成员的延迟是**既定实现要求**而非可选兜底：UI 取 provider 成员延迟只能走 `GET /providers/proxies/{name}`（成员 `history` / `extra` 健康检查样本），拿不到最新样本时显示"上次"标注或占位，禁止对成员调 `/proxies/{name}/delay` 并静默吞错。

组内聚合整个 provider 的写法（夹具已用，实测有效）：`proxy-groups` 条目里用 `use: [PROBE-PROV]`；在 `proxies:` 列表里直接写 provider 名会被 v1.19.33 拒绝并在启动时 fatal（`proxy group[1]: PROBE-SELECT: 'PROBE-PROV' not found`）。file 型 provider 的 `url` 字段实测**可省略**（仅 `type/path/interval` 即可加载），填不可达占位 URL 也接受且不会被抓取；夹具保留 `url` 以贴近真实订阅形态。

**Task 15 回写（节点页按实测形状实现，四条都会让"用例全绿但真机全废"）**：

- **`type` 字符串是 Go 的类型名，不是配置文件里的值**：实测 `/proxies` 与 `/providers/proxies` 里的组是 `"Selector"` / `"URLTest"`，成员是 `"Shadowsocks"` / `"Direct"` / `"Reject"`——与配置文件里的 `select` / `url-test` **不是同一套字符串**。因此"是不是组"的判据取**结构**：带非空 `all` 即组（实测只有组对象带 `all`，成员条目一律没有）。按 type 名单判组的写法在真机上一条都命中不了。
- **非订阅成员条目的 `provider-name` 是空串**（实测 `"provider-name":""`，不是缺键）。不折成 `null` 就会被判成"订阅成员"，于是全组测速一条都不发——而这条判据同时是"成员不许打 `/proxies/{成员}/delay`"那一条的开关，两边一起坏。
- **同一个成员名会同时出现在真 provider 与各组的 Compatible 伪 provider 里**（`default` 表就是这样），伪条目不带 `provider-name` 也不带健康样本。把 provider 表按成员名摊平时**不能"先到先赢"**：要留信息量大的那条（有 `provider-name` 优先，其次有 `history` 样本），否则真成员被伪条目占位，既测不了也读不到延迟。
- **`history` 里哪一端是最新没有实测依据**（探针只抓到长度 ≤1 的数组），因此不赌列表位置：按样本 `time` 取最大，全部时间戳解析不了才退回末位。


mixed 端口被占用的真机行为（Task 8 `Running` 判据依据，`-OccupyMixedPort` 实测）：预绑 7897 再起内核——内核**不退出**，controller 照常 `GET /version` **200**，日志打一行 `level=error msg="Start Mixed(http+socks) server error: listen tcp 127.0.0.1:7897: bind: Only one usage of each socket address (protocol/network address/port) is normally permitted."` 后继续完成初始化（provider 启动日志照常出现，controller 全部端点仍应答）；且此时 `GET /configs` 报 `"mixed-port":0`。结论：**controller 应答不能证明代理可用**，`Running` 判据必须包含 mixed 端口 TCP 连接探活（与计划 Task 8 的双探活签名一致）。

### 4.3 `settings.json`（Tray 独占写）

```
{ controller_host: "127.0.0.1", controller_port: 9097, secret: "<32hex 首次随机>",
  mixed_port: 7897, core_path: null, delay_test_url: "http://www.gstatic.com/generate_204",
  last_subscription: "<URL 或本地 yaml 绝对路径>", core_version: "<已装内核 tag>",
  sys_proxy_backup: { server: "<开启系统代理前的 ProxyServer>", bypass: "<同 ProxyOverride>" } | null }
```

**`sys_proxy_backup` 是"欠用户一次还原"的凭证（R-20b，2026-10-27 第四轮加）**，不是引擎设置：它只在 `sysproxy.set(true)` 那一次写入、还原成功即清成 `null`，且**绝不出现在 `settings.get` 的回包里**（与 `secret`/`core_path` 同一口径——UI 拿到的是四字段快照，不是原始文件）。字段为 `null` 的含义是"原本不存在"，还原时按删除处理，不写空串。

**扁平 snake_case，不嵌套 `controller: {host, port}`**（Task 4 落地裁定）：Task 8/10/11 全部用 `with` 改单个端口字段，嵌套会把每处改动变成 `s with { Controller = s.Controller with { Port = p } }`；且 Task 10 的跨进程 `SettingsPatch` 本就是扁平 `controller_port`，两处同形才不留翻译层。键名逐字段显式 `[JsonPropertyName]`，不靠命名策略推断；缺键必须回落到默认值（用户手改 settings.json 是预期通路）。`delay_test_url` 默认 `http://`（Task 1 探针用它调 `/delay` 实测出过延迟值，且与 UI 侧测速地址下拉、`handshake` 下发的 `delay_test_url` 同值）。

写入必须原子（同目录临时文件 + `File.Replace` 保留 `.bak`）。不放任何 phase 2 才用的占位字段。

### 4.4 界面偏好（UI 独占，`ApplicationData.GetForUnpackaged().LocalSettings`）

`backdrop` = `acrylic`(默认) · `mica` · `solid`；`theme` = `system` · `light` · `dark`；`window.{width,height,x,y,maximized}`；`proxies.show_hidden` = false。与 `settings.json` 分家，按写者划界，两棵真相树，不存在并发写。

### 4.5 更新源（GitHub releases）契约实测（Task 11 回写，2026-10-27 / win-x64）

`GET https://api.github.com/repos/MetaCubeX/mihomo/releases/latest`，真机核实：

- **带 `User-Agent` 是 GitHub API 文档要求，但"无 UA 直接 403"这条计划里的说法本机未复现**：`curl -H "User-Agent;"`（彻底不发该头）与默认 UA 两次请求都返回 **200**（2026-10-27）。UA 仍然带上、值 `ClashAcry`，作用是合规与标识来源，不是"没它就取不到"。**且 UA 按逐请求头设置、绝不挂 `DefaultRequestHeaders`**——沿用 §4.2 的 R-6a 纪律，避免污染共享 `HttpClient` 的其他通路。
- 真值样本（tag `v1.19.33`）：资产名形态 `mihomo-<os>-<arch>[-<go变体>]-<tag>.zip`。同一 tag 下 windows-amd64 一族有 **23 个**变体（`-v1-go120-` … `-v3-go125-`、`-compatible-`、裸 `-v1.19.33.zip`）。
  ⇒ 选包**必须按整名相等**匹配 `mihomo-windows-amd64-compatible-{tag}.zip`，回退 `mihomo-windows-amd64-{tag}.zip`；任何前缀/包含式匹配都会误选工具链变体（那一族体积还更大）。
- 资产 `size` 与真实字节数一致（compatible 包 `22543546`），`browser_download_url` 走 `302` → `objects.githubusercontent.com`，终响应 **`Content-Length: 22543546`、无 `Content-Encoding`**。⇒ 进度分母优先取响应的 `Content-Length`，缺失（分块传输 / 代理抹头）时回落到元数据 `size`。
- zip 内**不是** `mihomo.exe`：把 compatible 包整份取下来核对（22,543,546 字节压缩 / 解压后 61,304,320 字节），里面**只有一个条目**，名 `mihomo-windows-amd64-compatible.exe`——条目名跟随资产名。⇒ 解包按"首个 `.exe` 条目"定位，写入**调用方给定的目标路径**，绝不拿条目名拼路径（源站数据当路径 = 路径穿越面）。也⇒ 进度百分比只能按**压缩后的下载字节**算（见 §9）。
- **整包下载的真实耗时**（Task 11 Step 6 端到端，同一次真机 `core.update`）：compatible 包 22,543,546 字节在本机实测 **538 s**（约 1.9 MB/min）跑完，进度事件 **101 条、0..100 无重复无倒退**。⇒ 两条结论：① 更新通路**不许**用固定总时长兜底——`HttpCompletionOption.ResponseHeadersRead` 之下 `HttpClient.Timeout` 只约束到响应头，正文读取不受它管（这条 9 min 的下载在 5 min 的 client timeout 下照样活了下来）；护栏改成**静默预算 30 s**（连续 30 s 收不到任何一块才放弃，慢而活着就一直跑完）。② UI 侧的进度语义是"百分比 + 无 ETA"，慢 link 上 9 min 是合法耗时（§9）。

- **两道准入闸门（合并前评审 Critical 1，2026-10-27 补）**：元数据里**任何**字段都不得直接当路径或当地址用。
  ① 缓存文件名走 `CoreUpdater.SafeCachePath`：只接受「无非法字符 + `GetFileName` 等于自身 + 全路径仍在 `core/downloads` 内」的叶子名。
  今天 `PickAsset` 的整名相等判据已经让绝对形态拼不出来，但那是判据的巧合而非这道门的职责——判据哪天退回前缀匹配，路径必须自己站得住。
  ② 资产 URL 走 `ReleaseClient.IsTrustedDownloadUrl`：**https** 且 host **精确**命中 `github.com` / `objects.githubusercontent.com`
  （后缀匹配会被 `github.com.evil.test` 混进去）。拒在动网络与磁盘之前，语义仍是静默的 `DownloadFailed`。
  测试夹具的假源站在 `http://127.0.0.1`，所以构造缝 `downloadGuard` 由夹具注入"只放回环"，生产侧一律用默认判据。
- **在线通路没有摘要校验，这是裁定不是遗漏**：GitHub release 不随资产发布 sha256，从**同一个**已被攻陷的源再取一份校验文件等于没验；
  摘要校验只在内置内核那条路上成立（`packaging/core/BUNDLED.txt` 四行，`publish.ps1` 打包前 `-Verify`，且该文件**必须入库**——
  `.gitignore` 用 `packaging/core/*` + `!packaging/core/BUNDLED.txt`，写成整目录 ignore 会把溯源一起挡在库外，评审抓到过这个错）。

测试夹具的坑（同批实测）：WireMock.Net 2.20 的 `WithBody(byte[])` 会声明一个与实发字节数无关的 `Content-Length`（2,000,767 字节的包报成 `3987`），带 `contentType` 的重载则干脆不发这个头。夹具因此保留"无声明长度"形态，让分母走 `size` 那条回落路径。

要测"正文发到一半就断流"这条路径，WireMock 做不到，得用裸 `TcpListener` 手搓 HTTP 源站；此时**不读请求就 Close 会让 Windows 发 RST、把客户端还没读走的响应一起丢掉**（实测：正文完整写出，客户端 9 ms 内拿到连接重置）。夹具因此"发完正文后留 1 s 宽限期再断"。另外"既不声明长度也不分块、以 EOF 收尾"的响应会被 .NET 判协议违规——真实世界的未知长度形态是 `Transfer-Encoding: chunked`，夹具按后者写。

## 5. 分层与目录

```
ClashAcry.sln
src/ClashAcry.Core/        SettingsStore(原子写) · ConfigInjector · CoreManager(状态机+内核输出环+双探活)
                           SystemProxy(winreg+InternetSetOptionW) · MihomoClient(HttpClient)
                           CoreUpdater(GitHub release 挑选/下载/解包/轮换) · PipeProtocol / PipeServer / PipeClient
src/ClashAcry.Contracts/   管道消息与 DTO（UI ↔ Tray 唯一契约，无业务逻辑）
src/ClashAcry.Tray/        Win32NotifyIcon(手写) · 消息循环 · 管道服务 · 装配 Core
src/ClashAcry.UI/          App/MainWindow · Pages(Overview,Proxies,Settings) · ViewModels · BackdropController
packaging/core/            mihomo-windows-amd64-compatible.exe（随包内置）
scripts/                   measure-residency.ps1 · ui-acceptance.ps1
docs/superpowers/{specs,plans,checklists}/
```

`Core` 是不依赖任何 UI 框架的纯托管库——全部单元测试直接打它，不需要窗口。

## 6. v1 功能面

| 模块 | 内容 | 机制 |
|---|---|---|
| 内核托管 | 启停、重载、崩溃检测 + 启动时一次自动重试、内核输出环形 200 行（**stdout 为主**，实测 mihomo 的 Go 日志全走 stdout、stderr 恒空，两流各订阅一次进同一个环）、状态事件 | `CoreManager`：`Process` + 两路 `DataReceived`，`GET /version` 探活退避 |
| 配置导入 | 订阅 URL 下载 或 本地 yaml → 注入必需字段 → 写 `runtime\config.yaml` → 热切；失败保留旧配置 | `ConfigInjector`（只覆盖必需键，其余原样透传） |
| 节点面板 | 策略组/节点树、手动切换、单点测速 + 全组顺序测速（并发上限 4，结果就地更新） | `MihomoClient` 直连 §4.2 |
| Provider | 手动刷新，展示 `updatedAt` 与成员数；**列表按 `vehicleType` 过滤**（实测表内混有每组自动生成的 `Compatible` 伪 provider 与 `default`） | `GET/PUT /providers/proxies` |
| 开关 | 系统代理开/关；rule / global / direct 模式 | `SystemProxy` + `PATCH /configs {mode}` |
| 托盘 | 关窗即退 UI；Tray 常驻；快捷启停与切模式；退出清理 | Win32 托盘 + 管道 |
| 内核更新 | 内置一份 + 应用内检查/下载最新 release；进度百分比实时显示；旧版 `.bak` 轮换；`version.txt` 记 tag | `CoreUpdater` |
| 观感 | backdrop 三档即时切换、主题三档、页面 `ThemeTransition`、深色 chrome 逐类核对 | §8 |

**v1 明确不做**：连接列表与实时流量/日志（WS 全族）、规则编辑器、多配置/多订阅管理、`ss://`/`vmess://` 节点行导入、TUN、开机自启、MSIX、国际化（UI 仅中文）、主题色自定义（仅跟系统 light/dark）。

节点行导入的切分理由：它需要客户端生成 `proxy-providers` 骨架并猜 URI 归属，属"自造翻译层"，与 §7 原则冲突；yaml 订阅已覆盖主要用法。若你要保留这条，实现时改走引擎原生 provider 通路再评估。**base64 外壳不在此排除之列**：§6 承诺的「导入订阅 URL（Clash yaml）」指内容形态，分发时的 base64（含 url-safe 字母表与砍掉的 `=` 填充）属编码层，导入通路要先解码再嗅探——解出 Clash yaml 就正常导入并把**解码后**的文本存进 `imports\`，解出节点行仍按上面的排除项拒绝。

## 7. 数据流原则：mihomo 是唯一事实源

- UI 不镜像节点状态：任何写操作后重新 `GET /proxies` 全量重拉，并对刚操作项做乐观高亮。
- 自动测速、URL-test 间隔、分组、订阅解析一律交给 `config.yaml` 原生字段，客户端不重复实现。
- 注入最小化：只覆盖 `external-controller`、`secret`、`allow-lan: false`、`unified-delay: true`、`mixed-port`；其余字段不动、不排序、不格式化重写。
  **`mixed-port` 是无条件覆盖，不是"缺了才补"**（2026-10-27 真机回写，见 §12 ⑥）：系统代理与内核 mixed 探活用的都是 `settings.mixed_port`，订阅自带的端口一旦透传，就是"内核听 A、系统代理指 B"。
- 轮询/自动刷新不得产生可见抖动：列表 key 用节点名，数据无变化不重建可视树，数值就地更新。

## 8. Fluent / Acrylic 视觉方案

- **backdrop 三档**：`Acrylic`（默认，整窗亚克力——**实际装的是 XAML 层 `DesktopAcrylicBackdrop`，见本节末 Task 13 回写 ①**）· `Mica` · `纯色`。设置页单选即时生效，不重启：换 `Window.SystemBackdrop` 实例并触发一次重挂。
- **强制降级**：系统"个性化 → 颜色 → 透明效果"关闭、远程桌面会话、`DesktopAcrylicController.IsSupported()` 返回假这三种情况下，一律自动落纯色，并在设置项上明写"当前实际生效：纯色（系统透明已关闭）"。绝不静默假装还是亚克力。探测逻辑（读 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\EnableTransparency` + `IsSupported()`）由任务 0 探针实测确认可行性，不成立则改用等价的系统事件监听。
- **层级材质**：卡片/浮层用 `SystemBackdropElement` 或 `AcrylicBrush`（薄层），内容层保持 `SolidBackgroundFillColorBase` 系，保证文字对比度不靠猜。
- **主题**：`ElementTheme` 三档跟随系统/light/dark。深色下控件 chrome **逐类核对**（`Button`/`ComboBox`/`ComboBoxItem`/`ListBoxItem`/`ScrollBar` 含 `Track.Thumb` 与翻页 `RepeatButton`，每类都要检查默认模板是否残留浅色），验收判据用像素取样而非肉眼看图。**Task 16 实测：默认模板没有浅色残留，逐类模板覆盖不需要写；判据固化成 `ChromeContrastTests` 的像素阈值，见本节末回写。**
- **动效范围**：v1 只做 `ThemeTransition` + 页面切换转场 + 按钮 pressed/pointerover 默认态。connected animation、弹簧自定义曲线推迟。

**backdrop 落地形态与降级证据（Task 13 回写，2026-10-27 / win-x64 / Windows App SDK 2.5.1）**：

① **装的是 XAML 层 backdrop，不是 composition 控制器**。本节原写"整窗 `DesktopAcrylicController` / `MicaController`"，实测在 .NET 投影里挂不上去：`Microsoft.UI.Xaml.Media.ISystemBackdrop` 是不可访问类型（CS0122），控制器赋给 `Window.SystemBackdrop` 报 CS0029、`AddSystemBackdropConfiguration` 不可见（CS1061），`Microsoft.UI.Composition.SystemBackdrops.ISystemBackdrop` 干脆不存在（CS0246）。实际形态是 `Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop` / `MicaBackdrop`（`Window.SystemBackdrop` 的属性类型就是它们的公共基类 `SystemBackdrop`）。**代价**：拿不到 `SystemBackdropConfiguration`，材质明暗只能靠根元素 `RequestedTheme` 传导（本项目的主题三档正是这么生效的），也不需要 `Activated` 挂钩与 `Dispose` 生命周期——失焦停算归框架管。控制器唯一还需要的地方是 `IsSupported()` 探测，那是静态方法、不要求能实例化。
② **判级逻辑在 Core，纯函数**：`BackdropPolicy.Resolve(requested, systemAllowsTransparency, acrylicSupported, micaSupported)` → `(Effective, Degraded, Reason)`，三开关交叉 8 例 + 默认档不静默降档 1 例（`BackdropPolicyTests`，9/9）。UI 侧只负责"把结果装到窗口上 + 写标注"。文案口径固定：`当前实际生效：{亚克力|Mica|纯色}`，降级时追加`（{系统透明效果已关闭 | 当前设备不支持亚克力 | 当前设备不支持 Mica}）`；**纯色是请求档时不算降级、不带括号**。
③ **探测判据实测**：HKCU `…\Themes\Personalize\EnableTransparency` 可读（本机 0x1，测试全程只读），**缺键按"允许"处理**——老系统或被策略删键不等于用户关了透明，判成关会让默认亚克力凭空降级。
④ **降级分支靠注入驱动，不改用户机器**：`BackdropService` 的三个 `Func<bool>` 是可注入缝，UI 的 `--force-degrade` 把"系统允许"注成假，用来断言降级文案；真机上手动关闭透明效果的那项目视仍留在验收清单。**远程桌面会话没有单独判据**：v1 依赖 `IsSupported()` 在 RDP 下的表现（本机未实测），Task 18 冒烟若发现 RDP 下仍挂上材质，则回本节补显式判据。

**深色 chrome 逐类实测（Task 16 回写，2026-10-27 / win-x64 / 125% 缩放 2560x1440）**：

本节要求"检查默认模板是否残留浅色"这条**实测为否证**：Windows App SDK 2.5.1 的默认 Fluent 深色模板下，逐类取样全部已经是深色，`Themes/FluentDarkOverrides.xaml` 与逐类 `ControlTemplate` **一个都没写**（计划 Step 2 划掉）。下表是 Task 16 的**单点取样**读数（3x3 邻域最亮像素），Task 18 把判据形态换成了区域统计（见下面「阈值三常数」段末），表里的值仍是同一批控件、同一台机器，只是量的口径变细：**Task 18 复测的底色 P25 落在 48~70、亮特征波段最大值落在 204~255**：

| 部件 | 底色 | 前景 |
|---|---|---|
| 页面底 / 卡片（`OverviewPage`·`SettingsPage`·`ProxiesPage`） | 46~57 | — |
| `Button`（更新/导入/全组测速）边框 46~71，中心 | — | 255（文字） |
| `RadioButton`（模式三档、材质三档、主题三档） | 51~87 | — |
| `TextBox` 输入区 | 35 | — |
| `ComboBox` 底 61 · chevron | — | 255（文字）· **203 @fx=0.94**（不是计划写的 0.92） |
| `ComboBoxItem` 行（弹层） | 45~58 | 255 |
| 节点行 / 组行 | 46~57 | 255 |
| `ScrollBar` 轨道 48 · 翻页 `RepeatButton`（`VerticalSmallDecrease` 等 4 件）46 | — | 滑块芯 **161** |

阈值三常数（`ChromeContrastTests`）：`DarkMax=110`（实测深色 39~88，留余量）、`ForegroundMin=140`（实测前景 160~255）、`SpanMin=60`（滑块-轨道实测差 113）。**极性必须分开判**：底色要求"最亮处仍暗"，前景要求"最亮处够亮"——一条 `AssertDark` 通吃会把正确的深色主题里白色的箭头/文字误判成浅色残留。

**判据形态在 Task 18 从"单点"改成"区域统计"（阈值一条没放宽，`ChromeProbe.BandStats`）**：同一份代码在跑全量套件时随机红过三种样子，逐条量过渲染像素才确认根因是**取样形态**而不是主题发白——
① 带 `Header` 的 `TextBox` 的 UIA 矩形把标签行与字段一起框住（实测 771x75），**占位符字脊 238 与字段底 35 只隔 5 px**，而 `ChromeProbe.Sample` 取的是 3x3 邻域**最亮**那颗：布局漂几个像素就从"判底"翻成"判字"（`ImportUrlBox` 读到 238、判据 ≤110）。
② 反过来节点行名单点读到 47（判据 ≥140）——落在名字第一道竖上，最长一段连续亮只有 7 px。
③ 深色底是**压在 Desktop Acrylic 上的半透明填充**，同一点位会随背景漂（实测 35→56 而矩形没变），单点阈值本质上守不住。
所以底色改用区域第 25 百分位（实测深色档 48~70、浅色档 240 上下）、亮特征改用波段最大值 + 与底色的跨度，并在抓帧前加 `WaitLayoutSettled`（连续三次矩形一致）。**同时补一条反证** `FillCriterion_LightThemeReallyReadsLight`：主题单选真的切到浅色后同一量法必须读到发白，否则"≤110"是在自说自话（收尾切回跟随系统——那是用户的 LocalSettings 偏好）。

四条落地约束（都是踩出来的，下次改 UI 直接照做）：

① **`PrintWindow(PW_RENDERFULLCONTENT)` 抓不到 WinUI 的 `Popup`**：ComboBox 下拉、Flyout 落在独立的 `Microsoft.UI.Content.DesktopChildSiteBridge` 里、不是独立顶层 HWND，PrintWindow 只渲主窗合成面 → 弹层项抓出来是背后的页面。弹层类（`ComboBoxItem`）只能整屏 `CopyFromScreen`，且判据要先断言"屏幕坐标来自 UIA、换算零偏移"。
② **DPI 一致性是像素核对的前置条件**：UIA 矩形是物理像素，抓取进程若 unaware 则拿到虚拟化位图，125% 下差 1.25 倍——`ComboBoxItem` 全部读成同一个暗值、看似通过实则错位。测试进程用 `[ModuleInitializer]` 调 `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`（**不能放静态构造**：进程中途翻转感知会让后面的用例取到全透明像素）；产品侧 `ScreenCapture.Grab` 直接按缩放比断言失败，把"环境不对"变成显式失败而不是假绿。
③ **滚动条没有 `Thumb` 元素，而且分紧凑/展开两态**：UIA 只暴露 4 个 `RepeatButton`（`VerticalSmallDecrease`/`VerticalLargeDecrease`/`VerticalLargeIncrease`/`VerticalSmallIncrease`，**已自带 AutomationId，无需模板补 id**——计划 R-15 交接项①里"滚动条本身还没有 id"这条作废），且 `VerticalScrollBarVisibility="Auto"` 下整条在显形前报 `0x0 off=True`。
   **Task 18 把两个因子拆开测过，订正 Task 16 那句"指针进右缘 2px 停 ~700ms + 一次滚轮"**：
   ① 显形**不必**碰真鼠标——`ScrollPattern.SetScrollPercent` 单独就能让条出现（第一次轮询即命中）；
   ② 但那是**紧凑态**：实测 **3px 宽、整列 161、UIA 部件为空**（既没有轨道也没有翻页件），并且约 1 s 后自己收回 0×0——拿紧凑态当"轨道 vs 滑块"量就是在量滑块本身，这正是 Task 18 那条"轨道最暗处读到 161"红点的根因；落点也必须在滚完之后**立刻**按下，先滚完再等 1 s 才动指针就变成在空气上悬停（实测算出 `y=0`）；
   ③ **展开**到 15px（轨道 + 4 件）只在"指针压在条上 + 真有滚轮进来"时发生：光 `SetCursorPos` 悬停（±1px 抖动、10 个落点、12 轮）和光程序化滚动都停在药丸。滚轮打在指针下的窗口上，所以发之前先 `WindowFromPoint` + **属主 PID** 验明落点确实是本测试起的 UI 进程，不是它就一个字节都不发（旧版没有这道护栏，失败现场是"指针下窗口=别家 HWND、前台=False"，滚轮滚到了用户别的界面上）。顺带一条取证口径：`WindowFromPoint` 在客户区返回的是 `Microsoft.UI.Content.DesktopChildSiteBridge` 子窗口，拿它跟顶层 `HWND` 直接比会误判成别家，要比 PID。
   滑块在 15px 宽的条身里只占中间约 6px，横向剖面是 `48 48 48 48 48 48 48 62 118 161 161 161 118 62 48`——**fx=0.5 落在半透明边缘（62）**，所以量法是 `ChromeProbe.ScanThumb`：先粗扫 15 列找最亮列，再沿该列竖扫量轨道（48）/滑块芯（161）/滑块行数（约 30 行）。
④ **`AppWindow.Resize` 吃物理像素**：PerMonitorV2 之后按字面写 `1000x680` 会变成 800x544 DIP（界面小一圈）。现按 `GetDpiForWindow` 换算，设计稿的 1000x680 保持为 **DIP** 意图。

## 9. 错误处理



| 类别 | 场景 | 策略 |
|---|---|---|
| 内核启动失败 | controller 端口占用、yaml 语法错 | 内核输出环形缓冲（stdout+stderr），UI 展示 Go 原文**不翻译**；仅启动时自动重试一次，重试仍失败落 `Crashed` 并保留原因。**窗口分两档**：controller 未应答且进程还活着 = 仍在启动（下 geodata 属于这一档），宽限 120 s；controller 已应答而 mixed 不跟进 = 带病存活，仍 5 s 判死——见下面「启动窗口分档」 |
| 状态失真 | mixed 端口被占但 controller 正常，内核活着却不通 | `Running` 判据 = `GET /version` 成功 **且** mixed 端口 TCP 可连；任一不符不进入 `Running`，横幅报"端口 7897 被占用" |
| API 断连 | secret 不符、controller 未监听、Tray 掉线 | `MihomoClient` 透出真实状态码；UI 顶部横幅态 + 写操作按钮置灰；管道断开则提示一键重启 Tray |
| 系统代理残留 | UI/Tray 被强杀后注册表悬挂 | 三层：正常退出钩子必清；Tray 启动时校验"注册表代理指向本应用端口且探活失败"才清（指向其他软件的代理**绝不触碰**）；托盘"停止内核"顺带清 |
| 系统代理**载荷**被我们覆盖 | `Enable()` 无条件写 `ProxyServer`/`ProxyOverride`，关掉开关并不换回去（R-20b，第四轮真机吃到） | 开启前把两键原值快照进 `settings.json` 的 `sys_proxy_backup`，关闭时**只在当前值仍等于我们写的那份**才还原（第三方在我们之后写过就不动，与 R-7c 同向）；强杀那条路由冷启动的 `CleanupIfStale` 返回值触发同一次还原 |
| 订阅下载失败 | 网络/源站错误/非 yaml 响应 | 保留旧 `config.yaml` + 横幅报错；**永不清空配置** |
| 内核更新失败 | 下载中断、zip 解不出 exe、目标文件被占用 | 缓存已下载 zip 供重试跳过网络；替换失败退回原 `.exe` 并保持内核运行；全程不阻塞 UI（进度事件推 `core.update.progress`），拒绝阻塞式弹窗 |

**已知限制（Task 7 回写）**：`CleanupIfStale` 比对的是**当前**设置里的 `mixed_port`，若用户在系统代理还指向旧端口时改了 `mixed_port`，那份指向旧端口的悬挂值 v1 不会清掉（换端口后重新开启一次系统代理即覆盖）。

**取消语义（Task 8 回写）**：`StartAsync` 的 `CancellationToken` 一旦被调用方触发，**不算启动失败**——不落 `Crashed`、不进"自动重试一次"的循环。行为固定为三步，顺序不可换：① 杀掉本已 spawn 的 mihomo 进程（绝不留孤儿内核）；② `Transition(Stopped)` 并保留"启动被取消"作为原因；③ 原样抛 `OperationCanceledException`（吞掉就等于伪造业务结论，让调用方以为还能重试）。实现上这条 catch 必须在通用 `catch (Exception)` **之前**，否则取消会被归类成启动异常。

**探活成本实测（Task 8 回写，2026-10-26 / win-x64）**：mixed 端口的 TCP 探活对**没人监听的 loopback 端口**发起 connect，本机约 **2.03 s** 才拿到拒绝（控制器独立复现：`TcpClient.ConnectAsync` 与同步 `Socket.Connect` 各三次，2027~2044 ms，无预热效应；**真在听的端口 0 ms**）。原写法 `for (i < 20) { Delay(250); ... TcpAlive(1s 预算) }` 里"每轮 250 ms"只是**间隔**、不是预算——每轮还要付一次探活，于是名义 5 s 的窗口实测 25 s/次尝试、50 s/两次尝试（回归用例 `Start_DoesNotReachRunning_WhenControllerAliveButMixedPortDead` 实测 50 s）。这不是慢而是**说谎**：本节承诺"5 s 内未就绪"，实现给了 25 s，UI 会把"内核起不来"渲染成 25 秒假死。**裁定 R-8i**：① 探活窗口按**墙上时间**计（`Stopwatch` 起表、`Elapsed < 5 s` 继续轮），不许用固定轮数当预算——轮数 × 间隔在任何一次探活变慢时都会失真；② `TcpAlive` 单次超时 **250 ms**（loopback 上活监听者 0 ms 应答，250 ms 是三个数量级余量），这同时把 controller 占用预检的最坏成本从 1 s 压到 250 ms；③ 失败文案仍写"5 s 内未就绪"，改完它才名副其实。净效果：单次探活一轮 ≤ ~500 ms、窗口 5 s 封顶；一次失败的 `StartAsync`（自动重试一次 = 两个 5 s 窗口串在同一次调用里）实测 ~10.3 s，取代原来的 ~50 s。回归用例 `Start_DoesNotReachRunning_...` 用 `Stopwatch` 夹住 `StartAsync` 断言 **`elapsed < 12 s`**（阈值不是 8 s：8 s 漏算了"失败自动重试一次"会把两个窗口串联，正确实现下也要 ~10.3 s，会被自己的锁判红）。全套 Core.Tests 时长随之从 1 m 5 s 降到 25 s。双探活判据本身不改；`Starting` 期间 UI 仍必须显示"正在探活"（§11 与 AGENTS.md：长耗时操作必须有进行中反馈），只是不再有失真窗口。**遗留**：`KillProcess` 的 `catch { }` 理论上能吞掉"树杀没成功"却广播 `Stopped`（可能留下占端口的孤儿内核）；v1 不加特判——同一用户同一安全上下文下 `Kill(entireProcessTree)` 对自己子进程返回拒绝访问无可达路径，且回归用例 `DisposeAsync_KillsCoreAndLeavesStopped` 已用真 PID 在进程表里等它消失，把"说 Stopped 就得真没了"钉成可执行断言。若将来引入提权运行或外部看管进程，这条重开。

**进度语义（Task 11 回写，裁定 R-11c 落地口径）**：`core.update.progress` 的百分比是**下载进度**，不是"解包/替换"进度——分母优先取响应 `Content-Length`，缺失时回落到 release 元数据的资产 `size`（§4.5），两者都拿不到就**不报中间档**，只在读满后补一次 100% 收尾。节流判据是"整数百分比变了没有"：同一档位只发一帧，2 MB 包实测 25 帧、`pct` 单调不减且去重（回归用例 `Progress_IsMonotonic_Throttled_AndEndsAt100`）。缓存命中（`core\downloads\<资产名>` 存在**且能解出 `.exe` 条目**）完全跳过网络、也就没有进度帧——这不是缺反馈，那条路径本来就是瞬时的。结果帧 `core.update.result` 必发，UI 的"…中"才有终点。**UI 侧的同一口径（Task 14 落地）**：按钮文案里的百分号**留到结果帧**才收回，`100%` 不解锁按钮、也不把版本号那行改成进度数字——下载满格后面还在解包换文件，此刻收回反馈等于骗人；版本号只在结果帧之后**重新握手**取 `core_version`（回归锁 `UpdateClick_DisablesButton_ShowsPercent_AndRestoresOnResult` 同时钉住"100% 仍在进行中"和"版本来自握手"）。更新期间概览页的 2 s 轮询整体暂停（裁定 R-14c）：`PipeServer` 对**同一条连接**的请求是串行处理的，一条分钟级的 `core.update` 会把后面的 `settings.get` 全堵在它后面；进度与结果走广播那条独立写路径，不受影响。

**失败分类（Task 11 回写）**：源站取不到 / 没有匹配当前平台的资产 / 传输失败 → `DownloadFailed`；包能下下来却解不出 `.exe` 条目 → `ExtractFailed`（两种失败不混，否则"源站挂了"和"上游发了个空包"在 UI 上是同一句话）；目标 exe 被运行中的内核占用 → 备份已落 `.bak`、换文件抛 `IOException` → `InUseRolledBack`，**`version.txt` 不写**（写了下次就以为已是新版本，用户永久卡在坏版本）。`AlreadyLatest` 在任何联网动作之前判定，零下载零换文件。更新器全程不碰内核进程、不改 `CoreState`（§3 禁令 1）：停核 / 换文件 / 起核由 `CoreManager.SwapExecutableAsync` 在生命周期锁内完成。

**换文件的四条真机订正（Task 11 Step 6 端到端回写）**：
① **旧 exe 不存在不是"被占用"**——`FileNotFoundException` 是 `IOException` 的子类，备份那步的 `File.Copy` 在没有旧文件时抛它，于是被折成 `InUseRolledBack`：用户看到"内核被占用，已回滚"，而机器上根本没有可占用的内核。没有旧文件就没有要备份的东西 ⇒ 跳过备份直接把新核装进去（在线更新顺带修好这种坏安装），且不留下路不明的 `.bak`。
② **换之前没在跑，就绝不因为换核把它拉起来**；回滚分支同理。`SwapOutcome.CoreRunningAfter` 报告的是换完之后**真的**在不在跑，不是"操作成功"——第一版在这里既多起了一个用户没要的进程，又把 `Done(true)` 说成了假话。
③ **换核成功后的 controller 版本读取必须容错**：更新成功 ≠ 内核在跑（见②），那句裸调 `GetVersionAsync` 实测把 `connection refused` 抛穿到回帧——`Updated` 和 `core.update.result` 事件一起丢了，只剩一句 socket 错误。版本读不到就保持 `settings.core_version` 原值（展示位，内核真跑起来后 `handshake` 带真版本），结果仍是 `Updated`。
④ `InUseRolledBack` 的回滚依据不是"用 `.bak` 还原"，而是**第二次 Copy 失败 ⇒ 旧 exe 从头到尾没被动过**；`.bak` 只是留证。

**测试与真机宿主的互斥（同批实测）**：Core 测试用的管名就是生产管名 `clashacry.tray.v1`，真 Tray 宿主在跑时 `PipeRoundTripTests` 的 9 条用例会成片失败（自己的服务端建不起来 / `WaitsUp` 探到了真宿主）。跑套件前先确认没有 `ClashAcry.Tray.exe` 存活；反之，端到端验证也要在同一次跑里避开套件。

**启动窗口分档（真机回写，2026-10-27 / 用户机场订阅 + v1.19.33）**：上面那条 5 s 窗口被真实订阅打穿了，而且打穿的方式不是"内核起不来"，是**我们把它掐死了再判它起不来**。链路：订阅里一条 `GEOIP,CN,DIRECT` ⇒ mihomo 必须先有 `geoip.metadb` 才继续解析配置 ⇒ 本机没有就当场从 geodata 源站下载，**实测它自己给这次下载留的截止正好是 90 s**（`17:49:52` 打 `Can't find MMDB, start download`，`17:51:22` 抛 `can't download MMDB: context deadline exceeded` 后 fatal 退出）⇒ 我们的窗口是 5 s，于是第 5 秒 `KillProcess` 砍掉正在下载进程，留下 1–3 MB 的半截 `geoip.metadb` ⇒ 下一次启动读到 `MMDB invalid, remove and download`，重下、再被砍。两次尝试 × 5 s 全砍在同一位置，用户看到的是"导入成功、内核永远起不来"，而日志一行错误都没有（错误行是 fatal 才出现的，我们根本没活到那一刻）。
**裁定**：窗口按**证据**分两档，而不是把 5 s 单纯放宽——**controller 应答 = 配置已经解析完**，这是唯一能区分"还在启动"和"带病存活"的信号。
① controller **未**应答且内核进程还活着 ⇒ 仍在启动期，宽限到 `_startGrace`（默认 **120 s** = mihomo 自留的 90 s + 余量）；跨过原 5 s 快档时广播一次 `Starting` 带原因（"内核仍在启动……首次要下 geo 数据库"），这是 §11 的进行中反馈要求，不许让界面停在"启动中…"三个字上什么都不说。
② controller **已**应答而 mixed 端口不跟进 ⇒ 就是本节上面那条"带病存活"，仍按 `_fast_window`（5 s）判死，**不放宽**——这时候没有任何"还在忙"的解释。
③ 宽限到期或进程自己退出 ⇒ 照旧 `Crashed` + `KillProcess`，不留孤儿。失败文案里的秒数取**实际生效的那一档**（`controller=ok` 时报 5 s，否则报 120 s），不再硬写 "5s"。
回归锁：`Start_SlowBoot_PastFastWindow_KeepsWaiting_AndReachesRunning`（快档 300 ms / 宽限 5 s，controller 在 900 ms 才应答 ⇒ 必须走到 `Running` 且 `SpawnCount==1`；把宽限档退回快档这条立刻变红，已实测）与 `Start_ControllerNeverBoots_GraceExpires_CrashesWithGraceInReason`（钉住"宽限不是无限等"）。`Start_DoesNotReachRunning_WhenControllerAliveButMixedPortDead` 的 `< 12 s` 判据不受影响：它走的是②档，controller 从一开始就应答。**代价记账**：一条 `core.start` 命令在最坏情况下会占住这条管道连接 2×120 s（`PipeServer` 同连接串行，§12 遗留②的同一口径），期间 UI 的 2 s 轮询排队等待——首次下库是一次性成本，且界面靠广播仍在动，v1 接受；日后要么 geodata 随包分发（§12 ⑦），要么把 `core.start` 改成"立即回执 + 状态广播"。
**同源的一条**：`SubscriptionImport` 现在会拒掉 `proxies: []` 且无 `proxy-providers` 的订阅（见 §12 ⑥），半截 metadb 那个坑则靠上面的分档消除；本机那份完整文件是 8,426,776 字节。

**内置内核第一次真跑（Task 17 回写，v1.19.33 + 发布产物）**：`core.start` 起来的是**空默认配置的内核**——它监听 7890、连 `external-controller` 都没有，双探活 `controller=fail mixed(7897)=fail`，5 s 窗口内不落 `Running`，两次尝试后广播 `Crashed`，而内核日志里没有一行错误。根因是默认启动参数写的是 `-d "<paths.Root>"`：mihomo 在 `-d` 目录里找 `config.yaml`，注入产物只写 `AppPaths.ConfigFile`（= `RuntimeDir\config.yaml`），Root 下没有配置它就按内置默认起。§3 的 `spawn(-d <runtime>)` 从第一天起就是这个意思，TrayHost 的注释也这么写，只有实现指错了目录。修在 `CoreManager.DefaultLaunch`（提到 static，`-d "<RuntimeDir>"`），回归锁 `DefaultLaunch_PointsAtTheDirThatActuallyHoldsConfigYaml` 的判据是「从 Arguments 解出来的那个目录里必须真能看见 `config.yaml`」而不是死字符串比对——之前 15 条 `CoreManagerTests` 全部注入假 launchFactory（`.cmd` 脚本，根本不看 `-d`），默认分支一次都没被真配置跑过，这是"假夹具挡住真缺陷"的教科书样本。修完的同一条通路：`RESTful API listening at: 127.0.0.1:9097` + `Mixed(http+socks) proxy listening at: 127.0.0.1:7897`，握手回 `state:"Running"`，`core.stop` 后进程从进程表消失，宿主前后 HKCU 代理两项逐字未变。

## 10. 常驻开销目标（验收判据）

| 形态 | 目标 | 测量 | 实测（空壳，2026-10-26 / Task 2） |
|---|---|---|---|
| **Tray 空闲态**（没内核在跑：冷启动后、或内核已停） | Tray ≤ **40 MB** | `scripts/measure-residency.ps1`：`Get-Process` 采 `WorkingSet64` 与 `PrivateMemorySize64` 各 5 次取中位数 | Tray 空壳（self-contained 未 trim）WS **16.4 MB** / Private **4.6 MB**（三轮各取中位数：17.0 / 16.4 / 16.4）；trim 版 15.9 MB。**Task 17 满载产物**：空闲 31.4 / Private 7.2，连开 5 次管道会话后 37.4~37.8 ⇒ 在线内 |
| **日常常驻（满载）**：Tray + mihomo 在跑（UI 已关） | **合计 ≤ 80 MB**（Tray 自身在这一格不设 40 线，见下面的裁定） | 同上，两个进程各采一次并求和 | Task 17 真内核 3 分钟 soak：Tray 49.8→51.5 / mihomo 26.1→26.5 ⇒ **合计 74.0~77.5 MB**，线内但只剩 2.5~6 MB 余量 |
| UI 打开（Acrylic 档） | UI 进程 ≤ **220 MB**，Tray+内核+UI 合计 ≤ 260 MB | 同上，另记纯色档差值用于量化整窗 Acrylic 的成本 | Task 12 外壳（NavigationView + 空页，未上 Acrylic）WS **140.4 MB** / Private **86.5 MB**；Task 17 最坏档（节点页 30 行全落地 + 刚跑完全组测速）**180.0 / 104.9**，再轮询 20 s 回落 178.3 ⇒ 线内余量 40 MB |

**判据口径的裁定（2026-10-27，用户批准"按空闲和满载拆两格执行"）**：原来那一行"日常常驻：Tray ≤ 40 MB，合计 ≤ 80 MB"把两种形态压在同一个 40 上，而 40 是 Task 2 在什么都没装的空壳（16.4 MB）上定的。Task 17 的归因数据说明这是**错位而不是严格**：顶穿 40 的驱动项是「内核在 Tray 里跑起来」（`core.start` 后、界面一个字节都没发的采样点 49.0 MB；连开 5 次管道会话只有 37.8，3 次连续界面会话 40.2~45.1），且 WS +17.6 而 Private 只 +5.2、`DOTNET_GCHardLimit=32MB` 实测一格没动 ⇒ 多出来的是映像共享页与已触碰未归还的页，**不是可裁掉的托管堆**（trim 路线另有实测判死，见本节末 Task 17 段④）。拆两格之后**一个数字都没放宽**：空闲那格仍按 40 判（实测 31.4 / 37.8，在线内），满载那格由本就在的合计线 80 判（实测 74.0~77.5）。UI 的 220 与三进程的 260 不动。

数字原本是**待实测校准的初始线**（任务 0 探针先出空壳实测，若 WinUI 3 空壳常态即超线就回来改档默认值并记录，而不是悄悄放宽判据）——**Task 18 定稿**：三格全部有真机实测（Tray 空闲 31.4/37.8、满载合计 74.0~77.5、UI 最坏档 180.0），三条线一条没放宽；唯一动过的是**口径**（40 只管空闲态，见上面的裁定），且那次改动经用户批准。

**Task 2 基线复现命令**（本机 .NET SDK 10.0.203，win-x64，Release）：

```
dotnet publish src/ClashAcry.Tray -c Release -r win-x64 --self-contained
Start-Process src\ClashAcry.Tray\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\ClashAcry.Tray.exe -PassThru
powershell -File scripts/measure-residency.ps1 -ProcessName ClashAcry.Tray -Json   # → {"name":"ClashAcry.Tray","working_set_mb":16.4,"private_mb":4.6}
Stop-Process -Name ClashAcry.Tray   # 测完必须杀进程，否则污染后续测量
```

**Task 10 复测（2026-10-27，真宿主：托盘 + 管道 + 路由 + tooltip 泵，self-contained 发布产物）**：

| 采样点 | WS | Private |
|---|---|---|
| 起来 30 s（空闲） | 36.2 MB | 9.7 MB |
| 起来 90 s（空闲） | 37.7 MB | 10.4 MB |
| 连开 5 次管道会话后 | 45.4 MB | 15.8 MB |
| 之后再静置 30 s | 39.3 MB | 9.7 MB |

判据结论：**按字面不过**——空壳基线的 16.4 MB 是「什么都没装」的数字，装上 SettingsStore / HttpClient / 管道 / System.Text.Json 之后 WS 常态 37–45 MB，正好压在 40 MB 线上、被管道活动顶穿。三条已排除的坏味道：① 不是泄漏（连开 33 次会话后仍是 44.7 MB / Private 13.9 MB，回落后 Private 回到 9.7 MB）；② 不是 GC 配置问题（`ServerGarbageCollection=false`、`ConcurrentGarbageCollection=false` 已生效，Private 只有 ~10 MB，多出来的是 self-contained 运行时的**映像共享页**，算在 WS 里）；③ 合计线仍守住（45 + mihomo ≈ 25 MB ≈ 70 MB ≤ 80 MB）。
处理：不放宽判据，瘦身归到 Task 17（发布产物）一起做——`PublishTrimmed`（§11 探针第 4 条本来就要求它）+ ReadyToRun + `JsonSerializer` 源生成，届时以「管道活动后 WS ≤ 40 MB」为复测口径。

**Task 13 复测（整窗亚克力的成本，本节上一版留的账）**：同一 UI 外壳、同一采样法（各 5 次取中位数），**亚克力档 WS 145.6 MB / 纯色档 WS 145.9 MB** ⇒ 外壳阶段整窗亚克力**测不出额外开销**，差值在噪声内；UI 的内存账基本全是 XAML/WinUI 运行时本身，不是材质。与 Task 12 的 140.4 MB 不矛盾：那是还没装设置页与材质时的同一外壳。220 MB 线守住（余量 ~75 MB），真实控件（Task 14/15）落地后复测。

**Task 15 复测（真实控件落地后，即上面留的账）**：同一 self-contained 产物、同一采样法。Tray 空闲 **WS 33.6 MB / Private 8.3 MB**，UI 连上并跑过节点页轮询后 **WS 38.2 MB / Private 9.6 MB**；UI **WS 150.6 MB / Private 90.1 MB**（对照 Task 12 外壳基线 140.4 / 86.5，装了三个页面 + 亚克力的总代价 +10.2 MB WS）。三条线全部守住：Tray 40（Task 10 那次被管道顶穿到 45 的现象本轮没有复现，但**管道活动后的采样口径仍欠**，见下）、Tray+内核 80（38.2 + mihomo ≈ 25 ≈ 63）、UI 220（余量 ~69 MB）。
本轮采样的**两处口径不足，Task 17 必须补齐**：① 采 UI 时节点页**没有打开**，即"真实控件 + 一屏几十行节点 + 一次全组测速"这一档还没进过账；② 采 Tray 时**没有跑管道风暴**（Task 10 顶穿到 45.4 MB 的那一档）。发布产物与 trim 一并处理，届时以「节点页可见且刚测速完的 UI ≤ 220 MB」+「连开 5 次管道会话后 Tray ≤ 40 MB」双口径复测。
另记：本轮**未启动真实 mihomo**（`mihomo: none`），全部界面行为打在 FakeTray + WireMock 上，用户机器上同时在跑的另一个 clash 客户端（verge-mihomo，可能占 7897/9097）与它的系统代理设置全程未被触碰。

**Task 16 复测（改的是 DPI，不是控件；2026-10-27，同一 `--self-contained` 构建产物、各 5 次取中位数）**：Tray 空闲 **WS 34 MB / Private 8.5 MB**，UI 连上后 **WS 150.7 MB / Private 89.8 MB**（对照 Task 15 的 150.6 / 90.1：PerMonitorV2 + 按 DPI 换算窗口尺寸**没有可测代价**，仍在 220 线内）。宿主前后 HKCU `ProxyEnable=0`/`ProxyServer=127.0.0.1:8090`/`ProxyOverride` 逐字未变，真实 mihomo 未启动。**但 Tray 在 UI 会话后读到 WS 42.1 MB / Private 13.4 MB——40 MB 线又被管道活动顶穿**（Task 10 的 45.4 MB 同一现象，Task 15 那次 38.2 MB 是采样时点不同，不是问题消了）。判据不放宽：Task 17 的 trim + ReadyToRun 复测口径就是「连开 5 次管道会话后 Tray ≤ 40 MB」，做不到就停下来报告。

**Task 17 复测（2026-10-27，发布产物 `artifacts/unzipped`，self-contained 未 trim；这一轮第一次真跑内置内核）**：

| 采样点 | Tray WS/Private | mihomo WS/Private | UI WS/Private |
|---|---|---|---|
| 空闲（无内核、无界面会话） | 31.4 / 7.2 | — | — |
| 连开 5 次管道会话后 | 37.4~37.8 / ~10 | — | — |
| 3 次连续界面会话（每次 20 s，同一 Tray） | 会话中 40.4~42.2 / 11~13.6，会话后 40.2~45.1 / 9.8~15.7 | — | — |
| **内核在跑，界面不开** | **49.0 / 12.4** | 25.1 / 60.0 | — |
| 内核在跑 + 界面挂着轮询（3 分钟 soak，每 30 s 采一次） | 49.8→51.5 / 12.2~13.5，**平台不单调** | 26.1→26.5 / 60.5~61.0 | 158~161 / 91~96 |
| 节点页可见（30 行全落地）+ 刚跑完全组测速（FakeTray+WireMock，构建产物） | — | — | **180.0 / 104.9**，再轮询 20 s 回落 178.3 |

判据结论，三条分开说：
① **UI ≤ 220 MB 守住**，最坏档（节点页 30 行 + 全组测速刚结束）180 MB，余量 40 MB；Task 15 留的"节点页没打开"那格账补齐了。
② **Tray+内核 ≤ 80 MB 守住但很紧**：soak 期间合计 74.0~77.5 MB，mihomo 自身 WS 只有 26 MB（Private 60 MB 是 Go 预留的堆，不进 WS 口径）。
③ **Tray ≤ 40 MB 按字面不过，且这次找到了真正的驱动项**：顶穿 40 线的**不是管道活动**（5 次会话 37.8，界面会话 40~42），而是**内核在 Tray 里跑起来**——`core.start` 之后、界面一个字节都没发的采样点是 **49.0 MB**。此前每一轮（Task 10/15/16）都没启动过真内核，所以这条线一直被误判成"管道顶穿"。归因数据：WS +17.6 MB 而 Private 只 +5.2 MB ⇒ 多出来的是**映像共享页与已触碰未归还的页**，不是托管堆；把 `DOTNET_GCHeapHardLimit` 压到 32 MB 再测，四个采样点 48.9/49.9/49.6/52.4 **一格没动**，证实托管堆不是来源，trim/源生成也救不了它（Task 2 的 trim 空壳实测本来只有 0.5 MB 收益）。
④ **trim 这条瘦身路线本轮实测判死**：`-p:PublishTrimmed=true` 产物启动即 `InvalidOperationException: Reflection-based serialization has been disabled`（`SettingsStore.Load`，SettingsStore.cs:22）；再加 `-p:JsonSerializerIsReflectionEnabledByDefault=true` 仍以同一异常退出（runtimeconfig 里那条开关确实是 true，但 trim 后的 STJ 反射解析器已被裁掉）。要走通必须给 STJ 上 `JsonSerializerContext` 源生成，再处理 YamlDotNet（同样靠反射）——**收益上限就是 Task 2 量到的 0.5 MB，代价是两棵反射面的手术**，且 ③ 已证明缺口不在托管堆。ReadyToRun 只影响启动耗时，不影响这几个数字，未采用。
⑤ 系统代理与用户现成的 clash 客户端全程未动：`sysproxy.set` 一次都没调，宿主前后 HKCU `ProxyEnable=0` / `ProxyServer=127.0.0.1:8090` 逐字未变，机器上同时在跑的 `verge-mihomo`（pid 23600）未被连接；本项目的内核用自己的 `core\mihomo.exe`（v1.19.33）与自己的 9097/7897 端口。

**40 MB 这条线的处置结果（2026-10-27 已裁定）**：它是在 Task 2 的空壳（16.4 MB，什么都没装）上定的，从未按"Tray 里真的养着一颗内核进程 + 两条重定向日志流 + 输出环"这个形态重新推过。当时给的三条处置：
① **采用——把口径拆成两格，数字一个都不放宽**：`Tray ≤ 40 MB` 只管**空闲态**（无内核在跑；本轮实测 31.4 MB，连开 5 次管道会话后 37.4~37.8 MB，都在线内），内核在跑的那格由**合计线 `Tray+内核 ≤ 80 MB`** 管（本轮 74.0~77.5 MB）。用户批准"按空闲和满载拆两格执行"，判据表已按此重写（见本节开头）。
② 继续压实现换 40 线——可动的地方是 tooltip 泵每 2 s 的 `_store.Load()`（读盘 + JSON 反序列化，本可用内存里的 Settings）、`StderrTail` 的两次拷贝、两条重定向流；但归因数据（Private 只 +5.2 MB、GC 硬上限无效）说明这些是个位数 MB 的收益，填不平 9 MB。不采用。
③ 认定"Tray 满载 50 MB"是可接受形态并写下这条线——最诚实但最松，且要人接受 40 这条从第一天就没按满载定的数字作废。不采用（① 已经把 40 留在它唯一被验证过的形态上）。

**合并后复测（2026-10-27，`main` 的产物：`publish.ps1` 重打包 → 解包 → 真 Tray + 真内核 + 真窗口，agent 脚本自测那半程）**：三格全部复核，数字与本节上面的实测同档、线一条没碰——

| 采样点 | Tray | mihomo | UI |
| --- | --- | --- | --- |
| 冷启动 5 s（无内核、无界面） | WS **33.4** / Private — | 未起 | 未起 |
| 内核在跑（`core.start` 后、REST 跑过一轮） | WS **48.0** | WS **27.0** / Private 60.2 ⇒ 合计 **75.0 ≤ 80** | 未起 |
| `core.stop` 后沉淀 60 s | WS **49.0** / Private **12.3** | 进程已消失、9097/7897 无监听 | 未起 |
| 界面连上并跑过握手 + 轮询 | WS **40.4** / Private **10.5** | — | WS **152.3** / Private **89.7** ≤ 220 |

两处口径要在验收时讲清楚，否则会被读成"超标"：① **`core.stop` 不归还工作集**——停掉内核后 60 s 再采仍是 49.0（Private 只有 12.3），所以 40 那一格只能在**这一进程生命周期里没起过内核**的新 Tray 上读（本轮冷启动 33.4），跑过内核之后那格由合计线 80 判；这与上面 Task 17 的裁定同源，但清单第 6 条原话「内核已停或没起」是错的，已按此订正。② 界面连着轮询时 Tray 读到 40.4，压在 40 线上而不是线内——与 Task 10 的 45.4、Task 16 的 42.1 同一现象（映像共享页 + 已触碰未归还），判据不因它改动，验收时按"这一格以冷启动新进程为准"执行。

**真订阅下的满载那格破了线（第三轮实测，2026-10-27，未裁定）**：上面 74.0~77.5 那格是在 **3 个 `type: direct` 桩节点**上量的。换成用户的真订阅（25 个 anytls 节点 + 29 个组 + 内核自己载了 8.4 MB 的 `geoip.metadb`）后，UI 已起的采样点是 **Tray WS 63.8（Private 20.3）+ mihomo WS 40.9（Private 70.2）⇒ 合计 104.7 MB**，直接顶穿 80 那条线，余量不是"只剩 2.5~6 MB"而是**负的**。三条归因（各自主导程度未拆开验证，别当成结论读）：

① **节点数×轮询成本**：`/proxies` 一次全量重拉在桩上是 3 个成员、真机是 25 个成员 ×29 组，`System.Text.Json` 的响应对象与环形缓冲都按这个尺度长；

② **内核侧不是我们的账**：mihomo 自身从 26~27 MB 涨到 40.9 MB，多出来的是它自己的节点连接池与 geodata，我们一行代码都动不了——**80 这条线当初就没把真实订阅的规模算进去**；

③ **本轮 Tray 的采样条件不干净**：这个 Tray 生命周期里跑了 `core.start`（含一次 45 s 的 geodata 下载）、几十轮握手与轮询、还带着界面连着开——按上面的口径，这一格本就该由合计线判，而合计线里 mihomo 那半是真实规模。**要做的收尾**：用户过完人工冒烟后，用「真订阅 + 内核在跑 + UI 已关 + 静置 3 分钟」重采一次，再裁定这条线是按真实规模重写（比如 Tray ≤60 / 合计 ≤120）还是先做 Tray 侧归因（§12 遗留④那三条：tooltip 泵每 2 s 读盘 + JSON、`StderrTail` 双拷贝、两条重定向流）。**没有把数字藏起来**：本节的表与裁定句保持原样，破线只写在这一段里。

同批自测过的非开销项（都算清单第 3/4 条的脚本半程）：`handshake` 回 `state:"Running"` 且 `secret` 只在 handshake 回包里（`settings.get` 回包实测三字段、无 secret、无 `core_path`）；`sub.import` 两条通路（loopback HTTP URL 与本地 path）都回 `config_file`、原件按哈希落 `imports\`、**源 yaml mtime 与字节数逐字未变**；`/version` 无 token **401**、带 token **200** 回 `v1.19.33`；`/proxies/t1..t3/delay` 三条 200（72/62/60 ms）；HKCU 代理三值全程未动（`0` / `127.0.0.1:8090` / `localhost;127.*;<local>`，与清单第 0 条抄下的原值一致）。


## 11. 测试与验收

**任务 0 前置探针**（廉价实测，结论回写本 spec）：
1. §4.2 端点逐条真机核实（含 `PATCH`/`PUT` 与 provider 成员行为）；
2. WAS 2.5.1 unpackaged 下 `DesktopAcrylicController` 整窗 + `SystemBackdropElement` 的实际表现，以及系统关透明时的降级行为；
3. 空壳常驻实测：纯 Win32 Tray（含 self-contained）与 WinUI 3 UI 关窗前后的 Working Set；
4. Tray 是否可 `PublishTrimmed`（trimming 警告清零才采用）。

**Core（xUnit，全自动化）**：`SettingsStore` 原子写与默认值；`ConfigInjector` 覆盖集与"其余字段字节级不变"；`CoreManager` 状态机与内核输出环——假内核必须往 **stdout** 写日志才测得到环（假内核用 WireMock.Net + 一个占端子的 `cmd.exe` 假进程）；`SystemProxy` 打真实 HKCU（备份/还原夹具）；`CoreUpdater` 资产名匹配、zip 解包、`.bak` 轮换（WireMock 假 release 响应）。

**UI（FlaUI / UIAutomation，只读断言为主）**：断言"只有真窗口才有"的判据——断连时按钮置灰、横幅文案、开关回显、backdrop 档位切换后设置项标注实际生效值。不引快照测试。

> Task 12 实测确认：**unpackaged + self-contained 的 WinUI 3 能在本机真起窗并被 UIA3 读到**（`FlaUI.UIA3` 拿主窗标题、AutomationId、Name、IsOffscreen 全部可用）。这推掉了本 spec 从 Task 2 起一直挂着的前置疑问——后续所有 UI 任务的验收面都建立在这条上。三条派生约束记在计划的 R-12e/R-12g/R-12i：产物目录随调用方式在 `bin\Release` 与 `bin\x64\Release` 之间变（harness 要两个都试）、FlaUI 5 把 `element.Current.X` 扁平成 `element.X`、WinUI 3 的 `NavigationViewItem` 不实现 Invoke pattern（用 SelectionItem.Select，别用鼠标 Click 抢用户光标）。

**人工冒烟清单**（`docs/superpowers/checklists/v1-acceptance.md`）：内置内核启动 → 导入订阅 → 切节点 → 测延迟出值 → `curl -x 127.0.0.1:7897 https://www.gstatic.com/generate_204` 得 204 → 关窗后 `measure-residency.ps1` 达标 → 托盘"打开界面"重连正常 → 在线更新内核走完整进度 → 托盘退出后 `ProxyEnable=0` 且内核进程已终止。

**v1 验收线**：Core 测试全绿 + FlaUI 断言全过 + 冒烟清单全过 + §10 开销线达标。

> Task 18 实测（2026-10-27）：`dotnet build ClashAcry.sln -c Release` **0 警告**；Core.Tests **144/144**；UI.Tests **26/26**（改完取样形态后连跑 4 次全绿）。UI 侧五组是 Shell / Backdrop / Overview / ProxiesNoJitter / ChromeContrast，后者 6 条 `Fact`＝5 类控件极性分开判 + 1 条滚动条轨道/滑块对比 + 1 条浅色反证。**（这条的 6 是订正：合并前评审抓到当时三处文档都写成 7，`grep -c '\[Fact\]'` 实测 6；同批把总数一并订正，见下条。）**
>
> 两条跑法约束已被反复验证：产物目录随调用方式在 `bin\Release` 与 `bin\x64\Release` 之间变，所以**别加 `--no-build`**（加了会跑上一版二进制，报错现场与代码对不上）；Core.Tests 用生产管名，跑之前得确认没有活的 `ClashAcry.Tray.exe`。
>
> **合并前评审修复轮（2026-10-27，同批实测）**：三份只读评审（Core+Tray / UI / 测试与文档）逐条核实后修掉五项，测试基线随之抬到 **Core 154/154、UI 28/28**（Overview 8 条 = 原 6 + 新增 2）。三条新钉的跑法约束：
> ① **改 UI 源码后必须先 `dotnet build ClashAcry.sln`**——`dotnet test tests/ClashAcry.UI.Tests` 只重建测试工程（它按 R-12f 不引用 UI 工程），harness 起的是 `bin\x64\Release` 那个 exe，于是"改了没生效、测试仍全绿"，本轮就是靠这条才认出两条回归锁当时是空锁；
> ② **UI.Tests 现在起窗前先断言没有活的 `ClashAcry.Tray` 进程**（`UiAppHarness.AssertNoLiveTray`）：假 Tray 挂的也是生产管名，两台服务端同时挂得上时 UI 连到谁不定，连上真宿主就是往用户的真内核与真 `%LOCALAPPDATA%\ClashAcry` 上动手；这里只判定失败、不替用户杀进程；
> ③ 两条新回归锁 **必须在去掉修复后跑红过才算数**（已做：`UpdateInterruptedByTrayDeath_*` 超时、`TrayRestarts_*` 报"界面没重连上新的托盘"）。
>
> **合并实况（2026-10-27）**：v1 已进 `main`，合并提交 `2b21483`（`--no-ff`，树内容与修复轮末位提交 `ca7bf8c` 完全相同），合并前在 main 的树上重跑三件套全绿。仓库上从未开过 PR（匿名 API `pulls?state=all` 返空），所以这一步走的是 git 而非 `gh`；远端分支 `feat/clashacry-v1` 保留未删。过程细节与「为什么没装 gh」记在计划 Task 18 Step 4。
>
> **真订阅轮（2026-10-27 第三轮，同批实测）**：用户拿真机场 URL 报"导入失败 403"，挖出三条假夹具测不到的缺陷（UA 准入与空模板、`mixed-port` 透传、geodata 首下被探活窗口掐死），全部修 + 上锁，基线抬到 **Core 159/159、UI 28/28、构建 0 警告**。三条的细节分别记在 §12 ⑥、§12 ⑦ + §7、§9「启动窗口分档」；冒烟侧的观感与等待时长记在清单第 3/4 条。**假夹具的盲区样本再补一条**：`SubscriptionImportTests` 十条与 `ConfigInjectorTests` 全部用"我们自己写的 yaml + WireMock 随意回的 200"，所以"200 但零节点"和"订阅自带端口"这两种**真面板才会造出来的形状**一次都没暴露过。


## 12. 风险与开放问题

- **整窗 Desktop Acrylic 的合成开销——已实测，不再是风险项（Task 13/17）**：低端 GPU / 远程桌面上仍可能明显，但本机内存侧量不出代价——亚克力档 WS 145.6 vs 纯色档 145.9（差值在噪声内），最坏档（节点页 30 行 + 全组测速）180.0 / 220 线内。已用三档可切换 + 自动降级吸收；**RDP 下 `IsSupported()` 的实际表现本机未实测**，仍由 §8 的降级标注兜住。
- **WindowsAppSDKSelfContained 的常驻代价——已有答案，不超标**：自包含 MRT Core 确实把 UI 抬到 140~180 MB（对照：外壳未装页面时 140.4），但 220 MB 线守住、余量 40 MB，不需要回退到"依赖机器已装 Windows App Runtime"。Tray 侧 self-contained 空壳 16.4 MB（§10 Task 2）。
- **provider 成员与延迟端点——已有真机答案（v1.19.33，不再是风险项）**：`GET /proxies` 顶层表**不含** provider 成员（仅组 `all` 与 `GET /providers/proxies` 的 `proxies` 列表含之）；`GET /proxies/{成员名}/delay` 对成员**404**。据此 §4.2 已把"测 provider 成员延迟走 providers 详情 / 拿不到样本显示'上次'并标注"从兜底升级为既定实现要求。
- **mixed 端口被占时内核"带病存活"**（v1.19.33 实测，详见 §4.2 末段）：进程不退出、controller 全功能应答、仅 stdout 一行 `level=error` bind 失败；`Running` 判据必须双探活（controller + mixed 端口 TCP），与计划 Task 8 一致。另实测内核 Go 日志写在 **stdout**（stderr 恒空），`CoreManager` 的 stderr 环应同时采集两个流。
- **mihomo 跨版本 REST 漂移**：契约以 §4.2 + 源码为准，冒烟清单兜底。
- **横幅的"一键拉起/重启 Tray"尚未接（Task 14 记账；**2026-10-28 第五轮已接，裁定 R-21b，落地口径见 §3 与本节末**）**：现在横幅只说"托盘宿主未运行"，界面随即把所有内核控制置灰；用户想让界面重新连上，得自己启动 `ClashAcry.Tray.exe` 或重开 UI。补法已想清楚但不属 v1 最小闭环：横幅加一个按钮 → `EnsureTrayRunningAsync()` + 重新握手 + 清故障（故障文案按"第一条说话"，恢复靠一次成功轮询把它清空）。同批遗留：`PipeClient` 重连后 `_pending` 仍跨连接共享。
  **界面侧"断开后不会自动重连"这条已在合并前评审修复轮解决**（2026-10-27）：`RefreshAsync` 的失败分支现在调 `TrayLink.InvalidateHandshake()`，缓存握手一作废，下一轮（2 s）就走"探活 → 重新握手"，Tray 死过又起来时版本号与 controller 地址都会重新读回来；回归锁 `OverviewTests.TrayRestarts_UIReHandshakes_AndReadsTheNewVersion`。按钮侧同批修掉一条：`core.update` 在飞时连接断掉，回帧与结果广播一起丢，`_updatePct` 若不在失败分支复位就是「更新内核 37%」挂到进程重启（锁 `UpdateInterruptedByTrayDeath_TextDoesNotStallAtPercent`）。
- **评审修复轮记下但未动的四条（v1.1 账上，均不阻塞合并）**：
  ① `measure-residency.ps1` 按 `-ProcessName mihomo` 取进程，**不限定路径**——本机现在就有 `verge-mihomo.exe` 在跑，脚本会心平气和地量到别家内核的内存数（AGENTS 红线的另一面）。修法很小：按 `Path -like "$env:LOCALAPPDATA\ClashAcry\*"` 过滤或收 `-Pid`。
  ② `--reset-prefs` 走 `UiPrefs.Reset()` = `Values.Clear()`，会把开发者自己存的界面偏好整份抹掉，测试侧无人还原。应收窄到测试读写的那两个键。
  ③ `PipeServer`：`AcceptLoop` 的 `using var linked` 在循环因产线故障抛出而死时，会把令牌 dispose 给还在跑的 `Pump`（`ObjectDisposedException` 不在它的 catch 过滤器里 → 未观察的任务异常）；`DrainAsync` 的裸 `catch` 只停写半边、连接仍留在 `_clients` 里。两条都是窄窗口的控制通道退化，不是常态路径。
  ④ 三条宿主生命周期侧的宽限期缺失：`CoreManager.KillProcess` 的 `WaitForExitAsync()` 无超时（卡住的 mihomo 会楔死 `StopAsync`/退出钩子，5 s 预算后硬杀 → 孤儿内核 + 代理留在开启态）；`SubscriptionImport.ImportFromUrl` 是 sync-over-async + 无上限 `ReadToEnd` 且 `ct=default`（慢发订阅源能把它拖过 `HttpClient.Timeout`）；`TrayHost` 托盘分支的 `MessageLoop` 返回后不跑 `ShutdownAsync`（只有 `--no-tray` 分支跑）。
  另：`settings.update` 改端口后界面会把新端口写进系统代理而内核仍听旧端口（下次起核才刷新）；spec §4.4 里 `window.{width,height,x,y,maximized}` 与 `proxies.show_hidden` **未实现**（前者从未被裁定延后，属文档回写欠账）。
  ⑤ **概览页「内核版本」在冷装后恒显示"未知"（合并后复测发现，2026-10-27；第五轮已修，裁定 R-21a）**：`settings.core_version` 只有走过一次在线更新才会被写（`TrayCommandRouting` 在 `core.update` 成功路径里读 `/version` 落盘），而 Tray 把内核从 `Stopped` 带到 `Running` 之后**不回读一次 `/version`**——于是握手回 `core_version:null`，界面按 `hs.CoreVersion ?? "未知"` 显示（`OverviewViewModel:136`）。实测：真机起核后握手 `state:"Running"`、REST `/version` 带 token 明明回 `v1.19.33`，握手那格仍是空。修法是一行级别的：`CoreManager` 进 `Running` 后由 Tray 读一次 `/version` 写进 store（读失败保持 null，别把展示值当故障）。
  **实际落地比那"一行"多两层**，缺一层都仍是恒未知：
  ① Tray 侧：`TrayCommandRouting.StartCoreAsync` 在 `StartAsync` 回真之后调 `ReadVersionIntoStoreAsync()`（`core.start` 与 `core.reload` 的"没在跑就起"两条都走它），`core.update` 那条原本裸写的 try/catch 收敛进同一个 helper——**读失败保持原值、绝不抛穿成错误帧**（同一个 bug 在 Task 11 Step 6 真机踩过一次，锁 `CoreStart_VersionReadFails_StillRepliesRunning_NotErrorFrame`）。
  ② UI 侧握手缓存：`RefreshAsync` 只在「从没握过手」时才握手，之后一路复用 `LastHandshake`；冷装那条主路径上第一次握手时内核还没跑（版本无从可读），于是界面永远复用那份 `core_version:null` 的旧描述。修法是 `core.state` 事件一到就 `InvalidateHandshake()`（状态一变，握手带的版本/地址/secret 都不再是事实），下一轮轮询重握手取回真值（锁 `CoreStartStateEvent_UIReHandshakes_AndVersionLeavesUnknown`）。
  ③ 剩下的窄窗口不靠调度碰运气：Tray 写版本落在 `Transition(Running)` **之后几毫秒**，握手若正好卡进那几毫秒，缓存到的仍是「Running 但没版本号」的描述，而同一状态不会再发第二次事件——所以 UI 对这种握手**只用于本轮读数、不留缓存**（每轮重问，直到真值落地；锁 `RunningHandshakeWithoutVersion_IsNotCached_AsVersionCannotArriveLater`）。
  **不阻塞 v1**：版本号在 §6 只承担展示，启停/测速都不依赖它；但它会让冒烟的人第一眼以为"没连上"。
- **真订阅第一次打通暴露的三条（2026-10-27 合并后复测，⑥⑦⑧ 已修，代码与回归锁同批落地）**：
  ⑥ **机场面板按 User-Agent 发人，而且"200"不等于"给了节点"**。同一个订阅地址（`apa.dyljstar.sbs/s/…`）在本机实测的准入矩阵：
  | 请求 User-Agent | 状态 | 正文 |
  |---|---|---|
  | （无 UA 头 = .NET 裸 `new HttpClient()`） | **403** | `forbidden` |
  | `ClashAcry/1.0`、`ClashforWindows` | 200 | **10457 字节，`proxies: []`，0 个节点** |
  | `clash.meta`、`Meta/v1.19.33` | 200 | 17061 字节，25 个 anytls 节点 + url-test/fallback/select |
  | `clash-verge/v2.3.1`、`NekoBox` | 200 | 17060 字节（同一份节点） |
  | `stash` | 200 | 16749 字节 |
  | `surge` | 200 | 24528 字节（另一套格式，不是 Clash yaml） |
  | `v2rayN/6.0`、浏览器 UA | 200 | 4864 字节（套餐信息页，不是配置） |
  | `clash`（裸一个词） | 200 | **0 字节** |
  裁定：订阅通路的 UA 常量取 **`clash.meta`**——覆盖面最广的那一档，且我们跑的本来就是 mihomo（clash.meta 的延续），报这个名字属实。它仍**逐请求带**（R-6a：那颗 `HttpClient` 与 controller REST、GitHub API 共用）。第二层防御：`SubscriptionImport` 现在拒掉"`proxies` 明确为空**且**没有 `proxy-providers`"的正文，报 `订阅里一个节点都没有…`（锁 `Import_EmptyProxiesTemplate_FailsLoudly_WritesNothing`，反证 `Import_ProvidersOnlyConfig_IsAccepted` 不许误伤 provider 分发）——只报"已导入"而节点页空着，比一句明确的失败坏得多。**没做**：按响应头 `profile-update-*` 或面板 URL 参数自适应 UA；用户换机场如果再撞到"200 但空模板"，现在至少能看见一句明确的话。
  ⑦ **注入必须无条件覆盖 `mixed-port`**（§7 已改）：真订阅第一行就是 `mixed-port: 7890`，旧的"有端口键就不补"让内核听 7890，而系统代理与双探活用设置里的 7897 → 界面全绿、流量全断。锁 `Inject_ForcesMixedPort_EvenWhenSubscriptionShipsOne`；真机旁证是起核后 `GET /configs` 回 `mixed-port: 7897`。**遗留同族**：订阅若写了 `port: 7897` 或 `socks-port: 7897`（撞我们那个 mixed 端口），mihomo 先绑走、mixed 绑不上 ⇒ 回到"带病存活"档，5 s 判死。v1 不去猜、不改用户的端口键，撞上了由 §9 的②档文案指出来。
  ⑧ **geodata 首下**：见 §9「启动窗口分档」——分档修掉了"我们把下载掐死"，但**冷装 + 带 `GEOIP` 规则**的首次启动仍要等一次 8.4 MB 的 `geoip.metadb`（本机 ~45 s，且源站不稳：实测也出现过 90 s 超时 fatal）。v1.1 候选是把 `geoip.metadb` / `geosite.dat` 随包分发并在起核前种进 `runtime\`（与 `BUNDLED.txt` 同一套溯源口径），代价是 zip 再加 ~10 MB。
- **托盘退出轮暴露的两条（2026-10-27 第四轮人工冒烟）**：
  ⑨ **托盘「退出」后界面窗口还留在屏幕上（已修，裁定 R-20a，见 §3 反向语义）**：退出钩子原本只做「摘图标 → 关代理 → 停内核 → 关管道」，界面于是停在"宿主没了但我还开着"的形态——横幅 `与托盘的连接中断：Pipe is broken.`、控制全置灰、节点列表还显示上一世的「442 ms」。修法是在停内核之后遍历 `ClashAcry.UI` 进程调 `CloseMainWindow()`（**不 Kill**），由 UI 自己的「关窗即退」收尾；机制对真窗口实测：`MainWindowHandle=592352`、`CloseMainWindow()` 回 `True`、3 s 内进程消失。这条没有回归锁——`TrayHost.ShutdownAsync` 以 `Environment.Exit` 结尾、且要真托盘菜单才可达，单元侧无法在不掏空 TrayHost 的前提下钉住，以真机实测代替（同 §3 的 WinExe 退出路实测同一口径）。**仍然留着的相邻形态**：Tray 被强杀/崩溃时界面照旧活着显示断连横幅——那一条的出路是 §12 早就记过的「横幅一键拉起 Tray」按钮，**第五轮已接（裁定 R-21b，见下）**。
  ⑩ **`sysproxy.set(true)` 会覆盖别人写的 `ProxyServer` / `ProxyOverride`（本轮真机吃到，已修：R-20b）**：`SystemProxy.Enable` 无条件写这两键（I-3 只调整了写序，没做快照），`Disable` 依 R-7c 只翻 `ProxyEnable`、不碰载荷。这条**不是暗坑**——清单第 0 条的原话就是「过完第 5 条就回不去了，先把三值抄下来」，并据此要求人工先备份。第四轮实测确认它真的发生了：基线 `ProxyServer=127.0.0.1:8090` / `ProxyOverride=localhost;127.*;<local>`（clash-verge 留下的）变成 `127.0.0.1:7897` / `localhost,127.0.0.1,<local>`，托盘退出的清理链不会把它换回去（`ProxyEnable` 回了 0，流量没被劫走，但那两键回不去）。
  **修法（已落地）**：`Enable` 前 `SystemProxy.Capture()` 取两键原值 → 存进 `settings.json` 的 `sys_proxy_backup`（§4.3；Tray 独占写，符合两棵真相树；不进 `settings.get` 回包）→ `Disable` 调 `Restore(backup, ourServer, ourBypass)`，**判据是当前 `ProxyServer` 仍等于我们写的那份**（不是判 `ProxyEnable`：第三方可以在我们开着时改地址而不动开关）；不成立就一条都不写、快照留着，方向与 R-7c 一致——宁可漏还，不反向抹别人。还原语义含"原本不存在 ⇒ 删除该值"，不写空串（R-7b 同口径）。三条建档/销账规矩：① 只在当前值**不是**我们自己的串时建档，否则「开→还原失败→再开」会把自己写成"原值"、真原值永久丢失（锁 `SysProxy_EnableTwice_KeepsTheTrueOriginal_NotOurOwnLeftover`）；② 冷启动的悬挂清理也触发还原——强杀没有退出钩子，那是唯一还能还的时机，所以 `CleanupIfStale` 改成返回"是否真清掉了我们那条"，返回假绝不碰快照（锁 `CleanupIfStale_ReportsWhetherItCleanedOurOwnLeftover`）；③ 还原成功才销凭证（锁 `SysProxy_EnableThenDisable_RestoresTheForeignProxyServer`）。
  **本机的现值已手工改回基线**（`ProxyEnable=0` / `ProxyServer=127.0.0.1:8090` / `ProxyOverride=localhost;127.*;<local>`，用户指令「开始吧」）：旧产物没留快照，新代码无从代劳。**「停止内核」这条路径不还原**——它只翻开关（`CoreManager.Stop` → `CleanupIfStale`），载荷留在盘上等下一次 `sysproxy.set(false)` 或冷启动清理，避免"关了代理又开内核"时地址已被换掉。
- **第五轮（2026-10-28）把 v1.1 账上的两条一起收掉**：⑤「内核版本恒未知」（裁定 **R-21a**，三层修法见上）与横幅「一键拉起 Tray」按钮（裁定 **R-21b**，§3 已回写落地口径）。
  按钮这一条的三条判据值得单独记：① **只走既有通路**——点击处理调的就是外壳启动时那句 `EnsureTrayRunningAsync()`，UI 依旧不起内核（禁令 1），新增的只是"让用户按一下"这个入口；② **可见判据取"够不够得着托盘"而非"横幅开没开"**，`ErrorText` 非空 **且** `_state is null` 才露面——内核崩溃（托盘健在）时横幅同样开着，给一个「拉起托盘」是把人往错的方向推，这条用 `ShellTests.CoreCrashed_BannerShowsFault_ButNoRelaunchAction` 钉住（红证：把判据退化成"横幅开着就显示"，该用例当场变红）；③ **测试里绝不真按这个按钮**——`Process.Start` 会拉起真 `ClashAcry.Tray.exe` 抢生产管名，所以两条 UIA 锁都只读存在性/`IsOffscreen`，点击后的链路由 `--tray-absent` 的可见性 + 真机冒烟覆盖（回归锁 `TrayAbsent_BannerOffersOneClickRelaunch`）。
  本轮回归锁合计 6 条新增（Core 2 / UI 4：`OverviewTests` 2 条 + `ShellTests` 2 条），Core 168、UI 32 全绿。
- **secret 落盘明文**（同用户可读）：自用容忍；管道 ACL 限当前用户 SID，`handshake` 之外不向其他进程暴露。
- **yaml 保真度（Task 5 已实测结案，不再是待探针项）**：YamlDotNet 18.1.0 的 `YamlStream`/DOM 通路**保留全部字段值与键序、保留 block/flow 风格与用户锚点别名**（`&x`、`*x`、`<<:`），但**不保留注释**（DOM 不承载 comment token），且会出现三类无害漂移：block 序列压平到父键同列、flow 节点内部空白归一、文档尾部多一行 `...`。因此 v1 的公开承诺只有“不认识的原样透传、只做顶层必需键覆盖、用户手写原件永不被改写”，**UI 文案与导入说明不得声称保留注释或原始格式**；如日后要真保注释，需换库并另立任务。另：`YamlStream.Save` 必须显式 `assignAnchors: false`，否则会给内容重复的节点自动发随机锚点，产物里长出用户没写过的 `&203281033`。

## 13. phase 2 边界（不在本规格实现）

连接列表与实时流量/日志（WS 转发与节流）、TUN（需特权守护：SCM 服务 + 命名管道）、开机自启（HKCU `Run` 键，一行代码，随时可提级到 v1）、规则编辑器、多配置管理、MSIX 与自动更新分发、图标字体与主题色自定义。
