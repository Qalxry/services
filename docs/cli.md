# servicesctl 命令参考

> 本文件由 `Services.Tests/CliReferenceTests.cs` 根据 servicesctl 的内置帮助自动生成，请勿手工编辑。
> 修改帮助文本（`Services.Cli/HelpText.cs`）后，在仓库根目录执行
> `UPDATE_DOCS=1 dotnet test Services.Tests --filter CliReference` 重新生成；CI 会校验二者一致。

## 目录

- [总览](#总览)
- [list](#list) — 列出所有由 ServicesApp 管理的服务
- [get](#get) — 查看单个服务的完整配置与状态
- [create](#create) — 创建新服务
- [update](#update) — 修改已有服务的配置（只修改指定的字段，其余保持不变）
- [delete](#delete) — 停止并删除服务（不可撤销，不会询问确认）
- [start](#start) — 启动服务并等待其进入运行状态
- [stop](#stop) — 停止服务（最多等待 30 秒）
- [restart](#restart) — 重启服务（先停止再启动）
- [logs](#logs) — 查看服务最新日志文件的末尾内容，或实时跟踪新日志
- [export](#export) — 导出服务配置为 JSON（可用于备份、版本管理，或修改后通过 apply 应用）
- [apply](#apply) — 按配置文件声明式地创建 / 更新（/ 删除）服务
- [doctor](#doctor) — 诊断服务能否正常运行，并给出修复建议
- [migrate](#migrate) — 将所有服务切换到当前版本的包装器，并清理旧版本
- [version](#version) — 显示版本号
- [help](#help) — 显示帮助

## 总览

```text
servicesctl - ServicesApp 命令行工具：管理由 ServicesApp 创建的 Windows 服务

用法:
  servicesctl <命令> [参数] [选项]
  servicesctl help <命令>        查看某个命令的完整帮助（等同于 servicesctl <命令> --help）

命令:
  list      列出所有由 ServicesApp 管理的服务
  get       查看单个服务的完整配置与状态
  create    创建新服务
  update    修改已有服务的配置（只修改指定的字段，其余保持不变）
  delete    停止并删除服务（不可撤销，不会询问确认）
  start     启动服务并等待其进入运行状态
  stop      停止服务（最多等待 30 秒）
  restart   重启服务（先停止再启动）
  logs      查看服务最新日志文件的末尾内容，或实时跟踪新日志
  export    导出服务配置为 JSON（可用于备份、版本管理，或修改后通过 apply 应用）
  apply     按配置文件声明式地创建 / 更新（/ 删除）服务
  doctor    诊断服务能否正常运行，并给出修复建议
  migrate   将所有服务切换到当前版本的包装器，并清理旧版本
  version   显示版本号
  help      显示帮助

全局选项:
  --json        以 JSON 输出结果（推荐脚本 / AI 使用，字段说明见各命令帮助）
  -h, --help    显示帮助

退出码:
  0  成功
  1  执行失败（如系统 API 调用失败、服务启动超时）
  2  参数错误
  3  未找到服务
  4  需要管理员权限

说明:
  - 所有命令均为非交互式，不会弹出确认或等待输入；错误信息输出到 stderr。
  - list / get / logs 为只读命令，普通权限即可运行；其余命令会修改系统服务，
    需要在“以管理员身份运行”的终端中执行，否则返回退出码 4。
  - 只管理由 ServicesApp 创建的服务（GUI 与 CLI 创建的服务完全相同）。
  - 服务由包装器 servicesvc.exe 运行并监管目标程序，servicesctl.exe 需与它位于同一目录，
    否则 create 会失败。旧版本创建的服务可用 'servicesctl migrate' 迁移到新包装器。
  - GUI 正在运行时，命令行所做的修改会在 GUI 窗口获得焦点时自动刷新显示。

示例:
  servicesctl list --json
  servicesctl create --name MyApp --exe "C:\Apps\my-app.exe" --args "--port 8080" --auto-restart --start
  servicesctl update MyApp --max-restarts 0 --restart
  servicesctl logs MyApp --tail 50 --stream stderr
  servicesctl logs MyApp -f
  servicesctl delete MyApp
  servicesctl export -o services.json      # 导出全部配置，修改后用 apply -f services.json 应用
```

## list

```text
servicesctl list - 列出所有由 ServicesApp 管理的服务

用法:
  servicesctl list [--json] [--show-secrets]

输出:
  默认输出表格：ID、名称、状态、PID、启动类型、程序路径（按添加顺序排列）。
  --json 时输出服务对象数组，每个对象的字段见 'servicesctl help get'。
  --show-secrets 时 JSON 中包含敏感环境变量的值（需要管理员权限才能读取）。

权限: 普通权限即可。
```

## get

```text
servicesctl get - 查看单个服务的完整配置与状态

用法:
  servicesctl get <服务> [--json] [--show-secrets]

选项:
  --show-secrets        显示敏感环境变量的值（默认遮蔽；需要管理员权限才能读取）

参数:
  <服务>                服务 ID（如 WinSvcMgr_MyApp_1a2b3c4d）或服务名称，不区分大小写。
                        名称匹配到多个服务时报错，此时请改用 ID。

JSON 字段:
  id                服务 ID（Windows 服务名）
  name              服务名称（显示名称）
  description       描述
  state             running | stopped | start_pending | stop_pending | unknown
  statusText        状态的中文描述
  pid               进程 ID（ServicesApp 包装器进程），未运行时为 null
  startupType       auto | delayed-auto（延迟自动启动）| manual | disabled
  exePath           可执行文件路径
  args              保存的原始启动参数（可能为多行）
  commandLineArgs   实际传给程序的命令行参数（多行已合并）
  workingDir        工作目录
  autoRestart       是否启用失败自动重启
  restartPolicy     { delaySeconds, maxRestarts (0=不限), resetMinutes, backoff }
  stopPolicy        { mode (kill | ctrl-c | command), command, timeoutSeconds }
  account           { type (localsystem | localservice | networkservice | virtual | user), name }
  resourceLimits    资源限制 { memoryMB, processMemoryMB, cpuPercent, maxProcesses }，0 表示不限制
  environment       服务级环境变量对象 { 变量名: 值 }（按变量名排序，值为未展开的原始文本），未设置时为 {}
  secretEnvironment 敏感环境变量对象 { 变量名: 值 }，值默认为 null（遮蔽），--show-secrets 时为真实值；
                    无权读取（非管理员）时同样为 null。未设置时为 {}
  dependencies      依赖的服务名数组（本服务启动前需先启动的 Windows 服务），无依赖时为 []
  createdAt         创建时间（ISO 8601）
  latestLogPath     最新日志文件路径，尚无日志时为 null
  runtime           包装器记录的运行状态对象，尚无状态文件时为 null；字段:
                      wrapperPid         包装器进程 ID
                      wrapperStartedAt   包装器启动时间（ISO 8601，本地时间）
                      processPid         目标进程 ID，目标进程未运行（已退出 / 等待重启 / 服务已停止）时为 null
                      processStartedAt   目标进程启动时间，同上
                      uptimeSeconds      服务运行时长（秒）；仅服务运行中且状态属于当前服务进程时有值，否则为 null
                      restartCount       当前计数窗口内的重启次数（超过重置时间后清零）
                      totalRestarts      本次包装器运行以来的重启总次数
                      lastExitCode       目标进程最近一次的退出码，从未退出过为 null
                      lastExitAt         目标进程最近一次的退出时间
                      lastFailureReason  包装器最近一次以失败状态停止的原因，没有时为 null
                      lastFailureAt      记录该失败原因的时间，没有时为 null
                      updatedAt          状态最后更新时间

权限: 普通权限即可。
```

## create

```text
servicesctl create - 创建新服务

用法:
  servicesctl create --name <名称> --exe <路径> [选项] [--start] [--json]

必需选项:
  --name <名称>         服务名称，只能包含字母、数字、空格、下划线和短横线（支持中文）。
                        服务 ID 会根据名称自动生成：WinSvcMgr_<名称中的字母数字>_<8 位随机串>
  --exe <路径>          可执行文件路径（相对路径会转换为绝对路径，文件必须存在）

可选选项:
  --workdir <目录>      工作目录；不指定时默认为程序所在目录
  --args <参数>         启动参数，原样作为一个字符串传入，例如 --args "--port 8080 -v"。
                        可包含换行，启动时各行去除首尾空白、忽略空行后以空格拼接。
  --description <文本>  服务描述；系统中显示为 "Managed by Windows Service Manager: <描述>"，
                        为空时使用服务名称
  --startup <类型>      启动类型：auto（开机自启）| delayed-auto（延迟自动启动：开机后稍晚启动，
                        减轻开机负载）| manual（手动）| disabled（禁用）
  --auto-restart        启用失败自动重启（进程以非 0 退出码退出时重启）
  --no-auto-restart     关闭失败自动重启
  --restart-delay <秒>  首次重启前的等待时间，1-3600，默认 5
  --max-restarts <次>   计数窗口内的最大重启次数，0-1000，0 表示不限制，默认 5。
                        超过后服务以失败状态停止并写入 Windows 事件日志，约 1 分钟后由系统
                        恢复策略重新拉起（24 小时内最多 3 次）
  --restart-reset <分>  距上次重启超过该分钟数则重新计数，1-1440，默认 10
  --backoff             启用指数退避（每次重启延迟翻倍，最多 16 倍），默认启用
  --no-backoff          关闭指数退避，每次使用固定延迟
  --stop-mode <方式>    停止服务时结束程序的方式，默认 kill：
                          kill     直接结束进程树
                          ctrl-c   向程序控制台发送 Ctrl+C（Node / Python / Go 等），超时后强制结束
                          command  执行 --stop-command 指定的命令，超时后强制结束
  --stop-command <命令> 停止命令，在工作目录中通过 cmd 执行。占位符：{exe} 程序路径、
                        {workdir} 工作目录（均自动加引号）、{pid} 进程 ID。
                        例：nginx 用 "{exe} -s quit"（若启动参数含 -p / -c，停止命令需带相同参数）
  --stop-timeout <秒>   优雅停止的最长等待时间，1-3600，默认 30。系统关机时最多等待 4 秒
  --memory-limit <MB>   目标程序进程树（含子进程）的内存上限，超出时内存分配失败；0 表示不限制
  --process-memory-limit <MB>
                        单个进程的内存上限；0 表示不限制
  --cpu-limit <百分比>  CPU 占用硬上限（占全部 CPU 的百分比，1-100）；0 表示不限制
  --max-processes <数>  同时运行的进程数上限（含目标程序自身），超出时创建进程失败；0 表示不限制
                        以上限制通过 Job Object 施加于目标程序及其子进程，包装器自身不受限制；
                        目标程序启动后立即受限，在此之前（启动瞬间）创建的子进程不受限
  --account <账户>      运行账户，默认 localsystem（权限最高）：
                          localsystem | localservice | networkservice
                          virtual  虚拟账户 NT SERVICE\<服务ID>，每个服务独立、无需密码（推荐的低权限方案）
                          其他值视为用户名（.\user 或 DOMAIN\user），需配合 --password-stdin，
                          并会自动授予“作为服务登录”权限、校验密码
                        非 localsystem 账户需能读取程序与工作目录；日志目录写权限会自动授予
  --password-stdin      从标准输入读取用户密码（一行），避免密码出现在命令行与历史记录中。
                        更新时仅在切换为该用户或更换用户名时必需，否则保持原密码
  --env <KEY=VALUE>     为程序设置环境变量，可重复指定；叠加在服务继承的系统环境之上，同名时覆盖。
                        值中的 %VAR% 会在启动时展开，例如 --env "PATH=%PATH%;C:\tools"；
                        值可为空（KEY=）。更新时与已有变量合并，同名者被覆盖
  --secret-env <KEY=VALUE>
                        设置敏感环境变量（如密钥、口令），可重复指定。值保存在只有 SYSTEM、管理员与
                        服务运行账户可读的位置，get / export 默认不显示值；变量名与 --env 共用
  --unset-env <KEY>     移除该服务已设置的某个环境变量（普通或敏感），可重复指定
  --clear-env           清空该服务已设置的所有环境变量（普通与敏感）
                        同一命令中的处理顺序：--clear-env → --unset-env → --env → --secret-env
  --depends-on <服务名> 本服务依赖的服务，启动本服务前系统会先启动它，可重复指定。填写 Windows 服务名
                        （如 Tcpip、MySQL80，可以是任意服务，或本工具创建的服务 ID），服务必须已存在。
                        更新时以给定列表整体替换原有依赖；不支持依赖组
  --clear-depends       清空该服务的所有依赖（不能与 --depends-on 同时使用）
  --start               创建后立即启动并等待其进入运行状态（最多 30 秒）

默认值:
  启动类型 auto；不自动重启；重启策略 5 秒 / 5 次 / 10 分钟 / 指数退避；停止方式 kill；无自定义环境变量；无依赖服务。

输出:
  成功后输出新服务的信息；--json 时输出 { ok, action, id, message, service }，
  service 字段同 'servicesctl help get'。

权限: 需要管理员。

示例:
  servicesctl create --name MyApp --exe "C:\Apps\my-app.exe"
  servicesctl create --name "Node API" --exe "C:\nodejs\node.exe" --args "server.js" --workdir "D:\api" --auto-restart --max-restarts 0 --start --json
```

## update

```text
servicesctl update - 修改已有服务的配置（只修改指定的字段，其余保持不变）

用法:
  servicesctl update <服务> [选项] [--restart] [--json]

参数:
  <服务>                服务 ID（如 WinSvcMgr_MyApp_1a2b3c4d）或服务名称，不区分大小写。
                        名称匹配到多个服务时报错，此时请改用 ID。

选项（至少指定一项）:
  --name <名称>         修改服务名称（显示名称），服务 ID 不变
  --exe <路径>          可执行文件路径（相对路径会转换为绝对路径，文件必须存在）
  --workdir <目录>      工作目录；不指定时默认为程序所在目录
  --args <参数>         启动参数，原样作为一个字符串传入，例如 --args "--port 8080 -v"。
                        可包含换行，启动时各行去除首尾空白、忽略空行后以空格拼接。
  --description <文本>  服务描述；系统中显示为 "Managed by Windows Service Manager: <描述>"，
                        为空时使用服务名称
  --startup <类型>      启动类型：auto（开机自启）| delayed-auto（延迟自动启动：开机后稍晚启动，
                        减轻开机负载）| manual（手动）| disabled（禁用）
  --auto-restart        启用失败自动重启（进程以非 0 退出码退出时重启）
  --no-auto-restart     关闭失败自动重启
  --restart-delay <秒>  首次重启前的等待时间，1-3600，默认 5
  --max-restarts <次>   计数窗口内的最大重启次数，0-1000，0 表示不限制，默认 5。
                        超过后服务以失败状态停止并写入 Windows 事件日志，约 1 分钟后由系统
                        恢复策略重新拉起（24 小时内最多 3 次）
  --restart-reset <分>  距上次重启超过该分钟数则重新计数，1-1440，默认 10
  --backoff             启用指数退避（每次重启延迟翻倍，最多 16 倍），默认启用
  --no-backoff          关闭指数退避，每次使用固定延迟
  --stop-mode <方式>    停止服务时结束程序的方式，默认 kill：
                          kill     直接结束进程树
                          ctrl-c   向程序控制台发送 Ctrl+C（Node / Python / Go 等），超时后强制结束
                          command  执行 --stop-command 指定的命令，超时后强制结束
  --stop-command <命令> 停止命令，在工作目录中通过 cmd 执行。占位符：{exe} 程序路径、
                        {workdir} 工作目录（均自动加引号）、{pid} 进程 ID。
                        例：nginx 用 "{exe} -s quit"（若启动参数含 -p / -c，停止命令需带相同参数）
  --stop-timeout <秒>   优雅停止的最长等待时间，1-3600，默认 30。系统关机时最多等待 4 秒
  --memory-limit <MB>   目标程序进程树（含子进程）的内存上限，超出时内存分配失败；0 表示不限制
  --process-memory-limit <MB>
                        单个进程的内存上限；0 表示不限制
  --cpu-limit <百分比>  CPU 占用硬上限（占全部 CPU 的百分比，1-100）；0 表示不限制
  --max-processes <数>  同时运行的进程数上限（含目标程序自身），超出时创建进程失败；0 表示不限制
                        以上限制通过 Job Object 施加于目标程序及其子进程，包装器自身不受限制；
                        目标程序启动后立即受限，在此之前（启动瞬间）创建的子进程不受限
  --account <账户>      运行账户，默认 localsystem（权限最高）：
                          localsystem | localservice | networkservice
                          virtual  虚拟账户 NT SERVICE\<服务ID>，每个服务独立、无需密码（推荐的低权限方案）
                          其他值视为用户名（.\user 或 DOMAIN\user），需配合 --password-stdin，
                          并会自动授予“作为服务登录”权限、校验密码
                        非 localsystem 账户需能读取程序与工作目录；日志目录写权限会自动授予
  --password-stdin      从标准输入读取用户密码（一行），避免密码出现在命令行与历史记录中。
                        更新时仅在切换为该用户或更换用户名时必需，否则保持原密码
  --env <KEY=VALUE>     为程序设置环境变量，可重复指定；叠加在服务继承的系统环境之上，同名时覆盖。
                        值中的 %VAR% 会在启动时展开，例如 --env "PATH=%PATH%;C:\tools"；
                        值可为空（KEY=）。更新时与已有变量合并，同名者被覆盖
  --secret-env <KEY=VALUE>
                        设置敏感环境变量（如密钥、口令），可重复指定。值保存在只有 SYSTEM、管理员与
                        服务运行账户可读的位置，get / export 默认不显示值；变量名与 --env 共用
  --unset-env <KEY>     移除该服务已设置的某个环境变量（普通或敏感），可重复指定
  --clear-env           清空该服务已设置的所有环境变量（普通与敏感）
                        同一命令中的处理顺序：--clear-env → --unset-env → --env → --secret-env
  --depends-on <服务名> 本服务依赖的服务，启动本服务前系统会先启动它，可重复指定。填写 Windows 服务名
                        （如 Tcpip、MySQL80，可以是任意服务，或本工具创建的服务 ID），服务必须已存在。
                        更新时以给定列表整体替换原有依赖；不支持依赖组
  --clear-depends       清空该服务的所有依赖（不能与 --depends-on 同时使用）
  --restart             若服务正在运行，修改后立即重启使新配置生效

说明:
  新配置在服务下次启动时生效。服务正在运行且未指定 --restart 时，会在输出中提示需要重启。
  清空可选字段可传空字符串，例如 --args "" 或 --description ""。

权限: 需要管理员。

示例:
  servicesctl update MyApp --args "--port 9090" --restart
  servicesctl update WinSvcMgr_MyApp_1a2b3c4d --startup manual --no-auto-restart
  servicesctl update nginx --stop-mode command --stop-command "{exe} -s quit" --stop-timeout 60
  servicesctl update MyApp --account virtual --restart
  servicesctl update MyApp --env NODE_ENV=production --env "PATH=%PATH%;C:\tools" --unset-env DEBUG --restart
  servicesctl update MyApp --startup delayed-auto --depends-on Tcpip --depends-on MySQL80
  "<密码>" | servicesctl update MyApp --account .\svcuser --password-stdin     (PowerShell)
```

## delete

```text
servicesctl delete - 停止并删除服务（不可撤销，不会询问确认）

用法:
  servicesctl delete <服务> [--json]

参数:
  <服务>                服务 ID（如 WinSvcMgr_MyApp_1a2b3c4d）或服务名称，不区分大小写。
                        名称匹配到多个服务时报错，此时请改用 ID。

说明:
  会先停止服务，再删除服务及其注册表配置。日志文件保留在日志目录中，按日志保留策略自动清理。

权限: 需要管理员。
```

## start

```text
servicesctl start - 启动服务并等待其进入运行状态

用法:
  servicesctl start <服务> [--timeout <秒>] [--json]

参数:
  <服务>                服务 ID（如 WinSvcMgr_MyApp_1a2b3c4d）或服务名称，不区分大小写。
                        名称匹配到多个服务时报错，此时请改用 ID。

选项:
  --timeout <秒>        等待进入运行状态的最长时间，1-600，默认 30；超时返回退出码 1

说明:
  “运行中”表示包装器已启动。等待期间服务停止（程序不存在、目标程序立即退出等）时立即返回退出码 1，
  并输出包装器记录的失败原因。目标程序稍后才退出时，服务随后会停止或按重启策略重启，
  可用 'servicesctl get' 再次确认状态，或用 'servicesctl logs' 查看原因。

权限: 需要管理员。
```

## stop

```text
servicesctl stop - 停止服务（最多等待 30 秒）

用法:
  servicesctl stop <服务> [--json]

参数:
  <服务>                服务 ID（如 WinSvcMgr_MyApp_1a2b3c4d）或服务名称，不区分大小写。
                        名称匹配到多个服务时报错，此时请改用 ID。

权限: 需要管理员。
```

## restart

```text
servicesctl restart - 重启服务（先停止再启动）

用法:
  servicesctl restart <服务> [--timeout <秒>] [--json]

参数:
  <服务>                服务 ID（如 WinSvcMgr_MyApp_1a2b3c4d）或服务名称，不区分大小写。
                        名称匹配到多个服务时报错，此时请改用 ID。

选项:
  --timeout <秒>        等待进入运行状态的最长时间，1-600，默认 30

权限: 需要管理员。
```

## logs

```text
servicesctl logs - 查看服务最新日志文件的末尾内容，或实时跟踪新日志

用法:
  servicesctl logs <服务> [--tail <行数>] [--stream <流>] [--since <时间>] [-f] [--raw] [--json]

参数:
  <服务>                服务 ID（如 WinSvcMgr_MyApp_1a2b3c4d）或服务名称，不区分大小写。
                        名称匹配到多个服务时报错，此时请改用 ID。

选项:
  --tail <行数>         输出最后多少行（过滤后计数），0 表示全部，默认 200
  --stream <流>         all（默认）| stdout | stderr；包装器自身消息（system）始终输出
  --since <时间>        只输出该时间及之后的行。支持 ISO 8601 时间（如 2026-10-15T10:20:30，
                        不带时区时按本地时间，也可带 Z 或 +08:00）或相对时长：
                        30s（秒）| 10m（分）| 2h（时）| 1d（天）。
                        没有时间戳的旧版本日志行无法判断时间，指定 --since 时不输出
  -f, --follow          输出末尾内容后持续跟踪新写入的行（每 500ms 检查一次），按 Ctrl+C 退出（退出码 0）。
                        服务重启后会生成新的日志文件，此时自动切换并在 stderr 提示
  --raw                 保留 ANSI 颜色转义序列（默认去除）

输出格式:
  文本模式下每行保持日志文件原样：行首为本地时间戳（yyyy-MM-ddTHH:mm:ss.fff），
  其后 stdout 行无前缀，stderr 行以 "[stderr] " 开头，包装器消息以 "[system] " 开头
  （如进程退出码、自动重启记录），例如:
    2026-10-15T10:20:30.123 listening on :8080
    2026-10-15T10:20:31.456 [stderr] connection refused
    2026-10-15T10:20:32.001 [system] Process exited (code: 1)
  旧版本写入的日志行没有时间戳，仍可正常显示。
  --json 时输出 { id, logPath, lines: [{ timestamp, stream, text }] }，text 已去除前缀；
  timestamp 为 ISO 8601 本地时间字符串（如 "2026-10-15T10:20:30.123"），旧格式行为 null。
  --follow --json 时改为 JSON Lines：先输出末尾内容，再持续输出，
  每行一个 { timestamp, stream, text } 对象（不含 id / logPath 外层结构）。

说明:
  日志目录: %ProgramData%\windows_service_logs，每次服务启动生成一个新文件。
  日志总大小默认上限 1 GB，超出后自动删除最旧的文件（可在 GUI 应用设置中修改）。
  尚无日志时 logPath 为 null、lines 为空，退出码仍为 0；--follow 时会等待日志生成。

示例:
  servicesctl logs MyApp --since 10m
  servicesctl logs MyApp -f --stream stderr
  servicesctl logs MyApp --follow --json --tail 0

权限: 普通权限即可。
```

## export

```text
servicesctl export - 导出服务配置为 JSON（可用于备份、版本管理，或修改后通过 apply 应用）

用法:
  servicesctl export [<服务>...] [-o <文件>] [--show-secrets]

参数:
  <服务>                要导出的服务（ID 或名称），可指定多个；省略时导出全部服务

选项:
  -o, --output <文件>   写入文件（UTF-8）；省略或为 "-" 时输出到标准输出
  --show-secrets        同时导出敏感环境变量的值（默认只导出变量名，值为 null；需要管理员权限）

输出格式（与 apply 的输入相同）:
  {
    "version": 1,
    "services": [
      {
        "name": "MyApp",                     // 必需，按名称与已有服务匹配（不区分大小写）
        "description": "...",
        "exe": "C:\Apps\my-app.exe",       // 必需
        "args": "--port 8080",
        "workingDir": "C:\Apps",
        "startup": "auto",                   // auto | delayed-auto | manual | disabled
        "account": "virtual",                // localsystem | localservice | networkservice | virtual | 用户名
        "environment": { "NODE_ENV": "production" },
        "secretEnvironment": { "API_KEY": null }, // null 表示沿用已保存的值
        "dependencies": [ "Tcpip" ],
        "autoRestart": true,
        "restartPolicy": { "delaySeconds": 5, "maxRestarts": 5, "resetMinutes": 10, "backoff": true },
        "stopPolicy": { "mode": "kill", "command": null, "timeoutSeconds": 30 },
        "resourceLimits": { "memoryMB": 512, "processMemoryMB": 0, "cpuPercent": 50, "maxProcesses": 0 }
      }
    ]
  }
  不包含服务 ID、运行状态与密码；敏感环境变量默认只含变量名。

权限: 普通权限即可。
```

## apply

```text
servicesctl apply - 按配置文件声明式地创建 / 更新（/ 删除）服务

用法:
  servicesctl apply -f <文件> [--dry-run] [--prune] [--restart] [--json]

选项:
  -f, --file <文件>     配置文件（格式见 'servicesctl help export'）；为 "-" 时从标准输入读取
  --dry-run             只输出变更计划，不做任何修改（不需要管理员权限）
  --prune               删除配置文件中不存在的服务（默认不删除，需显式指定）
  --restart             更新后重启正在运行的服务，使新配置立即生效

规则:
  - 按名称匹配已有服务：不存在则创建，存在且有差异则更新，无差异则保持不变。
  - 条目中未写出的字段视为默认值（与 create 的默认值相同），即配置文件描述服务的完整配置。
  - 配置文件中出现未知字段、取值非法、名称重复、可执行文件不存在时，在修改任何服务之前报错（退出码 2）。
  - 配置文件不含密码：需要创建或切换到“指定用户”账户的服务会被拒绝，
    请先用 'servicesctl update <服务> --account <用户> --password-stdin' 设置；账户不变时沿用原密码。
  - secretEnvironment 中值为 null 的变量沿用服务中已保存的值（新建服务或该变量尚未保存时报错）；
    写出值则按新值设置。
  - 按顺序执行，遇到错误立即停止并报告已完成的部分（退出码 1）。

输出:
  文本模式每行一个动作：+ 创建、~ 更新（列出变化的字段）、- 删除、= 不变。
  --json 时输出 { ok, dryRun, actions: [{ action, name, id, changes, done, restarted }], message }，
  action 为 create | update | delete | unchanged。

权限: --dry-run 或无需修改时普通权限即可；实际修改需要管理员。

示例:
  servicesctl export -o services.json
  servicesctl apply -f services.json --dry-run
  servicesctl apply -f services.json --restart --json
```

## doctor

```text
servicesctl doctor - 诊断服务能否正常运行，并给出修复建议

用法:
  servicesctl doctor [<服务>...] [--all] [--json]

参数:
  <服务>                要检查的服务（ID 或名称），可指定多个；省略时检查全部服务

选项:
  --all                 文本输出时列出全部检查项（默认只列出警告与错误）

检查项（check 字段）:
  admin                 是否以管理员权限运行（否则部分检查跳过）
  wrapper-template      当前的包装器模板 servicesvc.exe 是否存在
  log-directory         日志目录是否存在
  exe / workdir         程序与工作目录是否存在
  wrapper               服务的包装器是否存在、位于受保护目录且为当前版本
  parameters            服务参数是否完整、取值是否有效
  dependencies          依赖的服务是否存在
  event-source          是否注册了 Windows 事件日志源
  account               运行账户能否解析
  account-read-exe      运行账户能否读取并执行程序（非 LocalSystem）
  account-read-workdir  运行账户能否进入工作目录（非 LocalSystem）
  account-write-logs    运行账户能否写入日志目录（非 LocalSystem）
  secrets               运行账户能否读取敏感环境变量
  runtime               自动启动的服务是否因失败而停止
  权限检查只考虑账户自身与内置组（Users、Authenticated Users 等），未展开域组，
  因此“未发现允许规则”为警告而非错误。

输出:
  文本：按全局 / 服务分组，每项为 [OK] / [WARN] / [ERROR] / [SKIP]，附修复建议。
  --json 时输出 { ok, summary: { ok, warning, error, skipped },
    checks: [{ serviceId, serviceName, check, status, message, fix }] }，status 为 ok / warning / error / skipped。

退出码: 存在 error 级别的问题时为 1，否则为 0。

权限: 普通权限即可；以管理员运行时检查更完整。
```

## migrate

```text
servicesctl migrate - 将所有服务切换到当前版本的包装器，并清理旧版本

用法:
  servicesctl migrate [--json]

说明:
  服务由包装器 servicesvc.exe 运行并监管目标程序。安装目录中的 servicesvc.exe 只是模板，
  服务运行的是复制到受保护目录的副本：
    %ProgramData%\ServicesApp\wrapper\<版本>-<哈希>\servicesvc.exe
  该目录只有 SYSTEM 与管理员可修改，升级安装目录中的文件也不会被运行中的服务锁定。
  本命令（ServicesApp 启动时与安装包安装后也会自动执行）:
    1. 将当前目录的 servicesvc.exe 部署到上述目录（内容相同的版本直接复用）；
    2. 将仍指向旧版本、安装目录或旧版 ServicesApp.exe 的服务改为指向该副本；
    3. 删除不再被任何服务引用的旧版本目录。仍在运行旧版本的服务，其目录在服务重启后的
       下一次 migrate 中删除；没有任何服务时删除全部版本。
  正在运行的服务继续使用旧包装器，下次启动（或执行 restart）后生效。

输出:
  --json 时输出 { ok, wrapperPath, migrated: [服务ID...], removedVersions: [目录名...], message }；
  没有服务时 wrapperPath 为 null。

权限: 需要管理员。
```

## version

```text
servicesctl version - 显示版本号

用法:
  servicesctl version [--json]
```

## help

```text
servicesctl help - 显示帮助

用法:
  servicesctl help             显示命令总览
  servicesctl help <命令>      显示指定命令的完整帮助
```
