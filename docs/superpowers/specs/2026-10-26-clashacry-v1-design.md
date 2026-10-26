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
| 常驻宿主 | 独立轻量进程 `ClashAcry.Tray`（纯 Win32，零 XAML，手写 `Shell_NotifyIcon`） | 不用 WinForms 托盘：那会把整套 WinForms 运行时请进常驻进程，直接顶爆 §10 的开销线 |
| 后台能力 | `ClashAcry.Core` 托管类库，被 Tray 与 UI **in-proc 复用** | 无数据面代码，全部是胶水级 OS/HTTP 调用；拆 Rust/AOT 均无收益 |
| 内核 | 原版 mihomo Go 二进制，随包内置一个 + 应用内在线更新 | 不 fork、不魔改，行为跟随上游 |
| 特权守护 | **phase 2**（TUN / 免 UAC 才需要） | v1 走系统代理模式，全程不需要管理员权限 |

已否决路线：Rust 后台（见 §1）；MSIX 打包（v1 不做，免安装 zip 够用）；WinForms 托盘（开销）；Web 技术栈界面（本项目目标即原生 Fluent，不在范围内）。

NativeAOT 明确**不承诺**：WinUI 3 XAML 编译绑定依赖 CoreCLR，本项目按 CoreCLR 自包含发布。Tray 进程 trimming **空壳实测（2026-10-26，Task 2）**：`dotnet publish src/ClashAcry.Tray -c Release -r win-x64 --self-contained -p:PublishTrimmed=true` 删 obj/bin 全新还原后 0 警告 0 错误——**无任何 `IL2xxx`/`IL3xxx`**（全局 `TreatWarningsAsErrors=true`，有警告即构建失败，故"零警告"是构建强保证）；发布产物 20 MB（未 trim 86 MB），空壳常驻 WS 15.9 MB（未 trim 16.4 MB）。**是否最终启用留待 Task 17 复验**：届时 Core 已含 YamlDotNet/反射敏感代码，只要冒出一条 IL2xxx 就按既定策略放弃 trimming。

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

## 4. 契约

### 4.1 命名管道 `clashacry.tray.v1`

管道 ACL 限当前用户 SID（`FileSecurity` 显式授权，拒绝 Everyone）。**落地形态已实测修正（Task 9 派发前探针，.NET 10 / `net10.0-windows10.0.19041.0`）**：`NamedPipeServerStream` 在 .NET 10 **没有**带 `PipeSecurity` 的构造函数，且对已建实例调 `SetAccessControl` 抛 `UnauthorizedAccessException`，读回仍是默认 5 条 Allow（含 `S-1-1-0` Everyone）——即"先建后设"这条路等于没设；唯一可行入口是 `NamedPipeServerStreamAcl.Create(...)`（建实例时落 DACL，读回恰好 1 条 Allow = 当前用户 SID）。另外 `maxNumberOfServerInstances` 传 255 会抛 `ArgumentOutOfRangeException`，合法是 1–254 或 `MaxAllowedServerInstances`。一帧一条 UTF-8 JSON 行：`{"id","kind":"command|event|reply","type","payload","seq","reply_to","error"}`。线键名一律小写/snake_case，且由 `ClashAcry.Contracts` 里每个跨进程结构体的显式 `[JsonPropertyName]` 钉死——**不靠序列化命名策略推断**（Task 3 落地：`Frame`、`HandshakeReply` 与三个事件 payload 全量标注，`FrameCodec` 不设命名策略）。

握手回包 `handshake` 的形状（UI 起 REST 客户端所需的全部参数，一次给齐）：`{state,host,port,secret,mixed_port,core_version?,delay_test_url}`——`delay_test_url` 走握手而非另开一条读设置的通路，UI 测速与设置页的测速地址下拉都用它。**secret 只在 `handshake` 的回包里下发，UI 不持久化、不展示。**

命令（UI → Tray）：`core.start`、`core.stop`、`core.reload`、`mode.set{mode}`、`sysproxy.set{enabled}`、`core.update`、`settings.update{patch}`、`ui.focus`、`handshake`（返回 `CoreState` + controller 地址 + secret，供 UI 起 REST 客户端）。

事件（Tray → UI）：`core.state{state,reason?,stderr_tail?}`、`core.update.progress{received,total,pct}`、`core.update.result{result}`、`settings.changed{SettingsSnapshot 全量}`——**四个即全部**。日志文本不单独成事件（`log{level,msg}` 在预检评审中作为无广播方的死契约删除），内核输出随 `core.state` 的 `stderr_tail` 下发；`settings.changed` 恒为完整的 `SettingsSnapshot` 而非键增量，以免"同一个事件两种 payload"。（键名拼写以 `stderr_tail`/`SettingsSnapshot` 为准，Task 3 已实测落码。）

UI 起来发现管道不存在（Tray 未运行或被杀）→ 顶部横幅"托盘宿主未运行" + 一键拉起 Tray 进程。

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

mixed 端口被占用的真机行为（Task 8 `Running` 判据依据，`-OccupyMixedPort` 实测）：预绑 7897 再起内核——内核**不退出**，controller 照常 `GET /version` **200**，日志打一行 `level=error msg="Start Mixed(http+socks) server error: listen tcp 127.0.0.1:7897: bind: Only one usage of each socket address (protocol/network address/port) is normally permitted."` 后继续完成初始化（provider 启动日志照常出现，controller 全部端点仍应答）；且此时 `GET /configs` 报 `"mixed-port":0`。结论：**controller 应答不能证明代理可用**，`Running` 判据必须包含 mixed 端口 TCP 连接探活（与计划 Task 8 的双探活签名一致）。

### 4.3 `settings.json`（Tray 独占写）

```
{ controller_host: "127.0.0.1", controller_port: 9097, secret: "<32hex 首次随机>",
  mixed_port: 7897, core_path: null, delay_test_url: "http://www.gstatic.com/generate_204",
  last_subscription: "<URL 或本地 yaml 绝对路径>", core_version: "<已装内核 tag>" }
```

**扁平 snake_case，不嵌套 `controller: {host, port}`**（Task 4 落地裁定）：Task 8/10/11 全部用 `with` 改单个端口字段，嵌套会把每处改动变成 `s with { Controller = s.Controller with { Port = p } }`；且 Task 10 的跨进程 `SettingsPatch` 本就是扁平 `controller_port`，两处同形才不留翻译层。键名逐字段显式 `[JsonPropertyName]`，不靠命名策略推断；缺键必须回落到默认值（用户手改 settings.json 是预期通路）。`delay_test_url` 默认 `http://`（Task 1 探针用它调 `/delay` 实测出过延迟值，且与 UI 侧测速地址下拉、`handshake` 下发的 `delay_test_url` 同值）。

写入必须原子（同目录临时文件 + `File.Replace` 保留 `.bak`）。不放任何 phase 2 才用的占位字段。

### 4.4 界面偏好（UI 独占，`ApplicationData.GetForUnpackaged().LocalSettings`）

`backdrop` = `acrylic`(默认) · `mica` · `solid`；`theme` = `system` · `light` · `dark`；`window.{width,height,x,y,maximized}`；`proxies.show_hidden` = false。与 `settings.json` 分家，按写者划界，两棵真相树，不存在并发写。

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
- 注入最小化：只覆盖 `external-controller`、`secret`、`allow-lan: false`、`unified-delay: true`；原文没有任何端口字段时补 `mixed-port`；其余字段不动、不排序、不格式化重写。
- 轮询/自动刷新不得产生可见抖动：列表 key 用节点名，数据无变化不重建可视树，数值就地更新。

## 8. Fluent / Acrylic 视觉方案

- **backdrop 三档**：`Acrylic`（默认，整窗 `DesktopAcrylicController`）· `Mica`（`MicaController`）· `纯色`。设置页单选即时生效，不重启：换 `Window.SystemBackdrop` 实例并触发一次重挂。
- **强制降级**：系统"个性化 → 颜色 → 透明效果"关闭、远程桌面会话、`DesktopAcrylicController.IsSupported()` 返回假这三种情况下，一律自动落纯色，并在设置项上明写"当前实际生效：纯色（系统透明已关闭）"。绝不静默假装还是亚克力。探测逻辑（读 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\EnableTransparency` + `IsSupported()`）由任务 0 探针实测确认可行性，不成立则改用等价的系统事件监听。
- **层级材质**：卡片/浮层用 `SystemBackdropElement` 或 `AcrylicBrush`（薄层），内容层保持 `SolidBackgroundFillColorBase` 系，保证文字对比度不靠猜。
- **主题**：`ElementTheme` 三档跟随系统/light/dark。深色下控件 chrome **逐类核对**（`Button`/`ComboBox`/`ComboBoxItem`/`ListBoxItem`/`ScrollBar` 含 `Track.Thumb` 与翻页 `RepeatButton`，每类都要检查默认模板是否残留浅色），验收判据用像素取样而非肉眼看图。
- **动效范围**：v1 只做 `ThemeTransition` + 页面切换转场 + 按钮 pressed/pointerover 默认态。connected animation、弹簧自定义曲线推迟。

## 9. 错误处理

| 类别 | 场景 | 策略 |
|---|---|---|
| 内核启动失败 | controller 端口占用、yaml 语法错 | 内核输出环形缓冲（stdout+stderr），UI 展示 Go 原文**不翻译**；仅启动时自动重试一次，重试仍失败落 `Crashed` 并保留原因 |
| 状态失真 | mixed 端口被占但 controller 正常，内核活着却不通 | `Running` 判据 = `GET /version` 成功 **且** mixed 端口 TCP 可连；任一不符不进入 `Running`，横幅报"端口 7897 被占用" |
| API 断连 | secret 不符、controller 未监听、Tray 掉线 | `MihomoClient` 透出真实状态码；UI 顶部横幅态 + 写操作按钮置灰；管道断开则提示一键重启 Tray |
| 系统代理残留 | UI/Tray 被强杀后注册表悬挂 | 三层：正常退出钩子必清；Tray 启动时校验"注册表代理指向本应用端口且探活失败"才清（指向其他软件的代理**绝不触碰**）；托盘"停止内核"顺带清 |
| 订阅下载失败 | 网络/源站错误/非 yaml 响应 | 保留旧 `config.yaml` + 横幅报错；**永不清空配置** |
| 内核更新失败 | 下载中断、zip 解不出 exe、目标文件被占用 | 缓存已下载 zip 供重试跳过网络；替换失败退回原 `.exe` 并保持内核运行；全程不阻塞 UI（进度事件推 `core.update.progress`），拒绝阻塞式弹窗 |

**已知限制（Task 7 回写）**：`CleanupIfStale` 比对的是**当前**设置里的 `mixed_port`，若用户在系统代理还指向旧端口时改了 `mixed_port`，那份指向旧端口的悬挂值 v1 不会清掉（换端口后重新开启一次系统代理即覆盖）。

**取消语义（Task 8 回写）**：`StartAsync` 的 `CancellationToken` 一旦被调用方触发，**不算启动失败**——不落 `Crashed`、不进"自动重试一次"的循环。行为固定为三步，顺序不可换：① 杀掉本已 spawn 的 mihomo 进程（绝不留孤儿内核）；② `Transition(Stopped)` 并保留"启动被取消"作为原因；③ 原样抛 `OperationCanceledException`（吞掉就等于伪造业务结论，让调用方以为还能重试）。实现上这条 catch 必须在通用 `catch (Exception)` **之前**，否则取消会被归类成启动异常。

## 10. 常驻开销目标（验收判据）

| 形态 | 目标 | 测量 | 实测（空壳，2026-10-26 / Task 2） |
|---|---|---|---|
| 日常常驻：Tray + mihomo（UI 已关） | Tray ≤ **40 MB**，合计 ≤ **80 MB** | `scripts/measure-residency.ps1`：`Get-Process` 采 `WorkingSet64` 与 `PrivateMemorySize64` 各 5 次取中位数 | Tray 空壳（self-contained 未 trim）WS **16.4 MB** / Private **4.6 MB**（三轮各取中位数：17.0 / 16.4 / 16.4）；trim 版 15.9 MB。距 40 MB 线余量充足 |
| UI 打开（Acrylic 档） | UI 进程 ≤ **220 MB**，Tray+内核+UI 合计 ≤ 260 MB | 同上，另记纯色档差值用于量化整窗 Acrylic 的成本 | 待 Task 12 起窗后实测（Task 2 仅验证 UI 空壳可编译：0 警告 0 错误） |

数字是**待实测校准的初始线**：任务 0 探针先出空壳实测，若 WinUI 3 空壳常态即超线，回到本节改档默认值并记录，而不是悄悄放宽判据。

**Task 2 基线复现命令**（本机 .NET SDK 10.0.203，win-x64，Release）：

```
dotnet publish src/ClashAcry.Tray -c Release -r win-x64 --self-contained
Start-Process src\ClashAcry.Tray\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\ClashAcry.Tray.exe -PassThru
powershell -File scripts/measure-residency.ps1 -ProcessName ClashAcry.Tray -Json   # → {"name":"ClashAcry.Tray","working_set_mb":16.4,"private_mb":4.6}
Stop-Process -Name ClashAcry.Tray   # 测完必须杀进程，否则污染后续测量
```

## 11. 测试与验收

**任务 0 前置探针**（廉价实测，结论回写本 spec）：
1. §4.2 端点逐条真机核实（含 `PATCH`/`PUT` 与 provider 成员行为）；
2. WAS 2.5.1 unpackaged 下 `DesktopAcrylicController` 整窗 + `SystemBackdropElement` 的实际表现，以及系统关透明时的降级行为；
3. 空壳常驻实测：纯 Win32 Tray（含 self-contained）与 WinUI 3 UI 关窗前后的 Working Set；
4. Tray 是否可 `PublishTrimmed`（trimming 警告清零才采用）。

**Core（xUnit，全自动化）**：`SettingsStore` 原子写与默认值；`ConfigInjector` 覆盖集与"其余字段字节级不变"；`CoreManager` 状态机与内核输出环——假内核必须往 **stdout** 写日志才测得到环（假内核用 WireMock.Net + 一个占端子的 `cmd.exe` 假进程）；`SystemProxy` 打真实 HKCU（备份/还原夹具）；`CoreUpdater` 资产名匹配、zip 解包、`.bak` 轮换（WireMock 假 release 响应）。

**UI（FlaUI / UIAutomation，只读断言为主）**：断言"只有真窗口才有"的判据——断连时按钮置灰、横幅文案、开关回显、backdrop 档位切换后设置项标注实际生效值。不引快照测试。

**人工冒烟清单**（`docs/superpowers/checklists/v1-acceptance.md`）：内置内核启动 → 导入订阅 → 切节点 → 测延迟出值 → `curl -x 127.0.0.1:7897 https://www.gstatic.com/generate_204` 得 204 → 关窗后 `measure-residency.ps1` 达标 → 托盘"打开界面"重连正常 → 在线更新内核走完整进度 → 托盘退出后 `ProxyEnable=0` 且内核进程已终止。

**v1 验收线**：Core 测试全绿 + FlaUI 断言全过 + 冒烟清单全过 + §10 开销线达标。

## 12. 风险与开放问题

- **整窗 Desktop Acrylic 的合成开销**：低端 GPU / 远程桌面上可能明显。已用三档可切换 + 自动降级吸收；§10 会量化 Acrylic 相对纯色档的内存差。
- **WindowsAppSDKSelfContained 的常驻代价未知**：自包含 MRT Core 可能抬高 UI 常驻；探针若发现超标，回退到"依赖机器已装 Windows App Runtime"（Win11 基本自带）并记录。
- **provider 成员与延迟端点——已有真机答案（v1.19.33，不再是风险项）**：`GET /proxies` 顶层表**不含** provider 成员（仅组 `all` 与 `GET /providers/proxies` 的 `proxies` 列表含之）；`GET /proxies/{成员名}/delay` 对成员**404**。据此 §4.2 已把"测 provider 成员延迟走 providers 详情 / 拿不到样本显示'上次'并标注"从兜底升级为既定实现要求。
- **mixed 端口被占时内核"带病存活"**（v1.19.33 实测，详见 §4.2 末段）：进程不退出、controller 全功能应答、仅 stdout 一行 `level=error` bind 失败；`Running` 判据必须双探活（controller + mixed 端口 TCP），与计划 Task 8 一致。另实测内核 Go 日志写在 **stdout**（stderr 恒空），`CoreManager` 的 stderr 环应同时采集两个流。
- **mihomo 跨版本 REST 漂移**：契约以 §4.2 + 源码为准，冒烟清单兜底。
- **secret 落盘明文**（同用户可读）：自用容忍；管道 ACL 限当前用户 SID，`handshake` 之外不向其他进程暴露。
- **yaml 保真度（Task 5 已实测结案，不再是待探针项）**：YamlDotNet 18.1.0 的 `YamlStream`/DOM 通路**保留全部字段值与键序、保留 block/flow 风格与用户锚点别名**（`&x`、`*x`、`<<:`），但**不保留注释**（DOM 不承载 comment token），且会出现三类无害漂移：block 序列压平到父键同列、flow 节点内部空白归一、文档尾部多一行 `...`。因此 v1 的公开承诺只有“不认识的原样透传、只做顶层必需键覆盖、用户手写原件永不被改写”，**UI 文案与导入说明不得声称保留注释或原始格式**；如日后要真保注释，需换库并另立任务。另：`YamlStream.Save` 必须显式 `assignAnchors: false`，否则会给内容重复的节点自动发随机锚点，产物里长出用户没写过的 `&203281033`。

## 13. phase 2 边界（不在本规格实现）

连接列表与实时流量/日志（WS 转发与节流）、TUN（需特权守护：SCM 服务 + 命名管道）、开机自启（HKCU `Run` 键，一行代码，随时可提级到 v1）、规则编辑器、多配置管理、MSIX 与自动更新分发、图标字体与主题色自定义。
