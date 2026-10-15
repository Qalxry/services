namespace Services.Core.Storage
{
    /// <summary>
    /// 注册表路径（HKLM 下）
    /// </summary>
    public static class RegistryPaths
    {
        public const string Services = @"SYSTEM\CurrentControlSet\Services";

        /// <summary>本工具的全局设置与服务索引</summary>
        public const string Product = @"SOFTWARE\WindowsServiceManager";

        /// <summary>由本工具管理的服务索引：每个服务一个子键（键名为服务 ID）</summary>
        public const string ManagedServices = Product + @"\ManagedServices";

        public static string Service(string serviceId) => $@"{Services}\{serviceId}";

        /// <summary>服务配置（由本工具写入、包装器读取）</summary>
        public static string Parameters(string serviceId) => $@"{Services}\{serviceId}\{ParametersSubKey}";

        public const string ParametersSubKey = "Parameters";

        /// <summary>敏感环境变量的值（Parameters 的子键，ACL 只允许 SYSTEM、管理员与服务运行账户读取）</summary>
        public const string SecretsSubKey = "Secrets";

        public static string Secrets(string serviceId) => $@"{Parameters(serviceId)}\{SecretsSubKey}";

        /// <summary>通知设置（Webhook 地址可能含令牌，ACL 同 Secrets）</summary>
        public const string NotificationsSubKey = "Notifications";

        public static string ServiceNotifications(string serviceId) => $@"{Parameters(serviceId)}\{NotificationsSubKey}";

        /// <summary>全局通知设置：只允许 SYSTEM、管理员完全控制，所有服务进程（SERVICE 组）读取</summary>
        public const string GlobalNotifications = Product + @"\" + NotificationsSubKey;
    }

    /// <summary>
    /// Services\&lt;ID&gt;\Parameters 下的值名
    /// </summary>
    public static class ParameterNames
    {
        public const string ExePath = "ExePath";
        public const string Args = "Args";
        public const string WorkingDir = "WorkingDir";
        public const string DisplayName = "DisplayName";
        public const string Description = "Description";
        public const string AutoRestart = "AutoRestart";
        public const string RestartDelaySeconds = "RestartDelaySeconds";
        public const string MaxRestarts = "MaxRestarts";
        public const string RestartResetMinutes = "RestartResetMinutes";
        public const string RestartBackoff = "RestartBackoff";
        public const string StopMode = "StopMode";
        public const string StopCommand = "StopCommand";
        public const string StopTimeoutSeconds = "StopTimeoutSeconds";
        public const string HealthCheckType = "HealthCheckType";
        public const string HealthCheckTarget = "HealthCheckTarget";
        public const string HealthCheckIntervalSeconds = "HealthCheckIntervalSeconds";
        public const string HealthCheckTimeoutSeconds = "HealthCheckTimeoutSeconds";
        public const string HealthCheckFailureThreshold = "HealthCheckFailureThreshold";
        public const string HealthCheckStartPeriodSeconds = "HealthCheckStartPeriodSeconds";
        public const string MemoryLimitMB = "MemoryLimitMB";
        public const string ProcessMemoryLimitMB = "ProcessMemoryLimitMB";
        public const string CpuLimitPercent = "CpuLimitPercent";
        public const string MaxProcesses = "MaxProcesses";
        /// <summary>REG_MULTI_SZ，每项 KEY=VALUE</summary>
        public const string Environment = "Environment";
        /// <summary>REG_MULTI_SZ，敏感环境变量的变量名（值在 Secrets 子键中，每个变量一个 REG_SZ 值）</summary>
        public const string SecretEnvironmentNames = "SecretEnvironmentNames";
        /// <summary>ISO 8601</summary>
        public const string CreatedAt = "CreatedAt";
        public const string ManagedBy = "ManagedBy";

        public const string ManagedByValue = "WindowsServiceManager";
    }

    /// <summary>
    /// Services\&lt;ID&gt; 下由 SCM 维护的值名
    /// </summary>
    public static class ScmValueNames
    {
        public const string ImagePath = "ImagePath";
        public const string ObjectName = "ObjectName";
        public const string DependOnService = "DependOnService";
        public const string DelayedAutostart = "DelayedAutostart";
        public const string Start = "Start";
    }

    /// <summary>
    /// 通知设置的值名（全局与服务级相同）
    /// </summary>
    public static class NotificationNames
    {
        public const string WebhookUrl = "WebhookUrl";
        /// <summary>逗号分隔的事件名</summary>
        public const string WebhookEvents = "WebhookEvents";
    }

    /// <summary>
    /// SOFTWARE\WindowsServiceManager 下的全局设置值名
    /// </summary>
    public static class SettingNames
    {
        public const string LogRetentionDays = "LogRetentionDays";
        public const string LogMaxTotalSizeMB = "LogMaxTotalSizeMB";
        public const string LogWordWrap = "LogWordWrap";
        public const string LogShowTimestamps = "LogShowTimestamps";
    }
}
