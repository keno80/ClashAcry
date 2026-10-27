<#
内置内核的本机准备（Task 17）：从 MetaCubeX/mihomo 最新 release 取 windows-amd64-compatible 资产，
落成 packaging\core\mihomo-windows-amd64-compatible.exe，并把溯源写进 packaging\core\BUNDLED.txt。

为什么入库的是「溯源三行 + 校验和」而不是二进制本身：packaging/core/ 整目录在 .gitignore 里
（产物里的 exe 在 git 里没有任何对应物 = 无法追溯），所以每次发布前必须能证明这颗 exe 来自哪个 release 资产。
BUNDLED.txt 因此记四行：tag / asset URL / 下载 zip 的 SHA-256（release 页面上的资产原文）/ 解出 exe 的 SHA-256
（publish.ps1 用它做本地校验——zip 不留在盘上，能校验收紧的是「本地 exe 没被换过」）。

幂等：目标存在且 BUNDLED.txt 的 tag 与 GitHub 最新 tag 一致、exe 校验和对得上，就完全跳过网络。
-Verify 只校验不下载（publish.ps1 用），对不上直接失败，逼你重取。
需要 dotnet 之外的东西：无（Invoke-WebRequest + Expand-Archive 都是 Windows PowerShell 5.1 自带）。
#>
param(
    [switch]$Verify,
    [string]$RepoDir = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$coreDir = Join-Path $RepoDir 'packaging\core'
$exe = Join-Path $coreDir 'mihomo-windows-amd64-compatible.exe'
$manifest = Join-Path $coreDir 'BUNDLED.txt'
$assetStem = 'mihomo-windows-amd64-compatible'

function Read-Manifest([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    $m = @{}
    Get-Content $path | ForEach-Object {
        if ($_ -match '^([^:]+):\s*(.+)$') { $m[$matches[1].Trim()] = $matches[2].Trim() }
    }
    return $m
}

function Test-Local([hashtable]$m) {
    if (-not (Test-Path $exe)) { return $false }
    if (-not $m -or -not $m['exe_sha256']) { return $false }
    $actual = (Get-FileHash -Algorithm SHA256 $exe).Hash.ToLowerInvariant()
    return $actual -eq $m['exe_sha256'].ToLowerInvariant()
}

$m = Read-Manifest $manifest

if ($Verify) {
    if (-not (Test-Local $m)) {
        throw "packaging\core 的内核缺失或与 BUNDLED.txt 的 sha256 对不上——先跑 scripts/fetch-bundled-core.ps1 重取，别发没有来源的内核。"
    }
    Write-Output ("内核就位：{0}（{1} 字节，sha256={2}…）" -f $m['tag'], (Get-Item $exe).Length, $m['exe_sha256'].Substring(0, 12))
    exit 0
}

# 先探一次「已有的是不是就是最新的」，是就不下载（脚本幂等，AGENTS 要求可反复跑）
$latest = Invoke-RestMethod -UserAgent 'ClashAcry-build' -Uri "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest"
$tag = $latest.tag_name
if ((Test-Local $m) -and $m['tag'] -eq $tag) {
    Write-Output ("内置内核已是最新（{0}），跳过下载。" -f $tag)
    exit 0
}

$asset = $latest.assets | Where-Object { $_.name -like "$assetStem-*.zip" } | Select-Object -First 1
if (-not $asset) { throw "最新 release $tag 里没有 $assetStem-*.zip 资产，别猜 URL。" }

New-Item -ItemType Directory -Force -Path $coreDir | Out-Null
$tmp = Join-Path $env:TEMP ("ca-core-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp $asset.name
    Write-Output ("下载 {0}（{1:N1} MB）…" -f $asset.name, ($asset.size / 1MB))
    Invoke-WebRequest -UserAgent 'ClashAcry-build' -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing

    $zipSha = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
    $unz = Join-Path $tmp 'u'
    Expand-Archive -Path $zip -DestinationPath $unz -Force
    # 包内 exe 名字带 tag（…-v1.19.35.exe），落成仓库内固定名，发布脚本才有确定的输入。
    $found = Get-ChildItem -Path $unz -Recurse -Filter '*.exe' | Select-Object -First 1
    if (-not $found) { throw "$($asset.name) 解出来没有 exe，包结构变了，去看 release 资产。" }
    $exeSha = (Get-FileHash -Algorithm SHA256 $found.FullName).Hash.ToLowerInvariant()
    Copy-Item $found.FullName $exe -Force

    @(
        "tag: $tag"
        "asset: $($asset.browser_download_url)"
        "sha256: $zipSha"
        "exe_sha256: $exeSha"
    ) | Set-Content -Path $manifest -Encoding ascii
    Write-Output ("内置内核已更新：{0} → {1}（exe sha256={2}…）" -f $tag, $exe, $exeSha.Substring(0, 12))
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
