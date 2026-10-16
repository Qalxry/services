using System.Globalization;
using Services.IntegrationTests.Infrastructure;

namespace Services.IntegrationTests
{
    public class LifecycleTests
    {
        [IntegrationFact]
        public void CreateStartStopDelete()
        {
            var workDir = ItEnvironment.NewWorkDir("lifecycle");
            using var service = TestService.Create("Lifecycle", "--stdout out-line --stderr err-line --print-cwd",
                "--workdir", workDir, "--description", "integration test", "--start");

            var info = service.Get();
            Assert.Equal("running", info.GetProperty("state").GetString());
            Assert.True(info.GetProperty("pid").GetInt32() > 0);
            Assert.Equal("integration test", info.GetProperty("description").GetString());
            Assert.Equal("localsystem", info.GetProperty("account").GetProperty("type").GetString());

            service.WaitForLog("ready");
            Assert.True(service.HasLog("out-line", "stdout"), service.Describe());
            Assert.True(service.HasLog("err-line", "stderr"), service.Describe());
            Assert.True(service.HasLog($"cwd {workDir}", "stdout"), service.Describe());
            Assert.All(service.Logs(), line => Assert.NotNull(line.Timestamp));

            Assert.NotNull(service.ProcessPid);

            service.Stop();
            Assert.Equal("stopped", service.State);

            Ctl.Run("delete", service.Id).EnsureSuccess();
            Assert.Equal(3, Ctl.Run("get", service.Id).ExitCode);
        }

        [IntegrationFact]
        public void WorkingDirectoryDefaultsToExecutableDirectory()
        {
            using var service = TestService.Create("WorkDir", "--print-cwd", "--start");
            service.WaitForLog($"cwd {ItEnvironment.BinDir}");
            Assert.Equal(ItEnvironment.BinDir, service.Get().GetProperty("workingDir").GetString());
        }

        [IntegrationFact]
        public void StoppingKillsTargetProcess()
        {
            using var service = TestService.Create("StopKill", "", "--start");
            service.WaitForLog("ready");
            var pid = service.ProcessPid ?? throw new InvalidOperationException(service.Describe());

            service.Stop();
            Wait.Until(() => Wait.ProcessExited(pid), "目标程序退出");

            var runtime = service.Get().GetProperty("runtime");
            Assert.Equal(System.Text.Json.JsonValueKind.Null, runtime.GetProperty("processPid").ValueKind);
        }

        [IntegrationFact]
        public void UpdateWithRestartAppliesNewArguments()
        {
            using var service = TestService.Create("Update", "--stdout version-1", "--start");
            service.WaitForLog("version-1");

            Ctl.Json("update", service.Id, "--args", "--stdout version-2", "--restart");

            service.WaitForLog("version-2");
            Assert.Equal("running", service.State);
            Assert.Equal("--stdout version-2", service.Get().GetProperty("args").GetString());
        }

        [IntegrationFact]
        public void MultiLineArgumentsAreJoined()
        {
            using var service = TestService.Create("MultiLine", "--stdout first\n--stdout second", "--start");
            service.WaitForLog("second");
            Assert.True(service.HasLog("first"), service.Describe());
            Assert.Equal("--stdout first --stdout second", service.Get().GetProperty("commandLineArgs").GetString());
        }

        [IntegrationFact]
        public void LogsSinceFiltersByTime()
        {
            using var service = TestService.Create("Since", "--stdout since-line", "--start");
            service.WaitForLog("since-line");

            var recent = Ctl.Json("logs", service.Id, "--since", "5m");
            Assert.Contains(recent.GetProperty("lines").EnumerateArray(), l => l.GetProperty("text").GetString() == "since-line");

            var future = DateTime.Now.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            var none = Ctl.Json("logs", service.Id, "--since", future);
            Assert.Empty(none.GetProperty("lines").EnumerateArray());
        }

        [IntegrationFact]
        public void StartupTypeRoundTrips()
        {
            using var service = TestService.Create("Startup", "", "--startup", "delayed-auto");
            Assert.Equal("delayed-auto", service.Get().GetProperty("startupType").GetString());

            Ctl.Json("update", service.Id, "--startup", "manual");
            Assert.Equal("manual", service.Get().GetProperty("startupType").GetString());

            Ctl.Json("update", service.Id, "--startup", "auto");
            Assert.Equal("auto", service.Get().GetProperty("startupType").GetString());
        }
    }
}
