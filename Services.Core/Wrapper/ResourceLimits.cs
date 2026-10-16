namespace Services.Core.Wrapper
{
    /// <summary>
    /// 目标程序的资源限制（0 表示不限制）
    /// </summary>
    public sealed record ResourceLimits(int MemoryLimitMB, int ProcessMemoryLimitMB, int CpuLimitPercent, int MaxProcesses)
    {
        public static ResourceLimits None { get; } = new(0, 0, 0, 0);

        public bool IsEmpty => MemoryLimitMB <= 0 && ProcessMemoryLimitMB <= 0 && CpuLimitPercent <= 0 && MaxProcesses <= 0;
    }
}
