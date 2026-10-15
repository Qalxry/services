using Services.Core.Helpers;
using Services.Core.Models;
using Services.Core.Services;

namespace Services.Cli
{
    /// <summary>
    /// create / update 共用的配置选项解析
    /// </summary>
    internal static partial class CliApp
    {
        // 旧版本的包装器（GUI 程序兼容 --service-wrapper），找不到独立包装器 servicesvc.exe 时使用
        private const string LegacyWrapperExeName = "ServicesApp.exe";

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

            // 环境变量按 clear → unset → set 的顺序处理，因此可在一条命令中重置并重新设置。
            // 普通与敏感变量共用一个命名空间：--env 设置的变量从敏感变量中移除，反之亦然
            var unsetEnv = args.GetValues("unset-env");
            var setEnv = args.GetValues("env");
            var setSecretEnv = args.GetValues("secret-env");
            if (args.HasFlag("clear-env") || unsetEnv.Count > 0 || setEnv.Count > 0 || setSecretEnv.Count > 0)
            {
                var environment = EnvironmentVariables.Clone(config.Environment);
                var secrets = EnvironmentVariables.Clone(config.SecretEnvironment);
                if (args.HasFlag("clear-env"))
                {
                    environment.Clear();
                    secrets.Clear();
                }
                foreach (var key in unsetEnv)
                {
                    if (string.IsNullOrWhiteSpace(key))
                        throw new UsageException("--unset-env 的变量名不能为空。", args.Command);
                    environment.Remove(key.Trim());
                    secrets.Remove(key.Trim());
                }
                foreach (var (key, value) in ParseEnvEntries(setEnv, "--env", args.Command))
                {
                    secrets.Remove(key);
                    environment[key] = value;
                }
                foreach (var (key, value) in ParseEnvEntries(setSecretEnv, "--secret-env", args.Command))
                {
                    environment.Remove(key);
                    secrets[key] = value;
                }
                config.Environment = environment;
                config.SecretEnvironment = secrets;
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

        private static List<KeyValuePair<string, string>> ParseEnvEntries(IEnumerable<string> entries, string option, string command)
        {
            var result = new List<KeyValuePair<string, string>>();
            foreach (var entry in entries)
            {
                try
                {
                    result.Add(EnvironmentVariables.ParseEntry(entry));
                }
                catch (ArgumentException ex)
                {
                    throw new UsageException($"{option} 参数错误：{ex.Message}（应为 KEY=VALUE）", command);
                }
            }
            return result;
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

        /// <summary>
        /// 启动服务并等待进入运行状态。服务在启动过程中停止（目标程序不存在、立即退出等）时立即失败，
        /// 并附上包装器记录的失败原因，而不是等到超时。
        /// </summary>
    }
}
