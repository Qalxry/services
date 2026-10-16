using Services.Core.Application;

namespace Services.Cli
{
    internal static partial class CliApp
    {
        private static async Task<int> StartAsync(ParsedArgs args)
        {
            int timeout = args.GetInt("timeout", 1, 600) ?? ServiceOperations.DefaultStartTimeoutSeconds;
            var ops = await OpenAsync();
            var service = await ops.FindAsync(args.Positionals[0]);

            await ops.StartAsync(service.Id, timeout);
            return await WriteActionResultAsync(args, ops, "start", service.Id, $"服务 '{service.Name}' 已启动。");
        }
    }
}
