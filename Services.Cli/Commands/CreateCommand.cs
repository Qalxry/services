using Services.Core.Models;

namespace Services.Cli
{
    internal static partial class CliApp
    {
        private static async Task<int> CreateAsync(ParsedArgs args)
        {
            var name = args.GetValue("name");
            var exe = args.GetValue("exe");
            if (string.IsNullOrWhiteSpace(name)) throw new UsageException("缺少必需选项 --name。", "create");
            if (string.IsNullOrWhiteSpace(exe)) throw new UsageException("缺少必需选项 --exe。", "create");

            var config = new ServiceConfig { Name = name.Trim() };
            ApplyConfigOptions(args, config);

            var ops = await OpenAsync();
            ops.Manager.WrapperExePath = ResolveWrapperPath();
            var password = ReadPasswordOption(args, config, existing: null);
            var id = await ops.Manager.CreateServiceAsync(config, password);

            var message = $"服务 '{config.Name}' 已创建（ID: {id}）。";
            if (args.HasFlag("start"))
            {
                await ops.StartAsync(id);
                message += " 已启动。";
            }

            return await WriteActionResultAsync(args, ops, "create", id, message);
        }
    }
}
