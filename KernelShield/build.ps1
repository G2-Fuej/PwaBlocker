$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
$wdkRoots = @(
  (Join-Path $programFilesX86 'Windows Kits/10'),
  (Join-Path $env:ProgramFiles 'Windows Kits/10')
) | Where-Object { $_ -and (Test-Path (Join-Path $_ 'Include')) }
if (-not $wdkRoots) { throw 'Windows Driver Kit not found. Install WDK and Visual Studio C++ driver tools first.' }
$cl = Get-Command cl.exe -ErrorAction SilentlyContinue
$link = Get-Command link.exe -ErrorAction SilentlyContinue
if (-not $cl -or -not $link) { throw 'cl.exe/link.exe are not available. Run this from the VS x64 Native Tools command prompt.' }
$wdk = $wdkRoots | Select-Object -First 1
$include = Get-ChildItem (Join-Path $wdk 'Include') -Directory |
  Where-Object { (Test-Path (Join-Path $_.FullName 'km')) -and (Test-Path (Join-Path $_.FullName 'shared')) } |
  Sort-Object Name -Descending | Select-Object -First 1
$lib = Get-ChildItem (Join-Path $wdk 'Lib') -Directory |
  Where-Object { Test-Path (Join-Path $_.FullName 'km/x64/ntoskrnl.lib') } |
  Sort-Object Name -Descending | Select-Object -First 1
if (-not $include -or -not $lib) { throw 'No matching WDK kernel headers/libraries were found.' }
$out = Join-Path $here 'build/amd64'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$inf = Join-Path $out 'PwaKernelShield.inf'
$obj = Join-Path $out 'PwaKernelShield.obj'
$sys = Join-Path $out 'PwaKernelShield.sys'
$source = Join-Path $here 'PwaKernelShield.c'
$nt = Join-Path $lib.FullName 'km/x64/ntoskrnl.lib'
$wdmsec = Join-Path $lib.FullName 'km/x64/wdmsec.lib'
$bufferOverflow = Join-Path $lib.FullName 'km/x64/bufferoverflowfastfailk.lib'
Copy-Item -LiteralPath (Join-Path $here 'PwaKernelShield.inf') -Destination $inf -Force
& $cl.Source /nologo /c /kernel /GS /Zl /W4 /WX /D_AMD64_ /D_WIN64 /I (Join-Path $include.FullName 'km') /I (Join-Path $include.FullName 'shared') /Fo$obj $source
if ($LASTEXITCODE -ne 0) { throw 'cl.exe failed' }
& $link.Source /nologo /driver /subsystem:native /integritycheck /nodefaultlib /entry:DriverEntry /out:$sys $obj $nt $wdmsec $bufferOverflow
if ($LASTEXITCODE -ne 0) { throw 'link.exe failed' }
Write-Output ('Built: ' + $sys)
Write-Output ('Staged INF: ' + $inf)
Write-Output 'Next: run Inf2Cat /driver:build/amd64 /os:10_X64, sign the SYS and CAT, then verify with signtool /kp.'
