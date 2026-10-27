param([switch]$OccupyMixedPort)

# Task 1 探针：真机核实 mihomo REST 端点形状（Windows PowerShell 5.1 兼容：
# 不用三元运算符与 -SkipHttpErrorCheck；每条 Probe 必打印状态码，失败不中断脚本）。

$core = Join-Path $PSScriptRoot '..\packaging\core\mihomo-windows-amd64-compatible.exe'
$run  = Join-Path $env:TEMP ('clashacry-probe-' + [guid]::NewGuid().ToString('N'))
$errFile = Join-Path $env:TEMP ('clashacry-probe-stderr-' + [guid]::NewGuid().ToString('N') + '.log')
$outFile = Join-Path $env:TEMP ('clashacry-probe-stdout-' + [guid]::NewGuid().ToString('N') + '.log')
New-Item -ItemType Directory -Force -Path $run, (Join-Path $run 'runtime') | Out-Null
Copy-Item (Join-Path $PSScriptRoot '..\packaging\imports-sample\minimal.yaml') (Join-Path $run 'config.yaml')
Copy-Item (Join-Path $PSScriptRoot '..\packaging\imports-sample\providers-sample.yaml') (Join-Path $run 'providers-sample.yaml')

$listener = $null
if ($OccupyMixedPort) {
  $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 7897)
  $listener.Start()
  'OCCUPY: pre-bound 127.0.0.1:7897 with TcpListener before starting kernel'
}

$p = Start-Process $core -ArgumentList '-d', $run -PassThru -WindowStyle Hidden -WorkingDirectory $run -RedirectStandardError $errFile -RedirectStandardOutput $outFile
try {
  $H = @{ Authorization = 'Bearer probe-secret' }
  $base = 'http://127.0.0.1:9097'
  $ready = $false
  for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    try { Invoke-RestMethod "$base/version" -Headers $H -TimeoutSec 1 | Out-Null; $ready = $true; break } catch {}
  }
  'READY-POLL: controller answered GET /version within 10s = ' + $ready

  if ($OccupyMixedPort) {
    # 观察内核在 mixed 端口被占用后是否退出：等 3 秒再看进程状态
    Start-Sleep -Seconds 3
    'OCCUPY-CHECK: kernel process exited = ' + $p.HasExited
  }

  function Probe($m, $path, $body) {
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
  Probe PUT    '/proxies/PROBE-SELECT' @{ proxy = 'prov-node-2' }
  Probe GET    '/proxies/PROBE-SELECT/delay?url=http%3A%2F%2Fwww.gstatic.com%2Fgenerate_204&timeout=5000'
  Probe GET    '/proxies/direct-node/delay?url=http%3A%2F%2Fwww.gstatic.com%2Fgenerate_204&timeout=5000'
  Probe GET    '/proxies/prov-node-1/delay?url=http%3A%2F%2Fwww.gstatic.com%2Fgenerate_204&timeout=5000'
  Probe GET    '/providers/proxies'
  Probe GET    '/providers/proxies/PROBE-PROV'
  Probe PUT    '/providers/proxies/PROBE-PROV'
  Probe PATCH  '/configs' @{ mode = 'global' }
  Probe PUT    '/configs' @{ mode = 'rule' }
  Probe PATCH  '/configs' @{ path = (Join-Path $run 'config.yaml') }
  Probe PUT    '/configs' @{ path = (Join-Path $run 'config.yaml') }

  # 结构证据：provider 成员是否出现在 GET /proxies 顶层表；/providers/proxies 根形状
  '--- STRUCTURE: GET /proxies ---'
  try {
    $vj = Invoke-RestMethod "$base/proxies" -Headers $H -TimeoutSec 5
    $names = @($vj.proxies.PSObject.Properties.Name)
    'root keys: ' + (($vj.PSObject.Properties.Name) -join ' ')
    'top-level proxy table keys (' + $names.Count + '): ' + ($names -join ' | ')
    'contains prov-node-1 at top level: ' + ($names -contains 'prov-node-1')
    'contains prov-node-2 at top level: ' + ($names -contains 'prov-node-2')
    'PROBE-SELECT.all: ' + (@($vj.proxies.'PROBE-SELECT'.all) -join ' | ')
    'PROBE-SELECT.now (last switch was PUT {"proxy":"prov-node-2"}; direct-node => proxy body NOT honored): ' + $vj.proxies.'PROBE-SELECT'.now
  } catch { 'structure check failed: ' + $_.Exception.Message }

  '--- STRUCTURE: GET /providers/proxies ---'
  try {
    $pj = Invoke-RestMethod "$base/providers/proxies" -Headers $H -TimeoutSec 5
    'root is JSON array: ' + ($pj -is [array])
    $pn = @($pj.PSObject.Properties.Name)
    'root keys: ' + ($pn -join ' | ')
    foreach ($k in $pn) {
      'provider [' + $k + '] = ' + (($pj.$k | ConvertTo-Json -Compress -Depth 6) -replace '\s+', ' ')
    }
  } catch { 'provider structure check failed: ' + $_.Exception.Message }
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 500
  # 实测：mihomo v1.19.33 的 Go 日志写在 stdout（logrus 默认输出被其指向 stdout），
  # 两个流都读出来，以免漏证据。
  '--- KERNEL STDERR (verbatim) ---'
  if (Test-Path $errFile) {
    $errLines = @(Get-Content $errFile)
    if ($errLines.Count -eq 0) { 'STDERR: <empty>' } else { $errLines | ForEach-Object { 'STDERR: ' + $_ } }
  } else {
    'STDERR: <file missing>'
  }
  '--- KERNEL STDOUT (verbatim; mihomo logs land here) ---'
  if (Test-Path $outFile) {
    $outLines = @(Get-Content $outFile)
    if ($outLines.Count -eq 0) { 'STDOUT: <empty>' } else { $outLines | ForEach-Object { 'STDOUT: ' + $_ } }
  } else {
    'STDOUT: <file missing>'
  }
  if ($listener) { $listener.Stop() }
  Remove-Item -Recurse -Force $run -ErrorAction SilentlyContinue
  Remove-Item -Force $errFile -ErrorAction SilentlyContinue
  Remove-Item -Force $outFile -ErrorAction SilentlyContinue
}
