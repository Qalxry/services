using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Services.Core.Models;

namespace ServicesApp
{
    /// <summary>
    /// 多选与批量启动 / 停止 / 重启
    /// </summary>
    public sealed partial class MainWindow
    {
        private void OnMultiSelectToggle(object sender, RoutedEventArgs e)
        {
            bool on = MultiSelectToggle.IsChecked == true;
            // 多选模式下点击用于选择，不再打开详情
            ServiceList.SelectionMode = on ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
            ServiceList.IsItemClickEnabled = !on;
            foreach (var button in new[] { BatchStartButton, BatchStopButton, BatchRestartButton })
                button.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            UpdateStatus(on ? "多选模式：勾选服务后使用“启动所选 / 停止所选 / 重启所选”。" : "已退出多选模式。");
        }

        private async void OnBatchStartClick(object sender, RoutedEventArgs e) =>
            await RunBatchAsync("启动", id => _operations.StartAsync(id));

        private async void OnBatchStopClick(object sender, RoutedEventArgs e) =>
            await RunBatchAsync("停止", id => _operations.StopAsync(id));

        private async void OnBatchRestartClick(object sender, RoutedEventArgs e) =>
            await RunBatchAsync("重启", id => _operations.RestartAsync(id));

        /// <summary>
        /// 依次对选中的服务执行操作，单个失败不中断其余服务，结束后汇总失败项
        /// </summary>
        private async Task RunBatchAsync(string verb, Func<string, Task> action)
        {
            var selected = ServiceList.SelectedItems.OfType<Service>().ToList();
            if (selected.Count == 0)
            {
                UpdateStatus($"请先勾选要{verb}的服务。");
                return;
            }

            var failures = new List<string>();
            for (int i = 0; i < selected.Count; i++)
            {
                var service = selected[i];
                UpdateStatus($"正在{verb}（{i + 1}/{selected.Count}）{service.Name}...");
                try
                {
                    await action(service.Id);
                }
                catch (Exception ex)
                {
                    failures.Add($"{service.Name}：{DescribeError(ex)}");
                }
            }

            UpdateStatus(failures.Count == 0
                ? $"已{verb} {selected.Count} 个服务。"
                : $"{verb}完成：成功 {selected.Count - failures.Count} 个，失败 {failures.Count} 个。");
            if (failures.Count > 0)
                await ShowDialog($"部分服务{verb}失败", string.Join("\n", failures));
        }
    }
}
