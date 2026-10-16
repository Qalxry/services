using System;
using Services.Core.Models;

namespace Services.Core.Wrapper
{
    /// <summary>
    /// 失败自动重启策略（来自服务参数）
    /// </summary>
    public sealed record RestartPolicySettings(bool Enabled, int DelaySeconds, int MaxRestarts, int ResetMinutes, bool Backoff)
    {
        public static RestartPolicySettings Default { get; } = new(
            false, RestartPolicyDefaults.DelaySeconds, RestartPolicyDefaults.MaxRestarts,
            RestartPolicyDefaults.ResetMinutes, RestartPolicyDefaults.Backoff);

        /// <summary>重启等待的上限（秒），与 CLI / GUI 的取值范围一致</summary>
        public const int MaxDelaySeconds = 3600;

        /// <summary>指数退避最多翻倍的次数（1、2、4、8、16 倍）</summary>
        public const int MaxBackoffShift = 4;
    }

    /// <summary>
    /// 重启计数：距离上一次重启超过重置时间则重新计数；超过最大次数时放弃。不包含任何线程同步，由调用方保证串行访问。
    /// </summary>
    public sealed class RestartTracker
    {
        private readonly RestartPolicySettings _settings;
        private DateTime _lastRestart = DateTime.MinValue;

        public RestartTracker(RestartPolicySettings settings)
        {
            _settings = settings;
        }

        /// <summary>当前计数窗口内的重启次数</summary>
        public int Count { get; private set; }

        /// <summary>实际安排过的重启总次数</summary>
        public int Total { get; private set; }

        /// <summary>
        /// 申请下一次重启。返回本次重启前应等待的时间；超过最大重启次数时返回 null（不再重启）。
        /// </summary>
        public TimeSpan? Next(DateTime now)
        {
            if (now - _lastRestart > TimeSpan.FromMinutes(_settings.ResetMinutes))
                Count = 0;

            Count++;
            if (_settings.MaxRestarts > 0 && Count > _settings.MaxRestarts)
                return null;

            _lastRestart = now;
            Total++;

            long delayMs = Math.Clamp(_settings.DelaySeconds, 1, RestartPolicySettings.MaxDelaySeconds) * 1000L;
            if (_settings.Backoff)
                delayMs <<= Math.Min(Count - 1, RestartPolicySettings.MaxBackoffShift);
            return TimeSpan.FromMilliseconds(delayMs);
        }
    }
}
