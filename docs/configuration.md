# 配置与数据存储

本文说明 ServicesApp 把服务配置、日志与运行状态保存在哪里，以及声明式配置文件的格式。
命令行用法见 [cli.md](cli.md)。

## 文件位置

| 内容 | 位置 | 说明 |
|---|---|---|
| 程序 | 安装目录（MSI 默认 `C:\Program Files\ServicesApp\`） | `ServicesApp.exe`（界面）、`servicesctl.exe`（命令行）、`servicesvc.exe`（包装器模板） |
| 包装器副本 | `%ProgramData%\ServicesApp\wrapper\<文件版本>-<哈希>\servicesvc.exe` | 服务实际运行的包装器；仅 SYSTEM 与管理员可修改，见下文 |
| 服务日志 | `%ProgramData%\windows_service_logs\<服务ID>_<时间>.log` | 每次服务启动生成一个新文件，单个文件超过 50 MB 时轮转 |
| 运行状态 | `%ProgramData%\windows_service_logs\state\<服务ID>.json` | 包装器写入：PID、运行时长、重启次数、最近退出与失败原因 |
| 启动崩溃记录 | `%ProgramData%\windows_service_logs\<服务ID>_CRASH_<时间>.log` | 包装器在读取配置等启动阶段失败时写入 |

日志按全局设置清理（保留天数、总大小上限），服务每次启动时以及 ServicesApp 每天执行一次。

### 包装器副本

安装目录中的 `servicesvc.exe` 只是模板。创建服务或执行 `servicesctl migrate`（ServicesApp 启动时、
MSI 安装 / 升级后会自动执行）时，模板被复制到 `wrapper\<版本>-<哈希>\`，服务的 ImagePath 指向该副本：

- 目录所有者为 Administrators，SYSTEM / Administrators 完全控制，Users 仅读取与执行，禁止继承上级权限；
- 升级时安装目录中的文件不被运行中的服务锁定，无需停止服务；
- `migrate` 删除不再被任何服务引用、且未被占用的旧版本目录；
- 卸载程序时不删除该目录（仍存在的服务依赖它运行）。删除全部服务后执行一次 `migrate` 即可清空。

## 注册表

### 服务配置：`HKLM\SYSTEM\CurrentControlSet\Services\<服务ID>`

服务 ID 形如 `WinSvcMgr_<名称中的字母数字>_<8 位随机十六进制>`，创建后不变。

由 SCM 维护的值（通过 Windows API 修改）：

| 值 | 类型 | 含义 |
|---|---|---|
| `ImagePath` | REG_EXPAND_SZ | `"<包装器路径>" --service-wrapper "<服务ID>"` |
| `DisplayName` | REG_SZ | 服务名称 |
| `Description` | REG_SZ | `Managed by Windows Service Manager: <描述>` |
| `Start` | REG_DWORD | 启动类型：2 自动、3 手动、4 禁用 |
| `DelayedAutostart` | REG_DWORD | 1 表示延迟自动启动（仅自动启动时有效） |
| `ObjectName` | REG_SZ | 运行账户：`LocalSystem`、`NT AUTHORITY\LocalService`、`NT SERVICE\<服务ID>`、`.\user` 等 |
| `DependOnService` | REG_MULTI_SZ | 依赖的服务 |
| `FailureActions` | REG_BINARY | 恢复策略：失败后 1 分钟重启，24 小时内最多 3 次 |

### 本工具的参数：`...\Services\<服务ID>\Parameters`

| 值 | 类型 | 含义 | 默认 |
|---|---|---|---|
| `ExePath` | REG_SZ | 目标程序路径（必需） | — |
| `Args` | REG_SZ | 启动参数，可多行（启动时各行去空白后以空格拼接） | 空 |
| `WorkingDir` | REG_SZ | 工作目录 | 程序所在目录 |
| `DisplayName` | REG_SZ | 服务名称 | — |
| `Description` | REG_SZ | 描述（原文，不含前缀） | 空 |
| `AutoRestart` | REG_DWORD | 1 启用失败自动重启 | 0 |
| `RestartDelaySeconds` | REG_DWORD | 首次重启前的等待（1–3600 秒） | 5 |
| `MaxRestarts` | REG_DWORD | 计数窗口内最大重启次数，0 不限 | 5 |
| `RestartResetMinutes` | REG_DWORD | 距上次重启超过该时间则重新计数 | 10 |
| `RestartBackoff` | REG_DWORD | 1 启用指数退避（最多 16 倍） | 1 |
| `StopMode` | REG_DWORD | 0 结束进程树、1 发送 Ctrl+C、2 执行停止命令 | 0 |
| `StopCommand` | REG_SZ | 停止命令，支持 `{exe}` `{workdir}` `{pid}` | 空 |
| `StopTimeoutSeconds` | REG_DWORD | 优雅停止的最长等待（1–3600 秒） | 30 |
| `MemoryLimitMB` | REG_DWORD | 目标程序进程树的内存上限（MB），0 不限 | 0 |
| `ProcessMemoryLimitMB` | REG_DWORD | 单个进程的内存上限（MB），0 不限 | 0 |
| `CpuLimitPercent` | REG_DWORD | CPU 占用硬上限（全部 CPU 的百分比），0 不限 | 0 |
| `MaxProcesses` | REG_DWORD | 同时运行的进程数上限（含目标程序），0 不限 | 0 |
| `Environment` | REG_MULTI_SZ | 环境变量，每项 `KEY=VALUE`，值中的 `%VAR%` 在启动时展开 | 空 |
| `SecretEnvironmentNames` | REG_MULTI_SZ | 敏感环境变量的变量名 | 空 |
| `CreatedAt` | REG_SZ | 创建时间（ISO 8601），用于默认排序 | — |
| `ManagedBy` | REG_SZ | 固定为 `WindowsServiceManager` | — |

### 敏感环境变量：`...\Parameters\Secrets`

每个敏感变量一个 REG_SZ 值。该子键禁止继承，只允许 SYSTEM / Administrators 完全控制、
服务运行账户读取（其余用户无法读取；服务键本身默认对所有已认证用户可读）。
修改服务时内容与权限会一并重写，运行账户变化时权限随之更新。

### 全局设置：`HKLM\SOFTWARE\WindowsServiceManager`

| 值 | 类型 | 含义 | 默认 |
|---|---|---|---|
| `LogRetentionDays` | REG_DWORD | 日志保留天数 | 7 |
| `LogMaxTotalSizeMB` | REG_DWORD | 日志总大小上限（MB），0 不限 | 1024 |
| `LogWordWrap` | REG_DWORD | 日志查看器自动换行 | 1 |
| `LogShowTimestamps` | REG_DWORD | 日志查看器显示时间戳 | 1 |

子键 `ManagedServices\<服务ID>` 是由本工具管理的服务索引（加速加载）；索引不存在时
（旧版本）会扫描全部服务并重建。

## 日志行格式

```
2026-10-15T10:20:30.123 程序的标准输出（stdout）
2026-10-15T10:20:30.456 [stderr] 程序的标准错误
2026-10-15T10:20:31.000 [system] 包装器记录的事件（启动、退出、重启、停止）
```

时间为本地时间，毫秒精度。程序输出中的 ANSI 颜色序列原样保存，界面中按颜色显示，
`servicesctl logs` 默认去除（`--raw` 保留）。

## 声明式配置文件

`servicesctl export` 输出、`servicesctl apply` 读取的 JSON（支持注释与尾随逗号）：

```jsonc
{
  "version": 1,
  "services": [
    {
      "name": "MyApp",                       // 必需，按名称（不区分大小写）与已有服务匹配
      "description": "接口服务",
      "exe": "C:\\Apps\\my-app.exe",          // 必需，应用时文件必须存在
      "args": "--port 8080",
      "workingDir": "C:\\Apps",              // 省略时为程序所在目录
      "startup": "auto",                     // auto | delayed-auto | manual | disabled
      "account": "virtual",                  // localsystem | localservice | networkservice | virtual | 用户名
      "environment": { "NODE_ENV": "production" },
      "secretEnvironment": { "API_KEY": null },   // null：沿用已保存的值
      "dependencies": [ "Tcpip" ],
      "autoRestart": true,
      "restartPolicy": { "delaySeconds": 5, "maxRestarts": 5, "resetMinutes": 10, "backoff": true },
      "stopPolicy": { "mode": "command", "command": "{exe} -s quit", "timeoutSeconds": 30 },
      "resourceLimits": { "memoryMB": 512, "processMemoryMB": 0, "cpuPercent": 50, "maxProcesses": 0 }
    }
  ]
}
```

规则：

- 条目中未写出的字段视为默认值：配置文件描述服务的**完整**配置，而不是增量修改。
- 未知字段、取值越界、名称重复、程序不存在等错误在修改任何服务之前报出（退出码 2）。
- 文件不包含服务 ID、运行状态与密码。需要“指定用户”账户的服务请先用
  `servicesctl update <服务> --account <用户> --password-stdin` 设置，账户不变时 `apply` 沿用原密码。
- `secretEnvironment` 默认只导出变量名（值为 null，表示沿用已保存的值）；`export --show-secrets` 导出值。
- `apply --prune` 删除文件中不存在的服务（需显式指定）；`--restart` 更新后重启正在运行的服务。
