$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = Join-Path $here 'Program.cs'
$manifest = Join-Path $here 'app.manifest'
$release = Join-Path $here 'bin\PWA屏蔽器.exe'
$test = Join-Path $here 'bin\PWA屏蔽器.test.exe'
$target = 'C:\Program Files (x86)\perfectworldarena'
$exe = Join-Path $target '完美世界竞技平台.exe'
$pvp = Join-Path $target 'plugin\PvpAlive.dll'
$expectedExe = 'A0115A5C58F9B49C47C947FBE2A13CEEC5202B22F8A247ADDAABF9C23D5ED891'
$expectedPvp = (Get-FileHash -Algorithm SHA256 $pvp).Hash
function Assert([bool]$ok, [string]$message) { if (-not $ok) { throw $message } }
function Get-PeMachine([string]$path) {
  $bytes = [IO.File]::ReadAllBytes($path)
  $pe = [BitConverter]::ToInt32($bytes, 0x3c)
  return [BitConverter]::ToUInt16($bytes, $pe + 4)
}
Assert ((Get-PeMachine $exe) -eq 0x14c) 'target EXE is not x86'
Assert ((Get-PeMachine $pvp) -eq 0x14c) 'PvpAlive.dll is not x86'
$source = Get-Content $src -Raw
Assert ($source -match 'ConsoleColor\.Green' -and $source -match 'ConsoleColor\.Red' -and $source -match 'ConsoleColor\.Yellow') 'color mapping is incomplete'
Assert ($source -match 'G A M E S 8 T H' -and $source -match 'Games8Th\.Team') 'startup logo text is incomplete'
Assert ($source -match '--login-then-shield' -and $source -match 'WaitForLogin') 'login-then-shield mode is missing'
for ($pass = 1; $pass -le 3; $pass++) {
  Write-Output "CHECK $pass/3"
  & $csc /nologo /target:exe /platform:x86 /optimize+ /win32manifest:$manifest /out:$release $src
  Assert ($LASTEXITCODE -eq 0) 'release build failed'
  & $csc /nologo /target:exe /platform:x86 /optimize+ /out:$test $src
  Assert ($LASTEXITCODE -eq 0) 'test build failed'
  $logoWatch = [Diagnostics.Stopwatch]::StartNew()
  $logo = (& $test --self-test | Out-String)
  $logoWatch.Stop()
  Assert ($logo -match 'G A M E S 8 T H') 'Games8Th.Team logo missing'
  Assert ($logo -match 'Games8Th.Team') 'team name missing from startup logo'
  Assert ($logoWatch.ElapsedMilliseconds -ge 1800) 'startup logo did not remain visible for 2 seconds'
  $self = $logo
  Assert (($self -split "`r?`n" | Where-Object { $_ -match '\[OK\] ' }).Count -eq 16) 'export self-test failed'
  Assert (-not ($self -match '\[X\]')) 'missing export reported'
  $dry = (& $test --dry-run --once --no-drivers --no-link | Out-String)
  Assert ($dry -match '\[UNVERIFIED\]') 'dry-run did not report absent runtime module'
  $driverDry = (& $test --dry-run --once --no-link | Out-String)
  Assert ($driverDry -match '\[DRIVER\] candidate=') 'driver discovery did not find MessageTransfer'
  Assert ((Get-FileHash -Algorithm SHA256 $exe).Hash -eq $expectedExe) 'target EXE changed'
  Assert ((Get-FileHash -Algorithm SHA256 $pvp).Hash -eq $expectedPvp) 'PvpAlive.dll changed'
  Write-Output "PASS $pass/3"
}
Write-Output 'THREE_PASS_OK'
