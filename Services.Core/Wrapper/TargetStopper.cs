using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Services.Core.Helpers;
using Services.Core.Models;

namespace Services.Core.Wrapper
{
    /// <summary>
    /// 按停止策略停止目标程序：先请求其自行退出（Ctrl+C 或停止命令），超时或请求失败后结束整个进程树。
    /// </summary>
    public sealed class TargetStopper
    {
        private readonly StopPolicySettings _policy;
        private readonly string _exePath;
        private readonly string _workingDir;
        private readonly Action<string> _log;
        private readonly Action _requestAdditionalTime;

        /// <param name="log">写入系统日志行</param>
        /// <param name="requestAdditionalTime">向 SCM 申请延长停止时间（系统关机时传入空操作）</param>
        public TargetStopper(StopPolicySettings policy, string exePath, string workingDir, Action<string> log, Action requestAdditionalTime)
        {
            _policy = policy;
            _exePath = exePath;
            _workingDir = workingDir;
            _log = log;
            _requestAdditionalTime = requestAdditionalTime;
        }

        public void Stop(Process process, int timeoutSeconds)
        {
            if (_policy.Mode != ServiceStopMode.Kill)
            {
                var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                bool requested = _policy.Mode == ServiceStopMode.CtrlC
                    ? RequestStopByCtrlC(process)
                    : RequestStopByCommand(process, deadline);

                if (requested && WaitForExit(process, deadline))
                {
                    _log("Process exited gracefully");
                    return;
                }

                _log(requested
                    ? $"Process did not exit within {timeoutSeconds}s, killing process tree"
                    : "Graceful stop request failed, killing process tree");
            }

            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // 进程已在此期间退出
            }
            process.WaitForExit(5000);
        }

        private bool RequestStopByCtrlC(Process process)
        {
            _log("Sending Ctrl+C to process");
            if (ConsoleControl.SendCtrlC(process.Id)) return true;

            _log("Failed to send Ctrl+C (the process may not have a console)");
            return false;
        }

        /// <summary>
        /// 通过 cmd.exe 在服务工作目录中执行停止命令，输出写入日志。命令本身的执行也计入超时。
        /// 命令无法启动时返回 false；命令返回非 0 时仅记录，仍等待目标程序退出。
        /// </summary>
        private bool RequestStopByCommand(Process process, DateTime deadline)
        {
            var command = StopCommandLine.Expand(_policy.Command, _exePath, _workingDir, process.Id);
            _log($"Running stop command: {command}");

            try
            {
                using var stopProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                        Arguments = StopCommandLine.ToCmdArguments(command),
                        WorkingDirectory = _workingDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    }
                };
                stopProcess.OutputDataReceived += (s, e) => { if (e.Data != null) _log($"[stop] {e.Data}"); };
                stopProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) _log($"[stop] {e.Data}"); };
                stopProcess.Start();
                stopProcess.BeginOutputReadLine();
                stopProcess.BeginErrorReadLine();

                if (WaitForExit(stopProcess, deadline))
                {
                    ProcessOutput.Drain(stopProcess, ProcessOutput.DrainTimeout);
                    if (stopProcess.ExitCode != 0)
                        _log($"Stop command exited with code {stopProcess.ExitCode}");
                }
                else
                {
                    _log("Stop command did not finish in time");
                    try { stopProcess.Kill(true); } catch { }
                }
                return true;
            }
            catch (Exception ex)
            {
                _log($"Failed to run stop command: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 等待进程退出直到截止时间；等待期间定期向 SCM 申请延长停止时间，避免被判定为停止超时
        /// </summary>
        private bool WaitForExit(Process process, DateTime deadline)
        {
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return process.HasExited;

                try { _requestAdditionalTime(); } catch { }

                var slice = (int)Math.Min(remaining.TotalMilliseconds, 2000);
                if (process.WaitForExit(Math.Max(slice, 1))) return true;
            }
        }
    }

    public static class ProcessOutput
    {
        /// <summary>进程退出后等待其输出读完的上限</summary>
        public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// 进程退出后等待重定向的 stdout / stderr 读到末尾，使最后几行输出在“进程退出”之前写入日志、
        /// 且不会在关闭日志后丢失。设置上限：目标程序的子进程继承了输出管道并继续运行时，管道不会结束。
        /// </summary>
        public static void Drain(Process process, TimeSpan timeout)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                process.WaitForExitAsync(cts.Token).Wait();
            }
            catch
            {
                // 超时或进程对象已释放
            }
        }
    }
}
