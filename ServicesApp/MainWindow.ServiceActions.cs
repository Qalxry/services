using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI;
using Services.Core.Application;
using Services.Core.Helpers;
using Services.Core.Services;
using Services.Core.Models;
using Services.Core.Presentation;
using System.Collections.ObjectModel;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinRT.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;

namespace ServicesApp
{
    /// <summary>
    /// 服务操作：启动、停止、删除、日志、详情、添加与编辑
    /// </summary>
    public sealed partial class MainWindow
    {
        private async void OnStartClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                try
                {
                    UpdateStatus($"正在启动服务 {id}...");
                    await _operations.StartAsync(id);
                    UpdateStatus($"服务 {id} 已启动。");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"服务 {id} 启动失败。");
                    await ShowDialog("错误", $"启动服务失败: {DescribeError(ex)}");
                }
            }
        }

        private async void OnStopClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                try
                {
                    UpdateStatus($"正在停止服务 {id}...");
                    await _operations.StopAsync(id);
                    UpdateStatus($"服务 {id} 已停止。");
                }
                catch (Exception ex)
                {
                    await ShowDialog("错误", $"停止服务失败: {DescribeError(ex)}");
                }
            }
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var confirm = await ShowConfirmDialog("删除服务", $"确定要删除服务 '{id}' 吗？此操作无法撤销。");
                if (confirm)
                {
                    try
                    {
                        UpdateStatus($"正在删除服务 {id}...");
                        await _serviceManager.DeleteServiceAsync(id);
                        LoadServices(true);
                        UpdateStatus($"服务 {id} 已删除。");
                    }
                    catch (Exception ex)
                    {
                        await ShowDialog("错误", $"删除服务失败: {ex.Message}");
                    }
                }
            }
        }

        private void OnLogsClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string serviceId)
            {
                var service = _allServices.FirstOrDefault(s => s.Id == serviceId);
                if (service != null)
                {
                    ShowLogViewer(service.Id, service.Name);
                }
                else
                {
                    ShowLogViewer(serviceId, serviceId);
                }
            }
        }

        private void ShowLogViewer(string serviceId, string displayName)
        {
            var logWindow = new LogWindow(serviceId, displayName, _logManager);
            logWindow.Activate();
            logWindow.CenterOnScreen(_appWindow);
        }

        private async void OnServiceItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Service service)
            {
                await ShowServiceDetailsAsync(service);
            }
        }

        private async Task ShowServiceDetailsAsync(Service service)
        {
            var grid = new Grid { RowSpacing = 10, ColumnSpacing = 16, MinWidth = 420 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            void AddRow(string label, string? value, bool monospace = false)
            {
                int row = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var labelBlock = new TextBlock { Text = label, Opacity = 0.6 };
                Grid.SetRow(labelBlock, row);

                var valueBlock = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                };
                if (monospace) valueBlock.FontFamily = new FontFamily("Consolas");
                Grid.SetRow(valueBlock, row);
                Grid.SetColumn(valueBlock, 1);

                grid.Children.Add(labelBlock);
                grid.Children.Add(valueBlock);
            }

            // 与 servicesctl get 使用同一组行
            var runtime = new ServiceStateStore().Read(service.Id);
            foreach (var row in ServiceDetails.Build(service, runtime, latestLogPath: null, DateTime.Now))
            {
                AddRow(row.Label, row.Value, row.Monospace);
            }

            var dialog = new ContentDialog
            {
                Title = "服务详情",
                PrimaryButtonText = "编辑",
                SecondaryButtonText = "查看日志",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await ShowServiceEditorAsync(service);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                ShowLogViewer(service.Id, service.Name);
            }
        }

        private async void OnAddServiceClick(object sender, RoutedEventArgs e)
        {
            await ShowServiceEditorAsync(null);
        }

        private async void OnEditClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var service = _allServices.FirstOrDefault(s => s.Id == id);
                if (service != null)
                {
                    await ShowServiceEditorAsync(service);
                }
            }
        }

        /// <summary>
        /// 显示添加/编辑服务对话框。existing 为 null 时为添加模式。
        /// </summary>
        private async Task ShowServiceEditorAsync(Service? existing)
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var form = new ServiceEditorForm(hwnd, existing);

            var dialog = new ContentDialog
            {
                Title = existing == null ? "添加新服务" : $"编辑服务 - {existing.Name}",
                PrimaryButtonText = existing == null ? "创建" : "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = form.View,
                XamlRoot = this.Content.XamlRoot
            };
            dialog.PrimaryButtonClick += (s, args) =>
            {
                if (!form.Validate()) args.Cancel = true;
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var config = form.ToConfig();
            if (existing == null)
            {
                try
                {
                    await _serviceManager.CreateServiceAsync(config, form.Password);
                    LoadServices();
                    UpdateStatus($"服务 {config.Name} 已创建。");
                }
                catch (Exception ex)
                {
                    await ShowDialog("错误", $"创建服务失败: {ex.Message}");
                }
                return;
            }

            try
            {
                await _serviceManager.UpdateServiceAsync(existing.Id, config, form.Password);
                LoadServices();
                UpdateStatus($"服务 {config.Name} 已更新。");
            }
            catch (Exception ex)
            {
                await ShowDialog("错误", $"更新服务失败: {ex.Message}");
                return;
            }

            if (existing.IsRunning &&
                await ShowConfirmDialog("应用更改", $"服务 '{config.Name}' 正在运行，新配置需重启服务后生效。是否立即重启？"))
            {
                try
                {
                    UpdateStatus($"正在重启服务 {existing.Id}...");
                    await _operations.RestartAsync(existing.Id);
                    UpdateStatus($"服务 {existing.Id} 已重启。");
                }
                catch (Exception ex)
                {
                    await ShowDialog("错误", $"重启服务失败: {DescribeError(ex)}");
                }
            }
        }
    }
}
