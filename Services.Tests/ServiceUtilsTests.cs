using Services.Core.Helpers;
using Xunit;

namespace Services.Tests
{
    public class ServiceUtilsTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\r\n\t \n")]
        public void NormalizeArgs_NullOrWhitespace_ReturnsEmpty(string? input)
        {
            Assert.Equal(string.Empty, ServiceUtils.NormalizeArgs(input));
        }

        [Fact]
        public void NormalizeArgs_SingleLine_Unchanged()
        {
            Assert.Equal("--port 80", ServiceUtils.NormalizeArgs("--port 80"));
        }

        [Theory]
        [InlineData("a\nb\nc")]
        [InlineData("a\r\nb\r\nc")]
        [InlineData("a\rb\rc")]
        public void NormalizeArgs_AnyLineBreak_JoinedWithSpace(string input)
        {
            Assert.Equal("a b c", ServiceUtils.NormalizeArgs(input));
        }

        [Fact]
        public void NormalizeArgs_MixedLineBreaks_AllHandled()
        {
            Assert.Equal("a b c d", ServiceUtils.NormalizeArgs("a\r\nb\nc\rd"));
        }

        [Fact]
        public void NormalizeArgs_TrimsEachLine()
        {
            Assert.Equal("--a 1 --b 2", ServiceUtils.NormalizeArgs("  --a 1  \n\t--b 2\t"));
        }

        [Fact]
        public void NormalizeArgs_SkipsEmptyAndBlankLines()
        {
            Assert.Equal("x y", ServiceUtils.NormalizeArgs("\n\nx\n\n   \r\n\ny\n\n"));
        }

        [Fact]
        public void NormalizeArgs_KeepsInnerWhitespaceWithinLine()
        {
            Assert.Equal("--name \"a  b\" --flag", ServiceUtils.NormalizeArgs("--name \"a  b\"\n--flag"));
        }
    }
}
