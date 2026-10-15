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
| 2 | 优雅停止（Ctrl+C / 自定义停止命令 + 超时强制结束） | P0 必须 | 已完成 |
| 3 | 放弃重启时以失败状态退出，触发 SCM 恢复策略 | P0 必须 | 已完成 |
| 4 | 运行账户（LocalSystem / LocalService / NetworkService / 虚拟账户 / 指定用户） | P1 生产 | 已完成 |
| 5 | 服务级环境变量 | P1 生产 | 已完成 |
| 6 | 日志：行时间戳、总大小上限、`logs --follow` | P1 生产 | 已完成 |
| 7 | 运行状态：启动时间、上次退出码、重启次数 | P1 生产 | 已完成 |
| 8 | 服务依赖与延迟自动启动 | P2 增强 | 已完成 |
| 9 | 独立轻量包装器（并移除多余的 WinForms 引用） | P2 增强 | 已完成 |
| 10 | 配置导出 / 声明式应用（`export` / `apply`） | P2 增强 | 已完成 |
| 11 | 自动化测试并接入 CI | P2 增强 | 已完成 |

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

**已知限制（后续加固）**：变量值以明文保存在 `Services\<ID>\Parameters`，该键默认对本机普通用户可读，
且会显示在 GUI 详情与 `get` 输出中。存放密钥时建议后续收紧 `Parameters` 键的 ACL
（仅 SYSTEM、Administrators 与服务运行账户可读），或改用 DPAPI 加密存储。

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

# 第二阶段

第一阶段 11 项已全部完成（见上）。第二阶段以“先验证、再整理、后扩展”为原则：
先用 Windows 端到端测试为第一阶段的行为兜底，再整理代码结构，最后增加新功能。

## 第二阶段总览

| # | 项目 | 优先级 | 状态 |
|---|---|---|---|
| A | Windows 端到端集成测试并接入 CI | P0 | 已完成 |
| B | 包装器生命周期状态机（消除启动 / 重启 / 停止竞态） | P0 | 已完成 |
| C | 受保护的版本化包装器目录（升级免停服务、自动清理、修复提权风险） | P0 | 已完成 |
| R | 代码结构整理（含 J：UI 与应用层解耦） | P1 | 已完成 |
| D | 环境变量等敏感配置的保护 | P1 | 已完成 |
| E | `servicesctl doctor` 诊断命令 | P1 | 已完成 |
| F | 资源限制（内存 / CPU，基于 Job Object） | P1 | 已完成 |
| G | 健康检查与看门狗 | P1 | 已完成 |
| H | 失败通知（Webhook） | P1 | 待办 |
| I | GUI 补齐新功能（导入导出、搜索筛选、运行时长列、批量操作） | P2 | 待办 |
| K | 启动前 / 停止后钩子命令 | P2 | 待办 |
| L | 常驻管理服务 + 本地接口（按需） | P2 | 待办 |
| M | 文档体系（CLI 参考自动生成） | P1 | 已完成 |
| N | 发布工程：合并 main、版本发布、代码签名、贡献上游 | P1 | 待办 |

建议顺序：A → B → C → R → D → E → M → F → G → H → I → K →（按需）L；N 穿插在里程碑节点。

## A. Windows 端到端集成测试（P0）

**现状**：389 个单元测试只覆盖纯逻辑；服务生命周期、账户权限、Job Object 等行为仅经过编译验证。

**方案**：
- 新增测试用程序 `Services.TestTarget`（控制台）：按参数打印 stdout / stderr、按指定退出码退出、
  响应 Ctrl+C 并记录、可派生子进程、可模拟卡死。
- 新增集成测试工程（仅在 Windows 且具备管理员权限时运行，否则跳过），通过 `servicesctl --json` 驱动，
  在 GitHub Actions 的 Windows 构建机上执行（构建机具备管理员权限，可真实创建服务）：
  创建 / 启动 / 停止、kill 与 ctrl-c 与 command 三种停止方式、崩溃自动重启与放弃后 SCM 恢复、
  结束包装器后子进程随之退出（Job Object）、虚拟账户运行与写日志、日志时间戳与 `logs --since`、
  环境变量与依赖生效、`export` / `apply` 幂等、`migrate`。
- 测试结束清理所有创建的服务。

**验收**：CI 中集成测试全部通过；第一阶段“需实机验证”的清单全部被覆盖。

## B. 包装器生命周期状态机（P0）

**现状**：重启通过不可取消的 `Task.Delay` 续延调度；停止与重启并发时存在窗口期；
`StopTarget` 多次读取 `_process`，可能与重启替换交错。

**方案**：将包装器拆为明确的状态（Starting / Running / BackingOff / Stopping / Stopped / Failed），
所有状态转换在同一把锁内完成；重启等待使用可取消的令牌，停止时取消；
重启策略、停止策略、运行状态记录拆分为独立组件（与 R 的拆分方向一致）。

**验收**：A 中新增“退避等待期间停止”“停止期间进程退出”等并发场景测试并通过。

## C. 受保护的版本化包装器目录（P0）

**现状**：服务运行时锁定 `servicesvc.exe`，MSI 升级需停止全部服务或重启系统；
另外服务以 SYSTEM 运行包装器，若程序被解压在普通用户可写的目录，存在本地提权风险。

**方案**：
- MSI / zip 中的 `servicesvc.exe` 作为模板，服务不直接运行它。
- `migrate`（ServicesApp 启动时自动执行）将模板复制到 `%ProgramData%\ServicesApp\wrapper\<版本>\servicesvc.exe`，
  目录设置显式 ACL：SYSTEM、Administrators 完全控制，其余用户仅读取与执行，禁止继承；服务 ImagePath 指向该副本。
- 清理：`migrate` 时删除不再被任何服务引用、且未被占用的旧版本目录；被占用则跳过，待服务重启后的下次清理删除。
  稳定后只保留当前版本。
- MSI 卸载时删除整个 `wrapper` 目录。

**实施调整**：
- 卸载时**不删除** `wrapper` 目录：卸载程序但保留的服务正是依赖该目录继续运行，删除会使它们全部无法启动。
  删除全部服务后执行一次 `migrate`（没有服务时不部署、只清理）即可清空。
- 版本目录名为 `<文件版本>-<内容哈希前 12 位>`，同一版本号的不同构建互不覆盖；复用前校验哈希。
- 目录所有者设为 Administrators（否则预先创建该目录的普通用户作为所有者仍可改写 ACL）；
  目录若是联接点则先删除再创建。
- 刚部署 10 分钟内的目录不清理，避免与另一进程正在进行的创建服务相互干扰。
- MSI 安装 / 升级后自动执行 `servicesctl migrate`。

**验收**：服务运行中升级 MSI 无需停服务、无需重启系统；所有服务重启并执行一次 `migrate` 后，
`wrapper` 下只剩当前版本；普通用户无法修改包装器文件。

## R. 代码结构整理（P1，含 J：UI 与应用层解耦）

**现状（技术债）**：`WindowsServiceManager`、`EmbeddedServiceWrapper`、`CliApp` 职责过多、文件过大；
GUI 与 CLI 存在重复的展示文本函数；注册表字段名为分散的字符串字面量；状态以中文字符串在 Core 中流转；
表单使用硬编码 Grid 行号；缺少 `.editorconfig` / `.gitattributes`，换行符混用。

**方案**：
- Core：拆分为 SCM 访问（P/Invoke 封装）、服务参数存储（注册表字段常量集中管理）、账户 / 依赖 / 迁移等独立组件；
  状态改为枚举，中文文本仅在展示层生成；新增应用层门面供 GUI 与 CLI 共同调用（方案 A）。
- 展示文本（启动类型、重启策略、停止方式、运行时长等）集中到一处，GUI 与 CLI 共用。
- CLI：每个命令一个文件。
- GUI：表单改用按顺序追加的行构建方式，消除硬编码行号；MainWindow 代码按职责拆分。
- 新增 `.editorconfig` 与 `.gitattributes`，在单独的提交中统一换行符（不夹带逻辑改动，便于审计）。

**验收**：行为不变（A 的集成测试与全部单元测试通过）；单文件不超过约 500 行；GUI 不再直接依赖中文状态字符串。

**完成情况**（5 个提交）：
- 换行符：`.editorconfig` / `.gitattributes`，单独提交统一 `WindowsServiceManager.cs` 的换行符。
- Core：`Scm/`（SCM 封装）、`Storage/`（注册表路径与值名常量、服务参数读写、服务索引）、
  `Presentation/`（展示文本与详情行，GUI 与 CLI 共用）、`Application/`（应用层门面 `ServiceOperations`、
  `ConfigApplier`）、`Configuration/`（配置文件模型与计划，自 CLI 移入）；`WindowsServiceManager` 985 → 485 行。
- 运行状态改为 `ServiceRunState` 枚举，中文文本只在 `Presentation` 中生成。
- CLI：`CliApp.cs` 1001 → 171 行，每个命令一个文件。
- GUI：表单改用按顺序追加行的 `FormRows`；`MainWindow` 按职责拆分为 5 个部分类文件（最大 440 行）。
- 方案 A（应用层门面）已落地：更换界面时只需调用 `ServiceOperations` / `ConfigApplier` 与 `Presentation`。

## D. 敏感配置保护（P1）

收紧 `Services\<ID>\Parameters` 的 ACL（SYSTEM、Administrators、服务运行账户可读），
或对标记为敏感的环境变量使用 DPAPI（LocalMachine 范围）加密存储；GUI / CLI 默认遮蔽敏感值，`--show-secrets` 显式查看。

**实施**：采用“敏感变量 + 受保护子键”而非收紧整个 Parameters：
- 整个 Parameters 只允许管理员读取会使 `servicesctl list / get` 无法以普通权限运行；因此只把标记为敏感的变量
  放入 `Parameters\Secrets`（禁止继承，SYSTEM / Administrators 完全控制，服务运行账户只读，账户变更时同步更新 ACL），
  变量名仍公开记录在 `SecretEnvironmentNames`。
- 未使用 DPAPI：LocalMachine 范围的 DPAPI 本机任何进程都能解密，起不到额外的访问控制作用，ACL 才是实际的保护。
- CLI：`--secret-env KEY=VALUE`；`get` / `list --json` / `export` 默认遮蔽，`--show-secrets` 显示；
  配置文件 `secretEnvironment` 中值为 null 表示沿用已保存的值。GUI：独立的“敏感环境变量”输入框，已保存的值显示为 `******`。

## E. `servicesctl doctor`（P1）

逐项检查并给出修复建议：运行账户对程序 / 工作目录的读取权限、对日志与状态目录的写权限、
包装器路径与版本、依赖服务是否存在、可执行文件是否存在、注册表参数是否完整、事件日志源是否注册。
支持 `--json`，便于 AI 自动排查。

## F. 资源限制（P1）

基于已有 Job Object：进程内存上限、Job 内存上限、CPU 占用上限（硬上限或权重）、进程数上限。
注册表、GUI、CLI、`export` / `apply` 同步支持。

## G. 健康检查与看门狗（P1）

周期性探测：HTTP（状态码）、TCP 端口、自定义命令（退出码）；连续失败 N 次判定为不健康并按重启策略重启；
启动宽限期；运行状态中记录最近一次检查结果。

## H. 失败通知（P1）

放弃重启、健康检查失败、服务异常停止时向配置的 Webhook POST JSON（服务、事件、原因、时间、主机名）；
支持全局与服务级配置、超时与重试；不影响服务本身的运行。

## I. GUI 补齐新功能（P2）

导入 / 导出配置（复用 export / apply 逻辑，含变更预览）、列表搜索与筛选、运行时长列、多选批量启动 / 停止 / 重启。

## K. 钩子命令（P2）

启动前（失败则不启动）与停止后执行的命令，对应 systemd `ExecStartPre=` / `ExecStopPost=`，输出写入日志。

## L. 常驻管理服务 + 本地接口（P2，按需）

以 SYSTEM 运行的管理服务，通过命名管道（严格 ACL）提供接口并推送状态变化；GUI / CLI 作为客户端，
GUI 无需管理员权限、可完全退出。仅在需要非管理员 GUI、Web 界面或远程管理时实施。

## M. 文档体系（P1）

`README.md`（简介、安装、快速开始）、`docs/cli.md`（由 `servicesctl` 内置帮助自动生成，CI 校验与程序一致）、
`docs/configuration.md`（注册表字段与配置文件格式）、`docs/architecture.md`、`docs/troubleshooting.md`、
`CHANGELOG.md`、`CONTRIBUTING.md`。

## N. 发布工程（P1）

- 功能分支经 PR 合并到 main，打版本标签触发发布流程，生成 CHANGELOG。
- 代码签名：在发布流程中使用 `signtool` 对 exe / msi 签名并加时间戳；证书来源待定
  （开源免费签名计划、云签名服务或个人开源证书），私钥不入仓库。
- 贡献上游：先与上游维护者沟通，按主题（界面修复、包装器可靠性、CLI）拆分为独立 PR。

---

## 工作方式记录

第一阶段由主代理负责架构设计、耦合度高的核心改动（包装器生命周期、账户、包装器拆分）与全部审核；
相对独立的改动交由子代理在独立 git worktree 中实现，主代理审核后合入，保证风格与约定一致。

第一阶段回顾：
- 子代理完成 5 项（#5 #6 #7 #8 #11），其中 4 项在审核中修正或补充后合入；主代理完成 6 项。
- 审核发现的重要问题：包装器 Exited 事件访问已释放进程导致崩溃并被 SCM 重新拉起、依赖更新丢失依赖组、
  apply 工作目录不幂等、CLI `/?` 失效、ANSI 截断序列误解析等。
- 由于多数改动集中在同几处（模型字段、表单、CLI 选项），可并行度有限；详细的任务说明（预先确定设计、
  命名、换行风格、提交格式）是保证一致性的关键。第二阶段沿用此方式，R 完成后可并行度预计提高。
