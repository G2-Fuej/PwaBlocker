$ErrorActionPreference = 'Stop'

$tests = Split-Path -Parent $MyInvocation.MyCommand.Path
$kernel = Split-Path -Parent $tests
$runtime = Join-Path $kernel 'runtime-test'
$source = Join-Path $tests 'PvpAliveHarness.cs'
$harness = Join-Path $runtime 'PvpAliveHarness.exe'
$target = 'C:\Program Files (x86)\perfectworldarena\plugin\PvpAlive.dll'
$stop = Join-Path $runtime 'smoke.stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

New-Item -ItemType Directory -Path $runtime -Force | Out-Null
& $csc /nologo /target:exe /platform:x86 /optimize+ /out:$harness $source
if ($LASTEXITCODE -ne 0) { throw 'Harness compilation failed.' }

New-Item -ItemType File -Path $stop -Force | Out-Null
try {
  & $harness $target $stop
  if ($LASTEXITCODE -ne 0) { throw "Harness smoke failed: $LASTEXITCODE" }
}
finally {
  Remove-Item -LiteralPath $stop -Force -ErrorAction SilentlyContinue
}

& sc.exe query PwaKernelShield
if ($LASTEXITCODE -ne 0) { throw 'PwaKernelShield query failed.' }
& sc.exe query MessageTransfer
if ($LASTEXITCODE -ne 0) { throw 'MessageTransfer query failed.' }
Write-Output 'HARNESS_SMOKE_OK'
