using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Services.IntegrationTests.Infrastructure;

namespace Services.IntegrationTests
{
    public class DoctorTests
    {
        private static List<JsonElement> ChecksFor(JsonElement result, string serviceId) =>
            result.GetProperty("checks").EnumerateArray()
                .Where(c => c.GetProperty("serviceId").GetString() == serviceId)
                .ToList();

        private static JsonElement Check(JsonElement result, string serviceId, string check) =>
            ChecksFor(result, serviceId).Single(c => c.GetProperty("check").GetString() == check);

        [IntegrationFact]
        public void HealthyServiceHasNoProblems()
        {
            using var service = TestService.Create("DoctorOk", "", "--startup", "manual");

            var run = Ctl.Run("doctor", service.Id, "--json");
            Assert.Equal(0, run.ExitCode);
            var checks = ChecksFor(run.Json(), service.Id);
            Assert.NotEmpty(checks);
            Assert.All(checks, c => Assert.Equal("ok", c.GetProperty("status").GetString()));
        }

        [IntegrationFact]
        public void MissingExecutableIsAnErrorWithFix()
        {
            var dir = ItEnvironment.NewWorkDir("doctorexe");
            var exe = Path.Combine(dir, "app.exe");
            File.Copy(ItEnvironment.TargetPath, exe);
            using var service = TestService.Create("DoctorExe", "", "--exe", exe, "--startup", "manual");
            File.Delete(exe);

            var run = Ctl.Run("doctor", service.Id, "--json");
            Assert.Equal(1, run.ExitCode);
            var check = Check(run.Json(), service.Id, "exe");
            Assert.Equal("error", check.GetProperty("status").GetString());
            Assert.Contains("update", check.GetProperty("fix").GetString());

            // 文本输出包含该问题
            Assert.Contains("[ERROR] exe", Ctl.Run("doctor", service.Id).Stdout);
        }

        [IntegrationFact]
        public void InaccessibleWorkingDirectoryIsReportedForVirtualAccount()
        {
            var dir = ItEnvironment.NewWorkDir("doctoracl");
            // 只允许 SYSTEM 与管理员访问
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            new DirectoryInfo(dir).SetAccessControl(security);

            using var service = TestService.Create("DoctorAcl", "", "--workdir", dir, "--account", "virtual", "--startup", "manual");

            var check = Check(Ctl.Run("doctor", service.Id, "--json").Json(), service.Id, "account-read-workdir");
            Assert.Equal("warning", check.GetProperty("status").GetString());
            Assert.Contains("icacls", check.GetProperty("fix").GetString());
        }
    }
}
