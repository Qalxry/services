using System;

namespace Services.Core.Models
{
    /// <summary>
    /// 包装器写入的服务运行状态（序列化为状态文件）。时间均为本地时间。
    /// 状态只反映最近一次包装器运行；包装器未运行时文件保留最后的状态。
    /// </summary>
    public sealed class ServiceRuntimeState
    {
        public string ServiceId { get; set; } = "";

        /// <summary>包装器进程 PID，即 SCM 报告的服务进程 ID</summary>
        public int WrapperPid { get; set; }

        public DateTime WrapperStartedAt { get; set; }

        /// <summary>当前目标进程 PID；目标进程未运行（已退出、等待重启或服务已停止）时为 null</summary>
        public int? ProcessPid { get; set; }

        public DateTime? ProcessStartedAt { get; set; }

        /// <summary>目标进程最近一次的退出码</summary>
        public int? LastExitCode { get; set; }

        public DateTime? LastExitAt { get; set; }

        /// <summary>当前计数窗口内的重启次数（与包装器的重启计数一致，超过重置时间后清零）</summary>
        public int RestartCount { get; set; }

        /// <summary>本次包装器运行以来实际安排的重启总次数</summary>
        public int TotalRestarts { get; set; }

        /// <summary>包装器最近一次以失败状态停止的原因（放弃重启、程序不存在等）</summary>
        public string? LastFailureReason { get; set; }

        /// <summary>记录 LastFailureReason 的时间（用于判断失败原因是否属于本次启动）</summary>
        public DateTime? LastFailureAt { get; set; }

        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// 计算服务运行时长。仅当服务正在运行，且状态文件由当前服务进程写入（WrapperPid 与服务 PID 一致）时有值；
        /// 否则（无状态、服务未运行、状态文件属于旧的进程）返回 null。
        /// </summary>
        /// <param name="servicePid">SCM 报告的服务进程 ID，未运行时为 0</param>
        public static TimeSpan? GetUptime(ServiceRuntimeState? state, int servicePid, bool isRunning, DateTime now)
        {
            if (state == null || !isRunning || servicePid <= 0 || state.WrapperPid != servicePid) return null;

            var uptime = now - state.WrapperStartedAt;
            return uptime < TimeSpan.Zero ? TimeSpan.Zero : uptime;
        }
    }
}
