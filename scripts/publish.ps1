<#
自包含发布产物（Task 17）：UI + Tray 同目录 + 内置内核，压成一个 zip。

    powershell -File scripts/publish.ps1                     # 常规发布
    powershell -File scripts/publish.ps1 -Trim -ReadyToRun   # 瘦身实验（判据见 spec §10）

三条踩出来的硬话：
① **先校验内核来源再打包**：packaging/core 不入库，所以「产物里那颗 exe 从哪来」只能靠 BUNDLED.txt 的 sha256 证明；
   对不上就停下来重取（fetch-bundled-core.ps1），不许打包一颗来路不明的内核。
② **dotnet publish 不会带上 XAML 产物**：实测发布目录缺 App.xbf / MainWindow.xbf / Views\*.xbf 和 ClashAcry.UI.pri
   （构建目录里有），于是产物里的 UI 双击即 0xC0000409 闪退。这里显式补拷并断言 .pri 到位——不是可选步骤。
③ **UI 与 Tray 同目录共存**：两份 self-contained 运行时文件同名同内容，覆盖无害；TrayLink 靠「同目录找
   ClashAcry.Tray.exe」拉起宿主（Task 12 的定位方式），所以不能分目录，除非同时改进程定位（spec §3）。
#>
param(
    [string]$Version = '0.1.0',
    [switch]$SkipCoreFetch,
    [switch]$Trim,
    [switch]$ReadyToRun,
    [string]$RepoDir = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
Set-Location $RepoDir

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "PATH 上没有 dotnet。本机 .NET 10 SDK 在 %LOCALAPPDATA%\Microsoft\dotnet，先 export PATH 再跑（AGENTS.md 环境节）。"
}

$outRoot = Join-Path $RepoDir 'artifacts\publish'
$stage = Join-Path $outRoot "ClashAcry-v$Version-win-x64"
$pubUi = Join-Path $RepoDir 'artifacts\pub\ui'
$pubTray = Join-Path $RepoDir 'artifacts\pub\tray'

# ① 内核：默认先跑一遍 fetch（幂等），再校验本地文件与 BUNDLED.txt 对得上。
if (-not $SkipCoreFetch) { & (Join-Path $PSScriptRoot 'fetch-bundled-core.ps1') }
& (Join-Path $PSScriptRoot 'fetch-bundled-core.ps1') -Verify

$extra = @()
if ($Trim) { $extra += '-p:PublishTrimmed=true' }
if ($ReadyToRun) { $extra += '-p:PublishReadyToRun=true' }

foreach ($p in @(
    @{ proj = 'src\ClashAcry.UI'; dir = $pubUi },
    @{ proj = 'src\ClashAcry.Tray'; dir = $pubTray })) {
    Remove-Item $p.dir -Recurse -Force -ErrorAction SilentlyContinue      # 残留文件会被 Compress-Archive 静默带上
    $cmd = "dotnet publish $($p.proj) -c Release -r win-x64 --self-contained -o $($p.dir)"
    Write-Output "发布：$cmd"
    Invoke-Expression ($cmd + ' ' + ($extra -join ' '))
    if ($LASTEXITCODE -ne 0) { throw "发布 $($p.proj) 失败（trim/R2R 的 IL2xxx 也算失败，别放宽）" }
}

# ② 补 XAML 产物：从**同配置的构建目录**取（publish 目录里没有它们）。
$uiBuild = Get-ChildItem (Join-Path $RepoDir 'src\ClashAcry.UI\bin') -Directory |
    ForEach-Object { Get-ChildItem $_.FullName -Directory -Filter 'Release' -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'net10.0-windows10.0.19041.0\win-x64' } } |
    Where-Object { Test-Path (Join-Path $_ 'ClashAcry.UI.pri') } | Select-Object -First 1
if (-not $uiBuild) { throw "找不到带 ClashAcry.UI.pri 的 UI 构建目录——XAML 产物没处补，产物会闪退。" }
foreach ($item in @('App.xbf', 'MainWindow.xbf', 'ClashAcry.UI.pri', 'Views')) {
    Copy-Item (Join-Path $uiBuild $item) (Join-Path $pubUi $item) -Recurse -Force
}
if (-not (Test-Path (Join-Path $pubUi 'ClashAcry.UI.pri'))) { throw '补拷后仍然没有 ClashAcry.UI.pri，别发。' }

# ③ 装配：Tray 先、UI 后（同名运行时文件内容一致），再放内核。
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$stage\core" | Out-Null
Copy-Item "$pubTray\*" $stage -Recurse -Force
Copy-Item "$pubUi\*"   $stage -Recurse -Force
Copy-Item (Join-Path $RepoDir 'packaging\core\mihomo-windows-amd64-compatible.exe') "$stage\core\mihomo.exe" -Force
# 首启生成，不打进包：settings.json / LocalSettings 都归运行时目录（两棵真相树）。
Get-ChildItem $stage -Filter 'settings.json' -Recurse | Remove-Item -Force

$zip = "$stage.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip -Force
Write-Output ("产物：{0}（{1:N1} MB；目录 {2} 个文件）" -f $zip,
    ((Get-Item $zip).Length / 1MB), (Get-ChildItem $stage -Recurse -File).Count)
Write-Output ("常驻口径：powershell -File scripts/measure-residency.ps1 -ProcessName ClashAcry.Tray")
