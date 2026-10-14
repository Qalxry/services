using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Services.Core.Models;
using System;
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
        private readonly CheckBox _autoRestartCheck;
        private readonly TextBlock _errorText;

        public FrameworkElement View { get; }

        public ServiceEditorForm(IntPtr ownerHwnd, Service? existing = null)
        {
            _ownerHwnd = ownerHwnd;

            // Use Grid for compact layout (No redundancy)
            var grid = new Grid { RowSpacing = 12, ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Name | Startup Type
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _nameBox = new TextBox { Header = "服务名称 (仅字母数字)", PlaceholderText = "MyService" };
            Grid.SetColumn(_nameBox, 0); Grid.SetRow(_nameBox, 0);

            _startupBox = new ComboBox { Header = "启动类型", HorizontalAlignment = HorizontalAlignment.Stretch };
            _startupBox.Items.Add("开机自启");
            _startupBox.Items.Add("手动启动");
            _startupBox.Items.Add("已禁用");
            Grid.SetColumn(_startupBox, 1); Grid.SetRow(_startupBox, 0);

            // Exe Path | File picker
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _exeBox = new TextBox { Header = "可执行文件路径", PlaceholderText = "C:\\Path\\To\\App.exe" };
            var exeRow = CreatePickerRow(_exeBox, "选择可执行文件", OnPickExe);
            Grid.SetColumn(exeRow, 0); Grid.SetRow(exeRow, 1);
            Grid.SetColumnSpan(exeRow, 2);

            // WorkDir | Folder picker
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _workDirBox = new TextBox { Header = "工作目录 (可选)", PlaceholderText = "默认为程序所在目录" };
            var workDirRow = CreatePickerRow(_workDirBox, "选择工作目录", OnPickWorkDir);
            Grid.SetColumn(workDirRow, 0); Grid.SetRow(workDirRow, 2);
            Grid.SetColumnSpan(workDirRow, 2);

            // Args (multi-line)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
            Grid.SetColumn(_argsBox, 0); Grid.SetRow(_argsBox, 3);
            Grid.SetColumnSpan(_argsBox, 2);

            // Description (multi-line)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
            Grid.SetColumn(_descriptionBox, 0); Grid.SetRow(_descriptionBox, 4);
            Grid.SetColumnSpan(_descriptionBox, 2);

            // Auto Restart
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _autoRestartCheck = new CheckBox { Content = "失败自动重启", IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_autoRestartCheck, 0); Grid.SetRow(_autoRestartCheck, 5);

            // Validation message
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _errorText = new TextBlock
            {
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };
            Grid.SetColumn(_errorText, 0); Grid.SetRow(_errorText, 6);
            Grid.SetColumnSpan(_errorText, 2);

            // Add children
            grid.Children.Add(_nameBox);
            grid.Children.Add(_startupBox);
            grid.Children.Add(exeRow);
            grid.Children.Add(workDirRow);
            grid.Children.Add(_argsBox);
            grid.Children.Add(_descriptionBox);
            grid.Children.Add(_autoRestartCheck);
            grid.Children.Add(_errorText);

            if (existing != null)
            {
                _nameBox.Text = existing.Name;
                _exeBox.Text = existing.ExePath;
                _argsBox.Text = existing.Args ?? "";
                _workDirBox.Text = existing.WorkingDir ?? "";
                _descriptionBox.Text = existing.Description ?? "";
                _autoRestartCheck.IsChecked = existing.AutoRestart;
                _startupBox.SelectedIndex = (int)existing.StartupType - (int)ServiceStartupType.Auto;
            }
            else
            {
                _startupBox.SelectedIndex = 0;
            }

            // Wrap in ScrollViewer but HIDE scrollbars (Clean UI)
            View = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, // Hidden but scrollable
                Content = grid
            };
        }

        /// <summary>
        /// 输入框 + 同行右侧的文件夹图标按钮
        /// </summary>
        private static Grid CreatePickerRow(TextBox box, string tooltip, Action onPick)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var button = new Button
            {
                Content = new FontIcon { Glyph = "\uE8B7", FontSize = 16 },
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

            _errorText.Text = error ?? "";
            _errorText.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
            if (error != null) _errorText.StartBringIntoView();
            return error == null;
        }

        public ServiceConfig ToConfig()
        {
            return new ServiceConfig
            {
                Name = _nameBox.Text.Trim(),
                ExePath = _exeBox.Text.Trim(),
                Args = NormalizeLineEndings(_argsBox.Text).Trim(),
                WorkingDir = _workDirBox.Text.Trim(),
                Description = NormalizeLineEndings(_descriptionBox.Text).Trim(),
                AutoRestart = _autoRestartCheck.IsChecked ?? false,
                StartupType = (ServiceStartupType)(_startupBox.SelectedIndex + (int)ServiceStartupType.Auto)
            };
        }

        // WinUI TextBox 使用 \r 作为换行符，统一为 \r\n 便于存储与显示
        private static string NormalizeLineEndings(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
        }
    }
}
