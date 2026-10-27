<#
裁定 R-10d⑤：app.ico 由构建期脚本产出、**产物入库**（src\ClashAcry.Tray\app.ico），
运行时用 LoadImageW(LR_LOADFROMFILE) 从磁盘读（裁定 R-10d④：是「复制到输出目录」不是「嵌入资源」）。

System.Drawing 只允许出现在这个脚本里——产品代码（Tray/UI）禁绝引用它（AGENTS.md / spec §10 的常驻预算
按「零 XAML、零 WinForms、不加载 GDI+ 类型」算）。用 Windows PowerShell 5.1 跑：
    powershell -File scripts/make-icon.ps1
控制器已实测（2026-10-26）：这样产出的 .ico（766 字节，目录项 count=1、w/h 字节为 0）
用 LoadImageW(..., 0, 0, LR_LOADFROMFILE) 与显式 32,32 两种调用都返回非零 HICON，所以够用。
图案是占位（深色圆底 + 青色圆点），像素级观感归 Task 18 验收。
#>
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\src\ClashAcry.Tray\app.ico'),
    [int]$Size = 32
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$bmp = New-Object System.Drawing.Bitmap $Size, $Size
$g = [System.Drawing.Graphics]::FromImage($bmp)
try {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::FromArgb(255, 24, 28, 36))
    # 静态 Pens/Brushes：占位图案不需要自定义颜色，而 5.1 上新建 Pen/SolidBrush 的几种写法都踩过坑
    # （`New-Object Pen(Color), 3` 把宽度丢在括号外 → $null；`-ArgumentList $teal` 报 CannotFindAppropriateCtor；
    #  `[SolidBrush]::new($teal)` 报 MethodArgumentConversionInvalidCast）。静态成员也就不必 Dispose。
    $g.DrawEllipse([System.Drawing.Pens]::Teal, 5, 5, ($Size - 10), ($Size - 10))
    $g.FillEllipse([System.Drawing.Brushes]::Teal, (($Size / 2) - 4), (($Size / 2) - 4), 8, 8)
} finally { $g.Dispose() }

$iconPath = [System.IO.Path]::GetFullPath($OutFile)
# Icon.FromHandle(bmp.GetHicon()) 是 GDI 句柄，Save 完交给 Icon.Dispose() 释放（脚本自己不漏句柄）。
$hicon = $bmp.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($hicon)
try {
    $fs = [System.IO.File]::Create($iconPath)
    try { $icon.Save($fs) } finally { $fs.Dispose() }
} finally {
    $icon.Dispose()
    $bmp.Dispose()
}

Write-Host ("app.ico -> {0} ({1} bytes)" -f $iconPath, (Get-Item $iconPath).Length)
