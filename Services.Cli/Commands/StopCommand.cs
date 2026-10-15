namespace Services.Cli
{
    internal static partial class CliApp
    {
        private static async Task<int> StopAsync(ParsedArgs args)
        {
            var ops = await OpenAsync();
            var service = await ops.FindAsync(args.Positionals[0]);

            await ops.StopAsync(service.Id);
            return await WriteActionResultAsync(args, ops, "stop", service.Id, $"服务 '{service.Name}' 已停止。");
        }
    }
}
