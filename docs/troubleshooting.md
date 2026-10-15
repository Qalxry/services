# 故障排查

遇到问题先运行诊断，它会逐项检查并给出修复命令：

```powershell
servicesctl doctor              # 只列出警告与错误
servicesctl doctor MyApp --all  # 某个服务的全部检查项
servicesctl doctor --json       # 供脚本 / AI 解析
```

然后查看服务的详情与日志：

```powershell
servicesctl get MyApp           # 状态、运行时长、重启次数、上次退出码、最近失败原因
servicesctl logs MyApp --tail 100
servicesctl logs MyApp --stream stderr --since 10m
```

`[system]` 开头的日志行由包装器写入（启动、退出、重启、停止），通常能直接说明原因。

## 服务启动后立即停止

`servicesctl start` 会立即报告“启动后立即停止”并附上原因：

| 原因 | 处理 |
|---|---|
| `Executable not found` | 程序路径错误或文件被删除：`servicesctl update <服务> --exe <路径>` |
| `Process exited with code N (AutoRestart disabled)` | 目标程序自身出错退出，查看日志中程序的输出；需要自动重启时加 `--auto-restart` |
| `Process kept failing, gave up after N restarts` | 程序持续崩溃，超过最大重启次数。修复程序问题；约 1 分钟后系统恢复策略会再次拉起服务 |
| `Service startup failed: ...` | 包装器读取配置失败，见 `%ProgramData%\windows_service_logs\<服务ID>_CRASH_*.log` |
| 没有原因、程序以 0 退出 | 程序认为自己正常结束（如前台进程立即返回、需要 `--foreground` 之类的参数） |

## 系统错误码

| 错误 | 含义 | 处理 |
|---|---|---|
| 1053 | 服务未及时响应启动 / 控制请求 | 检查包装器是否存在：`servicesctl migrate`；查看事件查看器“应用程序”日志 |
| 1057 | 账户名无效或密码错误 | 检查 `--account`；指定用户需 `--password-stdin` |
| 1067 | 进程意外终止 | 包装器以失败状态停止（见上表），查看日志 |
| 1069 | 登录失败 | 指定用户的密码已更改或账户缺少“作为服务登录”权限：重新 `update --account <用户> --password-stdin` |
| 2 | 文件不存在 | 目标程序不存在 |

## 低权限账户（虚拟账户、LocalService 等）

- 运行账户需要能读取并执行程序、进入工作目录、读写程序自己使用的目录。`doctor` 会检查前两项，
  并给出 `icacls` 授权命令；日志目录的写权限在保存服务时自动授予。
- 虚拟账户名为 `NT SERVICE\<服务ID>`，授权示例：`icacls "D:\data" /grant "NT SERVICE\WinSvcMgr_MyApp_1a2b3c4d:(OI)(CI)M"`。
- 敏感环境变量对运行账户不可读时，日志中会出现警告且这些变量不会被设置：重新保存服务即可更新权限。

## 停止相关

- **Ctrl+C 不生效**：只对控制台程序有效；GUI 程序或自行创建了新进程组的程序收不到 Ctrl+C，
  超时后会被强制结束。可改用停止命令（`--stop-mode command`）。
- **nginx 等需要专用停止命令**：`--stop-mode command --stop-command "{exe} -s quit"`。
  如果启动参数中有 `-p` / `-c`，停止命令也需带上相同参数。
- **停止很慢**：优雅停止最多等待 `--stop-timeout` 秒（默认 30），超时后结束进程树。
- **系统关机时**：最多等待 4 秒。

## 健康检查

- 日志中的 `Health check failed (n/N): ...` 说明每次失败的原因（HTTP 状态码、连接错误、命令退出码、超时）。
- 程序启动较慢时被误判为不健康：增大 `--health-start-period`（宽限期内的失败不计数，一次成功即结束宽限）。
- `servicesctl get` 的“健康状态”与 `runtime.healthStatus` 显示最近一次检查结果。

## 失败通知

- `servicesctl webhook test [<服务>]` 立即发送测试事件并报告结果。
- 服务日志中的 `Webhook notified: <事件>` / `Webhook notification failed (<事件>): <原因>` 记录每次发送结果。
- 修改 Webhook 后需重启服务才生效（包装器在启动时读取设置）。

## 升级与包装器

- 服务运行的是 `%ProgramData%\ServicesApp\wrapper\` 中的副本，升级安装目录不需要停止服务。
  升级后正在运行的服务在下次重启时切换到新版本，旧版本目录在之后的 `migrate` 中自动删除。
- `doctor` 报告“包装器不是当前版本”时：`servicesctl migrate`，然后重启服务。

## 日志

- 位置：`%ProgramData%\windows_service_logs\`，每次服务启动一个新文件。
- 只看到 `[system]` 行而没有程序输出：程序可能缓冲了输出（如 Python 需 `-u` 或 `PYTHONUNBUFFERED=1`）。
- 日志占用空间：在 ServicesApp 设置中调整保留天数与总大小上限。
