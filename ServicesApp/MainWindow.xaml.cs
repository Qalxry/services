using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI;
using Services.Core.Services;
using Services.Core.Models;
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
    public sealed partial class MainWindow : Window
    {
        private H.NotifyIcon.TaskbarIcon? TrayIcon;
        private readonly WindowsServiceManager _serviceManager;
        private readonly EnvironmentManager _envManager;
        private readonly LogManager _logManager;
        private AppWindow _appWindow;
        private bool _isRealExit = false;
        private DispatcherTimer? _refreshTimer;
        private bool _isRefreshRunning = false;
        private bool _isVisibleRefreshMode = true;
        private bool _isLoadServicesRunning = false;
        private System.Threading.Timer? _logCleanupTimer;
        private CancellationTokenSource? _cleanupCts;
        private static readonly TimeSpan VisibleRefreshInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan HiddenRefreshInterval = TimeSpan.FromSeconds(30);

        public ObservableCollection<Service> Services { get; } = new();

        public MainWindow()
        {
            this.InitializeComponent();
            ((FrameworkElement)this.Content).DataContext = this;

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            
            SetAdaptiveWindowSize();

            _appWindow.Closing += OnAppWindowClosing;
            _appWindow.Changed += OnAppWindowChanged;

            _serviceManager = new WindowsServiceManager();
            _serviceManager.ServiceUpdated += OnServiceUpdated;
            _envManager = new EnvironmentManager();
            _logManager = new LogManager();

            this.Activated += OnWindowActivated;

            Title = "ServicesApp";

            this.Closed += OnWindowClosed;

            _refreshTimer = new DispatcherTimer { Interval = VisibleRefreshInterval };
            _refreshTimer.Tick += async (s, e) =>
            {
                await RefreshStatusesAsync();
            };
            _refreshTimer.Start();

            // Startup log cleanup
            Task.Run(() =>
            {
                try { _logManager.CleanupOldLogs(LogManager.GetGlobalRetentionDays()); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Startup cleanup failed: {ex}"); }
            });

            // Daily cleanup at 2 AM
            ScheduleDailyLogCleanup();
        }

        private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            this.Activated -= OnWindowActivated;
            InitializeTrayIcon();
            await _serviceManager.InitializeAsync();
            LoadServices();
        }

        private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (!_isRealExit)
            {
                args.Cancel = true;
                _appWindow.Hide();
                UpdateTimerState(false);
            }
        }

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (_appWindow.Presenter is not OverlappedPresenter presenter) return;

            UpdateTimerState(presenter.State != OverlappedPresenterState.Minimized);
        }

        private void OnTrayIconDoubleTapped(object sender, RoutedEventArgs e)
        {
            ShowWindow();
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (_appWindow != null)
            {
                _appWindow.Closing -= OnAppWindowClosing;
                _appWindow.Changed -= OnAppWindowChanged;
            }
            
            if (TrayIcon != null)
            {
                TrayIcon.DoubleTapped -= OnTrayIconDoubleTapped;
            }
            
            _refreshTimer?.Stop();
            _refreshTimer = null;

            _logCleanupTimer?.Dispose();
            _logCleanupTimer = null;
            _cleanupCts?.Cancel();
            _cleanupCts?.Dispose();
            
            if (_serviceManager != null)
            {
                _serviceManager.ServiceUpdated -= OnServiceUpdated;
                _serviceManager.Dispose();
            }
            
            TrayIcon?.Dispose();
            this.Closed -= OnWindowClosed;
        }

        private void SetAdaptiveWindowSize()
        {
            try
            {
                var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
                if (displayArea != null)
                {
                    var workArea = displayArea.WorkArea;
                    
                    int targetWidth = (int)(workArea.Width * 0.65);
                    int targetHeight = (int)(workArea.Height * 0.70);
                    
                    const int MinWidth = 1000;
                    const int MinHeight = 700;
                    const int MaxWidth = 1600;
                    const int MaxHeight = 1000;
                    
                    targetWidth = Math.Clamp(targetWidth, MinWidth, MaxWidth);
                    targetHeight = Math.Clamp(targetHeight, MinHeight, MaxHeight);
                    
                    _appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));
                    
                    int x = workArea.X + (workArea.Width - targetWidth) / 2;
                    int y = workArea.Y + (workArea.Height - targetHeight) / 2;
                    _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
                }
                else
                {
                    _appWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to set adaptive window size: {ex.Message}");
                _appWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));
            }
        }

        private void InitializeTrayIcon()
        {
            try
            {
                TrayIcon = new H.NotifyIcon.TaskbarIcon();
                TrayIcon.ToolTipText = "ServicesApp";

                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    TrayIcon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
                }

                RootGrid.Children.Add(TrayIcon);
                TrayIcon.ContextMenuMode = H.NotifyIcon.ContextMenuMode.PopupMenu;
                TrayIcon.NoLeftClickDelay = true;
                TrayIcon.DoubleTapped += OnTrayIconDoubleTapped;

                var flyout = new MenuFlyout();

                var showCommand = new XamlUICommand();
                showCommand.Label = "显示窗口";
                showCommand.ExecuteRequested += (s, e) => ShowWindow();
                var showItem = new MenuFlyoutItem { Text = "显示窗口", Command = showCommand };

                var exitCommand = new XamlUICommand();
                exitCommand.Label = "退出";
                exitCommand.ExecuteRequested += (s, e) => RealExit();
                var exitItem = new MenuFlyoutItem { Text = "退出", Command = exitCommand };

                flyout.Items.Add(showItem);
                flyout.Items.Add(new MenuFlyoutSeparator());
                flyout.Items.Add(exitItem);

                TrayIcon.ContextFlyout = flyout;
                TrayIcon.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tray Icon Init Failed: {ex}");
            }
        }

        public void ShowWindow()
        {
            _appWindow.Show();
            _appWindow.MoveInZOrderAtTop();
            this.Activate();
            UpdateTimerState(true);
            LoadServices(); // Refresh immediately when showing
        }

        public void RealExit()
        {
            _isRealExit = true;
            TrayIcon?.Dispose();
            Application.Current.Exit();
        }


        private void OnServiceUpdated(object? sender, Service service)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                var existing = Services.FirstOrDefault(s => s.Id == service.Id);
                if (existing != null)
                {
                    existing.Status = service.Status;
                    existing.Pid = service.Pid;
                    existing.UpdatedAt = service.UpdatedAt;
                }
            });
        }

        private async void LoadServices(bool silent = false)
        {
            if (_isLoadServicesRunning) return;
            if (silent && _appWindow != null && !_appWindow.IsVisible) return;

            _isLoadServicesRunning = true;
            try
            {
                if (!silent) UpdateStatus("正在加载服务...");
                var list = await _serviceManager.GetServicesSnapshotAsync();

                for (int i = Services.Count - 1; i >= 0; i--)
                {
                    if (!list.Any(s => s.Id == Services[i].Id))
                    {
                        Services.RemoveAt(i);
                    }
                }

                foreach (var item in list)
                {
                    var existing = Services.FirstOrDefault(s => s.Id == item.Id);
                    if (existing == null)
                    {
                        Services.Add(item);
                    }
                    else
                    {
                        existing.CopyFrom(item);
                    }
                }

                if (!silent) UpdateStatus($"已加载 {list.Count} 个服务。");
            }
            catch (Exception ex)
            {
                if (!silent) UpdateStatus($"加载服务失败: {ex.Message}");
            }
            finally
            {
                _isLoadServicesRunning = false;
            }
        }

        private async void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            UpdateStatus("正在刷新服务列表...");
            await _serviceManager.InitializeAsync();
            LoadServices();
            UpdateStatus($"已加载 {Services.Count} 个服务。");
        }

        private void UpdateStatus(string message)
        {
            if (StatusText != null) StatusText.Text = $"{DateTime.Now:HH:mm:ss} - {message}";
        }

        private async void OnStartClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                try
                {
                    UpdateStatus($"正在启动服务 {id}...");
                    await _serviceManager.StartServiceAsync(id);
                    UpdateStatus($"服务 {id} 已启动。");
                }
                catch (Exception ex)
                {
                    await ShowDialog("错误", $"启动服务失败: {ex.Message}");
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
                    await _serviceManager.StopServiceAsync(id);
                    UpdateStatus($"服务 {id} 已停止。");
                }
                catch (Exception ex)
                {
                    await ShowDialog("错误", $"停止服务失败: {ex.Message}");
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
                var service = Services.FirstOrDefault(s => s.Id == serviceId);
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

            AddRow("服务名称", service.Name);
            AddRow("服务 ID", service.Id, true);
            AddRow("描述", service.Description);
            AddRow("状态", service.HasPid ? $"{service.Status} (PID {service.Pid})" : service.Status);
            AddRow("启动类型", GetStartupTypeText(service.StartupType));
            AddRow("失败自动重启", service.AutoRestart ? GetRestartPolicyText(service) : "未启用");
            AddRow("可执行文件路径", service.ExePath, true);
            AddRow("工作目录", service.WorkingDir, true);
            AddRow("启动参数", service.Args, true);
            AddRow("创建时间", service.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));

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

        private static string GetRestartPolicyText(Service service)
        {
            var maxText = service.MaxRestarts > 0 ? $"最多 {service.MaxRestarts} 次" : "不限次数";
            var backoffText = service.RestartBackoff ? "，指数退避" : "";
            return $"已启用（延迟 {service.RestartDelaySeconds} 秒{backoffText}，{maxText}，{service.RestartResetMinutes} 分钟后重置计数）";
        }

        private static string GetStartupTypeText(ServiceStartupType type) => type switch
        {
            ServiceStartupType.Auto => "开机自启",
            ServiceStartupType.Manual => "手动启动",
            ServiceStartupType.Disabled => "已禁用",
            _ => "未知"
        };

        private async void OnAddServiceClick(object sender, RoutedEventArgs e)
        {
            await ShowServiceEditorAsync(null);
        }

        private async void OnEditClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var service = Services.FirstOrDefault(s => s.Id == id);
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
                    await _serviceManager.CreateServiceAsync(config);
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
                await _serviceManager.UpdateServiceAsync(existing.Id, config);
                LoadServices();
                UpdateStatus($"服务 {config.Name} 已更新。");
            }
            catch (Exception ex)
            {
                await ShowDialog("错误", $"更新服务失败: {ex.Message}");
                return;
            }

            if (existing.Status == "运行中" &&
                await ShowConfirmDialog("应用更改", $"服务 '{config.Name}' 正在运行，新配置需重启服务后生效。是否立即重启？"))
            {
                try
                {
                    UpdateStatus($"正在重启服务 {existing.Id}...");
                    await _serviceManager.StopServiceAsync(existing.Id);
                    await _serviceManager.StartServiceAsync(existing.Id);
                    UpdateStatus($"服务 {existing.Id} 已重启。");
                }
                catch (Exception ex)
                {
                    await ShowDialog("错误", $"重启服务失败: {ex.Message}");
                }
            }
        }

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

            stack.Children.Add(infoStack);
            stack.Children.Add(new MenuFlyoutSeparator());
            stack.Children.Add(retentionHeader);
            stack.Children.Add(retentionBox);
            stack.Children.Add(new TextBlock { Text = "修改后立即生效，过期日志会自动清理。", FontSize = 12, Opacity = 0.6 });

            dialog.Content = stack;

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    int days = (int)retentionBox.Value;
                    LogManager.SetGlobalRetentionDays(days);
                    _logManager.CleanupOldLogs(days);
                    UpdateStatus($"设置已保存。已清理早于 {days} 天的日志。");
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

        private async Task ShowDialog(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "确定",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private async Task<bool> ShowConfirmDialog(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = "是",
                CloseButtonText = "否",
                XamlRoot = this.Content.XamlRoot
            };
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        private async Task RefreshStatusesAsync()
        {
            if (_isRefreshRunning) return;

            _isRefreshRunning = true;
            try
            {
                await _serviceManager.RefreshServiceStatusesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Refresh statuses failed: {ex}");
            }
            finally
            {
                _isRefreshRunning = false;
            }
        }

        private void UpdateTimerState(bool isVisible)
        {
            if (_refreshTimer == null) return;
            if (_isVisibleRefreshMode == isVisible && _refreshTimer.IsEnabled) return;

            _isVisibleRefreshMode = isVisible;
            _refreshTimer.Interval = isVisible ? VisibleRefreshInterval : HiddenRefreshInterval;

            if (!_refreshTimer.IsEnabled)
            {
                _refreshTimer.Start();
            }

            if (isVisible)
            {
                _ = RefreshStatusesAsync();
            }
        }

        private void ScheduleDailyLogCleanup()
        {
            _cleanupCts = new CancellationTokenSource();
            var token = _cleanupCts.Token;

            var now = DateTime.Now;
            var today2AM = now.Date.AddHours(2);
            var nextRun = now > today2AM ? today2AM.AddDays(1) : today2AM;
            var initialDelay = nextRun - now;

            _logCleanupTimer = new System.Threading.Timer(
                _ =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        try 
                        { 
                            var days = LogManager.GetGlobalRetentionDays();
                            _logManager.CleanupOldLogs(days); 
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Daily cleanup failed: {ex}"); }
                    }
                },
                null,
                initialDelay,
                TimeSpan.FromDays(1)
            );
        }
    }
}
