using System;
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
    /// 日志行格式：stdout 原样写入，stderr 与包装器自身消息带简短标签，便于日志查看器区分与过滤。
    /// </summary>
    public static class LogLine
    {
        public const string StderrTag = "[stderr] ";
        public const string SystemTag = "[system] ";

        // 旧版本写入的 stderr 前缀
        private const string LegacyStderrPrefix = "ERROR: ";

        public static string Format(LogLineKind kind, string text)
        {
            return kind switch
            {
                LogLineKind.Stderr => StderrTag + text,
                LogLineKind.System => SystemTag + text,
                _ => text
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

        public static (LogLineKind Kind, string Text) Parse(string line)
        {
            if (line.StartsWith(StderrTag, StringComparison.Ordinal))
                return (LogLineKind.Stderr, line.Substring(StderrTag.Length));

            if (line.StartsWith(SystemTag, StringComparison.Ordinal))
                return (LogLineKind.System, line.Substring(SystemTag.Length));

            if (line.StartsWith(LegacyStderrPrefix, StringComparison.Ordinal))
                return (LogLineKind.Stderr, line.Substring(LegacyStderrPrefix.Length));

            return (LogLineKind.Stdout, line);
        }
    }
}
