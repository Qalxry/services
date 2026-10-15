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
    public sealed partial class MainWindow : Window
    {
        private H.NotifyIcon.TaskbarIcon? TrayIcon;
        private readonly WindowsServiceManager _serviceManager;
        // 与 servicesctl 共用的应用层操作（启停并等待结果等）
        private readonly ServiceOperations _operations;
        private readonly EnvironmentManager _envManager;
        private readonly LogManager _logManager;
        private AppWindow _appWindow;
        private bool _isRealExit = false;
        private DispatcherTimer? _refreshTimer;
        private bool _isRefreshRunning = false;
        private bool _isVisibleRefreshMode = true;
        private bool _isLoadServicesRunning = false;
        private bool _isReloadOnActivateRunning = false;
        private System.Threading.Timer? _logCleanupTimer;
        private CancellationTokenSource? _cleanupCts;
        private RegisteredWaitHandle? _showWindowWait;
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
            _operations = new ServiceOperations(_serviceManager, _logManager);

            this.Activated += OnWindowActivated;

            Title = "ServicesApp";

            this.Closed += OnWindowClosed;

            // 再次启动程序时显示已运行实例的主窗口
            if (Program.ShowWindowEvent != null)
            {
                _showWindowWait = ThreadPool.RegisterWaitForSingleObject(
                    Program.ShowWindowEvent,
                    (_, _) => DispatcherQueue.TryEnqueue(ShowWindow),
                    null,
                    Timeout.Infinite,
                    executeOnlyOnce: false);
            }

            _refreshTimer = new DispatcherTimer { Interval = VisibleRefreshInterval };
            _refreshTimer.Tick += async (s, e) =>
            {
                await RefreshStatusesAsync();
            };
            _refreshTimer.Start();

            // Startup log cleanup
            Task.Run(() =>
            {
                try { _logManager.CleanupLogs(); }
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
            MigrateWrapperInBackground();
            this.Activated += OnWindowReactivated;
        }

        /// <summary>
        /// 将服务切换到当前版本的包装器（受保护目录中的副本）并清理旧版本，
        /// 运行中的服务在下次启动时生效
        /// </summary>
        private async void MigrateWrapperInBackground()
        {
            try
            {
                var result = await Task.Run(() => _serviceManager.MigrateWrapper());
                if (result.Migrated.Count > 0)
                {
                    UpdateStatus($"已将 {result.Migrated.Count} 个服务切换到当前版本的包装器，正在运行的服务重启后生效。");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Migrate wrapper failed: {ex}");
            }
        }

        /// <summary>
        /// 窗口重新获得焦点时从注册表重新加载服务，以显示命令行（servicesctl）或其他途径所做的修改
        /// </summary>
        private async void OnWindowReactivated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated || _isReloadOnActivateRunning) return;

            _isReloadOnActivateRunning = true;
            try
            {
                await _serviceManager.InitializeAsync();
                LoadServices(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Reload on activate failed: {ex}");
            }
            finally
            {
                _isReloadOnActivateRunning = false;
            }
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

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (_appWindow != null)
            {
                _appWindow.Closing -= OnAppWindowClosing;
                _appWindow.Changed -= OnAppWindowChanged;
            }
            
            this.Activated -= OnWindowReactivated;
            _showWindowWait?.Unregister(null);
            _showWindowWait = null;

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

        public void ShowWindow()
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }
            _appWindow.Show();
            _appWindow.MoveInZOrderAtTop();
            this.Activate();
            Win32Helper.SetForegroundWindow(WindowNative.GetWindowHandle(this));
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
                    existing.State = service.State;
                    existing.Pid = service.Pid;
                    existing.UpdatedAt = service.UpdatedAt;
                    if (_sortColumn == SortColumn.Status) ApplySort();
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

                ApplySort();

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

        /// <summary>
        /// 应用层异常的用户可读说明
        /// </summary>
        private static string DescribeError(Exception ex) => ex switch
        {
            ServiceStartException se when se.Kind == StartFailureKind.StoppedDuringStart =>
                "服务启动后立即停止" + (se.Reason != null ? $"：{se.Reason}" : "（目标程序可能已退出）") + "。可在“查看日志”中了解详情。",
            ServiceStartException => "服务在 30 秒内未进入运行状态。可在“查看日志”中了解原因。",
            ServiceStopException => "服务在 30 秒内未能停止。",
            _ => ex.Message
        };

        private void UpdateStatus(string message)
        {
            if (StatusText != null) StatusText.Text = $"{DateTime.Now:HH:mm:ss} - {message}";
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
                            _logManager.CleanupLogs();
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
