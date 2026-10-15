using Services.Core.Helpers;
using Services.Core.Services;

namespace Services.Cli
{
    internal static partial class CliApp
    {
        private static async Task<int> MigrateAsync(ParsedArgs args)
        {
            var wrapper = WindowsServiceManager.FindWrapperExecutable()
                ?? throw new CliException(ExitCodes.Failure,
                    $"未找到服务包装器 {ServiceUtils.WrapperExeName}，它应与 {HelpText.Tool}.exe 位于同一目录: {AppContext.BaseDirectory}");

            var ops = await OpenAsync();
            ops.Manager.WrapperExePath = wrapper;
            var result = ops.Manager.MigrateWrapper();

            var message = result.Migrated.Count == 0
                ? "所有服务均已使用当前包装器，无需迁移。"
                : $"已迁移 {result.Migrated.Count} 个服务。正在运行的服务需重启后生效。";
            if (result.RemovedVersions.Count > 0)
                message += $" 已删除 {result.RemovedVersions.Count} 个不再使用的旧版本包装器。";

            if (args.Json)
            {
                WriteJson(new MigrateResult
                {
                    WrapperPath = result.WrapperPath,
                    Migrated = result.Migrated,
                    RemovedVersions = result.RemovedVersions,
                    Message = message
                }, CliJsonContext.Readable.MigrateResult);
            }
            else
            {
                Console.WriteLine(message);
                foreach (var id in result.Migrated) Console.WriteLine($"  {id}");
                if (result.WrapperPath != null) Console.WriteLine($"当前包装器: {result.WrapperPath}");
            }
            return ExitCodes.Success;
        }
    }
}
