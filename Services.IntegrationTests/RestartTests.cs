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
            using var service = TestService.Create("NoAuto", $"--record {record} --exit 5 --delay 1000", "--no-auto-restart", "--start");

            service.WaitForState("stopped");

            Assert.Equal(1, CountStarts(record));
            var runtime = service.Get().GetProperty("runtime");
            Assert.Equal(5, runtime.GetProperty("lastExitCode").GetInt32());
            Assert.Contains("AutoRestart disabled", runtime.GetProperty("lastFailureReason").GetString());
        }

        [IntegrationFact]
        public void StopDuringBackoffCancelsRestart()
        {
            var record = Path.Combine(ItEnvironment.NewWorkDir("backoff"), "starts.txt");
            using var service = TestService.Create("Backoff", $"--record {record} --exit 3 --delay 500",
                "--auto-restart", "--restart-delay", "8", "--no-backoff", "--start");
            service.WaitForLogContaining("Restart 1/");

            var watch = System.Diagnostics.Stopwatch.StartNew();
            service.Stop();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"停止耗时 {watch.Elapsed}");

            // 越过原定的重启时间，确认没有被重新启动
            Thread.Sleep(10_000);
            Assert.Equal("stopped", service.State);
            Assert.Equal(1, CountStarts(record));
        }

        [IntegrationFact]
        public void NonZeroExitDuringStopDoesNotRestartOrFail()
        {
            var dir = ItEnvironment.NewWorkDir("exitstop");
            var record = Path.Combine(dir, "starts.txt");
            var marker = Path.Combine(dir, "ctrlc.txt");
            using var service = TestService.Create("ExitOnStop", $"--record {record} --ctrlc-file {marker} --ctrlc-exit 9",
                "--auto-restart", "--restart-delay", "1", "--stop-mode", "ctrl-c", "--start");
            service.WaitForLog("ready");

            service.Stop();
            Thread.Sleep(3000);

            Assert.True(File.Exists(marker), service.Describe());
            Assert.Equal("stopped", service.State);
            Assert.Equal(1, CountStarts(record));
            var runtime = service.Get().GetProperty("runtime");
            Assert.Equal(System.Text.Json.JsonValueKind.Null, runtime.GetProperty("lastFailureReason").ValueKind);
        }

        [IntegrationFact]
        public void LastOutputBeforeExitIsLogged()
        {
            using var service = TestService.Create("LastWords", "--stdout last-words-1 --stderr last-words-2 --exit 0", "--no-auto-restart");
            Ctl.Run("start", service.Id);

            service.WaitForState("stopped");

            var logs = service.Logs();
            int Index(string text) => logs.FindIndex(l => l.Text == text);
            Assert.True(Index("last-words-1") >= 0 && Index("last-words-2") >= 0, service.Describe());
            Assert.True(Index("last-words-1") < Index("Process exited (code: 0)"), service.Describe());
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

            var start = Ctl.Run("start", service.Id, "--timeout", "5");
            Assert.NotEqual(0, start.ExitCode);

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
