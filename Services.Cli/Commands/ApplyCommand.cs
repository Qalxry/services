using System.Text.Json;
using Services.Core.Application;
using Services.Core.Configuration;

namespace Services.Cli
{
    internal static partial class CliApp
    {
        private static async Task<int> ApplyAsync(ParsedArgs args)
        {
            bool dryRun = args.HasFlag("dry-run");
            bool prune = args.HasFlag("prune");
            bool restart = args.HasFlag("restart");

            var path = args.GetValue("file") ?? throw new UsageException("缺少必需选项 --file。", "apply");
            var json = path == "-" ? Console.In.ReadToEnd() : ReadConfigFile(path);

            var ops = await OpenAsync();
            var applier = new ConfigApplier(ops);
            var plan = await applier.PlanAsync(ConfigFileMapper.Deserialize(json), prune);

            if (!dryRun && plan.Any(a => a.Action != "unchanged") && !IsAdministrator())
                throw new CliException(ExitCodes.AdminRequired,
                    "apply 会修改系统服务，需要管理员权限。请在“以管理员身份运行”的终端中执行（--dry-run 不需要）。");

            string? error = null;
            if (!dryRun)
            {
                if (plan.Any(p => p.Action == "create")) ops.Manager.WrapperExePath = ResolveWrapperPath();
                if (await applier.ExecuteAsync(plan, restart) is { } failure)
                {
                    var reason = ToCliException(failure.Error, "apply")?.Message ?? failure.Error.Message;
                    error = $"{ApplyPlanner.ToActionText(failure.Item.Action)} '{failure.Item.Name}' 失败: {reason}";
                }
            }

            int changedCount = plan.Count(a => a.Action != "unchanged");
            var result = new ApplyResult
            {
                DryRun = dryRun,
                Ok = error == null,
                Actions = plan.Select(p => p.ToResult()).ToList(),
                Message = error ?? (dryRun
                    ? $"计划：{SummarizePlan(plan)}（--dry-run，未做任何修改）。"
                    : changedCount == 0 ? "所有服务均与配置文件一致，无需修改。" : $"已应用：{SummarizePlan(plan)}。")
            };

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

        private static string SummarizePlan(List<ApplyPlanItem> plan)
        {
            int Count(string action) => plan.Count(p => p.Action == action);
            return $"创建 {Count("create")}、更新 {Count("update")}、删除 {Count("delete")}、不变 {Count("unchanged")}";
        }
    }
}
