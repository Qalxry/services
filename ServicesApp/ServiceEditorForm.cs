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
    public sealed class ServiceEditorForm
    {
        private readonly IntPtr _ownerHwnd;
        private readonly TextBox _nameBox;
        private readonly ComboBox _startupBox;
        private readonly TextBox _exeBox;
        private readonly TextBox _argsBox;
        private readonly TextBox _workDirBox;
        private readonly TextBox _descriptionBox;
        private readonly TextBox _environmentBox;
        private readonly CheckBox _autoRestartCheck;
        private readonly NumberBox _restartDelayBox;
        private readonly NumberBox _maxRestartsBox;
        private readonly NumberBox _restartResetBox;
        private readonly CheckBox _restartBackoffCheck;
        private readonly FrameworkElement _restartPolicyPanel;
        private readonly ComboBox _stopModeBox;
        private readonly NumberBox _stopTimeoutBox;
        private readonly TextBox _stopCommandBox;
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
            form.Add(_autoRestartCheck, null);
            form.Add(_restartPolicyPanel);
            form.Add(_stopModeBox, _stopTimeoutBox);
            form.Add(_stopCommandBox);
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
                _autoRestartCheck.IsChecked = existing.AutoRestart;
                _restartPolicyPanel.Visibility = existing.AutoRestart ? Visibility.Visible : Visibility.Collapsed;
                _restartDelayBox.Value = existing.RestartDelaySeconds;
                _maxRestartsBox.Value = existing.MaxRestarts;
                _restartResetBox.Value = existing.RestartResetMinutes;
                _restartBackoffCheck.IsChecked = existing.RestartBackoff;
                _stopModeBox.SelectedIndex = (int)existing.StopMode;
                _stopTimeoutBox.Value = existing.StopTimeoutSeconds;
                _stopCommandBox.Text = existing.StopCommand ?? "";
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
            }
            UpdateStopControls();
            UpdateAccountControls();

            // Wrap in ScrollViewer but HIDE scrollbars (Clean UI)
            View = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, // Hidden but scrollable
                Content = form.Grid
            };
        }

        /// <summary>
        /// 输入框 + 同行右侧的选择按钮（文件：OpenFile 图标，文件夹：FolderOpen 图标）
        /// </summary>
        private static Grid CreatePickerRow(TextBox box, string glyph, string tooltip, Action onPick)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var button = new Button
            {
                Content = new FontIcon { Glyph = glyph, FontSize = 16 },
                VerticalAlignment = VerticalAlignment.Bottom,
                MinHeight = 32
            };
            ToolTipService.SetToolTip(button, tooltip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
            button.Click += (s, e) => onPick();
            Grid.SetColumn(button, 1);

            row.Children.Add(box);
            row.Children.Add(button);
            return row;
        }

        private static Microsoft.UI.Xaml.Media.Brush? GetThemeBrush(string key)
        {
            return Application.Current.Resources.TryGetValue(key, out var value) ? value as Microsoft.UI.Xaml.Media.Brush : null;
        }

        private static NumberBox CreateNumberBox(string header, int min, int max, int value)
        {
            return new NumberBox
            {
                Header = header,
                Minimum = min,
                Maximum = max,
                Value = value,
                SmallChange = 1,
                LargeChange = 10,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten
            };
        }

        private static int ReadNumber(NumberBox box, int fallback)
        {
            if (double.IsNaN(box.Value)) return fallback;
            return (int)Math.Clamp(Math.Round(box.Value), box.Minimum, box.Maximum);
        }

        private void OnPickExe()
        {
            var pickedPath = Win32Helper.PickFile(_ownerHwnd, "选择可执行文件");
            if (pickedPath == null) return;

            _exeBox.Text = pickedPath;
            if (string.IsNullOrWhiteSpace(_workDirBox.Text))
            {
                _workDirBox.Text = System.IO.Path.GetDirectoryName(pickedPath) ?? "";
            }
        }

        private void OnPickWorkDir()
        {
            var pickedPath = Win32Helper.PickFolder(_ownerHwnd, "选择工作目录");
            if (pickedPath != null) _workDirBox.Text = pickedPath;
        }

        /// <summary>
        /// 校验表单，失败时在表单内显示错误信息并返回 false
        /// </summary>
        private ServiceStopMode SelectedStopMode =>
            _stopModeBox.SelectedIndex >= 0 ? (ServiceStopMode)_stopModeBox.SelectedIndex : ServiceStopMode.Kill;

        /// <summary>
        /// 直接结束时无需超时；仅“执行停止命令”需要填写命令
        /// </summary>
        private void UpdateStopControls()
        {
            var mode = SelectedStopMode;
            _stopTimeoutBox.Visibility = mode == ServiceStopMode.Kill ? Visibility.Collapsed : Visibility.Visible;
            _stopCommandBox.Visibility = mode == ServiceStopMode.Command ? Visibility.Visible : Visibility.Collapsed;
        }

        private ServiceAccountType SelectedAccountType =>
            _accountBox.SelectedIndex >= 0 ? (ServiceAccountType)_accountBox.SelectedIndex : ServiceAccountType.LocalSystem;

        /// <summary>
        /// 新建服务、从其他账户类型切换为指定用户或更换用户名时必须提供密码；同一用户可留空保持原密码
        /// </summary>
        private bool RequiresPassword =>
            _existing == null ||
            _existing.AccountType != ServiceAccountType.User ||
            !string.Equals(_existing.AccountName, _accountNameBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 指定用户时填写的密码；留空或非指定用户时为 null（表示不修改密码）
        /// </summary>
        public string? Password =>
            SelectedAccountType == ServiceAccountType.User && !string.IsNullOrEmpty(_passwordBox.Password) ? _passwordBox.Password : null;

        private void UpdateAccountControls()
        {
            var isUser = SelectedAccountType == ServiceAccountType.User;
            _accountNameBox.Visibility = isUser ? Visibility.Visible : Visibility.Collapsed;
            _passwordBox.Visibility = isUser ? Visibility.Visible : Visibility.Collapsed;
            _accountHint.Visibility = SelectedAccountType == ServiceAccountType.LocalSystem ? Visibility.Collapsed : Visibility.Visible;
        }

        public bool Validate()
        {
            string? error = null;
            if (string.IsNullOrWhiteSpace(_nameBox.Text) || string.IsNullOrWhiteSpace(_exeBox.Text))
            {
                error = "服务名称和可执行文件路径为必填项。";
            }
            else if (_nameBox.Text.Trim().Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != ' '))
            {
                error = "服务名称只能包含字母、数字、空格、下划线和短横线。";
            }
            else if (!System.IO.File.Exists(_exeBox.Text.Trim()))
            {
                error = "可执行文件不存在，请检查路径。";
            }
            else if (!TryParseEnvironment(out _, out var environmentError))
            {
                error = environmentError;
            }
            else if (!TryParseDependencies(out _, out var dependenciesError))
            {
                error = dependenciesError;
            }
            else if (SelectedStopMode == ServiceStopMode.Command && string.IsNullOrWhiteSpace(_stopCommandBox.Text))
            {
                error = "停止方式为“执行停止命令”时必须填写停止命令。";
            }
            else if (SelectedAccountType == ServiceAccountType.User && string.IsNullOrWhiteSpace(_accountNameBox.Text))
            {
                error = "运行账户为“指定用户”时必须填写账户名。";
            }
            else if (SelectedAccountType == ServiceAccountType.User && RequiresPassword && string.IsNullOrEmpty(_passwordBox.Password))
            {
                error = "请填写该账户的密码。";
            }

            _errorText.Text = error ?? "";
            _errorText.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
            if (error != null) _errorText.StartBringIntoView();
            return error == null;
        }

        private bool TryParseEnvironment(out Dictionary<string, string> environment, out string? error)
        {
            try
            {
                environment = EnvironmentVariables.ParseText(_environmentBox.Text);
                error = null;
                return true;
            }
            catch (ArgumentException ex)
            {
                environment = EnvironmentVariables.Clone(null);
                error = ex.Message;
                return false;
            }
        }

        private bool TryParseDependencies(out List<string> dependencies, out string? error)
        {
            dependencies = ServiceDependencies.ParseText(_dependenciesBox.Text);
            try
            {
                ServiceDependencies.Validate(dependencies, _existing?.Id);
                error = null;
                return true;
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public ServiceConfig ToConfig()
        {
            TryParseEnvironment(out var environment, out _);
            TryParseDependencies(out var dependencies, out _);
            var (startupType, delayedAutoStart) = ServiceStartupModes.FromIndex(_startupBox.SelectedIndex);
            return new ServiceConfig
            {
                Name = _nameBox.Text.Trim(),
                ExePath = _exeBox.Text.Trim(),
                Args = NormalizeLineEndings(_argsBox.Text).Trim(),
                WorkingDir = _workDirBox.Text.Trim(),
                Description = NormalizeLineEndings(_descriptionBox.Text).Trim(),
                AutoRestart = _autoRestartCheck.IsChecked ?? false,
                RestartDelaySeconds = ReadNumber(_restartDelayBox, RestartPolicyDefaults.DelaySeconds),
                MaxRestarts = ReadNumber(_maxRestartsBox, RestartPolicyDefaults.MaxRestarts),
                RestartResetMinutes = ReadNumber(_restartResetBox, RestartPolicyDefaults.ResetMinutes),
                RestartBackoff = _restartBackoffCheck.IsChecked ?? RestartPolicyDefaults.Backoff,
                StopMode = SelectedStopMode,
                StopCommand = _stopCommandBox.Text.Trim(),
                StopTimeoutSeconds = ReadNumber(_stopTimeoutBox, StopPolicyDefaults.TimeoutSeconds),
                AccountType = SelectedAccountType,
                AccountName = SelectedAccountType == ServiceAccountType.User ? _accountNameBox.Text.Trim() : null,
                Environment = environment,
                StartupType = startupType,
                DelayedAutoStart = delayedAutoStart,
                Dependencies = dependencies
            };
        }

        // WinUI TextBox 使用 \r 作为换行符，统一为 \r\n 便于存储与显示
        private static string NormalizeLineEndings(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
        }
    }
}
