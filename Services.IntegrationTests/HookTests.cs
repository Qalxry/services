using Services.IntegrationTests.Infrastructure;

namespace Services.IntegrationTests
{
    public class HookTests
    {
        [IntegrationFact]
        public void HooksRunAroundEachStartWithServiceEnvironment()
        {
            var record = Path.Combine(ItEnvironment.NewWorkDir("hooks"), "starts.txt");
            using var service = TestService.Create("Hooks", $"--record {record} --exit 3 --delay 1000", "--start",
                "--env", "HOOK_VAR=hello",
                "--pre-start", "echo prepared-%HOOK_VAR%",
                "--post-stop", "echo stopped-{exitcode}",
                "--auto-restart", "--restart-delay", "1", "--no-backoff", "--max-restarts", "1");

            Wait.Until(() => File.Exists(record) && File.ReadAllLines(record).Length >= 2, "崩溃后重启一次", TimeSpan.FromSeconds(30), service.Describe);
            service.WaitForState("stopped", TimeSpan.FromSeconds(30));

            var lines = service.Logs().Select(l => l.Text).ToList();
            var preStarts = lines.Select((text, i) => (text, i)).Where(x => x.text == "[pre-start] prepared-hello").Select(x => x.i).ToList();
            var postStops = lines.Select((text, i) => (text, i)).Where(x => x.text == "[post-stop] stopped-3").Select(x => x.i).ToList();

            // 每次启动之前执行启动前命令（含自动重启），每次退出之后执行停止后命令（含重启之前）
            Assert.True(preStarts.Count == 2, service.Describe());
            Assert.True(postStops.Count == 2, service.Describe());
            Assert.True(preStarts[0] < postStops[0] && postStops[0] < preStarts[1] && preStarts[1] < postStops[1], service.Describe());
        }

        [IntegrationFact]
        public void FailingPreStartPreventsStart()
        {
            var record = Path.Combine(ItEnvironment.NewWorkDir("prestartfail"), "starts.txt");
            using var service = TestService.Create("PreStartFail", $"--record {record}", "--no-auto-restart", "--pre-start", "exit 4");

            var start = Ctl.Run("start", service.Id, "--timeout", "10");
            Assert.NotEqual(0, start.ExitCode);

            service.WaitForState("stopped");
            var reason = service.Get().GetProperty("runtime").GetProperty("lastFailureReason").GetString();
            Assert.Contains("Pre-start command exited with code 4", reason);
            Assert.False(File.Exists(record), "启动前命令失败时不应启动目标程序");
        }

        [IntegrationFact]
        public void PreStartTimeoutKillsCommand()
        {
            using var service = TestService.Create("PreStartTimeout", "", "--no-auto-restart",
                "--pre-start", "ping -n 30 127.0.0.1 >nul", "--hook-timeout", "2");

            var start = Ctl.Run("start", service.Id, "--timeout", "15");
            Assert.NotEqual(0, start.ExitCode);

            service.WaitForState("stopped");
            var reason = service.Get().GetProperty("runtime").GetProperty("lastFailureReason").GetString();
            Assert.Contains("Pre-start command did not finish within 2s", reason);
        }

        [IntegrationFact]
        public void PostStopRunsWhenServiceIsStopped()
        {
            var marker = Path.Combine(ItEnvironment.NewWorkDir("poststop"), "post-stop.txt");
            using var service = TestService.Create("PostStop", "", "--start", "--post-stop", $"echo {{pid}} {{exitcode}}> \"{marker}\"");
            service.WaitForLog("ready");
            var pid = service.ProcessPid ?? throw new InvalidOperationException(service.Describe());

            var hooks = service.Get().GetProperty("hooks");
            Assert.Equal("echo {pid} {exitcode}> \"" + marker + "\"", hooks.GetProperty("postStop").GetString());

            service.Stop();

            // 停止服务时等待停止后命令执行完
            Assert.True(File.Exists(marker), service.Describe());
            Assert.StartsWith($"{pid} ", File.ReadAllText(marker).Trim());
            Assert.True(service.HasLog("Post-stop command completed", "system"), service.Describe());
        }
    }
}
