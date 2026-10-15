using Services.Core.Application;
using Services.Core.Models;

namespace Services.Cli
{
    internal static partial class CliApp
    {
        private static async Task<int> UpdateAsync(ParsedArgs args)
        {
            var ops = await OpenAsync();
            var service = await ops.FindAsync(args.Positionals[0]);

            var config = ServiceConfig.FromService(service);
            int changed = ApplyConfigOptions(args, config);
            if (args.HasValue("name"))
            {
                config.Name = args.GetValue("name")!.Trim();
                changed++;
            }
            if (changed == 0)
                throw new UsageException("未指定任何要修改的字段。", "update");

            var password = ReadPasswordOption(args, config, service);
            await ops.Manager.UpdateServiceAsync(service.Id, config, password);

            var message = $"服务 '{config.Name}' 已更新。";
            if (ServiceOperations.IsActive(service.Id))
            {
                if (args.HasFlag("restart"))
                {
                    await ops.RestartAsync(service.Id);
                    message += " 已重启，新配置已生效。";
                }
                else
                {
                    message += " 服务正在运行，新配置需重启后生效（可使用 --restart 或 restart 命令）。";
                }
            }

            return await WriteActionResultAsync(args, ops, "update", service.Id, message);
        }
    }
}
