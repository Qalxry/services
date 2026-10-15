using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Services.Core.Helpers;
using Services.Core.Models;

namespace Services.Core.Configuration
{
    /// <summary>
    /// 配置文件内容或计划无效（格式错误、字段取值非法、名称冲突、程序不存在等）。消息包含出错位置，可直接展示给用户。
    /// </summary>
    public sealed class ConfigFileException : Exception
    {
        public ConfigFileException(string message) : base(message) { }
    }

    /// <summary>
    /// 声明式配置文件（servicesctl export / apply）。按服务名称匹配，不包含服务 ID、运行状态与密码。
    /// 条目中未写出的字段视为默认值。
    /// </summary>
    public sealed class ConfigFile
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;
        public List<ServiceDefinition> Services { get; set; } = new();
    }

    public sealed class ServiceDefinition
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Exe { get; set; }
        public string? Args { get; set; }
        public string? WorkingDir { get; set; }
        /// <summary>auto / delayed-auto / manual / disabled，默认 auto</summary>
        public string? Startup { get; set; }
        /// <summary>localsystem / localservice / networkservice / virtual / 用户名，默认 localsystem</summary>
        public string? Account { get; set; }
        public Dictionary<string, string>? Environment { get; set; }
        /// <summary>
        /// 敏感环境变量。值为 null 表示沿用服务中已保存的值（export 默认不导出值）；新建服务时必须提供值。
        /// </summary>
        public Dictionary<string, string?>? SecretEnvironment { get; set; }
        public List<string>? Dependencies { get; set; }
        public bool AutoRestart { get; set; }
        public RestartPolicyDefinition? RestartPolicy { get; set; }
        public StopPolicyDefinition? StopPolicy { get; set; }
        /// <summary>资源限制；省略或各项为 0 表示不限制</summary>
        public ResourceLimitsDefinition? ResourceLimits { get; set; }
    }

    public sealed class ResourceLimitsDefinition
    {
        public int MemoryMB { get; set; }
        public int ProcessMemoryMB { get; set; }
        public int CpuPercent { get; set; }
        public int MaxProcesses { get; set; }
    }

    public sealed class RestartPolicyDefinition
    {
        public int DelaySeconds { get; set; } = RestartPolicyDefaults.DelaySeconds;
        public int MaxRestarts { get; set; } = RestartPolicyDefaults.MaxRestarts;
        public int ResetMinutes { get; set; } = RestartPolicyDefaults.ResetMinutes;
        public bool Backoff { get; set; } = RestartPolicyDefaults.Backoff;
    }

    public sealed class StopPolicyDefinition
    {
        /// <summary>kill / ctrl-c / command，默认 kill</summary>
        public string Mode { get; set; } = "kill";
        public string? Command { get; set; }
        public int TimeoutSeconds { get; set; } = StopPolicyDefaults.TimeoutSeconds;
    }

    public sealed class ApplyResult
    {
        public bool Ok { get; set; } = true;
        public bool DryRun { get; set; }
        public List<ApplyActionResult> Actions { get; set; } = new();
        public string Message { get; set; } = "";
    }

    public sealed class ApplyActionResult
    {
        /// <summary>create / update / delete / unchanged</summary>
        public string Action { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Id { get; set; }
        /// <summary>update 时有差异的字段（与配置文件字段名一致）</summary>
        public List<string> Changes { get; set; } = new();
        /// <summary>是否已执行（dry-run 或执行中途失败时为 false）</summary>
        public bool Done { get; set; }
        /// <summary>update 且指定 --restart 时是否重启了运行中的服务</summary>
        public bool Restarted { get; set; }
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true)]
    [JsonSerializable(typeof(ConfigFile))]
    [JsonSerializable(typeof(ApplyResult))]
    public partial class ConfigFileJsonContext : JsonSerializerContext
    {
        public static ConfigFileJsonContext Readable { get; } = new(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // 拒绝未知字段，避免拼写错误被静默忽略
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>
    /// 配置文件条目与 Service / ServiceConfig 的相互转换及差异比较（纯逻辑，便于测试）
    /// </summary>
    public static class ConfigFileMapper
    {
        public static string Serialize(ConfigFile file) =>
            JsonSerializer.Serialize(file, ConfigFileJsonContext.Readable.ConfigFile);

        /// <summary>
        /// 解析配置文件；格式错误时抛出 ConfigFileException，消息包含出错位置
        /// </summary>
        public static ConfigFile Deserialize(string json)
        {
            try
            {
                return JsonSerializer.Deserialize(json, ConfigFileJsonContext.Readable.ConfigFile)
                    ?? throw new ConfigFileException("配置文件为空。");
            }
            catch (JsonException ex)
            {
                var location = ex.Path != null ? $"（位置: {ex.Path}，行 {ex.LineNumber + 1}）" : "";
                throw new ConfigFileException($"配置文件格式错误{location}: {ex.Message}");
            }
        }

        /// <summary>
        /// ToConfig 中表示“沿用已保存的敏感变量值”的占位值（含 NUL，不可能是合法的变量值），由 ApplyPlanner 解析
        /// </summary>
        public const string KeepExistingSecret = "\0<keep>";

        /// <param name="includeSecretValues">是否导出敏感环境变量的值；否则只导出变量名（值为 null）</param>
        public static ServiceDefinition ToDefinition(Service service, bool includeSecretValues = false)
        {
            return new ServiceDefinition
            {
                Name = service.Name,
                Description = string.IsNullOrWhiteSpace(service.Description) ? null : service.Description,
                Exe = service.ExePath,
                Args = string.IsNullOrWhiteSpace(service.Args) ? null : service.Args,
                WorkingDir = string.IsNullOrWhiteSpace(service.WorkingDir) ? null : service.WorkingDir,
                Startup = FormatStartup(service.StartupType, service.DelayedAutoStart),
                Account = FormatAccount(service.AccountType, service.AccountName),
                Environment = service.Environment.Count == 0
                    ? null
                    : service.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
                SecretEnvironment = service.SecretEnvironment.Count == 0
                    ? null
                    : service.SecretEnvironment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(p => p.Key, p => includeSecretValues ? p.Value : null, StringComparer.OrdinalIgnoreCase),
                Dependencies = service.Dependencies.Count == 0 ? null : service.Dependencies.ToList(),
                AutoRestart = service.AutoRestart,
                RestartPolicy = new RestartPolicyDefinition
                {
                    DelaySeconds = service.RestartDelaySeconds,
                    MaxRestarts = service.MaxRestarts,
                    ResetMinutes = service.RestartResetMinutes,
                    Backoff = service.RestartBackoff
                },
                StopPolicy = new StopPolicyDefinition
                {
                    Mode = FormatStopMode(service.StopMode),
                    Command = string.IsNullOrWhiteSpace(service.StopCommand) ? null : service.StopCommand,
                    TimeoutSeconds = service.StopTimeoutSeconds
                },
                ResourceLimits = service.MemoryLimitMB == 0 && service.ProcessMemoryLimitMB == 0 &&
                                 service.CpuLimitPercent == 0 && service.MaxProcesses == 0
                    ? null
                    : new ResourceLimitsDefinition
                    {
                        MemoryMB = service.MemoryLimitMB,
                        ProcessMemoryMB = service.ProcessMemoryLimitMB,
                        CpuPercent = service.CpuLimitPercent,
                        MaxProcesses = service.MaxProcesses
                    }
            };
        }

        /// <summary>
        /// 将条目转换为 ServiceConfig，并校验取值范围。path 用于错误消息（如 services[2]）。
        /// </summary>
        public static ServiceConfig ToConfig(ServiceDefinition definition, string path)
        {
            if (string.IsNullOrWhiteSpace(definition.Name))
                throw new ConfigFileException($"{path}.name 不能为空。");
            if (string.IsNullOrWhiteSpace(definition.Exe))
                throw new ConfigFileException($"{path}.exe 不能为空。");

            var restart = definition.RestartPolicy ?? new RestartPolicyDefinition();
            var stop = definition.StopPolicy ?? new StopPolicyDefinition();
            var limits = definition.ResourceLimits ?? new ResourceLimitsDefinition();
            var (startupType, delayed) = ParseStartup(definition.Startup, path);
            var (accountType, accountName) = ParseAccount(definition.Account);

            var config = new ServiceConfig
            {
                Name = definition.Name.Trim(),
                ExePath = definition.Exe.Trim(),
                Args = definition.Args?.Trim() ?? "",
                // 与保存时一致：未指定工作目录时使用程序所在目录，避免每次 apply 都被判定为有差异
                WorkingDir = string.IsNullOrWhiteSpace(definition.WorkingDir)
                    ? Path.GetDirectoryName(definition.Exe.Trim()) ?? ""
                    : definition.WorkingDir.Trim(),
                Description = definition.Description?.Trim() ?? "",
                StartupType = startupType,
                DelayedAutoStart = delayed,
                AccountType = accountType,
                AccountName = accountName,
                Environment = EnvironmentVariables.Clone(definition.Environment),
                SecretEnvironment = (definition.SecretEnvironment ?? new Dictionary<string, string?>())
                    .ToDictionary(p => p.Key.Trim(), p => p.Value ?? KeepExistingSecret, StringComparer.OrdinalIgnoreCase),
                Dependencies = (definition.Dependencies ?? new List<string>()).Select(d => d.Trim()).ToList(),
                AutoRestart = definition.AutoRestart,
                RestartDelaySeconds = CheckRange(restart.DelaySeconds, 1, 3600, $"{path}.restartPolicy.delaySeconds"),
                MaxRestarts = CheckRange(restart.MaxRestarts, 0, 1000, $"{path}.restartPolicy.maxRestarts"),
                RestartResetMinutes = CheckRange(restart.ResetMinutes, 1, 1440, $"{path}.restartPolicy.resetMinutes"),
                RestartBackoff = restart.Backoff,
                StopMode = ParseStopMode(stop.Mode, path),
                StopCommand = stop.Command?.Trim() ?? "",
                StopTimeoutSeconds = CheckRange(stop.TimeoutSeconds, 1, StopPolicyDefaults.MaxTimeoutSeconds, $"{path}.stopPolicy.timeoutSeconds"),
                MemoryLimitMB = CheckRange(limits.MemoryMB, 0, ResourceLimitRanges.MaxMemoryMB, $"{path}.resourceLimits.memoryMB"),
                ProcessMemoryLimitMB = CheckRange(limits.ProcessMemoryMB, 0, ResourceLimitRanges.MaxMemoryMB, $"{path}.resourceLimits.processMemoryMB"),
                CpuLimitPercent = CheckRange(limits.CpuPercent, 0, ResourceLimitRanges.MaxCpuPercent, $"{path}.resourceLimits.cpuPercent"),
                MaxProcesses = CheckRange(limits.MaxProcesses, 0, ResourceLimitRanges.MaxProcesses, $"{path}.resourceLimits.maxProcesses")
            };

            if (config.StopMode == ServiceStopMode.Command && string.IsNullOrWhiteSpace(config.StopCommand))
                throw new ConfigFileException($"{path}.stopPolicy.command 不能为空（mode 为 command）。");
            try
            {
                EnvironmentVariables.Validate(config.Environment);
            }
            catch (ArgumentException ex)
            {
                throw new ConfigFileException($"{path}.environment: {ex.Message}");
            }
            try
            {
                EnvironmentVariables.Validate(config.SecretEnvironment
                    .Where(p => p.Value != KeepExistingSecret)
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
                foreach (var key in config.SecretEnvironment.Keys.Where(k => string.IsNullOrWhiteSpace(k)))
                    throw new ArgumentException("变量名不能为空。");
            }
            catch (ArgumentException ex)
            {
                throw new ConfigFileException($"{path}.secretEnvironment: {ex.Message}");
            }
            var overlap = config.Environment.Keys.FirstOrDefault(config.SecretEnvironment.ContainsKey);
            if (overlap != null)
                throw new ConfigFileException($"{path}: 变量 '{overlap}' 不能同时出现在 environment 与 secretEnvironment 中。");

            return config;
        }

        /// <summary>
        /// 比较当前配置与目标配置，返回有差异的字段名（与配置文件字段名一致，按固定顺序）
        /// </summary>
        public static List<string> Diff(ServiceConfig current, ServiceConfig desired)
        {
            var changes = new List<string>();
            void Check(string field, bool equal) { if (!equal) changes.Add(field); }

            Check("name", current.Name == desired.Name);
            Check("description", Normalize(current.Description) == Normalize(desired.Description));
            Check("exe", string.Equals(current.ExePath, desired.ExePath, StringComparison.OrdinalIgnoreCase));
            Check("args", Normalize(current.Args) == Normalize(desired.Args));
            Check("workingDir", string.Equals(Normalize(current.WorkingDir), Normalize(desired.WorkingDir), StringComparison.OrdinalIgnoreCase));
            Check("startup", current.StartupType == desired.StartupType && current.DelayedAutoStart == desired.DelayedAutoStart);
            Check("account", current.AccountType == desired.AccountType &&
                (desired.AccountType != ServiceAccountType.User ||
                 string.Equals(current.AccountName, desired.AccountName, StringComparison.OrdinalIgnoreCase)));
            Check("environment", EnvironmentEquals(current.Environment, desired.Environment));
            Check("secretEnvironment", EnvironmentEquals(current.SecretEnvironment, desired.SecretEnvironment));
            Check("dependencies", current.Dependencies.Select(d => d.ToUpperInvariant()).OrderBy(d => d, StringComparer.Ordinal)
                .SequenceEqual(desired.Dependencies.Select(d => d.ToUpperInvariant()).OrderBy(d => d, StringComparer.Ordinal)));
            Check("autoRestart", current.AutoRestart == desired.AutoRestart);
            Check("restartPolicy", current.RestartDelaySeconds == desired.RestartDelaySeconds &&
                current.MaxRestarts == desired.MaxRestarts &&
                current.RestartResetMinutes == desired.RestartResetMinutes &&
                current.RestartBackoff == desired.RestartBackoff);
            Check("stopPolicy", current.StopMode == desired.StopMode &&
                Normalize(current.StopCommand) == Normalize(desired.StopCommand) &&
                current.StopTimeoutSeconds == desired.StopTimeoutSeconds);
            Check("resourceLimits", current.MemoryLimitMB == desired.MemoryLimitMB &&
                current.ProcessMemoryLimitMB == desired.ProcessMemoryLimitMB &&
                current.CpuLimitPercent == desired.CpuLimitPercent &&
                current.MaxProcesses == desired.MaxProcesses);
            return changes;
        }

        public static string FormatStartup(ServiceStartupType type, bool delayed) => type switch
        {
            ServiceStartupType.Auto => delayed ? "delayed-auto" : "auto",
            ServiceStartupType.Manual => "manual",
            ServiceStartupType.Disabled => "disabled",
            _ => "auto"
        };

        public static string FormatAccount(ServiceAccountType type, string? name) => type switch
        {
            ServiceAccountType.LocalService => "localservice",
            ServiceAccountType.NetworkService => "networkservice",
            ServiceAccountType.Virtual => "virtual",
            ServiceAccountType.User => name ?? "",
            _ => "localsystem"
        };

        public static string FormatStopMode(ServiceStopMode mode) => mode switch
        {
            ServiceStopMode.CtrlC => "ctrl-c",
            ServiceStopMode.Command => "command",
            _ => "kill"
        };

        private static (ServiceStartupType Type, bool Delayed) ParseStartup(string? value, string path) =>
            (value ?? "auto").Trim().ToLowerInvariant() switch
            {
                "auto" => (ServiceStartupType.Auto, false),
                "delayed-auto" => (ServiceStartupType.Auto, true),
                "manual" => (ServiceStartupType.Manual, false),
                "disabled" => (ServiceStartupType.Disabled, false),
                _ => throw new ConfigFileException($"{path}.startup 只能是 auto、delayed-auto、manual 或 disabled，实际为 '{value}'。")
            };

        private static (ServiceAccountType Type, string? Name) ParseAccount(string? value)
        {
            var account = (value ?? "localsystem").Trim();
            var type = account.ToLowerInvariant() switch
            {
                "" or "localsystem" => ServiceAccountType.LocalSystem,
                "localservice" => ServiceAccountType.LocalService,
                "networkservice" => ServiceAccountType.NetworkService,
                "virtual" => ServiceAccountType.Virtual,
                _ => ServiceAccountType.User
            };
            return (type, type == ServiceAccountType.User ? account : null);
        }

        private static ServiceStopMode ParseStopMode(string? value, string path) =>
            (value ?? "kill").Trim().ToLowerInvariant() switch
            {
                "kill" => ServiceStopMode.Kill,
                "ctrl-c" => ServiceStopMode.CtrlC,
                "command" => ServiceStopMode.Command,
                _ => throw new ConfigFileException($"{path}.stopPolicy.mode 只能是 kill、ctrl-c 或 command，实际为 '{value}'。")
            };

        private static int CheckRange(int value, int min, int max, string field)
        {
            if (value < min || value > max)
                throw new ConfigFileException($"{field} 必须在 {min} 到 {max} 之间，实际为 {value}。");
            return value;
        }

        private static string Normalize(string? value) => (value ?? "").Trim();

        private static bool EnvironmentEquals(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var (key, value) in a)
            {
                if (!b.TryGetValue(key, out var other) || other != value) return false;
            }
            return true;
        }
    }
}
