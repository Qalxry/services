using System;
using System.Collections.Generic;
using System.Linq;
using Services.Core.Helpers;
using Services.Core.Models;

namespace Services.Core.Presentation
{
    /// <summary>
    /// 服务信息的中文展示文本（GUI 与 CLI 共用）。Core 的其余部分只使用枚举与数值，不依赖这些文本。
    /// </summary>
    public static class ServiceText
    {
        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>遮蔽后的敏感值</summary>
        public const string SecretMask = "******";

        /// <summary>启动模式下拉框的显示文本，顺序即 <see cref="ServiceStartupModes.ToIndex"/> 的索引</summary>
        public static readonly string[] StartupModeNames = { "开机自启", "延迟自动启动", "手动启动", "已禁用" };

        public static string State(ServiceRunState state) => state switch
        {
            ServiceRunState.Running => "运行中",
            ServiceRunState.Stopped => "已停止",
            ServiceRunState.StartPending => "启动中",
            ServiceRunState.StopPending => "停止中",
            ServiceRunState.ContinuePending => "继续中",
            ServiceRunState.PausePending => "暂停中",
            ServiceRunState.Paused => "已暂停",
            _ => "未知"
        };

        /// <summary>
        /// 命令行 / JSON 中的状态取值：running / stopped / start_pending / stop_pending / continue_pending /
        /// pause_pending / paused / unknown
        /// </summary>
        public static string StateValue(ServiceRunState state) => state switch
        {
            ServiceRunState.Running => "running",
            ServiceRunState.Stopped => "stopped",
            ServiceRunState.StartPending => "start_pending",
            ServiceRunState.StopPending => "stop_pending",
            ServiceRunState.ContinuePending => "continue_pending",
            ServiceRunState.PausePending => "pause_pending",
            ServiceRunState.Paused => "paused",
            _ => "unknown"
        };

        /// <summary>状态文本，运行中时附带 PID</summary>
        public static string StateWithPid(ServiceRunState state, int pid) =>
            pid > 0 ? $"{State(state)} (PID {pid})" : State(state);

        public static string StartupMode(ServiceStartupType type, bool delayed) =>
            Enum.IsDefined(typeof(ServiceStartupType), type) ? StartupModeNames[ServiceStartupModes.ToIndex(type, delayed)] : "未知";

        public static string RestartPolicy(bool enabled, int delaySeconds, int maxRestarts, int resetMinutes, bool backoff)
        {
            if (!enabled) return "未启用";
            var maxText = maxRestarts > 0 ? $"最多 {maxRestarts} 次" : "不限次数";
            var backoffText = backoff ? "，指数退避" : "";
            return $"已启用（延迟 {delaySeconds} 秒{backoffText}，{maxText}，{resetMinutes} 分钟后重置计数）";
        }

        public static string StopPolicy(ServiceStopMode mode, string? command, int timeoutSeconds) => mode switch
        {
            ServiceStopMode.CtrlC => $"发送 Ctrl+C（{timeoutSeconds} 秒未退出则强制结束）",
            ServiceStopMode.Command => $"执行停止命令：{command}（{timeoutSeconds} 秒未退出则强制结束）",
            _ => "直接结束进程"
        };

        /// <summary>
        /// 时长短文本，最多显示两个相邻单位（较小的单位向下取整）：45 秒、5 分 12 秒、2 小时 3 分、3 天 4 小时
        /// </summary>
        public static string Duration(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

            if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays} 天 {duration.Hours} 小时";
            if (duration.TotalHours >= 1) return $"{duration.Hours} 小时 {duration.Minutes} 分";
            if (duration.TotalMinutes >= 1) return $"{duration.Minutes} 分 {duration.Seconds} 秒";
            return $"{duration.Seconds} 秒";
        }

        public static string Restarts(int restartCount, int totalRestarts) =>
            $"本轮 {restartCount} 次，累计 {totalRestarts} 次";

        /// <summary>目标进程从未退出过时返回 null</summary>
        public static string? LastExit(int? exitCode, DateTime? exitAt)
        {
            if (exitCode == null && exitAt == null) return null;

            var code = exitCode is { } c ? $"退出码 {c}" : "退出码未知";
            return exitAt is { } at ? $"{code}（{at.ToString(TimeFormat)}）" : code;
        }

        public static string Time(DateTime time) => time.ToString(TimeFormat);

        /// <summary>服务级失败通知；未设置时说明沿用全局设置</summary>
        public static string Webhook(string? url, string? events, bool readable, bool showValues)
        {
            if (!readable) return "（无权读取）";
            if (string.IsNullOrWhiteSpace(url)) return "沿用全局设置";
            var shown = showValues ? url : Notifications.WebhookSettings.Mask(url);
            var subscribed = Notifications.WebhookEvents.Format(Notifications.WebhookEvents.Parse(events)).Replace(",", "、");
            return $"{shown}（事件：{subscribed}）";
        }

        public static string HealthCheck(HealthCheckType type, string? target, int intervalSeconds, int timeoutSeconds,
            int failureThreshold, int startPeriodSeconds)
        {
            if (type == HealthCheckType.None) return "未启用";
            var kind = type switch { HealthCheckType.Http => "HTTP", HealthCheckType.Tcp => "TCP", _ => "命令" };
            return $"{kind} {target}（每 {intervalSeconds} 秒，超时 {timeoutSeconds} 秒，连续 {failureThreshold} 次失败视为不健康，" +
                   $"启动宽限 {startPeriodSeconds} 秒）";
        }

        /// <summary>健康状态文本；未配置健康检查或尚无结果时返回 null</summary>
        public static string? HealthStatus(string? status, string? message, int failures, DateTime? checkedAt)
        {
            if (status == null) return null;
            var text = status switch
            {
                "healthy" => "健康",
                "unhealthy" => $"不健康（连续失败 {failures} 次）",
                "starting" => failures > 0 ? $"启动中（失败 {failures} 次）" : "启动中",
                _ => status
            };
            if (checkedAt is { } at) text += $"，{Time(at)}";
            return message != null ? $"{text}：{message}" : text;
        }

        public static string Hooks(string? preStart, string? postStop, int timeoutSeconds)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(preStart)) parts.Add($"启动前：{preStart.Trim()}");
            if (!string.IsNullOrWhiteSpace(postStop)) parts.Add($"停止后：{postStop.Trim()}");
            return parts.Count == 0 ? "未设置" : $"{string.Join("；", parts)}（超时 {timeoutSeconds} 秒）";
        }

        public static string ResourceLimits(int memoryMB, int processMemoryMB, int cpuPercent, int maxProcesses)
        {
            var parts = new List<string>();
            if (memoryMB > 0) parts.Add($"内存 {memoryMB} MB");
            if (processMemoryMB > 0) parts.Add($"单进程内存 {processMemoryMB} MB");
            if (cpuPercent > 0) parts.Add($"CPU {cpuPercent}%");
            if (maxProcesses > 0) parts.Add($"进程数 {maxProcesses}");
            return parts.Count == 0 ? "不限制" : string.Join("，", parts);
        }

        /// <summary>
        /// 敏感环境变量：KEY=****** 行（showValues 时显示真实值）；无权读取时值显示为“（无权读取）”
        /// </summary>
        public static string SecretEnvironment(IReadOnlyDictionary<string, string> secrets, bool readable, bool showValues) =>
            string.Join("\n", secrets
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"{p.Key}={(!readable ? "（无权读取）" : showValues ? p.Value : SecretMask)}"));
    }
}
