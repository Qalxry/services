using ServicesApp;
using Windows.UI;
using Xunit;

namespace Services.Tests
{
    public class AnsiParserTests
    {
        private const string Esc = "\u001b";

        private static readonly Color[] Palette = AnsiParser.DarkPalette;

        private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);

        private static List<AnsiParser.Segment> Parse(string text) => AnsiParser.Parse(text, Palette);

        private static void AssertSegment(AnsiParser.Segment segment, string text, Color? foreground = null,
            bool bold = false, bool italic = false, bool underline = false)
        {
            Assert.Equal(text, segment.Text);
            Assert.Equal(foreground, segment.Foreground);
            Assert.Equal(bold, segment.Bold);
            Assert.Equal(italic, segment.Italic);
            Assert.Equal(underline, segment.Underline);
        }

        [Fact]
        public void Parse_NoEscape_ReturnsSingleDefaultSegment()
        {
            var segments = Parse("hello world");

            var segment = Assert.Single(segments);
            AssertSegment(segment, "hello world");
        }

        [Fact]
        public void Parse_EmptyString_ReturnsNoSegments()
        {
            Assert.Empty(Parse(string.Empty));
        }

        [Fact]
        public void Parse_OnlyEscapeSequences_ReturnsNoSegments()
        {
            Assert.Empty(Parse(Esc + "[31m" + Esc + "[0m"));
        }

        [Theory]
        [InlineData(30, 0)]
        [InlineData(31, 1)]
        [InlineData(32, 2)]
        [InlineData(33, 3)]
        [InlineData(34, 4)]
        [InlineData(35, 5)]
        [InlineData(36, 6)]
        [InlineData(37, 7)]
        public void Parse_StandardForeground_UsesPaletteIndex(int code, int index)
        {
            var segments = Parse($"{Esc}[{code}mtext{Esc}[0m");

            var segment = Assert.Single(segments);
            AssertSegment(segment, "text", Palette[index]);
        }

        [Theory]
        [InlineData(90, 8)]
        [InlineData(91, 9)]
        [InlineData(97, 15)]
        public void Parse_BrightForeground_UsesUpperPalette(int code, int index)
        {
            var segment = Assert.Single(Parse($"{Esc}[{code}mtext"));

            AssertSegment(segment, "text", Palette[index]);
        }

        [Fact]
        public void Parse_UsesGivenPalette()
        {
            var segment = Assert.Single(AnsiParser.Parse(Esc + "[31mx", AnsiParser.LightPalette));

            AssertSegment(segment, "x", AnsiParser.LightPalette[1]);
        }

        [Fact]
        public void Parse_ResetEndsStyle()
        {
            var segments = Parse($"{Esc}[1;31mA{Esc}[0mB");

            Assert.Equal(2, segments.Count);
            AssertSegment(segments[0], "A", Palette[1], bold: true);
            AssertSegment(segments[1], "B");
        }

        [Fact]
        public void Parse_EmptySgrParameters_ActAsReset()
        {
            var segments = Parse($"{Esc}[31mA{Esc}[mB");

            Assert.Equal(2, segments.Count);
            AssertSegment(segments[0], "A", Palette[1]);
            AssertSegment(segments[1], "B");
        }

        [Fact]
        public void Parse_BoldItalicUnderline_CanBeEnabled()
        {
            var segment = Assert.Single(Parse($"{Esc}[1;3;4mx"));

            AssertSegment(segment, "x", bold: true, italic: true, underline: true);
        }

        [Fact]
        public void Parse_BoldItalicUnderline_CanBeDisabledIndividually()
        {
            var segments = Parse($"{Esc}[1;3;4mA{Esc}[22mB{Esc}[23mC{Esc}[24mD");

            Assert.Equal(4, segments.Count);
            AssertSegment(segments[0], "A", bold: true, italic: true, underline: true);
            AssertSegment(segments[1], "B", italic: true, underline: true);
            AssertSegment(segments[2], "C", underline: true);
            AssertSegment(segments[3], "D");
        }

        [Fact]
        public void Parse_Code39_ResetsForegroundOnly()
        {
            var segments = Parse($"{Esc}[1;32mA{Esc}[39mB");

            Assert.Equal(2, segments.Count);
            AssertSegment(segments[0], "A", Palette[2], bold: true);
            AssertSegment(segments[1], "B", bold: true);
        }

        [Fact]
        public void Parse_StyleChange_SplitsSegments()
        {
            var segments = Parse($"plain {Esc}[31mred{Esc}[32mgreen");

            Assert.Equal(3, segments.Count);
            AssertSegment(segments[0], "plain ");
            AssertSegment(segments[1], "red", Palette[1]);
            AssertSegment(segments[2], "green", Palette[2]);
        }

        [Fact]
        public void Parse_Color256_StandardIndexUsesPalette()
        {
            var segment = Assert.Single(Parse($"{Esc}[38;5;1mx"));
            AssertSegment(segment, "x", Palette[1]);

            segment = Assert.Single(Parse($"{Esc}[38;5;15mx"));
            AssertSegment(segment, "x", Palette[15]);
        }

        [Theory]
        [InlineData(16, 0, 0, 0)]
        [InlineData(21, 0, 0, 255)]
        [InlineData(196, 255, 0, 0)]
        [InlineData(208, 255, 135, 0)]
        [InlineData(231, 255, 255, 255)]
        public void Parse_Color256_ColorCube(int n, byte r, byte g, byte b)
        {
            var segment = Assert.Single(Parse($"{Esc}[38;5;{n}mx"));

            AssertSegment(segment, "x", Rgb(r, g, b));
        }

        [Theory]
        [InlineData(232, 8)]
        [InlineData(244, 128)]
        [InlineData(255, 238)]
        public void Parse_Color256_Grayscale(int n, byte level)
        {
            var segment = Assert.Single(Parse($"{Esc}[38;5;{n}mx"));

            AssertSegment(segment, "x", Rgb(level, level, level));
        }

        [Fact]
        public void Parse_Color256_OutOfRange_LeavesForegroundUnchanged()
        {
            var segment = Assert.Single(Parse($"{Esc}[38;5;300mx"));

            AssertSegment(segment, "x");
        }

        [Fact]
        public void Parse_TrueColor_UsesRgbValues()
        {
            var segment = Assert.Single(Parse($"{Esc}[38;2;10;20;30mx"));

            AssertSegment(segment, "x", Rgb(10, 20, 30));
        }

        [Fact]
        public void Parse_ExtendedColor_FollowedByOtherCodes_AreAllApplied()
        {
            var segment = Assert.Single(Parse($"{Esc}[1;38;2;1;2;3;4mx"));

            AssertSegment(segment, "x", Rgb(1, 2, 3), bold: true, underline: true);
        }

        [Fact]
        public void Parse_ColonSeparatedParameters_AreSupported()
        {
            var segment = Assert.Single(Parse($"{Esc}[38:5:196mx"));

            AssertSegment(segment, "x", Rgb(255, 0, 0));
        }

        [Fact]
        public void Parse_TruncatedExtendedColor_DoesNotThrow()
        {
            var segment = Assert.Single(Parse($"{Esc}[38;5mx"));
            AssertSegment(segment, "x");

            // 截断的真彩色：残余参数属于扩展颜色，不应被解释为普通样式码（1 = 粗体）
            segment = Assert.Single(Parse($"{Esc}[38;2;1;2mx"));
            AssertSegment(segment, "x");
        }

        [Fact]
        public void Parse_TrueColorOutOfRange_IgnoredWithoutAffectingFollowingCodes()
        {
            var segment = Assert.Single(Parse($"{Esc}[38;2;300;0;0;4mx"));

            AssertSegment(segment, "x", underline: true);
        }

        [Fact]
        public void Parse_Code21_IsDoubleUnderlineShownAsUnderline()
        {
            var segment = Assert.Single(Parse($"{Esc}[1;21mx"));

            AssertSegment(segment, "x", bold: true, underline: true);
        }

        [Theory]
        [InlineData(40)]
        [InlineData(47)]
        [InlineData(100)]
        [InlineData(107)]
        public void Parse_BackgroundColor_IsIgnored(int code)
        {
            var segment = Assert.Single(Parse($"{Esc}[{code}mx"));

            AssertSegment(segment, "x");
        }

        [Fact]
        public void Parse_ExtendedBackgroundColor_IsIgnoredAndDoesNotAffectForeground()
        {
            var segment = Assert.Single(Parse($"{Esc}[31;48;5;21mx"));
            AssertSegment(segment, "x", Palette[1]);

            segment = Assert.Single(Parse($"{Esc}[48;2;1;2;3mx"));
            AssertSegment(segment, "x");
        }

        [Fact]
        public void Parse_NonSgrCsi_IsRemoved()
        {
            var segment = Assert.Single(Parse($"a{Esc}[2Kb{Esc}[10Ac{Esc}[2;5Hd{Esc}[?25le"));

            AssertSegment(segment, "abcde");
        }

        [Fact]
        public void Parse_NonSgrCsi_DoesNotBreakCurrentStyle()
        {
            var segment = Assert.Single(Parse($"{Esc}[31ma{Esc}[2Kb"));

            AssertSegment(segment, "ab", Palette[1]);
        }

        [Fact]
        public void Parse_OscSequences_AreRemoved()
        {
            var segment = Assert.Single(Parse($"{Esc}]0;title\u0007text"));
            AssertSegment(segment, "text");

            segment = Assert.Single(Parse($"a{Esc}]8;;http://x{Esc}\\b"));
            AssertSegment(segment, "ab");
        }

        [Fact]
        public void Parse_TwoCharEscape_IsRemoved()
        {
            var segment = Assert.Single(Parse($"a{Esc}Mb"));

            AssertSegment(segment, "ab");
        }

        [Fact]
        public void Parse_IncompleteSequenceAtLineEnd_IsDropped()
        {
            AssertSegment(Assert.Single(Parse($"abc{Esc}")), "abc");
            AssertSegment(Assert.Single(Parse($"abc{Esc}[")), "abc");
            AssertSegment(Assert.Single(Parse($"abc{Esc}[31")), "abc");
            AssertSegment(Assert.Single(Parse($"abc{Esc}]0;title")), "abc");
        }

        [Fact]
        public void Parse_UnknownSgrCode_IsIgnored()
        {
            var segment = Assert.Single(Parse($"{Esc}[31;5;7mx"));

            AssertSegment(segment, "x", Palette[1]);
        }
    }
}
