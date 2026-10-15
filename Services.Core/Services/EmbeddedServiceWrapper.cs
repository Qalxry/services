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
    /// 服务包装器：作为 Windows 服务运行，启动并监管目标程序（输出写入日志、按策略重启与停止）。
    /// 生命周期由 <see cref="SupervisorStateMachine"/> 决定；所有状态转换与目标程序的启动都在 _gate 锁内完成，
    /// 停止、重启等待、退出事件之间不存在竞态。耗时操作（停止进程、等待输出）与调用 ServiceBase.Stop 在锁外进行。
    /// </summary>
    public class EmbeddedServiceWrapper : ServiceBase
    {
        private readonly string _serviceId;
        private readonly object _gate = new();
        // 停止时取消重启等待
        private readonly CancellationTokenSource _stopCts = new();
        // OnStart 已返回（成功或失败）
        private readonly ManualResetEventSlim _startCompleted = new(false);

        private WrapperSettings? _settings;
        private SupervisorStateMachine? _machine;
        private RuntimeStateRecorder? _runtime;
        private Process? _process;
        // 包装器进程自身所在的 Job（KILL_ON_JOB_CLOSE）。生命周期与包装器进程一致，不主动关闭：
        // 关闭句柄会结束 Job 内所有进程（包括包装器自身），应由进程退出时由系统关闭。
        private JobObject? _job;
        private AsyncLogger? _logger;

        // 系统关机时 SCM 只给服务很短的时间（默认约 5 秒），优雅停止的等待上限
        private const int ShutdownStopTimeoutSeconds = 4;
        // 服务启动阶段出错（配置缺失等）时报告给 SCM 的退出码：ERROR_EXCEPTION_IN_SERVICE
        private const int ErrorExceptionInService = 1064;

        public EmbeddedServiceWrapper(string serviceName)
        {
            _serviceId = serviceName;
            ServiceName = serviceName;
            // 系统关机时同样尝试优雅停止目标程序
            CanShutdown = true;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                StartCore();
            }
            finally
            {
                _startCompleted.Set();
            }
        }

        private void StartCore()
        {
            _runtime = new RuntimeStateRecorder(_serviceId, Environment.ProcessId, DateTime.Now);

            SupervisorDecision decision;
            try
            {
                _settings = WrapperSettings.Load(_serviceId);
                _machine = new SupervisorStateMachine(_settings.Restart);
                SyncFailureActionsFlag(_settings.Restart.Enabled);

                InitLogger();
                if (_settings.SecretsUnavailable)
                    LogSystem("Warning: secret environment variables could not be read by the service account and were not set");
                CleanupLogsInBackground();
                EnterJobObject();

                lock (_gate)
                {
                    decision = StartTargetLocked(_machine.BeginInitialStart());
                }
            }
            catch (Exception ex)
            {
                LogCriticalError(ex);
                FailDuringStart(ErrorExceptionInService, $"Service startup failed: {ex.Message}");
                return;
            }

            // 首次启动即失败且不再重试（程序不存在、未启用自动重启）：在 OnStart 中抛出，
            // 由 ServiceBase 直接以失败状态结束启动。OnStart 期间调用 Stop() 会与启动完成的状态报告相互覆盖。
            if (decision.Action == SupervisorAction.FailService)
            {
                FailDuringStart(decision.ExitCode, decision.Reason!);
                return;
            }
            Execute(decision);
        }

        protected override void OnStop()
        {
            StopTarget(_settings?.Stop.TimeoutSeconds ?? 0, () => RequestAdditionalTime(5000));
        }

        protected override void OnShutdown()
        {
            StopTarget(Math.Min(_settings?.Stop.TimeoutSeconds ?? 0, ShutdownStopTimeoutSeconds), () => { });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stopCts.Dispose();
                _startCompleted.Dispose();
            }
            base.Dispose(disposing);
        }

        #region 目标程序

        /// <summary>
        /// 启动目标程序（调用方持有 _gate，状态机处于 Starting）。返回启动失败时的后续动作。
        /// </summary>
        private SupervisorDecision StartTargetLocked(int generation)
        {
            var settings = _settings!;
            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = settings.ExePath,
                    Arguments = settings.Args,
                    WorkingDirectory = settings.WorkingDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
                // 在继承的环境基础上叠加服务级环境变量，值中的 %VAR% 引用在此展开
                EnvironmentVariables.Apply(psi.Environment, settings.Environment, Environment.ExpandEnvironmentVariables);

                process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (s, e) => { if (e.Data != null) Log(LogLineKind.Stdout, e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) Log(LogLineKind.Stderr, e.Data); };
                // 退出事件只使用本次启动的进程对象与代数：重启会替换 _process，停止流程会释放它
                var started = process;
                process.Exited += (s, e) => OnTargetExited(started, generation);

                if (!process.Start())
                    throw new InvalidOperationException("Process.Start() returned false.");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                _process?.Dispose();
                _process = process;
                _machine!.ProcessStarted(generation);
                _runtime?.ProcessStarted(process.Id, DateTime.Now);
                return SupervisorDecision.None;
            }
            catch (Exception ex)
            {
                process?.Dispose();
                LogSystem($"Failed to start: {ex.Message}");

                bool fileNotFound = ex is System.ComponentModel.Win32Exception win32 &&
                                    win32.NativeErrorCode is 2 or 3;
                return _machine!.ProcessStartFailed(generation, fileNotFound, settings.ExePath, ex.Message, DateTime.Now);
            }
        }

        private void OnTargetExited(Process process, int generation)
        {
            int exitCode;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                // 进程对象已在停止流程中被释放
                exitCode = -1;
            }

            // 先读完剩余输出，使日志中程序的最后几行出现在“进程退出”之前
            ProcessOutput.Drain(process, ProcessOutput.DrainTimeout);

            SupervisorDecision decision;
            lock (_gate)
            {
                if (_machine == null) return;

                // 停止期间的退出同样记录（如 Ctrl+C 后的退出码），但不再据此决定重启或停止
                if (generation == _machine.Generation)
                {
                    LogSystem($"Process exited (code: {exitCode})");
                    _runtime?.ProcessExited(exitCode, DateTime.Now);
                }
                if (_machine.IsStopping) return;

                decision = _machine.ProcessExited(generation, exitCode, DateTime.Now);
            }
            Execute(decision);
        }

        /// <summary>
        /// 执行状态机给出的动作（不持有 _gate：Stop() 会同步调用 OnStop）
        /// </summary>
        private void Execute(SupervisorDecision decision)
        {
            switch (decision.Action)
            {
                case SupervisorAction.StopService:
                    LogSystem("Normal exit, not restarting");
                    WaitUntilRunningReported();
                    Stop();
                    break;

                case SupervisorAction.FailService:
                    WaitUntilRunningReported();
                    FailService(decision.ExitCode, decision.Reason!);
                    break;

                case SupervisorAction.ScheduleRestart:
                    ScheduleRestart(decision);
                    break;
            }
        }

        private void ScheduleRestart(SupervisorDecision decision)
        {
            int scheduledBy;
            lock (_gate)
            {
                var machine = _machine!;
                scheduledBy = machine.Generation;
                _runtime?.Restarts(machine.RestartCount, machine.TotalRestarts);
                var limit = machine.MaxRestarts > 0 ? machine.MaxRestarts.ToString() : "unlimited";
                LogSystem($"{decision.Reason} {decision.Attempt}/{limit} in {(long)decision.Delay.TotalMilliseconds}ms");
            }

            Task.Delay(decision.Delay, _stopCts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;

                SupervisorDecision next;
                lock (_gate)
                {
                    if (!_machine!.TryBeginRestart(scheduledBy, out var generation)) return;
                    next = StartTargetLocked(generation);
                }
                Execute(next);
            }, TaskScheduler.Default);
        }

        /// <summary>
        /// 目标程序在服务启动阶段就退出时，等 ServiceBase 向 SCM 报告“运行中”之后再停止服务：
        /// ServiceBase 在 OnStart 返回后（另一线程上）才报告“运行中”，在此之前调用 Stop() 报告的“已停止”会被覆盖。
        /// </summary>
        private void WaitUntilRunningReported()
        {
            _startCompleted.Wait(TimeSpan.FromSeconds(30));
            try
            {
                using var controller = new ServiceController(_serviceId);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (controller.Status == ServiceControllerStatus.StartPending && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(50);
                    controller.Refresh();
                }
            }
            catch
            {
                // 低权限账户可能无权查询服务状态，直接继续
            }
        }

        #endregion

        #region 停止

        private void StopTarget(int timeoutSeconds, Action requestAdditionalTime)
        {
            Process? process;
            lock (_gate)
            {
                // 停止流程只执行一次；_machine 为 null 表示启动阶段已失败，只需清理
                if (_machine != null && !_machine.BeginStop()) return;
                process = _process;
                _process = null;
            }
            _stopCts.Cancel();

            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        var settings = _settings!;
                        new TargetStopper(settings.Stop, settings.ExePath, settings.WorkingDir, LogSystem, requestAdditionalTime)
                            .Stop(process, timeoutSeconds);
                    }
                    ProcessOutput.Drain(process, ProcessOutput.DrainTimeout);
                }
                catch (Exception ex)
                {
                    LogSystem($"Error stopping process: {ex.Message}");
                }
                process.Dispose();
            }

            _runtime?.ProcessGone();
            lock (_gate)
            {
                _machine?.Stopped();
            }

            _logger?.Dispose();
            _logger = null;
        }

        /// <summary>
        /// 以失败状态停止服务：记录日志与 Windows 事件日志，并向 SCM 报告非 0 退出码。
        /// 启用自动重启的服务会因此触发 SCM 恢复策略，稍后由 SCM 重新拉起。
        /// </summary>
        private void FailService(int exitCode, string reason)
        {
            RecordFailure(exitCode, reason);
            ExitCode = exitCode;
            Stop();
        }

        /// <summary>
        /// OnStart 期间的失败：记录原因、清理，并抛出异常让 ServiceBase 以失败状态结束启动（报告 ExitCode）
        /// </summary>
        private void FailDuringStart(int exitCode, string reason)
        {
            RecordFailure(exitCode, reason);
            ExitCode = exitCode;
            StopTarget(0, () => { });
            throw new ServiceStartFailedException(reason);
        }

        private void RecordFailure(int exitCode, string reason)
        {
            LogSystem($"Service failed: {reason} (exit code {exitCode})");
            try
            {
                EventLog.WriteEntry($"{_serviceId}: {reason}", EventLogEntryType.Error);
            }
            catch { }
            _runtime?.Failed(reason);
        }

        #endregion

        #region 环境

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

        #endregion
    }

    /// <summary>
    /// 服务启动失败（原因已写入日志与运行状态）。由 ServiceBase.Run 重新抛出，入口程序据此以非 0 退出。
    /// </summary>
    public sealed class ServiceStartFailedException : Exception
    {
        public ServiceStartFailedException(string message) : base(message) { }
    }
}
