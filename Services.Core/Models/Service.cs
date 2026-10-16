using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Services.Core.Helpers;

namespace Services.Core.Models
{
    public class Service : INotifyPropertyChanged
    {
        private ServiceRunState _state = ServiceRunState.Unknown;
        private int _pid;
        private string _name = string.Empty;
        private string _exePath = string.Empty;
        private string? _args;
        private string? _workingDir;
        private string? _description;
        private bool _autoRestart;
        private ServiceStartupType _startupType = ServiceStartupType.Auto;
        private bool _delayedAutoStart;

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

        /// <summary>运行状态（展示文本由 Presentation.ServiceText 生成）</summary>
        public ServiceRunState State
        {
            get => _state;
            set
            {
                if (SetField(ref _state, value))
                {
                    OnPropertyChanged(nameof(IsRunning));
                }
            }
        }

        public bool IsRunning => State == ServiceRunState.Running;

        private TimeSpan? _uptime;

        /// <summary>运行时长（运行中且有属于当前服务进程的运行状态时有值），刷新状态时更新</summary>
        public TimeSpan? Uptime
        {
            get => _uptime;
            set => SetField(ref _uptime, value);
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

        /// <summary>延迟自动启动（开机后稍晚启动）；仅在启动类型为 Auto 时有意义</summary>
        public bool DelayedAutoStart
        {
            get => _delayedAutoStart;
            set => SetField(ref _delayedAutoStart, value);
        }

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

        /// <summary>启动前命令：每次启动目标程序前执行，失败（非 0、超时）则不启动；空表示不执行</summary>
        public string? PreStartCommand { get; set; }
        /// <summary>停止后命令：目标程序每次退出后执行，失败只记录；空表示不执行</summary>
        public string? PostStopCommand { get; set; }
        /// <summary>钩子命令的超时（秒）</summary>
        public int HookTimeoutSeconds { get; set; } = HookDefaults.TimeoutSeconds;

        /// <summary>健康检查方式；None 表示不检查（其余字段见 <see cref="HealthCheckDefaults"/>）</summary>
        public HealthCheckType HealthCheckType { get; set; } = HealthCheckType.None;
        /// <summary>检查目标：http 为 URL，tcp 为 host:port，command 为命令行</summary>
        public string? HealthCheckTarget { get; set; }
        public int HealthCheckIntervalSeconds { get; set; } = HealthCheckDefaults.IntervalSeconds;
        public int HealthCheckTimeoutSeconds { get; set; } = HealthCheckDefaults.TimeoutSeconds;
        public int HealthCheckFailureThreshold { get; set; } = HealthCheckDefaults.FailureThreshold;
        public int HealthCheckStartPeriodSeconds { get; set; } = HealthCheckDefaults.StartPeriodSeconds;

        /// <summary>目标程序进程树的内存上限（MB），0 表示不限制</summary>
        public int MemoryLimitMB { get; set; }
        /// <summary>单个进程的内存上限（MB），0 表示不限制</summary>
        public int ProcessMemoryLimitMB { get; set; }
        /// <summary>CPU 占用上限（占全部 CPU 的百分比，硬上限），0 表示不限制</summary>
        public int CpuLimitPercent { get; set; }
        /// <summary>同时运行的进程数上限（含目标程序自身），0 表示不限制</summary>
        public int MaxProcesses { get; set; }

        public ServiceAccountType AccountType { get; set; } = ServiceAccountType.LocalSystem;
        /// <summary>SCM 中记录的运行账户名（ObjectName），如 LocalSystem、NT SERVICE\&lt;服务ID&gt;、.\user</summary>
        public string? AccountName { get; set; }

        /// <summary>服务级环境变量（启动目标程序时叠加在继承的环境之上，值可用 %VAR% 引用现有变量）；变量名不区分大小写</summary>
        public IReadOnlyDictionary<string, string> Environment { get; set; } = EnvironmentVariables.Clone(null);

        /// <summary>
        /// 敏感环境变量（如密钥）：值保存在只有 SYSTEM、管理员与服务运行账户可读的注册表子键中，界面默认遮蔽。
        /// 无权读取时（非管理员）只有变量名，值为空字符串，且 <see cref="SecretsReadable"/> 为 false。
        /// </summary>
        public IReadOnlyDictionary<string, string> SecretEnvironment { get; set; } = EnvironmentVariables.Clone(null);

        /// <summary>受保护的配置（敏感环境变量、服务级 Webhook）是否已读取（无权读取时为 false）</summary>
        public bool SecretsReadable { get; set; } = true;

        /// <summary>服务级失败通知 Webhook 地址；为空表示沿用全局设置</summary>
        public string? WebhookUrl { get; set; }
        /// <summary>服务级 Webhook 订阅的事件（逗号分隔）；为空表示默认事件</summary>
        public string? WebhookEvents { get; set; }

        /// <summary>本服务启动前需先启动的服务名（Windows 服务名，可以是任意服务）；名称不区分大小写</summary>
        public IReadOnlyList<string> Dependencies { get; set; } = ServiceDependencies.Clone(null);

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
            State = other.State;
            Uptime = other.Uptime;
            Pid = other.Pid;
            StartupType = other.StartupType;
            DelayedAutoStart = other.DelayedAutoStart;
            AutoRestart = other.AutoRestart;
            RestartDelaySeconds = other.RestartDelaySeconds;
            MaxRestarts = other.MaxRestarts;
            RestartResetMinutes = other.RestartResetMinutes;
            RestartBackoff = other.RestartBackoff;
            StopMode = other.StopMode;
            StopCommand = other.StopCommand;
            StopTimeoutSeconds = other.StopTimeoutSeconds;
            PreStartCommand = other.PreStartCommand;
            PostStopCommand = other.PostStopCommand;
            HookTimeoutSeconds = other.HookTimeoutSeconds;
            HealthCheckType = other.HealthCheckType;
            HealthCheckTarget = other.HealthCheckTarget;
            HealthCheckIntervalSeconds = other.HealthCheckIntervalSeconds;
            HealthCheckTimeoutSeconds = other.HealthCheckTimeoutSeconds;
            HealthCheckFailureThreshold = other.HealthCheckFailureThreshold;
            HealthCheckStartPeriodSeconds = other.HealthCheckStartPeriodSeconds;
            MemoryLimitMB = other.MemoryLimitMB;
            ProcessMemoryLimitMB = other.ProcessMemoryLimitMB;
            CpuLimitPercent = other.CpuLimitPercent;
            MaxProcesses = other.MaxProcesses;
            AccountType = other.AccountType;
            AccountName = other.AccountName;
            Environment = EnvironmentVariables.Clone(other.Environment);
            SecretEnvironment = EnvironmentVariables.Clone(other.SecretEnvironment);
            SecretsReadable = other.SecretsReadable;
            WebhookUrl = other.WebhookUrl;
            WebhookEvents = other.WebhookEvents;
            Dependencies = ServiceDependencies.Clone(other.Dependencies);
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
        public string? PreStartCommand { get; set; }
        public string? PostStopCommand { get; set; }
        public int HookTimeoutSeconds { get; set; } = HookDefaults.TimeoutSeconds;
        public HealthCheckType HealthCheckType { get; set; } = HealthCheckType.None;
        public string? HealthCheckTarget { get; set; }
        public int HealthCheckIntervalSeconds { get; set; } = HealthCheckDefaults.IntervalSeconds;
        public int HealthCheckTimeoutSeconds { get; set; } = HealthCheckDefaults.TimeoutSeconds;
        public int HealthCheckFailureThreshold { get; set; } = HealthCheckDefaults.FailureThreshold;
        public int HealthCheckStartPeriodSeconds { get; set; } = HealthCheckDefaults.StartPeriodSeconds;
        /// <summary>资源限制，0 表示不限制（取值范围见 <see cref="ResourceLimitRanges"/>）</summary>
        public int MemoryLimitMB { get; set; }
        public int ProcessMemoryLimitMB { get; set; }
        public int CpuLimitPercent { get; set; }
        public int MaxProcesses { get; set; }
        public ServiceAccountType AccountType { get; set; } = ServiceAccountType.LocalSystem;
        /// <summary>指定用户时的账户名（如 .\svcuser、DOMAIN\svcuser）；其他类型忽略。密码不保存在配置中。</summary>
        public string? AccountName { get; set; }
        /// <summary>服务级环境变量；变量名不区分大小写，空表示不额外设置</summary>
        public IReadOnlyDictionary<string, string> Environment { get; set; } = EnvironmentVariables.Clone(null);
        /// <summary>敏感环境变量（受保护存储，界面遮蔽）；变量名不能与 Environment 重复</summary>
        public IReadOnlyDictionary<string, string> SecretEnvironment { get; set; } = EnvironmentVariables.Clone(null);
        /// <summary>服务级 Webhook 地址（受保护存储）；为空表示沿用全局设置</summary>
        public string? WebhookUrl { get; set; }
        /// <summary>服务级 Webhook 订阅的事件（逗号分隔）；为空表示默认事件</summary>
        public string? WebhookEvents { get; set; }
        public ServiceStartupType StartupType { get; set; } = ServiceStartupType.Auto;
        /// <summary>延迟自动启动；仅在启动类型为 Auto 时有效，其他类型保存时按 false 处理</summary>
        public bool DelayedAutoStart { get; set; }
        /// <summary>本服务启动前需先启动的服务名；保存前会规范化（去空白、去重）。空表示无依赖</summary>
        public IReadOnlyList<string> Dependencies { get; set; } = ServiceDependencies.Clone(null);

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
                PreStartCommand = service.PreStartCommand,
                PostStopCommand = service.PostStopCommand,
                HookTimeoutSeconds = service.HookTimeoutSeconds,
                HealthCheckType = service.HealthCheckType,
                HealthCheckTarget = service.HealthCheckTarget,
                HealthCheckIntervalSeconds = service.HealthCheckIntervalSeconds,
                HealthCheckTimeoutSeconds = service.HealthCheckTimeoutSeconds,
                HealthCheckFailureThreshold = service.HealthCheckFailureThreshold,
                HealthCheckStartPeriodSeconds = service.HealthCheckStartPeriodSeconds,
                MemoryLimitMB = service.MemoryLimitMB,
                ProcessMemoryLimitMB = service.ProcessMemoryLimitMB,
                CpuLimitPercent = service.CpuLimitPercent,
                MaxProcesses = service.MaxProcesses,
                AccountType = service.AccountType,
                AccountName = service.AccountName,
                Environment = EnvironmentVariables.Clone(service.Environment),
                SecretEnvironment = EnvironmentVariables.Clone(service.SecretEnvironment),
                WebhookUrl = service.WebhookUrl,
                WebhookEvents = service.WebhookEvents,
                StartupType = service.StartupType,
                DelayedAutoStart = service.DelayedAutoStart,
                Dependencies = ServiceDependencies.Clone(service.Dependencies)
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

    public enum HealthCheckType
    {
        None = 0,
        /// <summary>HTTP GET，2xx / 3xx 为健康</summary>
        Http = 1,
        /// <summary>TCP 端口可连接为健康</summary>
        Tcp = 2,
        /// <summary>命令退出码为 0 为健康</summary>
        Command = 3
    }

    /// <summary>
    /// 钩子命令（启动前 / 停止后）的超时默认值与上限
    /// </summary>
    public static class HookDefaults
    {
        public const int TimeoutSeconds = 60;
        public const int MaxTimeoutSeconds = 3600;
    }

    /// <summary>
    /// 健康检查的默认值与取值范围
    /// </summary>
    public static class HealthCheckDefaults
    {
        public const int IntervalSeconds = 30;
        public const int TimeoutSeconds = 5;
        /// <summary>连续失败多少次判定为不健康</summary>
        public const int FailureThreshold = 3;
        /// <summary>目标程序启动后的宽限期：期间的失败不计数</summary>
        public const int StartPeriodSeconds = 30;

        public const int MaxIntervalSeconds = 3600;
        public const int MaxTimeoutSeconds = 300;
        public const int MaxFailureThreshold = 100;
        public const int MaxStartPeriodSeconds = 3600;
    }

    /// <summary>
    /// 资源限制的取值范围（0 均表示不限制）
    /// </summary>
    public static class ResourceLimitRanges
    {
        public const int MaxMemoryMB = 1024 * 1024;
        public const int MaxCpuPercent = 100;
        public const int MaxProcesses = 10000;
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
