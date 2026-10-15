using Services.Core.Helpers;
using Services.Core.Models;
using Services.Core.Presentation;
using Xunit;

namespace Services.Tests
{
    public class ServiceStartupModesTests
    {
        public static IEnumerable<object[]> AllModes() => new[]
        {
            new object[] { ServiceStartupType.Auto, false, 0, "auto", "开机自启" },
            new object[] { ServiceStartupType.Auto, true, 1, "delayed-auto", "延迟自动启动" },
            new object[] { ServiceStartupType.Manual, false, 2, "manual", "手动启动" },
            new object[] { ServiceStartupType.Disabled, false, 3, "disabled", "已禁用" },
        };

        [Theory]
        [MemberData(nameof(AllModes))]
        public void ToIndex_And_FromIndex_AreInverse(ServiceStartupType type, bool delayed, int index, string cli, string display)
        {
            Assert.Equal(index, ServiceStartupModes.ToIndex(type, delayed));
            Assert.Equal((type, delayed), ServiceStartupModes.FromIndex(index));
        }

        [Theory]
        [MemberData(nameof(AllModes))]
        public void DisplayNames_MatchIndex(ServiceStartupType type, bool delayed, int index, string cli, string display)
        {
            Assert.Equal(display, ServiceText.StartupModeNames[index]);
            Assert.Equal(display, ServiceText.StartupMode(type, delayed));
        }

        [Fact]
        public void DisplayNames_HasOneEntryPerMode()
        {
            Assert.Equal(4, ServiceText.StartupModeNames.Length);
        }

        [Theory]
        [MemberData(nameof(AllModes))]
        public void CliValue_RoundTripsThroughTryParse(ServiceStartupType type, bool delayed, int index, string cli, string display)
        {
            Assert.Equal(cli, ServiceStartupModes.ToCliValue(type, delayed));

            Assert.True(ServiceStartupModes.TryParse(cli, out var parsedType, out var parsedDelayed));
            Assert.Equal(type, parsedType);
            Assert.Equal(delayed, parsedDelayed);
        }

        [Theory]
        [InlineData("AUTO", ServiceStartupType.Auto, false)]
        [InlineData(" Delayed-Auto ", ServiceStartupType.Auto, true)]
        [InlineData("Manual", ServiceStartupType.Manual, false)]
        public void TryParse_IsCaseInsensitiveAndTrims(string text, ServiceStartupType type, bool delayed)
        {
            Assert.True(ServiceStartupModes.TryParse(text, out var parsedType, out var parsedDelayed));
            Assert.Equal(type, parsedType);
            Assert.Equal(delayed, parsedDelayed);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("delayed")]
        [InlineData("boot")]
        public void TryParse_RejectsUnknownValues(string? text)
        {
            Assert.False(ServiceStartupModes.TryParse(text, out _, out _));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(4)]
        [InlineData(99)]
        public void FromIndex_InvalidIndexFallsBackToPlainAuto(int index)
        {
            Assert.Equal((ServiceStartupType.Auto, false), ServiceStartupModes.FromIndex(index));
        }

        [Fact]
        public void DelayedFlagIsIgnoredForNonAutoStartTypes()
        {
            Assert.False(ServiceStartupModes.NormalizeDelayed(ServiceStartupType.Manual, true));
            Assert.False(ServiceStartupModes.NormalizeDelayed(ServiceStartupType.Disabled, true));
            Assert.True(ServiceStartupModes.NormalizeDelayed(ServiceStartupType.Auto, true));
            Assert.False(ServiceStartupModes.NormalizeDelayed(ServiceStartupType.Auto, false));

            Assert.Equal(2, ServiceStartupModes.ToIndex(ServiceStartupType.Manual, true));
            Assert.Equal("manual", ServiceStartupModes.ToCliValue(ServiceStartupType.Manual, true));
        }

        [Fact]
        public void ServiceStartupTypeValuesStayEqualToScmStartTypes()
        {
            // 启动类型数值直接写入 SCM（2 自动 / 3 手动 / 4 禁用），延迟自动启动用独立标志，不能改动枚举
            Assert.Equal(2, (int)ServiceStartupType.Auto);
            Assert.Equal(3, (int)ServiceStartupType.Manual);
            Assert.Equal(4, (int)ServiceStartupType.Disabled);
        }
    }
}
