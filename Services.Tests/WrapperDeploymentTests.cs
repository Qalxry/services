using Services.Core.Helpers;
using Services.Core.Wrapper;
using Xunit;

namespace Services.Tests
{
    public class WrapperDeploymentTests
    {
        [Theory]
        [InlineData("\"C:\\Program Files\\ServicesApp\\servicesvc.exe\" --service-wrapper \"Svc\"", "C:\\Program Files\\ServicesApp\\servicesvc.exe")]
        [InlineData("  \"C:\\a\\ServicesApp.exe\" --service-wrapper \"Svc\"  ", "C:\\a\\ServicesApp.exe")]
        [InlineData("C:\\a\\servicesvc.exe --service-wrapper Svc", "C:\\a\\servicesvc.exe")]
        public void ParsesWrapperExecutable(string imagePath, string expected)
        {
            Assert.Equal(expected, ServiceUtils.ParseWrapperExecutable(imagePath));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("C:\\Windows\\system32\\svchost.exe -k netsvcs")]
        [InlineData("\"C:\\Program Files\\Other\\app.exe\" --run")]
        [InlineData("\" --service-wrapper")]
        public void IgnoresOtherServices(string? imagePath)
        {
            Assert.Null(ServiceUtils.ParseWrapperExecutable(imagePath));
        }

        [Fact]
        public void BuildAndParseRoundTrip()
        {
            var path = @"C:\ProgramData\ServicesApp\wrapper\1.2.0.0-abcdef012345\servicesvc.exe";
            Assert.Equal(path, ServiceUtils.ParseWrapperExecutable(ServiceUtils.BuildWrapperCommand(path, "WinSvcMgr_x_1")));
        }

        [Theory]
        [InlineData("1.2.0.0", "ABCDEF0123456789", "1.2.0.0-abcdef012345")]
        [InlineData(null, "abcdef0123456789", "0.0.0.0-abcdef012345")]
        [InlineData("1.0 (dev)\\..", "abcdef0123456789", "1.0dev..-abcdef012345")]
        [InlineData("2.0.0", "abc", "2.0.0-abc")]
        public void VersionDirectoryName(string? version, string hash, string expected)
        {
            Assert.Equal(expected, WrapperDeployment.GetVersionDirectoryName(version, hash));
        }

        [Fact]
        public void VersionDirectoryNameHasNoPathSeparators()
        {
            var name = WrapperDeployment.GetVersionDirectoryName(@"..\..\evil/x", "0123456789abcdef");
            Assert.DoesNotContain('\\', name);
            Assert.DoesNotContain('/', name);
        }

        [Fact]
        public void SelectsOnlyUnreferencedDirectories()
        {
            var root = @"C:\ProgramData\ServicesApp\wrapper";
            var dirs = new[] { $@"{root}\1.0.0.0-aaa", $@"{root}\1.1.0.0-bbb", $@"{root}\1.2.0.0-ccc" };
            var referenced = new[]
            {
                $@"{root}\1.1.0.0-BBB\servicesvc.exe",
                @"C:\Program Files\ServicesApp\servicesvc.exe"
            };

            Assert.Equal(new[] { $@"{root}\1.0.0.0-aaa", $@"{root}\1.2.0.0-ccc" },
                WrapperDeployment.SelectUnused(dirs, referenced).ToArray());
        }

        [Fact]
        public void NothingReferencedSelectsAll()
        {
            var dirs = new[] { @"C:\w\a", @"C:\w\b" };
            Assert.Equal(dirs, WrapperDeployment.SelectUnused(dirs, Array.Empty<string>()).ToArray());
        }
    }
}
