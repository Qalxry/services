using Services.Core.Helpers;
using Services.Core.Models;
using Xunit;

namespace Services.Tests
{
    public class ServiceAccountsTests
    {
        [Theory]
        [InlineData(null, ServiceAccountType.LocalSystem)]
        [InlineData("", ServiceAccountType.LocalSystem)]
        [InlineData("LocalSystem", ServiceAccountType.LocalSystem)]
        [InlineData("localsystem", ServiceAccountType.LocalSystem)]
        [InlineData(@".\LocalSystem", ServiceAccountType.LocalSystem)]
        [InlineData(@"NT AUTHORITY\LocalService", ServiceAccountType.LocalService)]
        [InlineData(@"nt authority\localservice", ServiceAccountType.LocalService)]
        [InlineData(@"NT AUTHORITY\NetworkService", ServiceAccountType.NetworkService)]
        [InlineData(@"NT SERVICE\WinSvcMgr_App_1234abcd", ServiceAccountType.Virtual)]
        [InlineData(@".\svcuser", ServiceAccountType.User)]
        [InlineData(@"CORP\svcuser", ServiceAccountType.User)]
        [InlineData("svcuser@corp.example", ServiceAccountType.User)]
        public void ParseObjectName_DetectsAccountType(string? objectName, ServiceAccountType expected)
        {
            Assert.Equal(expected, ServiceAccounts.ParseObjectName(objectName));
        }

        [Theory]
        [InlineData(ServiceAccountType.LocalSystem, "LocalSystem")]
        [InlineData(ServiceAccountType.LocalService, @"NT AUTHORITY\LocalService")]
        [InlineData(ServiceAccountType.NetworkService, @"NT AUTHORITY\NetworkService")]
        [InlineData(ServiceAccountType.Virtual, @"NT SERVICE\WinSvcMgr_App_1234abcd")]
        public void ToObjectName_BuiltInTypes_IgnoreUserName(ServiceAccountType type, string expected)
        {
            Assert.Equal(expected, ServiceAccounts.ToObjectName(type, "WinSvcMgr_App_1234abcd", @".\ignored"));
        }

        [Fact]
        public void ToObjectName_User_UsesTrimmedUserName()
        {
            Assert.Equal(@".\svcuser", ServiceAccounts.ToObjectName(ServiceAccountType.User, "id", @"  .\svcuser "));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("  ")]
        public void ToObjectName_UserWithoutName_Throws(string? userName)
        {
            Assert.Throws<ArgumentException>(() => ServiceAccounts.ToObjectName(ServiceAccountType.User, "id", userName));
        }

        [Theory]
        [InlineData(ServiceAccountType.LocalSystem)]
        [InlineData(ServiceAccountType.LocalService)]
        [InlineData(ServiceAccountType.NetworkService)]
        [InlineData(ServiceAccountType.Virtual)]
        [InlineData(ServiceAccountType.User)]
        public void ToObjectName_RoundTripsThroughParse(ServiceAccountType type)
        {
            var objectName = ServiceAccounts.ToObjectName(type, "WinSvcMgr_App_1234abcd", @".\svcuser");

            Assert.Equal(type, ServiceAccounts.ParseObjectName(objectName));
        }
    }
}
