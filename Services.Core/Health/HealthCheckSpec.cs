using System;
using Services.Core.Models;

namespace Services.Core.Health
{
    /// <summary>
    /// 健康检查配置的校验与解析（纯逻辑）
    /// </summary>
    public static class HealthCheckSpec
    {
        /// <summary>
        /// 校验检查目标，合法时返回 null，否则返回错误说明
        /// </summary>
        public static string? Validate(HealthCheckType type, string? target) => type switch
        {
            HealthCheckType.None => null,
            HealthCheckType.Http => Uri.TryCreate(target?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? null
                : "HTTP 健康检查的目标必须是 http:// 或 https:// 开头的完整 URL",
            HealthCheckType.Tcp => TryParseTcpTarget(target, out _, out _) ? null : "TCP 健康检查的目标必须是 host:port（端口 1-65535）",
            HealthCheckType.Command => string.IsNullOrWhiteSpace(target) ? "命令健康检查的目标（命令）不能为空" : null,
            _ => "未知的健康检查方式"
        };

        /// <summary>
        /// 解析 host:port（IPv6 地址写作 [::1]:8080）
        /// </summary>
        public static bool TryParseTcpTarget(string? target, out string host, out int port)
        {
            host = "";
            port = 0;
            var text = target?.Trim() ?? "";
            int colon = text.LastIndexOf(':');
            if (colon <= 0 || colon == text.Length - 1) return false;

            host = text.Substring(0, colon).Trim('[', ']');
            return host.Length > 0 && int.TryParse(text.Substring(colon + 1), out port) && port is >= 1 and <= 65535;
        }

        public static string ToValue(HealthCheckType type) => type.ToString().ToLowerInvariant();

        public static bool TryParseType(string? text, out HealthCheckType type)
        {
            switch (text?.Trim().ToLowerInvariant())
            {
                case "none": type = HealthCheckType.None; return true;
                case "http": type = HealthCheckType.Http; return true;
                case "tcp": type = HealthCheckType.Tcp; return true;
                case "command": type = HealthCheckType.Command; return true;
                default: type = HealthCheckType.None; return false;
            }
        }
    }
}
