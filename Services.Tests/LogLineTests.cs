using Services.Core.Helpers;
using Xunit;

namespace Services.Tests
{
    public class LogLineTests
    {
        [Theory]
        [InlineData(LogLineKind.Stdout, "hello world")]
        [InlineData(LogLineKind.Stderr, "something failed")]
        [InlineData(LogLineKind.System, "服务已启动")]
        [InlineData(LogLineKind.Stdout, "")]
        [InlineData(LogLineKind.Stderr, "")]
        public void FormatThenParse_RoundTrips(LogLineKind kind, string text)
        {
            var (parsedKind, parsedText) = LogLine.Parse(LogLine.Format(kind, text));

            Assert.Equal(kind, parsedKind);
            Assert.Equal(text, parsedText);
        }

        [Fact]
        public void Format_Stdout_IsWrittenAsIs()
        {
            Assert.Equal("plain", LogLine.Format(LogLineKind.Stdout, "plain"));
        }

        [Fact]
        public void Format_AddsTagsForStderrAndSystem()
        {
            Assert.Equal("[stderr] boom", LogLine.Format(LogLineKind.Stderr, "boom"));
            Assert.Equal("[system] started", LogLine.Format(LogLineKind.System, "started"));
        }

        [Fact]
        public void Parse_LegacyErrorPrefix_IsStderr()
        {
            var (kind, text) = LogLine.Parse("ERROR: disk full");

            Assert.Equal(LogLineKind.Stderr, kind);
            Assert.Equal("disk full", text);
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
            var (kind, text) = LogLine.Parse(line);

            Assert.Equal(LogLineKind.Stdout, kind);
            Assert.Equal(line, text);
        }

        [Fact]
        public void Parse_OnlyFirstTagIsRemoved()
        {
            var (kind, text) = LogLine.Parse("[stderr] [system] nested");

            Assert.Equal(LogLineKind.Stderr, kind);
            Assert.Equal("[system] nested", text);
        }

        [Fact]
        public void Parse_TagOnly_YieldsEmptyText()
        {
            var (kind, text) = LogLine.Parse("[system] ");

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
