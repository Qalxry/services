using System.Reflection;
using System.Security.Principal;
using Services.Core.Application;
using Services.Core.Configuration;

namespace Services.Cli
{
    /// <summary>
    /// 命令表与分发。各命令的实现位于 Commands/ 下（每个命令一个文件），
    /// 配置选项解析见 ConfigOptions.cs，输出格式见 Output.cs。
    /// </summary>
    internal static partial class CliApp
    {
        private static readonly string[] ConfigValueOptions =
            { "exe", "workdir", "args", "description", "startup", "restart-delay", "max-restarts", "restart-reset",
              "stop-mode", "stop-command", "stop-timeout", "account", "env", "secret-env", "unset-env", "depends-on" };

        private static readonly string[] ConfigRepeatableOptions = { "env", "secret-env", "unset-env", "depends-on" };

        private static readonly string[] ConfigFlagOptions =
            { "auto-restart", "no-auto-restart", "backoff", "no-backoff", "password-stdin", "clear-env", "clear-depends" };

        private static readonly List<CommandSpec> Commands = new()
        {
            new CommandSpec { Name = "list", Summary = HelpText.Summary(HelpText.List), Help = HelpText.List, FlagOptions = new[] { "show-secrets" }, Handler = ListAsync },
            new CommandSpec { Name = "get", Summary = HelpText.Summary(HelpText.Get), Help = HelpText.Get, MinPositionals = 1, MaxPositionals = 1, FlagOptions = new[] { "show-secrets" }, Handler = GetAsync },
            new CommandSpec
            {
                Name = "create", Summary = HelpText.Summary(HelpText.Create), Help = HelpText.Create, RequiresAdmin = true,
                ValueOptions = ConfigValueOptions.Append("name").ToArray(),
                RepeatableOptions = ConfigRepeatableOptions,
                FlagOptions = ConfigFlagOptions.Append("start").ToArray(),
                Handler = CreateAsync
            },
            new CommandSpec
            {
                Name = "update", Summary = HelpText.Summary(HelpText.Update), Help = HelpText.Update, RequiresAdmin = true,
                MinPositionals = 1, MaxPositionals = 1,
                ValueOptions = ConfigValueOptions.Append("name").ToArray(),
                RepeatableOptions = ConfigRepeatableOptions,
                FlagOptions = ConfigFlagOptions.Append("restart").ToArray(),
                Handler = UpdateAsync
            },
            new CommandSpec { Name = "delete", Summary = HelpText.Summary(HelpText.Delete), Help = HelpText.Delete, RequiresAdmin = true, MinPositionals = 1, MaxPositionals = 1, Handler = DeleteAsync },
            new CommandSpec { Name = "start", Summary = HelpText.Summary(HelpText.Start), Help = HelpText.Start, RequiresAdmin = true, MinPositionals = 1, MaxPositionals = 1, ValueOptions = new[] { "timeout" }, Handler = StartAsync },
            new CommandSpec { Name = "stop", Summary = HelpText.Summary(HelpText.Stop), Help = HelpText.Stop, RequiresAdmin = true, MinPositionals = 1, MaxPositionals = 1, Handler = StopAsync },
            new CommandSpec { Name = "restart", Summary = HelpText.Summary(HelpText.Restart), Help = HelpText.Restart, RequiresAdmin = true, MinPositionals = 1, MaxPositionals = 1, ValueOptions = new[] { "timeout" }, Handler = RestartAsync },
            new CommandSpec
            {
                Name = "logs", Summary = HelpText.Summary(HelpText.Logs), Help = HelpText.Logs, MinPositionals = 1, MaxPositionals = 1,
                ValueOptions = new[] { "tail", "stream", "since" }, FlagOptions = new[] { "raw", "follow" },
                ShortAliases = new Dictionary<string, string> { ["f"] = "follow" }, Handler = LogsAsync
            },
            new CommandSpec
            {
                Name = "export", Summary = HelpText.Summary(HelpText.Export), Help = HelpText.Export, MaxPositionals = int.MaxValue,
                ValueOptions = new[] { "output" }, FlagOptions = new[] { "show-secrets" }, ShortAliases = new Dictionary<string, string> { ["o"] = "output" }, Handler = ExportAsync
            },
            new CommandSpec
            {
                // 是否需要管理员取决于是否 --dry-run 以及是否有实际修改，由处理函数自行检查
                Name = "apply", Summary = HelpText.Summary(HelpText.Apply), Help = HelpText.Apply,
                ValueOptions = new[] { "file" }, FlagOptions = new[] { "dry-run", "prune", "restart" },
                ShortAliases = new Dictionary<string, string> { ["f"] = "file" }, Handler = ApplyAsync
            },
            new CommandSpec
            {
                Name = "doctor", Summary = HelpText.Summary(HelpText.Doctor), Help = HelpText.Doctor, MaxPositionals = int.MaxValue,
                FlagOptions = new[] { "all" }, Handler = DoctorAsync
            },
            new CommandSpec { Name = "migrate", Summary = HelpText.Summary(HelpText.Migrate), Help = HelpText.Migrate, RequiresAdmin = true, Handler = MigrateAsync },
            new CommandSpec { Name = "version", Summary = HelpText.Summary(HelpText.Version), Help = HelpText.Version, Handler = VersionAsync },
            new CommandSpec { Name = "help", Summary = HelpText.Summary(HelpText.HelpCommand), Help = HelpText.HelpCommand, MaxPositionals = 1, Handler = HelpAsync },
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
            catch (Exception ex) when (ToCliException(ex, spec?.Name) is { } mapped)
            {
                Console.Error.WriteLine($"错误: {mapped.Message}");
                return mapped.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"错误: {ex.Message}");
                return ExitCodes.Failure;
            }
        }

        private static CommandSpec? FindCommand(string name) =>
            Commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 应用层异常 → 带退出码与中文说明的 CLI 异常
        /// </summary>
        private static CliException? ToCliException(Exception ex, string? command) => ex switch
        {
            ServiceNotFoundException nf => new CliException(ExitCodes.NotFound,
                $"未找到服务 '{nf.Key}'。运行 '{HelpText.Tool} list' 查看所有服务。"),
            AmbiguousServiceNameException am => new UsageException(
                $"名称 '{am.Name}' 匹配到多个服务（{string.Join(", ", am.Ids)}），请改用服务 ID。"),
            ServiceStartException se when se.Kind == StartFailureKind.StoppedDuringStart => new CliException(ExitCodes.Failure,
                $"服务 '{se.ServiceId}' 启动后立即停止" + (se.Reason != null ? $"：{se.Reason}" : "（目标程序可能已退出）") +
                $"。可运行 '{HelpText.Tool} logs {se.ServiceId}' 查看日志。"),
            ServiceStartException se => new CliException(ExitCodes.Failure,
                $"服务 '{se.ServiceId}' 在规定时间内未进入运行状态（当前: {se.State}）。可运行 '{HelpText.Tool} logs {se.ServiceId}' 查看原因。"),
            ServiceStopException st => new CliException(ExitCodes.Failure, $"服务 '{st.ServiceId}' 在 30 秒内未能停止。"),
            ConfigFileException cf => new UsageException(cf.Message, command),
            _ => null
        };

        private static async Task<ServiceOperations> OpenAsync() => await ServiceOperations.OpenAsync();

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
    }
}
