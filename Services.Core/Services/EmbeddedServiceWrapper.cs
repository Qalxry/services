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

                InitLogger();
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
        /// 按重启策略安排下一次重启。超过最大重启次数时返回 false。
        /// </summary>
        private bool TryScheduleRestart((string ExePath, string Args, string WorkingDir) config, string action)
        {
            if ((DateTime.Now - _lastRestartTime).TotalMinutes > _restartResetMinutes)
                _restartCount = 0;

            _restartCount++;
            if (_maxRestarts > 0 && _restartCount > _maxRestarts)
            {
                LogSystem($"Max restarts ({_maxRestarts}) exceeded. Stopping.");
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

                    if (!_autoRestart || exitCode == 0)
                    {
                        LogSystem(exitCode == 0 ? "Normal exit, not restarting" : "AutoRestart disabled");
                        Stop();
                        return;
                    }

                    if (!TryScheduleRestart(config, "Restart"))
                    {
                        Stop();
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
                    LogSystem("FATAL: Executable not found. Service stopped.");
                    Stop();
                    return;
                }

                if (!_autoRestart) throw;

                if (!TryScheduleRestart(config, "Retry")) throw;
            }
        }
    }
}
