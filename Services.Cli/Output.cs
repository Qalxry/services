using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Services.Core.Application;
using Services.Core.Health;
using Services.Core.Notifications;
using Services.Core.Helpers;
using Services.Core.Models;
using Services.Core.Presentation;

namespace Services.Cli
{
    /// <summary>
    /// 输出：JSON 模型转换、详情文本、表格
    /// </summary>
    internal static partial class CliApp
    {
        private static async Task<int> WriteActionResultAsync(ParsedArgs args, ServiceOperations ops, string action, string id, string message)
        {
            await ops.Manager.InitializeAsync();
            var service = await ops.TryGetAsync(id);

            if (args.Json)
            {
                var info = service == null ? null : ToInfo(service, ops);
                WriteJson(new ActionResult { Action = action, Id = id, Message = message, Service = info }, CliJsonContext.Readable.ActionResult);
            }
            else
            {
                Console.WriteLine(message);
                if (service != null)
                {
                    Console.WriteLine();
                    WriteServiceText(service, ops);
                }
            }
            return ExitCodes.Success;
        }

        private static ServiceInfo ToInfo(Service s, ServiceOperations ops, bool showSecrets = false)
        {
            return new ServiceInfo
            {
                Id = s.Id,
                Name = s.Name,
                Description = s.Description,
                State = ServiceText.StateValue(s.State),
                StatusText = ServiceText.State(s.State),
                Pid = s.HasPid ? s.Pid : null,
                StartupType = ServiceStartupModes.ToCliValue(s.StartupType, s.DelayedAutoStart),
                ExePath = s.ExePath,
                Args = s.Args,
                CommandLineArgs = ServiceUtils.NormalizeArgs(s.Args),
                WorkingDir = s.WorkingDir,
                AutoRestart = s.AutoRestart,
                RestartPolicy = new RestartPolicyInfo
                {
                    DelaySeconds = s.RestartDelaySeconds,
                    MaxRestarts = s.MaxRestarts,
                    ResetMinutes = s.RestartResetMinutes,
                    Backoff = s.RestartBackoff
                },
                Account = new AccountInfo
                {
                    Type = s.AccountType.ToString().ToLowerInvariant(),
                    Name = s.AccountName ?? ServiceAccounts.LocalSystemName
                },
                StopPolicy = new StopPolicyInfo
                {
                    Mode = ToStopModeText(s.StopMode),
                    Command = string.IsNullOrEmpty(s.StopCommand) ? null : s.StopCommand,
                    TimeoutSeconds = s.StopTimeoutSeconds
                },
                Hooks = string.IsNullOrWhiteSpace(s.PreStartCommand) && string.IsNullOrWhiteSpace(s.PostStopCommand) ? null : new HooksInfo
                {
                    PreStart = string.IsNullOrWhiteSpace(s.PreStartCommand) ? null : s.PreStartCommand,
                    PostStop = string.IsNullOrWhiteSpace(s.PostStopCommand) ? null : s.PostStopCommand,
                    TimeoutSeconds = s.HookTimeoutSeconds
                },
                Webhook = string.IsNullOrWhiteSpace(s.WebhookUrl) ? null : new WebhookInfo
                {
                    Url = showSecrets ? s.WebhookUrl : WebhookSettings.Mask(s.WebhookUrl),
                    Events = WebhookEvents.Parse(s.WebhookEvents)
                },
                HealthCheck = s.HealthCheckType == HealthCheckType.None ? null : new HealthCheckInfo
                {
                    Type = HealthCheckSpec.ToValue(s.HealthCheckType),
                    Target = s.HealthCheckTarget ?? "",
                    IntervalSeconds = s.HealthCheckIntervalSeconds,
                    TimeoutSeconds = s.HealthCheckTimeoutSeconds,
                    FailureThreshold = s.HealthCheckFailureThreshold,
                    StartPeriodSeconds = s.HealthCheckStartPeriodSeconds
                },
                ResourceLimits = new ResourceLimitsInfo
                {
                    MemoryMB = s.MemoryLimitMB,
                    ProcessMemoryMB = s.ProcessMemoryLimitMB,
                    CpuPercent = s.CpuLimitPercent,
                    MaxProcesses = s.MaxProcesses
                },
                Environment = s.Environment
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(pair => pair.Key, pair => pair.Value),
                SecretEnvironment = s.SecretEnvironment
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(pair => pair.Key, pair => showSecrets && s.SecretsReadable ? pair.Value : null),
                Dependencies = s.Dependencies.ToList(),
                CreatedAt = s.CreatedAt,
                LatestLogPath = ops.LatestLogPath(s.Id),
                Runtime = ToRuntimeInfo(s, ops.ReadRuntime(s.Id))
            };
        }

        private static RuntimeInfo? ToRuntimeInfo(Service s, ServiceRuntimeState? state)
        {
            if (state == null) return null;

            var uptime = ServiceRuntimeState.GetUptime(state, s.Pid, s.IsRunning, DateTime.Now);
            return new RuntimeInfo
            {
                WrapperPid = state.WrapperPid,
                WrapperStartedAt = state.WrapperStartedAt,
                ProcessPid = state.ProcessPid,
                ProcessStartedAt = state.ProcessStartedAt,
                UptimeSeconds = uptime is { } span ? (long)span.TotalSeconds : null,
                RestartCount = state.RestartCount,
                TotalRestarts = state.TotalRestarts,
                LastExitCode = state.LastExitCode,
                LastExitAt = state.LastExitAt,
                LastFailureReason = state.LastFailureReason,
                LastFailureAt = state.LastFailureAt,
                HealthStatus = state.HealthStatus,
                HealthMessage = state.HealthMessage,
                HealthCheckedAt = state.HealthCheckedAt,
                HealthFailures = state.HealthFailures,
                UpdatedAt = state.UpdatedAt
            };
        }

        private static string ToStopModeText(ServiceStopMode mode) => mode switch
        {
            ServiceStopMode.CtrlC => "ctrl-c",
            ServiceStopMode.Command => "command",
            _ => "kill"
        };

        /// <summary>
        /// 详情文本：与 GUI 详情对话框使用同一组行（Presentation.ServiceDetails）
        /// </summary>
        private static void WriteServiceText(Service service, ServiceOperations ops, bool showSecrets = false)
        {
            var rows = ServiceDetails.Build(service, ops.ReadRuntime(service.Id), ops.LatestLogPath(service.Id), DateTime.Now, showSecrets);

            int width = rows.Max(r => DisplayWidth(r.Label));
            foreach (var row in rows)
            {
                // 多行的值（如环境变量、多行参数）从第二行起缩进到值所在的列
                var text = string.IsNullOrWhiteSpace(row.Value)
                    ? "-"
                    : row.Value.Replace("\r\n", "\n").Replace("\n", "\n" + new string(' ', width + 2));
                Console.WriteLine($"{PadDisplay(row.Label, width)}  {text}");
            }
        }

        private static void WriteTable(string[] headers, List<string[]> rows)
        {
            var widths = headers.Select((h, i) => Math.Max(DisplayWidth(h), rows.Max(r => DisplayWidth(r[i])))).ToArray();

            Console.WriteLine(string.Join("  ", headers.Select((h, i) => PadDisplay(h, widths[i]))).TrimEnd());
            foreach (var row in rows)
            {
                Console.WriteLine(string.Join("  ", row.Select((c, i) => PadDisplay(c, widths[i]))).TrimEnd());
            }
        }

        // 中日韩等全角字符按 2 列宽计算，使表格对齐
        private static int DisplayWidth(string text) => text.Sum(c => c >= 0x1100 && (c <= 0x115F || c >= 0x2E80) ? 2 : 1);

        private static string PadDisplay(string text, int width) => text + new string(' ', Math.Max(0, width - DisplayWidth(text)));

        private static void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo)
        {
            Console.WriteLine(JsonSerializer.Serialize(value, typeInfo));
        }
    }
}
