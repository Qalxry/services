using Services.IntegrationTests.Infrastructure;

namespace Services.IntegrationTests
{
    public class StopModeTests
    {
        [IntegrationFact]
        public void KillModeDoesNotSendCtrlC()
        {
            var dir = ItEnvironment.NewWorkDir("kill");
            var marker = Path.Combine(dir, "ctrlc.txt");
            using var service = TestService.Create("Kill", $"--ctrlc-file {marker}", "--start");
            service.WaitForLog("ready");

            service.Stop();

            Assert.False(File.Exists(marker), "kill 模式不应发送 Ctrl+C");
        }

        [IntegrationFact]
        public void CtrlCModeStopsGracefully()
        {
            var dir = ItEnvironment.NewWorkDir("ctrlc");
            var marker = Path.Combine(dir, "ctrlc.txt");
            using var service = TestService.Create("CtrlC", $"--ctrlc-file {marker}", "--start", "--stop-mode", "ctrl-c");
            service.WaitForLog("ready");

            service.Stop();

            Assert.True(File.Exists(marker), service.Describe());
            Assert.True(service.HasLog("Process exited gracefully", "system"), service.Describe());
        }

        [IntegrationFact]
        public void CtrlCModeKillsAfterTimeout()
        {
            using var service = TestService.Create("CtrlCTimeout", "--ignore-ctrlc", "--start",
                "--stop-mode", "ctrl-c", "--stop-timeout", "2");
            service.WaitForLog("ready");
            var pid = service.ProcessPid ?? throw new InvalidOperationException(service.Describe());

            service.Stop();

            Assert.True(Wait.ProcessExited(pid), service.Describe());
            Assert.True(service.HasLogContaining("did not exit within 2s"), service.Describe());
        }

        [IntegrationFact]
        public void CommandModeRunsStopCommandWithPlaceholders()
        {
            var dir = ItEnvironment.NewWorkDir("command");
            var quit = Path.Combine(dir, "quit.txt");
            using var service = TestService.Create("Command", $"--quit-file {quit}", "--start",
                "--stop-mode", "command", "--stop-command", $"echo {{pid}}> \"{quit}\"");
            service.WaitForLog("ready");
            var pid = service.ProcessPid ?? throw new InvalidOperationException(service.Describe());

            service.Stop();

            Assert.True(File.Exists(quit), service.Describe());
            Assert.Equal(pid.ToString(), File.ReadAllText(quit).Trim());
            Assert.True(service.HasLog("Process exited gracefully", "system"), service.Describe());
        }
    }
}
