using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using Services.Core.Models;
using Services.Core.Scm;
using Services.Core.Services;

namespace Services.Core.Application
{
    /// <summary>按 ID 或名称找不到服务</summary>
    public sealed class ServiceNotFoundException : Exception
    {
        public string Key { get; }

        public ServiceNotFoundException(string key) : base($"Service '{key}' not found")
        {
            Key = key;
        }
    }

    /// <summary>名称匹配到多个服务</summary>
    public sealed class AmbiguousServiceNameException : Exception
    {
        public string Name { get; }
        public IReadOnlyList<string> Ids { get; }

        public AmbiguousServiceNameException(string name, IReadOnlyList<string> ids)
            : base($"Name '{name}' matches multiple services: {string.Join(", ", ids)}")
        {
            Name = name;
            Ids = ids;
        }
    }

    public enum StartFailureKind
    {
        /// <summary>启动过程中服务停止（程序不存在、目标程序立即退出等）</summary>
        StoppedDuringStart,
        /// <summary>超时仍未进入运行状态</summary>
        Timeout
    }

    /// <summary>
    /// 服务未能进入运行状态。Reason 为包装器在本次启动中记录的失败原因（没有时为 null）。
    /// </summary>
    public sealed class ServiceStartException : Exception
    {
        public string ServiceId { get; }
        public StartFailureKind Kind { get; }
        public ServiceRunState State { get; }
        public string? Reason { get; }

        public ServiceStartException(string serviceId, StartFailureKind kind, ServiceRunState state, string? reason)
            : base($"Service '{serviceId}' failed to start ({kind}): {reason ?? state.ToString()}")
        {
            ServiceId = serviceId;
            Kind = kind;
            State = state;
            Reason = reason;
        }
    }

    /// <summary>服务未能在等待时间内停止</summary>
    public sealed class ServiceStopException : Exception
    {
        public string ServiceId { get; }

        public ServiceStopException(string serviceId) : base($"Service '{serviceId}' did not stop in time")
        {
            ServiceId = serviceId;
        }
    }

    /// <summary>
    /// 应用层门面：GUI 与 CLI 共同使用的服务操作（查找、启停并等待结果、读取运行状态与日志位置）。
    /// 不包含任何展示文本，失败以带结构化信息的异常报告，由调用方决定如何呈现。
    /// </summary>
    public sealed class ServiceOperations
    {
        public const int DefaultStartTimeoutSeconds = 30;

        public WindowsServiceManager Manager { get; }
        public LogManager Logs { get; }
        public ServiceStateStore States { get; }

        public ServiceOperations(WindowsServiceManager manager, LogManager? logs = null)
        {
            Manager = manager;
            Logs = logs ?? new LogManager();
            States = new ServiceStateStore(Logs.GetLogDirectory());
        }

        /// <summary>
        /// 创建并加载服务列表
        /// </summary>
        public static async Task<ServiceOperations> OpenAsync()
        {
            var manager = new WindowsServiceManager();
            await manager.InitializeAsync();
            return new ServiceOperations(manager);
        }

        /// <summary>
        /// 全部服务，按添加顺序（创建时间，其次 ID）
        /// </summary>
        public async Task<List<Service>> ListAsync() =>
            (await Manager.GetServicesSnapshotAsync())
                .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id, StringComparer.Ordinal)
                .ToList();

        public async Task<Service?> TryGetAsync(string id) =>
            (await Manager.GetServicesSnapshotAsync()).FirstOrDefault(s => s.Id == id);

        /// <summary>
        /// 按 ID 精确匹配，其次按名称匹配（均不区分大小写）
        /// </summary>
        public async Task<Service> FindAsync(string key)
        {
            var services = await Manager.GetServicesSnapshotAsync();

            var byId = services.FirstOrDefault(s => string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;

            var byName = services.Where(s => string.Equals(s.Name, key.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return byName[0];
            if (byName.Count > 1) throw new AmbiguousServiceNameException(key, byName.Select(s => s.Id).ToList());

            throw new ServiceNotFoundException(key);
        }

        /// <summary>
        /// 依次查找多个服务，结果去重并保持顺序
        /// </summary>
        public async Task<List<Service>> FindManyAsync(IEnumerable<string> keys)
        {
            var result = new List<Service>();
            foreach (var key in keys)
            {
                var service = await FindAsync(key);
                if (!result.Any(s => s.Id == service.Id)) result.Add(service);
            }
            return result;
        }

        public ServiceRuntimeState? ReadRuntime(string serviceId) => States.Read(serviceId);

        public string? LatestLogPath(string serviceId) => Logs.GetLatestLogPath(serviceId);

        /// <summary>服务正在运行或正在启动</summary>
        public static bool IsActive(string serviceId) =>
            ScmClient.QueryStatus(serviceId).State is ServiceRunState.Running or ServiceRunState.StartPending;

        /// <summary>
        /// 启动服务并等待进入运行状态。启动过程中服务停止时立即失败（附包装器记录的本次失败原因），超时同样失败。
        /// </summary>
        /// <exception cref="ServiceStartException"></exception>
        public async Task StartAsync(string serviceId, int timeoutSeconds = DefaultStartTimeoutSeconds)
        {
            var startedAt = DateTime.Now;
            await Manager.StartServiceAsync(serviceId);

            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            using var controller = new ServiceController(serviceId);
            while (true)
            {
                controller.Refresh();
                var status = controller.Status;
                if (status == ServiceControllerStatus.Running) return;

                // StartService 返回前 SCM 已将状态置为“启动中”，此后再看到“已停止”即表示启动失败
                if (status == ServiceControllerStatus.Stopped)
                {
                    // 只采用本次启动之后记录的失败原因（状态文件保留了以前运行的原因）
                    var state = States.Read(serviceId);
                    var reason = state?.LastFailureAt >= startedAt.AddSeconds(-1) ? state.LastFailureReason : null;
                    throw new ServiceStartException(serviceId, StartFailureKind.StoppedDuringStart, ServiceRunState.Stopped, reason);
                }

                if (DateTime.UtcNow >= deadline)
                    throw new ServiceStartException(serviceId, StartFailureKind.Timeout, (ServiceRunState)(int)status, null);

                await Task.Delay(200);
            }
        }

        /// <summary>
        /// 停止服务（最多等待 30 秒）
        /// </summary>
        /// <exception cref="ServiceStopException">等待后仍在运行</exception>
        public async Task StopAsync(string serviceId)
        {
            await Manager.StopServiceAsync(serviceId);
            if (IsActive(serviceId)) throw new ServiceStopException(serviceId);
        }

        public async Task RestartAsync(string serviceId, int timeoutSeconds = DefaultStartTimeoutSeconds)
        {
            await Manager.StopServiceAsync(serviceId);
            await StartAsync(serviceId, timeoutSeconds);
        }

        /// <summary>
        /// 服务正在运行时重启以应用新配置；未运行时不做任何事。返回是否重启。
        /// </summary>
        public async Task<bool> RestartIfActiveAsync(string serviceId, int timeoutSeconds = DefaultStartTimeoutSeconds)
        {
            if (!IsActive(serviceId)) return false;
            await RestartAsync(serviceId, timeoutSeconds);
            return true;
        }
    }
}
