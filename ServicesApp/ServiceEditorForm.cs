using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Services.Core.Helpers;
using Services.Core.Models;
using Services.Core.Presentation;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ServicesApp
{
    /// <summary>
    /// 添加/编辑服务共用的表单
    /// </summary>
    public sealed partial class ServiceEditorForm
    {
        private readonly IntPtr _ownerHwnd;
        private readonly TextBox _nameBox;
        private readonly ComboBox _startupBox;
        private readonly TextBox _exeBox;
        private readonly TextBox _argsBox;
        private readonly TextBox _workDirBox;
        private readonly TextBox _descriptionBox;
        private readonly TextBox _environmentBox;
        private readonly TextBox _secretEnvironmentBox;
        private readonly CheckBox _autoRestartCheck;
        private readonly NumberBox _restartDelayBox;
        private readonly NumberBox _maxRestartsBox;
        private readonly NumberBox _restartResetBox;
        private readonly CheckBox _restartBackoffCheck;
        private readonly FrameworkElement _restartPolicyPanel;
        private readonly ComboBox _stopModeBox;
        private readonly NumberBox _stopTimeoutBox;
        private readonly TextBox _stopCommandBox;
        private readonly ComboBox _healthTypeBox;
        private readonly TextBox _healthTargetBox;
        private readonly NumberBox _healthIntervalBox;
        private readonly NumberBox _healthTimeoutBox;
        private readonly NumberBox _healthRetriesBox;
        private readonly NumberBox _healthStartPeriodBox;
        private readonly NumberBox _memoryLimitBox;
        private readonly NumberBox _processMemoryLimitBox;
        private readonly NumberBox _cpuLimitBox;
        private readonly NumberBox _maxProcessesBox;
        private readonly ComboBox _accountBox;
        private readonly TextBlock _accountHint;
        private readonly TextBox _accountNameBox;
        private readonly PasswordBox _passwordBox;
        private readonly TextBox _dependenciesBox;
        private readonly Service? _existing;
        private readonly TextBlock _errorText;

        public FrameworkElement View { get; }

        public ServiceEditorForm(IntPtr ownerHwnd, Service? existing = null)
        {
            _ownerHwnd = ownerHwnd;
            _existing = existing;

            // Name | Startup Type

            _nameBox = new TextBox { Header = "服务名称 (仅字母数字)", PlaceholderText = "MyService" };

            _startupBox = new ComboBox { Header = "启动类型", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var name in ServiceText.StartupModeNames)
                _startupBox.Items.Add(name);

            // Exe Path | File picker
            _exeBox = new TextBox { Header = "可执行文件路径", PlaceholderText = "C:\\Path\\To\\App.exe" };
            var exeRow = CreatePickerRow(_exeBox, "\uE8E5", "选择可执行文件", OnPickExe);

            // WorkDir | Folder picker
            _workDirBox = new TextBox { Header = "工作目录 (可选)", PlaceholderText = "默认为程序所在目录" };
            var workDirRow = CreatePickerRow(_workDirBox, "\uE838", "选择工作目录", OnPickWorkDir);

            // Args (multi-line)
            _argsBox = new TextBox
            {
                Header = "启动参数 (可选)",
                PlaceholderText = "--config config.json",
                Description = "支持多行输入，启动时各行以空格拼接",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 80,
                MaxHeight = 160
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_argsBox, ScrollBarVisibility.Auto);

            // Description (multi-line)
            _descriptionBox = new TextBox
            {
                Header = "描述 (可选)",
                PlaceholderText = "服务用途说明",
                Description = "系统服务描述将显示为：Managed by Windows Service Manager: {描述}",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 60,
                MaxHeight = 120
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_descriptionBox, ScrollBarVisibility.Auto);

            // Environment variables (multi-line KEY=VALUE)
            _environmentBox = new TextBox
            {
                Header = "环境变量 (可选)",
                PlaceholderText = "NODE_ENV=production",
                Description = "每行一个 KEY=VALUE；值中可用 %PATH% 等引用现有变量",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                MinHeight = 60,
                MaxHeight = 120
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_environmentBox, ScrollBarVisibility.Auto);

            // Secret environment variables (values masked)
            _secretEnvironmentBox = new TextBox
            {
                Header = "敏感环境变量 (可选)",
                PlaceholderText = "API_KEY=...",
                Description = $"每行一个 KEY=VALUE；值只有 SYSTEM、管理员与运行账户可读，界面中显示为 {ServiceText.SecretMask}，保持不变即沿用原值",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                MinHeight = 40,
                MaxHeight = 120
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_secretEnvironmentBox, ScrollBarVisibility.Auto);

            // Auto Restart

            _autoRestartCheck = new CheckBox { Content = "失败自动重启", IsChecked = false, VerticalAlignment = VerticalAlignment.Center };

            // Restart policy (visible when auto restart is enabled)
            _restartDelayBox = CreateNumberBox("重启延迟 (秒)", 1, 3600, RestartPolicyDefaults.DelaySeconds);
            _maxRestartsBox = CreateNumberBox("最大重启次数", 0, 1000, RestartPolicyDefaults.MaxRestarts);
            _maxRestartsBox.Description = "0 表示不限制";
            _restartResetBox = CreateNumberBox("计数重置 (分钟)", 1, 1440, RestartPolicyDefaults.ResetMinutes);
            _restartResetBox.Description = "超过该时间未再失败则重新计数";
            _restartBackoffCheck = new CheckBox { Content = "指数退避（每次重启延迟翻倍，最多 16 倍）", IsChecked = RestartPolicyDefaults.Backoff };

            var policyGrid = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
            for (int i = 0; i < 3; i++)
                policyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            policyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            policyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(_maxRestartsBox, 1);
            Grid.SetColumn(_restartResetBox, 2);
            Grid.SetRow(_restartBackoffCheck, 1);
            Grid.SetColumnSpan(_restartBackoffCheck, 3);
            policyGrid.Children.Add(_restartDelayBox);
            policyGrid.Children.Add(_maxRestartsBox);
            policyGrid.Children.Add(_restartResetBox);
            policyGrid.Children.Add(_restartBackoffCheck);

            _restartPolicyPanel = new Border
            {
                Child = policyGrid,
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(4),
                Background = GetThemeBrush("CardBackgroundFillColorSecondaryBrush"),
                Visibility = Visibility.Collapsed
            };
            _autoRestartCheck.Checked += (s, e) => _restartPolicyPanel.Visibility = Visibility.Visible;
            _autoRestartCheck.Unchecked += (s, e) => _restartPolicyPanel.Visibility = Visibility.Collapsed;

            // Stop mode | Stop timeout
            _stopModeBox = new ComboBox { Header = "停止方式", HorizontalAlignment = HorizontalAlignment.Stretch };
            _stopModeBox.Items.Add("直接结束进程");
            _stopModeBox.Items.Add("发送 Ctrl+C（控制台程序）");
            _stopModeBox.Items.Add("执行停止命令");

            _stopTimeoutBox = CreateNumberBox("停止超时 (秒)", 1, StopPolicyDefaults.MaxTimeoutSeconds, StopPolicyDefaults.TimeoutSeconds);
            _stopTimeoutBox.Description = "超时未退出则强制结束进程树";

            // Stop command
            _stopCommandBox = new TextBox
            {
                Header = "停止命令",
                PlaceholderText = "{exe} -s quit",
                Description = "在工作目录中通过 cmd 执行；占位符：{exe} 程序路径、{workdir} 工作目录（自动加引号）、{pid} 进程 ID"
            };
            _stopModeBox.SelectionChanged += (s, e) => UpdateStopControls();

            // Health check
            _healthTypeBox = new ComboBox { Header = "健康检查", HorizontalAlignment = HorizontalAlignment.Stretch };
            _healthTypeBox.Items.Add("不检查");
            _healthTypeBox.Items.Add("HTTP（2xx / 3xx 为健康）");
            _healthTypeBox.Items.Add("TCP 端口可连接");
            _healthTypeBox.Items.Add("命令（退出码 0 为健康）");
            _healthTargetBox = new TextBox
            {
                Header = "检查目标",
                PlaceholderText = "http://127.0.0.1:8080/health",
                Description = "HTTP 为 URL；TCP 为 host:port；命令可用 {exe} {workdir} {pid}"
            };
            _healthIntervalBox = CreateNumberBox("检查间隔 (秒)", 1, HealthCheckDefaults.MaxIntervalSeconds, HealthCheckDefaults.IntervalSeconds);
            _healthTimeoutBox = CreateNumberBox("检查超时 (秒)", 1, HealthCheckDefaults.MaxTimeoutSeconds, HealthCheckDefaults.TimeoutSeconds);
            _healthRetriesBox = CreateNumberBox("连续失败次数", 1, HealthCheckDefaults.MaxFailureThreshold, HealthCheckDefaults.FailureThreshold);
            _healthRetriesBox.Description = "达到后结束程序并按重启策略处理";
            _healthStartPeriodBox = CreateNumberBox("启动宽限 (秒)", 0, HealthCheckDefaults.MaxStartPeriodSeconds, HealthCheckDefaults.StartPeriodSeconds);
            _healthStartPeriodBox.Description = "程序启动后这段时间内的失败不计数";
            _healthTypeBox.SelectionChanged += (s, e) => UpdateHealthControls();

            // Resource limits (0 = unlimited)
            _memoryLimitBox = CreateNumberBox("内存上限 (MB)", 0, ResourceLimitRanges.MaxMemoryMB, 0);
            _memoryLimitBox.Description = "目标程序及其子进程合计；0 表示不限制";
            _processMemoryLimitBox = CreateNumberBox("单进程内存上限 (MB)", 0, ResourceLimitRanges.MaxMemoryMB, 0);
            _processMemoryLimitBox.Description = "0 表示不限制";
            _cpuLimitBox = CreateNumberBox("CPU 上限 (%)", 0, ResourceLimitRanges.MaxCpuPercent, 0);
            _cpuLimitBox.Description = "占全部 CPU 的百分比；0 表示不限制";
            _maxProcessesBox = CreateNumberBox("进程数上限", 0, ResourceLimitRanges.MaxProcesses, 0);
            _maxProcessesBox.Description = "含目标程序自身；0 表示不限制";

            // Run-as account | Hint
            _accountBox = new ComboBox { Header = "运行账户", HorizontalAlignment = HorizontalAlignment.Stretch };
            _accountBox.Items.Add("本地系统（LocalSystem）");
            _accountBox.Items.Add("本地服务（LocalService）");
            _accountBox.Items.Add("网络服务（NetworkService）");
            _accountBox.Items.Add("虚拟账户（NT SERVICE\\服务ID）");
            _accountBox.Items.Add("指定用户");

            _accountHint = new TextBlock
            {
                Text = "低权限账户需能读取程序与工作目录（及程序要写入的目录）；日志目录权限会自动授予。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Bottom
            };

            // User name | Password
            _accountNameBox = new TextBox { Header = "账户名", PlaceholderText = @".\svcuser 或 DOMAIN\svcuser" };

            _passwordBox = new PasswordBox
            {
                Header = "密码",
                PlaceholderText = existing?.AccountType == ServiceAccountType.User ? "留空表示不修改密码" : ""
            };
            _accountBox.SelectionChanged += (s, e) => UpdateAccountControls();

            // Service dependencies (comma / newline separated service names)
            _dependenciesBox = new TextBox
            {
                Header = "依赖服务 (可选)",
                PlaceholderText = "Tcpip, MySQL80",
                Description = "启动本服务前会先启动这些服务；填写 Windows 服务名，如 Tcpip、MySQL80，多个用逗号分隔"
            };

            // Validation message
            _errorText = new TextBlock
            {
                Foreground = GetThemeBrush("SystemFillColorCriticalBrush"),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };

            // 表单行（自上而下）
            var form = new FormRows(columns: 2, spacing: 12);
            form.Add(_nameBox, _startupBox);
            form.Add(exeRow);
            form.Add(workDirRow);
            form.Add(_argsBox);
            form.Add(_descriptionBox);
            form.Add(_environmentBox);
            form.Add(_secretEnvironmentBox);
            form.Add(_autoRestartCheck, null);
            form.Add(_restartPolicyPanel);
            form.Add(_stopModeBox, _stopTimeoutBox);
            form.Add(_stopCommandBox);
            form.Add(_healthTypeBox, _healthTargetBox);
            form.Add(_healthIntervalBox, _healthTimeoutBox);
            form.Add(_healthRetriesBox, _healthStartPeriodBox);
            form.Add(_memoryLimitBox, _processMemoryLimitBox);
            form.Add(_cpuLimitBox, _maxProcessesBox);
            form.Add(_accountBox, _accountHint);
            form.Add(_accountNameBox, _passwordBox);
            form.Add(_dependenciesBox);
            form.Add(_errorText);

            if (existing != null)
            {
                _nameBox.Text = existing.Name;
                _exeBox.Text = existing.ExePath;
                _argsBox.Text = existing.Args ?? "";
                _workDirBox.Text = existing.WorkingDir ?? "";
                _descriptionBox.Text = existing.Description ?? "";
                _environmentBox.Text = NormalizeLineEndings(EnvironmentVariables.Format(existing.Environment));
                _secretEnvironmentBox.Text = NormalizeLineEndings(
                    ServiceText.SecretEnvironment(existing.SecretEnvironment, readable: true, showValues: false));
                _autoRestartCheck.IsChecked = existing.AutoRestart;
                _restartPolicyPanel.Visibility = existing.AutoRestart ? Visibility.Visible : Visibility.Collapsed;
                _restartDelayBox.Value = existing.RestartDelaySeconds;
                _maxRestartsBox.Value = existing.MaxRestarts;
                _restartResetBox.Value = existing.RestartResetMinutes;
                _restartBackoffCheck.IsChecked = existing.RestartBackoff;
                _stopModeBox.SelectedIndex = (int)existing.StopMode;
                _stopTimeoutBox.Value = existing.StopTimeoutSeconds;
                _stopCommandBox.Text = existing.StopCommand ?? "";
                _healthTypeBox.SelectedIndex = (int)existing.HealthCheckType;
                _healthTargetBox.Text = existing.HealthCheckTarget ?? "";
                _healthIntervalBox.Value = existing.HealthCheckIntervalSeconds;
                _healthTimeoutBox.Value = existing.HealthCheckTimeoutSeconds;
                _healthRetriesBox.Value = existing.HealthCheckFailureThreshold;
                _healthStartPeriodBox.Value = existing.HealthCheckStartPeriodSeconds;
                _memoryLimitBox.Value = existing.MemoryLimitMB;
                _processMemoryLimitBox.Value = existing.ProcessMemoryLimitMB;
                _cpuLimitBox.Value = existing.CpuLimitPercent;
                _maxProcessesBox.Value = existing.MaxProcesses;
                _accountBox.SelectedIndex = (int)existing.AccountType;
                _accountNameBox.Text = existing.AccountType == ServiceAccountType.User ? existing.AccountName ?? "" : "";
                _startupBox.SelectedIndex = ServiceStartupModes.ToIndex(existing.StartupType, existing.DelayedAutoStart);
                _dependenciesBox.Text = ServiceDependencies.Format(existing.Dependencies);
            }
            else
            {
                _startupBox.SelectedIndex = 0;
                _stopModeBox.SelectedIndex = 0;
                _accountBox.SelectedIndex = 0;
                _healthTypeBox.SelectedIndex = 0;
            }
            UpdateStopControls();
            UpdateAccountControls();
            UpdateHealthControls();

            // Wrap in ScrollViewer but HIDE scrollbars (Clean UI)
            View = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, // Hidden but scrollable
                Content = form.Grid
            };
        }

        // WinUI TextBox 使用 \r 作为换行符，统一为 \r\n 便于存储与显示
        private static string NormalizeLineEndings(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
        }
    }
}
