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
for ($pass = 1; $pass -le 3; $pass++) {
  Write-Output "CHECK $pass/3"
  & $csc /nologo /target:exe /platform:anycpu /optimize+ /win32manifest:$manifest /out:$release $src
  Assert ($LASTEXITCODE -eq 0) 'release build failed'
  & $csc /nologo /target:exe /platform:anycpu /optimize+ /out:$test $src
  Assert ($LASTEXITCODE -eq 0) 'test build failed'
  $self = (& $test --self-test | Out-String)
  Assert (($self -split "`r?`n" | Where-Object { $_ -match '\[OK\] ' }).Count -eq 16) 'export self-test failed'
  Assert (-not ($self -match '\[X\]')) 'missing export reported'
  $dry = (& $test --dry-run --once --no-drivers | Out-String)
  Assert ($dry -match '\[UNVERIFIED\]') 'dry-run did not report absent runtime module'
  Assert ((Get-FileHash -Algorithm SHA256 $exe).Hash -eq $expectedExe) 'target EXE changed'
  Assert ((Get-FileHash -Algorithm SHA256 $pvp).Hash -eq $expectedPvp) 'PvpAlive.dll changed'
  Write-Output "PASS $pass/3"
}
Write-Output 'THREE_PASS_OK'
