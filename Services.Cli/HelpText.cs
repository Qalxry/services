namespace Services.Cli
{
    /// <summary>
    /// 帮助文本。servicesctl help / --help 输出总览，servicesctl help &lt;命令&gt; 输出单个命令的完整说明。
    /// </summary>
    internal static class HelpText
    {
        public const string Tool = "servicesctl";

        // 服务参数的说明在 get / update / delete 等命令中复用
        private const string ServiceArg = """
              <服务>                服务 ID（如 WinSvcMgr_MyApp_1a2b3c4d）或服务名称，不区分大小写。
                                    名称匹配到多个服务时报错，此时请改用 ID。
            """;

        private const string ExeOption = """
              --exe <路径>          可执行文件路径（相对路径会转换为绝对路径，文件必须存在）
            """;

        private const string ConfigOptions = """
              --workdir <目录>      工作目录；不指定时默认为程序所在目录
              --args <参数>         启动参数，原样作为一个字符串传入，例如 --args "--port 8080 -v"。
                                    可包含换行，启动时各行去除首尾空白、忽略空行后以空格拼接。
              --description <文本>  服务描述；系统中显示为 "Managed by Windows Service Manager: <描述>"，
                                    为空时使用服务名称
              --startup <类型>      启动类型：auto（开机自启）| manual（手动）| disabled（禁用）
              --auto-restart        启用失败自动重启（进程以非 0 退出码退出时重启）
              --no-auto-restart     关闭失败自动重启
              --restart-delay <秒>  首次重启前的等待时间，1-3600，默认 5
              --max-restarts <次>   计数窗口内的最大重启次数，0-1000，0 表示不限制，默认 5。
                                    超过后服务以失败状态停止并写入 Windows 事件日志，约 1 分钟后由系统
                                    恢复策略重新拉起（24 小时内最多 3 次）
              --restart-reset <分>  距上次重启超过该分钟数则重新计数，1-1440，默认 10
              --backoff             启用指数退避（每次重启延迟翻倍，最多 16 倍），默认启用
              --no-backoff          关闭指数退避，每次使用固定延迟
            """;

        public static string Overview(IEnumerable<CommandSpec> commands)
        {
            var list = string.Join(Environment.NewLine,
                commands.Select(c => $"  {c.Name,-9} {c.Summary}"));

            return $$"""
                {{Tool}} - ServicesApp 命令行工具：管理由 ServicesApp 创建的 Windows 服务

                用法:
                  {{Tool}} <命令> [参数] [选项]
                  {{Tool}} help <命令>        查看某个命令的完整帮助（等同于 {{Tool}} <命令> --help）

                命令:
                {{list}}

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
                  - 服务实际由 ServicesApp.exe 以包装器方式运行，{{Tool}}.exe 需与 ServicesApp.exe
                    位于同一目录，否则 create 会失败。
                  - GUI 正在运行时，命令行所做的修改会在 GUI 窗口获得焦点时自动刷新显示。

                示例:
                  {{Tool}} list --json
                  {{Tool}} create --name MyApp --exe "C:\Apps\my-app.exe" --args "--port 8080" --auto-restart --start
                  {{Tool}} update MyApp --max-restarts 0 --restart
                  {{Tool}} logs MyApp --tail 50 --stream stderr
                  {{Tool}} delete MyApp
                """;
        }

        public const string List = $$"""
            {{Tool}} list - 列出所有由 ServicesApp 管理的服务

            用法:
              {{Tool}} list [--json]

            输出:
              默认输出表格：ID、名称、状态、PID、启动类型、程序路径（按添加顺序排列）。
              --json 时输出服务对象数组，每个对象的字段见 '{{Tool}} help get'。

            权限: 普通权限即可。
            """;

        public const string Get = $$"""
            {{Tool}} get - 查看单个服务的完整配置与状态

            用法:
              {{Tool}} get <服务> [--json]

            参数:
            {{ServiceArg}}

            JSON 字段:
              id                服务 ID（Windows 服务名）
              name              服务名称（显示名称）
              description       描述
              state             running | stopped | start_pending | stop_pending | unknown
              statusText        状态的中文描述
              pid               进程 ID（ServicesApp 包装器进程），未运行时为 null
              startupType       auto | manual | disabled
              exePath           可执行文件路径
              args              保存的原始启动参数（可能为多行）
              commandLineArgs   实际传给程序的命令行参数（多行已合并）
              workingDir        工作目录
              autoRestart       是否启用失败自动重启
              restartPolicy     { delaySeconds, maxRestarts (0=不限), resetMinutes, backoff }
              createdAt         创建时间（ISO 8601）
              latestLogPath     最新日志文件路径，尚无日志时为 null

            权限: 普通权限即可。
            """;

        public const string Create = $$"""
            {{Tool}} create - 创建新服务

            用法:
              {{Tool}} create --name <名称> --exe <路径> [选项] [--start] [--json]

            必需选项:
              --name <名称>         服务名称，只能包含字母、数字、空格、下划线和短横线（支持中文）。
                                    服务 ID 会根据名称自动生成：WinSvcMgr_<名称中的字母数字>_<8 位随机串>
            {{ExeOption}}

            可选选项:
            {{ConfigOptions}}
              --start               创建后立即启动并等待其进入运行状态（最多 30 秒）

            默认值:
              启动类型 auto；不自动重启；重启策略 5 秒 / 5 次 / 10 分钟 / 指数退避。

            输出:
              成功后输出新服务的信息；--json 时输出 { ok, action, id, message, service }，
              service 字段同 '{{Tool}} help get'。

            权限: 需要管理员。

            示例:
              {{Tool}} create --name MyApp --exe "C:\Apps\my-app.exe"
              {{Tool}} create --name "Node API" --exe "C:\nodejs\node.exe" --args "server.js" --workdir "D:\api" --auto-restart --max-restarts 0 --start --json
            """;

        public const string Update = $$"""
            {{Tool}} update - 修改已有服务的配置（只修改指定的字段，其余保持不变）

            用法:
              {{Tool}} update <服务> [选项] [--restart] [--json]

            参数:
            {{ServiceArg}}

            选项（至少指定一项）:
              --name <名称>         修改服务名称（显示名称），服务 ID 不变
            {{ExeOption}}
            {{ConfigOptions}}
              --restart             若服务正在运行，修改后立即重启使新配置生效

            说明:
              新配置在服务下次启动时生效。服务正在运行且未指定 --restart 时，会在输出中提示需要重启。
              清空可选字段可传空字符串，例如 --args "" 或 --description ""。

            权限: 需要管理员。

            示例:
              {{Tool}} update MyApp --args "--port 9090" --restart
              {{Tool}} update WinSvcMgr_MyApp_1a2b3c4d --startup manual --no-auto-restart
            """;

        public const string Delete = $$"""
            {{Tool}} delete - 停止并删除服务（不可撤销，不会询问确认）

            用法:
              {{Tool}} delete <服务> [--json]

            参数:
            {{ServiceArg}}

            说明:
              会先停止服务，再删除服务及其注册表配置。日志文件保留在日志目录中，按日志保留策略自动清理。

            权限: 需要管理员。
            """;

        public const string Start = $$"""
            {{Tool}} start - 启动服务并等待其进入运行状态

            用法:
              {{Tool}} start <服务> [--timeout <秒>] [--json]

            参数:
            {{ServiceArg}}

            选项:
              --timeout <秒>        等待进入运行状态的最长时间，1-600，默认 30；超时返回退出码 1

            说明:
              “运行中”表示包装器已启动。若目标程序启动后立即退出，服务随后会停止或按重启策略重启，
              可用 '{{Tool}} get' 再次确认状态，或用 '{{Tool}} logs' 查看原因。

            权限: 需要管理员。
            """;

        public const string Stop = $$"""
            {{Tool}} stop - 停止服务（最多等待 30 秒）

            用法:
              {{Tool}} stop <服务> [--json]

            参数:
            {{ServiceArg}}

            权限: 需要管理员。
            """;

        public const string Restart = $$"""
            {{Tool}} restart - 重启服务（先停止再启动）

            用法:
              {{Tool}} restart <服务> [--timeout <秒>] [--json]

            参数:
            {{ServiceArg}}

            选项:
              --timeout <秒>        等待进入运行状态的最长时间，1-600，默认 30

            权限: 需要管理员。
            """;

        public const string Logs = $$"""
            {{Tool}} logs - 查看服务最新日志文件的末尾内容

            用法:
              {{Tool}} logs <服务> [--tail <行数>] [--stream <流>] [--raw] [--json]

            参数:
            {{ServiceArg}}

            选项:
              --tail <行数>         输出最后多少行（过滤后计数），0 表示全部，默认 200
              --stream <流>         all（默认）| stdout | stderr；包装器自身消息（system）始终输出
              --raw                 保留 ANSI 颜色转义序列（默认去除）

            输出格式:
              文本模式下每行保持日志文件原样：stdout 行无前缀，stderr 行以 "[stderr] " 开头，
              包装器消息以 "[system] " 开头（如进程退出码、自动重启记录）。
              --json 时输出 { id, logPath, lines: [{ stream, text }] }，text 已去除前缀。

            说明:
              日志目录: %ProgramData%\windows_service_logs，每次服务启动生成一个新文件。
              尚无日志时 logPath 为 null、lines 为空，退出码仍为 0。

            权限: 普通权限即可。
            """;

        public const string Version = $$"""
            {{Tool}} version - 显示版本号

            用法:
              {{Tool}} version [--json]
            """;

        public const string HelpCommand = $$"""
            {{Tool}} help - 显示帮助

            用法:
              {{Tool}} help             显示命令总览
              {{Tool}} help <命令>      显示指定命令的完整帮助
            """;
    }
}
