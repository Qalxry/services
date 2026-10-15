using System;
using System.Collections.Generic;
using System.Linq;

namespace Services.Core.Notifications
{
    /// <summary>
    /// Webhook 事件名与订阅列表的解析（纯逻辑）
    /// </summary>
    public static class WebhookEvents
    {
        /// <summary>服务以失败状态停止：放弃重启、未启用自动重启时非 0 退出、程序不存在、启动失败</summary>
        public const string Failed = "failed";
        /// <summary>健康检查判定不健康（随后结束目标程序并按重启策略处理）</summary>
        public const string Unhealthy = "unhealthy";
        /// <summary>目标程序异常退出后安排重启（较频繁，默认不订阅）</summary>
        public const string Restarting = "restarting";
        /// <summary>servicesctl webhook test 发送的测试事件（总是发送）</summary>
        public const string Test = "test";

        public static readonly string[] All = { Failed, Unhealthy, Restarting };
        public static readonly string[] Default = { Failed, Unhealthy };

        /// <summary>
        /// 解析逗号 / 空白分隔的事件列表；空表示默认订阅。未知事件名时抛出 ArgumentException。
        /// </summary>
        public static List<string> Parse(string? text)
        {
            var names = (text ?? "").Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();
            if (names.Count == 0) return Default.ToList();

            var unknown = names.FirstOrDefault(n => !All.Contains(n));
            if (unknown != null)
                throw new ArgumentException($"未知的事件 '{unknown}'，可选：{string.Join("、", All)}");
            return All.Where(names.Contains).ToList();
        }

        public static string Format(IEnumerable<string> events) => string.Join(",", events);

        public static bool IsSubscribed(IEnumerable<string> events, string eventName) =>
            eventName == Test || events.Contains(eventName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 生效的 Webhook 设置。Url 为空表示不发送。
    /// </summary>
    public sealed record WebhookSettings(string Url, IReadOnlyList<string> Events)
    {
        public static WebhookSettings None { get; } = new("", WebhookEvents.Default);

        public bool Enabled => !string.IsNullOrWhiteSpace(Url);

        /// <summary>
        /// 服务级设置优先（设置了 URL 时），否则使用全局设置
        /// </summary>
        public static WebhookSettings Resolve(WebhookSettings? service, WebhookSettings? global) =>
            service is { Enabled: true } ? service : global ?? None;

        /// <summary>
        /// 校验 URL：必须是 http / https 绝对地址；为空时表示不发送，合法
        /// </summary>
        public static string? ValidateUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? null
                : "Webhook 地址必须是 http:// 或 https:// 开头的完整 URL";
        }

        /// <summary>
        /// 遮蔽 URL 中可能包含令牌的部分（路径与查询），只保留协议与主机，如 https://hooks.example.com/******
        /// </summary>
        public static string Mask(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return "******";
            bool hasDetail = uri.PathAndQuery.Length > 1 || !string.IsNullOrEmpty(uri.UserInfo);
            return $"{uri.Scheme}://{uri.Authority}{(hasDetail ? "/******" : "/")}";
        }
    }
}
