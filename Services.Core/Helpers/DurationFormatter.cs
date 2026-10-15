using System;

namespace Services.Core.Helpers
{
    /// <summary>
    /// 将时长格式化为中文短文本，最多显示两个相邻单位（较小的单位向下取整）：
    /// 45 秒、5 分 12 秒、2 小时 3 分、3 天 4 小时
    /// </summary>
    public static class DurationFormatter
    {
        public static string Format(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

            if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays} 天 {duration.Hours} 小时";
            if (duration.TotalHours >= 1) return $"{duration.Hours} 小时 {duration.Minutes} 分";
            if (duration.TotalMinutes >= 1) return $"{duration.Minutes} 分 {duration.Seconds} 秒";
            return $"{duration.Seconds} 秒";
        }
    }
}
