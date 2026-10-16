using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Services.Core.Helpers;
using Services.Core.Notifications;
using Services.Core.Scm;
using Services.Core.Wrapper;

namespace Services.Core.Services
{
    /// <summary>
    /// 包装器：目标进程的资源限制、健康检查与失败通知
    /// </summary>
    public partial class EmbeddedServiceWrapper
    {
        /// <summary>
        /// 将刚启动的目标程序加入资源限制 Job。进程启动后立即加入，此后创建的子进程同样受限；
        /// 加入失败只记录警告，不影响运行。
        /// </summary>
        private void ApplyResourceLimits(Process process)
        {
            var limits = _settings!.Limits;
            if (limits.IsEmpty) return;
            try
            {
                if (_limitsJob == null)
                {
                    _limitsJob = JobObject.CreateWithLimits(limits);
                    LogSystem($"Resource limits: memory {Describe(limits.MemoryLimitMB, "MB")}, per-process memory " +
                              $"{Describe(limits.ProcessMemoryLimitMB, "MB")}, CPU {Describe(limits.CpuLimitPercent, "%")}, " +
                              $"processes {Describe(limits.MaxProcesses, "")}");
                }
                _limitsJob.AssignProcess(process.Handle);
            }
            catch (Exception ex)
            {
                LogSystem($"Warning: failed to apply resource limits: {ex.Message}");
            }

            static string Describe(int value, string unit) => value > 0 ? $"{value}{unit}" : "unlimited";
        }

        /// <summary>
        /// 为刚启动的目标进程启动健康检查（调用方持有 _gate）
        /// </summary>
        private void StartHealthMonitor(Process process, int generation)
        {
            var settings = _settings!;
            if (!settings.Health.Enabled) return;

            _healthCts?.Cancel();
            _healthCts?.Dispose();
            _healthCts = CancellationTokenSource.CreateLinkedTokenSource(_stopCts.Token);

            var monitor = new HealthMonitor(settings.Health, LogSystem, _runtime, reason => KillUnhealthy(process, generation, reason));
            _ = monitor.RunAsync(settings.ExePath, settings.WorkingDir, process.Id, _healthCts.Token);
        }

        /// <summary>
        /// 健康检查判定不健康：结束目标进程树，随后的退出按重启策略处理（重启或以失败状态停止）
        /// </summary>
        private void KillUnhealthy(Process process, int generation, string reason)
        {
            lock (_gate)
            {
                if (_machine == null || _machine.State != SupervisorState.Running || _machine.Generation != generation) return;
                _healthKill = (generation, reason);
            }

            LogSystem($"Process is unhealthy after {_settings!.Health.FailureThreshold} consecutive failed checks, killing process tree");
            Notify(WebhookEvents.Unhealthy, $"Health check failed: {reason}", waitForDelivery: false);
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                LogSystem($"Failed to kill unhealthy process: {ex.Message}");
            }
        }

        // 以失败状态停止前等待通知送达的上限（之后进程可能退出）；其余事件在后台发送
        private static readonly TimeSpan BlockingNotifyTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan BackgroundNotifyTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// 按设置发送 Webhook 通知。结果写入日志；任何失败都不影响服务。
        /// </summary>
        /// <param name="waitForDelivery">等待发送完成（服务即将停止、进程可能随之退出时）</param>
        private void Notify(string eventName, string message, bool waitForDelivery, int? exitCode = null, int? restartCount = null)
        {
            var settings = _settings;
            if (settings == null || !settings.Webhook.Enabled || !WebhookEvents.IsSubscribed(settings.Webhook.Events, eventName)) return;

            var payload = WebhookPayload.Create(eventName, _serviceId, settings.DisplayName, message,
                DateTimeOffset.Now, Environment.MachineName, exitCode, restartCount);
            var send = WebhookNotifier.SendAsync(settings.Webhook.Url, payload, waitForDelivery ? BlockingNotifyTimeout : BackgroundNotifyTimeout)
                .ContinueWith(t =>
                {
                    var error = t.IsCompletedSuccessfully ? t.Result : t.Exception?.GetBaseException().Message;
                    LogSystem(error == null ? $"Webhook notified: {eventName}" : $"Webhook notification failed ({eventName}): {error}");
                }, TaskScheduler.Default);

            if (waitForDelivery)
            {
                try { send.Wait(BlockingNotifyTimeout + TimeSpan.FromSeconds(1)); }
                catch { }
            }
        }
    }
}
