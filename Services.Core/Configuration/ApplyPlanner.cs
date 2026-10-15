using Services.Core.Models;

namespace Services.Core.Configuration
{
    /// <summary>
    /// apply 的变更计划（纯逻辑，文件存在性检查由调用方注入，便于测试）
    /// </summary>
    public static class ApplyPlanner
    {
        /// <summary>
        /// 按名称（不区分大小写）匹配已有服务，生成创建 / 更新 / 删除 / 不变的计划，并在执行前完成全部校验
        /// </summary>
        public static List<ApplyPlanItem> Build(IReadOnlyList<ServiceConfig> desired, IReadOnlyList<Service> existing, bool prune, Func<string, bool> fileExists)
        {
            var plan = new List<ApplyPlanItem>();

            foreach (var config in desired)
            {
                var matches = existing.Where(s => string.Equals(s.Name, config.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count > 1)
                    throw new ConfigFileException($"名称 '{config.Name}' 匹配到多个已有服务（{string.Join(", ", matches.Select(s => s.Id))}），请先重命名或删除重复的服务。");

                if (matches.Count == 0)
                {
                    RejectUserAccount(config, "创建");
                    plan.Add(new ApplyPlanItem("create", config.Name, null, new List<string>(), config));
                    continue;
                }

                var service = matches[0];
                var changes = ConfigFileMapper.Diff(ServiceConfig.FromService(service), config);
                if (changes.Contains("account")) RejectUserAccount(config, "切换到");
                if (config.AccountType == ServiceAccountType.User && service.AccountType == ServiceAccountType.User)
                {
                    // 同一用户账户：沿用 SCM 中保存的账户名与密码
                    config.AccountName = service.AccountName;
                }

                plan.Add(new ApplyPlanItem(changes.Count == 0 ? "unchanged" : "update", config.Name, service.Id, changes, config));
            }

            if (prune)
            {
                foreach (var service in existing)
                {
                    if (!desired.Any(c => string.Equals(c.Name, service.Name, StringComparison.OrdinalIgnoreCase)))
                        plan.Add(new ApplyPlanItem("delete", service.Name, service.Id, new List<string>(), null));
                }
            }

            foreach (var item in plan.Where(p => p.Action is "create" or "update"))
            {
                if (!fileExists(item.Config!.ExePath))
                    throw new ConfigFileException($"服务 '{item.Name}' 的可执行文件不存在: {item.Config.ExePath}");
            }
            return plan;
        }

        /// <summary>
        /// 指定用户需要密码，配置文件中不保存密码，因此 apply 不负责创建或切换到用户账户
        /// </summary>
        private static void RejectUserAccount(ServiceConfig config, string verb)
        {
            if (config.AccountType != ServiceAccountType.User) return;
            throw new ConfigFileException(
                $"服务 '{config.Name}' 需要{verb}用户账户 '{config.AccountName}'，配置文件不包含密码，无法通过 apply 完成。" +
                $"请先使用 'servicesctl update <服务> --account {config.AccountName} --password-stdin' 设置账户。");
        }

        public static string ToActionText(string action) => action switch
        {
            "create" => "创建",
            "update" => "更新",
            "delete" => "删除",
            _ => "保持"
        };
    }

    public sealed class ApplyPlanItem
    {
        public string Action { get; }
        public string Name { get; }
        public string? Id { get; set; }
        public List<string> Changes { get; }
        public ServiceConfig? Config { get; }
        public bool Done { get; set; }
        public bool Restarted { get; set; }

        public ApplyPlanItem(string action, string name, string? id, List<string> changes, ServiceConfig? config)
        {
            Action = action;
            Name = name;
            Id = id;
            Changes = changes;
            Config = config;
        }

        public ApplyActionResult ToResult() => new()
        {
            Action = Action,
            Name = Name,
            Id = Id,
            Changes = Changes,
            Done = Done,
            Restarted = Restarted
        };

        public string ToText(bool dryRun)
        {
            var symbol = Action switch { "create" => "+", "update" => "~", "delete" => "-", _ => "=" };
            var detail = Action == "update" ? $"（{string.Join(", ", Changes)}）" : "";
            var id = Id != null ? $" [{Id}]" : "";
            var status = dryRun || Action == "unchanged" ? "" : Done ? (Restarted ? "  已完成，已重启" : "  已完成") : "  未执行";
            return $"{symbol} {ApplyPlanner.ToActionText(Action)} {Name}{id}{detail}{status}";
        }
    }
}
