# Perfect World Arena Runtime Shield

直接双击、不带参数启动时，工具会自动发现安装目录、复用或启动平台，并持续使用默认的 report-only 档扫描。没有安装平台时会保留错误窗口，不再只显示两秒 Logo 后退出。无参数模式不会自动启用代理或打开社区链接。

`PerfectWorldArenaShield.cs` 与仓库根目录的 `Program.cs` 保持同步。它默认不停止官方驱动，先正常登录，再按档位接管目标进程内的 `PvpAlive.dll` 导出。

```powershell
New-Item -ItemType Directory -Force .\bin | Out-Null
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x86 /optimize+ /win32manifest:app.manifest /out:bin\PerfectWorldArenaShield.exe PerfectWorldArenaShield.cs
```

```powershell
bin\PerfectWorldArenaShield.exe --login-then-shield --launch --profile report-only --proxy 127.0.0.1:7890 --no-link
```

可选档位：`observe`（只观察）、`report-only`（拦截 `postEvent`/`debugShowInfo`）、`connection`（仅伪装 `connectHost`）、`legacy`（旧版全量改写）。默认不清理服务、不停止官方驱动；诊断时可显式使用 `--cleanup-stale-guard --cleanup-analysis-services`。

`shield-patches.log` 可用于 `--restore`，恢复前会校验 PID 启动时间和当前补丁字节。
