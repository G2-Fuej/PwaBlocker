# PwaKernelShield

PwaKernelShield.sys is an independent auxiliary driver. It does not replace,
patch, or resign the official MessageTransfer.sys.

The driver registers a load-image notification and records the process that
loads the installed PvpAlive.dll. It exposes the PwaKernelShield device with:

- IOCTL_PWA_QUERY_TARGET: return the latest matching PID and image base.
- IOCTL_PWA_APPLY_PATCH: bounded request, expected-byte check, original-byte return.
- IOCTL_PWA_RESTORE_PATCH: guarded restore request using the supplied original bytes.

Requests are limited to 32 bytes, require a PID plus non-zero address, and are
accepted only for the most recently observed process that loaded PvpAlive.dll
and only inside that image's recorded address range. The device ACL permits
only SYSTEM and built-in administrators. The existing C# program remains the
default path until the signed driver is tested.

## Build

The verified local toolchain is Visual Studio Build Tools 17.14 with WDK
10.0.22621. Open an x64 Native Tools prompt and run:

    .\build.ps1

Output:

    build\amd64\PwaKernelShield.sys

For an embedded SYS signature, use this order. The SYS must be signed before
Inf2Cat because the embedded signature changes the SYS hash stored in the CAT:

    signtool.exe sign /fd SHA256 /a build\amd64\PwaKernelShield.sys
    Inf2Cat.exe /driver:build\amd64 /os:10_X64
    signtool.exe sign /fd SHA256 /a build\amd64\pwakernelshield.cat
    signtool.exe verify /kp /v build\amd64\PwaKernelShield.sys
    signtool.exe verify /kp /v build\amd64\pwakernelshield.cat

The files to hand to the signer are:

    build\amd64\PwaKernelShield.sys
    build\amd64\PwaKernelShield.inf
    build\amd64\pwakernelshield.cat

The build output is intentionally excluded from Git. Rebuild it from source,
then sign it. A successful build does not imply that the driver is installed,
loaded, or runtime-tested.

Install only after testing on the exact Windows build:

    rundll32.exe setupapi.dll,InstallHinfSection DefaultInstall 132 build\amd64\PwaKernelShield.inf
    sc.exe start PwaKernelShield

Start the signed auxiliary driver before starting the platform so its image-load
notification can observe PvpAlive.dll:

    PwaBlocker\bin\PWA屏蔽器.exe --kernel-shield --launch --profile report-only --no-link

Remove:

    sc.exe stop PwaKernelShield
    sc.exe delete PwaKernelShield

The official MessageTransfer service and its signed file remain untouched.
