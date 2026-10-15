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
        /// <summary>auto / manual / disabled</summary>
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
        public AccountInfo Account { get; set; } = new();
        /// <summary>服务级环境变量（名称 → 值，按名称排序）；值中的 %VAR% 引用在启动时展开。未设置时为空对象</summary>
        public Dictionary<string, string> Environment { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public string? LatestLogPath { get; set; }
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
        public string WrapperPath { get; set; } = "";
        /// <summary>被修改了包装器路径的服务 ID</summary>
        public List<string> Migrated { get; set; } = new();
        public string Message { get; set; } = "";
    }

    internal sealed class VersionInfo
    {
        public string Version { get; set; } = "";
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = true)]
    [JsonSerializable(typeof(ServiceInfo))]
    [JsonSerializable(typeof(List<ServiceInfo>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(ActionResult))]
    [JsonSerializable(typeof(LogsResult))]
    [JsonSerializable(typeof(LogLineInfo))]
    [JsonSerializable(typeof(VersionInfo))]
    [JsonSerializable(typeof(MigrateResult))]
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
