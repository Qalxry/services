using System.Text;
using Services.Core.Application;
using Services.Core.Configuration;

namespace Services.Cli
{
    internal static partial class CliApp
    {
        private static async Task<int> ExportAsync(ParsedArgs args)
        {
            var ops = await OpenAsync();
            var services = args.Positionals.Count == 0 ? null : await ops.FindManyAsync(args.Positionals);
            var file = await new ConfigApplier(ops).ExportAsync(services);
            var json = ConfigFileMapper.Serialize(file);

            var output = args.GetValue("output");
            if (string.IsNullOrEmpty(output) || output == "-")
            {
                Console.WriteLine(json);
            }
            else
            {
                File.WriteAllText(Path.GetFullPath(output), json + Environment.NewLine, new UTF8Encoding(false));
                Console.Error.WriteLine($"已导出 {file.Services.Count} 个服务到 {Path.GetFullPath(output)}");
            }
            return ExitCodes.Success;
        }
    }
}
