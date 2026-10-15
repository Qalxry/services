using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Services.Core.Helpers;
using Services.Core.Models;

namespace Services.Core.Health
{
    /// <summary>
    /// 执行一次健康检查。超时或任何异常都视为不健康，返回说明文字。
    /// </summary>
    public sealed class HealthProbe : IDisposable
    {
        private readonly HealthCheckType _type;
        private readonly string _target;
        private readonly TimeSpan _timeout;
        private readonly HttpClient? _http;

        public HealthProbe(HealthCheckType type, string target, TimeSpan timeout)
        {
            _type = type;
            _target = target.Trim();
            _timeout = timeout;
            if (type == HealthCheckType.Http)
            {
                // 不跟随重定向：3xx 本身即视为健康
                _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = timeout };
            }
        }

        /// <param name="exePath">命令检查的 {exe} 占位符</param>
        /// <param name="workingDir">命令检查的工作目录与 {workdir} 占位符</param>
        /// <param name="pid">命令检查的 {pid} 占位符</param>
        public async Task<(bool Healthy, string Message)> CheckAsync(string exePath, string workingDir, int pid, CancellationToken cancellation)
        {
            try
            {
                return _type switch
                {
                    HealthCheckType.Http => await CheckHttpAsync(cancellation),
                    HealthCheckType.Tcp => await CheckTcpAsync(cancellation),
                    HealthCheckType.Command => await CheckCommandAsync(exePath, workingDir, pid, cancellation),
                    _ => (true, "not configured")
                };
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                return (false, $"timed out after {_timeout.TotalSeconds:0}s");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (false, ex.GetBaseException().Message);
            }
        }

        private async Task<(bool, string)> CheckHttpAsync(CancellationToken cancellation)
        {
            using var response = await _http!.GetAsync(_target, HttpCompletionOption.ResponseHeadersRead, cancellation);
            int code = (int)response.StatusCode;
            return (code is >= 200 and < 400, $"HTTP {code}");
        }

        private async Task<(bool, string)> CheckTcpAsync(CancellationToken cancellation)
        {
            if (!HealthCheckSpec.TryParseTcpTarget(_target, out var host, out var port))
                return (false, $"invalid target {_target}");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(_timeout);
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            return (true, $"connected to {host}:{port}");
        }

        private async Task<(bool, string)> CheckCommandAsync(string exePath, string workingDir, int pid, CancellationToken cancellation)
        {
            var command = StopCommandLine.Expand(_target, exePath, workingDir, pid);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    Arguments = StopCommandLine.ToCmdArguments(command),
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(cancellation);
            _ = process.StandardError.ReadToEndAsync(cancellation);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(_timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                throw;
            }

            var firstLine = (await output).Split('\n', 2)[0].Trim();
            var detail = firstLine.Length > 0 ? $": {(firstLine.Length > 200 ? firstLine[..200] : firstLine)}" : "";
            return (process.ExitCode == 0, $"exit code {process.ExitCode}{detail}");
        }

        public void Dispose() => _http?.Dispose();
    }
}
