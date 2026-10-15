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
        private bool _stopCompleted = false;
        private ServiceStopMode _stopMode = ServiceStopMode.Kill;
        private string _stopCommand = "";
        private int _stopTimeoutSeconds = StopPolicyDefaults.TimeoutSeconds;
        private (string ExePath, string Args, string WorkingDir) _config;
        private int _restartCount = 0;
        private DateTime _lastRestartTime = DateTime.MinValue;

        // 系统关机时 SCM 只给服务很短的时间（默认约 5 秒），优雅停止的等待上限
        private const int ShutdownStopTimeoutSeconds = 4;

        // 服务以失败状态停止时报告给 SCM 的 Win32 退出码
        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_PROCESS_ABORTED = 1067;

        public EmbeddedServiceWrapper(string serviceName)
        {
            _serviceName = serviceName;
            ServiceName = serviceName;
            // 系统关机时同样尝试优雅停止目标程序
            CanShutdown = true;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                var config = LoadConfig();
                _config = config;
                LoadRestartPolicy();
                LoadStopPolicy();
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
            StopTarget(_stopTimeoutSeconds, requestAdditionalTime: true);
        }

        protected override void OnShutdown()
        {
            StopTarget(Math.Min(_stopTimeoutSeconds, ShutdownStopTimeoutSeconds), requestAdditionalTime: false);
        }

        private void StopTarget(int timeoutSeconds, bool requestAdditionalTime)
        {
            if (_stopCompleted) return;
            _stopCompleted = true;
            _isStopping = true;

            if (_process != null && !_process.HasExited)
            {
                try
                {
                    StopTargetProcess(_process, timeoutSeconds, requestAdditionalTime);
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

        /// <summary>
        /// 按停止方式请求目标程序自行退出，超时（或请求失败）后结束整个进程树
        /// </summary>
        private void StopTargetProcess(Process process, int timeoutSeconds, bool requestAdditionalTime)
        {
            if (_stopMode != ServiceStopMode.Kill)
            {
                var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                bool requested = _stopMode == ServiceStopMode.CtrlC
                    ? RequestStopByCtrlC(process)
                    : RequestStopByCommand(process, deadline, requestAdditionalTime);

                if (requested && WaitForExit(process, deadline, requestAdditionalTime))
                {
                    LogSystem("Process exited gracefully");
                    return;
                }

                LogSystem(requested
                    ? $"Process did not exit within {timeoutSeconds}s, killing process tree"
                    : "Graceful stop request failed, killing process tree");
            }

            process.Kill(true);
            process.WaitForExit(5000);
        }

        private bool RequestStopByCtrlC(Process process)
        {
            LogSystem("Sending Ctrl+C to process");
            if (ConsoleControl.SendCtrlC(process.Id)) return true;

            LogSystem("Failed to send Ctrl+C (the process may not have a console)");
            return false;
        }

        /// <summary>
        /// 通过 cmd.exe 在服务工作目录中执行停止命令，输出写入日志。命令本身的执行也计入超时。
        /// 命令无法启动时返回 false；命令返回非 0 时仅记录，仍等待目标程序退出。
        /// </summary>
        private bool RequestStopByCommand(Process process, DateTime deadline, bool requestAdditionalTime)
        {
            var workingDir = GetWorkingDirectory(_config);
            var command = StopCommandLine.Expand(_stopCommand, _config.ExePath, workingDir, process.Id);
            LogSystem($"Running stop command: {command}");

            try
            {
                using var stopProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                        Arguments = StopCommandLine.ToCmdArguments(command),
                        WorkingDirectory = workingDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    }
                };
                stopProcess.OutputDataReceived += (s, e) => { if (e.Data != null) LogSystem($"[stop] {e.Data}"); };
                stopProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) LogSystem($"[stop] {e.Data}"); };
                stopProcess.Start();
                stopProcess.BeginOutputReadLine();
                stopProcess.BeginErrorReadLine();

                if (WaitForExit(stopProcess, deadline, requestAdditionalTime))
                {
                    if (stopProcess.ExitCode != 0)
                        LogSystem($"Stop command exited with code {stopProcess.ExitCode}");
                }
                else
                {
                    LogSystem("Stop command did not finish in time");
                    try { stopProcess.Kill(true); } catch { }
                }
                return true;
            }
            catch (Exception ex)
            {
                LogSystem($"Failed to run stop command: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 等待进程退出直到截止时间；等待期间定期向 SCM 申请延长停止时间，避免被判定为停止超时
        /// </summary>
        private bool WaitForExit(Process process, DateTime deadline, bool requestAdditionalTime)
        {
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return process.HasExited;

                if (requestAdditionalTime)
                {
                    try { RequestAdditionalTime(5000); } catch { }
                }

                var slice = (int)Math.Min(remaining.TotalMilliseconds, 2000);
                if (process.WaitForExit(Math.Max(slice, 1))) return true;
            }
        }

        private static string GetWorkingDirectory((string ExePath, string Args, string WorkingDir) config) =>
            string.IsNullOrEmpty(config.WorkingDir) ? (Path.GetDirectoryName(config.ExePath) ?? "") : config.WorkingDir;

        private void LoadStopPolicy()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{_serviceName}\Parameters");
                if (key == null) return;

                if (key.GetValue("StopMode") is int mode && Enum.IsDefined(typeof(ServiceStopMode), mode))
                    _stopMode = (ServiceStopMode)mode;
                _stopCommand = key.GetValue("StopCommand") as string ?? "";
                if (key.GetValue("StopTimeoutSeconds") is int timeout && timeout > 0)
                    _stopTimeoutSeconds = Math.Min(timeout, StopPolicyDefaults.MaxTimeoutSeconds);

                // 停止命令为空时无法执行，退回直接结束
                if (_stopMode == ServiceStopMode.Command && string.IsNullOrWhiteSpace(_stopCommand))
                    _stopMode = ServiceStopMode.Kill;
            }
            catch { }
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
                    WorkingDirectory = GetWorkingDirectory(config),
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
