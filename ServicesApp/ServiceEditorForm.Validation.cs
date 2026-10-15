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
    /// 服务编辑表单：输入校验与转换为 ServiceConfig
    /// </summary>
    public sealed partial class ServiceEditorForm
    {
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
            else if (!TryParseEnvironment(out var environment, out var environmentError))
            {
                error = environmentError;
            }
            else if (!TryParseSecretEnvironment(out var secrets, out var secretError))
            {
                error = secretError;
            }
            else if (environment.Keys.FirstOrDefault(secrets.ContainsKey) is { } overlap)
            {
                error = $"变量 '{overlap}' 不能同时作为普通与敏感环境变量。";
            }
            else if (!TryParseDependencies(out _, out var dependenciesError))
            {
                error = dependenciesError;
            }
            else if (Services.Core.Health.HealthCheckSpec.Validate(SelectedHealthType, _healthTargetBox.Text) is { } healthError)
            {
                error = healthError + "。";
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

        /// <summary>
        /// 解析敏感变量；值仍为遮蔽值的已有变量沿用原值
        /// </summary>
        private bool TryParseSecretEnvironment(out Dictionary<string, string> secrets, out string? error)
        {
            try
            {
                var edited = EnvironmentVariables.ParseText(_secretEnvironmentBox.Text);
                secrets = EnvironmentVariables.RestoreMasked(edited,
                    _existing?.SecretEnvironment ?? EnvironmentVariables.Clone(null), ServiceText.SecretMask);
                error = null;
                return true;
            }
            catch (ArgumentException ex)
            {
                secrets = EnvironmentVariables.Clone(null);
                error = "敏感" + ex.Message;
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
            TryParseSecretEnvironment(out var secrets, out _);
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
                HealthCheckType = SelectedHealthType,
                HealthCheckTarget = SelectedHealthType == HealthCheckType.None ? "" : _healthTargetBox.Text.Trim(),
                HealthCheckIntervalSeconds = ReadNumber(_healthIntervalBox, HealthCheckDefaults.IntervalSeconds),
                HealthCheckTimeoutSeconds = ReadNumber(_healthTimeoutBox, HealthCheckDefaults.TimeoutSeconds),
                HealthCheckFailureThreshold = ReadNumber(_healthRetriesBox, HealthCheckDefaults.FailureThreshold),
                HealthCheckStartPeriodSeconds = ReadNumber(_healthStartPeriodBox, HealthCheckDefaults.StartPeriodSeconds),
                MemoryLimitMB = ReadNumber(_memoryLimitBox, 0),
                ProcessMemoryLimitMB = ReadNumber(_processMemoryLimitBox, 0),
                CpuLimitPercent = ReadNumber(_cpuLimitBox, 0),
                MaxProcesses = ReadNumber(_maxProcessesBox, 0),
                AccountType = SelectedAccountType,
                AccountName = SelectedAccountType == ServiceAccountType.User ? _accountNameBox.Text.Trim() : null,
                Environment = environment,
                SecretEnvironment = secrets,
                StartupType = startupType,
                DelayedAutoStart = delayedAutoStart,
                Dependencies = dependencies
            };
        }
    }
}
