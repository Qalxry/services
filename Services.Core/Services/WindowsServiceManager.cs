using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading.Tasks;
using Microsoft.Win32;
using Services.Core.Helpers;
using Services.Core.Models;
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

    public class WindowsServiceManager : IDisposable
    {
        private Dictionary<string, Service> _services = new();
        public event EventHandler<Service>? ServiceUpdated;
        private readonly object _lock = new();
        private const int MaxConcurrentStatusQueries = 8;

        /// <summary>
        /// 包装器模板路径。为空时使用同目录的 servicesvc.exe，不存在时（如开发调试）退回当前进程
        /// （ServicesApp.exe 兼容 --service-wrapper）。servicesvc.exe 模板不会被服务直接运行，
        /// 而是部署到受保护目录后再注册（见 <see cref="WrapperDeployment"/>）。
        /// </summary>
        public string? WrapperExePath { get; set; }

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
        /// 查找与当前程序同目录的独立包装器 servicesvc.exe，不存在时返回 null
        /// </summary>
        public static string? FindWrapperExecutable()
        {
            var path = Path.Combine(AppContext.BaseDirectory, ServiceUtils.WrapperExeName);
            return File.Exists(path) ? path : null;
        }

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

            var wrapped = new List<(string Id, string ImagePath)>();
            foreach (var id in ids)
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{id}");
                var imagePath = key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                // 只处理由包装器运行的服务
                if (imagePath != null && imagePath.Contains(ServiceUtils.WrapperArgument, StringComparison.Ordinal))
                    wrapped.Add((id, imagePath));
            }

            // 没有服务时不部署，只清理（删除全部服务后的残留版本由此清除）
            if (wrapped.Count > 0)
            {
                var wrapper = WrapperDeployment.Deploy(template);
                result.WrapperPath = wrapper;
                foreach (var (id, imagePath) in wrapped)
                {
                    var expected = ServiceUtils.BuildWrapperCommand(wrapper, id);
                    if (string.Equals(imagePath.Trim(), expected, StringComparison.OrdinalIgnoreCase)) continue;

                    ChangeServiceBinaryPath(id, expected);
                    result.Migrated.Add(id);
                }
            }

            result.RemovedVersions.AddRange(WrapperDeployment.CleanupUnused());
            return result;
        }

        private static void ChangeServiceBinaryPath(string serviceId, string binaryPath)
        {
            IntPtr scmHandle = ServiceUtils.OpenSCManager(null, null, ServiceUtils.SC_MANAGER_CONNECT);
            if (scmHandle == IntPtr.Zero)
                throw new Exception($"Failed to open SC Manager. Error: {Marshal.GetLastWin32Error()}");

            try
            {
                IntPtr serviceHandle = ServiceUtils.OpenService(scmHandle, serviceId, ServiceUtils.SERVICE_CHANGE_CONFIG);
                if (serviceHandle == IntPtr.Zero)
                    throw new Exception($"Failed to open service. Error: {Marshal.GetLastWin32Error()}");

                try
                {
                    if (!ServiceUtils.ChangeServiceConfig(
                            serviceHandle,
                            ServiceUtils.SERVICE_NO_CHANGE,
                            ServiceUtils.SERVICE_NO_CHANGE,
                            ServiceUtils.SERVICE_NO_CHANGE,
                            binaryPath,
                            null,
                            IntPtr.Zero,
                            null,
                            null,
                            null,
                            null))
                        throw new Exception($"Failed to change service binary path. Error: {Marshal.GetLastWin32Error()}");
                }
                finally
                {
                    ServiceUtils.CloseServiceHandle(serviceHandle);
                }
            }
            finally
            {
                ServiceUtils.CloseServiceHandle(scmHandle);
            }
        }

        public WindowsServiceManager()
        {
        }

        public async Task InitializeAsync()
        {
            await LoadServicesAsync();
        }

        public async Task<List<Service>> GetServicesAsync()
        {
            return await GetServicesSnapshotAsync();
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

        public Task<List<Service>> GetServicesSnapshotAsync()
        {
            lock (_lock)
            {
                return Task.FromResult(_services.Values.Select(CloneService).ToList());
            }
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
            return new Service
            {
                Id = s.Id,
                Name = s.Name,
                Status = s.Status,
                Pid = s.Pid,
                ExePath = s.ExePath,
                Args = s.Args,
                WorkingDir = s.WorkingDir,
                Description = s.Description,
                StartupType = s.StartupType,
                DelayedAutoStart = s.DelayedAutoStart,
                AutoRestart = s.AutoRestart,
                RestartDelaySeconds = s.RestartDelaySeconds,
                MaxRestarts = s.MaxRestarts,
                RestartResetMinutes = s.RestartResetMinutes,
                RestartBackoff = s.RestartBackoff,
                StopMode = s.StopMode,
                StopCommand = s.StopCommand,
                StopTimeoutSeconds = s.StopTimeoutSeconds,
                AccountType = s.AccountType,
                AccountName = s.AccountName,
                Environment = EnvironmentVariables.Clone(s.Environment),
                Dependencies = ServiceDependencies.Clone(s.Dependencies),
                CreatedAt = s.CreatedAt,
                UpdatedAt = s.UpdatedAt
            };
        }

        private async Task UpdateServiceStatusAsync(Service service)
        {
            if (service == null) return;
            await Task.Run(() =>
            {
                var (status, pid) = ServiceUtils.GetServiceStatus(service.Id);

                if (service.Status != status || service.Pid != pid)
                {
                    service.Status = status;
                    service.Pid = pid;
                    service.UpdatedAt = DateTime.Now;
                    ServiceUpdated?.Invoke(this, CloneService(service));
                }
                else
                {
                    service.UpdatedAt = DateTime.Now;
                }
            });
        }

        /// <returns>新服务的 ID</returns>
        /// <param name="password">运行账户为指定用户时的密码（不保存，仅交给 SCM）</param>
        public async Task<string> CreateServiceAsync(ServiceConfig config, string? password = null)
                {
                    ValidateConfig(config);
                    var dependencies = ValidateDependencies(config, null, null);
                    bool delayedAutoStart = ServiceStartupModes.NormalizeDelayed(config.StartupType, config.DelayedAutoStart);
                    PrepareUserAccount(config, password);

                    string serviceName = GenerateServiceName(config.Name);

                    // Double check registry instead of local cache
                    using (var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}"))
                    {
                        if (key != null) throw new Exception($"Service {serviceName} already exists");
                    }

                    string currentExe = ResolveServiceWrapper();

                    // Construct command safely
                    string wrapperCmd = ServiceUtils.BuildWrapperCommand(currentExe, serviceName);

                    // Use P/Invoke to create service
                    IntPtr scmHandle = ServiceUtils.OpenSCManager(null, null, ServiceUtils.SC_MANAGER_CREATE_SERVICE);
                    if (scmHandle == IntPtr.Zero)
                        throw new Exception($"Failed to open SC Manager. Error: {Marshal.GetLastWin32Error()}");

                    try
                    {
                        IntPtr serviceHandle = ServiceUtils.CreateService(
                            scmHandle,
                            serviceName,
                            config.Name,
                            ServiceUtils.SERVICE_ALL_ACCESS,
                            ServiceUtils.SERVICE_WIN32_OWN_PROCESS,
                            (uint)config.StartupType,
                            ServiceUtils.SERVICE_ERROR_NORMAL,
                            wrapperCmd,
                            null,
                            IntPtr.Zero,
                            dependencies.Count > 0 ? ServiceDependencies.BuildMultiString(dependencies) : null,
                            null,
                            null);

                        if (serviceHandle == IntPtr.Zero)
                            throw new Exception($"Failed to create service. Error: {Marshal.GetLastWin32Error()}");

                        ServiceUtils.CloseServiceHandle(serviceHandle);
                    }
                    finally
                    {
                        ServiceUtils.CloseServiceHandle(scmHandle);
                    }

                    try
                    {
                        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                        using (var servicesKey = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", true))
                        {
                            if (servicesKey != null)
                            {
                                using (var serviceKey = servicesKey.OpenSubKey(serviceName, true))
                                {
                                    if (serviceKey != null)
                                    {
                                        using (var paramsKey = serviceKey.CreateSubKey("Parameters"))
                                        {
                                            WriteParameters(paramsKey, config);
                                            paramsKey.SetValue("CreatedAt", DateTime.Now.ToString("o"));
                                            paramsKey.SetValue("ManagedBy", "WindowsServiceManager");
                                        }
                                    }
                                }
                            }
                        }

                        // Add to managed services index for fast loading
                        AddToManagedServicesIndex(serviceName);
                    }
                    catch (Exception ex)
                    {
                        DeleteServiceRegistration(serviceName);
                        throw new Exception($"Failed to configure service registry: {ex.Message}");
                    }

                    if (config.AccountType != ServiceAccountType.LocalSystem)
                    {
                        try
                        {
                            ApplyServiceAccount(serviceName, config, password);
                        }
                        catch (Exception ex)
                        {
                            DeleteServiceRegistration(serviceName);
                            throw new Exception($"Failed to configure service account: {ex.Message}");
                        }
                    }

                    if (delayedAutoStart)
                    {
                        try
                        {
                            ServiceUtils.SetDelayedAutoStart(serviceName, true);
                        }
                        catch (Exception ex)
                        {
                            DeleteServiceRegistration(serviceName);
                            throw new Exception($"Failed to configure delayed auto start: {ex.Message}");
                        }
                    }

                    EnsureEventSource(serviceName);

                    SetServiceDescription(serviceName, BuildServiceDescription(config));

                    // Configure recovery actions: Restart service after 1 minute if it fails (e.g. dependencies not ready)
                    await RunCommandAsync("sc.exe", $"failure \"{serviceName}\" reset= 86400 actions= restart/60000/restart/60000/restart/60000");

                    // 启用自动重启时，包装器放弃重启后以非 0 退出码停止，也交由上述恢复策略稍后重新拉起
                    ServiceUtils.SetFailureActionsOnNonCrashFailures(serviceName, config.AutoRestart);

                    await LoadServicesAsync();
                    return serviceName;
                }

        /// <summary>
        /// 更新已有服务的配置。服务 ID 保持不变，运行中的服务需重启后才会应用新的启动配置。
        /// </summary>
        /// <param name="password">切换为指定用户或修改其密码时提供；为 null 时不修改账户密码</param>
        public async Task UpdateServiceAsync(string serviceId, ServiceConfig config, string? password = null)
        {
            Service? existing;
            lock (_lock)
            {
                if (!_services.TryGetValue(serviceId, out existing)) throw new Exception("Service not found");
            }

            ValidateConfig(config);
            var dependencies = ValidateDependencies(config, serviceId, existing.Dependencies);
            // 依赖未变化时不重写（null 表示不修改），以免丢失本工具不展示的依赖组（+Group）
            bool dependenciesChanged = !ServiceDependencies.SameSet(dependencies, existing.Dependencies);
            bool delayedAutoStart = ServiceStartupModes.NormalizeDelayed(config.StartupType, config.DelayedAutoStart);

            bool accountChanged = config.AccountType != existing.AccountType ||
                (config.AccountType == ServiceAccountType.User &&
                 !string.Equals(config.AccountName?.Trim(), existing.AccountName, StringComparison.OrdinalIgnoreCase));
            if (accountChanged || (config.AccountType == ServiceAccountType.User && password != null))
            {
                PrepareUserAccount(config, password);
            }

            IntPtr scmHandle = ServiceUtils.OpenSCManager(null, null, ServiceUtils.SC_MANAGER_CONNECT);
            if (scmHandle == IntPtr.Zero)
                throw new Exception($"Failed to open SC Manager. Error: {Marshal.GetLastWin32Error()}");

            try
            {
                IntPtr serviceHandle = ServiceUtils.OpenService(scmHandle, serviceId, ServiceUtils.SERVICE_CHANGE_CONFIG);
                if (serviceHandle == IntPtr.Zero)
                    throw new Exception($"Failed to open service for update. Error: {Marshal.GetLastWin32Error()}");

                try
                {
                    if (!ServiceUtils.ChangeServiceConfig(
                            serviceHandle,
                            ServiceUtils.SERVICE_NO_CHANGE,
                            (uint)config.StartupType,
                            ServiceUtils.SERVICE_NO_CHANGE,
                            null,
                            null,
                            IntPtr.Zero,
                            dependenciesChanged ? ServiceDependencies.BuildMultiString(dependencies) : null,
                            null,
                            null,
                            config.Name))
                        throw new Exception($"Failed to update service config. Error: {Marshal.GetLastWin32Error()}");
                }
                finally
                {
                    ServiceUtils.CloseServiceHandle(serviceHandle);
                }
            }
            finally
            {
                ServiceUtils.CloseServiceHandle(scmHandle);
            }

            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var paramsKey = hklm.CreateSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceId}\Parameters"))
            {
                WriteParameters(paramsKey, config);
            }

            SetServiceDescription(serviceId, BuildServiceDescription(config));
            ServiceUtils.SetFailureActionsOnNonCrashFailures(serviceId, config.AutoRestart);

            // 延迟标志只对自动启动的服务有效；非自动启动时保留原标志（切回自动启动时会重新设置）
            if (config.StartupType == ServiceStartupType.Auto)
            {
                ServiceUtils.SetDelayedAutoStart(serviceId, delayedAutoStart);
            }

            if (accountChanged || (config.AccountType == ServiceAccountType.User && password != null))
            {
                ApplyServiceAccount(serviceId, config, password);
            }
            EnsureEventSource(serviceId);

            await LoadServicesAsync();
        }

        /// <summary>
        /// 指定用户：必须提供密码；授予“作为服务登录”权限并校验密码。在修改服务之前执行，失败时不留下半成品。
        /// </summary>
        private static void PrepareUserAccount(ServiceConfig config, string? password)
        {
            if (config.AccountType != ServiceAccountType.User) return;

            if (string.IsNullOrWhiteSpace(config.AccountName))
                throw new ArgumentException("Account name is required for a user account.");
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password is required for a user account.");

            var accountName = config.AccountName.Trim();
            ServiceAccounts.GrantLogonAsService(accountName);
            ServiceAccounts.ValidateCredentials(accountName, password);
        }

        /// <summary>
        /// 设置服务运行账户，并为非 LocalSystem 账户授予日志目录写权限
        /// </summary>
        private static void ApplyServiceAccount(string serviceId, ServiceConfig config, string? password)
        {
            var objectName = ServiceAccounts.ToObjectName(config.AccountType, serviceId, config.AccountName);

            // 虚拟账户在服务存在后才能解析 SID，因此先设置账户再授权目录。
            // 只有指定用户需要密码；虚拟账户的密码必须为 NULL（传空字符串会失败，错误 1057），内置账户忽略密码。
            ChangeServiceAccount(serviceId, objectName, config.AccountType == ServiceAccountType.User ? password ?? "" : null);

            if (config.AccountType != ServiceAccountType.LocalSystem)
            {
                ServiceAccounts.GrantDirectoryModify(new LogManager().GetLogDirectory(), objectName);
            }
        }

        private static void ChangeServiceAccount(string serviceId, string objectName, string? password)
        {
            IntPtr scmHandle = ServiceUtils.OpenSCManager(null, null, ServiceUtils.SC_MANAGER_CONNECT);
            if (scmHandle == IntPtr.Zero)
                throw new Exception($"Failed to open SC Manager. Error: {Marshal.GetLastWin32Error()}");

            try
            {
                IntPtr serviceHandle = ServiceUtils.OpenService(scmHandle, serviceId, ServiceUtils.SERVICE_CHANGE_CONFIG);
                if (serviceHandle == IntPtr.Zero)
                    throw new Exception($"Failed to open service. Error: {Marshal.GetLastWin32Error()}");

                try
                {
                    if (!ServiceUtils.ChangeServiceConfig(
                            serviceHandle,
                            ServiceUtils.SERVICE_NO_CHANGE,
                            ServiceUtils.SERVICE_NO_CHANGE,
                            ServiceUtils.SERVICE_NO_CHANGE,
                            null,
                            null,
                            IntPtr.Zero,
                            null,
                            objectName,
                            password,
                            null))
                        throw new Exception($"Failed to change service account. Error: {Marshal.GetLastWin32Error()}");
                }
                finally
                {
                    ServiceUtils.CloseServiceHandle(serviceHandle);
                }
            }
            finally
            {
                ServiceUtils.CloseServiceHandle(scmHandle);
            }
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
                System.Diagnostics.Debug.WriteLine($"Failed to register event source: {ex.Message}");
            }
        }

        private static void ValidateConfig(ServiceConfig config)
        {
            if (!File.Exists(config.ExePath))
                throw new FileNotFoundException("Executable not found", config.ExePath);

            // Security Validation
            if (config.Name.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != ' '))
                throw new ArgumentException("Service Name contains invalid characters.");

            if (config.Name.Contains("\"") || config.Name.Contains("\n") || config.Name.Contains("\r"))
                throw new ArgumentException("Service Name contains illegal characters.");

            if (config.StopMode == ServiceStopMode.Command && string.IsNullOrWhiteSpace(config.StopCommand))
                throw new ArgumentException("Stop command is required when stop mode is 'command'.");

            EnvironmentVariables.Validate(config.Environment);
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
            ServiceDependencies.EnsureExist(
                dependencies.Where(name => !current.Contains(name)),
                ServiceUtils.ServiceExists);
            return dependencies;
        }

        private static void WriteParameters(RegistryKey paramsKey, ServiceConfig config)
        {
            paramsKey.SetValue("ExePath", config.ExePath);
            paramsKey.SetValue("Args", config.Args ?? "");
            paramsKey.SetValue("WorkingDir", string.IsNullOrEmpty(config.WorkingDir) ? Path.GetDirectoryName(config.ExePath) ?? "" : config.WorkingDir);
            paramsKey.SetValue("DisplayName", config.Name);
            paramsKey.SetValue("Description", config.Description ?? "");
            paramsKey.SetValue("AutoRestart", config.AutoRestart ? 1 : 0);
            paramsKey.SetValue("RestartDelaySeconds", config.RestartDelaySeconds, RegistryValueKind.DWord);
            paramsKey.SetValue("MaxRestarts", config.MaxRestarts, RegistryValueKind.DWord);
            paramsKey.SetValue("RestartResetMinutes", config.RestartResetMinutes, RegistryValueKind.DWord);
            paramsKey.SetValue("RestartBackoff", config.RestartBackoff ? 1 : 0, RegistryValueKind.DWord);
            paramsKey.SetValue("StopMode", (int)config.StopMode, RegistryValueKind.DWord);
            paramsKey.SetValue("StopCommand", config.StopCommand ?? "");
            paramsKey.SetValue("StopTimeoutSeconds", config.StopTimeoutSeconds, RegistryValueKind.DWord);
            paramsKey.SetValue("Environment", EnvironmentVariables.FormatLines(config.Environment), RegistryValueKind.MultiString);
        }

        private static string BuildServiceDescription(ServiceConfig config)
        {
            // 未填写描述时回退为服务名称（与旧版本行为一致）
            var description = string.IsNullOrWhiteSpace(config.Description) ? config.Name : config.Description.Trim();
            return $"Managed by Windows Service Manager: {description}";
        }

        private static void SetServiceDescription(string serviceName, string description)
        {
            IntPtr scmHandle = ServiceUtils.OpenSCManager(null, null, ServiceUtils.SC_MANAGER_CONNECT);
            if (scmHandle == IntPtr.Zero)
                throw new Exception($"Failed to open SC Manager. Error: {Marshal.GetLastWin32Error()}");

            try
            {
                IntPtr serviceHandle = ServiceUtils.OpenService(scmHandle, serviceName, ServiceUtils.SERVICE_CHANGE_CONFIG);
                if (serviceHandle == IntPtr.Zero)
                    throw new Exception($"Failed to open service. Error: {Marshal.GetLastWin32Error()}");

                try
                {
                    var info = new ServiceUtils.SERVICE_DESCRIPTION { lpDescription = description };
                    if (!ServiceUtils.ChangeServiceConfig2(serviceHandle, ServiceUtils.SERVICE_CONFIG_DESCRIPTION, ref info))
                        throw new Exception($"Failed to set service description. Error: {Marshal.GetLastWin32Error()}");
                }
                finally
                {
                    ServiceUtils.CloseServiceHandle(serviceHandle);
                }
            }
            finally
            {
                ServiceUtils.CloseServiceHandle(scmHandle);
            }
        }


        private async Task RunCommandAsync(string command, string args)
        {
            var psi = new ProcessStartInfo(command, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi);
            if (p != null)
            {
                await p.WaitForExitAsync();
                if (p.ExitCode != 0)
                {
                    var err = await p.StandardError.ReadToEndAsync();
                    throw new Exception($"Command failed: {command} {args}\nError: {err}");
                }
            }
        }

        private string GenerateServiceName(string displayName)
        {
            var safe = new string(displayName.Where(c => char.IsLetterOrDigit(c)).ToArray());
            return $"WinSvcMgr_{safe}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        }

        public async Task StartServiceAsync(string serviceId)
        {
            Service? service;
            lock (_lock)
            {
                if (!_services.TryGetValue(serviceId, out service)) throw new Exception("Service not found");
            }

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
                        System.Diagnostics.Debug.WriteLine($"StartServiceAsync error: {ex}");
                    }
                });
                return;
            }
            await UpdateServiceStatusAsync(service);
            ServiceUpdated?.Invoke(this, service);
        }

        public async Task StopServiceAsync(string serviceId)
        {
            Service? service;
            lock (_lock)
            {
                if (!_services.TryGetValue(serviceId, out service)) throw new Exception("Service not found");
            }

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

        public async Task DeleteServiceAsync(string serviceId)
                {
                    lock (_lock)
                    {
                        if (!_services.ContainsKey(serviceId)) throw new Exception("Service not found");
                    }

                    await StopServiceAsync(serviceId);

                    DeleteServiceRegistration(serviceId);

                    lock (_lock)
                    {
                        _services.Remove(serviceId);
                    }
                }

        /// <summary>
        /// 从 SCM 删除服务并清理索引、注册表参数与事件日志源。不检查本地缓存，可用于创建失败时回滚。
        /// </summary>
        private void DeleteServiceRegistration(string serviceId)
                {
                    // Use P/Invoke to delete service
                    IntPtr scmHandle = ServiceUtils.OpenSCManager(null, null, ServiceUtils.SC_MANAGER_CONNECT);
                    if (scmHandle == IntPtr.Zero)
                        throw new Exception($"Failed to open SC Manager. Error: {Marshal.GetLastWin32Error()}");

                    try
                    {
                        // We need DELETE access
                        IntPtr serviceHandle = ServiceUtils.OpenService(scmHandle, serviceId, ServiceUtils.DELETE);
                        if (serviceHandle == IntPtr.Zero)
                            throw new Exception($"Failed to open service for deletion. Error: {Marshal.GetLastWin32Error()}");

                        try
                        {
                            if (!ServiceUtils.DeleteService(serviceHandle))
                                throw new Exception($"Failed to delete service. Error: {Marshal.GetLastWin32Error()}");
                        }
                        finally
                        {
                            ServiceUtils.CloseServiceHandle(serviceHandle);
                        }
                    }
                    finally
                    {
                        ServiceUtils.CloseServiceHandle(scmHandle);
                    }

                    // Remove from managed services index
                    RemoveFromManagedServicesIndex(serviceId);

                    // Clean up registry Parameters subkey
                    try
                    {
                        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceId}", writable: true);
                        key?.DeleteSubKeyTree("Parameters", throwOnMissingSubKey: false);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to delete Parameters registry key: {ex.Message}");
                    }

                    try
                    {
                        if (EventLog.SourceExists(serviceId))
                            EventLog.DeleteEventSource(serviceId);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to delete event source: {ex.Message}");
                    }

                    // 删除运行状态文件（失败时内部已忽略异常）
                    new ServiceStateStore().Delete(serviceId);
                }


        private void AddToManagedServicesIndex(string serviceName)
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var indexKey = hklm.CreateSubKey(@"SOFTWARE\WindowsServiceManager\ManagedServices");
                using var serviceIndexKey = indexKey.CreateSubKey(serviceName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to add service to index: {ex.Message}");
            }
        }

        private void RemoveFromManagedServicesIndex(string serviceName)
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var indexKey = hklm.OpenSubKey(@"SOFTWARE\WindowsServiceManager\ManagedServices", true);
                indexKey?.DeleteSubKey(serviceName, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to remove service from index: {ex.Message}");
            }
        }

        private async Task LoadServicesAsync()
        {
            var services = new Dictionary<string, Service>();
            await Task.Run(() =>
            {
                try
                {
                    using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var indexKey = hklm.OpenSubKey(@"SOFTWARE\WindowsServiceManager\ManagedServices");
                    
                    if (indexKey != null)
                    {
                        var managedServiceNames = indexKey.GetSubKeyNames();
                        using var servicesKey = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
                        if (servicesKey != null)
                        {
                            foreach (var serviceName in managedServiceNames)
                            {
                                try
                                {
                                    LoadSingleService(servicesKey, serviceName, services);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Failed to load service {serviceName}: {ex.Message}");
                                }
                            }
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("Index not found, performing full scan.");
                        LoadServicesLegacy(hklm, services);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"LoadServicesAsync error: {ex.Message}");
                }
            });

            lock (_lock)
            {
                _services = services;
            }
        }

        private void LoadSingleService(RegistryKey servicesKey, string serviceName, Dictionary<string, Service> services)
        {
            using var serviceKey = servicesKey.OpenSubKey(serviceName);
            if (serviceKey == null)
            {
                System.Diagnostics.Debug.WriteLine($"Service {serviceName} not found, may have been deleted.");
                return;
            }

            using var paramsKey = serviceKey.OpenSubKey("Parameters");
            if (paramsKey == null) return;

            var exePath = paramsKey.GetValue("ExePath") as string;
            if (string.IsNullOrEmpty(exePath)) return;

            var displayName = paramsKey.GetValue("DisplayName") as string ?? serviceName;
            var args = paramsKey.GetValue("Args") as string;
            var workingDir = paramsKey.GetValue("WorkingDir") as string;
            var description = paramsKey.GetValue("Description") as string;
            var autoRestartVal = paramsKey.GetValue("AutoRestart");
            bool autoRestart = (autoRestartVal is int val && val == 1);
            var restartDelaySeconds = paramsKey.GetValue("RestartDelaySeconds") is int delayVal ? delayVal : RestartPolicyDefaults.DelaySeconds;
            var maxRestarts = paramsKey.GetValue("MaxRestarts") is int maxVal ? maxVal : RestartPolicyDefaults.MaxRestarts;
            var restartResetMinutes = paramsKey.GetValue("RestartResetMinutes") is int resetVal ? resetVal : RestartPolicyDefaults.ResetMinutes;
            var restartBackoff = paramsKey.GetValue("RestartBackoff") is int backoffVal ? backoffVal == 1 : RestartPolicyDefaults.Backoff;
            var stopMode = paramsKey.GetValue("StopMode") is int stopModeVal && Enum.IsDefined(typeof(ServiceStopMode), stopModeVal)
                ? (ServiceStopMode)stopModeVal : ServiceStopMode.Kill;
            var stopCommand = paramsKey.GetValue("StopCommand") as string;
            var stopTimeoutSeconds = paramsKey.GetValue("StopTimeoutSeconds") is int stopTimeoutVal && stopTimeoutVal > 0
                ? stopTimeoutVal : StopPolicyDefaults.TimeoutSeconds;
            var environment = EnvironmentVariables.ParseLenient(paramsKey.GetValue("Environment") as string[]);
            var dependencies = ServiceDependencies.ParseRegistry(serviceKey.GetValue("DependOnService") as string[]);

            var createdAtStr = paramsKey.GetValue("CreatedAt") as string;
            DateTime createdAt = DateTime.Now;
            if (DateTime.TryParse(createdAtStr, out var dt)) createdAt = dt;

            var accountName = serviceKey.GetValue("ObjectName") as string;

            var startupType = ServiceStartupType.Auto;
            if (serviceKey.GetValue("Start") is int startVal && Enum.IsDefined(typeof(ServiceStartupType), startVal))
                startupType = (ServiceStartupType)startVal;
            // 延迟自动启动标志位于服务键（不是 Parameters），且只在自动启动类型下有效
            bool delayedAutoStart = ServiceStartupModes.NormalizeDelayed(
                startupType, serviceKey.GetValue("DelayedAutostart") is int delayedVal && delayedVal == 1);

            var (status, pid) = ServiceUtils.GetServiceStatus(serviceName);

            var service = new Service
            {
                Id = serviceName,
                Name = displayName,
                ExePath = exePath,
                Args = args,
                WorkingDir = workingDir,
                Description = description,
                AutoRestart = autoRestart,
                RestartDelaySeconds = restartDelaySeconds,
                MaxRestarts = maxRestarts,
                RestartResetMinutes = restartResetMinutes,
                RestartBackoff = restartBackoff,
                StopMode = stopMode,
                StopCommand = stopCommand,
                StopTimeoutSeconds = stopTimeoutSeconds,
                AccountType = ServiceAccounts.ParseObjectName(accountName),
                AccountName = string.IsNullOrWhiteSpace(accountName) ? ServiceAccounts.LocalSystemName : accountName,
                Environment = environment,
                Dependencies = dependencies,
                CreatedAt = createdAt,
                UpdatedAt = DateTime.Now,
                StartupType = startupType,
                DelayedAutoStart = delayedAutoStart,
                Status = status,
                Pid = pid
            };
            services[serviceName] = service;
        }

        private void LoadServicesLegacy(RegistryKey hklm, Dictionary<string, Service> services)
        {
            using var servicesKey = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (servicesKey == null) return;

            foreach (var serviceName in servicesKey.GetSubKeyNames())
            {
                try
                {
                    using var serviceKey = servicesKey.OpenSubKey(serviceName);
                    if (serviceKey == null) continue;

                    using var paramsKey = serviceKey.OpenSubKey("Parameters");
                    if (paramsKey == null) continue;

                    var exePath = paramsKey.GetValue("ExePath") as string;
                    if (string.IsNullOrEmpty(exePath)) continue;

                    LoadSingleService(servicesKey, serviceName, services);
                    AddToManagedServicesIndex(serviceName);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load service {serviceName}: {ex.Message}");
                }
            }
        }
    }
}
