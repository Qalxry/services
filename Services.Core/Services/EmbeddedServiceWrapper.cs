using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Services.Core.Helpers;
using Services.Core.Models;

namespace Services.Core.Services
{
    public class EmbeddedServiceWrapper : ServiceBase
    {
        private Process? _process;
        // 包装器进程自身所在的 Job（KILL_ON_JOB_CLOSE）。生命周期与包装器进程一致，不主动关闭：
        // 关闭句柄会结束 Job 内所有进程（包括包装器自身），应由进程退出时由系统关闭。
        private JobObject? _job;
        private string _serviceName;
        private AsyncLogger? _logger;
        private bool _autoRestart = false;
        private int _restartDelayMs = RestartPolicyDefaults.DelaySeconds * 1000;
        private int _maxRestarts = RestartPolicyDefaults.MaxRestarts;
        private int _restartResetMinutes = RestartPolicyDefaults.ResetMinutes;
        private bool _restartBackoff = RestartPolicyDefaults.Backoff;
        private bool _isStopping = false;
        private int _restartCount = 0;
        private DateTime _lastRestartTime = DateTime.MinValue;

        // 服务以失败状态停止时报告给 SCM 的 Win32 退出码
        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_PROCESS_ABORTED = 1067;

        public EmbeddedServiceWrapper(string serviceName)
        {
            _serviceName = serviceName;
            ServiceName = serviceName;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                var config = LoadConfig();
                LoadRestartPolicy();
                SyncFailureActionsFlag();

                InitLogger();
                EnterJobObject();
                StartTargetProcess(config);
            }
            catch (Exception ex)
            {
                LogCriticalError(ex);
                ExitCode = 1064;
                Stop();
            }
        }

        private void LogSystem(string message)
        {
            _logger?.Log(LogLine.Format(LogLineKind.System, message));
        }

        private void InitLogger()
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "windows_service_logs");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, $"{_serviceName}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            _logger = new AsyncLogger(logFile);
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

        private void LogCriticalError(Exception ex)
        {
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "windows_service_logs");
                Directory.CreateDirectory(logDir);
                var logFile = Path.Combine(logDir, $"{_serviceName}_CRASH_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.WriteAllText(logFile, $"Service Startup Failed:\n{ex}");
            }
            catch { }
        }

        protected override void OnStop()
        {
            _isStopping = true;
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    _process.Kill(true);
                    _process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    LogSystem($"Error stopping process: {ex.Message}");
                }
            }

            _process?.Dispose();
            _process = null;

            _logger?.Dispose();
            _logger = null;
        }

        private (string ExePath, string Args, string WorkingDir) LoadConfig()
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{_serviceName}\Parameters");
            if (key == null) throw new Exception("Service configuration not found in registry");

            var exePath = key.GetValue("ExePath") as string;
            var args = key.GetValue("Args") as string;
            var workingDir = key.GetValue("WorkingDir") as string;

            if (string.IsNullOrEmpty(exePath)) throw new Exception("ExePath is missing in registry");

            return (exePath, ServiceUtils.NormalizeArgs(args), workingDir ?? "");
        }

        private void LoadRestartPolicy()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{_serviceName}\Parameters");
                if (key == null) return;

                _autoRestart = key.GetValue("AutoRestart") is int v && v == 1;
                if (key.GetValue("RestartDelaySeconds") is int delay && delay > 0)
                    _restartDelayMs = Math.Min(delay, 3600) * 1000;
                if (key.GetValue("MaxRestarts") is int max && max >= 0)
                    _maxRestarts = max;
                if (key.GetValue("RestartResetMinutes") is int reset && reset > 0)
                    _restartResetMinutes = reset;
                if (key.GetValue("RestartBackoff") is int backoff)
                    _restartBackoff = backoff == 1;
            }
            catch { }
        }

        /// <summary>
        /// 使“非 0 退出也触发 SCM 恢复策略”与自动重启设置一致，让旧版本创建的服务无需编辑即可生效。
        /// 低权限运行账户无权修改服务配置，失败时忽略（创建 / 编辑服务时已由管理端设置）。
        /// </summary>
        private void SyncFailureActionsFlag()
        {
            try
            {
                ServiceUtils.SetFailureActionsOnNonCrashFailures(_serviceName, _autoRestart);
            }
            catch { }
        }

        /// <summary>
        /// 以失败状态停止服务：记录日志与 Windows 事件日志，并向 SCM 报告非 0 退出码。
        /// 启用自动重启的服务会因此触发 SCM 恢复策略，稍后由 SCM 重新拉起。
        /// </summary>
        private void FailService(int exitCode, string reason)
        {
            LogSystem($"Service failed: {reason} (exit code {exitCode})");
            try
            {
                EventLog.WriteEntry($"{_serviceName}: {reason}", EventLogEntryType.Error);
            }
            catch { }

            ExitCode = exitCode;
            Stop();
        }

        /// <summary>
        /// 按重启策略安排下一次重启。超过最大重启次数时返回 false。
        /// </summary>
        private bool TryScheduleRestart((string ExePath, string Args, string WorkingDir) config, string action)
        {
            if ((DateTime.Now - _lastRestartTime).TotalMinutes > _restartResetMinutes)
                _restartCount = 0;

            _restartCount++;
            if (_maxRestarts > 0 && _restartCount > _maxRestarts)
            {
                LogSystem($"Max restarts ({_maxRestarts}) exceeded.");
                return false;
            }

            int delay = _restartBackoff ? _restartDelayMs << Math.Min(_restartCount - 1, 4) : _restartDelayMs;
            _lastRestartTime = DateTime.Now;

            var limit = _maxRestarts > 0 ? _maxRestarts.ToString() : "unlimited";
            LogSystem($"{action} {_restartCount}/{limit} in {delay}ms");
            Task.Delay(delay).ContinueWith(_ =>
            {
                if (!_isStopping) StartTargetProcess(config);
            });
            return true;
        }

        private void StartTargetProcess((string ExePath, string Args, string WorkingDir) config)
        {
            try
            {
                _process?.Dispose();

                var psi = new ProcessStartInfo
                {
                    FileName = config.ExePath,
                    Arguments = config.Args,
                    WorkingDirectory = string.IsNullOrEmpty(config.WorkingDir) ? (Path.GetDirectoryName(config.ExePath) ?? "") : config.WorkingDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                _process = new Process { StartInfo = psi };

                _process.OutputDataReceived += (s, e) => { if (e.Data != null) _logger?.Log(LogLine.Format(LogLineKind.Stdout, e.Data)); };
                _process.ErrorDataReceived += (s, e) => { if (e.Data != null) _logger?.Log(LogLine.Format(LogLineKind.Stderr, e.Data)); };

                if (!_process.Start())
                {
                    throw new Exception("Process.Start() returned false.");
                }

                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                _process.EnableRaisingEvents = true;
                _process.Exited += (s, e) =>
                {
                    int exitCode = _process.ExitCode;
                    LogSystem($"Process exited (code: {exitCode})");

                    if (_isStopping) return;

                    if (exitCode == 0)
                    {
                        LogSystem("Normal exit, not restarting");
                        Stop();
                        return;
                    }

                    if (!_autoRestart)
                    {
                        FailService(ERROR_PROCESS_ABORTED, $"Process exited with code {exitCode} (AutoRestart disabled)");
                        return;
                    }

                    if (!TryScheduleRestart(config, "Restart"))
                    {
                        FailService(ERROR_PROCESS_ABORTED, $"Process kept failing, gave up after {_maxRestarts} restarts (last exit code {exitCode})");
                    }
                };
            }
            catch (Exception ex)
            {
                LogSystem($"Failed to start: {ex.Message}");

                // 处理文件不存在错误
                if (ex is System.ComponentModel.Win32Exception win32Ex && 
                    (win32Ex.NativeErrorCode == 2 || win32Ex.NativeErrorCode == 3))
                {
                    FailService(ERROR_FILE_NOT_FOUND, $"Executable not found: {config.ExePath}");
                    return;
                }

                // 重试由 Task 续延调用，异常会被吞掉导致服务停留在“运行中”却没有进程，因此不再抛出，统一以失败状态停止
                if (!_autoRestart || !TryScheduleRestart(config, "Retry"))
                {
                    FailService(ERROR_PROCESS_ABORTED, $"Failed to start process: {ex.Message}");
                }
            }
        }
    }
}
