using System.Globalization;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Services.Core.Helpers;
using Services.Core.Models;
using Services.Core.Services;

namespace Services.Cli
{
    internal static class CliApp
    {
        // 旧版本的包装器（GUI 程序兼容 --service-wrapper），找不到独立包装器 servicesvc.exe 时使用
        private const string LegacyWrapperExeName = "ServicesApp.exe";
        private const int DefaultStartTimeoutSeconds = 30;
        private const int DefaultLogTail = 200;
        private const int FollowPollInterval = 500;

        private static readonly string[] ConfigValueOptions =
            { "exe", "workdir", "args", "description", "startup", "restart-delay", "max-restarts", "restart-reset",
              "stop-mode", "stop-command", "stop-timeout", "account", "env", "unset-env", "depends-on" };

        private static readonly string[] ConfigRepeatableOptions = { "env", "unset-env", "depends-on" };

        private static readonly string[] ConfigFlagOptions =
            { "auto-restart", "no-auto-restart", "backoff", "no-backoff", "password-stdin", "clear-env", "clear-depends" };

        private static readonly List<CommandSpec> Commands = new()
        {
            new CommandSpec { Name = "list", Summary = "列出所有服务", Help = HelpText.List, Handler = ListAsync },
            new CommandSpec { Name = "get", Summary = "查看服务详情", Help = HelpText.Get, MinPositionals = 1, MaxPositionals = 1, Handler = GetAsync },
            new CommandSpec
            {
                Name = "create", Summary = "创建服务", Help = HelpText.Create, RequiresAdmin = true,
                ValueOptions = ConfigValueOptions.Append("name").ToArray(),
                RepeatableOptions = ConfigRepeatableOptions,
                FlagOptions = ConfigFlagOptions.Append("start").ToArray(),
                Handler = CreateAsync
            },
            new CommandSpec
            {
                Name = "update", Summary = "修改服务配置", Help = HelpText.Update, RequiresAdmin = true,
                MinPositionals = 1, MaxPositionals = 1,
                ValueOptions = ConfigValueOptions.Append("name").ToArray(),
                RepeatableOptions = ConfigRepeatableOptions,
                FlagOptions = ConfigFlagOptions.Append("restart").ToArray(),
                Handler = UpdateAsync
            },
            new CommandSpec { Name = "delete", Summary = "停止并删除服务", Help = HelpText.Delete, RequiresAdmin = true, MinPositionals = 1, MaxPositionals = 1, Handler = DeleteAsync },
            new CommandSpec { Name = "start", Summary = "启动服务", Help = HelpText.Start, RequiresAdmin = true, MinPositionals = 1, MaxPositionals = 1, ValueOptions = new[] { "timeout" }, Handler = StartAsync },
            new CommandSpec { Name = "stop", Summary = "停止服务", Help = HelpText.Stop, RequiresAdmin = true, MinPositionals = 1, MaxPositionals = 1, Handler = StopAsync },
            new CommandSpec { Name = "restart", Summary = "重启服务", Help = HelpText.Restart, RequiresAdmin = true, MinPositionals = 1, MaxPositionals = 1, ValueOptions = new[] { "timeout" }, Handler = RestartAsync },
            new CommandSpec
            {
                Name = "logs", Summary = "查看服务日志", Help = HelpText.Logs, MinPositionals = 1, MaxPositionals = 1,
                ValueOptions = new[] { "tail", "stream", "since" }, FlagOptions = new[] { "raw", "follow" },
                ShortAliases = new Dictionary<string, string> { ["f"] = "follow" }, Handler = LogsAsync
            },
            new CommandSpec { Name = "migrate", Summary = "将服务迁移到当前的独立包装器", Help = HelpText.Migrate, RequiresAdmin = true, Handler = MigrateAsync },
            new CommandSpec { Name = "version", Summary = "显示版本号", Help = HelpText.Version, Handler = VersionAsync },
            new CommandSpec { Name = "help", Summary = "显示帮助", Help = HelpText.HelpCommand, MaxPositionals = 1, Handler = HelpAsync },
        };

        public static async Task<int> RunAsync(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
            {
                Console.WriteLine(HelpText.Overview(Commands));
                return args.Length == 0 ? ExitCodes.Usage : ExitCodes.Success;
            }

            if (args[0] is "--version")
            {
                Console.WriteLine(GetVersion());
                return ExitCodes.Success;
            }

            var spec = FindCommand(args[0]);
            try
            {
                if (spec == null)
                    throw new UsageException($"未知命令 '{args[0]}'。");

                var parsed = ParsedArgs.Parse(spec, args.Skip(1).ToList());
                if (parsed.Help)
                {
                    Console.WriteLine(spec.Help);
                    return ExitCodes.Success;
                }

                if (spec.RequiresAdmin && !IsAdministrator())
                    throw new CliException(ExitCodes.AdminRequired,
                        $"命令 '{spec.Name}' 会修改系统服务，需要管理员权限。请在“以管理员身份运行”的终端中重新执行。");

                return await spec.Handler(parsed);
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine($"错误: {ex.Message}");
                var command = ex.Command ?? spec?.Name;
                Console.Error.WriteLine(command != null && FindCommand(command) != null
                    ? $"运行 '{HelpText.Tool} help {command}' 查看用法。"
                    : $"运行 '{HelpText.Tool} help' 查看可用命令。");
                return ex.ExitCode;
            }
            catch (CliException ex)
            {
                Console.Error.WriteLine($"错误: {ex.Message}");
                return ex.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"错误: {ex.Message}");
                return ExitCodes.Failure;
            }
        }

        private static CommandSpec? FindCommand(string name) =>
            Commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        #region Commands

        private static Task<int> HelpAsync(ParsedArgs args)
        {
            if (args.Positionals.Count == 0)
            {
                Console.WriteLine(HelpText.Overview(Commands));
                return Task.FromResult(ExitCodes.Success);
            }

            var spec = FindCommand(args.Positionals[0])
                ?? throw new UsageException($"未知命令 '{args.Positionals[0]}'。", "help");
            Console.WriteLine(spec.Help);
            return Task.FromResult(ExitCodes.Success);
        }

        private static async Task<int> MigrateAsync(ParsedArgs args)
        {
            var wrapper = WindowsServiceManager.FindWrapperExecutable()
                ?? throw new CliException(ExitCodes.Failure,
                    $"未找到服务包装器 {ServiceUtils.WrapperExeName}，它应与 {HelpText.Tool}.exe 位于同一目录: {AppContext.BaseDirectory}");

            var manager = await CreateManagerAsync();
            manager.WrapperExePath = wrapper;
            var migrated = manager.MigrateWrapper();

            var message = migrated.Count == 0
                ? "所有服务均已使用当前包装器，无需迁移。"
                : $"已迁移 {migrated.Count} 个服务。正在运行的服务需重启后生效。";

            if (args.Json)
            {
                WriteJson(new MigrateResult { WrapperPath = wrapper, Migrated = migrated, Message = message }, CliJsonContext.Readable.MigrateResult);
            }
            else
            {
                Console.WriteLine(message);
                foreach (var id in migrated) Console.WriteLine($"  {id}");
            }
            return ExitCodes.Success;
        }

        private static Task<int> VersionAsync(ParsedArgs args)
        {
            if (args.Json)
                WriteJson(new VersionInfo { Version = GetVersion() }, CliJsonContext.Readable.VersionInfo);
            else
                Console.WriteLine(GetVersion());
            return Task.FromResult(ExitCodes.Success);
        }

        private static async Task<int> ListAsync(ParsedArgs args)
        {
            var manager = await CreateManagerAsync();
            var services = (await manager.GetServicesSnapshotAsync())
                .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id, StringComparer.Ordinal)
                .ToList();
            var logManager = new LogManager();

            if (args.Json)
            {
                WriteJson(services.Select(s => ToInfo(s, logManager)).ToList(), CliJsonContext.Readable.ListServiceInfo);
                return ExitCodes.Success;
            }

            if (services.Count == 0)
            {
                Console.WriteLine("暂无由 ServicesApp 管理的服务。");
                return ExitCodes.Success;
            }

            var rows = services.Select(s => new[]
            {
                s.Id, s.Name, s.Status, s.HasPid ? s.Pid.ToString() : "-", ToStartupText(s.StartupType, s.DelayedAutoStart), s.ExePath
            }).ToList();
            WriteTable(new[] { "ID", "名称", "状态", "PID", "启动类型", "程序路径" }, rows);
            return ExitCodes.Success;
        }

        private static async Task<int> GetAsync(ParsedArgs args)
        {
            var manager = await CreateManagerAsync();
            var service = await FindServiceAsync(manager, args.Positionals[0]);
            var info = ToInfo(service, new LogManager());

            if (args.Json)
                WriteJson(info, CliJsonContext.Readable.ServiceInfo);
            else
                WriteServiceText(info);
            return ExitCodes.Success;
        }

        private static async Task<int> CreateAsync(ParsedArgs args)
        {
            var name = args.GetValue("name");
            var exe = args.GetValue("exe");
            if (string.IsNullOrWhiteSpace(name)) throw new UsageException("缺少必需选项 --name。", "create");
            if (string.IsNullOrWhiteSpace(exe)) throw new UsageException("缺少必需选项 --exe。", "create");

            var config = new ServiceConfig { Name = name.Trim() };
            ApplyConfigOptions(args, config);

            var manager = await CreateManagerAsync();
            manager.WrapperExePath = ResolveWrapperPath();
            var password = ReadPasswordOption(args, config, existing: null);
            var id = await manager.CreateServiceAsync(config, password);

            var message = $"服务 '{config.Name}' 已创建（ID: {id}）。";
            if (args.HasFlag("start"))
            {
                await StartAndWaitAsync(manager, id, DefaultStartTimeoutSeconds);
                message += " 已启动。";
            }

            return await WriteActionResultAsync(args, manager, "create", id, message);
        }

        private static async Task<int> UpdateAsync(ParsedArgs args)
        {
            var manager = await CreateManagerAsync();
            var service = await FindServiceAsync(manager, args.Positionals[0]);

            var config = ServiceConfig.FromService(service);
            int changed = ApplyConfigOptions(args, config);
            if (args.HasValue("name"))
            {
                config.Name = args.GetValue("name")!.Trim();
                changed++;
            }
            if (changed == 0)
                throw new UsageException("未指定任何要修改的字段。", "update");

            var password = ReadPasswordOption(args, config, service);
            await manager.UpdateServiceAsync(service.Id, config, password);

            var message = $"服务 '{config.Name}' 已更新。";
            if (IsRunning(service.Id))
            {
                if (args.HasFlag("restart"))
                {
                    await manager.StopServiceAsync(service.Id);
                    await StartAndWaitAsync(manager, service.Id, DefaultStartTimeoutSeconds);
                    message += " 已重启，新配置已生效。";
                }
                else
                {
                    message += " 服务正在运行，新配置需重启后生效（可使用 --restart 或 restart 命令）。";
                }
            }

            return await WriteActionResultAsync(args, manager, "update", service.Id, message);
        }

        private static async Task<int> DeleteAsync(ParsedArgs args)
        {
            var manager = await CreateManagerAsync();
            var service = await FindServiceAsync(manager, args.Positionals[0]);

            await manager.DeleteServiceAsync(service.Id);

            var message = $"服务 '{service.Name}' 已删除（ID: {service.Id}）。";
            if (args.Json)
                WriteJson(new ActionResult { Action = "delete", Id = service.Id, Message = message }, CliJsonContext.Readable.ActionResult);
            else
                Console.WriteLine(message);
            return ExitCodes.Success;
        }

        private static async Task<int> StartAsync(ParsedArgs args)
        {
            int timeout = args.GetInt("timeout", 1, 600) ?? DefaultStartTimeoutSeconds;
            var manager = await CreateManagerAsync();
            var service = await FindServiceAsync(manager, args.Positionals[0]);

            await StartAndWaitAsync(manager, service.Id, timeout);
            return await WriteActionResultAsync(args, manager, "start", service.Id, $"服务 '{service.Name}' 已启动。");
        }

        private static async Task<int> StopAsync(ParsedArgs args)
        {
            var manager = await CreateManagerAsync();
            var service = await FindServiceAsync(manager, args.Positionals[0]);

            await manager.StopServiceAsync(service.Id);
            if (IsRunning(service.Id))
                throw new CliException(ExitCodes.Failure, $"服务 '{service.Name}' 在 30 秒内未能停止。");

            return await WriteActionResultAsync(args, manager, "stop", service.Id, $"服务 '{service.Name}' 已停止。");
        }

        private static async Task<int> RestartAsync(ParsedArgs args)
        {
            int timeout = args.GetInt("timeout", 1, 600) ?? DefaultStartTimeoutSeconds;
            var manager = await CreateManagerAsync();
            var service = await FindServiceAsync(manager, args.Positionals[0]);

            await manager.StopServiceAsync(service.Id);
            await StartAndWaitAsync(manager, service.Id, timeout);
            return await WriteActionResultAsync(args, manager, "restart", service.Id, $"服务 '{service.Name}' 已重启。");
        }

        private static async Task<int> LogsAsync(ParsedArgs args)
        {
            int tail = args.GetInt("tail", 0, int.MaxValue) ?? DefaultLogTail;
            bool raw = args.HasFlag("raw");
            bool follow = args.HasFlag("follow");

            var stream = (args.GetValue("stream") ?? "all").ToLowerInvariant();
            if (stream is not ("all" or "stdout" or "stderr"))
                throw new UsageException($"--stream 只能是 all、stdout 或 stderr，实际为 '{stream}'。", "logs");

            DateTime? since = args.GetValue("since") is { } sinceText ? LogTimeParser.ParseSince(sinceText, DateTime.Now) : null;

            var manager = await CreateManagerAsync();
            var service = await FindServiceAsync(manager, args.Positionals[0]);

            var logManager = new LogManager();
            var logPath = logManager.GetLatestLogPath(service.Id);

            if (follow)
                return await FollowLogsAsync(args, service, logManager, logPath, tail, stream, since);

            var lines = logPath == null ? new List<string>() : ReadTail(logPath, tail, line => MatchesLogFilter(line, stream, since));

            if (args.Json)
            {
                var result = new LogsResult { Id = service.Id, LogPath = logPath };
                foreach (var line in lines)
                {
                    result.Lines.Add(ToLogLineInfo(line, raw));
                }
                WriteJson(result, CliJsonContext.Readable.LogsResult);
                return ExitCodes.Success;
            }

            if (logPath == null)
            {
                Console.Error.WriteLine($"服务 '{service.Name}' 暂无日志。");
                return ExitCodes.Success;
            }

            foreach (var line in lines)
            {
                Console.WriteLine(raw ? line : LogLine.StripAnsi(line));
            }
            return ExitCodes.Success;
        }

        /// <summary>
        /// 先输出末尾内容，再每 500ms 轮询新写入的行，直到 Ctrl+C。
        /// 服务重启会生成新的日志文件，检测到最新文件变化时读完旧文件剩余内容后切换。
        /// </summary>
        private static async Task<int> FollowLogsAsync(ParsedArgs args, Service service, LogManager logManager,
            string? logPath, int tail, string stream, DateTime? since)
        {
            bool raw = args.HasFlag("raw");

            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                // 取消默认的终止行为，让循环正常结束并以退出码 0 返回
                e.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += onCancel;

            try
            {
                void Emit(string line)
                {
                    if (!MatchesLogFilter(line, stream, since)) return;

                    if (args.Json)
                        Console.WriteLine(JsonSerializer.Serialize(ToLogLineInfo(line, raw), CliJsonContext.Compact.LogLineInfo));
                    else
                        Console.WriteLine(raw ? line : LogLine.StripAnsi(line));
                }

                long position = 0;
                if (logPath == null)
                {
                    Console.Error.WriteLine($"服务 '{service.Name}' 暂无日志，等待日志生成...（Ctrl+C 退出）");
                }
                else
                {
                    var buffer = new Queue<string>();
                    ReadNewLines(logPath, ref position, line =>
                    {
                        if (!MatchesLogFilter(line, stream, since)) return;
                        buffer.Enqueue(line);
                        if (tail > 0 && buffer.Count > tail) buffer.Dequeue();
                    });
                    foreach (var line in buffer) Emit(line);
                }

                var currentPath = logPath;
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(FollowPollInterval, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    try
                    {
                        var latest = logManager.GetLatestLogPath(service.Id);
                        if (latest != null && !string.Equals(latest, currentPath, StringComparison.OrdinalIgnoreCase))
                        {
                            // 先读完旧文件中尚未输出的内容，再切换到新文件
                            if (currentPath != null)
                            {
                                try { ReadNewLines(currentPath, ref position, Emit); }
                                catch (IOException) { }
                            }
                            Console.Error.WriteLine($"日志文件已切换: {latest}");
                            currentPath = latest;
                            position = 0;
                        }

                        if (currentPath != null)
                            ReadNewLines(currentPath, ref position, Emit);
                    }
                    catch (IOException)
                    {
                        // 文件被清理或暂时无法读取，下一轮重试
                    }
                }

                return ExitCodes.Success;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        private static bool MatchesLogFilter(string line, string stream, DateTime? since)
        {
            var parsed = LogLine.Parse(line);

            // 指定 --since 时，没有时间戳的旧格式行无法判断时间，一律排除
            if (since != null && (parsed.Timestamp == null || parsed.Timestamp < since)) return false;

            return stream == "all" || parsed.Kind == LogLineKind.System ||
                   (stream == "stdout" ? parsed.Kind == LogLineKind.Stdout : parsed.Kind == LogLineKind.Stderr);
        }

        private static LogLineInfo ToLogLineInfo(string line, bool raw)
        {
            var parsed = LogLine.Parse(line);
            return new LogLineInfo
            {
                Timestamp = parsed.Timestamp?.ToString(LogLine.TimestampFormat, CultureInfo.InvariantCulture),
                Stream = parsed.Kind.ToString().ToLowerInvariant(),
                Text = raw ? parsed.Text : LogLine.StripAnsi(parsed.Text)
            };
        }

        #endregion

        #region Helpers

        private static async Task<WindowsServiceManager> CreateManagerAsync()
        {
            var manager = new WindowsServiceManager();
            await manager.InitializeAsync();
            return manager;
        }

        /// <summary>
        /// 按 ID 精确匹配，其次按名称匹配（均不区分大小写）
        /// </summary>
        private static async Task<Service> FindServiceAsync(WindowsServiceManager manager, string key)
        {
            var services = await manager.GetServicesSnapshotAsync();

            var byId = services.FirstOrDefault(s => string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;

            var byName = services.Where(s => string.Equals(s.Name, key.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return byName[0];
            if (byName.Count > 1)
                throw new UsageException($"名称 '{key}' 匹配到多个服务（{string.Join(", ", byName.Select(s => s.Id))}），请改用服务 ID。");

            throw new CliException(ExitCodes.NotFound, $"未找到服务 '{key}'。运行 '{HelpText.Tool} list' 查看所有服务。");
        }

        /// <summary>
        /// 将命令行中的配置选项写入 config，返回修改的字段数
        /// </summary>
        private static int ApplyConfigOptions(ParsedArgs args, ServiceConfig config)
        {
            int changed = 0;

            if (args.GetValue("exe") is { } exe)
            {
                config.ExePath = Path.GetFullPath(exe.Trim());
                changed++;
            }
            if (args.GetValue("workdir") is { } workDir)
            {
                config.WorkingDir = string.IsNullOrWhiteSpace(workDir) ? "" : Path.GetFullPath(workDir.Trim());
                changed++;
            }
            if (args.GetValue("args") is { } arguments)
            {
                config.Args = arguments.Trim();
                changed++;
            }
            if (args.GetValue("description") is { } description)
            {
                config.Description = description.Trim();
                changed++;
            }
            if (args.GetValue("startup") is { } startup)
            {
                if (!ServiceStartupModes.TryParse(startup, out var startupType, out var delayedAutoStart))
                    throw new UsageException($"--startup 只能是 auto、delayed-auto、manual 或 disabled，实际为 '{startup}'。", args.Command);
                config.StartupType = startupType;
                config.DelayedAutoStart = delayedAutoStart;
                changed++;
            }
            if (args.GetSwitch("auto-restart", "no-auto-restart") is { } autoRestart)
            {
                config.AutoRestart = autoRestart;
                changed++;
            }
            if (args.GetInt("restart-delay", 1, 3600) is { } delay)
            {
                config.RestartDelaySeconds = delay;
                changed++;
            }
            if (args.GetInt("max-restarts", 0, 1000) is { } maxRestarts)
            {
                config.MaxRestarts = maxRestarts;
                changed++;
            }
            if (args.GetInt("restart-reset", 1, 1440) is { } reset)
            {
                config.RestartResetMinutes = reset;
                changed++;
            }
            if (args.GetSwitch("backoff", "no-backoff") is { } backoff)
            {
                config.RestartBackoff = backoff;
                changed++;
            }
            if (args.GetValue("stop-mode") is { } stopMode)
            {
                config.StopMode = stopMode.ToLowerInvariant() switch
                {
                    "kill" => ServiceStopMode.Kill,
                    "ctrl-c" => ServiceStopMode.CtrlC,
                    "command" => ServiceStopMode.Command,
                    _ => throw new UsageException($"--stop-mode 只能是 kill、ctrl-c 或 command，实际为 '{stopMode}'。", args.Command)
                };
                changed++;
            }
            if (args.GetValue("stop-command") is { } stopCommand)
            {
                config.StopCommand = stopCommand.Trim();
                changed++;
            }
            if (args.GetInt("stop-timeout", 1, StopPolicyDefaults.MaxTimeoutSeconds) is { } stopTimeout)
            {
                config.StopTimeoutSeconds = stopTimeout;
                changed++;
            }

            if (args.GetValue("account") is { } account)
            {
                if (string.IsNullOrWhiteSpace(account))
                    throw new UsageException("--account 不能为空。", args.Command);

                config.AccountType = account.Trim().ToLowerInvariant() switch
                {
                    "localsystem" => ServiceAccountType.LocalSystem,
                    "localservice" => ServiceAccountType.LocalService,
                    "networkservice" => ServiceAccountType.NetworkService,
                    "virtual" => ServiceAccountType.Virtual,
                    _ => ServiceAccountType.User
                };
                config.AccountName = config.AccountType == ServiceAccountType.User ? account.Trim() : null;
                changed++;
            }

            // 环境变量按 clear → unset → set 的顺序处理，因此可在一条命令中重置并重新设置
            var unsetEnv = args.GetValues("unset-env");
            var setEnv = args.GetValues("env");
            if (args.HasFlag("clear-env") || unsetEnv.Count > 0 || setEnv.Count > 0)
            {
                var environment = EnvironmentVariables.Clone(config.Environment);
                if (args.HasFlag("clear-env")) environment.Clear();
                foreach (var key in unsetEnv)
                {
                    if (string.IsNullOrWhiteSpace(key))
                        throw new UsageException("--unset-env 的变量名不能为空。", args.Command);
                    environment.Remove(key.Trim());
                }
                foreach (var entry in setEnv)
                {
                    try
                    {
                        var (key, value) = EnvironmentVariables.ParseEntry(entry);
                        environment[key] = value;
                    }
                    catch (ArgumentException ex)
                    {
                        throw new UsageException($"--env 参数错误：{ex.Message}（应为 KEY=VALUE）", args.Command);
                    }
                }
                config.Environment = environment;
                changed++;
            }

            // 依赖服务：--depends-on 以给定列表整体替换原依赖，--clear-depends 清空
            var dependsOn = args.GetValues("depends-on");
            if (args.HasFlag("clear-depends") && dependsOn.Count > 0)
                throw new UsageException("--clear-depends 不能与 --depends-on 同时使用。", args.Command);
            if (args.HasFlag("clear-depends"))
            {
                config.Dependencies = new List<string>();
                changed++;
            }
            else if (dependsOn.Count > 0)
            {
                if (dependsOn.Any(string.IsNullOrWhiteSpace))
                    throw new UsageException("--depends-on 的服务名不能为空（清空依赖请使用 --clear-depends）。", args.Command);

                var dependencies = ServiceDependencies.Normalize(dependsOn);
                try
                {
                    ServiceDependencies.Validate(dependencies);
                }
                catch (ArgumentException ex)
                {
                    throw new UsageException($"--depends-on 参数错误：{ex.Message}", args.Command);
                }
                config.Dependencies = dependencies;
                changed++;
            }

            if (config.StopMode == ServiceStopMode.Command && string.IsNullOrWhiteSpace(config.StopCommand))
                throw new UsageException("--stop-mode command 需要同时指定 --stop-command。", args.Command);

            if (config.ExePath.Length > 0 && !File.Exists(config.ExePath))
                throw new UsageException($"可执行文件不存在: {config.ExePath}", args.Command);
            if (!string.IsNullOrEmpty(config.WorkingDir) && !Directory.Exists(config.WorkingDir))
                throw new UsageException($"工作目录不存在: {config.WorkingDir}", args.Command);

            return changed;
        }

        /// <summary>
        /// 处理 --password-stdin：从标准输入读取一行作为指定用户的密码。
        /// 新建、切换为指定用户或更换用户名时必须提供；同一用户未提供时保持原密码。
        /// </summary>
        private static string? ReadPasswordOption(ParsedArgs args, ServiceConfig config, Service? existing)
        {
            bool isUser = config.AccountType == ServiceAccountType.User;
            if (args.HasFlag("password-stdin"))
            {
                if (!isUser)
                    throw new UsageException("--password-stdin 仅在 --account 为指定用户时使用。", args.Command);

                var password = Console.In.ReadLine();
                if (string.IsNullOrEmpty(password))
                    throw new UsageException("未能从标准输入读取到密码。", args.Command);
                return password.TrimEnd('\r');
            }

            bool requiresPassword = isUser && (existing == null || existing.AccountType != ServiceAccountType.User ||
                !string.Equals(existing.AccountName, config.AccountName, StringComparison.OrdinalIgnoreCase));
            if (requiresPassword)
                throw new UsageException($"运行账户为用户 '{config.AccountName}' 时需通过 --password-stdin 提供密码。", args.Command);
            return null;
        }

        /// <summary>
        /// 包装器路径：优先同目录的 servicesvc.exe，其次兼容旧版本的 ServicesApp.exe
        /// </summary>
        private static string ResolveWrapperPath()
        {
            var wrapper = WindowsServiceManager.FindWrapperExecutable();
            if (wrapper != null) return wrapper;

            var legacy = Path.Combine(AppContext.BaseDirectory, LegacyWrapperExeName);
            if (File.Exists(legacy)) return legacy;

            throw new CliException(ExitCodes.Failure,
                $"未找到服务包装器 {ServiceUtils.WrapperExeName}，它应与 {HelpText.Tool}.exe 位于同一目录: {AppContext.BaseDirectory}");
        }

        private static async Task StartAndWaitAsync(WindowsServiceManager manager, string id, int timeoutSeconds)
        {
            await manager.StartServiceAsync(id);

            using var sc = new ServiceController(id);
            try
            {
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(timeoutSeconds));
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                sc.Refresh();
                throw new CliException(ExitCodes.Failure,
                    $"服务 '{id}' 在 {timeoutSeconds} 秒内未进入运行状态（当前: {sc.Status}）。可运行 '{HelpText.Tool} logs {id}' 查看原因。");
            }
        }

        private static bool IsRunning(string id)
        {
            var (status, _) = ServiceUtils.GetServiceStatus(id);
            return status is "运行中" or "启动中";
        }

        private static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static string GetVersion()
        {
            var assembly = typeof(CliApp).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? assembly.GetName().Version?.ToString()
                          ?? "unknown";
            // 去掉 SDK 附加的 +commit 元数据
            int plus = version.IndexOf('+');
            return plus > 0 ? version.Substring(0, plus) : version;
        }

        /// <summary>
        /// 读取日志文件中满足条件的最后 count 行（count 为 0 时读取全部）。允许服务同时写入。
        /// </summary>
        private static List<string> ReadTail(string path, int count, Func<string, bool> filter)
        {
            var buffer = new Queue<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!filter(line)) continue;
                buffer.Enqueue(line);
                if (count > 0 && buffer.Count > count) buffer.Dequeue();
            }
            return buffer.ToList();
        }

        /// <summary>
        /// 从 position 开始读取文件中新增的完整行（以换行符结尾）并逐行回调，position 前进到最后一个换行符之后；
        /// 未写完的最后一行留待下次读取。文件变小（被截断）时从头开始。
        /// </summary>
        private static void ReadNewLines(string path, ref long position, Action<string> onLine)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < position) position = 0;

            long start = position;
            long remaining = fs.Length - start;
            fs.Seek(start, SeekOrigin.Begin);

            var chunk = new byte[64 * 1024];
            using var lineBytes = new MemoryStream();
            long offset = 0;

            while (remaining > 0)
            {
                int read = fs.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
                if (read <= 0) break;
                remaining -= read;

                int lineStart = 0;
                for (int i = 0; i < read; i++)
                {
                    if (chunk[i] != (byte)'\n') continue;

                    lineBytes.Write(chunk, lineStart, i - lineStart);
                    var line = Encoding.UTF8.GetString(lineBytes.GetBuffer(), 0, (int)lineBytes.Length);
                    onLine(line.TrimEnd('\r'));

                    lineBytes.SetLength(0);
                    lineStart = i + 1;
                    position = start + offset + i + 1;
                }
                lineBytes.Write(chunk, lineStart, read - lineStart);
                offset += read;
            }
        }

        private static async Task<int> WriteActionResultAsync(ParsedArgs args, WindowsServiceManager manager, string action, string id, string message)
        {
            await manager.InitializeAsync();
            var service = (await manager.GetServicesSnapshotAsync()).FirstOrDefault(s => s.Id == id);
            var info = service == null ? null : ToInfo(service, new LogManager());

            if (args.Json)
            {
                WriteJson(new ActionResult { Action = action, Id = id, Message = message, Service = info }, CliJsonContext.Readable.ActionResult);
            }
            else
            {
                Console.WriteLine(message);
                if (info != null)
                {
                    Console.WriteLine();
                    WriteServiceText(info);
                }
            }
            return ExitCodes.Success;
        }

        private static ServiceInfo ToInfo(Service s, LogManager logManager)
        {
            return new ServiceInfo
            {
                Id = s.Id,
                Name = s.Name,
                Description = s.Description,
                State = s.Status switch
                {
                    "运行中" => "running",
                    "已停止" => "stopped",
                    "启动中" => "start_pending",
                    "停止中" => "stop_pending",
                    _ => "unknown"
                },
                StatusText = s.Status,
                Pid = s.HasPid ? s.Pid : null,
                StartupType = ServiceStartupModes.ToCliValue(s.StartupType, s.DelayedAutoStart),
                ExePath = s.ExePath,
                Args = s.Args,
                CommandLineArgs = ServiceUtils.NormalizeArgs(s.Args),
                WorkingDir = s.WorkingDir,
                AutoRestart = s.AutoRestart,
                RestartPolicy = new RestartPolicyInfo
                {
                    DelaySeconds = s.RestartDelaySeconds,
                    MaxRestarts = s.MaxRestarts,
                    ResetMinutes = s.RestartResetMinutes,
                    Backoff = s.RestartBackoff
                },
                Account = new AccountInfo
                {
                    Type = s.AccountType.ToString().ToLowerInvariant(),
                    Name = s.AccountName ?? ServiceAccounts.LocalSystemName
                },
                StopPolicy = new StopPolicyInfo
                {
                    Mode = ToStopModeText(s.StopMode),
                    Command = string.IsNullOrEmpty(s.StopCommand) ? null : s.StopCommand,
                    TimeoutSeconds = s.StopTimeoutSeconds
                },
                Environment = s.Environment
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(pair => pair.Key, pair => pair.Value),
                Dependencies = s.Dependencies.ToList(),
                CreatedAt = s.CreatedAt,
                LatestLogPath = logManager.GetLatestLogPath(s.Id)
            };
        }

        private static string ToStopModeText(ServiceStopMode mode) => mode switch
        {
            ServiceStopMode.CtrlC => "ctrl-c",
            ServiceStopMode.Command => "command",
            _ => "kill"
        };

        private static string ToStartupText(ServiceStartupType type, bool delayed) => type switch
        {
            ServiceStartupType.Auto => delayed ? "延迟自启" : "开机自启",
            ServiceStartupType.Manual => "手动",
            ServiceStartupType.Disabled => "已禁用",
            _ => "未知"
        };

        private static void WriteServiceText(ServiceInfo info)
        {
            var policy = info.RestartPolicy;
            var restartText = info.AutoRestart
                ? $"已启用（延迟 {policy.DelaySeconds} 秒，{(policy.MaxRestarts > 0 ? $"最多 {policy.MaxRestarts} 次" : "不限次数")}，" +
                  $"{policy.ResetMinutes} 分钟后重置计数{(policy.Backoff ? "，指数退避" : "")}）"
                : "未启用";
            var stop = info.StopPolicy;
            var stopText = stop.Mode switch
            {
                "ctrl-c" => $"发送 Ctrl+C（{stop.TimeoutSeconds} 秒未退出则强制结束）",
                "command" => $"执行停止命令: {stop.Command}（{stop.TimeoutSeconds} 秒未退出则强制结束）",
                _ => "直接结束进程"
            };

            var rows = new (string Label, string? Value)[]
            {
                ("ID", info.Id),
                ("名称", info.Name),
                ("描述", info.Description),
                ("状态", info.Pid is { } pid ? $"{info.StatusText} (PID {pid})" : info.StatusText),
                ("启动类型", info.StartupType == "delayed-auto" ? "delayed-auto（延迟自动启动）" : info.StartupType),
                ("失败自动重启", restartText),
                ("停止方式", stopText),
                ("运行账户", info.Account.Name),
                ("程序路径", info.ExePath),
                ("工作目录", info.WorkingDir),
                ("启动参数", info.CommandLineArgs),
                ("环境变量", string.Join("\n", info.Environment.Select(pair => $"{pair.Key}={pair.Value}"))),
                ("依赖服务", ServiceDependencies.Format(info.Dependencies)),
                ("创建时间", info.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")),
                ("最新日志", info.LatestLogPath),
            };

            int width = rows.Max(r => DisplayWidth(r.Label));
            foreach (var (label, value) in rows)
            {
                // 多行的值（如环境变量）从第二行起缩进到值所在的列
                var text = string.IsNullOrWhiteSpace(value) ? "-" : value.Replace("\n", "\n" + new string(' ', width + 2));
                Console.WriteLine($"{PadDisplay(label, width)}  {text}");
            }
        }

        private static void WriteTable(string[] headers, List<string[]> rows)
        {
            var widths = headers.Select((h, i) => Math.Max(DisplayWidth(h), rows.Max(r => DisplayWidth(r[i])))).ToArray();

            Console.WriteLine(string.Join("  ", headers.Select((h, i) => PadDisplay(h, widths[i]))).TrimEnd());
            foreach (var row in rows)
            {
                Console.WriteLine(string.Join("  ", row.Select((c, i) => PadDisplay(c, widths[i]))).TrimEnd());
            }
        }

        // 中日韩等全角字符按 2 列宽计算，使表格对齐
        private static int DisplayWidth(string text) => text.Sum(c => c >= 0x1100 && (c <= 0x115F || c >= 0x2E80) ? 2 : 1);

        private static string PadDisplay(string text, int width) => text + new string(' ', Math.Max(0, width - DisplayWidth(text)));

        private static void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo)
        {
            Console.WriteLine(JsonSerializer.Serialize(value, typeInfo));
        }

        #endregion
    }
}
