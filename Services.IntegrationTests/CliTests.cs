using Services.IntegrationTests.Infrastructure;

namespace Services.IntegrationTests
{
    public class CliTests
    {
        [IntegrationFact]
        public void ExitCodesMatchDocumentation()
        {
            Assert.Equal(0, Ctl.Run("version").ExitCode);
            Assert.Equal(2, Ctl.Run("no-such-command").ExitCode);
            Assert.Equal(2, Ctl.Run("create", "--name", "IT_x").ExitCode);
            Assert.Equal(3, Ctl.Run("get", "IT_NoSuchService_123").ExitCode);
        }

        [IntegrationFact]
        public void EveryCommandHasHelp()
        {
            var overview = Ctl.Run("help").EnsureSuccess().Stdout;
            foreach (var command in new[] { "list", "get", "create", "update", "delete", "start", "stop", "restart", "logs", "export", "apply", "doctor", "migrate", "version" })
            {
                Assert.Contains(command, overview);
                var help = Ctl.Run(command, "--help").EnsureSuccess().Stdout;
                Assert.Contains(command, help);
            }
        }

        [IntegrationFact]
        public void ListIncludesCreatedService()
        {
            using var service = TestService.Create("List", "", "--startup", "manual");
            var list = Ctl.Json("list");
            Assert.Contains(list.EnumerateArray(), s => s.GetProperty("id").GetString() == service.Id);
            Assert.Equal(service.Id, Ctl.Json("get", service.Name).GetProperty("id").GetString());
        }
    }
}
