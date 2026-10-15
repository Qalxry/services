using Services.Cli;
using Xunit;

namespace Services.Tests
{
    public class LogTimeParserTests
    {
        private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0);

        [Theory]
        [InlineData("30s", 0, 0, 30)]
        [InlineData("10m", 0, 10, 0)]
        [InlineData("2h", 2, 0, 0)]
        [InlineData("1d", 24, 0, 0)]
        [InlineData("90m", 1, 30, 0)]
        [InlineData("0s", 0, 0, 0)]
        public void ParseSince_RelativeDuration_SubtractsFromNow(string raw, int hours, int minutes, int seconds)
        {
            var expected = Now - new TimeSpan(hours, minutes, seconds);

            Assert.Equal(expected, LogTimeParser.ParseSince(raw, Now));
        }

        [Theory]
        [InlineData("10M")]
        [InlineData("2H")]
        [InlineData("1D")]
        [InlineData("30S")]
        [InlineData("  10m  ")]
        public void ParseSince_RelativeDuration_IsCaseInsensitiveAndTrimmed(string raw)
        {
            Assert.True(LogTimeParser.ParseSince(raw, Now) < Now);
        }

        [Theory]
        [InlineData("2026-10-15T10:20:30", 2026, 10, 15, 10, 20, 30, 0)]
        [InlineData("2026-10-15T10:20:30.123", 2026, 10, 15, 10, 20, 30, 123)]
        [InlineData("2026-10-15 10:20:30", 2026, 10, 15, 10, 20, 30, 0)]
        [InlineData("2026-10-15T10:20", 2026, 10, 15, 10, 20, 0, 0)]
        [InlineData("2026-10-15", 2026, 10, 15, 0, 0, 0, 0)]
        public void ParseSince_IsoWithoutZone_IsLocalTime(string raw, int y, int mo, int d, int h, int mi, int s, int ms)
        {
            var parsed = LogTimeParser.ParseSince(raw, Now);

            Assert.Equal(new DateTime(y, mo, d, h, mi, s, ms), parsed);
        }

        [Fact]
        public void ParseSince_IsoWithUtcSuffix_IsConvertedToLocal()
        {
            var expected = new DateTime(2026, 10, 15, 2, 20, 30, DateTimeKind.Utc).ToLocalTime();

            Assert.Equal(expected, LogTimeParser.ParseSince("2026-10-15T02:20:30Z", Now));
        }

        [Fact]
        public void ParseSince_IsoWithOffset_IsConvertedToLocal()
        {
            // +08:00 的 10:20:30 即 UTC 02:20:30
            var expected = new DateTime(2026, 10, 15, 2, 20, 30, DateTimeKind.Utc).ToLocalTime();

            Assert.Equal(expected, LogTimeParser.ParseSince("2026-10-15T10:20:30+08:00", Now));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("abc")]
        [InlineData("10")]
        [InlineData("m10")]
        [InlineData("10x")]
        [InlineData("1.5h")]
        [InlineData("-5m")]
        [InlineData("+5m")]
        [InlineData("5 m")]
        [InlineData("10:20:30")]
        [InlineData("2026-13-45")]
        [InlineData("2026-10-15T25:00:00")]
        [InlineData("yesterday")]
        public void ParseSince_Invalid_ThrowsUsageExceptionForLogs(string raw)
        {
            var ex = Assert.Throws<UsageException>(() => LogTimeParser.ParseSince(raw, Now));

            Assert.Equal(ExitCodes.Usage, ex.ExitCode);
            Assert.Equal("logs", ex.Command);
            Assert.Contains("--since", ex.Message);
        }

        [Theory]
        [InlineData("99999999999999999999d")]
        [InlineData("9999999999d")]
        [InlineData("9999999999999999h")]
        public void ParseSince_AbsurdlyLargeDuration_ThrowsUsageException(string raw)
        {
            Assert.Throws<UsageException>(() => LogTimeParser.ParseSince(raw, Now));
        }
    }
}
