using Services.Core.Helpers;
using Xunit;

namespace Services.Tests
{
    public class ServiceDependenciesTests
    {
        [Fact]
        public void Normalize_TrimsDropsEmptyAndDeduplicatesIgnoringCase()
        {
            var result = ServiceDependencies.Normalize(new[] { " Tcpip ", "", "  ", "MySQL80", "tcpip", null, "MYSQL80", "Dnscache" });

            Assert.Equal(new[] { "Tcpip", "MySQL80", "Dnscache" }, result);
        }

        [Fact]
        public void Normalize_NullReturnsNewEmptyList()
        {
            var first = ServiceDependencies.Normalize(null);
            var second = ServiceDependencies.Normalize(null);

            Assert.Empty(first);
            Assert.NotSame(first, second);
        }

        [Fact]
        public void ParseText_AcceptsCommaSemicolonAndNewlineSeparators()
        {
            var result = ServiceDependencies.ParseText("Tcpip, MySQL80；Dnscache\r\nW32Time\n\nTcpip，LanmanServer");

            Assert.Equal(new[] { "Tcpip", "MySQL80", "Dnscache", "W32Time", "LanmanServer" }, result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" , ;\r\n")]
        public void ParseText_BlankReturnsEmpty(string? text)
        {
            Assert.Empty(ServiceDependencies.ParseText(text));
        }

        [Fact]
        public void Format_JoinsWithCommaAndSpace_EmptyForNoDependencies()
        {
            Assert.Equal("Tcpip, MySQL80", ServiceDependencies.Format(new[] { "Tcpip", "MySQL80" }));
            Assert.Equal("", ServiceDependencies.Format(new List<string>()));
            Assert.Equal("", ServiceDependencies.Format(null));
        }

        [Fact]
        public void Format_ThenParseText_RoundTrips()
        {
            var names = new[] { "Tcpip", "MySQL80", "WinSvcMgr_Api_1a2b3c4d" };

            Assert.Equal(names, ServiceDependencies.ParseText(ServiceDependencies.Format(names)));
        }

        [Fact]
        public void BuildMultiString_EachNameEndsWithNul()
        {
            // 封送字符串时会再追加一个终止符，因此整体以双 NUL 结束
            Assert.Equal("Tcpip\0MySQL80\0", ServiceDependencies.BuildMultiString(new[] { "Tcpip", "MySQL80" }));
            Assert.Equal("Tcpip\0", ServiceDependencies.BuildMultiString(new[] { "Tcpip" }));
        }

        [Fact]
        public void BuildMultiString_NormalizesAndEmptyMeansClear()
        {
            Assert.Equal("Tcpip\0", ServiceDependencies.BuildMultiString(new[] { " Tcpip", "TCPIP", "" }));
            Assert.Equal("", ServiceDependencies.BuildMultiString(new List<string>()));
            Assert.Equal("", ServiceDependencies.BuildMultiString(null));
        }

        [Fact]
        public void ParseRegistry_SkipsGroupsAndNormalizes()
        {
            var result = ServiceDependencies.ParseRegistry(new[] { "Tcpip", "+NetworkProvider", "", "tcpip", "MySQL80" });

            Assert.Equal(new[] { "Tcpip", "MySQL80" }, result);
            Assert.Empty(ServiceDependencies.ParseRegistry(null));
        }

        [Fact]
        public void Clone_ReturnsIndependentList()
        {
            var source = new List<string> { "Tcpip" };

            var copy = ServiceDependencies.Clone(source);
            copy.Add("MySQL80");

            Assert.NotSame(source, copy);
            Assert.Single(source);
            Assert.Empty(ServiceDependencies.Clone(null));
        }

        [Fact]
        public void Validate_AcceptsOrdinaryServiceNames()
        {
            ServiceDependencies.Validate(new[] { "Tcpip", "MySQL80", "WinSvcMgr_Api_1a2b3c4d", "Windows Time" });
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("a\0b")]
        [InlineData("a/b")]
        [InlineData(@"a\b")]
        [InlineData("a\tb")]
        [InlineData("+NetworkProvider")]
        public void Validate_RejectsInvalidNames(string name)
        {
            Assert.Throws<ArgumentException>(() => ServiceDependencies.Validate(new[] { name }));
        }

        [Fact]
        public void Validate_RejectsOverlongName()
        {
            var name = new string('a', ServiceDependencies.MaxNameLength + 1);

            Assert.Throws<ArgumentException>(() => ServiceDependencies.Validate(new[] { name }));
            ServiceDependencies.Validate(new[] { new string('a', ServiceDependencies.MaxNameLength) });
        }

        [Fact]
        public void Validate_RejectsSelfDependencyIgnoringCase()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                ServiceDependencies.Validate(new[] { "Tcpip", "winsvcmgr_api_1a2b3c4d" }, "WinSvcMgr_Api_1A2B3C4D"));

            Assert.Contains("自身", ex.Message);
            // 不提供自身 ID（创建时）则不检查
            ServiceDependencies.Validate(new[] { "winsvcmgr_api_1a2b3c4d" });
        }

        [Fact]
        public void Validate_MessageNamesTheOffendingEntry()
        {
            var ex = Assert.Throws<ArgumentException>(() => ServiceDependencies.Validate(new[] { "Tcpip", "bad/name" }));

            Assert.Contains("bad/name", ex.Message);
        }

        [Fact]
        public void EnsureExist_ListsAllMissingServices()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Tcpip" };

            var ex = Assert.Throws<ArgumentException>(() =>
                ServiceDependencies.EnsureExist(new[] { "Tcpip", "NoSuch1", "NoSuch2" }, existing.Contains));

            Assert.Contains("NoSuch1、NoSuch2", ex.Message);
        }

        [Fact]
        public void EnsureExist_AllPresentOrNoneRequested_DoesNotThrow()
        {
            ServiceDependencies.EnsureExist(new[] { "Tcpip" }, _ => true);
            ServiceDependencies.EnsureExist(Array.Empty<string>(), _ => false);
        }

        [Fact]
        public void SameSet_IgnoresOrderAndCase()
        {
            Assert.True(ServiceDependencies.SameSet(new[] { "Tcpip", "MySQL80" }, new[] { "mysql80", "TCPIP" }));
            Assert.True(ServiceDependencies.SameSet(null, new List<string>()));
            Assert.False(ServiceDependencies.SameSet(new[] { "Tcpip" }, new[] { "Tcpip", "MySQL80" }));
            Assert.False(ServiceDependencies.SameSet(new[] { "Tcpip" }, new[] { "Dnscache" }));
        }
    }
}
