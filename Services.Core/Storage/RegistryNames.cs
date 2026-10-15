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
        /// <summary>REG_MULTI_SZ，每项 KEY=VALUE</summary>
        public const string Environment = "Environment";
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
