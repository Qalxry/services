using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Services.Core.Helpers
{
    public enum LogLineKind
    {
        Stdout,
        Stderr,
        System
    }

    /// <summary>
    /// 解析后的日志行。旧格式（无时间戳）的行 Timestamp 为 null。
    /// </summary>
    public readonly record struct ParsedLogLine(LogLineKind Kind, string Text, DateTime? Timestamp);

    /// <summary>
    /// 日志行格式：行首为本地时间戳（ISO 8601，毫秒精度），其后 stdout 原样写入，
    /// stderr 与包装器自身消息带简短标签，便于日志查看器区分与过滤。
    /// 例：2026-10-15T10:20:30.123 [stderr] boom
    /// </summary>
    public static class LogLine
    {
        public const string StderrTag = "[stderr] ";
        public const string SystemTag = "[system] ";

        /// <summary>
        /// 行首时间戳的格式（本地时间，不带时区），其后紧跟一个空格
        /// </summary>
        public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff";

        private const int TimestampLength = 23;

        // 旧版本写入的 stderr 前缀
        private const string LegacyStderrPrefix = "ERROR: ";

        public static string Format(LogLineKind kind, string text, DateTime timestamp)
        {
            var stamp = timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture);
            return kind switch
            {
                LogLineKind.Stderr => $"{stamp} {StderrTag}{text}",
                LogLineKind.System => $"{stamp} {SystemTag}{text}",
                _ => $"{stamp} {text}"
            };
        }

        // CSI（含 SGR 颜色）、OSC 及其他双字符 ESC 序列
        private static readonly Regex AnsiEscapeRegex = new(
            @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)?|[@-Z\\-_])",
            RegexOptions.Compiled);

        /// <summary>
        /// 去除 ANSI 转义序列（颜色、光标控制等），得到纯文本
        /// </summary>
        public static string StripAnsi(string text)
        {
            return text.IndexOf('\x1B') < 0 ? text : AnsiEscapeRegex.Replace(text, string.Empty);
        }

        /// <summary>
        /// 解析一行日志，兼容旧格式（无时间戳、"ERROR: " 前缀）。
        /// 行首不是合法时间戳时视为普通文本。
        /// </summary>
        public static ParsedLogLine Parse(string line)
        {
            DateTime? timestamp = null;
            if (TryParseTimestamp(line, out var parsed))
            {
                timestamp = parsed;
                line = line.Length > TimestampLength ? line.Substring(TimestampLength + 1) : string.Empty;
            }

            if (line.StartsWith(StderrTag, StringComparison.Ordinal))
                return new ParsedLogLine(LogLineKind.Stderr, line.Substring(StderrTag.Length), timestamp);

            if (line.StartsWith(SystemTag, StringComparison.Ordinal))
                return new ParsedLogLine(LogLineKind.System, line.Substring(SystemTag.Length), timestamp);

            if (line.StartsWith(LegacyStderrPrefix, StringComparison.Ordinal))
                return new ParsedLogLine(LogLineKind.Stderr, line.Substring(LegacyStderrPrefix.Length), timestamp);

            return new ParsedLogLine(LogLineKind.Stdout, line, timestamp);
        }

        // 时间戳后必须紧跟空格（空文本的行末尾空格被编辑器去掉时，恰好只剩时间戳也接受）
        private static bool TryParseTimestamp(string line, out DateTime timestamp)
        {
            timestamp = default;
            if (line.Length < TimestampLength || (line.Length > TimestampLength && line[TimestampLength] != ' '))
                return false;

            return DateTime.TryParseExact(line.AsSpan(0, TimestampLength), TimestampFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
        }
    }
}
