using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using Services.Core.Helpers;
using Services.Core.Models;
using Services.Core.Scm;
using Services.Core.Storage;
using Services.Core.Wrapper;

namespace Services.Core.Services
{
    public sealed class WrapperMigrationResult
    {
        /// <summary>服务现在使用的包装器路径；没有服务或找不到模板时为 null</summary>
        public string? WrapperPath { get; set; }
        /// <summary>被修改了包装器路径的服务 ID</summary>
        public List<string> Migrated { get; } = new();
        /// <summary>被删除的旧版本目录名</summary>
        public List<string> RemovedVersions { get; } = new();
    }

    /// <summary>
    /// 由本工具管理的服务：缓存列表与状态，并编排创建、修改、删除、启停。
    /// SCM 调用见 <see cref="ScmClient"/>，注册表读写见 <see cref="ServiceRegistryStore"/>。
    /// </summary>
    public class WindowsServiceManager : IDisposable
    {
        private Dictionary<string, Service> _services = new();
        public event EventHandler<Service>? ServiceUpdated;
        private readonly object _lock = new();
        private const int MaxConcurrentStatusQueries = 8;
        private const string DescriptionPrefix = "Managed by Windows Service Manager: ";

        /// <summary>
        /// 包装器模板路径。为空时使用同目录的 servicesvc.exe，不存在时（如开发调试）退回当前进程
        /// （ServicesApp.exe 兼容 --service-wrapper）。servicesvc.exe 模板不会被服务直接运行，
        /// 而是部署到受保护目录后再注册（见 <see cref="WrapperDeployment"/>）。
        /// </summary>
        public string? WrapperExePath { get; set; }

        /// <summary>
        /// 查找与当前程序同目录的独立包装器 servicesvc.exe，不存在时返回 null
        /// </summary>
        public static string? FindWrapperExecutable()
        {
            var path = Path.Combine(AppContext.BaseDirectory, ServiceUtils.WrapperExeName);
            return File.Exists(path) ? path : null;
        }

        #region 查询

        public async Task InitializeAsync()
        {
            await LoadServicesAsync();
        }

        public Task<List<Service>> GetServicesAsync() => GetServicesSnapshotAsync();

        public Task<List<Service>> GetServicesSnapshotAsync()
        {
            lock (_lock)
            {
                return Task.FromResult(_services.Values.Select(CloneService).ToList());
            }
        }

        public async Task RefreshServiceStatusesAsync()
        {
            List<Service> servicesToUpdate;
            lock (_lock)
            {
                servicesToUpdate = _services.Values.ToList();
            }

            if (servicesToUpdate.Count == 0) return;

            var options = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentStatusQueries };
            await Parallel.ForEachAsync(servicesToUpdate, options, async (service, _) =>
            {
                await UpdateServiceStatusAsync(service);
            });
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _services.Clear();
            }
            GC.SuppressFinalize(this);
        }

        private static Service CloneService(Service s)
        {
            var clone = new Service { Id = s.Id };
            clone.CopyFrom(s);
            return clone;
        }

        private async Task UpdateServiceStatusAsync(Service service)
        {
            await Task.Run(() =>
            {
                var (state, pid) = ScmClient.QueryStatus(service.Id);
                var uptime = ComputeUptime(service.Id, state, pid);

                if (service.State != state || service.Pid != pid || service.Uptime != uptime)
                {
                    service.State = state;
                    service.Pid = pid;
                    service.Uptime = uptime;
                    service.UpdatedAt = DateTime.Now;
                    ServiceUpdated?.Invoke(this, CloneService(service));
                }
                else
                {
                    service.UpdatedAt = DateTime.Now;
                }
            });
        }

        private async Task LoadServicesAsync()
        {
            var services = await Task.Run(() =>
            {
                try
                {
                    var loaded = ServiceRegistryStore.ReadAll();
                    foreach (var service in loaded.Values)
                    {
                        (service.State, service.Pid) = ScmClient.QueryStatus(service.Id);
                        service.Uptime = ComputeUptime(service.Id, service.State, service.Pid);
                    }
                    return loaded;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"LoadServicesAsync error: {ex.Message}");
                    return new Dictionary<string, Service>();
                }
            });

            lock (_lock)
            {
                _services = services;
            }
        }

        /// <summary>
        /// 运行时长取自包装器写入的运行状态（按秒取整）；未运行或状态不属于当前服务进程时为 null
        /// </summary>
        private static TimeSpan? ComputeUptime(string serviceId, ServiceRunState state, int pid)
        {
            if (state != ServiceRunState.Running) return null;
            var uptime = ServiceRuntimeState.GetUptime(new ServiceStateStore().Read(serviceId), pid, isRunning: true, DateTime.Now);
            return uptime is { } value ? TimeSpan.FromSeconds(Math.Floor(value.TotalSeconds)) : null;
        }

        private Service GetCached(string serviceId)
        {
            lock (_lock)
            {
                return _services.TryGetValue(serviceId, out var service) ? service : throw new Exception("Service not found");
            }
        }

        #endregion

        #region 创建 / 修改 / 删除

        /// <returns>新服务的 ID</returns>
        /// <param name="password">运行账户为指定用户时的密码（不保存，仅交给 SCM）</param>
        public async Task<string> CreateServiceAsync(ServiceConfig config, string? password = null)
        {
            ValidateConfig(config);
            var dependencies = ValidateDependencies(config, null, null);
            bool delayedAutoStart = ServiceStartupModes.NormalizeDelayed(config.StartupType, config.DelayedAutoStart);
            ServiceAccountSetup.PrepareUserAccount(config, password);

            string serviceId = GenerateServiceId(config.Name);
            if (ServiceRegistryStore.ServiceKeyExists(serviceId))
                throw new Exception($"Service {serviceId} already exists");

            var wrapperCommand = ServiceUtils.BuildWrapperCommand(ResolveServiceWrapper(), serviceId);
            ScmClient.Create(serviceId, config.Name, config.StartupType, wrapperCommand,
                dependencies.Count > 0 ? ServiceDependencies.BuildMultiString(dependencies) : null);

            // 之后任何一步失败都删除已创建的服务，不留下半成品
            void Step(string what, Action action)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    DeleteServiceRegistration(serviceId);
                    throw new Exception($"Failed to configure {what}: {ex.Message}");
                }
            }

            Step("service registry", () =>
            {
                ServiceRegistryStore.WriteParameters(serviceId, config, isNew: true);
                ServiceRegistryStore.AddToIndex(serviceId);
            });
            if (config.AccountType != ServiceAccountType.LocalSystem)
                Step("service account", () => ServiceAccountSetup.Apply(serviceId, config, password));
            if (delayedAutoStart)
                Step("delayed auto start", () => ScmClient.SetDelayedAutoStart(serviceId, true));
            // 账户确定后再写入敏感变量，ACL 中的运行账户（如虚拟账户）此时才能解析
            Step("secret environment", () => WriteProtectedSettings(serviceId, config));

            EnsureEventSource(serviceId);
            ScmClient.SetDescription(serviceId, BuildServiceDescription(config));

            // 恢复策略：服务失败（如依赖未就绪）后 1 分钟由 SCM 重新拉起
            await RunCommandAsync("sc.exe", $"failure \"{serviceId}\" reset= 86400 actions= restart/60000/restart/60000/restart/60000");
            // 启用自动重启时，包装器放弃重启后以非 0 退出码停止，也交由上述恢复策略稍后重新拉起
            ScmClient.SetFailureActionsOnNonCrashFailures(serviceId, config.AutoRestart);

            await LoadServicesAsync();
            return serviceId;
        }

        /// <summary>
        /// 更新已有服务的配置。服务 ID 保持不变，运行中的服务需重启后才会应用新的启动配置。
        /// </summary>
        /// <param name="password">切换为指定用户或修改其密码时提供；为 null 时不修改账户密码</param>
        public async Task UpdateServiceAsync(string serviceId, ServiceConfig config, string? password = null)
        {
            var existing = GetCached(serviceId);

            ValidateConfig(config);
            var dependencies = ValidateDependencies(config, serviceId, existing.Dependencies);
            // 依赖未变化时不重写（null 表示不修改），以免丢失本工具不展示的依赖组（+Group）
            bool dependenciesChanged = !ServiceDependencies.SameSet(dependencies, existing.Dependencies);
            bool delayedAutoStart = ServiceStartupModes.NormalizeDelayed(config.StartupType, config.DelayedAutoStart);

            bool accountChanged = config.AccountType != existing.AccountType ||
                (config.AccountType == ServiceAccountType.User &&
                 !string.Equals(config.AccountName?.Trim(), existing.AccountName, StringComparison.OrdinalIgnoreCase));
            bool applyAccount = accountChanged || (config.AccountType == ServiceAccountType.User && password != null);
            if (applyAccount)
                ServiceAccountSetup.PrepareUserAccount(config, password);

            ScmClient.ChangeConfig(serviceId, config.StartupType, config.Name,
                dependenciesChanged ? ServiceDependencies.BuildMultiString(dependencies) : null);
            ServiceRegistryStore.WriteParameters(serviceId, config, isNew: false);
            ScmClient.SetDescription(serviceId, BuildServiceDescription(config));
            ScmClient.SetFailureActionsOnNonCrashFailures(serviceId, config.AutoRestart);

            // 延迟标志只对自动启动的服务有效；非自动启动时保留原标志（切回自动启动时会重新设置）
            if (config.StartupType == ServiceStartupType.Auto)
                ScmClient.SetDelayedAutoStart(serviceId, delayedAutoStart);

            if (applyAccount)
                ServiceAccountSetup.Apply(serviceId, config, password);
            // 每次都重写：内容可能变化，运行账户变化时 ACL 也要随之更新
            WriteProtectedSettings(serviceId, config);
            EnsureEventSource(serviceId);

            await LoadServicesAsync();
        }

        public async Task DeleteServiceAsync(string serviceId)
        {
            GetCached(serviceId);
            await StopServiceAsync(serviceId);
            DeleteServiceRegistration(serviceId);

            lock (_lock)
            {
                _services.Remove(serviceId);
            }
        }

        /// <summary>
        /// 从 SCM 删除服务并清理索引、注册表参数、事件日志源与运行状态文件。不检查本地缓存，可用于创建失败时回滚。
        /// </summary>
        private static void DeleteServiceRegistration(string serviceId)
        {
            ScmClient.Delete(serviceId);
            ServiceRegistryStore.RemoveFromIndex(serviceId);
            ServiceRegistryStore.DeleteParameters(serviceId);

            try
            {
                if (EventLog.SourceExists(serviceId))
                    EventLog.DeleteEventSource(serviceId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to delete event source: {ex.Message}");
            }

            // 删除运行状态文件（失败时内部已忽略异常）
            new ServiceStateStore().Delete(serviceId);
        }

        /// <summary>
        /// 写入只有 SYSTEM、管理员与运行账户可读的配置：敏感环境变量与服务级 Webhook
        /// </summary>
        private static void WriteProtectedSettings(string serviceId, ServiceConfig config)
        {
            var account = ServiceAccounts.ToObjectName(config.AccountType, serviceId, config.AccountName);
            SecretStore.Write(serviceId, config.SecretEnvironment, account);
            NotificationStore.WriteService(serviceId, new Notifications.WebhookSettings(config.WebhookUrl?.Trim() ?? "",
                Notifications.WebhookEvents.Parse(config.WebhookEvents)), account);
        }

        private static void ValidateConfig(ServiceConfig config)
        {
            if (!File.Exists(config.ExePath))
                throw new FileNotFoundException("Executable not found", config.ExePath);

            if (config.Name.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != ' '))
                throw new ArgumentException("Service Name contains invalid characters.");

            if (config.StopMode == ServiceStopMode.Command && string.IsNullOrWhiteSpace(config.StopCommand))
                throw new ArgumentException("Stop command is required when stop mode is 'command'.");

            if (config.MemoryLimitMB is < 0 or > ResourceLimitRanges.MaxMemoryMB ||
                config.ProcessMemoryLimitMB is < 0 or > ResourceLimitRanges.MaxMemoryMB ||
                config.CpuLimitPercent is < 0 or > ResourceLimitRanges.MaxCpuPercent ||
                config.MaxProcesses is < 0 or > ResourceLimitRanges.MaxProcesses)
                throw new ArgumentException("Resource limit is out of range.");

            if (Notifications.WebhookSettings.ValidateUrl(config.WebhookUrl) is { } webhookError)
                throw new ArgumentException(webhookError);
            Notifications.WebhookEvents.Parse(config.WebhookEvents);

            if (Health.HealthCheckSpec.Validate(config.HealthCheckType, config.HealthCheckTarget) is { } healthError)
                throw new ArgumentException(healthError);
            if (config.HealthCheckIntervalSeconds is < 1 or > HealthCheckDefaults.MaxIntervalSeconds ||
                config.HealthCheckTimeoutSeconds is < 1 or > HealthCheckDefaults.MaxTimeoutSeconds ||
                config.HealthCheckFailureThreshold is < 1 or > HealthCheckDefaults.MaxFailureThreshold ||
                config.HealthCheckStartPeriodSeconds is < 0 or > HealthCheckDefaults.MaxStartPeriodSeconds)
                throw new ArgumentException("Health check setting is out of range.");

            EnvironmentVariables.Validate(config.Environment);
            EnvironmentVariables.Validate(config.SecretEnvironment);
            var overlap = config.Environment.Keys.FirstOrDefault(config.SecretEnvironment.ContainsKey);
            if (overlap != null)
                throw new ArgumentException($"Environment variable '{overlap}' is defined both as normal and secret.");
        }

        /// <summary>
        /// 校验并规范化依赖服务：格式合法、不依赖自身、依赖的服务在系统中存在。
        /// 更新时，本来就已存在于该服务依赖中的项不再检查是否存在（避免被卸载的旧依赖阻止修改其他配置）。
        /// </summary>
        /// <param name="serviceId">更新时为服务自身的 ID（禁止依赖自身）；创建时为 null</param>
        /// <param name="currentDependencies">更新时服务当前的依赖；创建时为 null</param>
        /// <returns>规范化（去空白、去重）后的依赖列表</returns>
        private static List<string> ValidateDependencies(ServiceConfig config, string? serviceId, IReadOnlyList<string>? currentDependencies)
        {
            var dependencies = ServiceDependencies.Normalize(config.Dependencies);
            ServiceDependencies.Validate(dependencies, serviceId);

            var current = new HashSet<string>(currentDependencies ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            ServiceDependencies.EnsureExist(dependencies.Where(name => !current.Contains(name)), ScmClient.Exists);
            return dependencies;
        }

        private static string BuildServiceDescription(ServiceConfig config)
        {
            // 未填写描述时回退为服务名称（与旧版本行为一致）
            var description = string.IsNullOrWhiteSpace(config.Description) ? config.Name : config.Description.Trim();
            return DescriptionPrefix + description;
        }

        /// <summary>
        /// 注册事件日志源，使低权限运行账户的包装器也能写入 Windows 事件日志（注册需要管理员权限）
        /// </summary>
        private static void EnsureEventSource(string serviceId)
        {
            try
            {
                if (!EventLog.SourceExists(serviceId))
                    EventLog.CreateEventSource(serviceId, "Application");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to register event source: {ex.Message}");
            }
        }

        private static async Task RunCommandAsync(string command, string args)
        {
            var psi = new ProcessStartInfo(command, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi);
            if (p == null) return;

            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
            {
                var err = await p.StandardError.ReadToEndAsync();
                throw new Exception($"Command failed: {command} {args}\nError: {err}");
            }
        }

        private static string GenerateServiceId(string displayName)
        {
            var safe = new string(displayName.Where(char.IsLetterOrDigit).ToArray());
            return $"WinSvcMgr_{safe}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        }

        #endregion

        #region 启停

        public async Task StartServiceAsync(string serviceId)
        {
            var service = GetCached(serviceId);

            using var sc = new ServiceController(serviceId);
            if (sc.Status != ServiceControllerStatus.Running &&
                sc.Status != ServiceControllerStatus.StartPending)
            {
                sc.Start();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(100);
                        await UpdateServiceStatusAsync(service);
                        ServiceUpdated?.Invoke(this, service);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"StartServiceAsync error: {ex}");
                    }
                });
                return;
            }
            await UpdateServiceStatusAsync(service);
            ServiceUpdated?.Invoke(this, service);
        }

        public async Task StopServiceAsync(string serviceId)
        {
            var service = GetCached(serviceId);

            using var sc = new ServiceController(serviceId);
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                try
                {
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
                catch (System.ServiceProcess.TimeoutException) { }
            }
            await UpdateServiceStatusAsync(service);
            ServiceUpdated?.Invoke(this, service);
        }

        #endregion

        #region 包装器

        /// <summary>
        /// 新建服务时注册的包装器：servicesvc.exe 模板部署到受保护目录后的副本；
        /// 旧版兼容的 ServicesApp.exe（或当前进程）直接使用
        /// </summary>
        private string ResolveServiceWrapper()
        {
            var template = WrapperExePath ?? FindWrapperExecutable();
            if (template != null && IsStandaloneWrapper(template))
                return WrapperDeployment.Deploy(template);

            return template ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new Exception("Cannot determine current executable path");
        }

        private static bool IsStandaloneWrapper(string path) =>
            string.Equals(Path.GetFileName(path), ServiceUtils.WrapperExeName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 将所有服务的 ImagePath 指向当前包装器在受保护目录中的副本（旧版本创建的服务指向 ServicesApp.exe
        /// 或安装目录中的 servicesvc.exe，或包装器已升级），并清理不再被引用的旧版本目录。
        /// 正在运行的服务在下次启动时生效，其旧版本目录在服务重启后的下一次迁移中删除。
        /// </summary>
        /// <returns>找不到 servicesvc.exe 模板时不做任何修改</returns>
        public WrapperMigrationResult MigrateWrapper()
        {
            var result = new WrapperMigrationResult();
            var template = WrapperExePath ?? FindWrapperExecutable();
            if (template == null || !IsStandaloneWrapper(template)) return result;

            List<string> ids;
            lock (_lock)
            {
                ids = _services.Keys.ToList();
            }

            // 只处理由包装器运行的服务
            var wrapped = ids
                .Select(id => (Id: id, ImagePath: ServiceRegistryStore.ReadImagePath(id)))
                .Where(s => s.ImagePath != null && s.ImagePath.Contains(ServiceUtils.WrapperArgument, StringComparison.Ordinal))
                .ToList();

            // 没有服务时不部署，只清理（删除全部服务后的残留版本由此清除）
            if (wrapped.Count > 0)
            {
                var wrapper = WrapperDeployment.Deploy(template);
                result.WrapperPath = wrapper;
                foreach (var (id, imagePath) in wrapped)
                {
                    var expected = ServiceUtils.BuildWrapperCommand(wrapper, id);
                    if (string.Equals(imagePath!.Trim(), expected, StringComparison.OrdinalIgnoreCase)) continue;

                    ScmClient.ChangeBinaryPath(id, expected);
                    result.Migrated.Add(id);
                }
            }

            result.RemovedVersions.AddRange(WrapperDeployment.CleanupUnused());
            return result;
        }

        #endregion
    }
}
