using Services.Core.Presentation;

namespace Services.Cli
{
    internal static partial class CliApp
    {
        private static async Task<int> ListAsync(ParsedArgs args)
        {
            var ops = await OpenAsync();
            var services = await ops.ListAsync();

            if (args.Json)
            {
                WriteJson(services.Select(s => ToInfo(s, ops, args.HasFlag("show-secrets"))).ToList(), CliJsonContext.Readable.ListServiceInfo);
                return ExitCodes.Success;
            }

            if (services.Count == 0)
            {
                Console.WriteLine("暂无由 ServicesApp 管理的服务。");
                return ExitCodes.Success;
            }

            var rows = services.Select(s => new[]
            {
                s.Id, s.Name, ServiceText.State(s.State), s.HasPid ? s.Pid.ToString() : "-",
                s.Uptime is { } uptime ? ServiceText.Duration(uptime) : "-",
                ServiceText.StartupMode(s.StartupType, s.DelayedAutoStart), s.ExePath
            }).ToList();
            WriteTable(new[] { "ID", "名称", "状态", "PID", "运行时长", "启动类型", "程序路径" }, rows);
            return ExitCodes.Success;
        }
    }
}
