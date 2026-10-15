using System.Text.Json;
using Services.IntegrationTests.Infrastructure;

namespace Services.IntegrationTests
{
    public class RestartTests
    {
        private static int CountStarts(string record) =>
            File.Exists(record) ? File.ReadAllLines(record).Count(l => l.StartsWith("start ", StringComparison.Ordinal)) : 0;

        [IntegrationFact]
        public void CrashingProcessIsRestartedThenGivenUp()
        {
            var record = Path.Combine(ItEnvironment.NewWorkDir("crash"), "starts.txt");
            using var service = TestService.Create("Crash", $"--record {record} --exit 3",
                "--auto-restart", "--restart-delay", "1", "--max-restarts", "2", "--no-backoff", "--start");

            service.WaitForState("stopped", TimeSpan.FromSeconds(60));

            Assert.Equal(3, CountStarts(record));
            var runtime = service.Get().GetProperty("runtime");
            Assert.Equal(2, runtime.GetProperty("totalRestarts").GetInt32());
            Assert.Equal(3, runtime.GetProperty("lastExitCode").GetInt32());
            Assert.Contains("gave up", runtime.GetProperty("lastFailureReason").GetString());
        }

        [IntegrationFact]
        public void NonZeroExitWithoutAutoRestartFailsService()
        {
            var record = Path.Combine(ItEnvironment.NewWorkDir("noauto"), "starts.txt");
            using var service = TestService.Create("NoAuto", $"--record {record} --exit 5", "--no-auto-restart", "--start");

            service.WaitForState("stopped");

            Assert.Equal(1, CountStarts(record));
            var runtime = service.Get().GetProperty("runtime");
            Assert.Equal(5, runtime.GetProperty("lastExitCode").GetInt32());
            Assert.Contains("AutoRestart disabled", runtime.GetProperty("lastFailureReason").GetString());
        }

        [IntegrationFact]
        public void ZeroExitStopsServiceWithoutRestart()
        {
            var record = Path.Combine(ItEnvironment.NewWorkDir("zero"), "starts.txt");
            using var service = TestService.Create("Zero", $"--record {record} --exit 0 --delay 500",
                "--auto-restart", "--restart-delay", "1", "--start");

            service.WaitForState("stopped");
            Thread.Sleep(2000);

            Assert.Equal(1, CountStarts(record));
            Assert.Equal("stopped", service.State);
            Assert.True(service.HasLog("Normal exit, not restarting", "system"), service.Describe());
            var runtime = service.Get().GetProperty("runtime");
            Assert.Equal(JsonValueKind.Null, runtime.GetProperty("lastFailureReason").ValueKind);
        }

        [IntegrationFact]
        public void MissingExecutableFailsService()
        {
            var dir = ItEnvironment.NewWorkDir("missing");
            var exe = Path.Combine(dir, "gone.exe");
            File.Copy(ItEnvironment.TargetPath, exe);
            using var service = TestService.Create("Missing", "", "--exe", exe, "--auto-restart");
            File.Delete(exe);

            Ctl.Run("start", service.Id, "--timeout", "5");

            service.WaitForState("stopped");
            var runtime = service.Get().GetProperty("runtime");
            Assert.Contains("Executable not found", runtime.GetProperty("lastFailureReason").GetString());
        }

        [IntegrationFact]
        public void KillingWrapperKillsProcessTree()
        {
            var childFile = Path.Combine(ItEnvironment.NewWorkDir("job"), "child.txt");
            using var service = TestService.Create("Job", $"--spawn-child {childFile}", "--start");
            service.WaitForLog("ready");

            var wrapperPid = service.Get().GetProperty("pid").GetInt32();
            var targetPid = service.ProcessPid ?? throw new InvalidOperationException(service.Describe());
            var childPid = int.Parse(File.ReadAllText(childFile));
            Assert.False(Wait.ProcessExited(childPid));

            Wait.KillProcess(wrapperPid);

            Wait.Until(() => Wait.ProcessExited(targetPid), "目标程序随包装器退出");
            Wait.Until(() => Wait.ProcessExited(childPid), "目标程序的子进程随包装器退出");
        }
    }
}
