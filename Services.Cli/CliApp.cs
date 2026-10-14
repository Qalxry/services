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
        private const string WrapperExeName = "ServicesApp.exe";
        private const int DefaultStartTimeoutSeconds = 30;
        private const int DefaultLogTail = 200;

        private static readonly string[] ConfigValueOptions =
            { "exe", "workdir", "args", "description", "startup", "restart-delay", "max-restarts", "restart-reset" };

        private static readonly string[] ConfigFlagOptions =
            { "auto-restart", "no-auto-restart", "backoff", "no-backoff" };

        private static readonly List<CommandSpec> Commands = new()
        {
            new CommandSpec { Name = "list", Summary = "列出所有服务", Help = HelpText.List, Handler = ListAsync },
            new CommandSpec { Name = "get", Summary = "查看服务详情", Help = HelpText.Get, MinPositionals = 1, MaxPositionals = 1, Handler = GetAsync },
            new CommandSpec
            {
                Name = "create", Summary = "创建服务", Help = HelpText.Create, RequiresAdmin = true,
                ValueOptions = ConfigValueOptions.Append("name").ToArray(),
                FlagOptions = ConfigFlagOptions.Append("start").ToArray(),
                Handler = CreateAsync
            },
            new CommandSpec
            {
                Name = "update", Summary = "修改服务配置", Help = HelpText.Update, RequiresAdmin = true,
                MinPositionals = 1, MaxPositionals = 1,
                ValueOptions = ConfigValueOptions.Append("name").ToArray(),
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
                ValueOptions = new[] { "tail", "stream" }, FlagOptions = new[] { "raw" }, Handler = LogsAsync
            },
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
                s.Id, s.Name, s.Status, s.HasPid ? s.Pid.ToString() : "-", ToStartupText(s.StartupType), s.ExePath
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
            var id = await manager.CreateServiceAsync(config);

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

            await manager.UpdateServiceAsync(service.Id, config);

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

            var stream = (args.GetValue("stream") ?? "all").ToLowerInvariant();
            if (stream is not ("all" or "stdout" or "stderr"))
                throw new UsageException($"--stream 只能是 all、stdout 或 stderr，实际为 '{stream}'。", "logs");

            var manager = await CreateManagerAsync();
            var service = await FindServiceAsync(manager, args.Positionals[0]);

            var logPath = new LogManager().GetLatestLogPath(service.Id);
            var lines = logPath == null ? new List<string>() : ReadTail(logPath, tail, line =>
            {
                var kind = LogLine.Parse(line).Kind;
                return stream == "all" || kind == LogLineKind.System ||
                       (stream == "stdout" ? kind == LogLineKind.Stdout : kind == LogLineKind.Stderr);
            });

            if (args.Json)
            {
                var result = new LogsResult { Id = service.Id, LogPath = logPath };
                foreach (var line in lines)
                {
                    var (kind, text) = LogLine.Parse(line);
                    result.Lines.Add(new LogLineInfo
                    {
                        Stream = kind.ToString().ToLowerInvariant(),
                        Text = raw ? text : LogLine.StripAnsi(text)
                    });
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
                config.StartupType = startup.ToLowerInvariant() switch
                {
                    "auto" => ServiceStartupType.Auto,
                    "manual" => ServiceStartupType.Manual,
                    "disabled" => ServiceStartupType.Disabled,
                    _ => throw new UsageException($"--startup 只能是 auto、manual 或 disabled，实际为 '{startup}'。", args.Command)
                };
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

            if (config.ExePath.Length > 0 && !File.Exists(config.ExePath))
                throw new UsageException($"可执行文件不存在: {config.ExePath}", args.Command);
            if (!string.IsNullOrEmpty(config.WorkingDir) && !Directory.Exists(config.WorkingDir))
                throw new UsageException($"工作目录不存在: {config.WorkingDir}", args.Command);

            return changed;
        }

        private static string ResolveWrapperPath()
        {
            var path = Path.Combine(AppContext.BaseDirectory, WrapperExeName);
            if (!File.Exists(path))
                throw new CliException(ExitCodes.Failure,
                    $"未找到 {WrapperExeName}（服务由它以包装器方式运行），它应与 {HelpText.Tool}.exe 位于同一目录: {AppContext.BaseDirectory}");
            return path;
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
                StartupType = s.StartupType.ToString().ToLowerInvariant(),
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
                CreatedAt = s.CreatedAt,
                LatestLogPath = logManager.GetLatestLogPath(s.Id)
            };
        }

        private static string ToStartupText(ServiceStartupType type) => type switch
        {
            ServiceStartupType.Auto => "开机自启",
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

            var rows = new (string Label, string? Value)[]
            {
                ("ID", info.Id),
                ("名称", info.Name),
                ("描述", info.Description),
                ("状态", info.Pid is { } pid ? $"{info.StatusText} (PID {pid})" : info.StatusText),
                ("启动类型", info.StartupType),
                ("失败自动重启", restartText),
                ("程序路径", info.ExePath),
                ("工作目录", info.WorkingDir),
                ("启动参数", info.CommandLineArgs),
                ("创建时间", info.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")),
                ("最新日志", info.LatestLogPath),
            };

            int width = rows.Max(r => DisplayWidth(r.Label));
            foreach (var (label, value) in rows)
            {
                Console.WriteLine($"{PadDisplay(label, width)}  {(string.IsNullOrWhiteSpace(value) ? "-" : value)}");
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
