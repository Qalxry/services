using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Services.Cli
{
    internal sealed class ServiceInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        /// <summary>running / stopped / start_pending / stop_pending / unknown</summary>
        public string State { get; set; } = "";
        public string StatusText { get; set; } = "";
        public int? Pid { get; set; }
        /// <summary>auto / delayed-auto / manual / disabled</summary>
        public string StartupType { get; set; } = "";
        public string ExePath { get; set; } = "";
        /// <summary>保存的原始启动参数（可能为多行）</summary>
        public string? Args { get; set; }
        /// <summary>实际传给程序的命令行参数（多行合并为一行）</summary>
        public string CommandLineArgs { get; set; } = "";
        public string? WorkingDir { get; set; }
        public bool AutoRestart { get; set; }
        public RestartPolicyInfo RestartPolicy { get; set; } = new();
        public StopPolicyInfo StopPolicy { get; set; } = new();
        /// <summary>钩子命令；均未设置时为 null</summary>
        public HooksInfo? Hooks { get; set; }
        /// <summary>服务级失败通知；未设置（沿用全局设置）时为 null</summary>
        public WebhookInfo? Webhook { get; set; }
        /// <summary>健康检查配置；未启用时为 null</summary>
        public HealthCheckInfo? HealthCheck { get; set; }
        public ResourceLimitsInfo ResourceLimits { get; set; } = new();
        public AccountInfo Account { get; set; } = new();
        /// <summary>服务级环境变量（名称 → 值，按名称排序）；值中的 %VAR% 引用在启动时展开。未设置时为空对象</summary>
        public Dictionary<string, string> Environment { get; set; } = new();
        /// <summary>敏感环境变量（名称 → 值）。默认值为 null（遮蔽），--show-secrets 时为真实值；未设置时为空对象</summary>
        public Dictionary<string, string?> SecretEnvironment { get; set; } = new();
        /// <summary>本服务启动前需先启动的服务名（Windows 服务名）。无依赖时为空数组</summary>
        public List<string> Dependencies { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public string? LatestLogPath { get; set; }
        /// <summary>包装器写入的运行状态；尚无状态文件（服务从未以新版包装器运行过）时为 null</summary>
        public RuntimeInfo? Runtime { get; set; }
    }

    internal sealed class RuntimeInfo
    {
        /// <summary>包装器进程 ID</summary>
        public int WrapperPid { get; set; }
        public DateTime WrapperStartedAt { get; set; }
        /// <summary>目标进程 ID；目标进程未运行（已退出、等待重启或服务已停止）时为 null</summary>
        public int? ProcessPid { get; set; }
        public DateTime? ProcessStartedAt { get; set; }
        /// <summary>服务运行时长（秒）；仅服务运行中且状态文件属于当前服务进程时有值，否则为 null</summary>
        public long? UptimeSeconds { get; set; }
        /// <summary>当前计数窗口内的重启次数</summary>
        public int RestartCount { get; set; }
        /// <summary>本次包装器运行以来的重启总次数</summary>
        public int TotalRestarts { get; set; }
        public int? LastExitCode { get; set; }
        public DateTime? LastExitAt { get; set; }
        /// <summary>包装器最近一次以失败状态停止的原因</summary>
        public string? LastFailureReason { get; set; }
        public DateTime? LastFailureAt { get; set; }
        /// <summary>starting / healthy / unhealthy；未配置健康检查时为 null</summary>
        public string? HealthStatus { get; set; }
        public string? HealthMessage { get; set; }
        public DateTime? HealthCheckedAt { get; set; }
        public int HealthFailures { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    internal sealed class RestartPolicyInfo
    {
        public int DelaySeconds { get; set; }
        /// <summary>0 表示不限制</summary>
        public int MaxRestarts { get; set; }
        public int ResetMinutes { get; set; }
        public bool Backoff { get; set; }
    }

    internal sealed class StopPolicyInfo
    {
        /// <summary>kill / ctrl-c / command</summary>
        public string Mode { get; set; } = "kill";
        public string? Command { get; set; }
        public int TimeoutSeconds { get; set; }
    }

    internal sealed class HooksInfo
    {
        public string? PreStart { get; set; }
        public string? PostStop { get; set; }
        public int TimeoutSeconds { get; set; }
    }

    internal sealed class WebhookInfo
    {
        /// <summary>Webhook 地址；默认遮蔽路径与查询部分（可能含令牌），--show-secrets 显示完整地址；无权读取时为 null</summary>
        public string? Url { get; set; }
        /// <summary>订阅的事件：failed / unhealthy / restarting</summary>
        public List<string> Events { get; set; } = new();
    }

    internal sealed class WebhookResult
    {
        public bool Ok { get; set; } = true;
        /// <summary>全局 Webhook；未设置时为 null</summary>
        public WebhookInfo? Webhook { get; set; }
        public string Message { get; set; } = "";
    }

    internal sealed class HealthCheckInfo
    {
        /// <summary>http / tcp / command</summary>
        public string Type { get; set; } = "";
        public string Target { get; set; } = "";
        public int IntervalSeconds { get; set; }
        public int TimeoutSeconds { get; set; }
        public int FailureThreshold { get; set; }
        public int StartPeriodSeconds { get; set; }
    }

    /// <summary>资源限制，0 表示不限制</summary>
    internal sealed class ResourceLimitsInfo
    {
        public int MemoryMB { get; set; }
        public int ProcessMemoryMB { get; set; }
        public int CpuPercent { get; set; }
        public int MaxProcesses { get; set; }
    }

    internal sealed class AccountInfo
    {
        /// <summary>localsystem / localservice / networkservice / virtual / user</summary>
        public string Type { get; set; } = "localsystem";
        /// <summary>SCM 中的账户名，如 LocalSystem、NT SERVICE\&lt;服务ID&gt;、.\svcuser</summary>
        public string Name { get; set; } = "";
    }

    internal sealed class ActionResult
    {
        public bool Ok { get; set; } = true;
        public string Action { get; set; } = "";
        public string Id { get; set; } = "";
        public string Message { get; set; } = "";
        public ServiceInfo? Service { get; set; }
    }

    internal sealed class LogLineInfo
    {
        /// <summary>写入时间（ISO 8601 本地时间，毫秒精度，如 2026-10-15T10:20:30.123）；旧版本日志无时间戳时为 null</summary>
        public string? Timestamp { get; set; }
        /// <summary>stdout / stderr / system</summary>
        public string Stream { get; set; } = "";
        public string Text { get; set; } = "";
    }

    internal sealed class LogsResult
    {
        public string Id { get; set; } = "";
        public string? LogPath { get; set; }
        public List<LogLineInfo> Lines { get; set; } = new();
    }

    internal sealed class MigrateResult
    {
        public bool Ok { get; set; } = true;
        /// <summary>服务现在使用的包装器（受保护目录中的副本）；没有服务时为 null</summary>
        public string? WrapperPath { get; set; }
        /// <summary>被修改了包装器路径的服务 ID</summary>
        public List<string> Migrated { get; set; } = new();
        /// <summary>被删除的旧版本目录名</summary>
        public List<string> RemovedVersions { get; set; } = new();
        public string Message { get; set; } = "";
    }

    internal sealed class DoctorResult
    {
        /// <summary>没有 error 级别的问题</summary>
        public bool Ok { get; set; }
        public DoctorSummary Summary { get; set; } = new();
        public List<DoctorCheck> Checks { get; set; } = new();
    }

    internal sealed class DoctorSummary
    {
        public int Ok { get; set; }
        public int Warning { get; set; }
        public int Error { get; set; }
        public int Skipped { get; set; }
    }

    internal sealed class DoctorCheck
    {
        /// <summary>服务 ID；全局检查为 null</summary>
        public string? ServiceId { get; set; }
        public string? ServiceName { get; set; }
        public string Check { get; set; } = "";
        /// <summary>ok / warning / error / skipped</summary>
        public string Status { get; set; } = "";
        public string Message { get; set; } = "";
        /// <summary>建议的修复方法，没有时为 null</summary>
        public string? Fix { get; set; }
    }

    internal sealed class VersionInfo
    {
        public string Version { get; set; } = "";
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = true)]
    [JsonSerializable(typeof(ServiceInfo))]
    [JsonSerializable(typeof(RuntimeInfo))]
    [JsonSerializable(typeof(List<ServiceInfo>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(ActionResult))]
    [JsonSerializable(typeof(LogsResult))]
    [JsonSerializable(typeof(LogLineInfo))]
    [JsonSerializable(typeof(VersionInfo))]
    [JsonSerializable(typeof(MigrateResult))]
    [JsonSerializable(typeof(DoctorResult))]
    [JsonSerializable(typeof(WebhookResult))]
    internal partial class CliJsonContext : JsonSerializerContext
    {
        /// <summary>
        /// 不转义中文等非 ASCII 字符，便于阅读
        /// </summary>
        public static CliJsonContext Readable { get; } = new(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        /// <summary>
        /// 单行紧凑输出（JSON Lines，用于 logs --follow --json）
        /// </summary>
        public static CliJsonContext Compact { get; } = new(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }
}
