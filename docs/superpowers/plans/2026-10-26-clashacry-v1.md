# ClashAcry v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付一个 WinUI 3 客户端：整窗 Desktop Acrylic 可切 Mica/纯色，由无 XAML 的 Tray 进程托管原版 mihomo 内核生命周期，UI 按需启动关窗即退出。

**Architecture:** 双进程 + 一个共享托管库。`ClashAcry.Core`（纯托管，零 UI 依赖）承载设置存储、yaml 注入、内核状态机、系统代理、REST 客户端、内核更新器与管道协议；`ClashAcry.Tray` 是内核与 `settings.json` 的唯一写者并常驻托盘；`ClashAcry.UI` 只经命名管道发命令、只经 HTTP 读 mihomo 控制面。mihomo 是唯一事实源，UI 不镜像节点状态。

**Tech Stack:** .NET 10 / C#、Windows App SDK 2.5.1（WinUI 3，unpackaged self-contained）、YamlDotNet、System.Text.Json、System.IO.Pipes、Win32 `Shell_NotifyIcon`（手写 P/Invoke）、xUnit + WireMock.Net、FlaUI（UIA3）。

**Spec:** `docs/superpowers/specs/2026-10-26-clashacry-v1-design.md`（本计划逐条对齐其 §3 所有权、§4 契约、§10 开销线、§11 验收）

## Global Constraints

每个任务的隐含要求，值取自 spec 原文：

- 目标框架 `net10.0-windows10.0.19041.0`（Tray 与 Contracts 用 `net10.0-windows10.0.19041.0`，Contracts 若不需 Win32 则 `net10.0`）；Windows App SDK 包版本 **2.5.1**。
- **不引入 Rust、不引入 NativeAOT、不引入 WinForms/WPF、不引入整站 UI 框架。**
- Tray 进程**零 XAML**；托盘用 P/Invoke `Shell_NotifyIcon` + `LoadImage`，禁止 `System.Windows.Forms.NotifyIcon` 与 `System.Drawing.Common`。
- 内核生命周期归 Tray：UI 进程不得 `Process.Start` mihomo，不得 kill 它（spec §3 禁令 1）。
- UI 不写 `settings.json`；界面偏好只写 `ApplicationData.GetForUnpackaged().LocalSettings`（spec §4.4）。
- mihomo REST 端点的方法名与请求体形状**必须由 Task 1 探针真机核实并回写 spec §4.2**，之后才允许写 `MihomoClient`。
- 注入只覆盖 `external-controller` / `secret` / `allow-lan: false` / `unified-delay: true`；原文缺端口字段才补 `mixed-port`；其余 yaml 键原样透传、不重排序、不格式化重写。
- 用户手写的订阅/配置原件存 `imports\`，产物只写 `runtime\config.yaml`；**永不回写用户原件文件**。
- 轮询/自动刷新不得产生可见抖动：`ListView` 的 `ItemsSource` 用节点名作 key 的集合、就地更新属性，不整表重置。
- 长耗时操作（内核下载更新）必须有进行中反馈：按钮禁用 + 文案「下载更新中 NN%」，禁止阻塞式弹窗。
- UI 文案仅中文；深色 chrome 逐类核对（`Button`/`ComboBox`/`ComboBoxItem`/`ListBoxItem`/`ScrollBar` 含 `Track.Thumb` 与翻页 `RepeatButton`），判据为像素取样。
- 开销线（Task 2 建立基线，此后每次改 UI 复测）：Tray ≤ **40 MB**，Tray+内核 ≤ **80 MB**，UI ≤ **220 MB**。
- 每个任务结尾：`dotnet test` 相关项目全绿 → `git commit` → `git push origin feat/clashacry-v1`。
- **WireMock.Net 写法统一**：`server.Given(Request.Create()…匹配条件…).RespondWith(Response.Create()…响应…)`。`WithPath`/`UsingGet` 是请求侧，`WithStatusCode`/`WithBody`/`WithJson` 是响应侧——把响应方法挂在请求构造器上编译不过。JSON 响应一律用 `.WithBody(JsonSerializer.Serialize(obj))`，`Content-Type` 无所谓（客户端直接反序列化流）。
- 测试项目引用面：`tests/ClashAcry.UI.Tests` **只引用 `ClashAcry.Core`**，UI 侧一律经 FlaUI 从窗口外断言；不把 WinUI 应用工程拉进测试项目（XAML 编译器依赖会污染测试构建）。
- **管道 payload 三条硬规矩**（Task 3/10/14 共用，任何一条破坏都会让 UI 反序列化直接抛）：① 枚举一律走字符串（`JsonUtil` 装 `JsonStringEnumConverter`，`CoreState`/`UpdateResult` 不许变成 `3`）；② 跨进程的结构体全部住 `ClashAcry.Contracts`，字段用 `[JsonPropertyName]` 显式写 snake_case，不靠命名策略推断；③ **一个事件类型只有一种 payload 形状**（`settings.changed` 恒为 `SettingsSnapshot`），要加信息就加字段，不许换类型。
- **UIA 断言只有一个可靠读取点**：需要被断言的状态一律由 UI 自己写成 `TextBlock`（`BannerText`/`CoreStateLabel`/`BackdropSelectedLabel`/`Node_{Name}`…），不去读 `TogglePattern`/`SelectionItemPattern`/`TextBox` 内容——FlaUI 跨版本的 pattern 与 Name 聚合行为不一致，`Current.Name` 是属性、最稳。日志区因此用 `TextBlock` 而不是只读 `TextBox`。
- 跨进程 `Process.Start` 白名单只有两条：UI 拉起 Tray（Task 12）、Tray 拉起 UI（Task 10 `FocusUi`）。**任何一侧都不许起 mihomo**，内核进程只由 `CoreManager` 起停。

---

## 文件结构（先定边界，再拆任务）

```
ClashAcry.sln
Directory.Build.props
src/ClashAcry.Contracts/        (net10.0)  仅 DTO 与线格式，无业务逻辑
  ContractConstants.cs  CoreState.cs  Commands.cs  Events.cs  Frame.cs  FrameCodec.cs  JsonUtil.cs
src/ClashAcry.Core/             (net10.0-windows10.0.19041.0)
  Paths.cs  Settings.cs  SettingsStore.cs
  ConfigInjector.cs  SubscriptionImport.cs
  CoreManager.cs  StderrRing.cs  CoreProbe.cs
  SystemProxy.cs
  MihomoClient.cs  MihomoModels.cs
  CoreUpdater.cs  ReleaseClient.cs  ZipExtractor.cs
  BackdropKind.cs  BackdropPolicy.cs
  Proxies/ProxyNodeVm.cs  Proxies/ProxyGroupVm.cs  Proxies/ProxiesViewModel.cs  Proxies/FetchState.cs
  PipeServer.cs  PipeClient.cs
src/ClashAcry.Tray/             (WinExe，零 XAML)
  Program.cs  TrayHost.cs  TrayCommandRouting.cs  Win32NotifyIcon.cs  NativeMethods.cs  app.ico
src/ClashAcry.UI/               (WinUI 3 unpackaged)
  app.manifest  App.xaml App.xaml.cs  MainWindow.xaml MainWindow.xaml.cs
  Themes/FluentDarkOverrides.xaml
  Services/UiPrefs.cs  Services/TrayLink.cs  Services/SingleInstance.cs
  Backdrops/BackdropService.cs
  Views/OverviewPage.xaml(.cs)  Views/ProxiesPage.xaml(.cs)  Views/SettingsPage.xaml(.cs)
  ViewModels/OverviewViewModel.cs            // 节点页的模型在 Core/Proxies，UI 侧不重复建模
tests/ClashAcry.Core.Tests/     xUnit
tests/ClashAcry.UI.Tests/       FlaUI + xUnit（只引用 Core）
scripts/probe-mihomo-api.ps1  scripts/measure-residency.ps1  scripts/publish.ps1  scripts/fetch-bundled-core.ps1
packaging/core/mihomo-windows-amd64-compatible.exe  packaging/imports-sample/*.yaml
docs/superpowers/checklists/v1-acceptance.md
```

`Core` 不知道有 Tray 还是 UI；`Contracts` 不知道有 mihomo。依赖方向只能是 `UI → Core → Contracts` 与 `Tray → Core → Contracts`，`UI` 与 `Tray` 互不引用（它们只在命名管道上相遇）。

---

### Task 1: mihomo API 探针与契约回写

**Files:**
- Create: `scripts/probe-mihomo-api.ps1`
- Create: `packaging/imports-sample/minimal.yaml`
- Modify: `docs/superpowers/specs/2026-10-26-clashacry-v1-design.md`（§4.2 表格 + §12 风险条）

**Interfaces:**
- Consumes: 无
- Produces: 一份被证实的端点表（供 Task 6 `MihomoClient` 逐条对齐）；`packaging/imports-sample/minimal.yaml` 作为所有测试的夹具配置

- [x] **Step 1: 写最小探针配置**

创建 `packaging/imports-sample/minimal.yaml`（**故意保留注释与一个 provider 组**，用于同时验 yaml 保真与 provider 行为）：

```yaml
# probe fixture — 注释必须能被原样读回
mixed-port: 7897
external-controller: 127.0.0.1:9097
secret: probe-secret
proxies:
  - name: direct-node
    type: direct
proxy-groups:
  - name: PROBE-SELECT
    type: select
    proxies: [direct-node]
  - name: PROBE-AUTO
    type: url-test
    proxies: [direct-node]
    url: http://www.gstatic.com/generate_204
    interval: 600
```

- [x] **Step 2: 写探针脚本**

创建 `scripts/probe-mihomo-api.ps1`。脚本必须：把 `minimal.yaml` 复制到临时 runtime 目录 → 起 `packaging/core/mihomo-windows-amd64-compatible.exe -d <tmp>` → 轮询 `GET /version` 就绪 → 逐条打印「方法 路径 → HTTP 状态码 + 首 200 字符响应」→ 停内核。待核实清单（每条都要打印状态码，不允许凭记忆填表）：

```powershell
$core = Join-Path $PSScriptRoot '..\packaging\core\mihomo-windows-amd64-compatible.exe'
$run  = Join-Path $env:TEMP ('clashacry-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $run, (Join-Path $run 'runtime') | Out-Null
Copy-Item (Join-Path $PSScriptRoot '..\packaging\imports-sample\minimal.yaml') (Join-Path $run 'config.yaml')
$p = Start-Process $core -ArgumentList '-d', $run -PassThru -WindowStyle Hidden
try {
  $H = @{ Authorization = 'Bearer probe-secret' }
  $base = 'http://127.0.0.1:9097'
  for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    try { Invoke-RestMethod "$base/version" -Headers $H -TimeoutSec 1 | Out-Null; break } catch {}
  }
  function Probe($m, $path, $body) {
    # Windows PowerShell 5.1 兼容：不用三元运算符与 -SkipHttpErrorCheck
    $payload = $null
    if ($PSBoundParameters.Count -ge 3 -and $body) { $payload = ($body | ConvertTo-Json -Compress -Depth 5) }
    try {
      if ($payload) {
        $r = Invoke-WebRequest -Method $m -Uri "$base$path" -Headers $H -ContentType 'application/json' -Body $payload -UseBasicParsing -TimeoutSec 5
      } else {
        $r = Invoke-WebRequest -Method $m -Uri "$base$path" -Headers $H -UseBasicParsing -TimeoutSec 5
      }
      $c = ('' + $r.Content) -replace '\s+', ' '
      '{0,-6} {1,-52} -> {2} {3}' -f $m, $path, [int]$r.StatusCode, $c.Substring(0, [Math]::Min(200, $c.Length))
    } catch {
      $s = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 'ERR' }
      '{0,-6} {1,-52} -> {2} {3}' -f $m, $path, $s, $_.Exception.Message
    }
  }
  Probe GET    '/version'
  Probe GET    '/configs'
  Probe GET    '/proxies'
  Probe PUT    '/proxies/PROBE-SELECT' @{ name = 'direct-node' }
  Probe GET    '/proxies/PROBE-SELECT/delay?url=http%3A%2F%2Fwww.gstatic.com%2Fgenerate_204&timeout=5000'
  Probe GET    '/proxies/direct-node/delay?url=http%3A%2F%2Fwww.gstatic.com%2Fgenerate_204&timeout=5000'
  Probe GET    '/providers/proxies'
  Probe PATCH  '/configs' @{ mode = 'global' }
  Probe PUT    '/configs' @{ mode = 'rule' }
  Probe PATCH  '/configs' @{ path = (Join-Path $run 'config.yaml') }
  Probe PUT    '/configs' @{ path = (Join-Path $run 'config.yaml') }
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
  Remove-Item -Recurse -Force $run -ErrorAction SilentlyContinue
}
```

Run: `powershell -File scripts/probe-mihomo-api.ps1`
Expected: 12 行状态码输出，无 `ERR`（除故意探测的不支持组合）。

实测交付（Task 1 已完成）：脚本最终 **15 条探针**——为核实 provider 面加了 `PUT /proxies` 双请求体对照、provider 成员 `delay`、`GET/PUT /providers/proxies/{name}` 与两段结构化打印，并同时捕获 stdout/stderr。两处计划假设被实测推翻，已回写 spec §4.2：`proxies:` 里写 provider 名在 v1.19.33 是启动 fatal（正解 `use:`）、file 型 provider 的 `url` 可省略。另：脚本必须存成 **UTF-8 with BOM**，否则 PowerShell 5.1 按 GBK 解码，中文注释的末字节会吞掉下一条语句。

- [x] **Step 3: 读内核输出观察 mixed 端口占用行为**（原计划写「stderr」；实测日志在 stdout，stderr 恒空）

再跑一次脚本，在起内核前先占住 7897：`powershell -File scripts/probe-mihomo-api.ps1 -OccupyMixedPort`（脚本加该开关：用 `System.Net.Sockets.TcpListener` 提前绑定 7897）。记录：controller 是否正常、`GET /version` 是否 200、内核是否退出。这条直接决定 Task 8 `CoreManager` 的 `Running` 判据是否必须包含 mixed 端口 TCP 探活。

- [x] **Step 4: 回写 spec**

把实测结果**逐条**写进 spec §4.2：每行端点后追加实测状态码；`PUT` 与 `PATCH` 谁可用写清；`GET /configs` 不支持就把该行划掉并注明「404 实测」。还必须记录 **`GET /providers/proxies` 响应根的键名与形状**（`provider` 对象表还是数组），Task 6 的 `ProvidersResponse` 按它定型。同时在 §12 追加两条实测结论：provider 成员是否出现在 `GET /proxies` 顶层表、`delay` 对成员是否 404（404 就把兜底策略从「风险」升级为「既定实现要求」写进 §4.2）。若行为与本计划 Task 6 的签名冲突，以 spec 为准并改 Task 6。

- [x] **Step 5: 提交**

```bash
git add scripts/probe-mihomo-api.ps1 packaging/imports-sample/minimal.yaml docs/superpowers/specs
git commit -m "probe: 真机核实 mihomo REST 端点形状并回写 spec §4.2"
git push origin feat/clashacry-v1
```

---

### Task 2: 解决方案脚手架与常驻开销基线

**Files:**
- Create: `ClashAcry.sln`, `Directory.Build.props`
- Create: `src/ClashAcry.Contracts/ClashAcry.Contracts.csproj`, `src/ClashAcry.Core/ClashAcry.Core.csproj`, `src/ClashAcry.Tray/ClashAcry.Tray.csproj`, `src/ClashAcry.UI/ClashAcry.UI.csproj`（空壳）
- Create: `tests/ClashAcry.Core.Tests/ClashAcry.Core.Tests.csproj`
- Create: `scripts/measure-residency.ps1`
- Modify: `docs/superpowers/specs/...-design.md`（§10 表格追加实测列）

**Interfaces:**
- Consumes: 无
- Produces: 可编译的四项目 + 测试项目；`scripts/measure-residency.ps1 -ProcessName ClashAcry.Tray` 输出 JSON `{name, working_set_mb, private_mb}`

- [ ] **Step 1: 写 Directory.Build.props**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Platforms>x64</Platforms>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: 建四个项目 + 测试项目**

```bash
dotnet new classlib   -n ClashAcry.Contracts -o src/ClashAcry.Contracts
dotnet new classlib   -n ClashAcry.Core      -o src/ClashAcry.Core
dotnet new console    -n ClashAcry.Tray      -o src/ClashAcry.Tray
dotnet new app        -n ClashAcry.UI        -o src/ClashAcry.UI   # 若模板不存在则手写 csproj（Step 3）
dotnet new xunit      -n ClashAcry.Core.Tests -o tests/ClashAcry.Core.Tests
dotnet new xunit      -n ClashAcry.UI.Tests  -o tests/ClashAcry.UI.Tests
dotnet new sln -n ClashAcry
for p in src/*/*.csproj tests/*/*.csproj; do dotnet sln add "$p"; done
dotnet add src/ClashAcry.Core reference src/ClashAcry.Contracts
dotnet add src/ClashAcry.Tray reference src/ClashAcry.Core
dotnet add src/ClashAcry.UI  reference src/ClashAcry.Contracts
dotnet add src/ClashAcry.UI  reference src/ClashAcry.Core
dotnet add tests/ClashAcry.Core.Tests reference src/ClashAcry.Core
dotnet add tests/ClashAcry.Core.Tests package WireMock.Net
dotnet add tests/ClashAcry.UI.Tests reference src/ClashAcry.Core      # 只引 Core：把 WinUI 工程拉进测试项目会带来整套 XAML 编译器依赖
dotnet add tests/ClashAcry.UI.Tests package WireMock.Net
dotnet add tests/ClashAcry.UI.Tests package FlaUI.UIA3
dotnet add tests/ClashAcry.UI.Tests package System.Drawing.Common    # 只给 Task 16 的像素取样用；app 侧仍然禁 System.Drawing
```

`ClashAcry.Contracts` 的 csproj 里覆写 `<TargetFramework>net10.0</TargetFramework>`（不碰 Win32）。`Core` 加包：`YamlDotNet`、`Microsoft.Win32.Registry`、`System.IO.Pipes.AccessControl`（后者在 Windows 打包内，若还原不到则不引，`PipeSecurity` 已随 `System.IO.Pipes` 提供）。

- [ ] **Step 3: UI 壳 csproj（unpackaged self-contained）**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <UseWinUI>true</UseWinUI>
    <WindowsPackageType>None</WindowsPackageType>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.5.1" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.1" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Tray 壳 csproj（禁止 XAML/WinForms）**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <ServerGarbageCollection>false</ServerGarbageCollection>
    <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\ClashAcry.Core\ClashAcry.Core.csproj" />
  </ItemGroup>
</Project>
```

`Program.cs` 先只写 `Thread.Sleep(Timeout.Infinite)`，占位用（Task 10 替换）。

- [ ] **Step 5: 写开销测量脚本**

```powershell
param([string]$ProcessName = 'ClashAcry.Tray', [switch]$Json)
$p = Get-Process -Name $ProcessName -ErrorAction Stop
$ws = @(); $pr = @()
1..5 | ForEach-Object {
  $p.Refresh()
  $ws += [math]::Round($p.WorkingSet64 / 1MB, 1)
  $pr += [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
  Start-Sleep -Milliseconds 400
}
$med = { param($a) ($a | Sort-Object)[[int]($a.Count/2)] }
if ($Json) {
  @{ name = $ProcessName; working_set_mb = & $med $ws; private_mb = & $med $pr } | ConvertTo-Json -Compress
} else {
  "{0}: WS={1}MB Private={2}MB (中位数 x5)" -f $ProcessName, (& $med $ws), (& $med $pr)
}
```

- [ ] **Step 6: 建立基线并回写 spec §10**

```bash
dotnet build ClashAcry.sln -c Release
dotnet test tests/ClashAcry.Core.Tests -c Release
dotnet publish src/ClashAcry.Tray -c Release -r win-x64 --self-contained
Start-Process "$PWD\src\ClashAcry.Tray\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\ClashAcry.Tray.exe" -PassThru
powershell -File scripts/measure-residency.ps1 -ProcessName ClashAcry.Tray -Json
```
Expected: 空壳 Tray 的 WS 数值。**把数字写进 spec §10 表格新增的「实测（空壳）」列**。若空壳已 > 40 MB，停下来报告（不要放宽判据），并给出「换更小的发布配置 / 调整判据」两个选项让用户拍板。
另外验证 trimming：`dotnet publish src/ClashAcry.Tray -c Release -r win-x64 --self-contained -p:PublishTrimmed=true` —— 出现任何 `IL2xxx` 警告则记录并放弃 trimming，结论写进 spec §2 的 NativeAOT/trimming 段。

- [ ] **Step 7: 提交**

```bash
git add . && git commit -m "chore: 四项目解决方案脚手架与常驻开销测量脚本" && git push origin feat/clashacry-v1
```

---

### Task 3: Contracts —— 管道帧格式与消息类型

**Files:**
- Create: `src/ClashAcry.Contracts/ContractConstants.cs` `CoreState.cs` `Commands.cs` `Events.cs` `Frame.cs` `FrameCodec.cs` `JsonUtil.cs`
- Test: `tests/ClashAcry.Core.Tests/FrameCodecTests.cs`（Contracts 的线格式由 Core.Tests 覆盖，避免再开一个测试项目）

**Interfaces:**
- Consumes: 无
- Produces:
  - `sealed record Frame(string Kind, string Type, JsonElement? Payload, long Seq, string? Id, string? ReplyTo, string? Error)`
  - `static class FrameCodec { const int MaxFrameBytes = 1 << 20; string Encode(Frame f); Frame Decode(string line); Task<Frame?> ReadLineAsync(StreamReader r, CancellationToken ct); }`
  - `enum CoreState { Stopped, Starting, Running, Crashed, Updating }`
  - `static class CmdTypes { const string Handshake="handshake"; CoreStart="core.start"; CoreStop="core.stop"; CoreReload="core.reload"; ModeSet="mode.set"; SysProxySet="sysproxy.set"; CoreUpdate="core.update"; SubImport="sub.import"; SettingsUpdate="settings.update"; UiFocus="ui.focus"; }`
  - `static class EventTypes { CoreStateChanged="core.state"; UpdateProgress="core.update.progress"; UpdateResult="core.update.result"; SettingsChanged="settings.changed"; }`（四个事件名即全部事件；日志文本不单独成事件，随 `CoreStateChanged.StderrTail` 下发）
  - `sealed record HandshakeReply(CoreState State, string Host, int Port, string Secret, int MixedPort, string? CoreVersion, string DelayTestUrl)` —— 一次握手把「连内核所需的全部参数」交给 UI，`DelayTestUrl` 必须在这里：UI 的测速要用它，不该为它再开一条读设置的通路
  - `sealed record ImportRequest(string? Url = null, string? Path = null)` —— `sub.import` 的请求体；二者取其一，`Path` 非空走本地 yaml 通路（spec §6）
  - `sealed record ImportReply(string? ConfigFile, string? Reason = null)` —— `sub.import` 的回包，`Reason == null` 即成功
  - `sealed record CoreStateChanged(CoreState State, string? Reason, IReadOnlyList<string> StderrTail)` / `sealed record UpdateProgress(long Received, long Total, int Pct)` / `enum UpdateResult { Updated, AlreadyLatest, DownloadFailed, ExtractFailed, InUseRolledBack }` —— 三个跨进程 payload 类型在本任务一次建齐，Task 8/11 只 import 不重复定义（Global Constraint：payload 全住 Contracts）

- [ ] **Step 1: 写失败的测试**

`tests/ClashAcry.Core.Tests/FrameCodecTests.cs`：

```csharp
using System.Text.Json;
using ClashAcry.Contracts;
using Xunit;

namespace ClashAcry.Core.Tests;

public class FrameCodecTests
{
    [Fact]
    public void Encode_ProducesSingleLine_WhenPayloadContainsNewlinesAndQuotes()
    {
        var payload = JsonUtil.Element(new { msg = "line1\nline2\"quoted\"" });
        var line = FrameCodec.Encode(new Frame("command", CmdTypes.Handshake, payload, 7, "id-1", null, null));

        Assert.DoesNotContain('\n', line.TrimEnd('\n'));
        Assert.EndsWith("\n", line);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = new Frame("reply", EventTypes.CoreStateChanged,
            JsonUtil.Element(new { state = "Running" }), 42, "r-2", "c-9", null);

        var back = FrameCodec.Decode(FrameCodec.Encode(original).TrimEnd('\n'));

        Assert.Equal(original.Kind, back.Kind);
        Assert.Equal(original.Type, back.Type);
        Assert.Equal(original.Seq, back.Seq);
        Assert.Equal(original.ReplyTo, back.ReplyTo);
        Assert.Equal("Running", back!.Payload!.Value.GetProperty("state").GetString());
    }

    [Fact]
    public void Decode_RejectsFrameLongerThanMaxFrameBytes()
    {
        var tooLong = new string('x', FrameCodec.MaxFrameBytes + 1);
        Assert.Throws<FrameTooLongException>(() => FrameCodec.Decode(tooLong));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter FrameCodecTests -v minimal`
Expected: FAIL / 编译错误 "The type or namespace name 'Frame' could not be found"

- [ ] **Step 3: 实现**

```csharp
// ContractConstants.cs
namespace ClashAcry.Contracts;
public static class ContractConstants
{
    public const string PipeName = "clashacry.tray.v1";
    public const int ProtocolVersion = 1;
}

// CoreState.cs
namespace ClashAcry.Contracts;
public enum CoreState { Stopped, Starting, Running, Crashed, Updating }

// Commands.cs / Events.cs
namespace ClashAcry.Contracts;
public static class CmdTypes
{
    public const string Handshake = "handshake", CoreStart = "core.start", CoreStop = "core.stop",
        CoreReload = "core.reload", ModeSet = "mode.set", SysProxySet = "sysproxy.set",
        CoreUpdate = "core.update", SubImport = "sub.import",
        SettingsUpdate = "settings.update", UiFocus = "ui.focus";
}
public static class EventTypes
{
    public const string CoreStateChanged = "core.state", UpdateProgress = "core.update.progress",
        UpdateResult = "core.update.result",
        SettingsChanged = "settings.changed", Log = "log";
}

// Frame.cs
using System.Text.Json;
using System.Text.Json.Serialization;
namespace ClashAcry.Contracts;
public sealed record Frame(string Kind, string Type, JsonElement? Payload, long Seq,
                            string? Id, string? ReplyTo, string? Error);
public sealed record HandshakeReply(CoreState State, string Host, int Port, string Secret,
                                     int MixedPort, string? CoreVersion, string DelayTestUrl);
/// sub.import 的请求体 / 回包：Reason 为空即成功；UI 直接把它当"行内结果"文案的数据源（spec §6 失败保留旧配置）。
public sealed record ImportRequest([property: JsonPropertyName("url")] string? Url = null,
                                   [property: JsonPropertyName("path")] string? Path = null);
public sealed record ImportReply([property: JsonPropertyName("config_file")] string? ConfigFile,
                                 [property: JsonPropertyName("reason")] string? Reason = null);

/// 以下三型是 `core.state` / `core.update.progress` / `core.update.result` 三种事件的唯一 payload（Global Constraint：一个事件一种形状）。
/// `CoreStateChanged` 由 `CoreManager.Transition` 唯一出口产出（Task 8）、`CoreUpdater` 的进度/结果由 Task 11 产出，两侧都只 import 不重复定义。
public sealed record CoreStateChanged([property: JsonPropertyName("state")] CoreState State,
                                      [property: JsonPropertyName("reason")] string? Reason,
                                      [property: JsonPropertyName("stderr_tail")] IReadOnlyList<string> StderrTail);
public sealed record UpdateProgress([property: JsonPropertyName("received")] long Received,
                                    [property: JsonPropertyName("total")] long Total,
                                    [property: JsonPropertyName("pct")] int Pct);
public enum UpdateResult { Updated, AlreadyLatest, DownloadFailed, ExtractFailed, InUseRolledBack }

// FrameCodec.cs
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace ClashAcry.Contracts;

/// 线格式：一帧一行 UTF-8 JSON + '\n'。System.Text.Json 默认转义换行、不缩进，
/// 因此任何字段值都不可能引入裸换行——这是行分帧成立的前提，不要打开 WriteIndented。
public static class FrameCodec
{
    public const int MaxFrameBytes = 1 << 20;
    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Json.Encodings.Default.JavaScriptEncoder
    };

    public static string Encode(Frame f) => JsonSerializer.Serialize(f, Opts) + "\n";

    public static Frame Decode(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaxFrameBytes) throw new FrameTooLongException(line.Length);
        return JsonSerializer.Deserialize<Frame>(line, Opts) ?? throw new InvalidDataException("empty frame");
    }

    public static async Task<Frame?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var line = await reader.ReadLineAsync(ct);
        return line is null ? null : Decode(line);
    }
}

public sealed class FrameTooLongException : InvalidDataException
{
    public FrameTooLongException(int chars) : base($"frame too long: {chars} chars") { }
}

// JsonUtil.cs — 集中一处放 JSON 选项，供各任务复用。类名不得叫 JsonSerializer（会遮蔽 System.Text.Json.JsonSerializer）。
using System.Text.Json;
using System.Text.Json.Serialization;
namespace ClashAcry.Contracts;
public static class JsonUtil
{
    // JsonStringEnumConverter 是硬要求：System.Text.Json 默认把枚举写成数字，
    // 而 core.update.result（UpdateResult）、core.state（CoreState）在 UI 侧要能与人手写的
    // "Updated" / "Running" 字面量互认，测试里也是按字符串发的。
    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Opts);
    public static T Read<T>(JsonElement? element) =>
        element is null ? default! : element.Value.Deserialize<T>(Opts)!;
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter FrameCodecTests -v minimal`
Expected: PASS，3/3。

- [ ] **Step 5: 提交**

```bash
git add src/ClashAcry.Contracts tests/ClashAcry.Core.Tests/FrameCodecTests.cs
git commit -m "feat(contracts): 命名管道行分帧协议与命令/事件类型"
```

---

### Task 4: Paths 与 SettingsStore（原子写 + 单一写者）

**Files:**
- Create: `src/ClashAcry.Core/Paths.cs` `Settings.cs` `SettingsStore.cs`
- Test: `tests/ClashAcry.Core.Tests/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `static class AppPaths { string Root; string RuntimeDir; string ImportsDir; string CoreDir; string SettingsFile; string ConfigFile; string CoreExecutable; string CoreVersionFile; void EnsureCreated(); }`（`AppPaths.ForTest(string dir)` 供测试注入）
  - `sealed record Settings { string ControllerHost; int ControllerPort; string Secret; int MixedPort; string? CorePath; string DelayTestUrl; string? LastSubscription; string? CoreVersion; }`（record：路由与更新器要用 `with` 做局部改动）
  - `sealed class SettingsStore { Settings Load(); void Save(Settings s); Settings Update(Func<Settings,Settings> mutate); }`

- [ ] **Step 1: 写失败的测试**

```csharp
public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ca-settings-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private SettingsStore Store() => new(AppPaths.ForTest(_dir));

    [Fact]
    public void FirstLoad_CreatesFileWithRandomSecretAndDefaults()
    {
        var s = Store().Load();

        Assert.Equal(9097, s.ControllerPort);
        Assert.Equal(7897, s.MixedPort);
        Assert.Equal(32, s.Secret.Length);
        Assert.Matches("^[0-9a-f]{32}$", s.Secret);
        Assert.True(File.Exists(AppPaths.ForTest(_dir).SettingsFile));
    }

    [Fact]
    public void Secret_StableAcrossReloads()
    {
        var first = Store().Load().Secret;
        Assert.Equal(first, Store().Load().Secret);
    }

    [Fact]
    public void Save_LeavesNoTempFiles_AndBacksUpPreviousVersion()
    {
        var store = Store();
        store.Update(s => s with { MixedPort = 7899 });
        var files = Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray();

        Assert.Contains("settings.json", files);
        Assert.DoesNotContain(files, f => f!.StartsWith(".tmp"));
    }

    [Fact]
    public void CorruptedFile_FallsBackWithBackupPreserved()
    {
        File.WriteAllText(AppPaths.ForTest(_dir).SettingsFile, "{ not json");
        var s = Store().Load();

        Assert.Equal(9097, s.ControllerPort);
        Assert.True(File.Exists(AppPaths.ForTest(_dir).SettingsFile + ".corrupt"));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter SettingsStoreTests`
Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现**

```csharp
// Paths.cs
namespace ClashAcry.Core;

public sealed class AppPaths
{
    public string Root { get; }
    public string RuntimeDir => Path.Combine(Root, "runtime");
    public string ImportsDir => Path.Combine(Root, "imports");
    public string CoreDir => Path.Combine(Root, "core");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string ConfigFile => Path.Combine(RuntimeDir, "config.yaml");
    public string CoreExecutable => Path.Combine(CoreDir, "mihomo.exe");
    public string CoreVersionFile => Path.Combine(CoreDir, "version.txt");

    private AppPaths(string root) => Root = root;

    public static AppPaths Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClashAcry"));
    public static AppPaths ForTest(string dir) => new(dir);

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, RuntimeDir, ImportsDir, CoreDir })
            Directory.CreateDirectory(d);
    }
}

// Settings.cs
namespace ClashAcry.Core;

public sealed record Settings(
    string ControllerHost = "127.0.0.1",
    int ControllerPort = 9097,
    string Secret = "",
    int MixedPort = 7897,
    string? CorePath = null,
    string DelayTestUrl = "http://www.gstatic.com/generate_204",
    string? LastSubscription = null,
    string? CoreVersion = null);

// SettingsStore.cs
using System.Security.Cryptography;
using System.Text.Json;
namespace ClashAcry.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly object _gate = new();

    public SettingsStore(AppPaths paths) { _paths = paths; _paths.EnsureCreated(); }

    public Settings Load()
    {
        lock (_gate)
        {
            if (File.Exists(_paths.SettingsFile))
            {
                try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_paths.SettingsFile), Opts) ?? New(); }
                catch (JsonException)
                {
                    File.Copy(_paths.SettingsFile, _paths.SettingsFile + ".corrupt", overwrite: true);
                }
            }
            var fresh = New();
            Write(fresh);
            return fresh;
        }
    }

    public Settings Update(Func<Settings, Settings> mutate)
    {
        lock (_gate) { var next = mutate(Load()); Write(next); return next; }
    }

    public void Save(Settings s) { lock (_gate) Write(s); }

    private static Settings New() =>
        new(Secret: Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());

    private void Write(Settings s)
    {
        var tmp = Path.Combine(_paths.Root, $".tmp-{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opts));
        var target = _paths.SettingsFile;
        if (File.Exists(target)) File.Replace(tmp, target, target + ".bak");
        else File.Move(tmp, target);
    }
}
```

注：`Settings` 是 positional record，默认值即上表；`New()` 只负责补随机 secret。测试里断言 `.tmp` 不残留时，实现必须用 `Path.Combine(_paths.Root, ".tmp-...")` 的同目录临时文件（跨目录 `File.Replace` 会失败）。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter SettingsStoreTests`
Expected: PASS，4/4。

- [ ] **Step 5: 提交**

```bash
git add src/ClashAcry.Core tests/ClashAcry.Core.Tests/SettingsStoreTests.cs
git commit -m "feat(core): 设置模型与原子写存储（损坏留 .corrupt，覆盖留 .bak）"
```

---

### Task 5: ConfigInjector —— 最小键覆盖，其余字节级不动

**Files:**
- Create: `src/ClashAcry.Core/ConfigInjector.cs` `SubscriptionImport.cs`
- Test: `tests/ClashAcry.Core.Tests/ConfigInjectorTests.cs` `SubscriptionImportTests.cs`

**Interfaces:**
- Consumes: `Settings`（Task 4）、`AppPaths`
- Produces:
  - `static class ConfigInjector { const string RequiredKeys = ...; string Inject(string yaml, Settings s); }`
  - `sealed class SubscriptionImport { ImportResult ImportFromUrl(string url, HttpClient http, CancellationToken ct); ImportResult ImportFromFile(string path); ImportResult ImportFromText(string raw); }`（前两个只负责取字节，嗅探/存档/注入统一落在 `ImportFromText`）
  - `sealed record ImportResult(string OriginalFile, string ConfigFile, string ReasonIfFailed)`；失败时 `ConfigFile` 指向旧配置且**不删除任何东西**。

- [ ] **Step 1: 写失败的测试（断言"其余字段一字不改"）**

```csharp
public class ConfigInjectorTests
{
    private static readonly Settings S = new(ControllerHost: "127.0.0.1", ControllerPort: 9097,
        Secret: "abcd", MixedPort: 7897);

    [Fact]
    public void Inject_OverwritesOnlyRequiredKeys_AndKeepsUserOrdering()
    {
        var input = "mode: rule\n# 用户的注释\nproxies:\n  - name: a\n    type: ss\nport: 7890\n";

        var output = ConfigInjector.Inject(input, S);

        Assert.Contains("mode: rule", output);
        Assert.Contains("- name: a", output);
        Assert.Contains("external-controller: 127.0.0.1:9097", output);
        Assert.Contains("secret: abcd", output);
        Assert.Contains("allow-lan: false", output);
        Assert.Contains("unified-delay: true", output);
        Assert.Contains("port: 7890", output);          // 已有端口字段 → 不补 mixed-port
        Assert.DoesNotContain("mixed-port", output);
    }

    [Fact]
    public void Inject_AddsMixedPort_OnlyWhenNoPortFieldPresent()
    {
        var output = ConfigInjector.Inject("mode: rule\n", S);
        Assert.Contains("mixed-port: 7897", output);
    }

    [Fact]
    public void Inject_ReplacesExistingRequiredKeyInsteadOfDuplicating()
    {
        var output = ConfigInjector.Inject("secret: old\nallow-lan: true\n", S);

        Assert.Single(Match(output, "secret:"));
        Assert.Equal("secret: abcd", Match(output, "secret:").Single());
        Assert.Equal("allow-lan: false", Match(output, "allow-lan:").Single());
    }

    [Fact]
    public void Inject_ThrowsOnNonMappingRoot()
    {
        Assert.Throws<ConfigShapeException>(() => ConfigInjector.Inject("- just\n- a list\n", S));
    }

    private static string[] Match(string text, string key) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith(key)).ToArray();
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter ConfigInjectorTests`
Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现（用 YamlDotNet 的 DOM，避免序列化模型丢字段）**

```csharp
using System.Text;
using YamlDotNet.RepresentationModel;
namespace ClashAcry.Core;

/// 只覆盖顶层必需键。用 YamlStream/DOM 而非强类型模型，是为了保证订阅里我们不认识的字段
/// （geodata-mode、dns、experimental…）原样透传 —— spec §7「注入最小化」。
public static class ConfigInjector
{
    private static readonly string[] PortKeys = ["port", "socks-port", "mixed-port", "tproxy"];

    public static string Inject(string yaml, Settings s)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new ConfigShapeException("配置根节点必须是单一 YAML 映射");

        var overrides = new Dictionary<string, YamlNode>(StringComparer.Ordinal)
        {
            ["external-controller"] = new YamlScalarNode($"{s.ControllerHost}:{s.ControllerPort}"),
            ["secret"] = new YamlScalarNode(s.Secret),
            ["allow-lan"] = new YamlScalarNode("false"),
            ["unified-delay"] = new YamlScalarNode("true"),
        };
        bool AnyPortKey() => root.Children.Any(c => c.Key is YamlScalarNode k && PortKeys.Contains(k.Value ?? ""));
        if (!AnyPortKey()) overrides["mixed-port"] = new YamlScalarNode(s.MixedPort.ToString());

        // 未触及的键值对原样搬走（复用同一个 YamlNode 实例），只对必需键换值节点，
        // 这样键序与缩进风格都不受我们影响。
        var pairs = new List<KeyValuePair<YamlNode, YamlNode>>();
        foreach (var child in root.Children)
        {
            if (child.Key is not YamlScalarNode key)
                throw new ConfigShapeException($"顶层键必须是标量，实为 {child.Key.NodeType}");
            if (overrides.TryGetValue(key.Value ?? "", out var replacement))
            {
                pairs.Add(new KeyValuePair<YamlNode, YamlNode>(key, replacement));
                overrides.Remove(key.Value ?? "");
            }
            else pairs.Add(child);
        }
        foreach (var kv in overrides) pairs.Add(new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(kv.Key), kv.Value));

        stream.Documents[0].RootNode = new YamlMappingNode(pairs);
        var sw = new StringWriter();
        stream.Save(sw);              // 若该重载不存在，改 stream.Save(sw, omitInlineCommentMarker: false)
        return sw.ToString();
    }
}

public sealed class ConfigShapeException : Exception
{
    public ConfigShapeException(string m) : base(m) { }
}
```

`YamlMappingNode` 的构造重载若不接受 `IEnumerable<KeyValuePair<YamlNode, YamlNode>>`，用 `var m = new YamlMappingNode(); foreach (var p in pairs) m.Add(p.Key, p.Value);` 等价替代。**不许退回字符串替换来实现注入。**

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter ConfigInjectorTests`
Expected: PASS，4/4。**同时肉眼比对一次输出**：把 Task 1 的 `minimal.yaml` 喂进去，确认 `proxy-groups` 与 `proxies` 的嵌套结构仍是 block 风格；若变成 flow 风格，记录为已知限制写进 spec §12（内核仍能读，但人类难读）。

Task 1 实测带来的两条硬约束（改注入器时不许破）：
- **`proxy-groups` 整棵子树原样透传**，尤其 `use:` 列表。v1.19.33 里把 provider 名当节点名写进 `proxies:` 是**启动 fatal**（`proxy group[1]: PROBE-SELECT: 'PROBE-PROV' not found`），聚合订阅的唯一正确写法是 `use:`。注入器若"顺手"把 `use:` 展平、丢弃或改名，用户订阅会直接起不来。
- **`proxy-providers` 整棵子树同样原样透传**，不得补 `url`、不得改 `type`/`path`/`interval`。实测 file 型 provider 只写 `type/path/interval` 就能加载（`url` 可省略），擅自补一个不可达 URL 虽被接受但会误导用户。

- [ ] **Step 5: SubscriptionImport 与它的测试**

`SubscriptionImport` 的行为契约（测试必须逐条覆盖）：
1. 下载或读入的原文**先原样落盘** `imports\<kind>-<sha8>.yaml`，之后才注入产物 `runtime\config.yaml`；
2. 内容嗅探：以 `proxies:`/`proxy-groups:`/`mixed-port` 等顶层键开头视为 Clash yaml；`ss://`/`vmess://`/`trojan://` 行列表 → `Reason = "v1 不支持节点行导入（spec §6）"` 且**不写任何文件**；
3. 任何下载/解析失败：返回 `ImportResult(旧 ConfigFile, 旧 ConfigFile, Reason)`，绝不清空现有配置。

```csharp
public class SubscriptionImportTests : IDisposable
{
    private readonly string Dir = Directory.CreateTempSubdirectory("ca-sub-").FullName;
    private readonly HttpClient Http = new();
    public void Dispose() { Http.Dispose(); Directory.Delete(Dir, true); }

    private (AppPaths P, SettingsStore S) Fixture(string original)
    {
        var p = AppPaths.ForTest(Dir); p.EnsureCreated();
        File.WriteAllText(p.ConfigFile, original);                 // 先有一份可用的旧配置
        return (p, new SettingsStore(p));
    }

    [Fact]
    public void Import_DownloadsFails_KeepsOldConfigByteIdentical()
    {
        var wire = WireMockServer.Start();
        wire.Given(Request.Create().WithPath("/sub").UsingGet()).RespondWith(Response.Create().WithStatusCode(500));
        var (p, s) = Fixture("mode: rule\nsecret: keep\n");

        var r = new SubscriptionImport(p, s).ImportFromUrl(wire.Url + "/sub", Http, default);

        Assert.Equal("mode: rule\nsecret: keep\n", File.ReadAllText(p.ConfigFile));   // 契约 3：绝不清空
        Assert.NotEmpty(r.ReasonIfFailed);
        wire.Dispose();
    }

    [Fact]
    public void Import_ArchivesRawSourceBeforeInjecting()
    {
        // 契约 1：用户原文只进 imports\，注入产物只进 runtime\config.yaml；原件永不被改（红线）
        var yaml = "mode: rule\nmixed-port: 7890\nproxies:\n  - { name: n1, type: ss, server: 1.2.3.4 }\n";
        var wire = WireMockServer.Start();
        wire.Given(Request.Create().WithPath("/sub").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(yaml, "text/plain"));
        var (p, s) = Fixture("mode: old\n");

        var r = new SubscriptionImport(p, s).ImportFromUrl(wire.Url + "/sub", Http, default);

        Assert.Equal("", r.ReasonIfFailed);
        var raw = Directory.GetFiles(p.ImportsDir, "*.yaml");
        Assert.Single(raw);                                        // 存档恰好一份，文件名含 sha8 前缀
        Assert.Equal(yaml.Replace("\r\n", "\n"), File.ReadAllText(raw[0]).Replace("\r\n", "\n"));  // 原样，未注入
        Assert.Equal(p.ConfigFile, r.ConfigFile);
        Assert.Contains("external-controller", File.ReadAllText(p.ConfigFile));                    // 产物才带注入键
        wire.Dispose();
    }

    [Fact]
    public void Import_RejectsNodeLineList_WritesNothing()
    {
        // 契约 2：ss:// 行列表是 v1 明确不做（spec §6），必须报错且一个文件都不落
        var body = "ss://AAA@1.2.3.4:8389#hk1\nvmess://BBB#jp2\n";
        var (p, s) = Fixture("mode: old\n");

        var r = new SubscriptionImport(p, s).ImportFromText(body);

        Assert.Contains("不支持节点行导入", r.ReasonIfFailed);
        Assert.Equal("mode: old\n", File.ReadAllText(p.ConfigFile));                 // 旧配置不动
        Assert.False(Directory.Exists(p.ImportsDir) && Directory.GetFiles(p.ImportsDir).Length > 0);  // 连存档都不写
    }
}
```

`ImportFromText(string)` 是 `ImportFromUrl`/`ImportFromFile` 共用的内部落点（下载/读文件只做取字节，嗅探与落盘在它里面），测试直接打它，不为了第三个用例再造一个 WireMock。

Run: `dotnet test tests/ClashAcry.Core.Tests --filter SubscriptionImportTests`
Expected: PASS，3/3（三条行为契约各一条：失败不动旧配置 / 原文先存档再注入 / 节点行导入拒绝且零落盘）。

- [ ] **Step 6: 提交**

```bash
git add src/ClashAcry.Core tests/ClashAcry.Core.Tests
git commit -m "feat(core): yaml 最小键注入与订阅导入（失败保留旧配置）"
```

---

### Task 6: MihomoClient —— 类型化 REST，错误原样透出

**Files:**
- Create: `src/ClashAcry.Core/MihomoClient.cs` `MihomoModels.cs`
- Test: `tests/ClashAcry.Core.Tests/MihomoClientTests.cs`

**Interfaces:**
- Consumes: **Task 1 回写后的 spec §4.2 端点表**（方法名/请求体形状以它为准；本任务的签名如与实测冲突，改本任务）
- Produces:
  - `sealed class MihomoClient { MihomoClient(HttpClient http, string host, int port, string secret); Task<VersionInfo?> GetVersionAsync(CancellationToken ct); Task<ProxiesResponse> GetProxiesAsync(CancellationToken ct); Task SetProxyAsync(string group, string node, CancellationToken ct); Task<int> GetDelayAsync(string name, string url, int timeoutMs, CancellationToken ct); Task<ProvidersResponse> GetProvidersAsync(CancellationToken ct); Task RefreshProviderAsync(string name, CancellationToken ct); Task SetModeAsync(string mode, CancellationToken ct); Task ReloadConfigAsync(string path, CancellationToken ct); Task<CoreConfigView?> GetConfigsAsync(CancellationToken ct); }`
  - `ApiException(int StatusCode, string Body)`；`TimeoutException` 与 409 一律折成 `GetDelayAsync` 的 `-1`。

- [ ] **Step 1: 写失败的测试**

```csharp
public class MihomoClientTests : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly WireMockServer _wire = WireMockServer.Start();
    public MihomoClientTests() => _wire.Reset();
    public void Dispose() => _wire.Dispose();
    private MihomoClient Client() => new(Http, "127.0.0.1", _wire.Port, "sec");

    [Fact]
    public async Task GetProxies_ParsesProviderDashMember()
    {
        _wire.Given(Request.Create().WithPath("/proxies").UsingGet()).RespondWith(
            Response.Create().WithBody(JsonSerializer.Serialize(new
            {
                proxies = new { PROBE = new { @name = "PROBE", @type = "select", now = "n1", hidden = false,
                    all = new[] { "n1" }, history = new[] { new { time = "t", delay = 12 } } } }
            })));

        var r = await Client().GetProxiesAsync(default);
        var g = r.Proxies["PROBE"];

        Assert.Equal("n1", g.Now);
        Assert.Equal(12, g.History![0].Delay);
    }

    [Fact]
    public async Task Delay_ReturnsMinusOne_On409()
    {
        _wire.Reset();
        _wire.Given(Request.Create().WithPath("/proxies/x/delay").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(409).WithBody("unproxy"));
        Assert.Equal(-1, await Client().GetDelayAsync("x", "http://e/204", 5000, default));
    }

    [Fact]
    public async Task SetProxy_UsesBodyNameField_AndPathGroupName()
    {
        await Client().SetProxyAsync("PROBE", "n1", default);
        var req = _wire.LogEntries.Last().RequestMessage;
        Assert.Equal("/proxies/PROBE", req.Path);
        Assert.Equal("PUT", req.Method);
        Assert.Equal("n1", JsonDocument.Parse(req.Body!).RootElement.GetProperty("name").GetString());
        Assert.Equal("Bearer sec", req.Headers["Authorization"]);
    }

    [Fact]
    public async Task ApiException_ExposesStatusCodeAndBody()
    {
        _wire.Reset();
        _wire.Given(Request.Create().WithPath("/version").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(401).WithBody("unauthorized"));
        var ex = await Assert.ThrowsAsync<ApiException>(() => Client().GetVersionAsync(default));
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("unauthorized", ex.Body);
    }
}
```

（夹具要求：`_wire.Reset()` 在每个用例前调用；`HttpClient` 由测试基类提供并设 `Timeout = 5s`。）

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter MihomoClientTests`
Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现**

```csharp
// MihomoModels.cs
using System.Text.Json.Serialization;
namespace ClashAcry.Core;

public sealed record VersionInfo([property: JsonPropertyName("meta")] bool Meta,
                                 [property: JsonPropertyName("version")] string Version);

public sealed record DelaySample([property: JsonPropertyName("time")] string? Time,
    [property: JsonPropertyName("delay")] int? Delay,
    [property: JsonPropertyName("number")] int? Number)
{ public int? Effective => Delay ?? Number; }

public sealed class ProxyDetail
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("now")] public string? Now { get; set; }
    [JsonPropertyName("all")] public List<string>? All { get; set; }
    [JsonPropertyName("hidden")] public bool Hidden { get; set; }
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("provider-name")] public string? ProviderName { get; set; }
    [JsonPropertyName("history")] public List<DelaySample>? History { get; set; }
    [JsonPropertyName("delay")] public int? Delay { get; set; }
}
public sealed record ProxiesResponse([property: JsonPropertyName("proxies")]
    Dictionary<string, ProxyDetail> Proxies);
public sealed class ProviderInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("vehicleType")] public string? VehicleType { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; set; }
    [JsonPropertyName("testUrl")] public string? TestUrl { get; set; }
    [JsonPropertyName("expectedStatus")] public string? ExpectedStatus { get; set; }
    // Task 1 实测：provider 对象上**没有** `history` 键，健康样本在每个成员的 `history`/`extra` 里。
    // 曾按 provider.history 兜底显示「上次」是错的，别加回来。
    [JsonPropertyName("proxies")] public List<ProxyDetail>? Proxies { get; set; }
}
public sealed record ProvidersResponse(
    // Task 1 实测（v1.19.33）：根键是复数 `providers`，值是「键为 provider 名」的对象表，不是数组。
    // 表里还混有每个策略组自动生成的 `vehicleType:"Compatible"` 伪 provider 与 `default`：
    // 真实订阅一律按 `vehicleType` 过滤（只留 File / Http），否则节点页会出现一堆假 provider。
    [property: JsonPropertyName("providers")] Dictionary<string, ProviderInfo> Providers);
public sealed record CoreConfigView([property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("mixed-port")] int? MixedPort);

// MihomoClient.cs
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
namespace ClashAcry.Core;

public sealed class MihomoClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http; private readonly Uri _base;
    public MihomoClient(HttpClient http, string host, int port, string secret)
    {
        _http = http;
        _base = new Uri($"http://{host}:{port}");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    public Task<VersionInfo?> GetVersionAsync(CancellationToken ct) =>
        GetAsync<VersionInfo?>("/version", ct);
    public Task<ProxiesResponse> GetProxiesAsync(CancellationToken ct) =>
        GetAsync<ProxiesResponse>("/proxies", ct);
    public Task<CoreConfigView?> GetConfigsAsync(CancellationToken ct) =>
        GetAsync<CoreConfigView?>("/configs", ct);
    public Task SetProxyAsync(string group, string node, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"/proxies/{Uri.EscapeDataString(group)}", new { name = node }, ct);
    public Task SetModeAsync(string mode, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, "/configs", new { mode }, ct);
    public Task ReloadConfigAsync(string path, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, "/configs", new { path }, ct);
    public Task RefreshProviderAsync(string name, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"/providers/proxies/{Uri.EscapeDataString(name)}", null, ct);
    public Task<ProvidersResponse> GetProvidersAsync(CancellationToken ct) =>
        GetAsync<ProvidersResponse>("/providers/proxies", ct);

    public async Task<int> GetDelayAsync(string name, string url, int timeoutMs, CancellationToken ct)
    {
        var path = $"/proxies/{Uri.EscapeDataString(name)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeoutMs}";
        try { var d = await GetAsync<JsonElement>(path, ct); return d.GetProperty("delay").GetInt32(); }
        // 只把「测不了」（409 / 超时）折成 -1。Task 1 实测 provider 成员名与 provider 名一律 **404**
        // （spec §4.2 已写成既定实现要求）：404 = 调用方用错了通路，必须原样抛出去，不许在这里顺手吞成 -1，
        // 否则 Task 15 误调成员时会安静地显示"超时"，永远看不出是通路用错。
        catch (ApiException e) when (e.StatusCode is 409) { return -1; }
        catch (TaskCanceledException) { return -1; }
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct) =>
        Parse<T>(await Send(HttpMethod.Get, path, null, ct));
    private Task SendAsync(HttpMethod m, string path, object? body, CancellationToken ct) => Send(m, path, body, ct);

    private async Task<string> Send(HttpMethod m, string path, object? body, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(m, new Uri(_base, path));
        if (body is not null) msg.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(msg, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode, text);
        return text;
    }
    // T 为可空类型（VersionInfo? / CoreConfigView?）时空体返回 null；非空类型由调用方自行判 null
    private static T Parse<T>(string text) => JsonSerializer.Deserialize<T>(text, Json) ?? default!;
}

public sealed class ApiException : Exception
{
    public int StatusCode { get; } public string Body { get; }
    public ApiException(int code, string body) : base($"mihomo API {code}: {body}")
        => (StatusCode, Body) = (code, body);
}
```

`GET /configs` 若 Task 1 实测为 404，删除 `GetConfigsAsync` 与其测试，并把 `CoreConfigView` 一起删掉。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter MihomoClientTests`
Expected: PASS，4/4

- [ ] **Step 5: 提交**

```bash
git add src/ClashAcry.Core/MihomoClient.cs src/ClashAcry.Core/MihomoModels.cs tests/ClashAcry.Core.Tests/MihomoClientTests.cs
git commit -m "feat(core): mihomo REST 类型化客户端（状态码原样透出，delay 409 归 -1）"
```

---

### Task 7: SystemProxy —— HKCU 开关与"只清自己的"守卫

**Files:**
- Create: `src/ClashAcry.Core/SystemProxy.cs` `NativeInterop.cs`
- Test: `tests/ClashAcry.Core.Tests/SystemProxyTests.cs`

**Interfaces:**
- Consumes: `Settings`
- Produces:
  - `static class SystemProxy { void Enable(string server, string bypass); void Disable(); string? CurrentServer(); bool IsEnabled(); void CleanupIfStale(int mixedPort, Func<bool> coreAlive); void BroadcastChange(); }`

- [ ] **Step 1: 写失败的测试（真实 HKCU + 备份还原夹具）**

```csharp
[Collection("RegistryGuard")]
public class SystemProxyTests
{
    [Fact]
    public void Enable_WritesProxyEnableAndServer()
    {
        using var _ = new RegistrySnapshot();
        SystemProxy.Enable("127.0.0.1:7897", "localhost;127.*;10.*;192.168.*");

        Assert.True(SystemProxy.IsEnabled());
        Assert.Equal("127.0.0.1:7897", SystemProxy.CurrentServer());
    }

    [Fact]
    public void CleanupIfStale_DoesNotTouchForeignProxy()
    {
        using var _ = new RegistrySnapshot();
        SystemProxy.Enable("127.0.0.1:1234", "");   // 指向别的软件的代理

        SystemProxy.CleanupIfStale(7897, coreAlive: () => false);

        Assert.True(SystemProxy.IsEnabled());
        Assert.Equal("127.0.0.1:1234", SystemProxy.CurrentServer());
    }

    [Fact]
    public void CleanupIfStale_CleansOnlyWhenPointingAtUsAndCoreIsDead()
    {
        using var _ = new RegistrySnapshot();
        SystemProxy.Enable("127.0.0.1:7897", "");

        SystemProxy.CleanupIfStale(7897, coreAlive: () => false);
        Assert.False(SystemProxy.IsEnabled());

        SystemProxy.Enable("127.0.0.1:7897", "");
        SystemProxy.CleanupIfStale(7897, coreAlive: () => true);
        Assert.True(SystemProxy.IsEnabled());
    }
}
```

`RegistrySnapshot` 是夹具：`IDisposable`，构造时把 `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings` 下 `ProxyEnable/ProxyServer/ProxyOverride` 三值读出存字段，`Dispose` 还原并 `BroadcastChange()`。**没有这个夹具就不许跑这批测试**——它会改用户真实系统代理。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter SystemProxyTests`
Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现**

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace ClashAcry.Core;

public static class SystemProxy
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39, INTERNET_OPTION_REFRESH = 37;

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool InternetSetOptionW(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public static void Enable(string server, string bypass)
    {
        using var k = OpenWrite();
        k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        k.SetValue("ProxyServer", server, RegistryValueKind.String);
        if (!string.IsNullOrWhiteSpace(bypass)) k.SetValue("ProxyOverride", bypass, RegistryValueKind.String);
        BroadcastChange();
    }

    public static void Disable()
    {
        using var k = OpenWrite();
        k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        k.SetValue("ProxyServer", "", RegistryValueKind.String);
        BroadcastChange();
    }

    public static bool IsEnabled() => OpenRead()?.GetValue("ProxyEnable") as int? == 1;
    public static string? CurrentServer() => OpenRead()?.GetValue("ProxyServer") as string;

    /// 三层清理中的启动校验层（spec §9）：仅当代理指向本应用端口且内核已死才清；
    /// 指向其他软件的代理绝不触碰。
    public static void CleanupIfStale(int mixedPort, Func<bool> coreAlive)
    {
        var server = CurrentServer();
        if (!IsEnabled() || server is null) return;
        var ours = $"127.0.0.1:{mixedPort}";
        var normalized = server.Split(';')[0].Trim();
        if (!normalized.Equals(ours, StringComparison.OrdinalIgnoreCase)) return;
        if (coreAlive()) return;
        Disable();
    }

    public static void BroadcastChange()
    {
        InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    private static RegistryKey OpenWrite() =>
        Registry.CurrentUser.CreateSubKey(Key, RegistryKeyPermissionCheck.ReadWriteSubTree)
        ?? throw new InvalidOperationException("无法打开 Internet Settings 键");
    private static RegistryKey? OpenRead() => Registry.CurrentUser.OpenSubKey(Key);
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter SystemProxyTests`
Expected: PASS，3/3，且跑完后用户真实代理设置与跑前一致（`RegistrySnapshot` 还原生效）。

- [ ] **Step 5: 提交**

```bash
git add src/ClashAcry.Core/SystemProxy.cs tests/ClashAcry.Core.Tests/SystemProxyTests.cs
git commit -m "feat(core): 系统代理 HKCU 开关与悬挂清理（只清指向自己的）"
```

---

### Task 8: CoreManager —— 状态机、stderr 环、双探活、一次重试

**Files:**
- Create: `src/ClashAcry.Core/CoreManager.cs` `StderrRing.cs` `CoreProbe.cs`
- Test: `tests/ClashAcry.Core.Tests/CoreManagerTests.cs` `StderrRingTests.cs`

**Interfaces:**
- Consumes: `AppPaths`、`Settings`、`MihomoClient`、`SystemProxy.CleanupIfStale`
- Produces:
  - `sealed class CoreManager(AppPaths paths, SettingsStore store, MihomoClient client, CoreProbe probe, Func<string, ProcessStartInfo>? launchFactory = null)`
  - `CoreState State { get; }` / `int MixedPort { get; }` / `int? RunningProcessId { get; }` / `int StartAttemptCount { get; }` / `int SpawnCount { get; }` / `int ConcurrentGuardHits { get; }`
  - `event Action<CoreStateChanged>? StateChanged`；`IReadOnlyList<CoreStateChanged> StateChangedEvents { get; }`（`CoreStateChanged` 记录类型由 Task 3 定义在 Contracts，本任务直接 import，不再声明）
  - `Task<bool> StartAsync(CancellationToken ct)` / `Task StopAsync(CancellationToken ct)` / `Task ReloadAsync(CancellationToken ct)` / `Task<SwapOutcome> SwapExecutableAsync(Func<Task> replaceFiles, CancellationToken ct)`
  - `record struct SwapOutcome(bool CoreRunningAfter, bool RolledBack)`
  - `IReadOnlyList<string> StderrTail(int n = 50)`
  - `sealed class StderrRing(int capacity) { void Add(string line); void Clear(); IReadOnlyList<string> Snapshot(); }`

  命名说明（Task 1 实测后的定名裁定）：`StderrRing` / `StderrTail` / 线格式键 `stderr_tail` 是**历史名，实为"内核输出环"**——真内核把日志写在 stdout。名字不改（改名要连带动 Task 3 契约、Task 8、Task 14 显示与 spec §4.1，收益只有好看），但**语义必须在文档与注释里写清"两流进同一个环"**，否则下一个人会以为只看 stderr。
  - `sealed class CoreProbe(MihomoClient client) { Task<bool> ControllerAlive(CancellationToken ct); static Task<bool> TcpAlive(string host, int port, CancellationToken ct); }`

- [ ] **Step 1: 写失败的测试**

```csharp
public class StderrRingTests
{
    [Fact]
    public void Ring_KeepsOnlyLastCapacityLines_AndPreservesOrder()
    {
        var ring = new StderrRing(3);
        foreach (var l in new[] { "a", "b", "c", "d", "e" }) ring.Add(l);
        Assert.Equal(new[] { "c", "d", "e" }, ring.Snapshot());
    }
}

public class CoreManagerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ca-core-").FullName;
    private readonly WireMockServer _wire = WireMockServer.Start();
    private static readonly JsonSerializerOptions No = new();

    public CoreManagerTests()
    {
        _wire.Reset();
        _wire.Given(Request.Create().WithPath("/version").UsingGet()).RespondWith(
            Response.Create().WithBody(JsonSerializer.Serialize(new { meta = false, version = "v1.0.0" })));
    }
    public void Dispose() { _wire.Dispose(); TryDelete(_dir); }

    // 假内核：把 .cmd 脚本当内核跑（CoreManager 的 launchFactory 注入点）
    private static ProcessStartInfo CmdLaunch(string exe) => new("cmd.exe", $"/c \"{exe}\"")
    { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
    // Task 1 实测：真内核（mihomo v1.19.33）的 Go 日志全部走 **stdout**，stderr 恒空。
    // 假内核照这一点写：日志行 echo 到 stdout。若这里还写 `1>&2`，测试只能证明 stderr 支路通了，
    // 证明不了真实内核那条主路会被采进环（环的 stdout 订阅漏掉时测试仍然全绿）。
    private static string SleepScript() => "@echo off\r\necho start\r\ntimeout /t 60 /nobreak >nul\r\n";
    private static string KernelLogScript(string text) => $"@echo off\r\necho {text}\r\ntimeout /t 60 /nobreak >nul\r\n";
    private static string ExitScript() => "@echo off\r\necho boom\r\nexit 3\r\n";

    private int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }
    private static int Port(TcpListener l) => ((IPEndPoint)l.LocalEndpoint).Port;

    private CoreManager Build(string script, int mixedPort, int controllerPort)
    {
        var paths = AppPaths.ForTest(_dir); paths.EnsureCreated();
        File.WriteAllText(paths.CoreExecutable, script);
        var store = new SettingsStore(paths);
        store.Save(store.Load() with { ControllerPort = controllerPort, MixedPort = mixedPort });
        var client = new MihomoClient(new HttpClient { Timeout = TimeSpan.FromSeconds(2) },
            "127.0.0.1", controllerPort, store.Load().Secret);
        return new CoreManager(paths, store, client, new CoreProbe(client), CmdLaunch);
    }

    [Fact]
    public async Task Start_TransitionsToRunning_WhenVersionAndMixedPortAlive()
    {
        using var _ = new RegistrySnapshot();   // Stop 会清系统代理，测试必须还原真实 HKCU
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);

        Assert.True(await mgr.StartAsync(default));
        Assert.Equal(CoreState.Running, mgr.State);
        Assert.Contains(mgr.StateChangedEvents, e => e.State == CoreState.Running);
        await mgr.StopAsync(default);
    }

    [Fact]
    public async Task Start_DoesNotReachRunning_WhenControllerAliveButMixedPortDead()
    {
        // spec §9 状态失真：mixed 端口没人听 → 绝不进 Running
        var mgr = Build(SleepScript(), FreePort(), _wire.Port);

        Assert.False(await mgr.StartAsync(default));
        Assert.Equal(CoreState.Crashed, mgr.State);
        Assert.Contains("mixed", mgr.StateChangedEvents.Last().Reason);
    }

    [Fact]
    public async Task Start_FailsWhenVersionNeverResponds_AndKeepsStderrReason()
    {
        _wire.Reset();                       // /version 不再返回 200
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(KernelLogScript("config file error: bad dns section"), Port(mixed), _wire.Port);

        Assert.False(await mgr.StartAsync(default));
        Assert.Equal(CoreState.Crashed, mgr.State);
        Assert.Contains("探活失败", mgr.StateChangedEvents.Last().Reason);
        Assert.Contains(mgr.StateChangedEvents.Last().StderrTail, l => l.Contains("config file error"));
    }

    [Fact]
    public async Task Start_ExitsImmediately_ReportsExitCode_AndRetriesExactlyOnce()
    {
        var mgr = Build(ExitScript(), FreePort(), _wire.Port);

        Assert.False(await mgr.StartAsync(default));
        Assert.Equal(2, mgr.StartAttemptCount);           // 首发 + 一次重试，不多
        Assert.Equal(CoreState.Crashed, mgr.State);
        Assert.Contains("退出码 3", mgr.StateChangedEvents.Last().Reason);
    }

    [Fact]
    public async Task Stop_KillsCoreProcessAndDisablesSystemProxy()
    {
        using var _ = new RegistrySnapshot();
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);
        await mgr.StartAsync(default);
        var pid = mgr.RunningProcessId!.Value;
        SystemProxy.Enable($"127.0.0.1:{mgr.MixedPort}", "");

        await mgr.StopAsync(default);

        Assert.Equal(CoreState.Stopped, mgr.State);
        Assert.False(SystemProxy.IsEnabled());
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && Process.GetProcesses().Any(p => p.Id == pid)) await Task.Delay(100);
        Assert.DoesNotContain(Process.GetProcesses(), p => p.Id == pid);
        Assert.Null(mgr.RunningProcessId);   // Stop 后必须清空，避免留下陈旧 PID
    }

    [Fact]
    public void EveryTransition_EmitsEvent_AndNoStateIsSilent()
    {
        using var _ = new RegistrySnapshot();   // Stop 会清系统代理，测试必须还原真实 HKCU
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);
        var seen = new List<CoreState>();
        mgr.StateChanged += e => seen.Add(e.State);

        mgr.StartAsync(default).GetAwaiter().GetResult();
        mgr.StopAsync(default).GetAwaiter().GetResult();

        Assert.Equal(new[] { CoreState.Starting, CoreState.Running, CoreState.Stopped }, seen);
    }

    [Fact]
    public async Task ConcurrentStart_GuardHitsSecondRequest_AndSpawnHappensOnce()
    {
        using var _ = new RegistrySnapshot();   // Stop 会清系统代理，测试必须还原真实 HKCU
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mgr = Build(SleepScript(), Port(mixed), _wire.Port);
        var a = mgr.StartAsync(default); var b = mgr.StartAsync(default);

        Assert.True(await a); Assert.True(await b);
        Assert.Equal(1, mgr.SpawnCount);          // spec §3 禁令 1 的进程侧保证
        Assert.True(mgr.ConcurrentGuardHits >= 1);
        await mgr.StopAsync(default);
    }

    private static void TryDelete(string dir)
    { try { Directory.Delete(dir, true); } catch { } }
}
```

夹具约束（不是可选）：假内核走 `cmd.exe /c <脚本>`，因此 **`CoreManager` 必须接受可注入的 `launchFactory`**（`Func<string, ProcessStartInfo>`，默认实现用 mihomo 的 `-d <runtime>` 形态）；`CoreProbe` 同样必须是注入的构造参数，否则状态机无法在测试里驱动。`WireMock` 只 mock controller，`mixed` 端口由测试自己 `TcpListener` 占住或留空（`FreePort()`）来表达"内核活着但 mixed 不通"这一状态失真场景。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter CoreManagerTests`
Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现**

```csharp
// CoreProbe.cs
using System.Net.Sockets;
namespace ClashAcry.Core;

public sealed class CoreProbe(MihomoClient client)
{
    public async Task<bool> ControllerAlive(CancellationToken ct)
    { try { await client.GetVersionAsync(ct); return true; } catch { return false; } }

    public static async Task<bool> TcpAlive(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1000);
            await tcp.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch { return false; }
    }
}
```

```csharp
// CoreManager.cs
using System.Diagnostics;
using ClashAcry.Contracts;
namespace ClashAcry.Core;

public sealed class CoreManager : IAsyncDisposable
{
    private readonly AppPaths _paths; private readonly SettingsStore _store;
    private readonly MihomoClient _client; private readonly CoreProbe _probe;
    private readonly Func<string, ProcessStartInfo> _launch;
    private readonly StderrRing _stderr = new(200);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<CoreStateChanged> _events = new();
    private Process? _proc; private string? _lastReason;

    public CoreState State { get; private set; } = CoreState.Stopped;
    public int MixedPort { get; private set; }
    public int? RunningProcessId => _proc is { HasExited: false } ? _proc.Id : null;
    public event Action<CoreStateChanged>? StateChanged;
    public IReadOnlyList<CoreStateChanged> StateChangedEvents => _events;
    public int StartAttemptCount { get; private set; }
    public int SpawnCount { get; private set; }
    public int ConcurrentGuardHits { get; private set; }

    public CoreManager(AppPaths paths, SettingsStore store, MihomoClient client, CoreProbe probe,
                       Func<string, ProcessStartInfo>? launchFactory = null)
    {
        (_paths, _store, _client, _probe) = (paths, store, client, probe);
        _launch = launchFactory ?? (exe => new ProcessStartInfo(exe, $"-d \"{paths.Root}\"")
        { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
        MixedPort = store.Load().MixedPort;
    }

    public IReadOnlyList<string> StderrTail(int n = 50) => _stderr.Snapshot().TakeLast(n).ToList();

    // 锁纪律：只有公开入口取 _lifecycle，内部 *Locked 方法一律不取（信号量不可重入，Task 11 的换核流程也走这条）
    private async Task<T> InLifecycle<T>(Func<Task<T>> body, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct);
        try { return await body(); } finally { _lifecycle.Release(); }
    }

    public Task<bool> StartAsync(CancellationToken ct) => InLifecycle(() => StartLockedAsync(ct), ct);
    public Task StopAsync(CancellationToken ct) => InLifecycle(async () => { await StopLockedAsync(); }, ct);

    /// Task 11 用：在生命周期锁内换文件，内核由本方法负责停/起
    public Task<SwapOutcome> SwapExecutableAsync(Func<Task> replaceFiles, CancellationToken ct) =>
        InLifecycle(async () =>
        {
            var wasRunning = State is CoreState.Running or CoreState.Starting;
            Transition(CoreState.Updating);
            await StopLockedAsync();
            try { await replaceFiles(); }
            catch (IOException) { var back = await StartLockedAsync(); return SwapOutcome.RolledBack(back); }
            var started = wasRunning ? await StartLockedAsync() : true;
            return SwapOutcome.Done(started);
        }, ct);

    private async Task<bool> StartLockedAsync(CancellationToken ct)
    {
        if (State is CoreState.Running or CoreState.Starting) { ConcurrentGuardHits++; return State == CoreState.Running; }

        StartAttemptCount = 0;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            StartAttemptCount = attempt;
            if (await AttemptStart(ct)) return true;
            if (attempt == 1) _lastReason = $"首次启动失败（{_lastReason}），自动重试第 2 次";
        }
        Transition(CoreState.Crashed, _lastReason);
        return false;
    }

    private async Task<bool> AttemptStart(CancellationToken ct)
    {
        Transition(CoreState.Starting, StartAttemptCount > 1 ? _lastReason : null);
        var s = _store.Load();
        MixedPort = s.MixedPort;
        _stderr.Clear(); _lastReason = null;

        var exe = s.CorePath ?? _paths.CoreExecutable;
        if (!File.Exists(exe)) { _lastReason = $"内核文件不存在：{exe}"; return false; }
        // controller 端口已被占 → 不再 spawn（否则两个内核抢一个口，状态失真）
        if (await CoreProbe.TcpAlive(s.ControllerHost, s.ControllerPort, ct))
        { _lastReason = $"controller 端口 {s.ControllerPort} 已被其他进程占用"; return false; }

        try
        {
            SpawnCount++;
            _proc = Process.Start(_launch(exe))!;
            // Task 1 实测：mihomo v1.19.33 的 Go 日志全部走 **stdout**，stderr 恒空。
            // 两条流进同一个环（字段名 `_stderr` 是历史名，实际是"内核输出环"），顺序由到达时间决定；
            // 只订阅 ErrorDataReceived 的话日志面板永远是一片空白。每条流各订阅一次，重复订阅会让每行进环两次。
            _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) _stderr.Add(e.Data); };
            _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) _stderr.Add(e.Data); };
            _proc.BeginOutputReadLine(); _proc.BeginErrorReadLine();

            var controllerOk = false; var mixedOk = false;
            for (var i = 0; i < 20; i++)     // 20 x 250ms = 5s
            {
                await Task.Delay(250, ct);
                if (_proc.HasExited)
                { _lastReason = $"内核进程退出，退出码 {_proc.ExitCode}"; await KillProcess(); return false; }

                // spec §9 状态失真：controller 与 mixed 端口双探活都通过才允许 Running
                controllerOk = await _probe.ControllerAlive(ct);
                mixedOk = controllerOk && await CoreProbe.TcpAlive("127.0.0.1", MixedPort, ct);
                if (mixedOk) { Transition(CoreState.Running); return true; }
            }
            _lastReason = $"探活失败：controller={(controllerOk ? "ok" : "fail")} mixed({MixedPort})={(mixedOk ? "ok" : "fail")}，5s 内未就绪";
            await KillProcess();
            return false;
        }
        catch (Exception ex) { _lastReason = ex.Message; await KillProcess(); return false; }
    }

    private async Task StopLockedAsync()
    {
        await KillProcess();
        SystemProxy.Disable();     // spec §9：停止内核顺带清系统代理（无条件，幂等）
        Transition(CoreState.Stopped);
    }

    public Task ReloadAsync(CancellationToken ct) => _client.ReloadConfigAsync(_paths.ConfigFile, ct);

    private async Task KillProcess()
    {
        if (_proc is null) return;
        try { if (!_proc.HasExited) { _proc.Kill(entireProcessTree: true); await _proc.WaitForExitAsync(); } }
        catch { /* 已被外部杀死 */ }
        finally { _proc?.Dispose(); _proc = null; }   // 置 null → RunningProcessId 随之为 null
    }

    private void Transition(CoreState next, string? reason = null)
    {
        State = next;
        var e = new CoreStateChanged(next, reason, StderrTail());
        _events.Add(e);
        StateChanged?.Invoke(e);   // spec §3 禁令 3：所有迁移点唯一出口，且必须广播
    }

    public async ValueTask DisposeAsync() { await StopLockedAsync(); _lifecycle.Dispose(); }
}

public readonly record struct SwapOutcome(bool CoreRunningAfter, bool RolledBack)
{
    public static SwapOutcome Done(bool running) => new(running, false);
    public static SwapOutcome RolledBack(bool running) => new(running, true);
}
```

`StderrRing` 按 Task 8 Step 1 的测试实现为固定容量环形缓冲，必须提供 `Clear()`。锁纪律是本任务的核心：`StartAsync`/`StopAsync`/`SwapExecutableAsync` 是唯一取 `_lifecycle` 的三个入口，`CoreUpdater`（Task 11）只允许通过 `SwapExecutableAsync` 换文件，**不得自己碰内核进程**。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter "CoreManagerTests|StderrRingTests"`
Expected: PASS，8/8（`CoreManagerTests` 7 条 + `StderrRingTests` 1 条）。两条测试就是 spec 禁令的可执行化：`EveryTransition_EmitsEvent_AndNoStateIsSilent` —— 实现里出现任何绕过 `Transition` 的 `State =` 赋值它就红（禁令 3）；`ConcurrentStart_GuardHitsSecondRequest_AndSpawnHappensOnce` —— `SpawnCount` 必须为 1（禁令 1 的进程侧保证）。`Start_DoesNotReachRunning_WhenControllerAliveButMixedPortDead` 锁住 spec §9 的状态失真判据。

- [ ] **Step 5: 提交**

```bash
git add src/ClashAcry.Core tests/ClashAcry.Core.Tests
git commit -m "feat(core): 内核状态机（双探活判 Running、启动一次重试、stderr 环）"
```

---

### Task 9: 管道服务端与客户端

**Files:**
- Create: `src/ClashAcry.Core/PipeServer.cs` `PipeClient.cs`
- Test: `tests/ClashAcry.Core.Tests/PipeRoundTripTests.cs`

**Interfaces:**
- Consumes: `Frame`/`FrameCodec`/`CmdTypes`/`EventTypes`（Task 3）、`CoreManager`（Task 8）、`SettingsStore`（Task 4）
- Produces:
  - `sealed class PipeServer { public PipeServer(Func<Frame,Task<Frame?>> onCommand); Task StartAsync(CancellationToken ct); void Broadcast(string type, object? payload); int ClientCount; }`
  - `sealed class PipeClient { Task<bool> WaitUpAsync(TimeSpan timeout); Task<Frame?> RequestAsync(string type, object? payload, CancellationToken ct); event Action<Frame>? Event; Task<bool> IsServerUp(); }`

- [ ] **Step 1: 写失败的测试（同进程起服务端，真命名管道往返）**

```csharp
public class PipeRoundTripTests
{
    [Fact]
    public async Task Request_AwaitsReply_WithSameReplyToId()
    {
        var server = new PipeServer(async f =>
        {
            if (f.Type == CmdTypes.Handshake)
                return new Frame("reply", f.Type, JsonUtil.Element(new HandshakeReply(
                    CoreState.Running, "127.0.0.1", 9097, "sec", 7897, "v1", "http://www.gstatic.com/generate_204")), 1, f.Id, f.Id, null);
            return await Task.FromResult<Frame?>(null);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StartAsync(cts.Token);
        await Task.Delay(150);

        var client = new PipeClient();
        Assert.True(await client.WaitUpAsync(TimeSpan.FromSeconds(5)));

        var reply = await client.RequestAsync(CmdTypes.Handshake, null, cts.Token);

        var hs = JsonUtil.Read<HandshakeReply>(reply!.Payload);
        Assert.Equal(CoreState.Running, hs.State);
        Assert.Equal("sec", hs.Secret);
        Assert.Equal(reply.ReplyTo, reply.Id);
    }

    [Fact]
    public async Task Broadcast_ReachesEveryConnectedClient()
    {
        var server = new PipeServer(_ => Task.FromResult<Frame?>(null));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StartAsync(cts.Token);
        await Task.Delay(150);

        var a = new PipeClient(); var b = new PipeClient();
        var gotA = new TaskCompletionSource<Frame>(); var gotB = new TaskCompletionSource<Frame>();
        a.Event += gotA.SetResult; b.Event += gotB.SetResult;
        Assert.True(await a.WaitUpAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await b.WaitUpAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(200);

        server.Broadcast(EventTypes.CoreStateChanged, new { state = CoreState.Running });

        var fromA = await gotA.Task; var fromB = await gotB.Task;
        Assert.Equal(EventTypes.CoreStateChanged, fromA.Type);
        Assert.Equal(EventTypes.CoreStateChanged, fromB.Type);
        Assert.Equal(fromA.Seq, fromB.Seq);   // 同一次广播，两个客户端各得一份
    }

    [Fact]
    public async Task Client_WaitsUp_ReturnsFalseWhenServerAbsent()
    {
        Assert.False(await new PipeClient().WaitUpAsync(TimeSpan.FromMilliseconds(300)));
    }
}
```

（测试间冲突处理：命名管道名固定，测试类必须串行执行——给测试项目加 `[assembly: CollectionBehavior(DisableTestParallelization = true)]`。）

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter PipeRoundTripTests`
Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现**

```csharp
// PipeServer.cs
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ClashAcry.Contracts;
namespace ClashAcry.Core;

public sealed class PipeServer : IAsyncDisposable
{
    private readonly Func<Frame, Task<Frame?>> _onCommand;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ConnectedClient> _clients = new();
    private Task? _accept;

    public PipeServer(Func<Frame, Task<Frame?>> onCommand) => _onCommand = onCommand;
    public int ClientCount { get { lock (_clients) return _clients.Count; } }

    public Task StartAsync(CancellationToken ct) => _accept ??= AcceptLoop(ct);

    public void Broadcast(string type, object? payload)
    {
        var frame = new Frame("event", type, payload is null ? null : JsonUtil.Element(payload),
            Interlocked.Increment(ref _seq), Guid.NewGuid().ToString("N"), null, null);
        lock (_clients) foreach (var c in _clients.ToArray()) c.TrySend(frame);
    }

    private long _seq;

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            var pipe = CreateServerPipe();
            try { await pipe.WaitForConnectionAsync(ct); }
            catch (OperationCanceledException) { pipe.Dispose(); return; }

            var client = new ConnectedClient(pipe);
            lock (_clients) _clients.Add(client);
            _ = Pump(client, ct);
        }
    }

    private static NamedPipeServerStream CreateServerPipe()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.ReadWrite, AccessControlType.Allow));   // 只允许当前用户（spec §4.1）
        var pipe = new NamedPipeServerStream(ContractConstants.PipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        pipe.SetAccessControl(sec);
        return pipe;
    }

    private async Task Pump(ConnectedClient client, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await FrameCodec.ReadLineAsync(client.Reader, ct);
                if (frame is null) break;
                Frame? reply;
                try { reply = await _onCommand(frame); }
                catch (Exception ex) { reply = frame with { Kind = "reply", Error = ex.Message, ReplyTo = frame.Id, Payload = null }; }
                if (reply is not null) client.TrySend(reply with { Kind = "reply", ReplyTo = frame.Id });
            }
        }
        finally { lock (_clients) _clients.Remove(client); client.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        lock (_clients) foreach (var c in _clients.ToArray()) c.Dispose();
        if (_accept is not null) { try { await _accept; } catch { } }
    }
}

internal sealed class ConnectedClient(NamedPipeServerStream pipe) : IDisposable
{
    private readonly StreamWriter _w = new(pipe, new UTF8Encoding(false)) { AutoFlush = true };
    public StreamReader Reader { get; } = new(pipe, new UTF8Encoding(false));
    private readonly SemaphoreSlim _gate = new(1, 1);
    public void TrySend(Frame f) { _gate.Wait(); try { _w.Write(FrameCodec.Encode(f)); } finally { _gate.Release(); } }
    public void Dispose() { try { _w.Dispose(); Reader.Dispose(); pipe.Dispose(); } catch { } }
}
```

```csharp
// PipeClient.cs
using System.IO.Pipes;
using System.Text;
using ClashAcry.Contracts;
namespace ClashAcry.Core;

public sealed class PipeClient : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe; private StreamWriter? _w; private CancellationTokenSource? _cts;
    private readonly Dictionary<string, TaskCompletionSource<Frame>> _pending = new();
    private readonly object _gate = new();
    public event Action<Frame>? Event;

    public async Task<bool> WaitUpAsync(TimeSpan timeout)
    {
        try
        {
            _pipe = new NamedPipeClientStream(".", ContractConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync((int)timeout.TotalMilliseconds);
            _w = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoop(_cts.Token));
            return true;
        }
        catch { return false; }
    }

    public Task<bool> IsServerUp() => Task.FromResult(_pipe?.IsConnected == true);

    public async Task<Frame?> RequestAsync(string type, object? payload, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _pending[id] = tcs;
        var frame = new Frame("command", type, payload is null ? null : JsonUtil.Element(payload), 0, id, null, null);
        await _w!.WriteAsync(FrameCodec.Encode(frame), ct);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try { return await tcs.Task; } finally { lock (_gate) _pending.Remove(id); }
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        var reader = new StreamReader(_pipe!, new UTF8Encoding(false));
        while (!ct.IsCancellationRequested)
        {
            Frame? f;
            try { f = await FrameCodec.ReadLineAsync(reader, ct); } catch { return; }
            if (f is null) return;
            if (f.Kind == "reply" && f.ReplyTo is not null)
            {
                TaskCompletionSource<Frame>? tcs;
                lock (_gate) { _pending.TryGetValue(f.ReplyTo, out tcs); if (tcs is not null) _pending.Remove(f.ReplyTo); }
                tcs?.TrySetResult(f);
            }
            else Event?.Invoke(f);
        }
    }
    public async ValueTask DisposeAsync() { _cts?.Cancel(); _w?.Dispose(); _pipe?.Dispose(); await Task.CompletedTask; }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter PipeRoundTripTests`
Expected: PASS，3/3

- [ ] **Step 5: 提交**

```bash
git add src/ClashAcry.Core tests/ClashAcry.Core.Tests
git commit -m "feat(core): 命名管道服务端/客户端（当前用户 ACL、请求-回复与广播）"
```

---

### Task 10: Tray 宿主 —— Win32 托盘、装配、退出钩子

**Files:**
- Create: `src/ClashAcry.Tray/NativeMethods.cs` `Win32NotifyIcon.cs` `TrayCommandRouting.cs` `TrayHost.cs` `Program.cs`（替换 Task 2 占位）`app.ico`
- Modify: `src/ClashAcry.Tray/ClashAcry.Tray.csproj`（嵌入 `app.ico`）
- Modify: `src/ClashAcry.Contracts/Commands.cs`（追加 `ModeRequest`/`SysProxyRequest`/`SysProxyState`/`SettingsPatch`，与本任务路由共用同一形状）
- Test: `tests/ClashAcry.Core.Tests/TrayCommandRoutingTests.cs`

**Interfaces:**
- Consumes: `PipeServer`、`CoreManager`、`SettingsStore`、`SystemProxy`、`SubscriptionImport`、`CoreUpdater`（Task 11 前用桩）
- Produces:
  - `sealed class TrayHost { Task RunAsync(CancellationToken ct); CoreManager Core; PipeServer Pipe; TrayCommandRouting Routing; }`
  - `sealed class TrayCommandRouting { Task<Frame?> Handle(Frame f); }` —— 命令路由**独立于托盘/窗口**，可单测；构造签名见 Step 3
  - `sealed class Win32NotifyIcon(string icoPath, string tooltip) : IDisposable` —— `void MenuItem(int id, string text, Action onPick)` / `void SetTip(string tip)`；Step 4 的实现就是这份接口，测试与 `TrayHost` 都按它调用

- [ ] **Step 1: 写路由的失败测试（不依赖托盘）**

```csharp
public class TrayCommandRoutingTests
{
    [Fact]
    public async Task Handshake_Replys_WithCoreStateAndSecret()
    {
        var h = new TrayHarness();   // 假内核 + WireMock controller
        var reply = await h.Routing.Handle(new Frame("command", CmdTypes.Handshake, null, 0, "c1", null, null));

        var hs = JsonUtil.Read<HandshakeReply>(reply!.Payload);
        Assert.Equal(h.Core.State, hs.State);
        Assert.Equal(h.Store.Load().Secret, hs.Secret);
        Assert.Equal("reply", reply.Kind);
    }

    [Fact]
    public async Task SysProxySet_True_CallsEnableWithMixedPortAddress()
    {
        using var _ = new RegistrySnapshot();
        var h = new TrayHarness();
        await h.Routing.Handle(new Frame("command", CmdTypes.SysProxySet,
            JsonUtil.Element(new { enabled = true }), 0, "c1", null, null));

        Assert.True(SystemProxy.IsEnabled());
        Assert.Equal($"127.0.0.1:{h.Store.Load().MixedPort}", SystemProxy.CurrentServer());
    }

    [Fact]
    public async Task ModeSet_ForwardsToClient_AndRepliesOk()
    {
        var h = new TrayHarness();
        var reply = await h.Routing.Handle(new Frame("command", CmdTypes.ModeSet,
            JsonUtil.Element(new { mode = "global" }), 0, "c1", null, null));

        Assert.Null(reply!.Error);
        Assert.Equal("global", h.Wire.LogEntries.Last().RequestMessage.BodyMap!["mode"]);
    }

    [Fact]
    public async Task UnknownCommand_RepliesWithError_NotThrow()
    {
        var h = new TrayHarness();
        var reply = await h.Routing.Handle(new Frame("command", "nope", null, 0, "c1", null, null));
        Assert.Equal("unknown command: nope", reply!.Error);
    }

    [Fact]
    public async Task CoreStart_IsSerialized_TwoConcurrentRequestsOneAttempt()
    {
        var h = new TrayHarness();
        var t1 = h.Routing.Handle(Cmd("core.start", "a"));
        var t2 = h.Routing.Handle(Cmd("core.start", "b"));
        await Task.WhenAll(t1, t2);

        Assert.Equal(1, h.Core.ConcurrentGuardHits);   // 第二个请求走守卫，不再重复 spawn（spec §3 禁令 1）
    }

    [Theory]
    [InlineData(CmdTypes.Handshake)]
    [InlineData(CmdTypes.CoreStart)] [InlineData(CmdTypes.CoreStop)]
    [InlineData(CmdTypes.CoreReload)] [InlineData(CmdTypes.ModeSet)]
    [InlineData(CmdTypes.SysProxySet)] [InlineData(CmdTypes.SettingsUpdate)]
    [InlineData(CmdTypes.UiFocus)] [InlineData(CmdTypes.CoreUpdate)] [InlineData(CmdTypes.SubImport)]
    public async Task EveryDeclaredCommand_HasAHandler(string cmd)
    {
        var h = new TrayHarness();
        var reply = await h.Routing.Handle(new Frame("command", cmd, h.PayloadFor(cmd), 0, "c1", null, null));

        Assert.NotNull(reply);                       // 不许有命令静默落到 unknown
        Assert.NotEqual("unknown command: " + cmd, reply!.Error);
    }

    [Fact]
    public async Task SubImport_MissingUrl_RepliesError_NotSilentSuccess()
    {
        var h = new TrayHarness();
        var reply = await h.Routing.Handle(new Frame("command", CmdTypes.SubImport, null, 0, "c2", null, null));

        Assert.Contains("url", reply!.Error);         // 缺参数必须报错，不能假装导入成功
    }

    [Fact]
    public async Task SubImport_WritesRuntimeConfig_AndRepliesImportReply()
    {
        var h = new TrayHarness();
        var reply = await h.Routing.Handle(new Frame("command", CmdTypes.SubImport,
            JsonUtil.Element(new ImportRequest(h.WireUrl + "/sub")), 0, "c1", null, null));

        var ir = JsonUtil.Read<ImportReply>(reply!.Payload);
        Assert.Null(ir!.Reason);                                            // Reason 为空即成功
        Assert.Equal(h.Paths.ConfigFile, ir.ConfigFile);                    // 只写 runtime\config.yaml
        Assert.True(File.Exists(h.Paths.ConfigFile));
    }
}
```

`TrayCommandRouting` 的 `sub.import` 分支：读 `ImportRequest` → `new SubscriptionImport(paths, store).ImportFromUrl(url, _http, ct)` → 回 `ImportReply(result.ConfigFile, result.ReasonIfFailed)`（`ReasonIfFailed` 为空串时传 `null`，见 Step 3 的 `ImportAsync`）；异常一律转成 `ImportReply(null, ex.Message)` 而**不是** `Frame.Error`（UI 需要能区分"业务失败可展示"与"管道故障"，前者走行内文案，后者走重连逻辑）。

`TrayHarness` 是测试夹具（放在 `tests/ClashAcry.Core.Tests/TrayHarness.cs`）：临时 `AppPaths` + 真 `SettingsStore` + `WireMock` controller + `CoreManager(…, CmdLaunch)` 假内核 + `TrayCommandRouting`，除上面用到的 `Routing/Core/Store/Wire` 外还暴露 `Paths`（那份临时 `AppPaths`）与 `WireUrl`（WireMock 基址），供订阅导入断言落盘位置；并提供 `JsonElement? PayloadFor(string cmd)` 按命令给出合法 payload（`mode.set` → `{"mode":"global"}`；`sysproxy.set` → `{"enabled":true}`；`settings.update` → `{"mixed_port":7899}`；`sub.import` → `JsonUtil.Element(new ImportRequest(WireUrl + "/sub"))`；其余 → `null`）。`unknown` 之外的每条命令都必须真的执行动作，`EveryDeclaredCommand` 不允许用「返回 null reply」糊过去。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter TrayCommandRoutingTests`
Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现 TrayCommandRouting（命令表）**

```csharp
// TrayCommandRouting.cs
using System.Text.Json;
using ClashAcry.Contracts;
using ClashAcry.Core;
namespace ClashAcry.Tray;

/// 命令 → 动作的唯一映射点。不依赖托盘窗口句柄、不依赖 XAML，因此全部命令可在无交互环境单测。
/// 约定：业务失败（导入源站挂了、zip 解不出）走 ImportReply.Reason / UpdateResult 回包；
/// 请求本身不合法（未知命令、缺参数）走 Frame.Error。UI 靠这个区分「可展示的结果」和「管道故障」。
public sealed class TrayCommandRouting
{
    private readonly CoreManager _core; private readonly SettingsStore _store;
    private readonly MihomoClient _client; private readonly AppPaths _paths;
    private readonly HttpClient _http; private readonly Func<CoreUpdater> _updater;
    private readonly PipeServer _pipe; private readonly Action _focusUi;

    public TrayCommandRouting(CoreManager core, SettingsStore store, MihomoClient client,
        AppPaths paths, HttpClient http, Func<CoreUpdater> updater, PipeServer pipe, Action focusUi)
    {
        _core = core; _store = store; _client = client; _paths = paths;
        _http = http; _updater = updater; _pipe = pipe; _focusUi = focusUi;
    }

    public async Task<Frame?> Handle(Frame f)
    {
        try
        {
            object? result = f.Type switch
            {
                CmdTypes.Handshake => Handshake(),
                CmdTypes.CoreStart => await _core.StartAsync(default),
                CmdTypes.CoreStop => await StopAndCleanProxyAsync(),
                CmdTypes.CoreReload => await ReloadOrStartAsync(),
                CmdTypes.ModeSet => await SetModeAsync(Read<ModeRequest>(f).Mode),
                CmdTypes.SysProxySet => ApplySysProxy(Read<SysProxyRequest>(f).Enabled),
                CmdTypes.SettingsUpdate => ApplySettings(Read<SettingsPatch>(f)),
                CmdTypes.UiFocus => FocusUi(),
                CmdTypes.CoreUpdate => await UpdateCoreAsync(),
                CmdTypes.SubImport => await ImportAsync(f),
                _ => throw new InvalidOperationException($"unknown command: {f.Type}")
            };
            return f with { Kind = "reply", ReplyTo = f.Id, Error = null, Payload = JsonUtil.Element(result!) };
        }
        catch (Exception ex)
        {
            return f with { Kind = "reply", ReplyTo = f.Id, Error = ex.Message, Payload = null };
        }
    }

    private static T Read<T>(Frame f) =>
        f.Payload is null ? throw new InvalidOperationException($"{f.Type}: 缺少请求参数") : JsonUtil.Read<T>(f.Payload);

    private bool FocusUi() { _focusUi(); return true; }

    private HandshakeReply Handshake()
    {
        var s = _store.Load();
        return new HandshakeReply(_core.State, s.ControllerHost, s.ControllerPort, s.Secret,
                                  s.MixedPort, s.CoreVersion, s.DelayTestUrl);
    }

    private async Task<bool> StopAndCleanProxyAsync()
    {
        var ok = await _core.StopAsync(default);
        // 停止后进程已不在；用 RunningProcessId 作真判据，绝不写死 false（重启竞态下会误清）
        SystemProxy.CleanupIfStale(_store.Load().MixedPort, () => _core.RunningProcessId is not null);
        _pipe.Broadcast(EventTypes.SettingsChanged, Snapshot());
        return ok;
    }

    private async Task<bool> ReloadOrStartAsync()
    {
        if (_core.State is CoreState.Running) { await _core.ReloadAsync(default); return true; }
        return await _core.StartAsync(default);
    }

    private async Task<bool> SetModeAsync(string mode)
    {
        await _client.SetModeAsync(mode, default);
        return true;                                    // 不回显本地值：UI 重读 GET /proxies（spec §7）
    }

    private SettingsSnapshot Snapshot()
    {
        var s = _store.Load();
        return new SettingsSnapshot(SystemProxy.IsEnabled(), s.MixedPort, s.DelayTestUrl, s.CoreVersion);
    }

    private bool ApplySysProxy(bool enabled)
    {
        var port = _store.Load().MixedPort;
        if (enabled) SystemProxy.Enable($"127.0.0.1:{port}", "localhost,127.0.0.1,<local>");
        else SystemProxy.Disable();
        _pipe.Broadcast(EventTypes.SettingsChanged, Snapshot());   // 事件形状只有一种，见下面的 SettingsSnapshot
        return enabled;
    }

    private Settings ApplySettings(SettingsPatch p)
    {
        var next = _store.Update(s => s with
        {
            MixedPort = p.MixedPort ?? s.MixedPort,
            ControllerPort = p.ControllerPort ?? s.ControllerPort,
            DelayTestUrl = p.DelayTestUrl ?? s.DelayTestUrl
        });
        _pipe.Broadcast(EventTypes.SettingsChanged, Snapshot());
        return next;
    }

    private async Task<UpdateResult> UpdateCoreAsync()
    {
        var progress = new Progress<UpdateProgress>(p => { if (_pipe.ClientCount > 0) _pipe.Broadcast(EventTypes.UpdateProgress, p); });
        var res = await _updater().InstallLatestAsync(progress, _core.SwapExecutableAsync, default);
        if (res is UpdateResult.Updated)                // 版本号只在更新成功时重写，UI 收到 result 后重发 handshake 取新值
        {
            var v = await _client.GetVersionAsync(default);
            if (v is not null) _store.Update(s => s with { CoreVersion = v.Version });
        }
        if (_pipe.ClientCount > 0) _pipe.Broadcast(EventTypes.UpdateResult, res);
        return res;
    }

    private async Task<ImportReply> ImportAsync(Frame f)
    {
        var req = Read<ImportRequest>(f);
        if (req.Url is null && req.Path is null) throw new InvalidOperationException("sub.import: 需要 url 或 path");
        var imp = new SubscriptionImport(_paths, _store);
        var r = req.Path is not null ? imp.ImportFromFile(req.Path)
                                     : await Task.Run(() => imp.ImportFromUrl(req.Url!, _http, default));
        if (r.ReasonIfFailed.Length == 0) _store.Update(s => s with { LastSubscription = req.Url ?? req.Path });
        return new ImportReply(r.ConfigFile, r.ReasonIfFailed.Length == 0 ? null : r.ReasonIfFailed);
    }
}

// 请求体：Contracts 里只放跨进程回包（ImportRequest/ImportReply），这些细粒度 patch 类型同样进 Contracts，
// 因为 Tray 与 UI 都要按同一形状编解码；统一放在 Contracts/Commands.cs 末尾。
```

`Contracts/Commands.cs` 末尾补上这些请求/事件载荷类型（都是 record，字段用 `[JsonPropertyName]` 对齐 snake_case）：

```csharp
public sealed record ModeRequest([property: JsonPropertyName("mode")] string Mode);
public sealed record SysProxyRequest([property: JsonPropertyName("enabled")] bool Enabled);
public sealed record SettingsPatch([property: JsonPropertyName("mixed_port")] int? MixedPort,
                                   [property: JsonPropertyName("controller_port")] int? ControllerPort,
                                   [property: JsonPropertyName("delay_test_url")] string? DelayTestUrl);
/// `settings.changed` 的唯一载荷形状：任何一次设置/系统代理变动都广播同一结构，
/// 不在同一个事件类型上挂两种形状（UI 侧就没法只靠 type 反序列化）。
public sealed record SettingsSnapshot([property: JsonPropertyName("sys_proxy_enabled")] bool SysProxyEnabled,
                                      [property: JsonPropertyName("mixed_port")] int MixedPort,
                                      [property: JsonPropertyName("delay_test_url")] string DelayTestUrl,
                                      [property: JsonPropertyName("core_version")] string? CoreVersion);
```

（`ImportRequest`/`ImportReply` 已在 Task 3 定义，字段名 `url`/`path`/`config_file`/`reason` 同样用 `[JsonPropertyName]` 显式对齐——payload 是给 UI 和人一起看的，不靠命名策略推断。）

三处硬性要求，违反任一条 Step 6 的测试就会红：
- **`CoreStart`/`CoreStop`/`core.update` 全部经 `CoreManager`**（Task 8 的生命周期锁），路由里绝不出现 `Process.Start` 或 `File.Replace`。
- `ApplySysProxy` 用 `settings.MixedPort` 拼地址，**不许硬编码 7897**。
- `ImportAsync` 失败时 `ConfigFile` 仍是旧配置路径（`SubscriptionImport` 的失败语义，Task 5），**永不清空配置**（spec §9）。

- [ ] **Step 4: 实现 P/Invoke 托盘（零 WinForms）**

```csharp
// NativeMethods.cs
using System.Runtime.InteropServices;
namespace ClashAcry.Tray;

internal static class NativeMethods
{
    public const uint WM_USER = 0x0400, NIM_ADD = 0x0, NIM_MODIFY = 0x1, NIM_DELETE = 0x2,
        NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_STATE = 0x8,
        IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10, WM_TRAYICON = WM_USER + 1,
        WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B,
        TPM_RETURNCMD = 0x0100, TPM_NONOTIFY = 0x0080, MF_STRING = 0x0, HWND_MESSAGE = -3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int cbSize; public IntPtr hWnd; public uint uID; public uint uFlags;
        public uint uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd, message, wParam, lParam; public uint time; public POINT pt; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATAW pnid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImageW(IntPtr hinst, string name, uint type, int cx, int cy, uint fuLoad);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool GetMessageW(out MSG m, IntPtr h, uint first, uint last);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] public static extern bool AppendMenuW(IntPtr menu, uint flags, uint id, string text);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll")] public static extern IntPtr GetModuleHandleW(string? name);

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASS { public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; }
}
```

```csharp
// Win32NotifyIcon.cs —— 消息窗口用 HWND_MESSAGE；图标从磁盘 .ico 经 LoadImageW 取 HICON（禁 System.Drawing）
using System.Runtime.InteropServices;
namespace ClashAcry.Tray;

public sealed class Win32NotifyIcon : IDisposable
{
    private const uint WM_TRAY = NativeMethods.WM_USER + 1;
    private static readonly WndProc _proc = OnMessage;   // 静态委托，防 GC 回收
    private readonly IntPtr _hwnd; private readonly IntPtr _icon;
    private NativeMethods.NOTIFYICONDATAW _nid;
    private readonly List<(int Id, string Text, Action OnPick)> _menu = new();

    public Win32NotifyIcon(string icoPath, string tooltip)
    {
        var inst = NativeMethods.GetModuleHandleW(null);
        NativeMethods.RegisterClassW(new NativeMethods.WNDCLASS
        { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), lpszClassName = "ClashAcryTrayWnd", hInstance = inst });
        _hwnd = NativeMethods.CreateWindowExW(0, "ClashAcryTrayWnd", "ClashAcry", 0, 0, 0, 0, 0,
            new IntPtr(NativeMethods.HWND_MESSAGE), IntPtr.Zero, inst, IntPtr.Zero);
        lock (ByHwnd) ByHwnd[_hwnd] = this;   // 静态 WndProc 靠这张表找回实例
        _icon = NativeMethods.LoadImageW(IntPtr.Zero, icoPath, NativeMethods.IMAGE_ICON, 0, 0, NativeMethods.LR_LOADFROMFILE);
        if (_icon == IntPtr.Zero) throw new InvalidOperationException($"图标加载失败：{icoPath}（Win32Error {Marshal.GetLastWin32Error()}）");

        _nid = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(), hWnd = _hwnd, uID = 1,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | 0x80 /*NIF_SHOWTIP*/,
            uCallbackMessage = WM_TRAY, hIcon = _icon, szTip = tooltip, uVersionOrTimeout = 4
        };
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _nid))
            throw new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) 失败");
        _nid.uVersionOrTimeout = 4;   // NIM_SETVERSION：用新版回调（低 16 位是消息 id）
        NativeMethods.Shell_NotifyIcon(0x4 /*NIM_SETVERSION*/, ref _nid);
    }

    public void MenuItem(int id, string text, Action onPick)
    {
        _menu.RemoveAll(m => m.Id == id); _menu.Add((id, text, onPick)); _menu.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    public void SetTip(string tooltip) { _nid.szTip = tooltip; _nid.uFlags = NativeMethods.NIF_TIP; NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref _nid); }

    private static readonly Dictionary<IntPtr, Win32NotifyIcon> ByHwnd = new();

    private static IntPtr OnMessage(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == NativeMethods.WM_TRAYICON && ByHwnd.TryGetValue(h, out var self))
        {
            self.HandleTray((uint)l, (int)(l.ToInt64() >> 32));   // 低位 uID，高位鼠标消息
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(h, m, w, l);
    }

    private void HandleTray(uint iconId, int mouseMsg)
    {
        if (mouseMsg == NativeMethods.WM_LBUTTONDBLCLK) { _menu.FirstOrDefault(m => m.Id == 1).OnPick?.Invoke(); return; }
        if (mouseMsg is not (NativeMethods.WM_RBUTTONUP or NativeMethods.WM_CONTEXTMENU)) return;

        NativeMethods.GetCursorPos(out var p);
        var menu = NativeMethods.CreatePopupMenu();
        foreach (var (id, text, _) in _menu) NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (uint)id, text);
        // 实测注意：HWND_MESSAGE 窗口不能置前台。若弹出菜单不响应点击，改为建普通隐藏窗口
        // （WS_POPUP + 不显示）后 SetForegroundWindow + GetWindowRect 定位，并把结论回写 spec §12。
        var picked = NativeMethods.TrackPopupMenuEx(menu, NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY, p.X, p.Y, _hwnd, IntPtr.Zero);
        if (picked > 0) _menu.FirstOrDefault(m => m.Id == picked).OnPick?.Invoke();
    }

    public void Dispose()
    {
        _nid.uFlags = 0; NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref _nid);
        NativeMethods.DestroyIcon(_icon); NativeMethods.DestroyWindow(_hwnd);
    }
}
```

`OnMessage` 必须是静态且能把消息路由回实例：实现时用一个 `static readonly Dictionary<IntPtr, Win32NotifyIcon> ByHwnd`（构造时登记 `_hwnd`，`Dispose` 摘除），静态 `WndProc` 按 `h` 查实例后调 `HandleTray(uint iconId, int mouseMsg)`。**回调分派不许写在 lambda 里造成委托被 GC**（`_proc` 已作为字段持有，这是硬性要求）。`HandleTray` 行为：`mouseMsg` 为 `WM_RBUTTONUP`/`WM_CONTEXTMENU` → `GetCursorPos` → `CreatePopupMenu` → 逐条 `AppendMenuW(MF_STRING, id, text)` → `TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY, x, y, _hwnd, IntPtr.Zero)` → 返回的 id 找到对应 `OnPick` 调用；`WM_LBUTTONDBLCLK` → 直接执行 id 1（打开界面）那条。菜单项固定五条：`1 打开界面` / `3 启动内核` / `4 停止内核` / `5 切换系统代理` / `7 退出`（id 留空隙便于以后插分隔线）。

- [ ] **Step 5: 实现 TrayHost 与 Program**

`TrayHost.RunAsync` 必须按此顺序：
1. `AppPaths.Default().EnsureCreated()`；`SettingsStore` → `Settings`
2. `SystemProxy.CleanupIfStale(settings.MixedPort, coreAlive: () => false)`（Tray 冷启动时内核必然不在，这一层专治上次被强杀的悬挂）
3. 从 `packaging` 复制内置内核到 `core\mihomo.exe`（仅当目标不存在，不覆盖在线更新结果），`File.WriteAllText(CoreVersionFile, "bundled")`
4. 建 `MihomoClient`/`CoreProbe`/`CoreManager`/`TrayCommandRouting`/`PipeServer`，`CoreManager.StateChanged += e => _pipe.Broadcast(EventTypes.CoreStateChanged, e)`
5. 建 `Win32NotifyIcon`，菜单项固定五条：`打开界面` / `启动内核` / `停止内核` / `切换 系统代理` / `退出`；图标 tooltip 每 2s 由 `core.state` 刷新为「内核运行中 · 系统代理开 · 7897」这类一行文案
6. 若内核未运行 → **不自动启动**，等命令（v1 语义：可预期的显式启停）
7. 退出钩子（`ConsoleCtrl` + 菜单「退出」）：`SystemProxy.Disable()` → `CoreManager.StopAsync()` → `NIM_DELETE` → `Environment.Exit(0)`

`TrayHost` 的实现照这份写（菜单五条的动作**必须是调用 `Routing.Handle`**，不在菜单回调里直接碰内核或注册表——否则 Task 10 的路由测试就覆盖不到托盘这条真实入口）：

```csharp
// TrayHost.cs
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClashAcry.Contracts;
using ClashAcry.Core;
namespace ClashAcry.Tray;

public sealed class TrayHost
{
    private readonly AppPaths _paths; private readonly SettingsStore _store;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    public CoreManager Core { get; private set; } = null!;
    public PipeServer Pipe { get; private set; } = null!;
    public TrayCommandRouting Routing { get; private set; } = null!;
    private Win32NotifyIcon? _icon;

    public async Task RunAsync(CancellationToken ct)
    {
        _paths = AppPaths.Default(); _paths.EnsureCreated();
        _store = new SettingsStore(_paths);
        var s = _store.Load();

        SystemProxy.CleanupIfStale(s.MixedPort, coreAlive: () => false);       // 第 2 层守卫：冷启动专治上次被强杀
        if (!File.Exists(_paths.CoreExecutable))                               // 第 3 步：内置内核只在缺失时铺，不覆盖在线更新结果
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "core", "mihomo.exe");
            if (File.Exists(bundled)) File.Copy(bundled, _paths.CoreExecutable);
            File.WriteAllText(_paths.CoreVersionFile, "bundled");
        }

        var client = new MihomoClient(_http, s.ControllerHost, s.ControllerPort, s.Secret);
        var probe = new CoreProbe(client);
        Core = new CoreManager(_paths, _store, client, probe);                    // launchFactory 用 Task 8 的默认实现（mihomo -d <runtime>），Tray 侧不自己拼参数
        Pipe = new PipeServer(f => Routing.Handle(f));
        Routing = new TrayCommandRouting(Core, _store, client, _paths, _http,
            () => new CoreUpdater(_paths, new ReleaseClient(_http, ReleaseClient.DefaultLatestUrl)),
            Pipe, FocusUi);
        Core.StateChanged += e => Pipe.Broadcast(EventTypes.CoreStateChanged, e);
        await Pipe.StartAsync(ct);                                              // 常驻任务：不能 await 到底

        _icon = new Win32NotifyIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"), "ClashAcry");
        _icon.MenuItem(1, "打开界面", FocusUi);
        _icon.MenuItem(3, "启动内核", () => _ = Routing.Handle(Cmd(CmdTypes.CoreStart)));
        _icon.MenuItem(4, "停止内核", () => _ = Routing.Handle(Cmd(CmdTypes.CoreStop)));
        _icon.MenuItem(5, "切换系统代理", () => _ = Routing.Handle(
            Cmd(CmdTypes.SysProxySet, new SysProxyRequest(!SystemProxy.IsEnabled()))));
        _icon.MenuItem(7, "退出", () => { _ = QuitAsync(); });

        _ = Task.Run(() => PumpTooltip(ct));                                    // tooltip 每 2s 由状态刷新，不额外探活
        MessageLoop(ct);                                                          // GetMessageW 循环：托盘回调在这里被派发
    }

    private static Frame Cmd(string type, object? payload = null) =>
        new("command", type, payload is null ? null : JsonUtil.Element(payload), 0, Guid.NewGuid().ToString("N"), null, null);

    private void FocusUi()
    {
        // 已开则由 UI 自己处理 ui.focus（单实例），未开才起进程——起 UI 是 Tray 唯一允许的跨进程 Start
        _ = Routing.Handle(Cmd(CmdTypes.UiFocus));
        if (Process.GetProcessesByName("ClashAcry.UI").Length == 0)
            Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ClashAcry.UI.exe")) { UseShellExecute = true });
    }

    private async Task QuitAsync()
    {
        await Routing.Handle(Cmd(CmdTypes.SysProxySet, new SysProxyRequest(false)));   // 复用路由，保证"托盘退出清代理"与"UI 关代理"走同一段代码
        await Core.StopAsync(default);
        _icon?.Dispose();
        Environment.Exit(0);
    }

    private void PumpTooltip(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var s = Core.State;
            var proxy = SystemProxy.IsEnabled() ? "系统代理开" : "系统代理关";
            _icon?.SetTip($"ClashAcry · {StateText(s)} · {proxy} · {_store.Load().MixedPort}");
            try { Task.Delay(2000, ct).Wait(ct); } catch { }
        }
    }

    private static string StateText(CoreState s) => s switch
    {
        CoreState.Stopped => "内核未运行", CoreState.Starting => "启动中", CoreState.Running => "内核运行中",
        CoreState.Crashed => "内核异常", CoreState.Updating => "更新中", _ => s.ToString()
    };

    private static void MessageLoop(CancellationToken ct)
    {
        while (NativeMethods.GetMessageW(out var m, IntPtr.Zero, 0, 0) && !ct.IsCancellationRequested)
        { NativeMethods.TranslateMessage(ref m); NativeMethods.DispatchMessageW(ref m); }
    }
}
```

> `WM_TRAYICON` 回调只在 `GetMessageW` 循环里才被派发；如果管道 Pump 之后忘了进消息循环，托盘图标会出现但**点了没反应**。这是纯 Win32 托盘最常见的坑，`MessageLoop` 必须在 `RunAsync` 的最后同步跑。
> `PumpTooltip` 里的 `Task.Delay(...).Wait(ct)` 是后台线程上的同步等待，可以接受（这就是个专用泵线程）；`SetTip` 内部会 `GetCursorPos` 之类都不做，纯注册表外的读，不违反 Tray ≤40 MB 线。

`Program.cs`：解析 `--no-tray`（跳过 `Win32NotifyIcon` 构造、直接进 `Task.Delay(Infinite)` 等命令，供 FlaUI/CI 无交互跑宿主）、装 `Console.CancelKeyPress` 与 `SetConsoleCtrlHandler` → `QuitAsync()`、`new TrayHost(...).RunAsync(cts.Token).GetAwaiter().GetResult()`。

- [ ] **Step 6: 跑测试 + 手动确认托盘**

```bash
dotnet test tests/ClashAcry.Core.Tests --filter TrayCommandRoutingTests
dotnet run --project src/ClashAcry.Tray -c Release
```
Expected: 测试 PASS；系统托盘出现图标，右键菜单五条可点，「停止内核」后 tooltip 更新，退出后图标消失且 `ProxyEnable=0`。
`powershell -File scripts/measure-residency.ps1 -ProcessName ClashAcry.Tray` → 记录数值，必须 ≤ 40 MB（超标停下来报告，不放判据）。

- [ ] **Step 7: 提交**

```bash
git add src/ClashAcry.Tray src/ClashAcry.Contracts/Commands.cs tests/ClashAcry.Core.Tests/TrayCommandRoutingTests.cs tests/ClashAcry.Core.Tests/TrayHarness.cs
git commit -m "feat(tray): Win32 托盘宿主与命令路由（零 XAML/零 WinForms，退出钩子清代理）"
```

---

### Task 11: CoreUpdater —— 内置内核 + 在线更新与进度

**Files:**
- Create: `src/ClashAcry.Core/ReleaseClient.cs` `ZipExtractor.cs` `CoreUpdater.cs`
- Test: `tests/ClashAcry.Core.Tests/CoreUpdaterTests.cs`

**Interfaces:**
- Consumes: `AppPaths`、`CoreManager`（替换前需停）、`PipeServer.Broadcast`
- Produces:
  - `sealed record ReleaseAsset(string Name, string BrowserDownloadUrl, long Size)`
  - `sealed record Release(string TagName, IReadOnlyList<ReleaseAsset> Assets)`
  - `UpdateProgress` / `UpdateResult` 由 Task 3 定义在 Contracts，本任务直接 import 使用（不重复声明）
  - `sealed class CoreUpdater(AppPaths paths, ReleaseClient client)`：
    `Task<string?> CheckLatestAsync(CancellationToken ct)`、
    `Task<UpdateResult> InstallLatestAsync(IProgress<UpdateProgress>? progress, SwapAsync swap, CancellationToken ct)`、
    `static ReleaseAsset? PickAsset(Release r)`
  - `delegate Task<SwapOutcome> SwapAsync(Func<Task> replaceFiles, CancellationToken ct)`（值与 `CoreManager.SwapExecutableAsync` 一致；更新器**不碰内核进程**，只把换文件动作交给它，见 Task 8）

- [ ] **Step 1: 写失败的测试**

```csharp
using System.IO.Compression;
using System.Text.Json;
using ClashAcry.Contracts;
using ClashAcry.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace ClashAcry.Core.Tests;

public class CoreUpdaterTests
{
    private readonly WireMockServer _wire = WireMockServer.Start();
    private readonly string _dir = Directory.CreateTempSubdirectory("ca-update-").FullName;

    private AppPaths Paths()
    {
        var p = AppPaths.ForTest(_dir); p.EnsureCreated();
        // 更新器是"覆盖"语义，测试必须先有一份可覆盖的旧 exe（MZ 头，内容与解出的新包不同，断言才有意义）
        if (!File.Exists(p.CoreExecutable)) File.WriteAllBytes(p.CoreExecutable, new byte[] { 0x4d, 0x5a, 1 });
        return p;
    }
    private CoreUpdater Updater() => new(Paths(), new ReleaseClient(new HttpClient(), $"{_wire.Url}/latest"));

    // 测试用 swap：与 CoreManager.SwapExecutableAsync 同语义（IOException → 已回滚），但不真起进程
    private static async Task<SwapOutcome> TestSwap(Func<Task> replaceFiles, CancellationToken ct)
    {
        try { await replaceFiles(); return SwapOutcome.Done(true); }
        catch (IOException) { return SwapOutcome.RolledBack(false); }
    }

    // 假 release zip：内存里造，含单个 .exe 条目
    private static byte[] ZipWithEntry(string entryName, byte[] content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        using (var e = zip.CreateEntry(entryName).Open()) e.Write(content);
        return ms.ToArray();
    }
    // zip == null → 不注册 /dl 映射，下载端点必然 404（模拟源站失败）
    private void StubRelease(byte[]? zip)
    {
        _wire.Given(Request.Create().WithPath("/latest").UsingGet()).RespondWith(
            Response.Create().WithStatusCode(200).WithBody(JsonSerializer.Serialize(new
            {
                tag_name = "v9",
                assets = new[] { new { name = "mihomo-windows-amd64-v9.zip", browser_download_url = $"{_wire.Url}/dl", size = zip?.Length ?? 0 } }
            })));
        if (zip is not null)
            _wire.Given(Request.Create().WithPath("/dl").UsingGet()).RespondWith(
                Response.Create().WithStatusCode(200).WithBody(zip, isBase64: false));
    }
    private string CachedZipPath() => Path.Combine(Paths().CoreDir, "downloads", "mihomo-windows-amd64-v9.zip");

    [Fact]
    public void PickAsset_PrefersCompatible_AndIgnoresToolchainVariants()
    {
        var r = new Release("v1.19.33", new[]
        {
            new ReleaseAsset("mihomo-linux-amd64-v1.19.33.zip", "u", 1),
            new ReleaseAsset("mihomo-windows-amd64-v1-go120-v1.19.33.zip", "bad", 1),
            new ReleaseAsset("mihomo-windows-amd64-compatible-v1.19.33.zip", "want", 1),
            new ReleaseAsset("mihomo-windows-amd64-v1.19.33.zip", "also-ok", 1),
        });
        Assert.Equal("mihomo-windows-amd64-compatible-v1.19.33.zip", CoreUpdater.PickAsset(r)!.Name);
    }

    [Fact]
    public void PickAsset_FallsBackToPlainAmd64_WhenCompatibleMissing()
    {
        var r = new Release("v1.19.33", new[]
        {
            new ReleaseAsset("mihomo-windows-amd64-v1-go120-v1.19.33.zip", "bad", 1),
            new ReleaseAsset("mihomo-windows-amd64-v1.19.33.zip", "want", 1),
        });
        Assert.Equal("mihomo-windows-amd64-v1.19.33.zip", CoreUpdater.PickAsset(r)!.Name);
    }

    [Fact]
    public void PickAsset_ReturnsNull_WhenNothingMatches()
    {
        var r = new Release("v1.0.0", new[] { new ReleaseAsset("mihomo-darwin-arm64-v1.0.0.zip", "u", 1) });
        Assert.Null(CoreUpdater.PickAsset(r));
    }

    [Fact]
    public async Task Install_ExtractsFirstExeEntryAndRecordsTag()
    {
        var zip = ZipWithEntry("mihomo-windows-amd64-v9.exe", new byte[] { 0x4d, 0x5a, 2, 3 });
        StubRelease(zip);

        var res = await Updater().InstallLatestAsync(null, TestSwap, default);

        Assert.Equal(UpdateResult.Updated, res);
        Assert.Equal("v9", File.ReadAllText(Paths().CoreVersionFile).Trim());
        Assert.Equal(new byte[] { 0x4d, 0x5a, 2, 3 }, File.ReadAllBytes(Paths().CoreExecutable));
    }

    [Fact]
    public async Task AlreadyLatest_SwapsNothing()
    {
        StubRelease(ZipWithEntry("mihomo-windows-amd64-v9.exe", new byte[] { 7 }));
        File.WriteAllText(Paths().CoreVersionFile, "v9");

        Assert.Equal(UpdateResult.AlreadyLatest, await Updater().InstallLatestAsync(null, TestSwap, default));
    }

    [Fact]
    public async Task Progress_IsMonotonic_Throttled_AndEndsAt100()
    {
        var zip = ZipWithEntry("mihomo-windows-amd64-v9.exe", new byte[1_000_000]);
        StubRelease(zip);
        var seen = new List<int>();
        await Updater().InstallLatestAsync(new Progress<UpdateProgress>(p => seen.Add(p.Pct)), TestSwap, default);

        Assert.Equal(100, seen.Last());
        Assert.True(seen.Zip(seen.Skip(), (a, b) => b >= a).All(x => x));           // 单调不减
        Assert.True(seen.Count < 100, $"节流失效：{seen.Count} 次回调");             // ≤1% 变化才发，最多 100 个档位
        Assert.Equal(seen.Count, seen.Distinct().Count());                          // 无重复档位
    }

    [Fact]
    public async Task DownloadFailure_KeepsOldExe_AndWritesNoCache()
    {
        StubRelease(zip: null);                       // /dl 未注册 → 404
        var before = File.ReadAllBytes(Paths().CoreExecutable);

        Assert.Equal(UpdateResult.DownloadFailed, await Updater().InstallLatestAsync(null, TestSwap, default));

        Assert.Equal(before, File.ReadAllBytes(Paths().CoreExecutable));   // 失败绝不动旧内核
        Assert.False(File.Exists(CachedZipPath()));                        // 半截包不进缓存
    }

    [Fact]
    public async Task CachedZip_SkipsNetworkEntirely_AndStillInstalls()
    {
        var zip = ZipWithEntry("mihomo-windows-amd64-v9.exe", new byte[] { 4, 5, 6 });
        StubRelease(zip: null);                       // 只有 /latest 可达
        Directory.CreateDirectory(Path.GetDirectoryName(CachedZipPath())!);
        File.WriteAllBytes(CachedZipPath(), zip);

        Assert.Equal(UpdateResult.Updated, await Updater().InstallLatestAsync(null, TestSwap, default));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Paths().CoreExecutable));
    }

    [Fact]
    public async Task InUseTarget_RollsBackAndReportsInUseRolledBack()
    {
        StubRelease(ZipWithEntry("mihomo-windows-amd64-v9.exe", new byte[] { 9 }));
        using var hold = new FileStream(Paths().CoreExecutable, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(UpdateResult.InUseRolledBack, await Updater().InstallLatestAsync(null, TestSwap, default));
        Assert.True(File.Exists(Paths().CoreExecutable + ".bak"));                  // 替换前先落 .bak，回滚有据
    }
}
```

文件头补 `using System.IO.Compression;`、`using System.Text.Json;`（`ZipArchive` / `JsonSerializer` 都靠它），其余 using 同 Task 6 的测试文件。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter CoreUpdaterTests`
Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现**

要点（不是可选）：
- `ReleaseClient(HttpClient http, string latestUrl)`：生产传 `ReleaseClient.DefaultLatestUrl`（就是 `const string DefaultLatestUrl = "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest";`，Task 10 的 `TrayHost` 装配处只引用这个常量、不再抄一遍字符串），测试传 WireMock 地址（**URL 走构造参数，不写死**，否则本任务全部测试无法落地）。请求带 `User-Agent: ClashAcry`（GitHub API 无 UA 直接 403，这是实测约束，回写 spec §4.2）。非 2xx / 超时 / 断网 → `CheckLatestAsync` 返回 `null` 静默，不抛。
- `PickAsset(Release)`：先找 `mihomo-windows-amd64-compatible-{tag}.zip`，回退 `mihomo-windows-amd64-{tag}.zip`；**用 tag 做精确后缀匹配**，否则 `-v1-go120-` 工具链变体会被误选（两个都要有测试，见 Step 1 的 `PrefersCompatible` / `FallsBackToPlainAmd64`）。
- 版本比对：`core\version.txt` 内容与 release tag 相同 → 直接返回 `AlreadyLatest`，不下载、不换文件。
- 下载：流式写 `core\downloads\<资产名>.partial`，**读满后 `File.Move` 转正**（半截包留在 `.partial`，缓存命中判据才可靠——`DownloadFailure_KeepsOldExe_AndWritesNoCache` 锁住这条）。`Content-Length` 缺失时末次以实际字节数收尾。
- 进度：`pct = (int)(received * 100 / total)`；与上次回调的 `pct` 相同则不发（节流），首次与 100% 必发——`Progress_IsMonotonic_Throttled_AndEndsAt100` 用「回调次数 == 去重次数且 <100」双向卡住这条。
- 缓存命中（`core\downloads\<资产名>` 存在**且能解出 `.exe` 条目**）→ 完全跳过网络，`CachedZip_SkipsNetworkEntirely_AndStillInstalls` 就是这条（该测试里 `/dl` 根本没注册，走通即证明没联网）。
- 解包：取 zip 内**首个 `.exe` 条目**（真实包内文件名带资产后缀，不是 `mihomo.exe`）写到临时文件，再由换文件动作落为 `core\mihomo.exe`。
- 换文件动作（交给 `swap` 委托执行的那段）：**先 `File.Copy(CoreExecutable, CoreExecutable + ".bak", overwrite: true)` 再 `File.Copy(temp, CoreExecutable, overwrite: true)`**。不要用 `File.Replace`——目标被内核占用时它会直接失败、不产生 `.bak`，回滚就无据可依；备份先行则 `InUseTarget_RollsBackAndReportsInUseRolledBack` 的 `.bak` 断言成立。IOException 从委托里抛出 → `SwapOutcome.RolledBack` → 返回 `InUseRolledBack`，**旧内核继续跑**。
- 状态：更新期间的 `CoreState.Updating` 迁移由 `CoreManager.SwapExecutableAsync` 在生命周期锁内完成（Task 8 已实现），`CoreUpdater` **不得自己改状态、不得碰进程**（spec §3 禁令 1/3）。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter CoreUpdaterTests`
Expected: PASS，9/9

- [ ] **Step 5: 接进 Tray 路由**

`core.update` → `TrayCommandRouting` 调 `updater.InstallLatestAsync(progress, _core.SwapExecutableAsync, ct)`——**更新器拿到的是 `CoreManager` 的换文件入口，不是进程控制权**（spec §3 禁令 1：内核生命周期只归 Tray，且替换动作必须落在 `_lifecycle` 锁内，避免"边下载边点启动"）。进度回调里把 `UpdateProgress` 转成 `core.update.progress` 事件广播；UI 断开时不发（`PipeServer.ClientCount > 0`）。结果 `UpdateResult` 也要广播（`core.update.result`），否则 UI 的「…中」没有终点（用户红线：长耗时操作必须有进行中反馈）。

- [ ] **Step 6: 提交**

```bash
git add src/ClashAcry.Core src/ClashAcry.Tray tests/ClashAcry.Core.Tests/CoreUpdaterTests.cs
git commit -m "feat(core): 内核在线更新（tag 精确选包、zip 缓存重试、占用回滚）"
```

---

### Task 12: UI 外壳 —— App、MainWindow、单实例、关窗即退

**Files:**
- Create: `src/ClashAcry.UI/App.xaml` `App.xaml.cs` `MainWindow.xaml` `MainWindow.xaml.cs` `Services/TrayLink.cs` `Services/SingleInstance.cs` `Services/UiPrefs.cs`
- Create（空壳页，只为让本任务的 `RootFrame.Navigate` 与导航断言跑通；内容在 Task 13/14/15 填）：`Views/OverviewPage.xaml(.cs)` `Views/ProxiesPage.xaml(.cs)` `Views/SettingsPage.xaml(.cs)`
- Delete: `dotnet new app` 生成的多余模板文件
- Test: `tests/ClashAcry.UI.Tests/ShellTests.cs`

**Interfaces:**
- Consumes: `PipeClient`、`HandshakeReply`、`UiPrefs`
- Produces:
  - `sealed class TrayLink { Task<HandshakeReply?> HandshakeAsync(); Task<bool> SendAsync(string cmd, object? payload); Task<T?> RequestAsync<T>(string cmd, object? payload); string DelayUrl { get; } HandshakeReply? LastHandshake { get; } event Action<Frame>? Event; Task<bool> EnsureTrayRunningAsync(); }`（`DelayUrl` 由 `HandshakeAsync` 填，Task 15 的测速与 Task 14 的 `DelayUrlCombo` 都读它）
  - `sealed class UiPrefs { string Backdrop; string Theme; ... 全部读写走 ApplicationData.GetForUnpackaged().LocalSettings }`

- [ ] **Step 1: MainWindow 的最小 XAML（三页 + 状态横幅占位）**

照此写，不要自创结构（`NavigationView` 是左侧栏 + 内容区的最省事外壳；再自己加 Mica/Acrylic 类容器属于重复造轮子）：

```xml
<Window x:Class="ClashAcry.UI.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <!-- 标题在代码里设：MainWindow ctor 里 `Title = "ClashAcry";`，FlaUI 的 ShellTests 断言 w.Title == "ClashAcry" -->
  <Grid>
    <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
    <InfoBar x:Name="Banner" Grid.Row="0" IsOpen="False" Severity="Warning"
             AutomationProperties.AutomationId="Banner">
      <!-- 文案放子 TextBlock 而不是 InfoBar.Message：UIA 里 Message 不保证成为 Name，
           断言只有一个可靠的读取点 -->
      <TextBlock x:Name="BannerText" AutomationProperties.AutomationId="BannerText"/>
    </InfoBar>
    <NavigationView x:Name="Nav" Grid.Row="1" PaneDisplayMode="Left" SelectionFollowsFocus="Disabled"
                    IsBackButtonVisible="Collapsed">
      <NavigationView.MenuItems>
        <NavigationViewItem Content="概览" Tag="overview" AutomationProperties.AutomationId="NavOverview"/>
        <NavigationViewItem Content="节点" Tag="proxies" AutomationProperties.AutomationId="NavProxies"/>
        <NavigationViewItem Content="设置" Tag="settings" AutomationProperties.AutomationId="NavSettings"/>
      </NavigationView.MenuItems>
      <!-- ContentTransitions 置空：默认 EntranceThemeTransition 让每次切页整块淡入，
           会被读成"界面在闪"（用户不抖动红线的第一现场）；页面实例由 MainWindow 缓存，见 Step 2 第 4 条 -->
      <Frame x:Name="RootFrame">
        <Frame.ContentTransitions><TransitionCollection/></Frame.ContentTransitions>
      </Frame>
    </NavigationView>
  </Grid>
</Window>
```

- [ ] **Step 2: App.xaml.cs 生命周期**

要求逐条：
1. `OnLaunched` → 先 `SingleInstance.TryFocusExisting()`：若 `TrayLink` 上存在 UI 已开标记则发 `ui.focus` 并 `Exit()`；否则建窗。
2. `MainWindow.Closed += (s,e) => Application.Current.Exit();` —— **关窗即退进程**（spec §3），不做隐藏分支。
3. `UnhandledException` → 写 `%LOCALAPPDATA%\ClashAcry\logs\ui-crash-.log` 并 `Banner` 显示；不允许静默吞。
4. `RootFrame.Navigate(typeof(OverviewPage))` 为默认页；`Nav.SelectionChanged` 用 `Tag` 映射页面类型。**页面实例必须复用**：三个 Page 的 XAML 都设 `NavigationCacheMode="Required"`，切页时 `RootFrame.GoBack()/GoForward()` 或自建 `Dictionary<string, Page>` 缓存——每次切页都重建页面意味着重新握手 + 重拉 `/proxies`，整页白一下，正是用户红线的现场。

- [ ] **Step 3: `TrayLink` 与"Tray 不在"横幅**

`EnsureTrayRunningAsync()`：`WaitUpAsync(500ms)` 失败 → `Process.Start` 拉起 `ClashAcry.Tray.exe`（相对 UI exe 同目录，路径从 `AppContext.BaseDirectory` 推导），再 `WaitUpAsync(3s)`；仍失败 → `BannerText.Text = "托盘宿主未运行，内核控制不可用"` + `Banner.IsOpen = true`。拉起 Tray 是唯一允许的跨进程 `Process.Start`，且只启动 Tray，不启动 mihomo（spec §3 禁令 1 不违反）。

`SendAsync(cmd, payload)` = `RequestAsync(cmd, payload)` 丢弃回包；`RequestAsync<T>(cmd, payload)` 直接转 `_client.RequestAsync(cmd, payload, _ct)`，`reply is { Error: not null }` 时 `throw new TrayCommandException(reply.Error)`，否则 `JsonUtil.Read<T>(reply.Payload)`。**fire-and-forget 不算成功**（spec §3 禁令 3）：任何需要"改动已生效"文案的调用都必须走 `RequestAsync<T>` 拿到回包。

- [ ] **Step 4: FlaUI 断言（真窗口）**

```csharp
public class ShellTests : IClassFixture<UiAppHarness>
{
    private readonly UiAppHarness _harness;
    public ShellTests(UiAppHarness harness) => _harness = harness;

    [Fact]
    public void WindowOpensWithThreeNavItems()
    {
        using var app = _harness.Launch(extraArgs: null);
        var w = app.GetMainWindow(new Automation());
        Assert.Equal("ClashAcry", w.Title);
        Assert.NotNull(w.FindFirstDescendant(cf => cf.ByAutomationId("NavOverview")));
        Assert.NotNull(w.FindFirstDescendant(cf => cf.ByAutomationId("NavProxies")));
        Assert.NotNull(w.FindFirstDescendant(cf => cf.ByAutomationId("NavSettings")));
    }

    [Fact]
    public void TrayAbsent_ShowsBannerAndDisablesCoreToggle()
    {
        Assert.True(Process.GetProcessesByName("ClashAcry.Tray").Length == 0, "本用例要求 Tray 未运行");
        using var app = _harness.Launch("--tray-absent");
        var w = app.GetMainWindow(new Automation());

        var banner = w.FindFirstDescendant(cf => cf.ByAutomationId("Banner"));
        Assert.NotNull(banner);
        Assert.False(banner.Current.IsOffscreen);                                                  // 横幅可见
        Assert.Contains("托盘宿主未运行", w.FindFirstDescendant(cf => cf.ByAutomationId("BannerText")).Current.Name);
    }
}

/// exe 路径从测试程序集位置推导，绝不写死相对路径——`dotnet test` 的工作目录不是仓库根，
/// 硬编码 "src/ClashAcry.UI/bin/..." 只会得到一个 "进程启动失败"。
public sealed class UiAppHarness
{
    private static readonly string Exe = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "ClashAcry.UI", "bin", "Release", "net10.0-windows10.0.19041.0", "win-x64", "ClashAcry.UI.exe"));

    public Application Launch(string? extraArgs)
    {
        Assert.True(File.Exists(Exe), $"未找到 UI exe：{Exe}（先跑 dotnet build -c Release）");
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false };
        if (extraArgs is not null) psi.ArgumentList.Add(extraArgs);
        return Application.Attach(Process.Start(psi)!);   // Attach 而非 Launch：需要传参
    }
}
```

（`--tray-absent` 是 UI 的测试启动参数：检测到它就跳过 `EnsureTrayRunningAsync`，让"Tray 不在"这一状态可复现。**不许为了让测试好看而把横幅常开。** FlaUI 的 `Application.Attach(Process)` 需要 `using FlaUI.Core;` + `using FlaUI.Core.Definitions;`，`xunit` 侧要 `using System.Diagnostics; using System.IO;`。）

- [ ] **Step 5: 跑通**

```bash
dotnet build ClashAcry.sln -c Release
dotnet test tests/ClashAcry.UI.Tests -c Release
```
Expected: 构建零警告（`TreatWarningsAsErrors`）；FlaUI 两条 PASS；手动跑 UI 能看到窗口、切三个页面、关掉窗口后 `Get-Process ClashAcry.UI` 无残留。

- [ ] **Step 6: 提交**

```bash
git add src/ClashAcry.UI tests/ClashAcry.UI.Tests
git commit -m "feat(ui): WinUI 3 外壳（关窗即退、单实例、Tray 缺位横幅）"
```

---

### Task 13: Backdrop 三档 —— 纯策略进 Core，Acrylic 默认与降级标注在 UI

**Files:**
- Create: `src/ClashAcry.Core/BackdropKind.cs` `src/ClashAcry.Core/BackdropPolicy.cs` `src/ClashAcry.UI/Backdrops/BackdropService.cs`
- Test: `tests/ClashAcry.Core.Tests/BackdropPolicyTests.cs`（纯函数）+ `tests/ClashAcry.UI.Tests/BackdropTests.cs`（真窗口标注）

**Interfaces:**
- Consumes: `UiPrefs.Backdrop`
- Produces:
  - `enum BackdropKind { Acrylic, Mica, Solid }`（**放 Core 不放 UI**：策略要进 Core 测试集，UI 测试集里没有 STA 宿主可以创建 `Microsoft.UI.Xaml.Window`）
  - `static class BackdropPolicy { static (BackdropKind Effective, bool Degraded, string Reason) Resolve(BackdropKind requested, bool systemAllowsTransparency, bool acrylicSupported, bool micaSupported); }`
  - `sealed class BackdropService { BackdropKind Requested { get; } BackdropKind Effective { get; } bool Degraded { get; } string DegradedReason { get; } void Apply(Window w, BackdropKind kind, ElementTheme theme); }`
  - `static bool BackdropService.TransparentEffectsAllowedBySystem()`

- [ ] **Step 1: 写失败的测试（纯策略，无窗口）**

```csharp
public class BackdropPolicyTests
{
    // (systemAllows, acrylicSupported, micaSupported) 三个开关的交叉，判据只有"生效档 + 是否降级 + 文案"
    [Theory]
    [InlineData(BackdropKind.Acrylic, true,  true,  true,  BackdropKind.Acrylic, false)]  // 默认档：整窗亚克力
    [InlineData(BackdropKind.Mica,    true,  true,  true,  BackdropKind.Mica,    false)]
    [InlineData(BackdropKind.Solid,   true,  true,  true,  BackdropKind.Solid,   false)]
    [InlineData(BackdropKind.Acrylic, false, true,  true,  BackdropKind.Solid,   true)]   // 系统关了透明
    [InlineData(BackdropKind.Mica,    false, true,  true,  BackdropKind.Solid,   true)]
    [InlineData(BackdropKind.Solid,   false, true,  true,  BackdropKind.Solid,   false)]  // 纯色本来就无所谓
    [InlineData(BackdropKind.Acrylic, true,  false, true,  BackdropKind.Solid,   true)]   // 设备不支持亚克力
    [InlineData(BackdropKind.Mica,    true,  true,  false, BackdropKind.Solid,   true)]   // 设备不支持 Mica
    public void Resolve_PicksRequestedOrSolid(BackdropKind requested, bool allows, bool acrylicOk, bool micaOk,
                                             BackdropKind expected, bool expectDegraded)
    {
        var (effective, degraded, reason) = BackdropPolicy.Resolve(requested, allows, acrylicOk, micaOk);

        Assert.Equal(expected, effective);
        Assert.Equal(expectDegraded, degraded);
        // 降级必须给出可读原因：设置页要原样展示"当前实际生效：纯色（…）"
        if (expectDegraded) Assert.False(string.IsNullOrWhiteSpace(reason));
        else Assert.Equal("", reason);
    }

    [Fact]
    public void Resolve_NeverSilentlyDowngrades_DefaultAcrylic()
    {
        // spec §8 的用户拍板：默认值不得为省开销而降档；只有系统/硬件不允许时才降，且必须留话
        var (effective, degraded, _) = BackdropPolicy.Resolve(BackdropKind.Acrylic, true, true, true);
        Assert.Equal(BackdropKind.Acrylic, effective);
        Assert.False(degraded);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter BackdropPolicyTests`
Expected: FAIL（类型不存在）→ Step 3 的 3a 落地后即 PASS，9/9

- [ ] **Step 3: 实现**

3a — Core 纯策略（`src/ClashAcry.Core/BackdropPolicy.cs`，不引用任何 WinUI 类型）：

```csharp
namespace ClashAcry.Core;

public enum BackdropKind { Acrylic, Mica, Solid }

public static class BackdropPolicy
{
    public static (BackdropKind Effective, bool Degraded, string Reason) Resolve(
        BackdropKind requested, bool systemAllowsTransparency, bool acrylicSupported, bool micaSupported) => requested switch
    {
        BackdropKind.Acrylic when !systemAllowsTransparency
            => (BackdropKind.Solid, true, "系统透明效果已关闭"),
        BackdropKind.Acrylic when !acrylicSupported
            => (BackdropKind.Solid, true, "当前设备不支持亚克力"),
        BackdropKind.Mica when !systemAllowsTransparency
            => (BackdropKind.Solid, true, "系统透明效果已关闭"),
        BackdropKind.Mica when !micaSupported
            => (BackdropKind.Solid, true, "当前设备不支持 Mica"),
        BackdropKind.Mica => (BackdropKind.Mica, false, ""),
        BackdropKind.Acrylic => (BackdropKind.Acrylic, false, ""),
        _ => (BackdropKind.Solid, false, ""),
    };
}
```

3b — UI 服务（`src/ClashAcry.UI/Backdrops/BackdropService.cs`）只做"把策略结果装到窗口上"：

```csharp
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ClashAcry.Core;
namespace ClashAcry.UI.Backdrops;

public sealed class BackdropService
{
    private readonly Func<bool> _systemAllows, _acrylicOk, _micaOk;   // 注入点：让"降级"这条分支在无窗口环境也可驱动
    private ISystemBackdrop? _current;
    private SystemBackdropConfiguration? _config;

    public BackdropService(Func<bool>? systemAllows = null, Func<bool>? acrylicOk = null, Func<bool>? micaOk = null)
    {
        _systemAllows = systemAllows ?? TransparentEffectsAllowedBySystem;
        _acrylicOk = acrylicOk ?? DesktopAcrylicController.IsSupported;
        _micaOk = micaOk ?? MicaController.IsSupported;
    }

    public BackdropKind Requested { get; private set; } = BackdropKind.Acrylic;   // spec §8：默认亚克力
    public BackdropKind Effective { get; private set; } = BackdropKind.Solid;
    public bool Degraded { get; private set; }
    public string DegradedReason { get; private set; } = "";

    public void Apply(Window w, BackdropKind kind, ElementTheme theme)
    {
        Requested = kind;
        var (effective, degraded, reason) = BackdropPolicy.Resolve(kind, _systemAllows(), _acrylicOk(), _micaOk());
        DisposeCurrent();
        _current = effective switch
        {
            BackdropKind.Acrylic => new DesktopAcrylicController(),
            BackdropKind.Mica => new MicaController { Kind = MicaKind.BaseAlloy },
            _ => null,
        };
        if (_current is not null)
        {
            _config ??= new SystemBackdropConfiguration { IsInputActive = true };
            _config.Theme = theme switch
            {
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                ElementTheme.Light => SystemBackdropTheme.Light,
                _ => SystemBackdropTheme.Default,
            };
            w.Activated -= OnActivated; w.Activated += OnActivated;
            _current.AddSystemBackdropConfiguration(_config);   // 两个 controller 都实现了 ISystemBackdrop，统一挂载
            w.SystemBackdrop = _current;
        }
        else w.SystemBackdrop = null;                            // 纯色：不挂 backdrop，由页面背景色承担

        Effective = effective; Degraded = degraded; DegradedReason = reason;
    }

    public static bool TransparentEffectsAllowedBySystem()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("EnableTransparency") as int? != 0;
    }
    private void OnActivated(object s, WindowActivatedEventArgs e) => _config!.IsInputActive = e.WindowActivationState != WindowActivationState.Deactivated;
    private void DisposeCurrent() { (_current as IDisposable)?.Dispose(); _current = null; }   // 重挂前必须释放：控制器不 Dispose 会泄漏 composition 对象
}
```

`MicaController { Kind = MicaKind.BaseAlloy }` 若在 2.5.1 上编译不过（该属性属于较新 SDK），去掉 `Kind` 初始化即可，Mica 档仍然成立。若 Task 13 的真机验证（Step 6 第 ② 项：关掉系统透明效果再看窗口）证明 `EnableTransparency` 注册表判定与实际视觉不符，改用实测等价的判据并回写 spec §8。

- [ ] **Step 4: 设置页三档单选 + 实际生效标注 + 主题三档**

`SettingsPage.xaml`：
- backdrop 组：三个 `RadioButton`（`Acrylic（默认）`/`Mica`/`纯色`，组名 `BackdropGroup`，AutomationId 分别 `BackdropAcrylic`/`BackdropMica`/`BackdropSolid`）；
- 主题组（spec §8 要求浅色/深色/跟随系统三档，本任务一并落地）：三个 `RadioButton`（`ThemeLight`/`ThemeDark`/`ThemeSystem`，组名 `ThemeGroup`），选中即 `RootPage.RequestedTheme = theme` 并写 `UiPrefs.Theme`；
- 两行状态文案：`TextBlock AutomationId="BackdropSelectedLabel"`（用户选了哪档：`Acrylic`/`Mica`/`Solid`）与 `TextBlock AutomationId="BackdropEffectiveLabel"`（实际生效：`Degraded` 为真必须写成「当前实际生效：纯色（{DegradedReason}）」，为假写「当前实际生效：亚克力 / Mica / 纯色」）。
- 本任务只交付这三组设置控件；`SettingsPage` 的**订阅导入区**（`ImportUrlBox`/`ImportButton`/`ImportResult`）在 Task 14 Step 3 补上，因为它的行为要靠 `FakeTray` 才能断言，而 `FakeTray` 是 Task 14 的产物。Task 13 的测试不涉及这三个控件，空着不影响本任务通过。

选中即 `UiPrefs.Backdrop = kind; _backdrop.Apply(this, kind, theme); 更新两个 Label`，**不重启、不重开窗**（spec §8 即时切换）。

> 为什么多一个 `BackdropSelectedLabel`：FlaUI 各版本读 `TogglePattern`/`SelectionItemPattern` 的写法不一样（`Current.Toggle` 在 FlaUI 4/5 已移除），而 `Current.Name` 是 UIA **属性**、跨版本稳定。测试一律走"UI 自己把状态写成一行可读文本"，不在测试里跟 pattern API 较劲。同理，`OverviewPage` 的开关状态一律由 `CoreStateLabel` 文本反映，测试不用读 `ToggleSwitch` 的 pattern。

- [ ] **Step 5: UI 侧断言（只读，真窗口）**

`tests/ClashAcry.UI.Tests/BackdropTests.cs` 复用 Task 12 的 `UiAppHarness`，只断言"标注说得对"，不断言材质本身。
本项目**只引用 Core，不引用 UI 工程**（WinUI 应用工程被测试项目引用会拖进一整套 XAML 编译器依赖，收益为零）——所以判据写成自一致性断言，而不是在测试里重算材质支持度：

```csharp
public class BackdropTests : IClassFixture<UiAppHarness>
{
    private readonly UiAppHarness _harness;
    public BackdropTests(UiAppHarness harness) => _harness = harness;

    private static string Text(Window w, string id) => w.FindFirstDescendant(cf => cf.ByAutomationId(id)).Current.Name;
    private static void OpenSettings(Window w) => w.FindFirstDescendant(cf => cf.ByAutomationId("NavSettings")).Click();

    [Fact]
    public void DefaultRequestsAcrylic_AndLabelIsConsistent()
    {
        using var app = _harness.Launch(extraArgs: null);
        var w = app.GetMainWindow(new Automation());
        OpenSettings(w);

        Assert.Equal("Acrylic", Text(w, "BackdropSelectedLabel"));                 // 默认档必须是"请求亚克力"
        var label = Text(w, "BackdropEffectiveLabel");
        Assert.True(label.Contains("亚克力") || label.Contains("Mica") || label.Contains("纯色"), label);
        if (!label.Contains("亚克力")) Assert.Contains("（", label);                 // 降级必须写明原因（spec §8）
    }

    [Fact]
    public void SwitchingToSolid_ReloadsLabelWithoutReopeningWindow()
    {
        using var app = _harness.Launch(extraArgs: null);
        var w = app.GetMainWindow(new Automation());
        var hwndBefore = w.Current.NativeWindowHandle;
        OpenSettings(w);
        w.FindFirstDescendant(cf => cf.ByAutomationId("BackdropSolid")).Click();

        Assert.Equal(hwndBefore, w.Current.NativeWindowHandle);                                          // 换档不得重开窗口
        Assert.Equal("Solid", Text(w, "BackdropSelectedLabel"));
        Assert.Contains("纯色", Text(w, "BackdropEffectiveLabel"));                   // 纯色档：生效即纯色，无降级
    }

    [Fact]
    public void PrefsSurviveRestart()
    {
        using (var app = _harness.Launch(extraArgs: null))
        {
            var w = app.GetMainWindow(new Automation());
            OpenSettings(w);
            w.FindFirstDescendant(cf => cf.ByAutomationId("BackdropMica")).Click();
            Assert.Equal("Mica", Text(w, "BackdropSelectedLabel"));
            w.Close();
        }
        using var again = _harness.Launch(extraArgs: null);                           // 重开进程：偏好必须留下来
        var w2 = again.GetMainWindow(new Automation());
        OpenSettings(w2);
        Assert.Equal("Mica", Text(w2, "BackdropSelectedLabel"));
    }
}
```

（`using FlaUI.Core; using FlaUI.Core.Automation;`。HWND 从 `w.Current.NativeWindowHandle` 取（FlaUI 的 `Window` 是 UIA 元素，没有 `Handle` 属性）；材质本身没法用 UIA 断言，交给 Step 6 的肉眼与 Task 16 的像素取样。`UiAppHarness.Launch` 返回的 `Application` 要能被 `using` 且 `Dispose` 时真的杀掉 UI 进程——Task 12 的实现里用 `Application.Attach` 已满足。）

- [ ] **Step 6: 跑通 + 肉眼确认**

```bash
dotnet test tests/ClashAcry.Core.Tests --filter BackdropPolicyTests
dotnet build ClashAcry.sln -c Release
dotnet test tests/ClashAcry.UI.Tests --filter BackdropTests
```
Expected: 策略 9/9、UI 3/3。然后手动两项（这俩只能人眼）：① 切三档，窗口背景立刻变化且窗口没重开；② 到「设置 → 个性化 → 颜色 → 透明效果」关掉后重开 UI，窗口不透明，且设置页标注出现「当前实际生效：纯色（系统透明已关闭）」——spec §8「降级要明说」的验收点。

- [ ] **Step 7: 提交**

```bash
git add src/ClashAcry.Core src/ClashAcry.UI tests/ClashAcry.Core.Tests/BackdropPolicyTests.cs tests/ClashAcry.UI.Tests/BackdropTests.cs
git commit -m "feat(ui): backdrop 三档即时切换 + 主题三档（默认整窗亚克力，降级显式标注）"
```

---

### Task 14: OverviewPage —— 内核开关、模式、系统代理、更新进度

**Files:**
- Create: `src/ClashAcry.UI/ViewModels/OverviewViewModel.cs` `Views/OverviewPage.xaml(.cs)`
- Modify: `src/ClashAcry.UI/Views/SettingsPage.xaml(.cs)`（补订阅导入区：`ImportUrlBox`/`ImportButton`/`ImportResult`）
- Test: `tests/ClashAcry.UI.Tests/OverviewTests.cs` `tests/ClashAcry.UI.Tests/Fakes/FakeTray.cs`

**Interfaces:**
- Consumes: `TrayLink`、`MihomoClient`、`CoreState`、`UpdateProgress`
- Produces: `OverviewViewModel` 暴露 `bool CoreToggleEnabled`、`string CoreStateText`、`bool SysProxyOn`、`string Mode`、`int UpdatePct`（-1 = 无进行中）、`string? ErrorText`

- [ ] **Step 1: 写失败的测试（真窗口 + 假 Tray）**

关键手法：UI 侧只经命名管道与 Tray 说话，所以**测试进程里起一个 `PipeServer` 当假 Tray**，UI 的 `WaitUpAsync` 直接命中它（管道名固定，因此本项目的 UI 测试必须串行——沿用 Task 9 的 `[assembly: CollectionBehavior(DisableTestParallelization = true)]`）。不这样做就只能靠手动点击，`Crashed`、进度推进这类"事件驱动的状态"根本没法断言。

```csharp
public sealed class FakeTray : IAsyncDisposable
{
    private readonly PipeServer _server;
    public List<Frame> Commands { get; } = new();
    public Func<Frame, Frame?> Responder;

    public FakeTray()
    {
        // 默认回包：除 handshake 外的命令一律回"成功"，形状必须是各自约定的 record——
        // 回成原始 payload（那是请求）会让 UI 的反序列化直接抛异常，测试就变成了测 PipeServer。
        Responder = f => f with { Payload = JsonUtil.Element(new ImportReply(@"C:\fake\runtime\config.yaml", null)) };
        _server = new PipeServer(f => { lock (Commands) Commands.Add(f); return Task.FromResult(Responder(f)); });
        _ = _server.StartAsync(default);            // AcceptLoop 是常驻任务，不能 await
    }

    public void HandshakeWith(HandshakeReply reply) =>
        Responder = f => f.Type == CmdTypes.Handshake
            ? f with { Payload = JsonUtil.Element(reply) }
            : f with { Payload = JsonUtil.Element(new ImportReply(@"C:\fake\runtime\config.yaml", null)) };

    public void Emit(string type, object payload) => _server.Broadcast(type, payload);

    public async Task<bool> WaitUiConnectedAsync(TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (_server.ClientCount > 0) return true;
            await Task.Delay(50);
        }
        return false;
    }
    public async ValueTask DisposeAsync() => await _server.DisposeAsync();
}

public class OverviewTests : IClassFixture<UiAppHarness>
{
    private readonly UiAppHarness _harness;
    public OverviewTests(UiAppHarness harness) => _harness = harness;

    private static readonly HandshakeReply Stopped =
        new(CoreState.Stopped, "127.0.0.1", 9097, "sec", 7897, "v-test", "http://www.gstatic.com/generate_204");

    private static string Text(Window w, string id) =>
        w.FindFirstDescendant(cf => cf.ByAutomationId(id)).Current.Name;

    [Fact]
    public async Task CrashedEvent_ShowsAbnormalState_AndRawStderrInLog()
    {
        using var tray = new FakeTray();
        tray.HandshakeWith(Stopped);
        using var app = _harness.Launch(extraArgs: null);
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(5)));
        var w = app.GetMainWindow(new Automation());

        tray.Emit(EventTypes.CoreStateChanged,
            new CoreStateChanged(CoreState.Crashed, "connection refused",
                new[] { "time=… level=error msg=bind: address already in use" }));

        await WaitUntil(() => Text(w, "CoreStateLabel").Contains("异常"), TimeSpan.FromSeconds(3));
        w.FindFirstDescendant(cf => cf.ByAutomationId("LogButton")).Click();     // 展开日志
        await WaitUntil(() => Text(w, "LogText").Contains("address already in use"), TimeSpan.FromSeconds(3));
        Assert.DoesNotContain("地址已被使用", Text(w, "LogText"));               // Go 原文不翻译（spec §9）
    }

    [Fact]
    public async Task UpdateClick_DisablesButton_ShowsPercent_AndRestoresOnResult()
    {
        using var tray = new FakeTray();
        tray.HandshakeWith(Stopped);
        using var app = _harness.Launch(extraArgs: null);
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(5)));
        var w = app.GetMainWindow(new Automation());

        w.FindFirstDescendant(cf => cf.ByAutomationId("UpdateButton")).Click();

        await WaitUntil(() => Text(w, "UpdateButton").Contains("%"), TimeSpan.FromSeconds(3));
        Assert.False(w.FindFirstDescendant(cf => cf.ByAutomationId("UpdateButton")).Current.IsEnabled);
        Assert.Contains(CmdTypes.CoreUpdate, tray.Commands.Select(c => c.Type));    // 走管道，不在 UI 里下载

        var seen = new List<int>();
        foreach (var pct in new[] { 0, 37, 100 })
        {
            tray.Emit(EventTypes.UpdateProgress, new UpdateProgress(pct * 1024, 102_400, pct));
            seen.Add(pct);
        }
        await WaitUntil(() => Text(w, "UpdateButton") == "更新内核", TimeSpan.FromSeconds(3));
        tray.Emit(EventTypes.UpdateResult, "Updated");
        Assert.Equal("v-test", Text(w, "CoreVersionLabel"));                        // 版本行来自握手，进度不冒充版本
    }

    [Fact]
    public async Task SilentRefresh_LeavesEveryVisibleTextNodeUntouched()
    {
        using var tray = new FakeTray();
        tray.HandshakeWith(Stopped);
        using var app = _harness.Launch(extraArgs: null);
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(5)));
        var w = app.GetMainWindow(new Automation());
        w.FindFirstDescendant(cf => cf.ByAutomationId("NavProxies")).Click();       // 让代理页在后台挂着刷新
        w.FindFirstDescendant(cf => cf.ByAutomationId("NavOverview")).Click();

        var before = new[] { "CoreStateLabel", "CoreVersionLabel", "SysProxyStateLabel" }.ToDictionary(Text);
        await Task.Delay(2_600);                                                    // 跨过至少一个 2s 刷新周期
        foreach (var (id, text) in before)
            Assert.Equal(text, Text(w, id));                                        // 用户红线：刷新不得留下可见痕迹
    }

    [Fact]
    public async Task CoreToggle_On_SendsCoreStart_AndWaitsForStateEvent()
    {
        using var tray = new FakeTray();
        tray.HandshakeWith(Stopped);
        using var app = _harness.Launch(extraArgs: null);
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(5)));
        var w = app.GetMainWindow(new Automation());

        w.FindFirstDescendant(cf => cf.ByAutomationId("CoreToggle")).Click();           // 点开关，不读 pattern（见 Task 13 Step 4 说明）

        await WaitUntil(() => tray.Commands.Any(c => c.Type == CmdTypes.CoreStart), TimeSpan.FromSeconds(3));
        tray.Emit(EventTypes.CoreStateChanged, new CoreStateChanged(CoreState.Running, null, Array.Empty<string>()));
        await WaitUntil(() => Text(w, "CoreStateLabel") == "运行中", TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ImportUrl_FallsThroughToTray_AndShowsResultInline()
    {
        using var tray = new FakeTray();
        tray.HandshakeWith(Stopped);
        using var app = _harness.Launch(extraArgs: null);
        var w = app.GetMainWindow(new Automation());
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(5)));

        w.FindFirstDescendant(cf => cf.ByAutomationId("NavSettings")).Click();
        w.FindFirstDescendant(cf => cf.ByAutomationId("ImportUrlBox")).Click();
        Keyboard.Type("http://127.0.0.1:1/sub.yaml");              // FlaUI 输入用真实键鼠事件，SetValue 对 WinUI TextBox 不可靠
        w.FindFirstDescendant(cf => cf.ByAutomationId("ImportButton")).Click();

        Assert.Contains(CmdTypes.SubImport, tray.Commands.Select(c => c.Type));    // 导入只在 Tray 执行（写 config.yaml 的唯一写者）
        await WaitUntil(() => Text(w, "ImportResult").Contains("已导入"), TimeSpan.FromSeconds(5));
        Assert.NotEqual("导入中…", Text(w, "ImportButton"));                       // 完成后按钮文案必须回来
    }

    private static async Task WaitUntil(Func<bool> cond, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline) { if (cond()) return; await Task.Delay(100); }
        throw new TimeoutException("断言等待超时");
    }
}
```

（`FlaUI` 侧 `using FlaUI.Core; using FlaUI.Core.Automation; using FlaUI.Core.Input;`——`Keyboard.Type` 在 `FlaUI.Core.Input` 命名空间下，漏了它编译不过；`System` 侧 `using System.Linq; using System.Threading.Tasks; using ClashAcry.Contracts; using ClashAcry.Core;`。`FakeTray` 与 `WaitUntil` 写到 `tests/ClashAcry.UI.Tests/Fakes/` 下，Task 15 的窗口测试要共用。测试项目引用 Core 即可，**不引用 UI 工程**。最后一个用例断言的是"点下去不等于已启动——状态必须等 Tray 事件"（spec §3 禁令 3 的 UI 侧对应物），UI 不许做乐观翻转。）

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/ClashAcry.UI.Tests --filter OverviewTests`
Expected: FAIL

- [ ] **Step 3: 实现页面**

XAML 结构（自上而下，全部控件带 `AutomationProperties.AutomationId`，**id 必须与 Step 1 测试里的字符串逐字对齐**，否则测试找不到元素只会得到 `null` 引用异常）：

`OverviewPage.xaml`：状态区（`CoreStateLabel` + `CoreToggle` `ToggleSwitch`）、模式区（`ModeRadio` 三条：规则/全局/直连）、代理区（`SysProxyToggle` + `SysProxyStateLabel`）、更新区（`UpdateButton` + `UpdateProgress` `ProgressBar` + `CoreVersionLabel`）、日志区（`LogButton` → `Flyout` 内 `ScrollViewer`（`AutomationId="LogPanelBackground"`）包 `TextBlock x:Name/AutomationId="LogText"`，等宽字体、`IsTextSelectionEnabled="True"`）。根 `Grid` 带 `AutomationId="PageBackground"`；三条 `ModeRadio` 里给第一条的模板外圈 `Ellipse` 挂 `AutomationId="ModeRadioBorder"`（`FindFirstDescendant` 取到的就是它）。这两个 id 是给 Task 16 的像素取样用的，本任务不单独断言。

> 日志区不用 `TextBox IsReadOnly`：WinUI 里 `TextBox` 的 UIA `Name` 不是它的内容（要读 ValuePattern），而 `TextBlock` 的 `Name` 就是文本内容——断言只有一个可靠读取点，和 `BannerText`/`BackdropSelectedLabel` 是同一条约定。选择复制靠 `IsTextSelectionEnabled` 而不是靠可编辑控件。

`SettingsPage.xaml` 追加订阅导入区（本任务交付，Task 13 只做了 backdrop/主题三组）：
- `TextBox AutomationId="ImportUrlBox"`（占位文案「订阅链接 / 节点配置 URL」）
- `Button AutomationId="ImportButton"`（`Content="导入订阅"`）
- `TextBlock AutomationId="ImportResult"`（初始为空字符串，不得预填任何提示，否则 `ImportUrl_FallsThroughToTray_AndShowsResultInline` 的「不含"导入中"」断言会在结果落地前偶然成立）
- 点击处理：`ImportButton.IsEnabled = false; ImportButton.Content = "导入中…";` → `var reply = await _link.RequestAsync<ImportReply>(CmdTypes.SubImport, new ImportRequest(url, null));` → `ImportResult.Text = reply is null ? "托盘未响应" : reply.Reason is null ? $"已导入：{reply.ConfigFile}" : $"导入失败：{reply.Reason}";`，并在 `finally` 恢复 `Content = "导入订阅"`/`IsEnabled = true`（spec §9：长耗时操作要有进行中反馈，且**不得用阻塞弹窗**——失败只落行内文案，绝不 `MessageDialog`）。
- `ImportFileButton`（`Content="从本地 yaml 导入"`）：`var p = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary }; p.FileTypeFilter.Add(".yaml"); p.FileTypeFilter.Add(".yml"); p.InitializeWithWindow(WindowHandle);` → 选到文件后同样走 `SubImport`，`new ImportRequest(null, p.Path)`，回包/文案与 URL 通路一致（spec §6 的「订阅 URL 下载 或 本地 yaml」）。**这一条不进 FlaUI**：`FileOpenPicker` 是系统对话框、属另一进程，UIA 驱动不稳；它的行为由 Task 5 的 `ImportFromFile` 单测 + Task 10 的 `sub.import(path)` 路由测试 + Task 18 冒烟清单的人工一步共同覆盖。
- `DelayUrlCombo`（`ComboBox`，项：`http://www.gstatic.com/generate_204` / `https://cp.cloudflare.com/generate_204` / `http://www.google.com/generate_204`）：初值取 `handshake` 下发的 `DelayTestUrl`（Task 3 已把它放进 `HandshakeReply`，Task 15 的测速也用同一个值）；选中即 `_link.SendAsync(CmdTypes.SettingsUpdate, new SettingsPatch(null, null, selectedUrl))`，随后 `settings.changed` 广播的 `SettingsSnapshot` 用来纠正/回显（事件与请求用的是同一个形状，不存在"同一个事件两种 payload"）。这个 ComboBox 同时是 Task 16 `ComboArrow` 取样的目标，模板覆盖别漏。

ViewModel 要求：
- `CoreStateText` 映射固定：`Stopped→「未运行」 Running→「运行中」 Starting→「启动中…」 Crashed→「异常」 Updating→「更新中…」`
- 静默刷新：`DispatcherTimer` 2s 只在值真变时赋 `Text`（红线：不得可见抖动）
- `CoreVersionLabel` 取 `handshake` 下发的 `CoreVersion`，更新完成后由 `core.update.result` 触发重读（进度百分比不进这一行）
- 模式切换与系统代理开关：`await _link.SendAsync(CmdTypes.ModeSet/SysProxySet, ...)`，成功后不本地乐观改状态，等 `core.state`/重读 `GET /configs`（若 Task 1 实测 `GET /configs` 404，则改由 `GET /proxies` 的响应与 Tray 侧记录推断，并在 spec 记一笔）
- 更新按钮：`UpdatePct` 来自 `core.update.progress` 事件；`100` 后按钮文案回「更新内核」并更新 `CoreVersionLabel`
- 日志区原样展示 `CoreStateChanged.StderrTail` 的 Go 原文，一行一条，**不翻译、不裁剪、不加表情**（spec §9）

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/ClashAcry.UI.Tests --filter OverviewTests`
Expected: PASS，5/5（`CrashedEvent` / `UpdateProgress` / `SilentRefresh` / `CoreToggle` / `ImportUrl`）

- [ ] **Step 5: 提交**

```bash
git add src/ClashAcry.UI tests/ClashAcry.UI.Tests/OverviewTests.cs tests/ClashAcry.UI.Tests/Fakes
git commit -m "feat(ui): 概览页（内核/模式/系统代理开关与内核更新进度反馈）"
```

---

### Task 15: ProxiesPage —— 组树、切换、测速、不抖动更新

**Files:**
- Create: `src/ClashAcry.Core/Proxies/ProxyNodeVm.cs` `Proxies/ProxyGroupVm.cs` `Proxies/ProxiesViewModel.cs`
- Create: `src/ClashAcry.UI/Views/ProxiesPage.xaml(.cs)`
- Test: `tests/ClashAcry.Core.Tests/ProxiesViewModelTests.cs`（合并/测速/切换的纯逻辑）+ FlaUI 不抖动断言并入 `tests/ClashAcry.UI.Tests/OverviewTests.cs` 同目录新文件 `ProxiesNoJitterTests.cs`

**Interfaces:**
- Consumes: `MihomoClient.GetProxiesAsync/GetProvidersAsync/SetProxyAsync/GetDelayAsync/RefreshProviderAsync`、`Settings.DelayTestUrl`（经 `handshake` 下发）
- Produces:
  - `class ProxyNodeVm { string Name; bool Selected; string DelayText; bool IsTesting; string? ProviderName; bool Highlight; }`（`INotifyPropertyChanged`）
  - `class ProxyGroupVm { string Name; string Type; string Now; ObservableCollection<ProxyNodeVm> Nodes; bool Expanded; }`
  - `sealed class ProviderRowVm(string Name, DateTime? UpdatedAt, IReadOnlyList<string> Members)`（spec §6「展示 `updatedAt` 与成员数」；`string UpdatedAtText => UpdatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "未知"`、`int MemberCount => Members.Count`、`bool IsRefreshing` 由 VM 计算，XAML 里不做日期与集合运算）
  - `sealed class ProxiesViewModel`：
    `ObservableCollection<ProxyGroupVm> Groups`、`ObservableCollection<ProviderRowVm> ProviderRows`（由 `RefreshAsync` 的 `providers` 参数填充，键序按 `Name` 排序，保证轮询不产生行序跳动）、`int TestConcurrency => 4`、
    `Task RefreshAsync(ProxiesResponse proxies, ProvidersResponse? providers)`（**吃已拉好的数据，不负责拉**，这样合并逻辑在 Core 里可测）、
    `Task SelectAsync(ProxyGroupVm group, ProxyNodeVm node, CancellationToken ct)`、
    `Task TestGroupAsync(ProxyGroupVm group, CancellationToken ct)`、
    `Task TestAllAsync(CancellationToken ct)`、
    `Task RefreshProviderAsync(string providerName, CancellationToken ct)`（spec §6 的 Provider 手动刷新入口；把对应 `ProviderRowVm.IsRefreshing` 置真再发 PUT，完成后 `RefreshAsync` 重拉纠正——刷新中的行也要有进行中反馈）
    构造：`ProxiesViewModel(Func<CancellationToken, Task<ProxiesResponse>> fetch, Func<string, string, CancellationToken, Task> putProxy, Func<string, CancellationToken, Task<int>> measure, Func<string, CancellationToken, Task> refreshProvider, string delayTestUrl)`

- [ ] **Step 1: 写失败的测试（含不抖动判据）**

放在 Core 测试集有两个理由：`ObservableCollection` 属于 `System.ObjectModel`，不需要 UI 工程；而**合并逻辑是"不抖动"这条红线的唯一可实现处**，它在 UI 里的话就只能靠人眼盯窗口，那是验收不是测试。

```csharp
public class ProxiesViewModelTests
{
    private static ProxyDetail Group(string name, string now, params string[] all) => new()
    { Name = name, Type = "select", Now = now, All = all.ToList(), Hidden = false };
    private static ProxyDetail Node(string name, string? provider = null, int? liveDelay = null,
                                    params DelaySample[] history) => new()
    { Name = name, Type = "ss", ProviderName = provider, Delay = liveDelay, History = history.ToList() };
    private static ProxiesResponse Resp(params ProxyDetail[] details) =>
        new(details.ToDictionary(d => d.Name, d => d));

    // 五个委托全靠注入：fetch 默认返回空表，measure 默认 -1（"拿不到实时值"），记录用的 List 由用例自己传进来
    private static ProxiesViewModel Vm(
        Func<string, CancellationToken, Task<int>>? measure = null,
        List<(string Group, string Node)>? puts = null,
        List<string>? providerRefreshes = null,
        Func<CancellationToken, Task<ProxiesResponse>>? fetch = null) => new(
            fetch ?? (ct => Task.FromResult(Resp())),
            (g, n, ct) => { puts?.Add((g, n)); return Task.CompletedTask; },
            measure ?? ((n, ct) => Task.FromResult(-1)),
            p => { providerRefreshes?.Add(p); return Task.CompletedTask; },
            "http://www.gstatic.com/generate_204");

    [Fact]
    public async Task Refresh_MatchesExistingNodeVmsByName_AndKeepsVisualTreeItems()
    {
        var vm = Vm();
        await vm.RefreshAsync(Resp(Group("G", "n1", "n1", "n2"), Node("n1"), Node("n2")), null);
        var first = vm.Groups[0].Nodes[0];

        await vm.RefreshAsync(Resp(Group("G", "n1", "n1", "n2"), Node("n1", liveDelay: 99), Node("n2")), null);

        Assert.Same(first, vm.Groups[0].Nodes[0]);                 // 同实例复用：容器不重建，ItemContainer 不重挂
        Assert.Equal("99 ms", first.DelayText);                    // 数值就地更新
        Assert.Equal(2, vm.Groups[0].Nodes.Count);
    }

    [Fact]
    public async Task Refresh_AddsAndRemovesOnlyChangedMembers()
    {
        var vm = Vm();
        await vm.RefreshAsync(Resp(Group("G", "n1", "n1"), Node("n1")), null);
        var survivor = vm.Groups[0].Nodes[0];

        await vm.RefreshAsync(Resp(Group("G", "n1", "n1", "n3"), Node("n1"), Node("n3")), null);   // 新增 n3
        Assert.Same(survivor, vm.Groups[0].Nodes[0]);
        Assert.Equal(new[] { "n1", "n3" }, vm.Groups[0].Nodes.Select(n => n.Name));

        await vm.RefreshAsync(Resp(Group("G", "n3", "n3"), Node("n3")), null);                     // 移除 n1
        Assert.Equal(new[] { "n3" }, vm.Groups[0].Nodes.Select(n => n.Name));
        Assert.Equal("n3", vm.Groups[0].Now);                              // 选中项来源永远是 mihomo 的 now（spec §7）
    }

    [Fact]
    public async Task MemberWithoutLiveDelay_ShowsLastSampleMarkedShangCi()
    {
        // provider 成员按 Task 1 实测拿不到活测值：history 最新样本 + 「上次」标记，不冒充实时值（spec §12）
        var vm = Vm();
        await vm.RefreshAsync(Resp(Group("G", "n1", "n1"), Node("n1", provider: "sub-a")),
            new ProvidersResponse(new Dictionary<string, ProviderInfo>
            {
                ["sub-a"] = new() { Name = "sub-a", Proxies = new() { Node("n1", "sub-a", liveDelay: null, history: new[] { new DelaySample("t", 12, null) }) } }
            }));

        Assert.Equal("上次 12 ms", vm.Groups[0].Nodes[0].DelayText);
        Assert.Equal("sub-a", vm.Groups[0].Nodes[0].ProviderName);
    }

    [Fact]
    public async Task Providers_AreProjectedAsRowsWithNameTimeAndCount()
    {
        // spec §6「Provider：手动刷新，展示 updatedAt 与成员数」——这三样在 VM 里成形，页面上只绑不做运算
        // Task 1 实测：`GET /providers/proxies` 的表里混有每个策略组自动生成的 `vehicleType:"Compatible"`
        // 伪 provider 与一个 `default`。VM 必须按 vehicleType 过滤（只留 File / Http），否则页面上是一堆假行。
        var vm = Vm();
        await vm.RefreshAsync(Resp(Group("G", "n1", "n1"), Node("n1", provider: "sub-a")),
            new ProvidersResponse(new Dictionary<string, ProviderInfo>
            {
                ["sub-b"] = new() { Name = "sub-b", VehicleType = "File", UpdatedAt = new DateTime(2026, 10, 26, 8, 30, 0),
                                    Proxies = new() { Node("n2", "sub-b"), Node("n3", "sub-b") } },
                ["sub-a"] = new() { Name = "sub-a", VehicleType = "Http", UpdatedAt = new DateTime(2026, 10, 25, 1, 2, 0),
                                    Proxies = new() { Node("n1", "sub-a") } },
                ["G"] = new() { Name = "G", VehicleType = "Compatible",
                                Proxies = new() { Node("n1", null) } },           // 组自动生成的伪 provider：不得成行
                ["default"] = new() { Name = "default", VehicleType = "Compatible", Proxies = new() { } },
            }));

        Assert.Equal(new[] { "sub-a", "sub-b" }, vm.ProviderRows.Select(r => r.Name));   // 按名排序：轮询不得让行序跳动
        Assert.Equal(1, vm.ProviderRows[0].MemberCount);
        Assert.Equal(2, vm.ProviderRows[1].MemberCount);
        Assert.Contains("2026-10-26", vm.ProviderRows[1].UpdatedAtText);                  // 由 VM 格式化
        Assert.Equal("未知", new ProviderRowVm("x", null, Array.Empty<string>()).UpdatedAtText);              // updatedAt 缺失不许显示空或 0001-01-01
    }

    [Fact]
    public async Task MemberWithNeitherLiveNorHistory_ShowsTimeout()
    {
        var vm = Vm();
        await vm.RefreshAsync(Resp(Group("G", "n1", "n1"), Node("n1", provider: "sub-a")), null);
        Assert.Equal("超时", vm.Groups[0].Nodes[0].DelayText);
    }

    [Fact]
    public async Task Select_PutsThenRefetches_AndHighlightsOnce()
    {
        var puts = new List<(string Group, string Node)>();
        var fetches = 0;
        var after = Resp(Group("G", "n2", "n1", "n2"), Node("n1"), Node("n2"));      // 服务端已切到 n2
        var vm = Vm(puts: puts, fetch: ct => { fetches++; return Task.FromResult(after); });

        await vm.RefreshAsync(Resp(Group("G", "n1", "n1", "n2"), Node("n1"), Node("n2")), null);
        fetches = 0;
        await vm.SelectAsync(vm.Groups[0], vm.Groups[0].Nodes[1], default);           // 选 n2

        Assert.Equal(("G", "n2"), puts.Single());
        Assert.Equal(1, fetches);                                                     // PUT 之后全量重拉，本地不猜
        Assert.True(vm.Groups[0].Nodes[1].Highlight);                                 // 刚点的那条高亮，结构不变
        Assert.Equal("n2", vm.Groups[0].Nodes[1].Name);
        Assert.Equal("n2", vm.Groups[0].Now);                                         // 选中态来自重拉结果
    }

    [Fact]
    public async Task TestAll_NeverExceedsFourInFlight()
    {
        int live = 0, peak = 0;
        var gate = new object();
        var names = Enumerable.Range(0, 12).Select(i => $"n{i}").ToArray();
        var data = Resp(new[] { Group("G", "n0", names) }.Concat(names.Select(n => Node(n))).ToArray());
        var vm = Vm(fetch: ct => Task.FromResult(data), measure: async (n, ct) =>
        {
            lock (gate) peak = Math.Max(peak, ++live);
            await Task.Delay(40, ct);
            lock (gate) live--;
            return 20;
        });

        await vm.RefreshAsync(data, null);
        await vm.TestAllAsync(default);

        Assert.Equal(4, peak);                                                   // 并发上限是硬约束，不是"大概"
        Assert.Equal(12, vm.Groups[0].Nodes.Count(n => n.DelayText == "20 ms"));
        Assert.All(vm.Groups[0].Nodes, n => Assert.False(n.IsTesting));          // 全部收尾，不留"测速中"僵尸态
    }

    [Fact]
    public async Task ProviderRefresh_GoesThroughRegisteredEndpoint()
    {
        var refreshed = new List<string>();
        var vm = Vm(providerRefreshes: refreshed);
        await vm.RefreshAsync(Resp(Group("G", "n1", "n1"), Node("n1", provider: "sub-a")), null);

        await vm.RefreshProviderAsync("sub-a", default);

        Assert.Equal(new[] { "sub-a" }, refreshed);                              // 手动刷新走 PUT /providers/proxies/{name}
    }
}
```

（本文件顶部只要 `using ClashAcry.Core;`——`System.Linq`、`System.Collections.Generic`、`System.Threading.Tasks` 由 `Directory.Build.props` 的 `ImplicitUsings` 带进来，xUnit 的 `using Xunit;` 若模板的 `GlobalUsings.cs` 没带就自己补一行；`ProxyDetail`/`ProvidersResponse`/`ProviderInfo`/`DelaySample` 与 `ProxiesViewModel`/`ProviderRowVm` 同在 `ClashAcry.Core`（Task 6 的 `MihomoModels.cs` 用的是 `namespace ClashAcry.Core`），本文件不需要 `using ClashAcry.Contracts;`。）

`Group`/`Node` 混在同一个 `Resp(...)` 数组里时，合并逻辑靠 `Type` 区分：`select`/`url-test`/`relay`/`fallback` 当组，其余当节点。`ProxyDetail.Delay`（实时值）优先于 `History` 最新样本，两者皆无才是 `超时`。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/ClashAcry.Core.Tests --filter ProxiesViewModelTests`
Expected: FAIL（类型不存在）→ 实现后 PASS，8/8（新增 Providers_AreProjectedAsRowsWithNameTimeAndCount 锁 spec §6 的 Provider 展示面）

- [ ] **Step 3: 实现**

关键约束（逐条对应测试）：
- `RefreshAsync` 合并而非替换：`Groups` 按组名查/建，`Nodes` 按 `Name` 查/建，只更新属性；**只有成员增减才 `Add`/`Remove`**。这是「不抖动」的实现点——整表重置会让 `ListView` 重挂容器，滚动条跳回顶部，用户一眼就看出"每 2 秒闪一次"。
- 延迟文本三档：实时值 → `NN ms`；无实时值但有 history → `上次 NN ms`；都没有 → `超时`。`-1` 一律视为"无实时值"（`MihomoClient.GetDelayAsync` 已把 409/超时折成 -1）。
- provider 成员详情来自 `ProvidersResponse[*].Proxies`：合并时按 `Name` 覆盖/补齐节点，`ProviderName` 一并带上；`show_hidden`（`UiPrefs`）决定是否隐藏 `hidden: true` 成员，默认隐藏。
- **provider 成员不许走 `GET /proxies/{name}/delay`**（Task 1 实测 v1.19.33 对成员名与 provider 名一律 **404** `{"message":"Resource not found"}`，spec §4.2 已把它写成既定实现要求）。成员延迟只从 `ProvidersResponse[*].Proxies[i]` 的 `history` / `extra` 健康样本取，取不到落「上次/超时」档；`measure` 委托与 `TestAllAsync` 只对非 provider 成员（`ProviderName` 为空）调用——全组测速时对 provider 成员直接跳过（它们已有 history 样本），别调了拿 404 再静默吞错。
- `ProviderRows` 在每次 `RefreshAsync` 里整体按 `Name` 排序后**就地更新**（同 `Groups` 的查/建手法：行存在就改属性，只有 provider 增减才 `Add`/`Remove`）；`UpdatedAtText` 取 `ProviderInfo.UpdatedAt`，`MemberCount` 取 `Proxies.Count`，`Proxies` 为 null 记 0。provider 列表本来不常变，整表重建等于给自己埋一次抖动。
- **`ProviderRows` 必须先按 `vehicleType` 过滤**（Task 1 实测：`GET /providers/proxies` 的 `providers` 表里混有每个策略组自动生成的 `"Compatible"` 伪 provider 和一个 `"default"`，只留 `"File"`/`"Http"`）。不过滤的话节点页底部会多出十几个假 provider 行，还能对它们发 PUT 刷新。**过滤只作用于行**：成员详情合并仍吃整张表——`Compatible` 伪 provider 里列的就是组内节点，按 `Name` 覆盖是无操作，不会污染数据。
- 选中项来源永远是 `ProxyDetail.Now`（mihomo 是事实源，spec §7）；`SelectAsync` 顺序固定为 `putProxy` → `fetch` → 设 `Highlight`（1.2s 后自动撤），中间不做乐观翻转。
- 测速并发：`SemaphoreSlim(TestConcurrency = 4)`；`IsTesting` 在拿到许可时置真、释放时置假，`DelayText` 只在有值时改。
- `INotifyPropertyChanged` 只给真正会变的属性（`DelayText`/`Selected`/`IsTesting`/`Highlight`），`Name`/`ProviderName` 设为 `init`——少一个通知就少一次重绘机会。

- [ ] **Step 4: UI 页绑定 + 不抖动的窗口侧断言**

`ProxiesPage.xaml`：`NavigationView` 内容区放一个 `ItemsView`/`ListView`（`ItemsSource="{x:Bind Vm.Groups}"`），组用 `Expander` 或层级 `IsExpanded` 绑定，成员行三列（名称、延迟、选中点）。**AutomationId 命名约定（窗口侧断言全靠它，必须逐字一致）**：

- 行标识 `Node_{Name}` 挂在**承载整行文本的那个 `TextBlock`** 上，内容形如 `日本 01 · 11 ms`——即"UIA Name 里同时有节点名和延迟值"。挂在外层容器上不成立：WinUI 里 `ListViewItem`/`StackPanel` 的 `Name` 不会自动聚合子元素文本，断言会读到空串。
- 折叠箭头 `Node_{Name}_Collapse` 挂在组行首的 `Button`（chevron）上，**行本体不设点击**（Task 15 Step 1 的 `GroupCollapse_TapsChevronNotRow` 就是锁这一条：点整行会撞上"选中节点"这个主操作）。
- 组行标识 `Group_{Name}`，同样挂在承载组名 + 当前选择的 `TextBlock` 上。

`ProxiesViewModel` 由 UI 侧组装：`fetch` 指向 `MihomoClient.GetProxiesAsync`、`putProxy` 指向 `SetProxyAsync`、`measure` 指向 `GetDelayAsync(name, _link.DelayUrl, 5000, ct)`、`refreshProvider` 指向 `RefreshProviderAsync`（内核直连地址、secret 与 `DelayTestUrl` 全部来自 `handshake`，secret 不进任何界面文本）。**滚动容器整页只放一个**，合并逻辑保住了容器，页面别再自己重建。

Provider 区放在页面底部一个 `ItemsControl`（`AutomationId="ProviderPanel"`），绑 `ProviderRows`，每行三个可断言元素：承载「`{Name} · {UpdatedAtText} · {MemberCount} 个节点`」的 `TextBlock`（`AutomationId="Provider_{Name}"`，遵守 UIA 单一读取点约定）、一个「刷新」`Button`（`AutomationId="Provider_{Name}_Refresh"`，点击调 `RefreshProviderAsync(row.Name)` 并把按钮 `IsEnabled=false` + `Content="刷新中…"` 直到 `finally` 恢复——长耗时操作要有进行中反馈）、`IsRefreshing` 期间行文字保持旧值不闪空。刷新成功后由 `RefreshAsync` 重拉 `GET /providers/proxies` 纠正 `updatedAt`，本地不猜新时间。

`tests/ClashAcry.UI.Tests/ProxiesNoJitterTests.cs`（FakeTray 手法同 Task 14；`handshake` 把 host/port 指向 WireMock，UI 的 REST 读就落在假内核上）：

```csharp
public class ProxiesNoJitterTests : IClassFixture<UiAppHarness>, IDisposable
{
    private readonly UiAppHarness _harness;
    private readonly WireMockServer _wire = WireMockServer.Start();
    public ProxiesNoJitterTests(UiAppHarness harness) => _harness = harness;
    public void Dispose() => _wire.Dispose();

    // 重注同一个匹配条件前必须先清旧映射：WireMock 默认按注册顺序命中，旧 body 会一直赢，"改值"就变成假失败。
    // 版本差异：1.x 有 ResetMappings()；若编译不过按顺序试 Reset() / RemoveMappings(...)，再不行给本类换一个新端口。
    private void StubProxies(int delay)
    {
        _wire.ResetMappings();
        _wire.Given(Request.Create().WithPath("/proxies").UsingGet()).RespondWith(Response.Create().WithBody(
            JsonSerializer.Serialize(new
            {
                proxies = new
                {
                    G = new { @name = "G", @type = "select", now = "n1", all = new[] { "n1", "n2" }, hidden = false },
                    n1 = new { @name = "n1", @type = "ss", delay },
                    n2 = new { @name = "n2", @type = "ss", delay = delay + 1 }
                }
            })));
    }

    [Fact]
    public async Task AutoRefresh_KeepsRowIdentity_AndUpdatesValueInPlace()
    {
        StubProxies(11);
        using var tray = new FakeTray();
        tray.HandshakeWith(new HandshakeReply(CoreState.Running, "127.0.0.1", _wire.Port, "sec", 7897, "v-test",
                                              $"{_wire.Url}/delay"));   // 测速地址一并进 handshake，UI 不用第二条读设置的通路
        using var app = _harness.Launch(extraArgs: null);
        var w = app.GetMainWindow(new Automation());
        Assert.True(await tray.WaitUiConnectedAsync(TimeSpan.FromSeconds(5)));
        w.FindFirstDescendant(cf => cf.ByAutomationId("NavProxies")).Click();

        var row = await WaitRow(w, "Node_n1");                                   // 第一轮：11 ms
        Assert.Contains("11 ms", row.Current.Name);
        StubProxies(99);                                                          // 下一轮自动刷新读到 99 ms
        await WaitUntil(() => w.FindFirstDescendant(cf => cf.ByAutomationId("Node_n1"))
                          ?.Current.Name.Contains("99 ms") == true, TimeSpan.FromSeconds(6));

        // 关键判据：同一 AutomationId 仍在原位（容器没被重建），且文本已就地更新
        var again = w.FindFirstDescendant(cf => cf.ByAutomationId("Node_n1"));
        Assert.False(again.Current.IsOffscreen);
        Assert.Equal(row.Current.BoundingRectangle.Width, again.Current.BoundingRectangle.Width);
    }

    private static async Task<AutomationElement> WaitRow(Window w, string id)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            var e = w.FindFirstDescendant(cf => cf.ByAutomationId(id));
            if (e is not null) return e;
            await Task.Delay(100);
        }
        throw new TimeoutException($"未找到行 {id}");
    }
}
```

（`WaitUntil` 与 `FakeTray` 是 Task 14 引入的共享夹具，放在 `tests/ClashAcry.UI.Tests/Fakes/` 下供两个测试文件共用；`using System.Text.Json;` 供序列化 stub 响应。）

- [ ] **Step 5: 跑测试 + 手动确认**

```bash
dotnet test tests/ClashAcry.Core.Tests --filter ProxiesViewModelTests
dotnet build ClashAcry.sln -c Release
dotnet test tests/ClashAcry.UI.Tests --filter ProxiesNoJitterTests
```
Expected: 逻辑 8/8、窗口 1/1。然后接真实内核跑一次全组测速，肉眼看滚动条与列表**无整表闪烁、滚动位置不跳顶**（这条是用户红线，必须人眼确认一次）。

- [ ] **Step 6: 提交**

```bash
git add src/ClashAcry.Core src/ClashAcry.UI tests/ClashAcry.Core.Tests/ProxiesViewModelTests.cs tests/ClashAcry.UI.Tests
git commit -m "feat(ui): 节点页（按名合并刷新、并发限 4 测速、上次值不冒充实时、Provider 手动刷新）"
```

---

### Task 16: 深色 chrome 像素核对

**Files:**
- Create: `src/ClashAcry.UI/Themes/FluentDarkOverrides.xaml`
- Create: `tests/ClashAcry.UI.Tests/Fakes/WindowCapture.cs`
- Modify: `src/ClashAcry.UI/App.xaml`（合并 `Themes/FluentDarkOverrides.xaml` 到 `MergedDictionaries`）
- Test: `tests/ClashAcry.UI.Tests/ChromeContrastTests.cs`

**Interfaces:**
- Consumes: Task 12–15 的全部可视控件
- Produces: `static class WindowCapture { static Bitmap Grab(IntPtr hwnd); static RECT WindowRect(IntPtr hwnd); static IntPtr Hwnd(Window w); }`（`PrintWindow` + `PW_RENDERFULLCONTENT`）、`static class ChromeProbe { static (int R, int G, int B) Sample(Bitmap bmp, Rectangle element, RECT window, double fx, double fy); }`（**五个参数**：控件矩形是屏幕坐标，必须减去窗口左上角才是位图坐标，签名里没有 `window` 就换算不了）

- [ ] **Step 1: 写取样夹具（判据是像素，不是肉眼看整体截图）**

为什么不用 PowerShell + `CopyFromScreen`：WinUI 3 窗口由 DWM 合成，屏幕抓图会被别的窗口挡住、也抓不到滚动条那几像素宽的部件；`PrintWindow` 带 `PW_RENDERFULLCONTENT(2)` 是对 WinUI 窗口唯一稳定的抓取方式。所以取样器放进 C# 测试夹具，控件矩形仍从 FlaUI 的 `BoundingRectangle` 取（它在屏幕坐标，`PrintWindow` 的位图是窗口相对坐标——用窗口左上角做减法，这一步错了整份断言就全错位）。

```csharp
public static class WindowCapture
{
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public const uint PW_RENDERFULLCONTENT = 2;

    public static Bitmap Grab(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        try { Assert.True(PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT), "PrintWindow 失败：窗口可能被最小化"); }
        finally { g.ReleaseHdc(); }
        return bmp;
    }
    public static RECT WindowRect(IntPtr hwnd) { GetWindowRect(hwnd, out var r); return r; }
    /// FlaUI 的 `Window` 是 UIA 元素，没有 `Handle` 属性；HWND 只从 `Current.NativeWindowHandle` 拿。
    public static IntPtr Hwnd(FlaUI.Core.AutomationElements.Window w) => new(w.Current.NativeWindowHandle);
}

public static class ChromeProbe
{
    /// fx/fy 是控件矩形内的相对取样点（0~1）。返回该点 3x3 邻域的最亮像素——
    /// 取"最亮"而不是"中心"：中心可能落在文字上，浅色 chrome 恰恰体现在边框/槽/滑块上。
    public static (int R, int G, int B) Sample(Bitmap bmp, Rectangle element, RECT window, double fx, double fy)
    {
        var x = (int)(element.Width * fx) + element.Left - window.Left;
        var y = (int)(element.Height * fy) + element.Top - window.Top;
        int best = -1, br = 0, bg = 0, bb = 0;
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var p = bmp.GetPixel(Math.Clamp(x + dx, 0, bmp.Width - 1), Math.Clamp(y + dy, 0, bmp.Height - 1));
            if (p.R + p.G + p.B > best) { best = p.R + p.G + p.B; (br, bg, bb) = (p.R, p.G, p.B); }
        }
        return (br, bg, bb);
    }
}
```

断言主体（每条一个部件，缺一条就是当初 sing-box 那次漏滚动条的翻版）。**判据分两个方向，不能混**：底色/槽这类应当是深色的，用「最亮像素仍暗」；箭头、描边这类承载前景的，反过来要求「最亮像素够亮」——否则一次正确的深色主题也会因为箭头是白的而被判成残留浅色，那种断言只会逼人放宽阈值。

```csharp
public class ChromeContrastTests : IClassFixture<UiAppHarness>
{
    private readonly UiAppHarness _harness;
    public ChromeContrastTests(UiAppHarness harness) => _harness = harness;

    // 深色面板色系判据：三通道都 < 90 且通道间差 < 25（偏色/灰白都不放过）
    private static void AssertDark(string what, (int R, int G, int B) px) =>
        Assert.True(px.R < 90 && px.G < 90 && px.B < 90 && Math.Max(px.R, Math.Max(px.G, px.B)) - Math.Min(px.R, Math.Min(px.G, px.B)) < 25,
            $"{what} 不是深色色系：RGB({px.R},{px.G},{px.B})");

    // 前景可见判据：3x3 邻域最亮像素三通道和 > 240（≈平均 80 以上），糊在深色底上就算不合格
    private static void AssertForegroundVisible(string what, (int R, int G, int B) px) =>
        Assert.True(px.R + px.G + px.B > 240, $"{what} 的前景在深色底上不够清晰：RGB({px.R},{px.G},{px.B})");

    // (所在页导航项, 部件 id, 取样点, 极性)。极性写进数据，避免"所有目标共用一条 AssertDark"这种一视同仁的假核对。
    // ScrollBarThumb 不进这张表：滑块本来就只比槽亮一点，绝对阈值无论定多少都会被"调亮滑块"或"调暗槽"绕过，
    // 改用下面的相对判据（thumb 与 track 的亮度差）。
    public static IEnumerable<object[]> Targets() => new[]
    {
        new object[] { "NavOverview", "PageBackground",    0.85, 0.1,  "dark" },
        new object[] { "NavOverview", "ScrollBarTrack",     0.5, 0.5,  "dark" },
        new object[] { "NavOverview", "LogPanelBackground", 0.5, 0.5,  "dark" },
        new object[] { "NavOverview", "ModeRadioBorder",    0.06, 0.5, "foreground" },
        new object[] { "NavSettings", "ComboArrow",         0.92, 0.5, "foreground" },
    };

    [Theory]
    [MemberData(nameof(Targets))]
    public void DarkChrome_PerPartContrast(string nav, string id, double fx, double fy, string polarity)
    {
        using var app = _harness.Launch(extraArgs: "--force-dark");            // 固定深色，不跟随系统
        var w = app.GetMainWindow(new Automation());
        w.FindFirstDescendant(cf => cf.ByAutomationId(nav)).Click();
        var hwnd = WindowCapture.Hwnd(w);
        var rect = WindowCapture.WindowRect(hwnd);
        using var bmp = WindowCapture.Grab(hwnd);

        var part = id == "LogPanelBackground" ? OpenLogFlyout(w) : w.FindFirstDescendant(cf => cf.ByAutomationId(id));
        Assert.NotNull(part);                                    // 部件必须先带 AutomationId：找不到控件不算通过
        var px = ChromeProbe.Sample(bmp, part.BoundingRectangle, rect, fx, fy);

        if (polarity == "dark") AssertDark(id, px);
        else AssertForegroundVisible(id, px);
        Console.WriteLine($"{id}: RGB({px.R},{px.G},{px.B})");    // 数值进 checklist，Step 3 收
    }

    [Fact]
    public void ScrollBarThumb_IsDistinguishableFromTrack()
    {
        using var app = _harness.Launch(extraArgs: "--force-dark");
        var w = app.GetMainWindow(new Automation());
        w.FindFirstDescendant(cf => cf.ByAutomationId("NavProxies")).Click();   // 节点列表才是必然溢出的那一页
        var hwnd = WindowCapture.Hwnd(w);
        var rect = WindowCapture.WindowRect(hwnd);
        using var bmp = WindowCapture.Grab(hwnd);

        var track = ChromeProbe.Sample(bmp, w.FindFirstDescendant(cf => cf.ByAutomationId("ScrollBarTrack")).BoundingRectangle, rect, 0.5, 0.5);
        var thumb = ChromeProbe.Sample(bmp, w.FindFirstDescendant(cf => cf.ByAutomationId("ScrollBarThumb")).BoundingRectangle, rect, 0.5, 0.5);
        int Sum((int R, int G, int B) p) => p.R + p.G + p.B;
        Assert.True(Math.Abs(Sum(thumb) - Sum(track)) > 40,
            $"滑块与槽看不出差别（track={Sum(track)} thumb={Sum(thumb)}）——深色下滚动条等于不可用");
    }

    // 日志区在 Flyout 里，取样前必须真的展开它（不展开就找不到，等于这条核对没跑）
    private static FlaUI.Core.AutomationElements.AutomationElement OpenLogFlyout(Window w)
    {
        w.FindFirstDescendant(cf => cf.ByAutomationId("LogButton")).Click();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var el = w.FindFirstDescendant(cf => cf.ByAutomationId("LogPanelBackground"));
            if (el is not null) return el;
            Thread.Sleep(100);
        }
        throw new TimeoutException("日志 Flyout 未展开，LogPanelBackground 取不到");
    }
}
```

（`targets` 里每个 id 都要求实现方在对应部件模板上补 `AutomationProperties.AutomationId`：`ScrollBarTrack`/`ScrollBarThumb` 挂在 `ScrollBar` 的 `Track`/`Thumb` 模板部件上，`ComboArrow` 挂在 `ComboBox` 箭头 `Border` 上，`ModeRadioBorder` 挂在 `RadioButton` 的外圈 `Ellipse` 上，`PageBackground` 挂在 `OverviewPage` 的根 `Grid`（`AutomationId="PageBackground"`）上，`LogPanelBackground` 挂在日志 Flyout 的面板 `Border` 上。**找不到控件＝不通过**，不允许 silently skip。）

（`BoundingRectangle` 是 FlaUI 直接给出的 `System.Drawing.Rectangle`，屏幕坐标；`rect` 是窗口矩形，`Sample` 内部做减法换算到位图坐标。夹具侧要 `using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices; using System.Threading; using System.Linq; using FlaUI.Core; using FlaUI.Core.Automation; using FlaUI.Core.AutomationElements; using Xunit;`。测试项目要 `<ItemGroup>` 加 `System.Drawing.Common`（包版本随 xUnit 传递依赖即可，Windows-only），这只进测试夹具，**app 侧仍然禁 `System.Drawing`**（spec §3 的 Tray 40 MB 线）。）

- [ ] **Step 2: 逐类补齐模板覆盖**

对 `Button`、`ComboBox`、`ComboBoxItem`、`ListBoxItem`/`ItemsView` 行、`ScrollBar`（含 `Track.Thumb` 与翻页 `RepeatButton`）各自提供 `ControlTemplate`，颜色全部取自 `FluentDarkOverrides.xaml` 的 `SolidColorBrush` 资源（`ChromeBackgroundBrush`、`ChromeBorderBrush`、`ThumbBrush`、`SubtleHoverBrush`），并给每个被取样的部件补上上面用到的 `AutomationProperties.AutomationId`。只设 `Background` 改不动默认模板；把 `VerticalScrollBarVisibility` 从 `Visible` 改成 `Auto` **不算修复**（内容必然溢出时只是把"永远白"变成"需要时白"）。

- [ ] **Step 3: 跑取样断言，记录前后数值**

```bash
dotnet build ClashAcry.sln -c Release
dotnet test tests/ClashAcry.UI.Tests --filter ChromeContrastTests
```
Expected: 5 条极性分支 + 1 条滑块/槽对比 = **6/6 全 PASS**。把每条的实际 RGB 数值贴进 `docs/superpowers/checklists/v1-acceptance.md` 的「深色 chrome」小节（数值来自断言消息，PASS 时在取样后加一行 `Console.WriteLine($"{id}: {px}")` 并用 `--logger "console;verbosity=detailed"` 收出来），这样下次改 UI 不用重跑也能看出改了什么。

`--force-dark` 是 UI 的测试启动参数（Task 13 落地）：检测到它就把 `RootPage.RequestedTheme` 钉在 `Dark`，不受系统主题影响。**不许为了让断言好看而让 app 忽略用户主题设置**——它只在带该参数时生效。

- [ ] **Step 4: 提交**

```bash
git add src/ClashAcry.UI/Themes src/ClashAcry.UI/App.xaml tests/ClashAcry.UI.Tests docs/superpowers/checklists
git commit -m "feat(ui): 深色 chrome 逐类控件模板覆盖 + PrintWindow 像素取样断言"
```

---

### Task 17: 发布产物与内置内核

**Files:**
- Create: `scripts/publish.ps1` `scripts/fetch-bundled-core.ps1`
- Modify: `.gitignore`（`packaging/core/` 不入库）

**Interfaces:**
- Consumes: 全部前置任务
- Produces: `artifacts/publish/ClashAcry-v<ver>-win-x64.zip`，内含 `ClashAcry.UI.exe`、`ClashAcry.Tray.exe`、`core\mihomo.exe`、`settings.json`（首启生成，不打进包）

- [ ] **Step 1: 写发布脚本**

```powershell
$ErrorActionPreference = 'stop'
$ver = '0.1.0'
dotnet publish src/ClashAcry.UI   -c Release -r win-x64 --self-contained -o artifacts\pub\ui
dotnet publish src/ClashAcry.Tray -c Release -r win-x64 --self-contained -o artifacts\pub\tray
$out = "artifacts\publish\ClashAcry-v$ver-win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }        # 上次残留文件会被 Compress-Archive 静默带上
New-Item -ItemType Directory -Force -Path "$out\core" | Out-Null
Copy-Item artifacts\pub\tray\* $out -Recurse -Force
Copy-Item artifacts\pub\ui\*  $out -Recurse -Force   # UI 的 WindowsAppSDK 自包含文件与 Tray 同目录共存
Copy-Item packaging\core\mihomo-windows-amd64-compatible.exe "$out\core\mihomo.exe" -Force
Compress-Archive -Path "$out\*" -DestinationPath "$out.zip" -Force
Write-Output "产物：$out.zip"
```

若 UI 与 Tray 文件互相覆盖导致启动失败，改为**分目录** `ui\` 与 `tray\`，并在 Task 12 `TrayLink` 的路径推导里把候选路径改成 `..\tray\ClashAcry.Tray.exe`（这是本任务唯一允许回头改前序任务的地方，改完要在 spec §3 记一行进程定位方式）。

- [ ] **Step 2: 内置内核走"本机准备"，不入 git**

`.gitattributes` 的 LFS 路线不采用（远端未开 LFS 会直接报错，配额也不值得赌）。`scripts/fetch-bundled-core.ps1` 从 GitHub `MetaCubeX/mihomo/releases/latest` 取 `mihomo-windows-amd64-compatible-<tag>.zip`、解出包内 exe 落成 `packaging/core/mihomo-windows-amd64-compatible.exe`，并把 tag 写进 `packaging/core/BUNDLED.txt`（记录内置版本，spec §6「内置带一个」的可追溯来源）；`packaging/core/` 整目录加进 `.gitignore`。脚本要幂等：目标存在且 `BUNDLED.txt` 与最新 tag 一致就跳过网络。

Task 1 评审提出的溯源要求（本任务必须补上，`packaging/core/` 不入库意味着**产物里的 exe 在 git 里没有任何对应物**）：`BUNDLED.txt` 不止写 tag，写三行——

```
tag: v1.19.33
asset: https://github.com/MetaCubeX/mihomo/releases/download/v1.19.33/mihomo-windows-amd64-compatible-v1.19.33.zip
sha256: <下载 zip 的 SHA-256>
```

`sha256` 由脚本用 `Get-FileHash -Algorithm SHA256` 对**下载的 zip 原文**计算后写入（不是对解出来的 exe；zip 才是 release 页面上的资产）。发布前跑一次 `scripts/publish.ps1` 之前，先按这三行校验本地 `packaging/core` 的来源；校验不过就重新下载。这样任何人重建产物包时能证明里面那颗 `core\mihomo.exe` 来自哪个 release，而不是"某台机器上恰好有个 exe"。

- [ ] **Step 3: 验证产物**

```bash
powershell -File scripts/fetch-bundled-core.ps1
powershell -File scripts/publish.ps1
powershell -Command "Expand-Archive artifacts/publish/ClashAcry-v0.1.0-win-x64.zip -DestinationPath artifacts/unzipped -Force"
powershell -Command "Start-Process artifacts/unzipped/ClashAcry.Tray.exe -ArgumentList '--no-tray' -PassThru | Out-Null"
powershell -File scripts/measure-residency.ps1 -ProcessName ClashAcry.Tray -Json
```
Expected: 在未装 Windows App Runtime 的干净目录能双击起 UI（本机验证一次即可，`WindowsAppSDKSelfContained=true` 已在 Task 2 的 csproj 里落地）；Tray 常驻数值写进 spec §10 的「实测（发布产物）」列。

- [ ] **Step 4: 提交**

```bash
git add scripts .gitignore docs/superpowers/specs
git commit -m "build: 自包含发布脚本 + 内置内核本机准备（packaging/core 不入库）"
```

---

### Task 18: 验收清单执行与合并

**Files:**
- Create: `docs/superpowers/checklists/v1-acceptance.md`
- Modify: `docs/superpowers/specs/...-design.md`（把实测结论固化进 §4.2/§10/§12）

- [ ] **Step 1: 写清单（照这份落盘，每条给可复制命令与期望输出）**

`docs/superpowers/checklists/v1-acceptance.md` 正文：

```markdown
# ClashAcry v1 验收清单

执行顺序即编号。带「人眼」标记的只能人工确认，其余全部脚本自测。

## 1. 自动化全绿
    dotnet build ClashAcry.sln -c Release          # 期望：0 warning 0 error
    dotnet test  tests/ClashAcry.Core.Tests -c Release
    dotnet test  tests/ClashAcry.UI.Tests  -c Release
期望：Core 侧全绿（≥45 条）、UI 侧全绿（Shell / Backdrop / Overview / ProxiesNoJitter / ChromeContrast 五组，其中 ChromeContrast 是 5 条极性分支 + 1 条滑块对比）。

## 2. 内置内核就位
    powershell -File scripts/fetch-bundled-core.ps1
    cat packaging/core/BUNDLED.txt
期望：打印一个 release tag（如 `v1.19.33`），`packaging/core/mihomo-windows-amd64-compatible.exe` 存在且 > 10 MB。

## 3. 起 Tray + 导入订阅（URL 与本地两条通路都要过）
    artifacts\unzipped\ClashAcry.Tray.exe --no-tray     # 后台起宿主
    start artifacts\unzipped\ClashAcry.UI.exe
操作 A：设置页「导入订阅」→ 贴一条 Clash yaml 链接 → 期望行内出现「已导入：…」，无弹窗。
操作 B：设置页「从本地 yaml 导入」→ 选一个本地 yaml 文件 → 期望同样是行内「已导入：…」。
期望：`%LOCALAPPDATA%\ClashAcry\runtime\config.yaml` 出现且含 `external-controller`、`secret`；
`%LOCALAPPDATA%\ClashAcry\imports\` 里留有原件；**用户贴/选的那个原始 yaml 文件本身未被改写**（用文件属性时间戳核一次）。
操作 B 是唯一只能人工确认的导入路径：`FileOpenPicker` 是系统对话框，不在 FlaUI 覆盖范围内（原因见 Task 14 Step 3）。

## 4. 起内核 + 节点闭环
操作：概览页内核开关拨到「开」→ 等状态「运行中」→ 节点页选一个节点 → 点「测速」。
期望：状态标签从「启动中…」到「运行中」（双探活都过才算运行，spec §9）；
选中项在 2 秒内自动回显为刚点的节点；延迟显示 `NN ms`，失败的显示「超时」而不是空白。

## 5. 系统代理真的生效
    curl -x http://127.0.0.1:7897 -s -o NUL -w "%%{http_code}\n" http://www.gstatic.com/generate_204
期望：`204`。关系统代理后再跑一次，期望仍 `204`（直连也能拿到），此时以
    powershell -Command "Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' | Select ProxyEnable, ProxyServer"
判据：开=1 且 ProxyServer=`127.0.0.1:7897`；关=0。

## 6. 关窗即退 + 常驻开销（人眼 + 脚本）
操作：关掉 UI 主窗口（不点托盘退出）。
    Get-Process ClashAcry.UI -ErrorAction SilentlyContinue      # 期望：无输出（进程已退）
    powershell -File scripts/measure-residency.ps1 -Json
期望：Tray ≤ 40 MB；Tray+内核 ≤ 80 MB。数值回写 spec §10 实测列。

## 7. 托盘「打开界面」重连回显
操作：托盘菜单 → 打开界面。
期望：UI 重新拉起并直接显示「运行中」（不是「未运行」再跳一次）——即 handshake 回显成功。

## 8. 内核在线更新有进度
操作：概览页点「更新内核」。
期望：按钮立刻禁用并显示「下载更新中 NN%」，百分比单调上升，**全程无弹窗**；
完成后 `type %LOCALAPPDATA%\ClashAcry\core\version.txt` 显示新 tag，且内核仍在运行（更新走了 SwapExecutable 的自动重启）。

## 9. backdrop 降级要明说（人眼）
操作：系统「设置 → 个性化 → 颜色 → 透明效果」关闭，重开 UI。
期望：窗口不透明，且设置页标注为「当前实际生效：纯色（系统透明已关闭）」。

## 10. 轮询不得可见抖动（人眼）
操作：节点页停留 30 秒，滚动到中部。
期望：列表不整表闪烁、滚动位置不跳顶、延迟数字就地变化。

## 11. 退出清理
操作：托盘 → 退出。
    powershell -Command "(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings').ProxyEnable"
    Get-Process mihomo* -ErrorAction SilentlyContinue
期望：`ProxyEnable` 为 0；无 mihomo 残留进程。
```

- [ ] **Step 2: 跑全量自动化**

```bash
dotnet build ClashAcry.sln -c Release
dotnet test tests/ClashAcry.Core.Tests -c Release
dotnet test tests/ClashAcry.UI.Tests -c Release
```
Expected: 三条命令全绿，构建无警告。

- [ ] **Step 3: 人工冒烟（用户参与点）**

需要人眼确认的只有五处：整窗亚克力的观感、滚动条/下拉的深色 chrome、列表刷新的不抖动、更新进度反馈的连续性、以及「从本地 yaml 导入」的系统文件对话框（`FileOpenPicker` 属另一进程，FlaUI 不覆盖，见 Task 14 Step 3）。其余全部脚本自测。

- [ ] **Step 4: 回写 spec 并合并**

把 §4.2 端点表、§10 实测列、§12 已消解的风险条目全部定稿。

```bash
git add docs && git commit -m "docs: v1 验收结论与实测数据回写 spec"
git push origin feat/clashacry-v1
gh pr create --base main --head feat/clashacry-v1 --title "ClashAcry v1：双进程 mihomo 客户端（WinUI 3 + Acrylic）" --body "$(cat <<'EOF'
## Summary
- Tray 宿主托管内核生命周期，UI 关窗即退；整窗 Desktop Acrylic 默认，可切 Mica/纯色
- 核心闭环：启停、订阅导入、节点切换与测速、系统代理、内核在线更新
## Test plan
- [ ] dotnet test（Core + UI）全绿
- [ ] v1-acceptance.md 逐条勾选
- [ ] measure-residency.ps1 达标（Tray ≤40MB / 合计 ≤80MB）
EOF
)"
```
合并前把 PR 链接报给用户，**由用户点合并**（破坏性/共享状态操作需确认）。

---

## 依赖顺序

```
1 探针 ─┬─ 2 脚手架 ─ 3 Contracts ─ 4 Settings ─ 5 Injector ─┐
        │                                  ├─ 6 MihomoClient ─┤
        │                                  ├─ 7 SystemProxy ──┼─ 8 CoreManager ─ 9 Pipe ─ 10 TrayHost ─ 11 Updater
        └──────────────────────────────────────────────────────┘
12 UI 外壳 ─ 13 Backdrop ─ 14 Overview ─ 15 Proxies ─ 16 Chrome ─ 17 发布 ─ 18 验收
```

Task 6/7/11 与 Task 12/13 可并行推进（分属两个进程），其余串行。
