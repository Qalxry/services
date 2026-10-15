using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using Services.Core.Helpers;
using Services.Core.Models;
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
        public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
        public RestartPolicySettings Restart { get; init; } = RestartPolicySettings.Default;
        public StopPolicySettings Stop { get; init; } = StopPolicySettings.Default;

        public static WrapperSettings Load(string serviceId)
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPaths.Parameters(serviceId))
                ?? throw new InvalidOperationException("Service configuration not found in registry");

            var exePath = key.GetValue(ParameterNames.ExePath) as string;
            if (string.IsNullOrEmpty(exePath)) throw new InvalidOperationException("ExePath is missing in registry");

            var workingDir = key.GetValue(ParameterNames.WorkingDir) as string;
            return new WrapperSettings
            {
                ExePath = exePath,
                Args = ServiceUtils.NormalizeArgs(key.GetValue(ParameterNames.Args) as string),
                WorkingDir = string.IsNullOrEmpty(workingDir) ? Path.GetDirectoryName(exePath) ?? "" : workingDir,
                Environment = Try(() => EnvironmentVariables.ParseLenient(key.GetValue(ParameterNames.Environment) as string[]),
                    new Dictionary<string, string>()),
                Restart = Try(() => LoadRestartPolicy(key), RestartPolicySettings.Default),
                Stop = Try(() => LoadStopPolicy(key), StopPolicySettings.Default)
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

        private static T Try<T>(Func<T> load, T fallback)
        {
            try { return load(); }
            catch { return fallback; }
        }
    }
}
