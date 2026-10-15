# ServicesApp 改进提案（面向服务器场景的持久服务管理）

本文档记录将 ServicesApp / `servicesctl` 打磨为可用于生产服务器的服务管理工具所需的改进，
按优先级排列。每项完成后单独提交一次，便于审计；完成后在“状态”中注明提交。

## 背景：与 systemd 的对应关系

| systemd | ServicesApp |
|---|---|
| systemd（进程监管） | Windows 服务控制管理器（SCM）+ `ServicesApp.exe --service-wrapper` 包装器 |
| unit 文件 | 注册表 `HKLM\SYSTEM\CurrentControlSet\Services\<ID>\Parameters` |
| `systemctl` | `servicesctl` |
| `journalctl` | `servicesctl logs` |

`servicesctl` 只管理由 ServicesApp 创建的服务。当前已具备：开机自启、失败自动重启（含退避）、
stdout/stderr 日志、GUI 与 CLI 管理、JSON 输出。以下为生产服务器场景下仍存在的缺口。

## 优先级总览

| # | 项目 | 优先级 | 状态 |
|---|---|---|---|
| 1 | 子进程纳入 Job Object，杜绝孤儿进程 | P0 必须 | 已完成 |
| 2 | 优雅停止（Ctrl+C / 自定义停止命令 + 超时强制结束） | P0 必须 | 待办 |
| 3 | 放弃重启时以失败状态退出，触发 SCM 恢复策略 | P0 必须 | 待办 |
| 4 | 运行账户（LocalSystem / LocalService / NetworkService / 虚拟账户 / 指定用户） | P1 生产 | 待办 |
| 5 | 服务级环境变量 | P1 生产 | 待办 |
| 6 | 日志：行时间戳、总大小上限、`logs --follow` | P1 生产 | 待办 |
| 7 | 运行状态：启动时间、上次退出码、重启次数 | P1 生产 | 待办 |
| 8 | 服务依赖与延迟自动启动 | P2 增强 | 待办 |
| 9 | 独立轻量包装器（并移除多余的 WinForms 引用） | P2 增强 | 待办 |
| 10 | 配置导出 / 声明式应用（`export` / `apply`） | P2 增强 | 待办 |
| 11 | 自动化测试并接入 CI | P2 增强 | 待办 |

实施顺序：1 → 3 → 2 → 4 → 5 → 6 → 7 → 8 → 9 → 10 → 11（11 可与其他项并行）。

通用约束：
- 新增配置项在注册表缺省时必须保持旧版本行为，已有服务无需重建。
- GUI（表单 / 详情）与 CLI（选项 / 帮助文本 / JSON 字段）同步更新。
- 注释、界面文本、帮助文本使用中文。

---

## 1. 子进程纳入 Job Object，杜绝孤儿进程（P0）

**现状**：`EmbeddedServiceWrapper` 用 `Process.Start` 直接启动目标程序，未关联任何 Job Object。
包装器进程一旦崩溃或被强制结束，目标程序（及其子进程）继续运行；随后 SCM 恢复策略重启服务，
又启动一个新实例，导致端口冲突、双开。

**方案**：
- 包装器启动时创建 Job Object，设置 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`；
  每次启动目标进程后立即 `AssignProcessToJobObject`。
- 包装器进程退出（含崩溃、被结束）时句柄关闭，系统自动结束整个进程树。
- 目标程序自己创建的子进程默认继承 Job，一并受控。

**验收**：在任务管理器中结束包装器进程后，目标程序及其子进程随之退出。

## 2. 优雅停止（P0）

**现状**：`OnStop` 直接 `Process.Kill(entireProcessTree: true)`，进行中的请求被中断、数据可能未落盘。

**方案**：服务新增“停止方式”配置，优雅停止超时后回退为强制结束。

| 停止方式 | 做法 | 适用 |
|---|---|---|
| `kill`（默认，保持现状） | 结束进程树 | 不关心退出方式的程序 |
| `ctrl-c` | 包装器附加到子进程的隐藏控制台并发送 Ctrl+C（同 NSSM） | Node / Python / Go / .NET 等响应 SIGINT 的程序 |
| `command` | 执行自定义停止命令，等待主进程自行退出 | nginx（`{exe} -s quit`）、redis（`redis-cli shutdown`）等 |

- 停止超时（秒，默认 30）：超时后强制结束进程树。
- 等待期间调用 `RequestAdditionalTime`，避免 SCM 判定停止超时。
- 停止命令支持占位符 `{exe}` `{workdir}` `{pid}`，在服务工作目录中执行。
  nginx 需保持与启动参数一致的 `-p` / `-c`，例如 `{exe} -c conf\my.conf -s quit`。
- 系统关机时同样尝试优雅停止（`CanShutdown`），超时时间受系统限制。
- 注册表：`StopMode`、`StopCommand`、`StopTimeoutSeconds`。
- GUI 表单与详情、CLI `--stop-mode` / `--stop-command` / `--stop-timeout` 同步支持。

**验收**：nginx 服务配置 `command` + `{exe} -s quit` 后，`servicesctl stop` 由 nginx 自行退出；
停止命令无效时在超时后被强制结束。

## 3. 放弃重启时以失败状态退出（P0）

**现状**：超过最大重启次数后包装器调用 `Stop()`，退出码为 0，SCM 视为正常停止，
已配置的恢复策略（`sc failure ... restart/60000 ×3`）不会触发，服务永久停止且无任何告警。

**方案**：
- 放弃重启、目标程序无法启动等异常情况下设置非 0 的服务退出码（`ServiceSpecificExitCode`）。
- 为服务启用 `FailureActionsOnNonCrashFailures`，使非 0 退出也触发 SCM 恢复策略
  （即“过一段时间后再从头尝试”），并在日志中写明原因。
- 同时写入 Windows 事件日志，便于现有监控系统采集告警。

**验收**：目标程序持续崩溃时，超过最大次数后服务进入失败状态，约 1 分钟后由 SCM 重新拉起；
事件查看器中可见对应记录。

## 4. 运行账户（P1）

**现状**：创建服务时未指定账户（`lpServiceStartName = null`），所有服务以 LocalSystem 运行，权限过高。

**方案**：
- 支持 LocalSystem（默认）、LocalService、NetworkService、虚拟账户 `NT SERVICE\<服务ID>`、指定用户（账户 + 密码）。
- 通过 `ChangeServiceConfig` 设置；指定用户时由 SCM 加密保存密码，本工具不落盘密码，
  并为账户授予“作为服务登录”权限。
- 非 SYSTEM 账户需能写日志：为日志目录授予该账户写权限。
- GUI / CLI 同步（CLI 密码通过 `--password-stdin` 读取，避免出现在命令行历史中）。

**验收**：以虚拟账户运行的服务可正常启动、写日志；`get` 显示当前账户。

## 5. 服务级环境变量（P1）

**现状**：只能修改全局系统环境变量，无法为单个服务设置（对应 systemd `Environment=`）。

**方案**：
- 注册表 `Parameters\Environment`（REG_MULTI_SZ，`KEY=VALUE`）；包装器启动目标进程时合并到继承的环境中。
- GUI 表单新增多行“环境变量”输入；CLI `--env KEY=VALUE`（可重复）、`--unset-env KEY`、`--clear-env`。

**验收**：设置后目标程序可读取到对应变量；`get --json` 输出 `environment` 对象。

## 6. 日志改进（P1）

**现状**：日志行无时间戳；仅按天数清理，没有总大小上限，高输出服务可能写满磁盘；无法实时跟踪。

**方案**：
- 每行写入时间戳（ISO 8601 本地时间，毫秒精度），格式：`2026-10-15T10:20:30.123 [stderr] ...`；
  解析器兼容旧格式（无时间戳）。
- 全局日志总大小上限（默认 1 GB，可在设置中修改），超出时从最旧文件开始删除；单服务文件轮转保留现状。
- 日志窗口显示时间戳（可通过复选框隐藏）；`servicesctl logs` 新增 `--follow`（`-f`）实时跟踪与 `--since`。

**验收**：新日志每行带时间戳；超过上限后最旧文件被清理；`logs -f` 可持续输出新行直到 Ctrl+C。

## 7. 运行状态信息（P1）

**现状**：无法像 `systemctl status` 一样查看运行时长、上次退出码、重启次数；这些信息只存在于包装器内存。

**方案**：
- 包装器将运行状态写入状态文件 `%ProgramData%\windows_service_logs\state\<服务ID>.json`
  （包装器启动时间、目标进程 PID 与启动时间、上次退出码与时间、本轮重启次数、最近一次放弃原因）。
  使用文件而非 HKLM，以兼容低权限运行账户（见第 4 项）。
- GUI 详情、CLI `get` 显示；JSON 新增 `runtime` 字段。

**验收**：目标程序崩溃重启后，`get` 中重启次数与上次退出码正确。

## 8. 服务依赖与延迟自动启动（P2）

**方案**：
- 依赖：`ChangeServiceConfig` 的 `lpDependencies`，可选择其他服务（含非本工具创建的系统服务，如 `Tcpip`、`MySQL80`）。
- 延迟自动启动：`ChangeServiceConfig2(SERVICE_CONFIG_DELAYED_AUTO_START_INFO)`，启动类型新增 `delayed-auto`。
- GUI / CLI 同步（`--depends-on`、`--startup delayed-auto`）。

## 9. 独立轻量包装器（P2）

**现状**：每个服务进程运行的都是完整 GUI 程序（单文件约 255 MB，其中约 87 MB 为未使用的
WinForms/WPF 框架）；升级 GUI 必须停止所有服务；服务绑定在 GUI 的安装路径上。

**方案**：
- 移除 `ServicesApp.csproj` 中未使用的 `Microsoft.WindowsDesktop.App.WindowsForms` 引用（约 −87 MB）。
- 新增控制台项目 `servicesvc.exe`（包装器专用，自包含单文件 + 裁剪），服务 ImagePath 指向它。
- GUI 启动时自动将旧服务（指向 `ServicesApp.exe --service-wrapper`）迁移到新包装器（服务停止后生效 / 下次重启生效）。
- `ServicesApp.exe --service-wrapper` 保留兼容，未迁移的服务继续可用。
- 构建工作流、发布工作流、MSI 同步包含新程序。

## 10. 配置导出 / 声明式应用（P2）

**方案**：
- `servicesctl export [<服务>...] [-o file.json]`：导出服务配置（不含运行状态与密码）。
- `servicesctl apply -f file.json [--dry-run] [--prune]`：按名称匹配，创建缺失服务、更新差异字段；
  `--dry-run` 只输出计划；`--prune` 删除文件中不存在的服务（需显式指定）。
- 便于版本库管理配置与 AI 批量操作。

## 11. 自动化测试并接入 CI（P2）

**方案**：
- 新增 `Services.Tests`（xUnit），覆盖可在 Linux / Windows 上运行的纯逻辑：
  参数合并、日志行格式与解析、ANSI 解析与去除、CLI 参数解析、配置导入导出的序列化等。
- 构建工作流增加 `dotnet test` 步骤。

---

## 工作方式记录

本轮由主代理负责架构设计、耦合度高的核心改动（包装器生命周期、账户、包装器拆分）与全部审核；
相对独立的改动交由子代理在独立 git worktree 中实现，主代理审核后合入，保证风格与约定一致。
