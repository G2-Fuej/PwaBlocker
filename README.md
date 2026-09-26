# Perfect World Arena Runtime Shield

直接双击、不带参数启动时，工具会自动查找完美世界竞技平台安装目录，复用已经运行的平台或自动启动平台，并以默认的 report-only 档持续扫描。找不到平台时会显示错误并等待用户确认，不会在 Logo 后直接闪退。无参数模式不会自动设置代理，也不会额外打开社区链接。

这是一个只修改目标进程内存的运行时工具，不改写安装目录中的 EXE、DLL 或 SYS。默认保持官方驱动和登录链路不变，避免提前停止 `MessageTransfer.sys` 造成网络异常。

## 构建

```powershell
New-Item -ItemType Directory -Force .\bin | Out-Null
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x86 /optimize+ /win32manifest:app.manifest /out:bin\PWA屏蔽器.exe Program.cs
```

## 运行档位

```powershell
# 只观察，不改目标进程
bin\PWA屏蔽器.exe --launch --profile observe --no-link

# 默认的轻量档：仅拦截 PvpAlive 的 postEvent/debugShowInfo
bin\PWA屏蔽器.exe --login-then-shield --launch --profile report-only --proxy 127.0.0.1:7890 --no-link

# 连接档：只让 connectHost 返回成功，其余导出保持原样
bin\PWA屏蔽器.exe --login-then-shield --launch --profile connection --proxy 127.0.0.1:7890 --no-link

# 兼容旧版本的全量入口改写
bin\PWA屏蔽器.exe --login-then-shield --launch --profile legacy --no-link
```

`--login-then-shield` 会先正常启动平台；完成登录后回到控制台按回车，工具才会扫描并接管已加载的 `PvpAlive.dll`。屏蔽器会持续驻留，避免平台切换到游戏子进程后监控提前退出。`--proxy` 只设置平台的 Chromium 代理参数，不修改原生模块环境变量。

默认不清理服务，也不停止 `MessageTransfer.sys`。如需诊断残留项，显式追加 `--cleanup-stale-guard --cleanup-analysis-services`。

## 检查与恢复

```powershell
bin\PWA屏蔽器.exe --self-test --no-link
bin\PWA屏蔽器.exe --dry-run --once --profile report-only --no-link
bin\PWA屏蔽器.exe --pid 1234 --profile report-only --interval 250 --exit-after-patch --no-link
bin\PWA屏蔽器.exe --restore
```

`shield-patches.log` 保存 PID、进程启动时间、地址、原始字节和补丁字节。`--restore` 会校验进程身份和当前字节，避免把补丁写入复用后的 PID。
