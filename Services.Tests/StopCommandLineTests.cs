using Services.Core.Helpers;
using Xunit;

namespace Services.Tests
{
    public class StopCommandLineTests
    {
        [Fact]
        public void Expand_QuotesExeAndWorkdir()
        {
            var result = StopCommandLine.Expand("{exe} -p {workdir} -s quit", @"C:\Program Files\nginx\nginx.exe", @"C:\Program Files\nginx", 1234);

            Assert.Equal(@"""C:\Program Files\nginx\nginx.exe"" -p ""C:\Program Files\nginx"" -s quit", result);
        }

        [Fact]
        public void Expand_ReplacesPid()
        {
            var result = StopCommandLine.Expand("taskkill /pid {pid}", @"C:\a.exe", @"C:\", 4321);

            Assert.Equal("taskkill /pid 4321", result);
        }

        [Fact]
        public void Expand_PlaceholdersAreCaseInsensitive()
        {
            var result = StopCommandLine.Expand("{EXE} {Pid}", @"C:\a.exe", @"C:\", 7);

            Assert.Equal(@"""C:\a.exe"" 7", result);
        }

        [Fact]
        public void Expand_DoesNotDoubleQuoteAlreadyQuotedPath()
        {
            var result = StopCommandLine.Expand("{exe}", @"""C:\a b\x.exe""", @"C:\", 1);

            Assert.Equal(@"""C:\a b\x.exe""", result);
        }

        [Fact]
        public void Expand_CommandWithoutPlaceholders_IsTrimmedOnly()
        {
            var result = StopCommandLine.Expand("  redis-cli shutdown  ", @"C:\a.exe", @"C:\", 1);

            Assert.Equal("redis-cli shutdown", result);
        }

        [Fact]
        public void ToCmdArguments_WrapsCommandForCmdSlashS()
        {
            var args = StopCommandLine.ToCmdArguments(@"""C:\a b\nginx.exe"" -s quit");

            Assert.Equal(@"/d /s /c """"C:\a b\nginx.exe"" -s quit""", args);
        }
    }
}
