$ErrorActionPreference = 'Stop'

$tests = Split-Path -Parent $MyInvocation.MyCommand.Path
$kernel = Split-Path -Parent $tests
$root = Split-Path -Parent $kernel
$runtime = Join-Path $kernel 'runtime-test'
$transcript = Join-Path $kernel 'signed-runtime-cycles.log'
$targetDll = 'C:\Program Files (x86)\perfectworldarena\plugin\PvpAlive.dll'
$csc = Join-Path (Join-Path $env:WINDIR 'Microsoft.NET') 'Framework64\v4.0.30319\csc.exe'
$harness = Join-Path $runtime 'PvpAliveHarness.exe'
$shield = Join-Path $runtime 'PWA-Shield-Test.exe'

function Assert([bool]$condition, [string]$message) {
  if (-not $condition) { throw $message }
}

function Get-ServiceState([string]$name) {
  try { return (Get-Service -Name $name -ErrorAction Stop).Status.ToString().ToUpperInvariant() }
  catch { return 'MISSING' }
}

function Wait-ForText([string]$path, [string]$pattern, [int]$seconds) {
  $deadline = (Get-Date).AddSeconds($seconds)
  do {
    if (Test-Path -LiteralPath $path) {
      $text = Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
      if ($text -match $pattern) { return $true }
    }
    Start-Sleep -Milliseconds 100
  } while ((Get-Date) -lt $deadline)
  return $false
}

function Get-NewRelevantApplicationErrors([datetime]$since) {
  return @(Get-WinEvent -FilterHashtable @{ LogName='Application'; Id=1000,1001; StartTime=$since } -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'PvpAliveHarness|PWA-Shield-Test|PWA屏蔽器|完美世界竞技平台' })
}

function Get-NewKernelFailures([datetime]$since) {
  return @(Get-WinEvent -FilterHashtable @{ LogName='System'; Id=41,1001,7000,7001,7009,7026,7031,7034; StartTime=$since } -ErrorAction SilentlyContinue | Where-Object { $_.Id -in 41,1001 -or $_.Message -match 'PwaKernelShield' })
}

Start-Transcript -LiteralPath $transcript -Force | Out-Null
try {
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = New-Object Security.Principal.WindowsPrincipal($identity)
  Assert $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) 'Administrator token is required.'
  Assert (Test-Path -LiteralPath $targetDll) 'PvpAlive.dll is missing.'
  Assert ((Get-ServiceState 'PwaKernelShield') -eq 'RUNNING') 'PwaKernelShield is not running.'
  Assert ((Get-ServiceState 'MessageTransfer') -eq 'RUNNING') 'MessageTransfer is not running.'

  New-Item -ItemType Directory -Path $runtime -Force | Out-Null
  & $csc /nologo /target:exe /platform:x86 /optimize+ /out:$harness (Join-Path $tests 'PvpAliveHarness.cs')
  Assert ($LASTEXITCODE -eq 0) 'Harness compilation failed.'
  $shieldSource = Get-ChildItem -LiteralPath (Join-Path $root 'bin') -File -Filter '*.test.exe' | Select-Object -First 1 -ExpandProperty FullName
  Assert (-not [string]::IsNullOrWhiteSpace($shieldSource) -and (Test-Path -LiteralPath $shieldSource)) 'Missing shield test executable in bin.'
  Copy-Item -LiteralPath $shieldSource -Destination $shield -Force
  $dumpsBefore = @(Get-ChildItem C:\Windows\Minidump -File -ErrorAction SilentlyContinue | ForEach-Object FullName)

  for ($cycle = 1; $cycle -le 3; $cycle++) {
    Write-Output "RUNTIME CYCLE $cycle/3"
    $cycleStart = Get-Date
    $stdout = Join-Path $runtime "harness-$cycle.log"
    $stderr = Join-Path $runtime "harness-$cycle.err.log"
    $stop = Join-Path $runtime "harness-$cycle.stop"
    $ledger = Join-Path $runtime 'shield-patches.log'
    Remove-Item -LiteralPath $stdout,$stderr,$stop,$ledger -Force -ErrorAction SilentlyContinue

    $arguments = @('"' + $targetDll + '"', '"' + $stop + '"')
    $process = Start-Process -FilePath $harness -ArgumentList $arguments -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    try {
      Assert (Wait-ForText $stdout 'READY pid=' 20) 'Harness did not become ready.'
      $baselineLines = @(Get-Content -LiteralPath $stdout)
      $baselineText = $baselineLines -join [Environment]::NewLine
      $readyLine = $baselineLines | Where-Object { $_ -like 'READY pid=*' } | Select-Object -First 1
      Assert ([bool]$readyLine) 'Harness did not report its PID.'
      $actualPidText = ([regex]::Match($readyLine, 'READY pid=([0-9]+)')).Groups[1].Value
      $actualPid = 0
      Assert ([int]::TryParse($actualPidText, [ref]$actualPid)) 'Harness PID was invalid.'
      Assert ($actualPid -gt 0) 'Harness PID was empty.'
      foreach ($name in @('postEvent','debugShowInfo','connectHost','startInstance','stopInstance','initMatchInfo')) {
        $line = @($baselineLines | Where-Object { $_ -like "BASE $name *" }) | Select-Object -First 1
        $lineOk = ($null -ne $line) -and ([bool]($line -match 'byte=55$'))
        Assert ([bool]$lineOk) "Baseline byte mismatch: $name"
      }

      $patchOutput = (& $shield --kernel-shield --profile report-only --max-scans 5 --interval 100 --no-drivers --no-link 2>&1 | Out-String)
      Write-Output $patchOutput
      Assert ($LASTEXITCODE -eq 0) 'Kernel shield patch process failed.'
      Assert ($patchOutput.Contains("[PATCH] kernel pid=$actualPid postEvent")) 'postEvent was not patched.'
      Assert ($patchOutput.Contains("[PATCH] kernel pid=$actualPid debugShowInfo")) 'debugShowInfo was not patched.'
      Assert (Wait-ForText $stdout 'CHANGE postEvent old=55 new=C3' 10) 'Harness did not observe postEvent patch.'
      Assert (Wait-ForText $stdout 'CHANGE debugShowInfo old=55 new=C3' 10) 'Harness did not observe debugShowInfo patch.'
      $changed = Get-Content -LiteralPath $stdout -Raw
      Assert (-not ($changed -match 'CHANGE (connectHost|startInstance|stopInstance|initMatchInfo)')) 'An excluded export changed.'

      $restoreOutput = (& $shield --restore --kernel-shield --no-drivers --no-link 2>&1 | Out-String)
      Write-Output $restoreOutput
      Assert ($LASTEXITCODE -eq 0) 'Kernel shield restore process failed.'
      Assert ($restoreOutput.Contains('[OK] restored=2')) 'Expected exactly two restored exports.'
      Assert (Wait-ForText $stdout 'CHANGE postEvent old=C3 new=55' 10) 'Harness did not observe postEvent restore.'
      Assert (Wait-ForText $stdout 'CHANGE debugShowInfo old=C3 new=55' 10) 'Harness did not observe debugShowInfo restore.'
    }
    finally {
      New-Item -ItemType File -Path $stop -Force | Out-Null
      if (-not $process.HasExited) { $process.WaitForExit(10000) | Out-Null }
      if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }

    Assert $process.HasExited 'Harness did not exit.'
    $harnessFinal = Get-Content -LiteralPath $stdout -Raw
    Assert ($harnessFinal.Contains("STOP pid=$actualPid")) 'Harness did not report a clean stop.'
    Assert ($harnessFinal.Contains('EXIT=0')) 'Harness did not report exit 0.'
    $harnessError = Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue
    Assert ([string]::IsNullOrWhiteSpace($harnessError)) 'Harness wrote to stderr.'
    Assert ((Get-ServiceState 'PwaKernelShield') -eq 'RUNNING') 'PwaKernelShield stopped unexpectedly.'
    Assert ((Get-ServiceState 'MessageTransfer') -eq 'RUNNING') 'MessageTransfer stopped unexpectedly.'
    Start-Sleep -Seconds 2
    Assert ((Get-NewRelevantApplicationErrors $cycleStart).Count -eq 0) 'A relevant application crash event was recorded.'
    Assert ((Get-NewKernelFailures $cycleStart).Count -eq 0) 'A kernel or driver failure event was recorded.'

    & sc.exe stop PwaKernelShield | Out-Null
    Assert ($LASTEXITCODE -eq 0) 'Driver stop failed.'
    Assert ((Get-ServiceState 'PwaKernelShield') -eq 'STOPPED') 'Driver did not stop.'
    & sc.exe start PwaKernelShield | Out-Null
    Assert ($LASTEXITCODE -eq 0) 'Driver restart failed.'
    Assert ((Get-ServiceState 'PwaKernelShield') -eq 'RUNNING') 'Driver did not restart.'
    Assert ((Get-ServiceState 'MessageTransfer') -eq 'RUNNING') 'MessageTransfer changed during lifecycle test.'
    Write-Output "RUNTIME PASS $cycle/3"
  }

  $dumpsAfter = @(Get-ChildItem C:\Windows\Minidump -File -ErrorAction SilentlyContinue | ForEach-Object FullName)
  Assert (@($dumpsAfter | Where-Object { $_ -notin $dumpsBefore }).Count -eq 0) 'A new minidump was created.'
  Assert ((Get-ServiceState 'PwaKernelShield') -eq 'RUNNING') 'PwaKernelShield is not running after cycles.'
  Assert ((Get-ServiceState 'MessageTransfer') -eq 'RUNNING') 'MessageTransfer is not running after cycles.'
  Write-Output 'SIGNED_RUNTIME_THREE_PASS_OK'
}
finally {
  Stop-Transcript | Out-Null
}
