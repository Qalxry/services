using System.Text;
using System.Text.Json;
using Services.Core.Models;
using Services.Core.Services;

namespace Services.Cli
{
    /// <summary>
    /// export / apply：以配置文件声明式管理服务
    /// </summary>
    internal static partial class CliApp
    {
        private static async Task<int> ExportAsync(ParsedArgs args)
        {
            var manager = await CreateManagerAsync();
            var services = args.Positionals.Count == 0
                ? (await manager.GetServicesSnapshotAsync()).OrderBy(s => s.CreatedAt).ThenBy(s => s.Id, StringComparer.Ordinal).ToList()
                : await FindServicesAsync(manager, args.Positionals);

            var file = new ConfigFile { Services = services.Select(ConfigFileMapper.ToDefinition).ToList() };
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

        private static async Task<List<Service>> FindServicesAsync(WindowsServiceManager manager, IEnumerable<string> keys)
        {
            var result = new List<Service>();
            foreach (var key in keys)
            {
                var service = await FindServiceAsync(manager, key);
                if (!result.Any(s => s.Id == service.Id)) result.Add(service);
            }
            return result;
        }

        private static async Task<int> ApplyAsync(ParsedArgs args)
        {
            bool dryRun = args.HasFlag("dry-run");
            bool prune = args.HasFlag("prune");
            bool restart = args.HasFlag("restart");

            var path = args.GetValue("file") ?? throw new UsageException("缺少必需选项 --file。", "apply");
            var json = path == "-" ? Console.In.ReadToEnd() : ReadConfigFile(path);
            var file = ConfigFileMapper.Deserialize(json);
            if (file.Version != ConfigFile.CurrentVersion)
                throw new UsageException($"不支持的配置文件版本 {file.Version}（当前支持 {ConfigFile.CurrentVersion}）。", "apply");

            var desired = file.Services.Select((d, i) => ConfigFileMapper.ToConfig(d, $"services[{i}]")).ToList();
            var duplicate = desired.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
                throw new UsageException($"配置文件中服务名称 '{duplicate.Key}' 重复。", "apply");

            var manager = await CreateManagerAsync();
            var plan = ApplyPlanner.Build(desired, await manager.GetServicesSnapshotAsync(), prune, File.Exists);

            if (!dryRun && plan.Any(a => a.Action != "unchanged") && !IsAdministrator())
                throw new CliException(ExitCodes.AdminRequired,
                    "apply 会修改系统服务，需要管理员权限。请在“以管理员身份运行”的终端中执行（--dry-run 不需要）。");

            var result = new ApplyResult { DryRun = dryRun };
            string? error = null;
            if (!dryRun)
            {
                foreach (var item in plan)
                {
                    try
                    {
                        await ExecuteApplyItemAsync(manager, item, restart);
                        item.Done = true;
                    }
                    catch (Exception ex)
                    {
                        error = $"{ApplyPlanner.ToActionText(item.Action)} '{item.Name}' 失败: {ex.Message}";
                        break;
                    }
                }
            }

            result.Ok = error == null;
            result.Actions = plan.Select(p => p.ToResult()).ToList();
            int changedCount = plan.Count(a => a.Action != "unchanged");
            result.Message = error ?? (dryRun
                ? $"计划：{SummarizePlan(plan)}（--dry-run，未做任何修改）。"
                : changedCount == 0 ? "所有服务均与配置文件一致，无需修改。" : $"已应用：{SummarizePlan(plan)}。");

            if (args.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, ConfigFileJsonContext.Readable.ApplyResult));
            }
            else
            {
                foreach (var item in plan) Console.WriteLine(item.ToText(dryRun));
                Console.WriteLine();
                Console.WriteLine(result.Message);
            }

            if (error != null)
            {
                Console.Error.WriteLine($"错误: {error}");
                return ExitCodes.Failure;
            }
            return ExitCodes.Success;
        }

        private static string ReadConfigFile(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new UsageException($"配置文件不存在: {fullPath}", "apply");
            return File.ReadAllText(fullPath);
        }

        private static async Task ExecuteApplyItemAsync(WindowsServiceManager manager, ApplyPlanItem item, bool restart)
        {
            switch (item.Action)
            {
                case "create":
                    manager.WrapperExePath ??= ResolveWrapperPath();
                    item.Id = await manager.CreateServiceAsync(item.Config!);
                    break;

                case "update":
                    await manager.UpdateServiceAsync(item.Id!, item.Config!);
                    if (restart && IsRunning(item.Id!))
                    {
                        await manager.StopServiceAsync(item.Id!);
                        await StartAndWaitAsync(manager, item.Id!, DefaultStartTimeoutSeconds);
                        item.Restarted = true;
                    }
                    break;

                case "delete":
                    await manager.DeleteServiceAsync(item.Id!);
                    break;
            }
        }

        private static string SummarizePlan(List<ApplyPlanItem> plan)
        {
            int Count(string action) => plan.Count(p => p.Action == action);
            return $"创建 {Count("create")}、更新 {Count("update")}、删除 {Count("delete")}、不变 {Count("unchanged")}";
        }
    }
}
