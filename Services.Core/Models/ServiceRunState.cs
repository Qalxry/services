namespace Services.Core.Models
{
    /// <summary>
    /// 服务运行状态，取值与 SCM 的 SERVICE_STATUS.dwCurrentState 一致。展示文本见 Presentation.ServiceText。
    /// </summary>
    public enum ServiceRunState
    {
        /// <summary>无法查询（服务不存在或无权限）</summary>
        Unknown = 0,
        Stopped = 1,
        StartPending = 2,
        StopPending = 3,
        Running = 4,
        ContinuePending = 5,
        PausePending = 6,
        Paused = 7
    }
}
