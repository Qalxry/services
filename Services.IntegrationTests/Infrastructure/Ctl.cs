using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Services.IntegrationTests.Infrastructure
{
    internal sealed record CtlResult(int ExitCode, string Stdout, string Stderr, string CommandLine)
    {
        public JsonElement Json()
        {
            try
            {
                return JsonDocument.Parse(Stdout).RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{CommandLine} 的输出不是 JSON: {ex.Message}\n{this}");
            }
        }

        public CtlResult EnsureSuccess()
        {
            if (ExitCode != 0) throw new InvalidOperationException($"命令失败（退出码 {ExitCode}）\n{this}");
            return this;
        }

        public override string ToString() =>
            $"> {CommandLine}\n[exit {ExitCode}]\n[stdout]\n{Stdout}\n[stderr]\n{Stderr}";
    }

    /// <summary>
    /// 调用 servicesctl
    /// </summary>
    internal static class Ctl
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

        public static CtlResult Run(params string[] args) => RunWith(ItEnvironment.CtlPath, args);

        public static CtlResult RunWithInput(string input, params string[] args) => RunWith(ItEnvironment.CtlPath, args, input);

        /// <summary>
        /// 运行并要求成功，返回 JSON 输出（自动追加 --json）
        /// </summary>
        public static JsonElement Json(params string[] args) => Run(args.Append("--json").ToArray()).EnsureSuccess().Json();

        public static CtlResult RunWith(string ctl, string[] args, string? input = null)
        {
            var psi = new ProcessStartInfo(ctl)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = input != null,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)!;
            if (input != null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                try { process.Kill(true); } catch { }
                throw new TimeoutException($"servicesctl {string.Join(' ', args)} 超时");
            }
            process.WaitForExit();
            return new CtlResult(process.ExitCode, stdout.Result, stderr.Result, "servicesctl " + string.Join(' ', args.Select(Quote)));
        }

        public static CtlResult RunWith(string ctl, params string[] args) => RunWith(ctl, args, null);

        private static string Quote(string arg) => arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg;
    }
}
