using Services.Core.Configuration;
using Services.Core.Models;
using Xunit;

namespace Services.Tests
{
    public class ConfigFileTests
    {
        private static Service FullService() => new()
        {
            Id = "WinSvcMgr_Api_1234abcd",
            Name = "Api",
            Description = "接口服务",
            ExePath = @"C:\Apps\api.exe",
            Args = "--port 8080",
            WorkingDir = @"C:\Apps",
            StartupType = ServiceStartupType.Auto,
            DelayedAutoStart = true,
            AccountType = ServiceAccountType.Virtual,
            AccountName = @"NT SERVICE\WinSvcMgr_Api_1234abcd",
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["NODE_ENV"] = "production" },
            Dependencies = new List<string> { "Tcpip" },
            AutoRestart = true,
            RestartDelaySeconds = 7,
            MaxRestarts = 0,
            RestartResetMinutes = 30,
            RestartBackoff = false,
            StopMode = ServiceStopMode.Command,
            StopCommand = "{exe} -s quit",
            StopTimeoutSeconds = 60
        };

        private static ServiceDefinition Minimal(string name = "App") => new() { Name = name, Exe = @"C:\Apps\app.exe" };

        /// <summary>
        /// 模拟已保存的服务：保存时未指定的工作目录会被补为程序所在目录（与 WriteParameters 一致）
        /// </summary>
        private static Service Existing(string id, string name) => new()
        {
            Id = id,
            Name = name,
            ExePath = @"C:\Apps\app.exe",
            WorkingDir = Path.GetDirectoryName(@"C:\Apps\app.exe")
        };

        [Fact]
        public void ExportThenImport_HasNoDifferences()
        {
            var service = FullService();

            var definition = ConfigFileMapper.ToDefinition(service);
            var config = ConfigFileMapper.ToConfig(definition, "services[0]");

            Assert.Empty(ConfigFileMapper.Diff(ServiceConfig.FromService(service), config));
        }

        [Fact]
        public void SerializeThenDeserialize_RoundTrips()
        {
            var file = new ConfigFile { Services = { ConfigFileMapper.ToDefinition(FullService()) } };

            var json = ConfigFileMapper.Serialize(file);
            var parsed = ConfigFileMapper.Deserialize(json);
            var config = ConfigFileMapper.ToConfig(Assert.Single(parsed.Services), "services[0]");

            Assert.Empty(ConfigFileMapper.Diff(ServiceConfig.FromService(FullService()), config));
            Assert.Contains("\"startup\": \"delayed-auto\"", json);
            Assert.Contains("\"account\": \"virtual\"", json);
            Assert.Contains("接口服务", json);
        }

        [Fact]
        public void Deserialize_AllowsCommentsAndTrailingCommas()
        {
            var json = """
                {
                  // 注释
                  "version": 1,
                  "services": [ { "name": "A", "exe": "C:\\a.exe", }, ],
                }
                """;

            var file = ConfigFileMapper.Deserialize(json);

            Assert.Equal("A", Assert.Single(file.Services).Name);
        }

        [Fact]
        public void Deserialize_UnknownField_ThrowsConfigFileExceptionWithPath()
        {
            var json = """{ "version": 1, "services": [ { "name": "A", "exe": "C:\\a.exe", "exePath": "x" } ] }""";

            var ex = Assert.Throws<ConfigFileException>(() => ConfigFileMapper.Deserialize(json));

            Assert.Contains("exePath", ex.Message);
        }

        [Fact]
        public void Deserialize_InvalidJson_ThrowsConfigFileException()
        {
            Assert.Throws<ConfigFileException>(() => ConfigFileMapper.Deserialize("{ not json"));
        }

        [Fact]
        public void ToConfig_MinimalEntry_UsesCreateDefaults()
        {
            var config = ConfigFileMapper.ToConfig(Minimal(), "services[0]");

            Assert.Equal(ServiceStartupType.Auto, config.StartupType);
            Assert.False(config.DelayedAutoStart);
            Assert.Equal(ServiceAccountType.LocalSystem, config.AccountType);
            Assert.False(config.AutoRestart);
            Assert.Equal(RestartPolicyDefaults.DelaySeconds, config.RestartDelaySeconds);
            Assert.Equal(RestartPolicyDefaults.MaxRestarts, config.MaxRestarts);
            Assert.Equal(ServiceStopMode.Kill, config.StopMode);
            Assert.Equal(StopPolicyDefaults.TimeoutSeconds, config.StopTimeoutSeconds);
            Assert.Empty(config.Environment);
            Assert.Empty(config.Dependencies);
        }

        [Fact]
        public void ToConfig_OmittedWorkingDir_DefaultsToExeDirectory()
        {
            // 与保存时（WriteParameters）的处理一致，避免 apply 每次都把 workingDir 判定为有差异。
            // 期望值同样经 Path.GetDirectoryName 计算：在 Windows（CI）上为 C:\Apps
            var config = ConfigFileMapper.ToConfig(Minimal(), "services[0]");

            Assert.Equal(Path.GetDirectoryName(@"C:\Apps\app.exe"), config.WorkingDir);
        }

        [Theory]
        [InlineData(null, @"C:\a.exe", "services[3].name")]
        [InlineData("A", null, "services[3].exe")]
        public void ToConfig_MissingRequiredField_Throws(string? name, string? exe, string expectedPath)
        {
            var ex = Assert.Throws<ConfigFileException>(() => ConfigFileMapper.ToConfig(new ServiceDefinition { Name = name, Exe = exe }, "services[3]"));

            Assert.Contains(expectedPath, ex.Message);
        }

        [Fact]
        public void ToConfig_InvalidValues_ReportFieldPath()
        {
            var badStartup = Minimal();
            badStartup.Startup = "sometimes";
            Assert.Contains("services[0].startup", Assert.Throws<ConfigFileException>(() => ConfigFileMapper.ToConfig(badStartup, "services[0]")).Message);

            var badRange = Minimal();
            badRange.RestartPolicy = new RestartPolicyDefinition { DelaySeconds = 0 };
            Assert.Contains("restartPolicy.delaySeconds", Assert.Throws<ConfigFileException>(() => ConfigFileMapper.ToConfig(badRange, "services[0]")).Message);

            var missingCommand = Minimal();
            missingCommand.StopPolicy = new StopPolicyDefinition { Mode = "command" };
            Assert.Contains("stopPolicy.command", Assert.Throws<ConfigFileException>(() => ConfigFileMapper.ToConfig(missingCommand, "services[0]")).Message);

            var badEnv = Minimal();
            badEnv.Environment = new Dictionary<string, string> { ["A=B"] = "x" };
            Assert.Contains("services[0].environment", Assert.Throws<ConfigFileException>(() => ConfigFileMapper.ToConfig(badEnv, "services[0]")).Message);
        }

        [Fact]
        public void ToConfig_AccountKeywordsAndUserName()
        {
            var entry = Minimal();
            entry.Account = "NetworkService";
            Assert.Equal(ServiceAccountType.NetworkService, ConfigFileMapper.ToConfig(entry, "s").AccountType);

            entry.Account = @".\svcuser";
            var config = ConfigFileMapper.ToConfig(entry, "s");
            Assert.Equal(ServiceAccountType.User, config.AccountType);
            Assert.Equal(@".\svcuser", config.AccountName);
        }

        [Fact]
        public void Diff_ReportsChangedFields()
        {
            var current = ServiceConfig.FromService(FullService());
            var desired = ServiceConfig.FromService(FullService());
            desired.Args = "--port 9090";
            desired.Environment = new Dictionary<string, string> { ["NODE_ENV"] = "development" };
            desired.StopTimeoutSeconds = 10;

            Assert.Equal(new[] { "args", "environment", "stopPolicy" }, ConfigFileMapper.Diff(current, desired));
        }

        [Fact]
        public void Diff_IgnoresDependencyOrderAndCaseInsensitiveFields()
        {
            var current = ServiceConfig.FromService(FullService());
            current.Dependencies = new List<string> { "Tcpip", "MySQL80" };
            var desired = ServiceConfig.FromService(FullService());
            desired.Dependencies = new List<string> { "mysql80", "TCPIP" };
            desired.ExePath = desired.ExePath.ToUpperInvariant();
            desired.Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["node_env"] = "production" };

            Assert.Empty(ConfigFileMapper.Diff(current, desired));
        }

        private static Service WithArgs(Service service, string args)
        {
            service.Args = args;
            return service;
        }

        private static Service WithUser(Service service, string accountName)
        {
            service.AccountType = ServiceAccountType.User;
            service.AccountName = accountName;
            return service;
        }

        private static IReadOnlyList<ServiceConfig> Desired(params ServiceDefinition[] definitions) =>
            definitions.Select((d, i) => ConfigFileMapper.ToConfig(d, $"services[{i}]")).ToList();

        [Fact]
        public void Plan_CreatesUpdatesKeepsAndPrunes()
        {
            var existing = new List<Service>
            {
                Existing("id-a", "A"),
                WithArgs(Existing("id-b", "B"), "--old"),
                Existing("id-c", "C")
            };
            var b = Minimal("b");
            b.Args = "--new";

            var plan = ApplyPlanner.Build(Desired(Minimal("A"), b, Minimal("D")), existing, prune: true, fileExists: _ => true);

            Assert.Equal(new[] { "unchanged", "update", "create", "delete" }, plan.Select(p => p.Action));
            Assert.Equal("id-b", plan[1].Id);
            Assert.Equal(new[] { "name", "args" }, plan[1].Changes);
            Assert.Equal("C", plan[3].Name);
        }

        [Fact]
        public void Plan_WithoutPrune_KeepsUnlistedServices()
        {
            var existing = new List<Service> { Existing("id-c", "C") };

            var plan = ApplyPlanner.Build(Desired(Minimal("A")), existing, prune: false, fileExists: _ => true);

            Assert.Equal("create", Assert.Single(plan).Action);
        }

        [Fact]
        public void Plan_MissingExecutable_ThrowsBeforeAnyChange()
        {
            var ex = Assert.Throws<ConfigFileException>(() => ApplyPlanner.Build(Desired(Minimal("A")), new List<Service>(), false, _ => false));

            Assert.Contains("可执行文件不存在", ex.Message);
        }

        [Fact]
        public void Plan_AmbiguousExistingName_Throws()
        {
            var existing = new List<Service>
            {
                Existing("id-1", "A"),
                Existing("id-2", "a")
            };

            Assert.Throws<ConfigFileException>(() => ApplyPlanner.Build(Desired(Minimal("A")), existing, false, _ => true));
        }

        [Fact]
        public void Plan_UserAccountNeedingPassword_IsRejected()
        {
            var entry = Minimal("A");
            entry.Account = @".\svcuser";

            var ex = Assert.Throws<ConfigFileException>(() => ApplyPlanner.Build(Desired(entry), new List<Service>(), false, _ => true));

            Assert.Contains("--password-stdin", ex.Message);
        }

        [Fact]
        public void Plan_SameUserAccount_IsKeptWithoutPassword()
        {
            var existing = new List<Service>
            {
                WithUser(Existing("id-a", "A"), @".\svcuser")
            };
            var entry = Minimal("A");
            entry.Account = @".\SVCUSER";

            var plan = ApplyPlanner.Build(Desired(entry), existing, false, _ => true);

            Assert.Equal("unchanged", Assert.Single(plan).Action);
        }
    }
}
