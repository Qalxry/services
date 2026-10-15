using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Services.Core.Helpers;
using Services.Core.Models;

namespace Services.Core.Storage
{
    /// <summary>
    /// 服务配置在注册表中的读写：Services\&lt;ID&gt;\Parameters（本工具的配置）、Services\&lt;ID&gt; 下由 SCM 维护的值
    /// （启动类型、账户、依赖、延迟启动、ImagePath），以及本工具的服务索引。
    /// 不涉及运行状态（由 SCM 查询）。
    /// </summary>
    public static class ServiceRegistryStore
    {
        /// <summary>
        /// 写入 Parameters。新建时同时写入创建时间与管理标记。
        /// </summary>
        public static void WriteParameters(string serviceId, ServiceConfig config, bool isNew)
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.CreateSubKey(RegistryPaths.Parameters(serviceId));

            key.SetValue(ParameterNames.ExePath, config.ExePath);
            key.SetValue(ParameterNames.Args, config.Args ?? "");
            key.SetValue(ParameterNames.WorkingDir, string.IsNullOrEmpty(config.WorkingDir)
                ? Path.GetDirectoryName(config.ExePath) ?? ""
                : config.WorkingDir);
            key.SetValue(ParameterNames.DisplayName, config.Name);
            key.SetValue(ParameterNames.Description, config.Description ?? "");
            key.SetValue(ParameterNames.AutoRestart, config.AutoRestart ? 1 : 0);
            key.SetValue(ParameterNames.RestartDelaySeconds, config.RestartDelaySeconds, RegistryValueKind.DWord);
            key.SetValue(ParameterNames.MaxRestarts, config.MaxRestarts, RegistryValueKind.DWord);
            key.SetValue(ParameterNames.RestartResetMinutes, config.RestartResetMinutes, RegistryValueKind.DWord);
            key.SetValue(ParameterNames.RestartBackoff, config.RestartBackoff ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(ParameterNames.StopMode, (int)config.StopMode, RegistryValueKind.DWord);
            key.SetValue(ParameterNames.StopCommand, config.StopCommand ?? "");
            key.SetValue(ParameterNames.StopTimeoutSeconds, config.StopTimeoutSeconds, RegistryValueKind.DWord);
            key.SetValue(ParameterNames.MemoryLimitMB, config.MemoryLimitMB, RegistryValueKind.DWord);
            key.SetValue(ParameterNames.ProcessMemoryLimitMB, config.ProcessMemoryLimitMB, RegistryValueKind.DWord);
            key.SetValue(ParameterNames.CpuLimitPercent, config.CpuLimitPercent, RegistryValueKind.DWord);
            key.SetValue(ParameterNames.MaxProcesses, config.MaxProcesses, RegistryValueKind.DWord);
            key.SetValue(ParameterNames.Environment, EnvironmentVariables.FormatLines(config.Environment), RegistryValueKind.MultiString);
            // 敏感变量只在此记录变量名，值由 SecretStore 写入受保护的子键
            key.SetValue(ParameterNames.SecretEnvironmentNames,
                config.SecretEnvironment.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray(), RegistryValueKind.MultiString);

            if (isNew)
            {
                key.SetValue(ParameterNames.CreatedAt, DateTime.Now.ToString("o"));
                key.SetValue(ParameterNames.ManagedBy, ParameterNames.ManagedByValue);
            }
        }

        /// <summary>
        /// 删除 Parameters（服务本身由 SCM 删除）。失败时忽略。
        /// </summary>
        public static void DeleteParameters(string serviceId)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(RegistryPaths.Service(serviceId), writable: true);
                key?.DeleteSubKeyTree(RegistryPaths.ParametersSubKey, throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to delete Parameters registry key: {ex.Message}");
            }
        }

        public static string? ReadImagePath(string serviceId)
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPaths.Service(serviceId));
            return key?.GetValue(ScmValueNames.ImagePath, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }

        public static bool ServiceKeyExists(string serviceId)
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPaths.Service(serviceId));
            return key != null;
        }

        /// <summary>
        /// 读取一个由本工具管理的服务的配置（不含运行状态）。服务已不存在或不是本工具创建的（无 ExePath）时返回 null。
        /// </summary>
        public static Service? Read(RegistryKey servicesKey, string serviceId)
        {
            using var serviceKey = servicesKey.OpenSubKey(serviceId);
            if (serviceKey == null) return null;

            using var p = serviceKey.OpenSubKey(RegistryPaths.ParametersSubKey);
            if (p == null) return null;

            var exePath = p.GetValue(ParameterNames.ExePath) as string;
            if (string.IsNullOrEmpty(exePath)) return null;

            var startupType = serviceKey.GetValue(ScmValueNames.Start) is int start && Enum.IsDefined(typeof(ServiceStartupType), start)
                ? (ServiceStartupType)start : ServiceStartupType.Auto;
            var accountName = serviceKey.GetValue(ScmValueNames.ObjectName) as string;

            // 敏感变量：名称公开记录；值只有有权读取受保护子键时才能取得（以名称列表为准，丢弃多余的值）
            var names = (p.GetValue(ParameterNames.SecretEnvironmentNames) as string[] ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var secrets = names.Count == 0 ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : SecretStore.TryRead(serviceId);
            if (secrets != null)
            {
                secrets = names.ToDictionary(name => name, name => secrets.TryGetValue(name, out var value) ? value : "",
                    StringComparer.OrdinalIgnoreCase);
            }

            return new Service
            {
                Id = serviceId,
                Name = p.GetValue(ParameterNames.DisplayName) as string ?? serviceId,
                ExePath = exePath,
                Args = p.GetValue(ParameterNames.Args) as string,
                WorkingDir = p.GetValue(ParameterNames.WorkingDir) as string,
                Description = p.GetValue(ParameterNames.Description) as string,
                AutoRestart = p.GetValue(ParameterNames.AutoRestart) is int autoRestart && autoRestart == 1,
                RestartDelaySeconds = ReadInt(p, ParameterNames.RestartDelaySeconds, _ => true, RestartPolicyDefaults.DelaySeconds),
                MaxRestarts = ReadInt(p, ParameterNames.MaxRestarts, _ => true, RestartPolicyDefaults.MaxRestarts),
                RestartResetMinutes = ReadInt(p, ParameterNames.RestartResetMinutes, _ => true, RestartPolicyDefaults.ResetMinutes),
                RestartBackoff = p.GetValue(ParameterNames.RestartBackoff) is int backoff ? backoff == 1 : RestartPolicyDefaults.Backoff,
                StopMode = p.GetValue(ParameterNames.StopMode) is int stopMode && Enum.IsDefined(typeof(ServiceStopMode), stopMode)
                    ? (ServiceStopMode)stopMode : ServiceStopMode.Kill,
                StopCommand = p.GetValue(ParameterNames.StopCommand) as string,
                StopTimeoutSeconds = ReadInt(p, ParameterNames.StopTimeoutSeconds, v => v > 0, StopPolicyDefaults.TimeoutSeconds),
                MemoryLimitMB = ReadInt(p, ParameterNames.MemoryLimitMB, v => v >= 0, 0),
                ProcessMemoryLimitMB = ReadInt(p, ParameterNames.ProcessMemoryLimitMB, v => v >= 0, 0),
                CpuLimitPercent = ReadInt(p, ParameterNames.CpuLimitPercent, v => v is >= 0 and <= ResourceLimitRanges.MaxCpuPercent, 0),
                MaxProcesses = ReadInt(p, ParameterNames.MaxProcesses, v => v >= 0, 0),
                AccountType = ServiceAccounts.ParseObjectName(accountName),
                AccountName = string.IsNullOrWhiteSpace(accountName) ? ServiceAccounts.LocalSystemName : accountName,
                Environment = EnvironmentVariables.ParseLenient(p.GetValue(ParameterNames.Environment) as string[]),
                SecretEnvironment = secrets ?? names.ToDictionary(name => name, _ => "", StringComparer.OrdinalIgnoreCase),
                SecretsReadable = secrets != null,
                Dependencies = ServiceDependencies.ParseRegistry(serviceKey.GetValue(ScmValueNames.DependOnService) as string[]),
                CreatedAt = DateTime.TryParse(p.GetValue(ParameterNames.CreatedAt) as string, out var createdAt) ? createdAt : DateTime.Now,
                UpdatedAt = DateTime.Now,
                StartupType = startupType,
                // 延迟自动启动标志位于服务键（不是 Parameters），且只在自动启动类型下有效
                DelayedAutoStart = ServiceStartupModes.NormalizeDelayed(
                    startupType, serviceKey.GetValue(ScmValueNames.DelayedAutostart) is int delayed && delayed == 1)
            };
        }

        /// <summary>
        /// 读取全部由本工具管理的服务。优先使用服务索引；索引不存在（旧版本）时扫描全部服务并重建索引。
        /// </summary>
        public static Dictionary<string, Service> ReadAll()
        {
            var services = new Dictionary<string, Service>();
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var servicesKey = hklm.OpenSubKey(RegistryPaths.Services);
            if (servicesKey == null) return services;

            using var indexKey = hklm.OpenSubKey(RegistryPaths.ManagedServices);
            bool rebuildIndex = indexKey == null;
            var candidates = indexKey?.GetSubKeyNames() ?? servicesKey.GetSubKeyNames();

            foreach (var serviceId in candidates)
            {
                try
                {
                    if (Read(servicesKey, serviceId) is not { } service) continue;
                    services[serviceId] = service;
                    if (rebuildIndex) AddToIndex(serviceId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to load service {serviceId}: {ex.Message}");
                }
            }
            return services;
        }

        public static void AddToIndex(string serviceId)
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var indexKey = hklm.CreateSubKey(RegistryPaths.ManagedServices);
                using var _ = indexKey.CreateSubKey(serviceId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to add service to index: {ex.Message}");
            }
        }

        public static void RemoveFromIndex(string serviceId)
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var indexKey = hklm.OpenSubKey(RegistryPaths.ManagedServices, writable: true);
                indexKey?.DeleteSubKey(serviceId, throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to remove service from index: {ex.Message}");
            }
        }

        private static int ReadInt(RegistryKey key, string name, Func<int, bool> isValid, int defaultValue) =>
            key.GetValue(name) is int value && isValid(value) ? value : defaultValue;
    }
}
