using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using Services.Core.Helpers;
using Services.Core.Models;
using Services.Core.Notifications;
using Services.Core.Storage;

namespace Services.Core.Wrapper
{
    /// <summary>
    /// 停止策略（来自服务参数）
    /// </summary>
    public sealed record StopPolicySettings(ServiceStopMode Mode, string Command, int TimeoutSeconds)
    {
        public static StopPolicySettings Default { get; } = new(ServiceStopMode.Kill, "", StopPolicyDefaults.TimeoutSeconds);
    }

    /// <summary>
    /// 健康检查配置（来自服务参数）
    /// </summary>
    public sealed record HealthCheckSettings(HealthCheckType Type, string Target, int IntervalSeconds, int TimeoutSeconds,
        int FailureThreshold, int StartPeriodSeconds)
    {
        public static HealthCheckSettings None { get; } = new(HealthCheckType.None, "", HealthCheckDefaults.IntervalSeconds,
            HealthCheckDefaults.TimeoutSeconds, HealthCheckDefaults.FailureThreshold, HealthCheckDefaults.StartPeriodSeconds);

        public bool Enabled => Type != HealthCheckType.None;
    }

    /// <summary>
    /// 包装器启动时从注册表 Services\&lt;ID&gt;\Parameters 读取的全部配置。
    /// 程序路径缺失时抛出异常（服务无法运行）；其余字段读取失败时使用默认值。
    /// </summary>
    public sealed class WrapperSettings
    {
        public required string ExePath { get; init; }
        /// <summary>多行参数已合并为一行</summary>
        public required string Args { get; init; }
        /// <summary>未配置时为程序所在目录</summary>
        public required string WorkingDir { get; init; }
        /// <summary>普通与敏感环境变量合并后的结果</summary>
        public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
        /// <summary>配置了敏感环境变量但运行账户无权读取（ACL 被修改等），这些变量未设置</summary>
        public bool SecretsUnavailable { get; init; }
        public RestartPolicySettings Restart { get; init; } = RestartPolicySettings.Default;
        public StopPolicySettings Stop { get; init; } = StopPolicySettings.Default;
        public ResourceLimits Limits { get; init; } = ResourceLimits.None;
        public HealthCheckSettings Health { get; init; } = HealthCheckSettings.None;
        /// <summary>服务名称（显示名称），用于通知</summary>
        public string DisplayName { get; init; } = "";
        /// <summary>生效的 Webhook（服务级优先，其次全局）</summary>
        public WebhookSettings Webhook { get; init; } = WebhookSettings.None;

        public static WrapperSettings Load(string serviceId)
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPaths.Parameters(serviceId))
                ?? throw new InvalidOperationException("Service configuration not found in registry");

            var exePath = key.GetValue(ParameterNames.ExePath) as string;
            if (string.IsNullOrEmpty(exePath)) throw new InvalidOperationException("ExePath is missing in registry");

            var workingDir = key.GetValue(ParameterNames.WorkingDir) as string;
            var environment = Try(() => EnvironmentVariables.ParseLenient(key.GetValue(ParameterNames.Environment) as string[]),
                new Dictionary<string, string>());
            bool hasSecrets = key.GetValue(ParameterNames.SecretEnvironmentNames) is string[] { Length: > 0 };
            var secrets = hasSecrets ? SecretStore.TryRead(serviceId) : null;
            if (secrets != null)
            {
                foreach (var (name, value) in secrets) environment[name] = value;
            }

            return new WrapperSettings
            {
                ExePath = exePath,
                Args = ServiceUtils.NormalizeArgs(key.GetValue(ParameterNames.Args) as string),
                WorkingDir = string.IsNullOrEmpty(workingDir) ? Path.GetDirectoryName(exePath) ?? "" : workingDir,
                Environment = environment,
                SecretsUnavailable = hasSecrets && secrets == null,
                Restart = Try(() => LoadRestartPolicy(key), RestartPolicySettings.Default),
                Stop = Try(() => LoadStopPolicy(key), StopPolicySettings.Default),
                Limits = Try(() => new ResourceLimits(
                    ReadNonNegative(key, ParameterNames.MemoryLimitMB),
                    ReadNonNegative(key, ParameterNames.ProcessMemoryLimitMB),
                    Math.Min(ReadNonNegative(key, ParameterNames.CpuLimitPercent), ResourceLimitRanges.MaxCpuPercent),
                    ReadNonNegative(key, ParameterNames.MaxProcesses)), ResourceLimits.None),
                Health = Try(() => LoadHealthCheck(key), HealthCheckSettings.None),
                DisplayName = key.GetValue(ParameterNames.DisplayName) as string ?? serviceId,
                Webhook = Try(() => WebhookSettings.Resolve(NotificationStore.ReadService(serviceId), NotificationStore.ReadGlobal()),
                    WebhookSettings.None)
            };
        }

        private static RestartPolicySettings LoadRestartPolicy(RegistryKey key)
        {
            var d = RestartPolicySettings.Default;
            return new RestartPolicySettings(
                Enabled: key.GetValue(ParameterNames.AutoRestart) is int enabled && enabled == 1,
                DelaySeconds: key.GetValue(ParameterNames.RestartDelaySeconds) is int delay && delay > 0
                    ? Math.Min(delay, RestartPolicySettings.MaxDelaySeconds) : d.DelaySeconds,
                MaxRestarts: key.GetValue(ParameterNames.MaxRestarts) is int max && max >= 0 ? max : d.MaxRestarts,
                ResetMinutes: key.GetValue(ParameterNames.RestartResetMinutes) is int reset && reset > 0 ? reset : d.ResetMinutes,
                Backoff: key.GetValue(ParameterNames.RestartBackoff) is int backoff ? backoff == 1 : d.Backoff);
        }

        private static StopPolicySettings LoadStopPolicy(RegistryKey key)
        {
            var mode = key.GetValue(ParameterNames.StopMode) is int value && Enum.IsDefined(typeof(ServiceStopMode), value)
                ? (ServiceStopMode)value : ServiceStopMode.Kill;
            var command = key.GetValue(ParameterNames.StopCommand) as string ?? "";
            var timeout = key.GetValue(ParameterNames.StopTimeoutSeconds) is int t && t > 0
                ? Math.Min(t, StopPolicyDefaults.MaxTimeoutSeconds) : StopPolicyDefaults.TimeoutSeconds;

            // 停止命令为空时无法执行，退回直接结束
            if (mode == ServiceStopMode.Command && string.IsNullOrWhiteSpace(command))
                mode = ServiceStopMode.Kill;
            return new StopPolicySettings(mode, command, timeout);
        }

        private static HealthCheckSettings LoadHealthCheck(RegistryKey key)
        {
            var type = key.GetValue(ParameterNames.HealthCheckType) is int value && Enum.IsDefined(typeof(HealthCheckType), value)
                ? (HealthCheckType)value : HealthCheckType.None;
            if (type == HealthCheckType.None) return HealthCheckSettings.None;

            int Read(string name, int fallback, int min, int max) =>
                key.GetValue(name) is int v && v >= min ? Math.Min(v, max) : fallback;

            return new HealthCheckSettings(
                type,
                (key.GetValue(ParameterNames.HealthCheckTarget) as string ?? "").Trim(),
                Read(ParameterNames.HealthCheckIntervalSeconds, HealthCheckDefaults.IntervalSeconds, 1, HealthCheckDefaults.MaxIntervalSeconds),
                Read(ParameterNames.HealthCheckTimeoutSeconds, HealthCheckDefaults.TimeoutSeconds, 1, HealthCheckDefaults.MaxTimeoutSeconds),
                Read(ParameterNames.HealthCheckFailureThreshold, HealthCheckDefaults.FailureThreshold, 1, HealthCheckDefaults.MaxFailureThreshold),
                Read(ParameterNames.HealthCheckStartPeriodSeconds, HealthCheckDefaults.StartPeriodSeconds, 0, HealthCheckDefaults.MaxStartPeriodSeconds));
        }

        private static int ReadNonNegative(RegistryKey key, string name) =>
            key.GetValue(name) is int value && value > 0 ? value : 0;

        private static T Try<T>(Func<T> load, T fallback)
        {
            try { return load(); }
            catch { return fallback; }
        }
    }
}
