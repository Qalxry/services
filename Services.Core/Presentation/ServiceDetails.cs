using System;
using System.Collections.Generic;
using Services.Core.Helpers;
using Services.Core.Models;

namespace Services.Core.Presentation
{
    /// <summary>
    /// 详情中的一行。Value 为 null 或空白表示无值（由界面显示为占位符）；Monospace 提示路径、命令等宜用等宽字体。
    /// </summary>
    public sealed record DetailRow(string Label, string? Value, bool Monospace = false);

    /// <summary>
    /// 服务详情的展示内容：GUI 详情对话框与 servicesctl get 的文本输出使用同一组行，保证两者一致。
    /// </summary>
    public static class ServiceDetails
    {
        /// <param name="runtime">包装器写入的运行状态，没有状态文件时为 null</param>
        /// <param name="latestLogPath">最新日志路径；为 null 时不输出该行</param>
        /// <param name="showSecrets">显示敏感环境变量的值（默认遮蔽）</param>
        public static List<DetailRow> Build(Service service, ServiceRuntimeState? runtime, string? latestLogPath, DateTime now,
            bool showSecrets = false)
        {
            var uptime = ServiceRuntimeState.GetUptime(runtime, service.Pid, service.IsRunning, now);

            var rows = new List<DetailRow>
            {
                new("服务名称", service.Name),
                new("服务 ID", service.Id, true),
                new("描述", service.Description),
                new("状态", ServiceText.StateWithPid(service.State, service.Pid)),
                new("运行时长", uptime is { } span ? ServiceText.Duration(span) : null),
                new("重启次数", runtime == null ? null : ServiceText.Restarts(runtime.RestartCount, runtime.TotalRestarts)),
                new("上次退出", runtime == null ? null : ServiceText.LastExit(runtime.LastExitCode, runtime.LastExitAt)),
            };
            if (!string.IsNullOrWhiteSpace(runtime?.LastFailureReason))
                rows.Add(new("最近失败原因", runtime.LastFailureReason));

            rows.AddRange(new DetailRow[]
            {
                new("启动类型", ServiceText.StartupMode(service.StartupType, service.DelayedAutoStart)),
                new("失败自动重启", ServiceText.RestartPolicy(service.AutoRestart, service.RestartDelaySeconds,
                    service.MaxRestarts, service.RestartResetMinutes, service.RestartBackoff)),
                new("停止方式", ServiceText.StopPolicy(service.StopMode, service.StopCommand, service.StopTimeoutSeconds)),
                new("运行账户", service.AccountName, true),
                new("可执行文件路径", service.ExePath, true),
                new("工作目录", service.WorkingDir, true),
                new("启动参数", service.Args, true),
                new("环境变量", EnvironmentVariables.Format(service.Environment), true),
                new("敏感环境变量", ServiceText.SecretEnvironment(service.SecretEnvironment, service.SecretsReadable, showSecrets), true),
                new("依赖服务", ServiceDependencies.Format(service.Dependencies), true),
                new("创建时间", ServiceText.Time(service.CreatedAt)),
            });
            if (latestLogPath != null)
                rows.Add(new("最新日志", latestLogPath, true));
            return rows;
        }
    }
}
