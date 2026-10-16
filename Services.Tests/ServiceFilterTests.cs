using Services.Core.Models;
using Services.Core.Presentation;
using Xunit;

namespace Services.Tests
{
    public class ServiceFilterTests
    {
        private static Service Make(string name, ServiceRunState state, string exe = @"C:\Apps\app.exe", string? description = null) => new()
        {
            Id = $"WinSvcMgr_{name}_1234",
            Name = name,
            State = state,
            ExePath = exe,
            Description = description
        };

        [Fact]
        public void NoFilterMatchesEverything()
        {
            Assert.False(ServiceFilter.None.IsActive);
            Assert.True(ServiceFilter.None.Matches(Make("A", ServiceRunState.Unknown)));
        }

        [Theory]
        [InlineData("api", true)]
        [InlineData("API", true)]
        [InlineData("winsvcmgr_api", true)]
        [InlineData("nginx.exe", true)]
        [InlineData("gateway", true)]
        [InlineData("api nginx", true)]
        [InlineData("api redis", false)]
        [InlineData("redis", false)]
        public void QueryMatchesNameIdPathAndDescription(string query, bool expected)
        {
            var service = Make("Api", ServiceRunState.Running, @"C:\nginx\nginx.exe", "public gateway");
            Assert.Equal(expected, new ServiceFilter(query, StateFilter.All).Matches(service));
        }

        [Theory]
        [InlineData(StateFilter.Running, ServiceRunState.Running, true)]
        [InlineData(StateFilter.Running, ServiceRunState.Stopped, false)]
        [InlineData(StateFilter.Stopped, ServiceRunState.Stopped, true)]
        [InlineData(StateFilter.Other, ServiceRunState.StartPending, true)]
        [InlineData(StateFilter.Other, ServiceRunState.Unknown, true)]
        [InlineData(StateFilter.Other, ServiceRunState.Running, false)]
        public void StateFilterWorks(StateFilter filter, ServiceRunState state, bool expected)
        {
            Assert.Equal(expected, new ServiceFilter(null, filter).Matches(Make("A", state)));
        }

        [Fact]
        public void QueryAndStateCombine()
        {
            var filter = new ServiceFilter("api", StateFilter.Running);
            Assert.True(filter.IsActive);
            Assert.True(filter.Matches(Make("Api", ServiceRunState.Running)));
            Assert.False(filter.Matches(Make("Api", ServiceRunState.Stopped)));
            Assert.False(filter.Matches(Make("Web", ServiceRunState.Running)));
        }
    }
}
