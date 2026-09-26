$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $here 'PerfectWorldArenaShield.cs'
$manifest = Join-Path $here 'app.manifest'
$release = Join-Path $here 'bin\PerfectWorldArenaShield.exe'
$test = Join-Path $here 'bin\PerfectWorldArenaShield.test.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$target = 'C:\Program Files (x86)\perfectworldarena'
$exe = Get-ChildItem -LiteralPath $target -File -Filter '*.exe' |
  Where-Object { $_.Name -notlike 'Uninstall *' } |
  Select-Object -First 1 -ExpandProperty FullName
$pvp = Join-Path $target 'plugin\PvpAlive.dll'

function Assert([bool]$ok, [string]$message) { if (-not $ok) { throw $message } }
function Get-PeMachine([string]$path) {
  $bytes = [IO.File]::ReadAllBytes($path)
  $pe = [BitConverter]::ToInt32($bytes, 0x3c)
  return [BitConverter]::ToUInt16($bytes, $pe + 4)
}

Assert (Test-Path $csc) "csc missing: $csc"
Assert ($exe -and (Test-Path $exe)) "target EXE missing under: $target"
Assert (Test-Path $exe) "target EXE missing: $exe"
Assert (Test-Path $pvp) "PvpAlive.dll missing: $pvp"
$expectedExe = (Get-FileHash -Algorithm SHA256 $exe).Hash
$expectedPvp = (Get-FileHash -Algorithm SHA256 $pvp).Hash
Assert ((Get-PeMachine $exe) -eq 0x14c) 'target EXE is not x86'
Assert ((Get-PeMachine $pvp) -eq 0x14c) 'PvpAlive.dll is not x86'

for ($pass = 1; $pass -le 3; $pass++) {
  Write-Output "CHECK $pass/3"
  & $csc /nologo /target:exe /platform:x86 /optimize+ /win32manifest:$manifest /out:$release $src
  Assert ($LASTEXITCODE -eq 0) 'release build failed'
  & $csc /nologo /target:exe /platform:x86 /optimize+ /out:$test $src
  Assert ($LASTEXITCODE -eq 0) 'test build failed'
  Assert ((Get-PeMachine $release) -eq 0x14c) 'release is not x86'
  Assert ((Get-PeMachine $test) -eq 0x14c) 'test build is not x86'
  $source = Get-Content $src -Raw
  Assert ($source -match 'if \(args\.Length == 0\) launchRequested = true') 'zero-argument auto-launch missing'
  Assert ($source -match 'ResolveTargetRoot\(\)') 'automatic target discovery missing'
  Assert ($source -match 'ReadInstalledRoots\(Registry\.LocalMachine\)') 'registry target discovery missing'
  Assert ($source -match 'using running platform pid=') 'running-platform reuse missing'
  Assert ($source -match 'PauseBeforeExit\(\)') 'startup-error pause missing'
  Assert ($source -match 'UseShellExecute = true') 'platform output isolation missing'
  Assert (-not ($source -match 'process\.Modules')) 'noisy Process.Modules scan still present'
  Assert ($source -match 'if \(!dryRun && !noDrivers && !stopDrivers && \(loginThenShield \|\| launchRequested\)\) TryStartDrivers\(\)') 'official driver startup guard missing'
  $self = (& $test --self-test --no-link | Out-String)
  Assert (($self -split "`r?`n" | Where-Object { $_ -match '\[OK\].*RVA=' }).Count -eq 16) 'export self-test failed'
  Assert ($self -match '\[OK\] kernel target size=544') 'kernel target layout self-test failed'
  Assert ($self -match '\[OK\] kernel request size=124') 'kernel request layout self-test failed'
  Assert ($self -match '\[OK\] kernel query ioctl=0x80006004') 'kernel query IOCTL self-test failed'
  Assert ($self -match '\[OK\] kernel apply ioctl=0x8000A008') 'kernel apply IOCTL self-test failed'
  Assert ($self -match '\[OK\] kernel restore ioctl=0x8000A00C') 'kernel restore IOCTL self-test failed'
  Assert (-not ($self -match '\[X\]')) 'missing export reported'
  $dry = (& $test --dry-run --once --profile report-only --no-drivers --no-link | Out-String)
  Assert ($dry -match 'profile=report-only') 'report-only profile not selected'
  Assert ($dry -match '\[UNVERIFIED\]') 'dry-run did not report absent runtime module'
  Assert ((Get-FileHash -Algorithm SHA256 $exe).Hash -eq $expectedExe) 'target EXE changed'
  Assert ((Get-FileHash -Algorithm SHA256 $pvp).Hash -eq $expectedPvp) 'PvpAlive.dll changed'
  $manifestText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($manifest))
  Assert ($manifestText -match 'requireAdministrator') 'elevation manifest missing'
  Write-Output "PASS $pass/3"
}
Write-Output 'THREE_PASS_OK'
