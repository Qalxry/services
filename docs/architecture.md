# 架构

## 组成

```
┌──────────────┐   ┌──────────────┐
│ ServicesApp  │   │ servicesctl  │      界面与命令行：管理端，需要管理员权限修改服务
│ (WinUI 3)    │   │ (控制台)      │
└──────┬───────┘   └──────┬───────┘
       │  Services.Core（应用层门面 + 领域逻辑）
       ▼                  ▼
┌─────────────────────────────────────┐
│ Windows 服务控制管理器（SCM）、注册表 │
└──────────────────┬──────────────────┘
                   │ 启动服务：ImagePath = "<wrapper>" --service-wrapper "<ID>"
                   ▼
            ┌──────────────┐  Job Object  ┌────────────┐
            │ servicesvc   │─────────────▶│ 目标程序    │ stdout / stderr → 日志文件
            │ （包装器）     │              │ 及其子进程   │
            └──────────────┘              └────────────┘
```

- **ServicesApp**（`ServicesApp/`）：WinUI 3 图形界面、托盘、日志查看器。
- **servicesctl**（`Services.Cli/`）：命令行工具，所有命令支持 `--json`，面向脚本与 AI。
- **servicesvc**（`Services.Wrapper/`）：服务包装器。服务实际运行的是它，由它启动并监管目标程序；
  自包含、裁剪的单文件程序（约 12 MB），与界面完全无关，关闭或升级界面不影响服务。
- **Services.Core**：上述三者共用的全部逻辑。

管理端与包装器之间没有进程间通信：管理端把配置写入注册表并通过 SCM 控制服务，
包装器启动时读取配置，并把运行状态写入状态文件、把输出写入日志文件，管理端再读取这些文件。

## Services.Core 分层

| 目录 | 职责 | 依赖 |
|---|---|---|
| `Models/` | 服务模型与枚举（`Service`、`ServiceConfig`、`ServiceRunState`、默认值） | 无 |
| `Helpers/` | 纯逻辑与小型系统封装：参数、环境变量、依赖、日志行、ANSI、账户、Job Object、Ctrl+C | Models |
| `Scm/` | SCM 的 Win32 声明（`ScmNative`）与托管封装（`ScmClient`） | Models |
| `Storage/` | 注册表路径与值名常量、服务参数读写（`ServiceRegistryStore`）、敏感变量（`SecretStore`） | Models、Helpers |
| `Services/` | `WindowsServiceManager`（缓存与编排）、日志与状态文件、包装器服务主体 | 以上全部 |
| `Wrapper/` | 包装器内部组件：状态机、重启计数、停止方式、配置读取、运行状态记录、受保护部署 | Models、Helpers、Storage、Scm |
| `Configuration/` | 声明式配置文件：模型、映射、差异、变更计划 | Models、Helpers |
| `Diagnostics/` | `servicesctl doctor` 的检查项 | 以上全部 |
| `Application/` | **应用层门面**：`ServiceOperations`（查找、启停并等待结果、运行状态）、`ConfigApplier` | 以上全部 |
| `Presentation/` | 中文展示文本（状态、策略、时长、详情行），GUI 与 CLI 共用 | Models、Helpers |

约定：

- 界面（GUI / CLI）通过 `Application` 调用核心功能，用 `Presentation` 生成文本；
  更换或新增界面（如 Web 界面）时只需调用这两层。
- 除 `Presentation` 与面向用户的错误消息外，Core 只使用枚举与数值，不依赖中文文本。
- 失败以带结构化信息的异常报告（`ServiceStartException` 等），由界面决定如何呈现（CLI 映射为退出码）。
- 纯逻辑（解析、差异、状态机、权限判断）不依赖 Windows API，在 `Services.Tests` 中于 Linux / Windows 上测试。

## 包装器生命周期

`EmbeddedServiceWrapper` 只负责编排，决策由纯逻辑的 `SupervisorStateMachine` 给出：

```
Idle ──BeginInitialStart──▶ Starting ──ProcessStarted──▶ Running
                              │                          │ 退出码 0 → Exited（正常停止服务）
                  启动失败 ───┤                          │ 非 0 且未启用自动重启 → Exited（以失败状态停止）
                              ▼                          ▼ 非 0 且允许重启
                         BackingOff ◀────────────────── ScheduleRestart（等待 delay，可取消）
                              │ TryBeginRestart（代数匹配且仍在等待）
                              ▼
                           Starting …

任意状态 ──BeginStop──▶ Stopping ──▶ Stopped（之后到达的退出 / 重启事件一律忽略）
```

- 每次启动分配递增的**代数**，过期的退出事件、重复的重启定时都因代数不匹配而被忽略。
- 状态转换与目标程序的启动在同一把锁内完成；停止进程、等待输出与调用 `ServiceBase.Stop` 在锁外进行。
- 首次启动即失败时在 `OnStart` 中抛出，由 `ServiceBase` 以失败状态结束启动；
  目标程序在启动阶段就退出时，等 SCM 收到“运行中”后再停止服务，避免两次状态报告相互覆盖。
- 以失败状态停止时报告非 0 退出码（1067 / 2），配合 `FailureActionsOnNonCrashFailures`，
  由 SCM 的恢复策略稍后重新拉起服务。

### 进程与停止

- 包装器启动时把自己加入设置了 `KILL_ON_JOB_CLOSE` 的 Job Object，目标程序及其子进程继承该 Job：
  包装器以任何方式退出（包括被强制结束），整个进程树都会被系统结束。
- 停止方式：结束进程树（kill）；向目标程序的隐藏控制台发送 Ctrl+C（ctrl-c）；在工作目录中通过
  `cmd.exe` 执行停止命令（command）。后两者超时后结束进程树；等待期间向 SCM 申请延长停止时间。
- 进程退出后先读完剩余输出再记录退出，日志关闭时写完队列中的内容。
- 资源限制（内存、CPU 硬上限、进程数）施加在另一个只包含目标程序的 Job 上（嵌套在上述 Job 之内），
  包装器自身不受限制；目标程序启动后立即加入，此后创建的子进程同样受限。

## 测试

| 工程 | 内容 | 运行环境 |
|---|---|---|
| `Services.Tests` | 纯逻辑单元测试（约 450 个），以及 `docs/cli.md` 与内置帮助一致性检查 | Linux / Windows |
| `Services.IntegrationTests` | 端到端测试：通过 `servicesctl --json` 真实创建并操作服务 | Windows + 管理员（CI） |
| `Services.TestTarget` | 集成测试用的目标程序（输出、退出码、Ctrl+C、子进程等） | — |

集成测试覆盖生命周期、三种停止方式、崩溃重启与放弃、Job Object、虚拟账户、环境变量与敏感变量、
依赖、导出 / 应用、迁移与受保护的包装器目录、doctor。
