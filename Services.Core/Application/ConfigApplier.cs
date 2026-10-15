using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Services.Core.Configuration;
using Services.Core.Models;

namespace Services.Core.Application
{
    /// <summary>
    /// 声明式配置的导出、计划与执行（servicesctl export / apply 与 GUI 导入导出共用）
    /// </summary>
    public sealed class ConfigApplier
    {
        private readonly ServiceOperations _operations;

        public ConfigApplier(ServiceOperations operations)
        {
            _operations = operations;
        }

        /// <summary>
        /// 导出服务配置；services 为 null 时导出全部（按添加顺序）。敏感环境变量默认只导出变量名。
        /// </summary>
        public async Task<ConfigFile> ExportAsync(IReadOnlyList<Service>? services = null, bool includeSecretValues = false)
        {
            var selected = services ?? await _operations.ListAsync();
            return new ConfigFile { Services = selected.Select(s => ConfigFileMapper.ToDefinition(s, includeSecretValues)).ToList() };
        }

        /// <summary>
        /// 校验配置文件并生成变更计划，不修改任何服务
        /// </summary>
        /// <exception cref="ConfigFileException">配置无效</exception>
        public async Task<List<ApplyPlanItem>> PlanAsync(ConfigFile file, bool prune)
        {
            if (file.Version != ConfigFile.CurrentVersion)
                throw new ConfigFileException($"不支持的配置文件版本 {file.Version}（当前支持 {ConfigFile.CurrentVersion}）。");

            var desired = file.Services.Select((d, i) => ConfigFileMapper.ToConfig(d, $"services[{i}]")).ToList();
            var duplicate = desired.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
                throw new ConfigFileException($"配置文件中服务名称 '{duplicate.Key}' 重复。");

            return ApplyPlanner.Build(desired, await _operations.Manager.GetServicesSnapshotAsync(), prune, File.Exists);
        }

        /// <summary>
        /// 按计划依次执行，遇到错误即停止。返回出错的计划项与异常（全部成功时为 null）；
        /// 各项的 Done / Restarted / Id 记录执行结果。
        /// </summary>
        /// <param name="restart">更新正在运行的服务后重启以应用新配置</param>
        public async Task<(ApplyPlanItem Item, Exception Error)?> ExecuteAsync(IEnumerable<ApplyPlanItem> plan, bool restart)
        {
            foreach (var item in plan)
            {
                try
                {
                    await ExecuteItemAsync(item, restart);
                    item.Done = true;
                }
                catch (Exception ex)
                {
                    return (item, ex);
                }
            }
            return null;
        }

        private async Task ExecuteItemAsync(ApplyPlanItem item, bool restart)
        {
            var manager = _operations.Manager;
            switch (item.Action)
            {
                case "create":
                    item.Id = await manager.CreateServiceAsync(item.Config!);
                    break;

                case "update":
                    await manager.UpdateServiceAsync(item.Id!, item.Config!);
                    if (restart) item.Restarted = await _operations.RestartIfActiveAsync(item.Id!);
                    break;

                case "delete":
                    await manager.DeleteServiceAsync(item.Id!);
                    break;
            }
        }
    }
}
