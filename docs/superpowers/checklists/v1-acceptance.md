# ClashAcry v1 验收清单

执行顺序即编号。带**【人眼】**的只能人工确认，其余给可复制命令与期望输出。
标了「本会话已自测」的条目是 2026-10-27 由 agent 跑过的，数值来自 spec §10 的 Task 17 表；其余等人工过一遍。

环境前提（AGENTS.md）：本机 .NET 10 SDK 在 `%LOCALAPPDATA%\Microsoft\dotnet`，Git Bash 里先
`export PATH=/c/Users/Keno/AppData/Local/Microsoft/dotnet:$PATH`，否则 `dotnet` 会报 "No .NET SDKs were found"。

---

## 0. 先备份系统代理三值【动手，必须第一个做】

本项目会往 HKCU 写 `ProxyEnable`/`ProxyServer`/`ProxyOverride`。`SystemProxy.Disable()` 只翻开关、
不抹 `ProxyServer`（裁定 R-7c），但 **`Enable()` 会覆盖它**——机器上如果留着别家客户端写过的
`ProxyServer`（本机现状是 `127.0.0.1:8090`、`ProxyEnable=0`），过完第 5 条就回不去了。先把三值抄下来：

```powershell
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' |
    Select ProxyEnable, ProxyServer, ProxyOverride | Format-List
```

跑完清单（第 11 条）按抄下的值原样写回。**注意**：全程不要碰 `verge-mihomo` / `clash-verge` 那套
（AGENTS.md 红线：指向其他软件的代理绝不触碰）；本项目只用自己的 9097/7897。

## 1. 自动化全绿【本会话已自测】

```bash
dotnet build ClashAcry.sln -c Release              # 期望：0 警告 0 错误
dotnet test  tests/ClashAcry.Core.Tests -c Release # 期望：154/154
dotnet test  tests/ClashAcry.UI.Tests  -c Release  # 期望：28/28
```

UI 侧五组：Shell 5 / Backdrop 4 / Overview 8 / ProxiesNoJitter 5 / ChromeContrast 6（后者每类控件的
深色底与浅色前景**极性分开判**，加一条滚动条轨道-滑块对比、一条浅色反证）。四条跑测试的坑：

- UI.Tests 必须**不带 `--no-build`** 跑（测试解析 `bin\Release\…`，而解决方案构建写的是 `bin\x64\Release\…`，见 R-12e/R-14f）。
- **改过 UI/Tray 源码后必须先 `dotnet build ClashAcry.sln -c Release` 再跑 UI.Tests**：`dotnet test tests/ClashAcry.UI.Tests`
  只重建测试工程（它按 R-12f 不引用 UI 工程），harness 起的是解决方案构建出来的那个 exe。
  少了这一步就是「改了没生效、测试还全绿」——评审修复轮里两条新回归锁最初就是这样被验成空锁的。
- Core.Tests 用的管名就是生产管名 `clashacry.tray.v1`：跑套件前先确认没有 `ClashAcry.Tray.exe` 存活，
  否则 `PipeRoundTripTests` 9 条成片失败（R-17 交接与 spec §9 末）。**UI.Tests 现在把这条变成硬断言**
  （`UiAppHarness.AssertNoLiveTray`：起窗前查到活的 `ClashAcry.Tray` 就当场失败，不替你杀进程）——
  假 Tray 与真宿主抢同一个管名时，UI 连上真那个，`Press(CoreToggle)` / 更新 / 导入就会打到你的真内核上。
- 两条 `DefaultLaunch_*` / `Start_*` 用例钉住内核启动参数目录，**别把 `-d` 改回 Root**（见第 4 条）。

## 2. 内置内核就位【本会话已自测】

```bash
powershell -File scripts/fetch-bundled-core.ps1          # 期望：幂等，打印「内核就位」
powershell -File scripts/fetch-bundled-core.ps1 -Verify  # 期望：按 BUNDLED.txt 的 sha256 校验通过
```

期望：`packaging/core/BUNDLED.txt` 四行——`tag` / `asset`（release 页的 zip URL）/ `sha256`（**zip 原文**，
因为 zip 才是 release 资产）/ `exe_sha256`（打包进产物的那颗 exe）。二进制（exe 与 zip）不入库，
但 **`BUNDLED.txt` 必须入库**：`.gitignore` 用的是 `packaging/core/*` + `!packaging/core/BUNDLED.txt`，
写成整目录 `packaging/core/` 会把它一起挡掉（合并前评审抓到的：那样「产物里的 exe 从哪来」在干净克隆上无从证明）。
`publish.ps1` 在打包前强制跑一次 `-Verify`。核一眼它真的在库里：

```bash
git ls-files packaging/core          # 期望输出 packaging/core/BUNDLED.txt
```

## 3. 起 Tray + 导入订阅（URL 与本地两条通路都要过）

```bash
artifacts\unzipped\ClashAcry.Tray.exe --no-tray     # 后台起宿主（先 kill 掉任何在跑的 Tray）
start artifacts\unzipped\ClashAcry.UI.exe          # UI 不在时会自己拉起同目录的 Tray
```

- 操作 A【可脚本半程】：设置页「导入订阅」贴一条 Clash yaml 链接 → 期望行内出现「已导入：…」，**无弹窗**。
- 操作 B【人眼】：设置页「从本地 yaml 导入」选一个本地 yaml → 期望同样是行内「已导入：…」。
  `FileOpenPicker` 是系统对话框，不在 FlaUI 覆盖范围内（Task 14 Step 3），所以这条只能人工。

期望落盘：`%LOCALAPPDATA%\ClashAcry\runtime\config.yaml` 出现且含 `external-controller: 127.0.0.1:9097`、
`secret`、`allow-lan: false`、`unified-delay: true`；`%LOCALAPPDATA%\ClashAcry\imports\` 留有原件；
**你贴/选的那个原始 yaml 文件本身未被改写**（用文件修改时间核一次）。订阅源如果本来就有 `dns`/`geodata-mode`
等键，产物里必须原样透传（spec §7 注入最小化）。

## 4. 起内核 + 节点闭环【人眼；管道那半程本会话已自测】

操作：概览页内核开关拨到「开」→ 等状态到「运行中」→ 节点页选一个节点 → 点「测速」。

期望：状态从「启动中…」到「运行中」（**双探活都过才算运行**，spec §9）；选中项 2 s 内自动回显为刚点的节点；
延迟显示 `NN ms`，失败显示「超时」而不是空白。旁证两条：

```bash
netstat -ano | grep -E "9097|7897" | grep LISTENING    # 期望两条都在
curl -s -m 3 http://127.0.0.1:9097/version            # 期望 401（说明 secret 生效），带 -H 后 200
```

> 本会话实测（v1.19.33，测试用本地 yaml，节点是 3 个 `type: direct`）：`core.start` 之后
> `RESTful API listening at: 127.0.0.1:9097` + `Mixed(http+socks) proxy listening at: 127.0.0.1:7897`，
> 握手回 `state:"Running"`，`core.stop` 后 mihomo 从进程表消失。
> **如果这里看到的是 `Crashed` 而内核日志一行错误都没有**：先查 `mihomo -d` 指的是不是 `runtime` 目录
> ——指错成数据根目录时内核找不到 `config.yaml`，会按内置默认配置起来（监听 7890、连 controller 都没开），
> 双探活于是全 fail。这是 Task 17 真机跑出来的第一个缺陷，回归锁 `DefaultLaunch_PointsAtTheDirThatActuallyHoldsConfigYaml`。

## 5. 系统代理真的生效【人眼 + 脚本】

概览页把「系统代理」拨到开，然后：

```bash
curl -x http://127.0.0.1:7897 -s -o NUL -w "%{http_code}\n" http://www.gstatic.com/generate_204
powershell -Command "Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' | Select ProxyEnable, ProxyServer, ProxyOverride"
```

期望：`204`；注册表判据 开=`1` 且 `ProxyServer=127.0.0.1:7897`、`ProxyOverride` 含 `localhost,127.0.0.1,<local>`。
关掉后再跑一次 curl 期望仍 `204`（直连也拿得到），此时判据只看 `ProxyEnable=0`。
**别忘了回到第 0 条把 `ProxyServer`/`ProxyOverride` 原样写回。**

## 6. 关窗即退 + 常驻开销（两格口径）【人眼 + 脚本；数值本会话已自测】

操作：关掉 UI 主窗口（**不点托盘退出**）。

```bash
powershell -File scripts/measure-residency.ps1 -ProcessName ClashAcry.Tray -Json
powershell -File scripts/measure-residency.ps1 -ProcessName mihomo -Json
```

期望：`Get-Process ClashAcry.UI` 无输出（关窗即退）；
- **Tray 空闲态**（内核已停或没起）WS ≤ **40 MB** —— 本会话实测 空闲 **31.4** / 连开 5 次管道会话后 **37.4~37.8**；
- **满载常驻**（Tray + mihomo 在跑，UI 已关）合计 ≤ **80 MB** —— 本会话 3 分钟 soak 实测 **74.0~77.5**（mihomo 自身 WS 25~26 MB）。

口径是 Task 17 裁定的「按空闲/满载拆两格」（spec §10 开头的判据表）：40 只管空闲态，
满载那格由合计线管。这一条**不是把 40 放宽**——内核在跑时 Tray 读 49~51 MB 是实测事实，写在这里备查。
UI 自己那格（≤220）在节点页 30 行全落地 + 刚跑完全组测速的最坏档实测 **180.0 MB**（spec §10 Task 17 表）。

## 7. 托盘「打开界面」重连回显【人眼】

操作：托盘右键 → 打开界面。
期望：UI 重新拉起并**直接**显示「运行中」（不是「未运行」再跳一次）——即 `handshake` 回显成功。

## 8. 内核在线更新有进度【人眼 + 脚本】

操作：概览页点「更新内核」（当前版本已是最新时会直接回「已是最新」，要验证进度得先在设置里或 `version.txt` 留一个旧 tag）。
期望：按钮立刻禁用并显示「下载更新中 NN%」，百分比单调上升，**全程无弹窗**；100% 后按钮**仍不解锁**，
等结果帧才收回（裁定 R-14c 的同一口径）。完成后：

```bash
type %LOCALAPPDATA%\ClashAcry\core\version.txt
```

期望：显示新 tag，且内核仍在运行（更新走 `SwapExecutableAsync` 的自动重启；换之前没在跑的话**不会**被换核拉起来）。

## 9. backdrop 降级要明说【人眼】

操作：系统「设置 → 个性化 → 颜色 → 透明效果」关闭，重开 UI。
期望：窗口不透明，且设置页标注当前实际生效材质为「纯色（系统透明已关闭）」。
> 这条另有自动化覆盖：`--force-degrade` 注入 + BackdropTests 断言标注文案，所以**不需要**真去改用户的
> 个性化设置来验证逻辑分支；这里的人眼项只确认「真实关透明时视觉上确实不透明」。
> 远程桌面（RDP）下同样应落纯色并同样标注。

## 10. 轮询不得可见抖动【人眼】

操作：节点页停留 30 秒，滚动到中部。
期望：列表不整表闪烁、滚动位置不跳顶、延迟数字就地变化。
> 元素身份那半程已由 `ProxiesNoJitterTests` 用 UIA `RuntimeId` 钉住（轮询前后同一 RuntimeId ⇒ 容器没被重建）；
> 人眼项确认的是滚动位置与视觉闪烁。顺带核对：离开节点页后**不该**再有 2 s 轮询（`Unloaded` 停表，Task 15 交接③）。

## 11. 深色 chrome 在真实数据下再确认一次【人眼】

操作：真内核 + 真订阅（几十上百个节点）下，把 概览 / 节点 / 设置 三页与 ComboBox 弹层、滚动条各看一遍。
期望：下拉项、滚动条滑块、节点行在深色里都**分得开**，不糊成一片。
> 像素判据已经固化成 `ChromeContrastTests` 的三条阈值（`DarkMax=110` / `ForegroundMin=140` / `SpanMin=60`），
> 但**量法在 Task 18 换过**：底色取区域第 25 百分位（实测深色档 48~70、浅色档 240 上下），
> 亮特征取同一区域的波段最大值加与底色的跨度（实测 204~255、跨度 140~200）——阈值一条没放宽，
> 换形态的原因是单点取样不稳（带 `Header` 的 `TextBox` 里占位符字脊 238 与字段底 35 只隔 5 px，
> 而取样点取的是 3×3 邻域最亮那颗；详见计划 R-18a）。滚动条那条另有一处实测口径：
> auto-hide 条在**没悬停**时只有 3px 药丸、UIA 里没有轨道也没有翻页件，所以核对是在"程序化滚动 + 指针压在条上 + 滚轮"
> 的展开态上做的，滚轮发送前先用 `WindowFromPoint` + 属主 PID 验明落点是本测试进程（计划 R-18b）。
> 反证 `FillCriterion_LightThemeReallyReadsLight` 会把主题真的切到浅色再量一次（收尾切回跟随系统）。
> 本条是 R-16 交接项①——那轮取样用的是假内核与 30 个桩节点，真实数据下的肉眼确认还没做过。
> 另外本会话发现并修掉了一条 DPI 缺陷（UI 缺 `app.manifest`，125% 缩放上窗口按 unaware 跑、XAML 只画左上角），
> 顺带确认：**高分缩放下文字不发虚**。

## 12. 退出清理【人眼 + 脚本】

操作：托盘右键 → 退出。

```bash
powershell -Command "(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings').ProxyEnable"
powershell -Command "Get-Process mihomo -ErrorAction SilentlyContinue"
```

期望：`ProxyEnable` 为 0；无 `mihomo` 残留进程（`DisposeAsync_KillsCoreAndLeavesStopped` 已在测试侧钉过真 PID 消失）。
最后按第 0 条把 `ProxyServer` / `ProxyOverride` 还原成你抄下的原值。

---

## v1 验收线（spec §11）

- 第 1 条全绿（Core 154 / UI 28）+ 第 2~12 条勾完 + §10 三格开销线达标。
- 已知遗留，**不阻塞 v1**，记在 spec §12 / 计划交接项里：
  ① 概览页横幅缺「一键拉起/重启 Tray」按钮（spec §12）；
  ② `PipeServer` 对同一连接串行处理，分钟级 `core.update` 会堵住后面的请求（头阻塞，进度走广播不受影响）；
  ③ `ProviderInfo.Extra` 里的健康样本字段未建模；
  ④ Tray 满载 49~51 MB 的实现级压缩空间（tooltip 泵每 2 s 读盘 + JSON、`StderrTail` 双拷贝、两条重定向流），
    按 Task 17 归因是个位数 MB 收益，不足以回到 40，所以走的是拆两格那条路。
