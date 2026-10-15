using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Services.Core.Helpers;
using Services.Core.Models;

namespace Services.Cli
{
    /// <summary>
    /// 声明式配置文件（servicesctl export / apply）。按服务名称匹配，不包含服务 ID、运行状态与密码。
    /// 条目中未写出的字段视为默认值。
    /// </summary>
    internal sealed class ConfigFile
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;
        public List<ServiceDefinition> Services { get; set; } = new();
    }

    internal sealed class ServiceDefinition
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
        public List<string>? Dependencies { get; set; }
        public bool AutoRestart { get; set; }
        public RestartPolicyDefinition? RestartPolicy { get; set; }
        public StopPolicyDefinition? StopPolicy { get; set; }
    }

    internal sealed class RestartPolicyDefinition
    {
        public int DelaySeconds { get; set; } = RestartPolicyDefaults.DelaySeconds;
        public int MaxRestarts { get; set; } = RestartPolicyDefaults.MaxRestarts;
        public int ResetMinutes { get; set; } = RestartPolicyDefaults.ResetMinutes;
        public bool Backoff { get; set; } = RestartPolicyDefaults.Backoff;
    }

    internal sealed class StopPolicyDefinition
    {
        /// <summary>kill / ctrl-c / command，默认 kill</summary>
        public string Mode { get; set; } = "kill";
        public string? Command { get; set; }
        public int TimeoutSeconds { get; set; } = StopPolicyDefaults.TimeoutSeconds;
    }

    internal sealed class ApplyResult
    {
        public bool Ok { get; set; } = true;
        public bool DryRun { get; set; }
        public List<ApplyActionResult> Actions { get; set; } = new();
        public string Message { get; set; } = "";
    }

    internal sealed class ApplyActionResult
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
    internal partial class ConfigFileJsonContext : JsonSerializerContext
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
    internal static class ConfigFileMapper
    {
        public static string Serialize(ConfigFile file) =>
            JsonSerializer.Serialize(file, ConfigFileJsonContext.Readable.ConfigFile);

        /// <summary>
        /// 解析配置文件；格式错误时抛出 UsageException，消息包含出错位置
        /// </summary>
        public static ConfigFile Deserialize(string json)
        {
            try
            {
                return JsonSerializer.Deserialize(json, ConfigFileJsonContext.Readable.ConfigFile)
                    ?? throw new UsageException("配置文件为空。", "apply");
            }
            catch (JsonException ex)
            {
                var location = ex.Path != null ? $"（位置: {ex.Path}，行 {ex.LineNumber + 1}）" : "";
                throw new UsageException($"配置文件格式错误{location}: {ex.Message}", "apply");
            }
        }

        public static ServiceDefinition ToDefinition(Service service)
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
                }
            };
        }

        /// <summary>
        /// 将条目转换为 ServiceConfig，并校验取值范围。path 用于错误消息（如 services[2]）。
        /// </summary>
        public static ServiceConfig ToConfig(ServiceDefinition definition, string path)
        {
            if (string.IsNullOrWhiteSpace(definition.Name))
                throw new UsageException($"{path}.name 不能为空。", "apply");
            if (string.IsNullOrWhiteSpace(definition.Exe))
                throw new UsageException($"{path}.exe 不能为空。", "apply");

            var restart = definition.RestartPolicy ?? new RestartPolicyDefinition();
            var stop = definition.StopPolicy ?? new StopPolicyDefinition();
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
                Dependencies = (definition.Dependencies ?? new List<string>()).Select(d => d.Trim()).ToList(),
                AutoRestart = definition.AutoRestart,
                RestartDelaySeconds = CheckRange(restart.DelaySeconds, 1, 3600, $"{path}.restartPolicy.delaySeconds"),
                MaxRestarts = CheckRange(restart.MaxRestarts, 0, 1000, $"{path}.restartPolicy.maxRestarts"),
                RestartResetMinutes = CheckRange(restart.ResetMinutes, 1, 1440, $"{path}.restartPolicy.resetMinutes"),
                RestartBackoff = restart.Backoff,
                StopMode = ParseStopMode(stop.Mode, path),
                StopCommand = stop.Command?.Trim() ?? "",
                StopTimeoutSeconds = CheckRange(stop.TimeoutSeconds, 1, StopPolicyDefaults.MaxTimeoutSeconds, $"{path}.stopPolicy.timeoutSeconds")
            };

            if (config.StopMode == ServiceStopMode.Command && string.IsNullOrWhiteSpace(config.StopCommand))
                throw new UsageException($"{path}.stopPolicy.command 不能为空（mode 为 command）。", "apply");
            try
            {
                EnvironmentVariables.Validate(config.Environment);
            }
            catch (ArgumentException ex)
            {
                throw new UsageException($"{path}.environment: {ex.Message}", "apply");
            }

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
                _ => throw new UsageException($"{path}.startup 只能是 auto、delayed-auto、manual 或 disabled，实际为 '{value}'。", "apply")
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
                _ => throw new UsageException($"{path}.stopPolicy.mode 只能是 kill、ctrl-c 或 command，实际为 '{value}'。", "apply")
            };

        private static int CheckRange(int value, int min, int max, string field)
        {
            if (value < min || value > max)
                throw new UsageException($"{field} 必须在 {min} 到 {max} 之间，实际为 {value}。", "apply");
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

    /// <summary>
    /// apply 的变更计划（纯逻辑，文件存在性检查由调用方注入，便于测试）
    /// </summary>
    internal static class ApplyPlanner
    {
        /// <summary>
        /// 按名称（不区分大小写）匹配已有服务，生成创建 / 更新 / 删除 / 不变的计划，并在执行前完成全部校验
        /// </summary>
        public static List<ApplyPlanItem> Build(IReadOnlyList<ServiceConfig> desired, IReadOnlyList<Service> existing, bool prune, Func<string, bool> fileExists)
        {
            var plan = new List<ApplyPlanItem>();

            foreach (var config in desired)
            {
                var matches = existing.Where(s => string.Equals(s.Name, config.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count > 1)
                    throw new UsageException($"名称 '{config.Name}' 匹配到多个已有服务（{string.Join(", ", matches.Select(s => s.Id))}），请先重命名或删除重复的服务。", "apply");

                if (matches.Count == 0)
                {
                    RejectUserAccount(config, "创建");
                    plan.Add(new ApplyPlanItem("create", config.Name, null, new List<string>(), config));
                    continue;
                }

                var service = matches[0];
                var changes = ConfigFileMapper.Diff(ServiceConfig.FromService(service), config);
                if (changes.Contains("account")) RejectUserAccount(config, "切换到");
                if (config.AccountType == ServiceAccountType.User && service.AccountType == ServiceAccountType.User)
                {
                    // 同一用户账户：沿用 SCM 中保存的账户名与密码
                    config.AccountName = service.AccountName;
                }

                plan.Add(new ApplyPlanItem(changes.Count == 0 ? "unchanged" : "update", config.Name, service.Id, changes, config));
            }

            if (prune)
            {
                foreach (var service in existing)
                {
                    if (!desired.Any(c => string.Equals(c.Name, service.Name, StringComparison.OrdinalIgnoreCase)))
                        plan.Add(new ApplyPlanItem("delete", service.Name, service.Id, new List<string>(), null));
                }
            }

            foreach (var item in plan.Where(p => p.Action is "create" or "update"))
            {
                if (!fileExists(item.Config!.ExePath))
                    throw new UsageException($"服务 '{item.Name}' 的可执行文件不存在: {item.Config.ExePath}", "apply");
            }
            return plan;
        }

        /// <summary>
        /// 指定用户需要密码，配置文件中不保存密码，因此 apply 不负责创建或切换到用户账户
        /// </summary>
        private static void RejectUserAccount(ServiceConfig config, string verb)
        {
            if (config.AccountType != ServiceAccountType.User) return;
            throw new UsageException(
                $"服务 '{config.Name}' 需要{verb}用户账户 '{config.AccountName}'，配置文件不包含密码，无法通过 apply 完成。" +
                $"请先使用 '{HelpText.Tool} update <服务> --account {config.AccountName} --password-stdin' 设置账户。", "apply");
        }

        public static string ToActionText(string action) => action switch
        {
            "create" => "创建",
            "update" => "更新",
            "delete" => "删除",
            _ => "保持"
        };
    }

    internal sealed class ApplyPlanItem
    {
        public string Action { get; }
        public string Name { get; }
        public string? Id { get; set; }
        public List<string> Changes { get; }
        public ServiceConfig? Config { get; }
        public bool Done { get; set; }
        public bool Restarted { get; set; }

        public ApplyPlanItem(string action, string name, string? id, List<string> changes, ServiceConfig? config)
        {
            Action = action;
            Name = name;
            Id = id;
            Changes = changes;
            Config = config;
        }

        public ApplyActionResult ToResult() => new()
        {
            Action = Action,
            Name = Name,
            Id = Id,
            Changes = Changes,
            Done = Done,
            Restarted = Restarted
        };

        public string ToText(bool dryRun)
        {
            var symbol = Action switch { "create" => "+", "update" => "~", "delete" => "-", _ => "=" };
            var detail = Action == "update" ? $"（{string.Join(", ", Changes)}）" : "";
            var id = Id != null ? $" [{Id}]" : "";
            var status = dryRun || Action == "unchanged" ? "" : Done ? (Restarted ? "  已完成，已重启" : "  已完成") : "  未执行";
            return $"{symbol} {ApplyPlanner.ToActionText(Action)} {Name}{id}{detail}{status}";
        }
    }
}
