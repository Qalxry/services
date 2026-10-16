using System;

namespace Services.Core.Health
{
    public static class HealthStatuses
    {
        public const string Starting = "starting";
        public const string Healthy = "healthy";
        public const string Unhealthy = "unhealthy";
    }

    /// <summary>
    /// 健康检查结果的计数（纯逻辑，与 Docker HEALTHCHECK 语义一致）：
    /// 启动宽限期内的失败不计数，期间一次成功即视为已就绪；连续失败达到阈值判定为不健康。
    /// </summary>
    public sealed class HealthTracker
    {
        private readonly int _threshold;
        private readonly DateTime _startPeriodEnds;
        private bool _ready;

        public HealthTracker(int failureThreshold, TimeSpan startPeriod, DateTime startedAt)
        {
            _threshold = Math.Max(1, failureThreshold);
            _startPeriodEnds = startedAt + startPeriod;
        }

        public int ConsecutiveFailures { get; private set; }

        public string Status { get; private set; } = HealthStatuses.Starting;

        /// <summary>
        /// 记录一次检查结果，返回本次是否刚判定为不健康（需要重启目标程序）
        /// </summary>
        public bool Record(bool healthy, DateTime now)
        {
            if (healthy)
            {
                _ready = true;
                ConsecutiveFailures = 0;
                Status = HealthStatuses.Healthy;
                return false;
            }

            if (!_ready && now < _startPeriodEnds)
            {
                Status = HealthStatuses.Starting;
                return false;
            }

            ConsecutiveFailures++;
            if (ConsecutiveFailures < _threshold) return false;

            bool becameUnhealthy = Status != HealthStatuses.Unhealthy;
            Status = HealthStatuses.Unhealthy;
            return becameUnhealthy;
        }
    }
}
