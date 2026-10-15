using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Services.Core.Models
{
    public class Service : INotifyPropertyChanged
    {
        private string _status = "未知";
        private int _pid;
        private string _name = string.Empty;
        private string _exePath = string.Empty;
        private string? _args;
        private string? _workingDir;
        private string? _description;
        private bool _autoRestart;
        private ServiceStartupType _startupType = ServiceStartupType.Auto;

        public string Id { get; set; } = string.Empty;

        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        public string ExePath
        {
            get => _exePath;
            set => SetField(ref _exePath, value);
        }

        public string? Args
        {
            get => _args;
            set => SetField(ref _args, value);
        }

        public string? WorkingDir
        {
            get => _workingDir;
            set => SetField(ref _workingDir, value);
        }

        public string? Description
        {
            get => _description;
            set => SetField(ref _description, value);
        }

        public string Status
        {
            get => _status;
            set => SetField(ref _status, value);
        }

        public int Pid
        {
            get => _pid;
            set
            {
                if (SetField(ref _pid, value))
                {
                    OnPropertyChanged(nameof(HasPid));
                }
            }
        }

        public bool HasPid => Pid > 0;

        public ServiceStartupType StartupType
        {
            get => _startupType;
            set
            {
                if (SetField(ref _startupType, value))
                {
                    OnPropertyChanged(nameof(AutoStart));
                }
            }
        }

        public bool AutoStart => StartupType == ServiceStartupType.Auto;

        public bool AutoRestart
        {
            get => _autoRestart;
            set => SetField(ref _autoRestart, value);
        }

        public int RestartDelaySeconds { get; set; } = RestartPolicyDefaults.DelaySeconds;
        public int MaxRestarts { get; set; } = RestartPolicyDefaults.MaxRestarts;
        public int RestartResetMinutes { get; set; } = RestartPolicyDefaults.ResetMinutes;
        public bool RestartBackoff { get; set; } = RestartPolicyDefaults.Backoff;

        public ServiceStopMode StopMode { get; set; } = ServiceStopMode.Kill;
        public string? StopCommand { get; set; }
        public int StopTimeoutSeconds { get; set; } = StopPolicyDefaults.TimeoutSeconds;

        public ServiceAccountType AccountType { get; set; } = ServiceAccountType.LocalSystem;
        /// <summary>SCM 中记录的运行账户名（ObjectName），如 LocalSystem、NT SERVICE\&lt;服务ID&gt;、.\user</summary>
        public string? AccountName { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// 从另一个实例复制所有可变字段（用于刷新列表时保留同一对象引用）
        /// </summary>
        public void CopyFrom(Service other)
        {
            Name = other.Name;
            ExePath = other.ExePath;
            Args = other.Args;
            WorkingDir = other.WorkingDir;
            Description = other.Description;
            Status = other.Status;
            Pid = other.Pid;
            StartupType = other.StartupType;
            AutoRestart = other.AutoRestart;
            RestartDelaySeconds = other.RestartDelaySeconds;
            MaxRestarts = other.MaxRestarts;
            RestartResetMinutes = other.RestartResetMinutes;
            RestartBackoff = other.RestartBackoff;
            StopMode = other.StopMode;
            StopCommand = other.StopCommand;
            StopTimeoutSeconds = other.StopTimeoutSeconds;
            AccountType = other.AccountType;
            AccountName = other.AccountName;
            CreatedAt = other.CreatedAt;
            UpdatedAt = other.UpdatedAt;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    public class ServiceConfig
    {
        public string Name { get; set; } = string.Empty;
        public string ExePath { get; set; } = string.Empty;
        public string? Args { get; set; }
        public string? WorkingDir { get; set; }
        public string? Description { get; set; }
        public bool AutoRestart { get; set; }
        public int RestartDelaySeconds { get; set; } = RestartPolicyDefaults.DelaySeconds;
        public int MaxRestarts { get; set; } = RestartPolicyDefaults.MaxRestarts;
        public int RestartResetMinutes { get; set; } = RestartPolicyDefaults.ResetMinutes;
        public bool RestartBackoff { get; set; } = RestartPolicyDefaults.Backoff;
        public ServiceStopMode StopMode { get; set; } = ServiceStopMode.Kill;
        public string? StopCommand { get; set; }
        public int StopTimeoutSeconds { get; set; } = StopPolicyDefaults.TimeoutSeconds;
        public ServiceAccountType AccountType { get; set; } = ServiceAccountType.LocalSystem;
        /// <summary>指定用户时的账户名（如 .\svcuser、DOMAIN\svcuser）；其他类型忽略。密码不保存在配置中。</summary>
        public string? AccountName { get; set; }
        public ServiceStartupType StartupType { get; set; } = ServiceStartupType.Auto;

        /// <summary>
        /// 以已有服务的当前配置为基础创建配置（用于部分字段更新）
        /// </summary>
        public static ServiceConfig FromService(Service service)
        {
            return new ServiceConfig
            {
                Name = service.Name,
                ExePath = service.ExePath,
                Args = service.Args,
                WorkingDir = service.WorkingDir,
                Description = service.Description,
                AutoRestart = service.AutoRestart,
                RestartDelaySeconds = service.RestartDelaySeconds,
                MaxRestarts = service.MaxRestarts,
                RestartResetMinutes = service.RestartResetMinutes,
                RestartBackoff = service.RestartBackoff,
                StopMode = service.StopMode,
                StopCommand = service.StopCommand,
                StopTimeoutSeconds = service.StopTimeoutSeconds,
                AccountType = service.AccountType,
                AccountName = service.AccountName,
                StartupType = service.StartupType
            };
        }
    }

    /// <summary>
    /// 失败自动重启策略的默认值（未配置时与旧版本行为一致）
    /// </summary>
    public static class RestartPolicyDefaults
    {
        /// <summary>首次重启前的等待时间（秒）</summary>
        public const int DelaySeconds = 5;
        /// <summary>计数窗口内允许的最大重启次数，0 表示不限制</summary>
        public const int MaxRestarts = 5;
        /// <summary>距上次重启超过该时间（分钟）则重置重启计数</summary>
        public const int ResetMinutes = 10;
        /// <summary>是否启用指数退避（每次重启延迟翻倍，最多 16 倍）</summary>
        public const bool Backoff = true;
    }

    /// <summary>
    /// 停止服务时结束目标程序的方式
    /// </summary>
    public enum ServiceStopMode
    {
        /// <summary>直接结束进程树（默认，与旧版本一致）</summary>
        Kill = 0,
        /// <summary>向目标程序的控制台发送 Ctrl+C，超时后结束进程树</summary>
        CtrlC = 1,
        /// <summary>执行自定义停止命令（如 nginx -s quit），超时后结束进程树</summary>
        Command = 2
    }

    /// <summary>
    /// 服务运行账户类型
    /// </summary>
    public enum ServiceAccountType
    {
        /// <summary>LocalSystem（默认，权限最高，与旧版本一致）</summary>
        LocalSystem = 0,
        /// <summary>NT AUTHORITY\LocalService：低权限，以匿名身份访问网络</summary>
        LocalService = 1,
        /// <summary>NT AUTHORITY\NetworkService：低权限，以计算机账户访问网络</summary>
        NetworkService = 2,
        /// <summary>虚拟账户 NT SERVICE\&lt;服务ID&gt;：每个服务独立的低权限账户，无需密码</summary>
        Virtual = 3,
        /// <summary>指定的本地或域用户（需要密码）</summary>
        User = 4
    }

    public static class StopPolicyDefaults
    {
        /// <summary>优雅停止的最长等待时间（秒），超时后强制结束进程树</summary>
        public const int TimeoutSeconds = 30;
        public const int MaxTimeoutSeconds = 3600;
    }

    public enum ServiceStartupType
    {
        Auto = 2,
        Manual = 3,
        Disabled = 4
    }
}
