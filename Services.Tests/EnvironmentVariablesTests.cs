using Services.Core.Helpers;
using Xunit;

namespace Services.Tests
{
    public class EnvironmentVariablesTests
    {
        [Fact]
        public void Parse_SplitsOnFirstEquals()
        {
            var result = EnvironmentVariables.Parse(new[] { "CONN=Server=db;Port=1", "A=1" });

            Assert.Equal("Server=db;Port=1", result["CONN"]);
            Assert.Equal("1", result["A"]);
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void Parse_IgnoresBlankLinesAndTrimsLineAndKey()
        {
            var result = EnvironmentVariables.Parse(new[] { "", "   ", "  A = 1  ", "\t" });

            Assert.Single(result);
            Assert.Equal(" 1", result["A"]);
        }

        [Fact]
        public void Parse_AllowsEmptyValue()
        {
            var result = EnvironmentVariables.Parse(new[] { "EMPTY=" });

            Assert.Equal("", result["EMPTY"]);
        }

        [Fact]
        public void Parse_DuplicateKeysLaterWins_CaseInsensitive()
        {
            var result = EnvironmentVariables.Parse(new[] { "Path=a", "PATH=b" });

            Assert.Single(result);
            Assert.Equal("b", result["path"]);
        }

        [Theory]
        [InlineData("NOEQUALS")]
        [InlineData("=value")]
        [InlineData("  =value")]
        [InlineData("A\0B=1")]
        [InlineData("A=1\02")]
        public void Parse_InvalidEntry_ThrowsWithLineNumber(string bad)
        {
            var ex = Assert.Throws<ArgumentException>(() => EnvironmentVariables.Parse(new[] { "OK=1", "", bad }));

            Assert.Contains("第 3 行", ex.Message);
        }

        [Fact]
        public void ParseText_HandlesAllLineBreakStyles()
        {
            var result = EnvironmentVariables.ParseText("A=1\r\nB=2\nC=3\rD=4\r\n");

            Assert.Equal(4, result.Count);
            Assert.Equal("3", result["C"]);
        }

        [Fact]
        public void ParseText_NullOrEmpty_IsEmpty()
        {
            Assert.Empty(EnvironmentVariables.ParseText(null));
            Assert.Empty(EnvironmentVariables.ParseText(""));
        }

        [Fact]
        public void ParseLenient_SkipsInvalidEntries()
        {
            var result = EnvironmentVariables.ParseLenient(new[] { "A=1", "garbage", "=x", "B=2" });

            Assert.Equal(2, result.Count);
            Assert.Equal("2", result["B"]);
            Assert.Empty(EnvironmentVariables.ParseLenient(null));
        }

        [Fact]
        public void ParseEntry_ReturnsKeyAndValue()
        {
            var entry = EnvironmentVariables.ParseEntry("PATH=%PATH%;C:\\tools");

            Assert.Equal("PATH", entry.Key);
            Assert.Equal("%PATH%;C:\\tools", entry.Value);
        }

        [Fact]
        public void ParseEntry_Invalid_Throws()
        {
            Assert.Throws<ArgumentException>(() => EnvironmentVariables.ParseEntry("KEY"));
            Assert.Throws<ArgumentException>(() => EnvironmentVariables.ParseEntry("=1"));
        }

        [Fact]
        public void Format_SortsByKeyAndRoundTrips()
        {
            var env = new Dictionary<string, string> { ["b"] = "2", ["A"] = "1=x", ["C"] = "" };

            var text = EnvironmentVariables.Format(env);

            Assert.Equal("A=1=x\nb=2\nC=", text);
            var parsed = EnvironmentVariables.ParseText(text);
            Assert.Equal(env.Count, parsed.Count);
            Assert.All(env, pair => Assert.Equal(pair.Value, parsed[pair.Key]));
        }

        [Fact]
        public void Format_Empty_IsEmptyString()
        {
            Assert.Equal("", EnvironmentVariables.Format(new Dictionary<string, string>()));
            Assert.Empty(EnvironmentVariables.FormatLines(new Dictionary<string, string>()));
        }

        [Fact]
        public void Validate_AcceptsNormalVariables()
        {
            EnvironmentVariables.Validate(new Dictionary<string, string> { ["A"] = "1", ["ProgramFiles(x86)"] = "x y", ["E"] = "" });
        }

        [Theory]
        [InlineData("", "v")]
        [InlineData("A=B", "v")]
        [InlineData("A\0", "v")]
        [InlineData(" A", "v")]
        [InlineData("A", "line1\nline2")]
        [InlineData("A", "line1\rline2")]
        [InlineData("A", "x\0y")]
        public void Validate_RejectsIllegalKeyOrValue(string key, string value)
        {
            Assert.Throws<ArgumentException>(() => EnvironmentVariables.Validate(new Dictionary<string, string> { [key] = value }));
        }

        [Fact]
        public void Clone_CreatesIndependentCaseInsensitiveCopy()
        {
            var source = new Dictionary<string, string> { ["Path"] = "x" };

            var copy = EnvironmentVariables.Clone(source);
            copy["Other"] = "y";

            Assert.Equal("x", copy["PATH"]);
            Assert.False(source.ContainsKey("Other"));
            Assert.Empty(EnvironmentVariables.Clone(null));
        }

        [Fact]
        public void Apply_OverridesInheritedAndExpandsValues()
        {
            var target = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = "C:\\base", ["KEEP"] = "k" };
            var env = new Dictionary<string, string> { ["Path"] = "%PATH%;C:\\tools", ["NEW"] = "n" };

            EnvironmentVariables.Apply(target, env, value => value.Replace("%PATH%", target["PATH"]));

            Assert.Equal("C:\\base;C:\\tools", target["PATH"]);
            Assert.Equal("n", target["NEW"]);
            Assert.Equal("k", target["KEEP"]);
        }
    }
}
