using Services.IntegrationTests.Infrastructure;

namespace Services.IntegrationTests
{
    public class AccountAndEnvironmentTests
    {
        [IntegrationFact]
        public void VirtualAccountRunsTargetAndWritesLogsAndState()
        {
            using var service = TestService.Create("Virtual", "--print-user", "--account", "virtual", "--start");

            service.WaitForLog($"user NT SERVICE\\{service.Id}");
            var info = service.Get();
            Assert.Equal("virtual", info.GetProperty("account").GetProperty("type").GetString());
            Assert.Equal($"NT SERVICE\\{service.Id}", info.GetProperty("account").GetProperty("name").GetString());
            // 运行状态文件由包装器以虚拟账户写入
            Assert.NotNull(service.ProcessPid);
        }

        [IntegrationFact]
        public void LocalServiceAccountRunsTarget()
        {
            using var service = TestService.Create("LocalSvc", "--print-user", "--account", "localservice", "--start");
            service.WaitForLog("user NT AUTHORITY\\LOCAL SERVICE");
        }

        [IntegrationFact]
        public void EnvironmentVariablesAreAppliedAndExpanded()
        {
            using var service = TestService.Create("Env", "--print-env IT_PLAIN --print-env IT_EXPANDED",
                "--env", "IT_PLAIN=hello world", "--env", @"IT_EXPANDED=%SystemRoot%\it", "--start");

            service.WaitForLog("env IT_PLAIN=hello world");
            service.WaitForLog($"env IT_EXPANDED={Environment.GetEnvironmentVariable("SystemRoot")}\\it");

            var environment = service.Get().GetProperty("environment");
            Assert.Equal(@"%SystemRoot%\it", environment.GetProperty("IT_EXPANDED").GetString());
        }

        [IntegrationFact]
        public void StartingServiceStartsItsDependencies()
        {
            using var dependency = TestService.Create("Dependency", "", "--startup", "manual");
            using var dependent = TestService.Create("Dependent", "", "--depends-on", dependency.Id);

            var dependencies = dependent.Get().GetProperty("dependencies").EnumerateArray().Select(d => d.GetString()).ToList();
            Assert.Equal(new[] { dependency.Id }, dependencies);

            dependent.Start();

            Assert.Equal("running", dependency.State);
            Assert.Equal("running", dependent.State);

            // 停止被依赖的服务时，依赖它的服务先被停止
            dependency.Stop();
            Assert.Equal("stopped", dependent.State);

            Ctl.Json("update", dependent.Id, "--clear-depends");
            Assert.Empty(dependent.Get().GetProperty("dependencies").EnumerateArray());
        }

        [IntegrationFact]
        public void DependingOnMissingServiceIsRejected()
        {
            var name = TestService.NewName("BadDep");
            var result = Ctl.Run("create", "--name", name, "--exe", ItEnvironment.TargetPath, "--depends-on", "IT_NoSuchService_123");
            Assert.NotEqual(0, result.ExitCode);
            Assert.DoesNotContain(Ctl.Json("list").EnumerateArray(), s => s.GetProperty("name").GetString() == name);
        }
    }
}
