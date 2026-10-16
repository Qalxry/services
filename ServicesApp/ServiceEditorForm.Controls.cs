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
    /// 服务编辑表单：控件辅助方法与联动（停止方式、账户、健康检查）
    /// </summary>
    public sealed partial class ServiceEditorForm
    {
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

        private HealthCheckType SelectedHealthType =>
            _healthTypeBox.SelectedIndex > 0 ? (HealthCheckType)_healthTypeBox.SelectedIndex : HealthCheckType.None;

        private void UpdateHealthControls()
        {
            var visibility = SelectedHealthType == HealthCheckType.None ? Visibility.Collapsed : Visibility.Visible;
            foreach (var control in new FrameworkElement[] { _healthTargetBox, _healthIntervalBox, _healthTimeoutBox, _healthRetriesBox, _healthStartPeriodBox })
                control.Visibility = visibility;
            _healthTargetBox.PlaceholderText = SelectedHealthType switch
            {
                HealthCheckType.Tcp => "127.0.0.1:8080",
                HealthCheckType.Command => "curl -f http://127.0.0.1:8080/health",
                _ => "http://127.0.0.1:8080/health"
            };
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
    }
}
