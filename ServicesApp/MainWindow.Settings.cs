using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI;
using Services.Core.Application;
using Services.Core.Helpers;
using Services.Core.Services;
using Services.Core.Models;
using Services.Core.Storage;
using Services.Core.Notifications;
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
    /// 全局设置与系统环境变量对话框
    /// </summary>
    public sealed partial class MainWindow
    {
        private async void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "应用设置",
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                XamlRoot = this.Content.XamlRoot
            };

            var stack = new StackPanel { Spacing = 15, Padding = new Thickness(0, 10, 0, 0) };

            var infoStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var infoBlock = new TextBlock
            {
                Text = "Windows后台服务管理工具",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center
            };
            var githubLink = new HyperlinkButton
            {
                Content = "GitHub",
                NavigateUri = new Uri("https://github.com/sky22333/services"),
                Padding = new Thickness(8, 4, 8, 4),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12
            };
            ToolTipService.SetToolTip(githubLink, "访问 GitHub 仓库");
            infoStack.Children.Add(infoBlock);
            infoStack.Children.Add(githubLink);

            var retentionHeader = new TextBlock { Text = "日志保留策略", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) };
            var retentionStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var retentionBox = new NumberBox
            {
                Value = LogManager.GetGlobalRetentionDays(),
                Minimum = 1,
                Maximum = 365,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                Width = 200,
                VerticalAlignment = VerticalAlignment.Center,
                Header = "保留天数"
            };
            var maxSizeBox = new NumberBox
            {
                Value = LogManager.GetGlobalMaxTotalSizeMB(),
                Minimum = 0,
                Maximum = 1048576,
                SmallChange = 128,
                LargeChange = 1024,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                Width = 200,
                VerticalAlignment = VerticalAlignment.Center,
                Header = "日志总大小上限 (MB)"
            };

            stack.Children.Add(infoStack);
            stack.Children.Add(new MenuFlyoutSeparator());
            stack.Children.Add(retentionHeader);
            stack.Children.Add(retentionBox);
            stack.Children.Add(maxSizeBox);
            stack.Children.Add(new TextBlock { Text = "修改后立即生效，过期日志会自动清理；0 表示不限制总大小，超出上限时从最旧的日志开始删除。", FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            // 全局失败通知（Webhook）
            var webhook = NotificationStore.ReadGlobal();
            var webhookBox = new TextBox
            {
                Header = "Webhook 地址",
                PlaceholderText = "https://example.com/hooks/...",
                Text = webhook?.Url ?? "",
                Description = "留空表示不发送；服务也可在编辑时单独设置（优先于此处）"
            };
            var webhookEvents = new WebhookEventChecks(webhook?.Events ?? WebhookEvents.Default);
            var testButton = new Button { Content = "发送测试", Margin = new Thickness(0, 4, 0, 0) };
            var testResult = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
            testButton.Click += async (s, args) =>
            {
                var url = webhookBox.Text.Trim();
                if (string.IsNullOrEmpty(url) || WebhookSettings.ValidateUrl(url) is { } invalid)
                {
                    testResult.Text = "请先填写有效的 Webhook 地址。";
                    return;
                }
                testButton.IsEnabled = false;
                testResult.Text = "正在发送...";
                var error = await WebhookNotifier.SendAsync(url,
                    WebhookPayload.Create(WebhookEvents.Test, "", "", "ServicesApp webhook test", DateTimeOffset.Now, Environment.MachineName),
                    TimeSpan.FromSeconds(20));
                testResult.Text = error == null ? "测试事件已发送。" : $"发送失败：{error}";
                testButton.IsEnabled = true;
            };
            stack.Children.Add(new TextBlock { Text = "失败通知（Webhook）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
            stack.Children.Add(webhookBox);
            stack.Children.Add(webhookEvents.View);
            stack.Children.Add(testButton);
            stack.Children.Add(testResult);

            dialog.Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    var webhookUrl = webhookBox.Text.Trim();
                    if (WebhookSettings.ValidateUrl(webhookUrl) is { } webhookError)
                    {
                        await ShowDialog("错误", webhookError + "。");
                        return;
                    }
                    NotificationStore.WriteGlobal(new WebhookSettings(webhookUrl, webhookEvents.Selected));

                    int days = (int)retentionBox.Value;
                    int maxSizeMB = double.IsNaN(maxSizeBox.Value) ? LogManager.GetGlobalMaxTotalSizeMB() : (int)maxSizeBox.Value;
                    LogManager.SetGlobalRetentionDays(days);
                    LogManager.SetGlobalMaxTotalSizeMB(maxSizeMB);
                    _logManager.CleanupOldLogs(days, maxSizeMB);
                    var sizeText = maxSizeMB == 0 ? "不限制日志总大小" : $"日志总大小上限 {maxSizeMB} MB";
                    UpdateStatus($"设置已保存。已清理早于 {days} 天的日志，{sizeText}。");
                }
                catch (Exception ex)
                {
                    await ShowDialog("错误", $"保存设置失败: {ex.Message}");
                }
            }
        }

        private async void OnEnvVarsClick(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "添加到 PATH 环境变量",
                PrimaryButtonText = "添加",
                CloseButtonText = "取消",
                XamlRoot = this.Content.XamlRoot
            };

            var stack = new StackPanel { Spacing = 10 };
            var pathBox = new TextBox { Header = "目录路径", PlaceholderText = "C:\\My\\Tools" };
            var browseBtn = new Button { Content = "浏览目录..." };

            browseBtn.Click += (s, args) =>
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var pickedPath = Win32Helper.PickFolder(hwnd, "选择目录");

                if (pickedPath != null) pathBox.Text = pickedPath;
            };

            stack.Children.Add(pathBox);
            stack.Children.Add(browseBtn);
            dialog.Content = stack;

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (string.IsNullOrWhiteSpace(pathBox.Text)) return;

                try
                {
                    _envManager.AddToPath(pathBox.Text);
                    UpdateStatus("环境变量已更新。");
                    await ShowDialog("成功", "路径添加成功。");
                }
                catch (Exception ex)
                {
                    await ShowDialog("错误", $"更新环境变量失败: {ex.Message}");
                }
            }
        }
    }
}
