using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Services.Core.Helpers;
using Services.Core.Models;

namespace Services.Core.Wrapper
{
    public enum HookKind
    {
        /// <summary>每次启动目标程序之前，失败则不启动</summary>
        PreStart,
        /// <summary>目标程序每次退出之后，失败只记录</summary>
        PostStop
    }

    /// <summary>
    /// 钩子命令配置（来自服务参数）；命令为空表示不执行
    /// </summary>
    public sealed record HookSettings(string PreStart, string PostStop, int TimeoutSeconds)
    {
        public static HookSettings None { get; } = new("", "", HookDefaults.TimeoutSeconds);

        public bool HasPreStart => !string.IsNullOrWhiteSpace(PreStart);
        public bool HasPostStop => !string.IsNullOrWhiteSpace(PostStop);
    }

    /// <summary>
    /// 钩子命令的执行结果。Error 为失败原因（英文，写入日志与失败原因）
    /// </summary>
    public readonly record struct HookResult(bool Succeeded, string? Error)
    {
        public static HookResult Success { get; } = new(true, null);
        public static HookResult Failure(string error) => new(false, error);
    }

    /// <summary>
    /// 执行钩子命令（对应 systemd ExecStartPre= / ExecStopPost=）：在工作目录中通过 cmd 执行，
    /// 使用与目标程序相同的环境变量，输出逐行写入服务日志。非 0 退出码、超时、无法启动均视为失败；
    /// 超时或被取消时结束整个命令进程树。
    /// </summary>
    public sealed class HookRunner
    {
        private readonly string _exePath;
        private readonly string _workingDir;
        private readonly IReadOnlyDictionary<string, string> _environment;
        private readonly Action<string> _log;

        /// <param name="log">写入系统日志行</param>
        public HookRunner(string exePath, string workingDir, IReadOnlyDictionary<string, string> environment, Action<string> log)
        {
            _exePath = exePath;
            _workingDir = workingDir;
            _environment = environment;
            _log = log;
        }

        public static string Label(HookKind kind) => kind == HookKind.PreStart ? "pre-start" : "post-stop";

        /// <param name="pid">目标进程 ID（停止后命令）；启动前命令为 null</param>
        /// <param name="exitCode">目标程序的退出码（停止后命令）；未知时为 null</param>
        /// <param name="requestAdditionalTime">在 SCM 启动 / 停止阶段执行时申请延长时间；其余情况传入空操作</param>
        /// <param name="cancel">服务停止时取消（结束命令进程树）</param>
        public HookResult Run(HookKind kind, string command, int timeoutSeconds, int? pid, int? exitCode,
            Action requestAdditionalTime, CancellationToken cancel)
        {
            var label = Label(kind);
            var expanded = StopCommandLine.Expand(command, _exePath, _workingDir, pid, exitCode);
            _log($"Running {label} command: {expanded}");

            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    Arguments = StopCommandLine.ToCmdArguments(expanded),
                    WorkingDirectory = _workingDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
                EnvironmentVariables.Apply(psi.Environment, _environment, Environment.ExpandEnvironmentVariables);

                process = new Process { StartInfo = psi };
                process.OutputDataReceived += (s, e) => { if (e.Data != null) _log($"[{label}] {e.Data}"); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) _log($"[{label}] {e.Data}"); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                process?.Dispose();
                return Fail(label, $"Failed to run {label} command: {ex.Message}");
            }

            using (process)
            {
                var outcome = WaitForExit(process, DateTime.UtcNow.AddSeconds(timeoutSeconds), requestAdditionalTime, cancel);
                if (outcome != WaitOutcome.Exited)
                {
                    try { process.Kill(true); } catch { }
                    ProcessOutput.Drain(process, ProcessOutput.DrainTimeout);
                    return Fail(label, outcome == WaitOutcome.Cancelled
                        ? $"{Capitalize(label)} command was cancelled because the service is stopping"
                        : $"{Capitalize(label)} command did not finish within {timeoutSeconds}s");
                }

                ProcessOutput.Drain(process, ProcessOutput.DrainTimeout);
                if (process.ExitCode != 0)
                    return Fail(label, $"{Capitalize(label)} command exited with code {process.ExitCode}");
            }

            _log($"{Capitalize(label)} command completed");
            return HookResult.Success;
        }

        private HookResult Fail(string label, string error)
        {
            _log(error);
            return HookResult.Failure(error);
        }

        private enum WaitOutcome { Exited, TimedOut, Cancelled }

        /// <summary>
        /// 等待命令退出直到截止时间；期间定期向 SCM 申请延长时间，并响应取消
        /// </summary>
        private static WaitOutcome WaitForExit(Process process, DateTime deadline, Action requestAdditionalTime, CancellationToken cancel)
        {
            var nextExtension = DateTime.MinValue;
            while (true)
            {
                if (cancel.IsCancellationRequested) return WaitOutcome.Cancelled;
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return process.HasExited ? WaitOutcome.Exited : WaitOutcome.TimedOut;

                if (DateTime.UtcNow >= nextExtension)
                {
                    try { requestAdditionalTime(); } catch { }
                    nextExtension = DateTime.UtcNow.AddSeconds(2);
                }

                var slice = (int)Math.Min(remaining.TotalMilliseconds, 250);
                if (process.WaitForExit(Math.Max(slice, 1))) return WaitOutcome.Exited;
            }
        }

        private static string Capitalize(string label) => char.ToUpperInvariant(label[0]) + label[1..];
    }
}
