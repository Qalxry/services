using Services.Core.Helpers;
using Xunit;

namespace Services.Tests
{
    public class LogLineTests
    {
        private static readonly DateTime Stamp = new(2026, 10, 15, 10, 20, 30, 123);

        [Theory]
        [InlineData(LogLineKind.Stdout, "hello world")]
        [InlineData(LogLineKind.Stderr, "something failed")]
        [InlineData(LogLineKind.System, "服务已启动")]
        [InlineData(LogLineKind.Stdout, "")]
        [InlineData(LogLineKind.Stderr, "")]
        public void FormatThenParse_RoundTrips(LogLineKind kind, string text)
        {
            var parsed = LogLine.Parse(LogLine.Format(kind, text, Stamp));

            Assert.Equal(kind, parsed.Kind);
            Assert.Equal(text, parsed.Text);
            Assert.Equal(Stamp, parsed.Timestamp);
        }

        [Fact]
        public void Format_Stdout_HasTimestampPrefixOnly()
        {
            Assert.Equal("2026-10-15T10:20:30.123 plain", LogLine.Format(LogLineKind.Stdout, "plain", Stamp));
        }

        [Fact]
        public void Format_AddsTagsForStderrAndSystem()
        {
            Assert.Equal("2026-10-15T10:20:30.123 [stderr] boom", LogLine.Format(LogLineKind.Stderr, "boom", Stamp));
            Assert.Equal("2026-10-15T10:20:30.123 [system] started", LogLine.Format(LogLineKind.System, "started", Stamp));
        }

        [Fact]
        public void Format_PadsFieldsAndKeepsMilliseconds()
        {
            var stamp = new DateTime(2026, 1, 2, 3, 4, 5, 6);

            Assert.Equal("2026-01-02T03:04:05.006 x", LogLine.Format(LogLineKind.Stdout, "x", stamp));
        }

        [Fact]
        public void Format_IgnoresCurrentCulture()
        {
            var original = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");

                Assert.Equal("2026-10-15T10:20:30.123 x", LogLine.Format(LogLineKind.Stdout, "x", Stamp));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void Parse_TimestampedLines_ExposeKindTextAndTimestamp()
        {
            var stdout = LogLine.Parse("2026-10-15T10:20:30.123 hello");
            var stderr = LogLine.Parse("2026-10-15T10:20:31.456 [stderr] boom");
            var system = LogLine.Parse("2026-10-15T10:20:32.001 [system] Process exited (code: 1)");

            Assert.Equal(new ParsedLogLine(LogLineKind.Stdout, "hello", new DateTime(2026, 10, 15, 10, 20, 30, 123)), stdout);
            Assert.Equal(new ParsedLogLine(LogLineKind.Stderr, "boom", new DateTime(2026, 10, 15, 10, 20, 31, 456)), stderr);
            Assert.Equal(new ParsedLogLine(LogLineKind.System, "Process exited (code: 1)", new DateTime(2026, 10, 15, 10, 20, 32, 1)), system);
        }

        [Fact]
        public void Parse_TimestampedLegacyErrorPrefix_IsStderr()
        {
            var parsed = LogLine.Parse("2026-10-15T10:20:30.123 ERROR: disk full");

            Assert.Equal(LogLineKind.Stderr, parsed.Kind);
            Assert.Equal("disk full", parsed.Text);
            Assert.Equal(Stamp, parsed.Timestamp);
        }

        [Fact]
        public void Parse_TimestampOnly_YieldsEmptyText()
        {
            // 空文本的行写入时为“时间戳 + 空格”；末尾空格被去掉时同样接受
            foreach (var line in new[] { "2026-10-15T10:20:30.123 ", "2026-10-15T10:20:30.123" })
            {
                var parsed = LogLine.Parse(line);

                Assert.Equal(LogLineKind.Stdout, parsed.Kind);
                Assert.Equal(string.Empty, parsed.Text);
                Assert.Equal(Stamp, parsed.Timestamp);
            }
        }

        [Fact]
        public void Parse_OnlyFirstTimestampIsRemoved()
        {
            var parsed = LogLine.Parse("2026-10-15T10:20:30.123 2026-10-15T09:00:00.000 inner");

            Assert.Equal("2026-10-15T09:00:00.000 inner", parsed.Text);
            Assert.Equal(Stamp, parsed.Timestamp);
        }

        [Theory]
        [InlineData("2026-13-15T10:20:30.123 invalid month")]
        [InlineData("2026-10-15T25:20:30.123 invalid hour")]
        [InlineData("2026-10-15T10:20:30 no milliseconds")]
        [InlineData("2026-10-15 10:20:30.123 space instead of T")]
        [InlineData("2026-10-15T10:20:30.123Z zone suffix")]
        [InlineData("2026-10-15T10:20:30.123x not followed by a space")]
        [InlineData("2026-10-15T10:20:30.12 short milliseconds")]
        [InlineData(" 2026-10-15T10:20:30.123 leading space")]
        [InlineData("abcd-ef-ghTij:kl:mn.opq text")]
        public void Parse_InvalidTimestamp_IsPlainText(string line)
        {
            var parsed = LogLine.Parse(line);

            Assert.Equal(LogLineKind.Stdout, parsed.Kind);
            Assert.Equal(line, parsed.Text);
            Assert.Null(parsed.Timestamp);
        }

        [Fact]
        public void Parse_LegacyErrorPrefix_IsStderr()
        {
            var (kind, text, timestamp) = LogLine.Parse("ERROR: disk full");

            Assert.Equal(LogLineKind.Stderr, kind);
            Assert.Equal("disk full", text);
            Assert.Null(timestamp);
        }

        [Theory]
        [InlineData("just a line")]
        [InlineData("")]
        [InlineData("error: lower case is not legacy")]
        [InlineData("ERROR:no space is not legacy")]
        [InlineData(" [stderr] leading space is not a tag")]
        [InlineData("[stderr]no space is not a tag")]
        public void Parse_OrdinaryLine_IsStdoutUnchanged(string line)
        {
            var (kind, text, timestamp) = LogLine.Parse(line);

            Assert.Equal(LogLineKind.Stdout, kind);
            Assert.Equal(line, text);
            Assert.Null(timestamp);
        }

        [Fact]
        public void Parse_OnlyFirstTagIsRemoved()
        {
            var (kind, text, _) = LogLine.Parse("[stderr] [system] nested");

            Assert.Equal(LogLineKind.Stderr, kind);
            Assert.Equal("[system] nested", text);
        }

        [Fact]
        public void Parse_TagOnly_YieldsEmptyText()
        {
            var (kind, text, _) = LogLine.Parse("[system] ");

            Assert.Equal(LogLineKind.System, kind);
            Assert.Equal(string.Empty, text);
        }

        [Fact]
        public void StripAnsi_NoEscape_ReturnsSameText()
        {
            Assert.Equal("plain [text]", LogLine.StripAnsi("plain [text]"));
        }

        [Theory]
        [InlineData("\u001b[31mred\u001b[0m", "red")]
        [InlineData("\u001b[1;32mbold green\u001b[0m", "bold green")]
        [InlineData("\u001b[38;5;208morange\u001b[39m", "orange")]
        [InlineData("\u001b[38;2;1;2;3mtrue color\u001b[0m", "true color")]
        [InlineData("\u001b[mreset\u001b[m", "reset")]
        public void StripAnsi_RemovesSgrSequences(string input, string expected)
        {
            Assert.Equal(expected, LogLine.StripAnsi(input));
        }

        [Theory]
        [InlineData("a\u001b[2Kb", "ab")]
        [InlineData("a\u001b[10Ab", "ab")]
        [InlineData("a\u001b[2;5Hb", "ab")]
        [InlineData("a\u001b[?25lb\u001b[?25h", "ab")]
        [InlineData("\u001b[1G\u001b[Kprogress", "progress")]
        public void StripAnsi_RemovesCursorAndEraseSequences(string input, string expected)
        {
            Assert.Equal(expected, LogLine.StripAnsi(input));
        }

        [Theory]
        [InlineData("\u001b]0;window title\u0007text", "text")]
        [InlineData("\u001b]8;;http://example.com\u001b\\link\u001b]8;;\u001b\\", "link")]
        public void StripAnsi_RemovesOscSequences(string input, string expected)
        {
            Assert.Equal(expected, LogLine.StripAnsi(input));
        }

        [Fact]
        public void StripAnsi_RemovesTwoCharEscapeSequences()
        {
            Assert.Equal("ab", LogLine.StripAnsi("a\u001bMb"));
        }

        [Fact]
        public void StripAnsi_KeepsNonEscapeContent()
        {
            Assert.Equal("[ok] 完成 100%", LogLine.StripAnsi("\u001b[32m[ok]\u001b[0m 完成 100%"));
        }

        [Fact]
        public void StripAnsi_Empty_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, LogLine.StripAnsi(string.Empty));
        }
    }
}
