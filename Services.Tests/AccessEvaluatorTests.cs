using Services.Core.Diagnostics;
using Xunit;

namespace Services.Tests
{
    public class AccessEvaluatorTests
    {
        private const string Account = "S-1-5-80-123-456";
        private const int Read = 0x1;
        private const int Execute = 0x20;
        private const int Write = 0x2;

        private static IReadOnlyCollection<string> Sids => AccessEvaluator.CandidateSids(Account);

        [Fact]
        public void GrantedThroughUsersGroup()
        {
            var aces = new[] { new AceInfo(AccessEvaluator.Users, Read | Execute, true) };
            Assert.Equal(AccessVerdict.Granted, AccessEvaluator.Evaluate(aces, Sids, Read | Execute));
        }

        [Fact]
        public void RightsCombineAcrossEntries()
        {
            var aces = new[]
            {
                new AceInfo(Account, Read, true),
                new AceInfo(AccessEvaluator.AuthenticatedUsers, Execute, true)
            };
            Assert.Equal(AccessVerdict.Granted, AccessEvaluator.Evaluate(aces, Sids, Read | Execute));
        }

        [Fact]
        public void DenyWins()
        {
            var aces = new[]
            {
                new AceInfo(AccessEvaluator.Users, Read | Execute | Write, true),
                new AceInfo(Account, Write, false)
            };
            Assert.Equal(AccessVerdict.Denied, AccessEvaluator.Evaluate(aces, Sids, Write));
            Assert.Equal(AccessVerdict.Granted, AccessEvaluator.Evaluate(aces, Sids, Read));
        }

        [Fact]
        public void OnlyAdministratorsMeansNotGranted()
        {
            var aces = new[]
            {
                new AceInfo(AccessEvaluator.Administrators, 0x1F01FF, true),
                new AceInfo(AccessEvaluator.LocalSystem, 0x1F01FF, true)
            };
            Assert.Equal(AccessVerdict.NotGranted, AccessEvaluator.Evaluate(aces, Sids, Read));
        }

        [Fact]
        public void PartialRightsAreNotEnough()
        {
            var aces = new[] { new AceInfo(Account, Read, true) };
            Assert.Equal(AccessVerdict.NotGranted, AccessEvaluator.Evaluate(aces, Sids, Read | Write));
        }

        [Fact]
        public void VirtualAccountsIncludeAllServices()
        {
            Assert.Contains(AccessEvaluator.AllServices, AccessEvaluator.CandidateSids("S-1-5-80-1-2"));
            Assert.DoesNotContain(AccessEvaluator.AllServices, AccessEvaluator.CandidateSids(AccessEvaluator.LocalService));
            var aces = new[] { new AceInfo(AccessEvaluator.AllServices, Read, true) };
            Assert.Equal(AccessVerdict.Granted, AccessEvaluator.Evaluate(aces, AccessEvaluator.CandidateSids("S-1-5-80-1-2"), Read));
        }

        [Fact]
        public void SidComparisonIgnoresCase()
        {
            var aces = new[] { new AceInfo("s-1-5-32-545", Read, true) };
            Assert.Equal(AccessVerdict.Granted, AccessEvaluator.Evaluate(aces, Sids, Read));
        }
    }
}
