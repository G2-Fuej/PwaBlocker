# Perfect World Arena Shield

这是仓库根入口的运行时屏蔽器，针对当前安装的 `PvpAlive.dll` 导出门和 `MessageTransfer.sys` 服务；`RuntimeShield` 目录保留同一份独立源码和检查脚本。
它不会修改安装目录里的 EXE、DLL 或 SYS；启动后扫描加载了目标 DLL 的进程，按磁盘 PE 导出表计算 RVA，然后在远程进程中保存原始字节、临时改写入口并刷新指令缓存。

构建（Windows PowerShell）：

```powershell
New-Item -ItemType Directory -Force .\bin | Out-Null
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x86 /optimize+ /win32manifest:app.manifest /out:bin\PWA屏蔽器.exe Program.cs
```

目标平台和 `PvpAlive.dll` 都是 x86，因此屏蔽器必须以 x86 进程运行，才能枚举并修改目标进程中的 32 位模块。

由于目标平台会以提升权限加载驱动和反作弊组件，启动器带有 `requireAdministrator` 清单；双击时需要通过一次 UAC 提示。

运行：

```powershell
bin\PWA屏蔽器.exe --launch
```

正常启动时会自动打开 QQ 群链接 `https://qm.qq.com/q/BB2CSRSfZu`；检查或自动化运行时可追加 `--no-link` 禁止打开浏览器。

常用检查参数：

```powershell
bin\PWA屏蔽器.exe --dry-run --once
bin\PWA屏蔽器.exe --no-drivers --launch --interval 500
bin\PWA屏蔽器.exe --pid 1234 --interval 250
bin\PWA屏蔽器.exe --self-test
bin\PWA屏蔽器.exe --restore
bin\PWA屏蔽器.exe --no-link
```

`shield-patches.log` 记录每个进程、导出、地址、原始字节和替换字节，`--restore` 按记录写回原始字节；没有观察到 `PvpAlive.dll` 时会明确输出 `UNVERIFIED`，不会伪造成功。当前实现的真返回入口为 `connectHost`，状态/查询入口返回 0，其余入口立即返回；如目标版本改变，工具会重新读取导出表并跳过缺失导出。
