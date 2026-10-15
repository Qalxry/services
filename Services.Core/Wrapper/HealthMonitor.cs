using System;
using System.Threading;
using System.Threading.Tasks;
using Services.Core.Health;

namespace Services.Core.Wrapper
{
    /// <summary>
    /// 周期性检查一个目标进程的健康状态。连续失败达到阈值时调用 onUnhealthy（由包装器结束该进程，
    /// 再按重启策略处理）后结束；进程退出或服务停止时通过取消令牌结束。
    /// </summary>
    public sealed class HealthMonitor
    {
        private readonly HealthCheckSettings _settings;
        private readonly Action<string> _log;
        private readonly RuntimeStateRecorder? _runtime;
        private readonly Action<string> _onUnhealthy;

        /// <param name="onUnhealthy">判定为不健康时调用，参数为最近一次失败的说明</param>
        public HealthMonitor(HealthCheckSettings settings, Action<string> log, RuntimeStateRecorder? runtime, Action<string> onUnhealthy)
        {
            _settings = settings;
            _log = log;
            _runtime = runtime;
            _onUnhealthy = onUnhealthy;
        }

        public Task RunAsync(string exePath, string workingDir, int pid, CancellationToken cancellation) =>
            Task.Run(() => LoopAsync(exePath, workingDir, pid, cancellation), CancellationToken.None);

        private async Task LoopAsync(string exePath, string workingDir, int pid, CancellationToken cancellation)
        {
            var tracker = new HealthTracker(_settings.FailureThreshold, TimeSpan.FromSeconds(_settings.StartPeriodSeconds), DateTime.Now);
            _runtime?.Health(HealthStatuses.Starting, null, 0, null);
            using var probe = new HealthProbe(_settings.Type, _settings.Target, TimeSpan.FromSeconds(_settings.TimeoutSeconds));

            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_settings.IntervalSeconds), cancellation);
                    var (healthy, message) = await probe.CheckAsync(exePath, workingDir, pid, cancellation);
                    cancellation.ThrowIfCancellationRequested();

                    var previous = tracker.Status;
                    bool unhealthy = tracker.Record(healthy, DateTime.Now);
                    _runtime?.Health(tracker.Status, message, tracker.ConsecutiveFailures, DateTime.Now);

                    if (!healthy)
                    {
                        _log(tracker.Status == HealthStatuses.Starting
                            ? $"Health check failed during start period: {message}"
                            : $"Health check failed ({tracker.ConsecutiveFailures}/{_settings.FailureThreshold}): {message}");
                    }
                    else if (previous != HealthStatuses.Healthy)
                    {
                        _log($"Health check passed: {message}");
                    }

                    if (unhealthy)
                    {
                        _onUnhealthy(message);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 进程已退出或服务正在停止
            }
            catch (Exception ex)
            {
                _log($"Health monitor stopped unexpectedly: {ex.Message}");
            }
        }
    }
}
