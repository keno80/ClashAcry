param(
    [string]$ProcessName = 'ClashAcry.Tray',
    [string]$PathLike = '',
    [switch]$Json
)
$ErrorActionPreference = 'Stop'

# 只量**我们自己**的那一份。本机常驻着 clash-verge 的 verge-mihomo：按进程名取就会量到别家内核的内存数，
# 而那个数字看起来完全正常（v1.1 账上第①条）。认「自家」的默认口径是两处——
#   %LOCALAPPDATA%\ClashAcry\*   内核与它的数据（core\mihomo.exe）
#   <本仓库根>\*                 构建/发布产物里的 Tray 与 UI
# 同名的别家进程一律排除；剩下不是恰好一个就当场失败，不返回一个来路不明的中位数。
$own = @(
    (Join-Path $env:LOCALAPPDATA 'ClashAcry\*'),
    ((Resolve-Path (Join-Path $PSScriptRoot '..')).Path + '\*')
)

$all = @(Get-Process -Name $ProcessName -ErrorAction Stop)
# 嵌套 Where-Object 里的 $_ 是**内层**那一项，所以进程对象先落到 $proc，模式落到内层 $_：
# 直接写 $_.Path 会在字符串上取 .Path（= null），整个过滤就静默恒假了。
$picks = @($all | Where-Object {
    $proc = $_
    try {
        if ($PathLike) { return $proc.Path -like $PathLike }
        return (@($own | Where-Object { $proc.Path -like $_ }).Count -gt 0)
    } catch { return $false }
})
if ($picks.Count -ne 1) {
    $kept = @($picks | ForEach-Object Id)
    $foreign = @($all | Where-Object { $kept -notcontains $_.Id } | ForEach-Object { try { $_.Path } catch { '<无权限读路径>' } })
    throw ("期望恰好 1 个 {0} 属于本项目，实际 {1} 个{2}" -f $ProcessName, $picks.Count,
        $(if ($foreign.Count) { "；已排除：$($foreign -join ', ')" } else { '' }))
}

$p = $picks[0]
$ws = @(); $pr = @()
1..5 | ForEach-Object {
  $p.Refresh()
  $ws += [math]::Round($p.WorkingSet64 / 1MB, 1)
  $pr += [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
  Start-Sleep -Milliseconds 400
}
$med = { param($a) ($a | Sort-Object)[[int]($a.Count/2)] }
if ($Json) {
  @{ name = $ProcessName; path = $p.Path; working_set_mb = & $med $ws; private_mb = & $med $pr } | ConvertTo-Json -Compress
} else {
  "{0}: WS={1}MB Private={2}MB (中位数 x5, {3})" -f $ProcessName, (& $med $ws), (& $med $pr), $p.Path
}
