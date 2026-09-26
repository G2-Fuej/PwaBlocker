$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$package = Join-Path $here 'build\amd64'
$inf = Join-Path $package 'PwaKernelShield.inf'
$sys = Join-Path $package 'PwaKernelShield.sys'
$cat = Join-Path $package 'pwakernelshield.cat'
$log = Join-Path $here 'install-signed-driver.log'

Start-Transcript -LiteralPath $log -Force | Out-Null
try {
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = New-Object Security.Principal.WindowsPrincipal($identity)
  if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator token is required.'
  }

  foreach ($file in @($inf, $sys, $cat)) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Missing package file: $file" }
  }
  foreach ($file in @($sys, $cat)) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid') {
      throw "Invalid signature: $file ($($signature.Status))"
    }
  }

  Write-Output 'MessageTransfer before install:'
  & sc.exe query MessageTransfer

  Write-Output 'Installing signed PwaKernelShield package:'
  & pnputil.exe /add-driver $inf /install
  if ($LASTEXITCODE -ne 0) { throw "pnputil failed: $LASTEXITCODE" }

  & sc.exe query PwaKernelShield *> $null
  if ($LASTEXITCODE -ne 0) {
    Write-Output 'PnPUtil staged the package but did not create the legacy service; applying DefaultInstall.'
    & rundll32.exe setupapi.dll,InstallHinfSection DefaultInstall 132 $inf
    if ($LASTEXITCODE -ne 0) { throw "DefaultInstall failed: $LASTEXITCODE" }
  }

  Write-Output 'Starting PwaKernelShield:'
  & sc.exe start PwaKernelShield
  $startExit = $LASTEXITCODE
  if ($startExit -ne 0 -and $startExit -ne 1056) {
    throw "sc start failed: $startExit"
  }

  Write-Output 'PwaKernelShield after start:'
  & sc.exe query PwaKernelShield
  if ($LASTEXITCODE -ne 0) { throw 'PwaKernelShield query failed.' }

  Write-Output 'MessageTransfer after install:'
  & sc.exe query MessageTransfer
  if ($LASTEXITCODE -ne 0) { throw 'MessageTransfer query failed.' }

  Write-Output 'INSTALL_SIGNED_DRIVER_OK'
}
finally {
  Stop-Transcript | Out-Null
}
