using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Services.Core.Application;
using Services.Core.Configuration;
using WinRT.Interop;

namespace ServicesApp
{
    /// <summary>
    /// 导入 / 导出声明式配置（与 servicesctl export / apply 使用同一套逻辑与文件格式）
    /// </summary>
    public sealed partial class MainWindow
    {
        private static readonly (string Name, string Spec)[] JsonFilters = { ("JSON 配置文件", "*.json"), ("所有文件", "*.*") };

        private async void OnExportClick(object sender, RoutedEventArgs e)
        {
            var path = Win32Helper.SaveFile(WindowNative.GetWindowHandle(this), "导出服务配置", "services.json", "json", JsonFilters);
            if (path == null) return;

            try
            {
                var file = await new ConfigApplier(_operations).ExportAsync();
                await File.WriteAllTextAsync(path, ConfigFileMapper.Serialize(file) + Environment.NewLine, new UTF8Encoding(false));
                UpdateStatus($"已导出 {file.Services.Count} 个服务到 {path}（敏感环境变量的值与 Webhook 地址不导出）。");
            }
            catch (Exception ex)
            {
                await ShowDialog("错误", $"导出失败: {ex.Message}");
            }
        }

        private async void OnImportClick(object sender, RoutedEventArgs e)
        {
            var path = Win32Helper.PickFile(WindowNative.GetWindowHandle(this), "导入服务配置", JsonFilters);
            if (path == null) return;

            ConfigFile file;
            try
            {
                file = ConfigFileMapper.Deserialize(await File.ReadAllTextAsync(path));
            }
            catch (Exception ex)
            {
                await ShowDialog("无法读取配置文件", ex.Message);
                return;
            }

            var applier = new ConfigApplier(_operations);
            List<ApplyPlanItem> plan;
            try
            {
                plan = await applier.PlanAsync(file, prune: false);
            }
            catch (ConfigFileException ex)
            {
                await ShowDialog("配置文件无效", ex.Message);
                return;
            }

            // 变更预览：勾选“删除多余服务”时重新生成计划
            var planText = new TextBlock { FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
            var pruneCheck = new CheckBox { Content = "删除配置文件中不存在的服务" };
            var restartCheck = new CheckBox { Content = "更新后重启正在运行的服务，使新配置立即生效" };
            var dialog = new ContentDialog
            {
                Title = $"导入 {Path.GetFileName(path)}",
                PrimaryButtonText = "应用",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot,
                Content = new ScrollViewer
                {
                    Content = new StackPanel { Spacing = 12, Children = { summary, planText, pruneCheck, restartCheck } },
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }
            };

            void ShowPlan()
            {
                planText.Text = string.Join("\n", plan.Select(item => item.ToText(dryRun: true)));
                int changes = plan.Count(item => item.Action != "unchanged");
                summary.Text = changes == 0
                    ? "所有服务均与配置文件一致，无需修改。"
                    : $"将创建 {Count("create")}、更新 {Count("update")}、删除 {Count("delete")} 个服务，{Count("unchanged")} 个保持不变。" +
                      "（+ 创建  ~ 更新，括号内为变化的字段  - 删除  = 不变）";
                dialog.IsPrimaryButtonEnabled = changes > 0;
            }
            int Count(string action) => plan.Count(item => item.Action == action);

            pruneCheck.Click += async (s, args) =>
            {
                try
                {
                    plan = await applier.PlanAsync(file, pruneCheck.IsChecked == true);
                    ShowPlan();
                }
                catch (ConfigFileException ex)
                {
                    summary.Text = ex.Message;
                }
            };
            ShowPlan();

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            UpdateStatus("正在应用配置...");
            var failure = await applier.ExecuteAsync(plan, restartCheck.IsChecked == true);
            LoadServices(true);

            var result = string.Join("\n", plan.Select(item => item.ToText(dryRun: false)));
            if (failure is { } error)
            {
                UpdateStatus("导入未完成。");
                await ShowDialog("导入未完成",
                    $"{ApplyPlanner.ToActionText(error.Item.Action)} '{error.Item.Name}' 失败: {DescribeError(error.Error)}\n\n{result}");
            }
            else
            {
                UpdateStatus($"已导入：创建 {Count("create")}、更新 {Count("update")}、删除 {Count("delete")}。");
            }
        }
    }
}
