using System.Globalization;
using System.Text.RegularExpressions;

namespace Services.Cli
{
    /// <summary>
    /// 解析 servicesctl logs --since 的时间参数（无 Windows 依赖，测试工程直接链接此文件）
    /// </summary>
    internal static class LogTimeParser
    {
        // 相对时长：数字 + 单位（s 秒 / m 分 / h 时 / d 天）
        private static readonly Regex RelativeRegex = new(@"^(\d+)([smhd])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // ISO 8601 以 yyyy-MM-dd 开头，避免把 "10" 之类的数字误当作日期
        private static readonly Regex IsoRegex = new(@"^\d{4}-\d{2}-\d{2}", RegexOptions.CultureInvariant);

        /// <summary>
        /// 解析 ISO 8601 时间（不带时区时按本地时间，带 Z / 偏移时转换为本地时间）
        /// 或相对时长（30s / 10m / 2h / 1d，相对 now）。返回本地时间。
        /// </summary>
        public static DateTime ParseSince(string raw, DateTime now)
        {
            var text = raw.Trim();

            var relative = RelativeRegex.Match(text);
            if (relative.Success)
            {
                if (long.TryParse(relative.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
                {
                    try
                    {
                        var span = char.ToLowerInvariant(relative.Groups[2].Value[0]) switch
                        {
                            's' => TimeSpan.FromSeconds(amount),
                            'm' => TimeSpan.FromMinutes(amount),
                            'h' => TimeSpan.FromHours(amount),
                            _ => TimeSpan.FromDays(amount)
                        };
                        return now - span;
                    }
                    catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
                    {
                    }
                }
                throw Invalid(raw, "时长过大");
            }

            if (IsoRegex.IsMatch(text) &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var absolute))
            {
                return absolute.LocalDateTime;
            }

            throw Invalid(raw, null);
        }

        private static UsageException Invalid(string raw, string? reason)
        {
            var detail = reason == null ? "" : $"（{reason}）";
            return new UsageException(
                $"--since 的值必须是 ISO 8601 时间（如 2026-10-15T10:20:30）或相对时长（如 30s、10m、2h、1d），实际为 '{raw}'{detail}。", "logs");
        }
    }
}
