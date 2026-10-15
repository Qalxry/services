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
    /// 包装器：日志、Job Object、恢复标志等运行环境
    /// </summary>
    public partial class EmbeddedServiceWrapper
    {
        private void Log(LogLineKind kind, string text)
        {
            _logger?.Log(LogLine.Format(kind, text, DateTime.Now));
        }

        private void LogSystem(string message) => Log(LogLineKind.System, message);

        private void InitLogger()
        {
            var logDir = new LogManager().GetLogDirectory();
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, $"{_serviceId}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            _logger = new AsyncLogger(logFile);
        }

        /// <summary>
        /// 服务启动时按全局设置清理一次日志（GUI 未运行时也能生效）。
        /// 在后台执行且吞掉所有异常，不影响服务启动；正在写入的日志文件无法删除，会被自动跳过。
        /// </summary>
        private static void CleanupLogsInBackground()
        {
            Task.Run(() =>
            {
                try { new LogManager().CleanupLogs(); }
                catch { }
            });
        }

        /// <summary>
        /// 将包装器进程加入 KILL_ON_JOB_CLOSE 的 Job。目标程序及其子进程创建时即继承该 Job，
        /// 包装器以任何方式退出（包括崩溃、被强制结束）时，系统都会结束整个进程树，避免遗留孤儿进程。
        /// </summary>
        private void EnterJobObject()
        {
            try
            {
                _job = new JobObject();
                _job.AssignCurrentProcess();
            }
            catch (Exception ex)
            {
                // 此时 Job 内尚无任何进程，关闭句柄是安全的
                _job?.Dispose();
                _job = null;
                LogSystem($"Warning: failed to create job object, child processes may outlive the wrapper: {ex.Message}");
            }
        }

        /// <summary>
        /// 使“非 0 退出也触发 SCM 恢复策略”与自动重启设置一致，让旧版本创建的服务无需编辑即可生效。
        /// 低权限运行账户无权修改服务配置，失败时忽略（创建 / 编辑服务时已由管理端设置）。
        /// </summary>
        private void SyncFailureActionsFlag(bool autoRestart)
        {
            try
            {
                ScmClient.SetFailureActionsOnNonCrashFailures(_serviceId, autoRestart);
            }
            catch { }
        }

        private void LogCriticalError(Exception ex)
        {
            try
            {
                var logDir = new LogManager().GetLogDirectory();
                Directory.CreateDirectory(logDir);
                var logFile = Path.Combine(logDir, $"{_serviceId}_CRASH_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.WriteAllText(logFile, $"Service Startup Failed:\n{ex}");
            }
            catch { }
        }
    }
}
