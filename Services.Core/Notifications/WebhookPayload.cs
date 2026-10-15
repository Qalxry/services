using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Services.Core.Notifications
{
    /// <summary>
    /// POST 到 Webhook 的 JSON 内容
    /// </summary>
    public sealed class WebhookPayload
    {
        /// <summary>failed / unhealthy / restarting / test</summary>
        public string Event { get; set; } = "";
        public string ServiceId { get; set; } = "";
        public string ServiceName { get; set; } = "";
        public string Host { get; set; } = "";
        /// <summary>ISO 8601，带时区偏移</summary>
        public string Time { get; set; } = "";
        /// <summary>人类可读的说明（失败原因、健康检查结果等）</summary>
        public string Message { get; set; } = "";
        public int? ExitCode { get; set; }
        /// <summary>当前计数窗口内的重启次数</summary>
        public int? RestartCount { get; set; }

        public static WebhookPayload Create(string eventName, string serviceId, string serviceName, string message,
            DateTimeOffset time, string host, int? exitCode = null, int? restartCount = null) => new()
        {
            Event = eventName,
            ServiceId = serviceId,
            ServiceName = serviceName,
            Host = host,
            Time = time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            Message = message,
            ExitCode = exitCode,
            RestartCount = restartCount
        };

        public string ToJson() => JsonSerializer.Serialize(this, WebhookJsonContext.Readable.WebhookPayload);
    }

    // 源生成序列化：包装器以裁剪方式发布，不能使用基于反射的序列化
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(WebhookPayload))]
    internal partial class WebhookJsonContext : JsonSerializerContext
    {
        /// <summary>不转义中文等非 ASCII 字符（接收方如聊天机器人直接显示）</summary>
        public static WebhookJsonContext Readable { get; } = new(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }
}
