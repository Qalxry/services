using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Services.Core.Helpers;
using Services.Core.Scm;
using Services.Core.Wrapper;

namespace Services.Core.Services
{
    /// <summary>
    /// 包装器：目标进程的资源限制与健康检查
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
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                LogSystem($"Failed to kill unhealthy process: {ex.Message}");
            }
        }
    }
}
