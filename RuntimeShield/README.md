# Perfect World Arena Shield

这是一个运行时屏蔽器，针对当前安装的 `PvpAlive.dll` 导出门和 `MessageTransfer.sys` 服务。
它不会修改安装目录里的 EXE、DLL 或 SYS；启动后扫描加载了目标 DLL 的进程，按磁盘 PE 导出表计算 RVA，然后在远程进程中保存原始字节、临时改写入口并刷新指令缓存。

构建（Windows PowerShell）：

```powershell
New-Item -ItemType Directory -Force .\bin | Out-Null
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:anycpu /optimize+ /win32manifest:app.manifest /out:bin\PerfectWorldArenaShield.exe PerfectWorldArenaShield.cs
```

由于目标平台会以提升权限加载驱动和反作弊组件，启动器带有 `requireAdministrator` 清单；双击时需要通过一次 UAC 提示。

运行：

```powershell
bin\PerfectWorldArenaShield.exe --launch
```

正常启动时会自动打开 QQ 群链接 `https://qm.qq.com/q/BB2CSRSfZu`；检查或自动化运行时可追加 `--no-link` 禁止打开浏览器。

若启动阶段出现网络或反作弊异常，可改用登录后模式：

```powershell
bin\PerfectWorldArenaShield.exe --login-then-shield --launch
```

该模式先保持平台和 `MessageTransfer` 驱动原样运行，完成登录后按回车，再只屏蔽已加载的 `PvpAlive.dll` 用户态入口。

常用检查参数：

```powershell
bin\PerfectWorldArenaShield.exe --dry-run --once
bin\PerfectWorldArenaShield.exe --no-drivers --launch --interval 500
bin\PerfectWorldArenaShield.exe --pid 1234 --interval 250
bin\PerfectWorldArenaShield.exe --self-test
bin\PerfectWorldArenaShield.exe --restore
bin\PerfectWorldArenaShield.exe --no-link
bin\PerfectWorldArenaShield.exe --login-then-shield --launch --no-link
```

`shield-patches.log` 记录每个进程、导出、地址、原始字节和替换字节，`--restore` 按记录写回原始字节；没有观察到 `PvpAlive.dll` 时会明确输出 `UNVERIFIED`，不会伪造成功。当前实现的真返回入口为 `connectHost`，状态/查询入口返回 0，其余入口立即返回；如目标版本改变，工具会重新读取导出表并跳过缺失导出。
