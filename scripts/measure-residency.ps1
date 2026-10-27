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
