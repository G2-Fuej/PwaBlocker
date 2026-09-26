$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$kit = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Windows Kits\10'
$vs = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio\2022\BuildTools'
$infVerif = Join-Path $kit 'Tools\10.0.22621.0\x64\infverif.exe'
$inf2Cat = Join-Path $kit 'bin\10.0.22621.0\x86\Inf2Cat.exe'
$dumpbin = Join-Path $vs 'VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64\dumpbin.exe'
$build = Join-Path $here 'build-driver.cmd'
$output = Join-Path $here 'build\amd64'
$sys = Join-Path $output 'PwaKernelShield.sys'
$inf = Join-Path $output 'PwaKernelShield.inf'
$cat = Join-Path $output 'pwakernelshield.cat'

function Assert([bool]$condition, [string]$message) {
  if (-not $condition) { throw $message }
}

foreach ($required in @($infVerif, $inf2Cat, $dumpbin, $build)) {
  Assert (Test-Path -LiteralPath $required) ("missing tool: " + $required)
}

for ($pass = 1; $pass -le 3; $pass++) {
  Write-Output "KERNEL CHECK $pass/3"
  & cmd.exe /d /c $build
  Assert ($LASTEXITCODE -eq 0) 'driver build failed'
  Assert (Test-Path -LiteralPath $sys) 'SYS missing'
  Assert (Test-Path -LiteralPath $inf) 'INF missing'

  $headers = (& $dumpbin /headers $sys | Out-String)
  Assert ($LASTEXITCODE -eq 0) 'dumpbin headers failed'
  Assert ($headers -match '8664 machine \(x64\)') 'driver is not x64'
  Assert ($headers -match 'subsystem \(Native\)') 'driver subsystem is not Native'
  Assert ($headers -match 'Check integrity') 'FORCE_INTEGRITY is missing'

  $imports = (& $dumpbin /imports $sys | Out-String)
  Assert ($LASTEXITCODE -eq 0) 'dumpbin imports failed'
  Assert ($imports -match 'ntoskrnl\.exe') 'ntoskrnl import missing'
  Assert ($imports -notmatch 'KERNEL32\.dll|LIBCMT|MSVCR|VCRUNTIME') 'user-mode CRT import detected'
  Assert ($imports -match 'PsRemoveLoadImageNotifyRoutine') 'image callback cleanup import missing'
  Assert ($imports -match 'PsSetCreateProcessNotifyRoutineEx') 'process callback import missing'

  & $infVerif /w $inf
  Assert ($LASTEXITCODE -eq 0) 'InfVerif failed'
  & $inf2Cat /driver:$output /os:10_X64
  Assert ($LASTEXITCODE -eq 0) 'Inf2Cat failed'
  Assert (Test-Path -LiteralPath $cat) 'catalog missing'
  Write-Output "KERNEL PASS $pass/3"
}

Write-Output 'KERNEL_THREE_PASS_OK'
