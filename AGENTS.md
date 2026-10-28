# ClashAcry 项目约定（AGENTS.md）

设计规格：`docs/superpowers/specs/2026-10-26-clashacry-v1-design.md`（下称 spec）。

## 流程规则（用户指定，2026-10-26）

1. **有进度就提交并推送**：每完成一个可交付单元，`git commit` 后直接 `git push`，无需逐次征求同意。
   远端：https://github.com/keno80/ClashAcry　主干：`main`
2. **新功能一律新开分支**：分支名 `feat/<范围>`；**完成且验证通过后**才合并回 `main`。
   当前开发分支：`feat/clashacry-v1`
3. **维护计划与进度**：每完成一个任务，勾选 `docs/superpowers/plans/` 下对应计划的步骤复选框；
   计划外的重要决策或实测结论（尤其 spec §4.2 端点表与 §10 开销数字）**回写 spec**，随代码一起提交。
4. 破坏性 git 操作（force push、改写历史、删分支/文件）仍需事先确认。

## 构建 / 测试

```
dotnet build ClashAcry.sln -c Release
dotnet test  tests/ClashAcry.Core.Tests          # xUnit，全部打在 Core 库上，不需要窗口
dotnet test  tests/ClashAcry.UI.Tests            # FlaUI 只读断言，需真窗口（spec §11）
dotnet publish src/ClashAcry.UI   -c Release -r win-x64 --self-contained  # spec §2 部署形态
dotnet publish src/ClashAcry.Tray -c Release -r win-x64 --self-contained
powershell -File scripts/measure-residency.ps1   # spec §10 开销判据
```

UI 冒烟与验收清单：`docs/superpowers/checklists/v1-acceptance.md`（随任务落地创建）。

## 硬约束（违反即返工）

- **内核生命周期归 Tray**：UI 进程绝不 spawn/kill mihomo，启停重载一律走命名管道（spec §3 三条禁令）。
- **UI 与 Tray 同生死**：够不着托盘且宽限（连续 3 轮轮询 ≈ 6 s）用尽 ⇒ 界面自己关窗退出，横幅里**不放**任何"拉起/重启托盘"按钮（spec §3 裁定 R-22）。内核崩溃不算断连：那时托盘还在，界面必须活着把原因说完。
- **两棵真相树**：UI 不写 `settings.json`（经 `settings.update`）；引擎设置由 Tray 原子写；界面偏好走 `LocalSettings`。
- **mihomo 是唯一事实源**：不镜像节点状态，写操作后全量重拉；注入只覆盖 §4.3 列出的必需键，其余 yaml 原样透传。
- **API 契约先实测再写代码**：端点方法名/请求体/响应形状必须对真机核实并回写 spec §4.2，禁止凭记忆写端点。
- **常驻开销**：Tray 保持零 XAML、不用 WinForms 托盘；每次改动 UI 后复测 `measure-residency.ps1`（判据见 spec §10）。
- **轮询/自动刷新不得产生可见抖动**：列表 key 用节点名、数值就地更新。
- **长耗时操作必须有进行中反馈**：下载/更新内核走进度百分比事件推给 UI，禁止阻塞式弹窗。
- **深色 chrome 逐类核对**：`Button`/`ComboBox`/`ComboBoxItem`/`ListBoxItem`/`ScrollBar`（含 `Track.Thumb` 与翻页 `RepeatButton`）每类都要验，判据用像素取样，不靠肉眼看图。
- **backdrop 降级要明说**：系统关透明/RDP/`IsSupported()` 为假时自动落纯色，并在设置里标注当前实际生效材质。

## 环境

Windows x64 / Windows 11；.NET 10 SDK；Windows App SDK 2.5.1；Visual Studio Build Tools 或 `dotnet` CLI；Git Bash。

**SDK 调用方式（本机实测，2026-10-26）**：`C:\Program Files\dotnet` 里只有 6/8 **运行时**、无 SDK；.NET 10 SDK 装在
`C:\Users\Keno\AppData\Local\Microsoft\dotnet`（官方 `dotnet-install.ps1`，用户目录、未改系统 PATH）。
PATH 上那个 `dotnet` 会报 "No .NET SDKs were found"。所有命令走全路径或先把该目录前置到 PATH：

```
export PATH=/c/Users/Keno/AppData/Local/Microsoft/dotnet:$PATH
```
