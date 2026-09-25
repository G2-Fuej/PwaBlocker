$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $here 'PerfectWorldArenaShield.cs'
$manifest = Join-Path $here 'app.manifest'
$release = Join-Path $here 'bin\PerfectWorldArenaShield.exe'
$test = Join-Path $here 'bin\PerfectWorldArenaShield.test.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$target = 'C:\Program Files (x86)\perfectworldarena'
$exe = Join-Path $target '完美世界竞技平台.exe'
$pvp = Join-Path $target 'plugin\PvpAlive.dll'
$expectedExe = 'A0115A5C58F9B49C47C947FBE2A13CEEC5202B22F8A247ADDAABF9C23D5ED891'
$expectedPvp = 'REPLACE_FROM_FIRST_SCAN'

function Assert([bool]$ok, [string]$message) { if (-not $ok) { throw $message } }
if (-not (Test-Path $csc)) { throw "csc missing: $csc" }
if ($expectedPvp -eq 'REPLACE_FROM_FIRST_SCAN') {
  $expectedPvp = (Get-FileHash -Algorithm SHA256 $pvp).Hash
}

for ($pass = 1; $pass -le 3; $pass++) {
  Write-Output "CHECK $pass/3"
  & $csc /nologo /target:exe /platform:anycpu /optimize+ /win32manifest:$manifest /out:$release $src
  Assert ($LASTEXITCODE -eq 0) 'release build failed'
  & $csc /nologo /target:exe /platform:anycpu /optimize+ /out:$test $src
  Assert ($LASTEXITCODE -eq 0) 'test build failed'

  $self = (& $test --self-test | Out-String)
  Assert (($self -split "`r?`n" | Where-Object { $_ -match '\[OK\] ' }).Count -eq 16) 'export self-test did not validate all 16 exports'
  Assert (-not ($self -match '\[X\]')) 'export self-test reported a missing export'

  & 'C:\Users\Administrator\.codex\skills\xiaotao-win-flow-auto\scripts\scan.ps1' -Target $release -ResultPath (Join-Path $here 'shield-scan.json') | Out-Null
  Assert ($LASTEXITCODE -eq 0) 'shield scan failed'
  $scan = Get-Content (Join-Path $here 'shield-scan.json') -Raw | ConvertFrom-Json
  Assert ($scan.packed_modules.Count -eq 0) 'shield unexpectedly contains packed modules'
  Assert ($scan.architecture -eq 'x86') 'unexpected PE architecture'

  $dry = (& $test --dry-run --once --no-drivers | Out-String)
  Assert ($dry -match '\[UNVERIFIED\]') 'dry-run did not report the absent runtime module'

  $hExe = (Get-FileHash -Algorithm SHA256 $exe).Hash
  $hPvp = (Get-FileHash -Algorithm SHA256 $pvp).Hash
  Assert ($hExe -eq $expectedExe) 'target EXE changed during verification'
  Assert ($hPvp -eq $expectedPvp) 'PvpAlive.dll changed during verification'
  $manifestText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($manifest))
  Assert ($manifestText -match 'requireAdministrator') 'elevation manifest missing'
  Write-Output "PASS $pass/3"
}
Write-Output 'THREE_PASS_OK'
