using Services.Cli;
using Xunit;

namespace Services.Tests
{
    public class CommandLineTests
    {
        private static CommandSpec Spec(
            string[]? valueOptions = null,
            string[]? flagOptions = null,
            int min = 0,
            int max = 1,
            IReadOnlyDictionary<string, string>? shortAliases = null) => new()
        {
            Name = "demo",
            Summary = "测试命令",
            Help = "demo help",
            ValueOptions = valueOptions ?? new[] { "name", "args", "delay" },
            FlagOptions = flagOptions ?? new[] { "auto-restart", "no-auto-restart", "force" },
            MinPositionals = min,
            MaxPositionals = max,
            ShortAliases = shortAliases ?? new Dictionary<string, string>(),
            Handler = _ => Task.FromResult(0)
        };

        private static ParsedArgs Parse(CommandSpec spec, params string[] tokens) => ParsedArgs.Parse(spec, tokens);

        [Fact]
        public void Parse_ValueOption_SeparateToken()
        {
            var parsed = Parse(Spec(), "--name", "web");

            Assert.Equal("web", parsed.GetValue("name"));
            Assert.True(parsed.HasValue("name"));
            Assert.Empty(parsed.Positionals);
            Assert.Equal("demo", parsed.Command);
        }

        [Fact]
        public void Parse_ValueOption_EqualsForm()
        {
            var parsed = Parse(Spec(), "--name=web");

            Assert.Equal("web", parsed.GetValue("name"));
        }

        [Fact]
        public void Parse_ValueOption_EqualsFormKeepsLaterEqualsSigns()
        {
            var parsed = Parse(Spec(), "--args=--key=value");

            Assert.Equal("--key=value", parsed.GetValue("args"));
        }

        [Fact]
        public void Parse_ValueOption_EmptyInlineValue()
        {
            var parsed = Parse(Spec(), "--name=");

            Assert.True(parsed.HasValue("name"));
            Assert.Equal(string.Empty, parsed.GetValue("name"));
        }

        [Fact]
        public void Parse_ValueOption_ValueMayStartWithDash()
        {
            var parsed = Parse(Spec(), "--args", "--port 80");

            Assert.Equal("--port 80", parsed.GetValue("args"));
            Assert.Empty(parsed.Positionals);
        }

        [Fact]
        public void Parse_ValueOption_NegativeNumberAsValue()
        {
            var parsed = Parse(Spec(), "--delay", "-1");

            Assert.Equal("-1", parsed.GetValue("delay"));
        }

        [Fact]
        public void Parse_ValueOption_LastOneWins()
        {
            var parsed = Parse(Spec(), "--name", "a", "--name=b");

            Assert.Equal("b", parsed.GetValue("name"));
        }

        [Fact]
        public void Parse_OptionNames_AreCaseInsensitive()
        {
            var parsed = Parse(Spec(), "--NAME", "web", "--Force");

            Assert.Equal("web", parsed.GetValue("name"));
            Assert.True(parsed.HasFlag("force"));
        }

        [Fact]
        public void GetValue_NotProvided_ReturnsNull()
        {
            var parsed = Parse(Spec());

            Assert.Null(parsed.GetValue("name"));
            Assert.False(parsed.HasValue("name"));
        }

        [Fact]
        public void Parse_FlagOption_IsRecorded()
        {
            var parsed = Parse(Spec(), "--force");

            Assert.True(parsed.HasFlag("force"));
            Assert.False(parsed.HasFlag("auto-restart"));
        }

        [Fact]
        public void Parse_GlobalFlags_AreAlwaysAccepted()
        {
            var parsed = Parse(Spec(flagOptions: Array.Empty<string>()), "--json");

            Assert.True(parsed.Json);
            Assert.False(parsed.Help);
        }

        [Fact]
        public void Parse_FlagWithValue_Throws()
        {
            var ex = Assert.Throws<UsageException>(() => Parse(Spec(), "--force=yes"));

            Assert.Equal(ExitCodes.Usage, ex.ExitCode);
            Assert.Equal("demo", ex.Command);
            Assert.Contains("--force", ex.Message);
        }

        [Fact]
        public void Parse_GlobalFlagWithValue_Throws()
        {
            Assert.Throws<UsageException>(() => Parse(Spec(), "--json=true"));
        }

        [Fact]
        public void Parse_UnknownLongOption_Throws()
        {
            var ex = Assert.Throws<UsageException>(() => Parse(Spec(), "--nope"));

            Assert.Equal(ExitCodes.Usage, ex.ExitCode);
            Assert.Equal("demo", ex.Command);
            Assert.Contains("--nope", ex.Message);
        }

        [Fact]
        public void Parse_UnknownLongOptionWithValue_ReportsNameWithoutValue()
        {
            var ex = Assert.Throws<UsageException>(() => Parse(Spec(), "--nope=1"));

            Assert.Contains("--nope", ex.Message);
        }

        [Theory]
        [InlineData("-x")]
        [InlineData("-name")]
        public void Parse_ShortOptionOtherThanHelp_Throws(string token)
        {
            var ex = Assert.Throws<UsageException>(() => Parse(Spec(), token));

            Assert.Contains(token, ex.Message);
        }

        private static readonly Dictionary<string, string> Aliases = new() { ["f"] = "force", ["n"] = "name" };

        [Fact]
        public void Parse_ShortAlias_ActsLikeLongFlag()
        {
            var parsed = Parse(Spec(shortAliases: Aliases), "-f");

            Assert.True(parsed.HasFlag("force"));
            Assert.Empty(parsed.Positionals);
        }

        [Fact]
        public void Parse_ShortAlias_ForValueOptionTakesNextToken()
        {
            var parsed = Parse(Spec(shortAliases: Aliases), "-n", "web", "svc");

            Assert.Equal("web", parsed.GetValue("name"));
            Assert.Equal(new[] { "svc" }, parsed.Positionals);
        }

        [Fact]
        public void Parse_ShortAlias_ForValueOptionMissingValue_Throws()
        {
            var ex = Assert.Throws<UsageException>(() => Parse(Spec(shortAliases: Aliases), "-n"));

            Assert.Contains("--name", ex.Message);
        }

        [Fact]
        public void Parse_ShortAlias_MixedWithLongOptions()
        {
            var parsed = Parse(Spec(shortAliases: Aliases), "svc", "-f", "--name=x");

            Assert.True(parsed.HasFlag("force"));
            Assert.Equal("x", parsed.GetValue("name"));
            Assert.Equal(new[] { "svc" }, parsed.Positionals);
        }

        [Fact]
        public void Parse_ShortAlias_NotDeclared_Throws()
        {
            // 未声明别名的命令不接受 -f
            var ex = Assert.Throws<UsageException>(() => Parse(Spec(), "-f"));

            Assert.Contains("-f", ex.Message);
        }

        [Theory]
        [InlineData("-x")]
        [InlineData("-fn")]
        [InlineData("-f=1")]
        [InlineData("-F")]
        public void Parse_ShortAlias_UnknownOrMalformed_Throws(string token)
        {
            var ex = Assert.Throws<UsageException>(() => Parse(Spec(shortAliases: Aliases), token));

            Assert.Contains(token, ex.Message);
        }

        [Fact]
        public void Parse_ShortAlias_AfterDoubleDash_IsPositional()
        {
            var parsed = Parse(Spec(shortAliases: Aliases), "--", "-f");

            Assert.False(parsed.HasFlag("force"));
            Assert.Equal(new[] { "-f" }, parsed.Positionals);
        }

        [Fact]
        public void Parse_ValueOptionMissingValue_Throws()
        {
            var ex = Assert.Throws<UsageException>(() => Parse(Spec(), "--name"));

            Assert.Contains("--name", ex.Message);
            Assert.Equal("demo", ex.Command);
        }

        [Fact]
        public void Parse_DoubleDash_RestAreAllPositionals()
        {
            var spec = Spec(max: 3);

            var parsed = Parse(spec, "--name", "a", "--", "--not-an-option", "-h");

            Assert.Equal("a", parsed.GetValue("name"));
            Assert.Equal(new[] { "--not-an-option", "-h" }, parsed.Positionals);
            Assert.False(parsed.Help);
        }

        [Fact]
        public void Parse_SingleDash_IsPositional()
        {
            var parsed = Parse(Spec(), "-");

            Assert.Equal(new[] { "-" }, parsed.Positionals);
        }

        [Theory]
        [InlineData("-h")]
        [InlineData("--help")]
        [InlineData("/?")]
        public void Parse_Help_SetsHelpFlag(string token)
        {
            var parsed = Parse(Spec(), token);

            Assert.True(parsed.Help);
        }

        [Fact]
        public void Parse_Help_SkipsPositionalCountValidation()
        {
            var spec = Spec(min: 1, max: 1);

            var parsed = Parse(spec, "-h");

            Assert.True(parsed.Help);
            Assert.Empty(parsed.Positionals);
        }

        [Fact]
        public void Parse_TooManyPositionals_ThrowsNamingTheExtraOne()
        {
            var ex = Assert.Throws<UsageException>(() => Parse(Spec(max: 1), "a", "b"));

            Assert.Contains("'b'", ex.Message);
            Assert.Equal("demo", ex.Command);
        }

        [Fact]
        public void Parse_TooFewPositionals_Throws()
        {
            var ex = Assert.Throws<UsageException>(() => Parse(Spec(min: 1), "--force"));

            Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        }

        [Fact]
        public void Parse_PositionalsAndOptionsMixed_AnyOrder()
        {
            var parsed = Parse(Spec(), "--force", "svc", "--name", "x");

            Assert.Equal(new[] { "svc" }, parsed.Positionals);
            Assert.Equal("x", parsed.GetValue("name"));
            Assert.True(parsed.HasFlag("force"));
        }

        [Fact]
        public void Parse_NoTokens_NoMinimum_Succeeds()
        {
            var parsed = Parse(Spec(min: 0, max: 0));

            Assert.Empty(parsed.Positionals);
        }

        [Fact]
        public void GetInt_NotProvided_ReturnsNull()
        {
            Assert.Null(Parse(Spec()).GetInt("delay", 0, 10));
        }

        [Theory]
        [InlineData("0")]
        [InlineData("5")]
        [InlineData("10")]
        public void GetInt_InRange_ReturnsValue(string raw)
        {
            var parsed = Parse(Spec(), "--delay", raw);

            Assert.Equal(int.Parse(raw), parsed.GetInt("delay", 0, 10));
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("11")]
        [InlineData("abc")]
        [InlineData("1.5")]
        [InlineData("")]
        [InlineData("99999999999")]
        public void GetInt_OutOfRangeOrInvalid_Throws(string raw)
        {
            var parsed = Parse(Spec(), "--delay=" + raw);

            var ex = Assert.Throws<UsageException>(() => parsed.GetInt("delay", 0, 10));

            Assert.Contains("--delay", ex.Message);
            Assert.Equal("demo", ex.Command);
        }

        [Fact]
        public void GetSwitch_NeitherSpecified_ReturnsNull()
        {
            Assert.Null(Parse(Spec()).GetSwitch("auto-restart", "no-auto-restart"));
        }

        [Fact]
        public void GetSwitch_OnOrOff_ReturnsBool()
        {
            Assert.True(Parse(Spec(), "--auto-restart").GetSwitch("auto-restart", "no-auto-restart"));
            Assert.False(Parse(Spec(), "--no-auto-restart").GetSwitch("auto-restart", "no-auto-restart"));
        }

        [Fact]
        public void GetSwitch_Conflict_Throws()
        {
            var parsed = Parse(Spec(), "--auto-restart", "--no-auto-restart");

            var ex = Assert.Throws<UsageException>(() => parsed.GetSwitch("auto-restart", "no-auto-restart"));

            Assert.Equal(ExitCodes.Usage, ex.ExitCode);
            Assert.Contains("--auto-restart", ex.Message);
            Assert.Contains("--no-auto-restart", ex.Message);
        }
    }
}
