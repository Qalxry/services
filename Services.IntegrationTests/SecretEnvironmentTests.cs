using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Services.IntegrationTests.Infrastructure;

namespace Services.IntegrationTests
{
    public class SecretEnvironmentTests
    {
        [IntegrationFact]
        public void SecretIsAvailableToServiceButMaskedAndProtected()
        {
            using var service = TestService.Create("Secret", "--print-env IT_SECRET --print-env IT_PLAIN",
                "--secret-env", "IT_SECRET=s3cret-value", "--env", "IT_PLAIN=visible", "--account", "virtual", "--start");

            // 以虚拟账户运行的包装器能读取受保护的值
            service.WaitForLog("env IT_SECRET=s3cret-value");
            service.WaitForLog("env IT_PLAIN=visible");

            // get 默认遮蔽，--show-secrets 显示
            var masked = service.Get().GetProperty("secretEnvironment");
            Assert.Equal(JsonValueKind.Null, masked.GetProperty("IT_SECRET").ValueKind);
            var shown = Ctl.Json("get", service.Id, "--show-secrets").GetProperty("secretEnvironment");
            Assert.Equal("s3cret-value", shown.GetProperty("IT_SECRET").GetString());
            Assert.DoesNotContain("s3cret-value", Ctl.Run("get", service.Id).Stdout);

            // 公开的 Parameters 中只有变量名
            using (var parameters = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service.Id}\Parameters")!)
            {
                foreach (var name in parameters.GetValueNames())
                {
                    var value = parameters.GetValue(name);
                    var text = value is string[] lines ? string.Join("\n", lines) : value?.ToString() ?? "";
                    Assert.DoesNotContain("s3cret-value", text);
                }
            }

            // Secrets 子键禁止继承，只有 SYSTEM、Administrators 与服务的虚拟账户
            using var secrets = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service.Id}\Parameters\Secrets")!;
            var security = secrets.GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            var allowed = new[]
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                (SecurityIdentifier)new NTAccount($@"NT SERVICE\{service.Id}").Translate(typeof(SecurityIdentifier))
            };
            foreach (RegistryAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                Assert.Contains((SecurityIdentifier)rule.IdentityReference, allowed);
            }
        }

        [IntegrationFact]
        public void ExportWithoutValuesAppliesAsUnchanged()
        {
            using var service = TestService.Create("SecretExport", "", "--secret-env", "IT_TOKEN=abc", "--startup", "manual");
            var file = Path.Combine(ItEnvironment.NewWorkDir("secretexport"), "services.json");

            Ctl.Run("export", service.Id, "-o", file).EnsureSuccess();
            Assert.DoesNotContain("abc", File.ReadAllText(file));

            var plan = Ctl.Json("apply", "-f", file);
            var action = plan.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("name").GetString() == service.Name);
            Assert.Equal("unchanged", action.GetProperty("action").GetString());

            // 写出新值则更新
            var config = JsonNode.Parse(File.ReadAllText(file))!;
            config["services"]![0]!["secretEnvironment"]!["IT_TOKEN"] = "rotated";
            File.WriteAllText(file, config.ToJsonString());
            Ctl.Json("apply", "-f", file);
            Assert.Equal("rotated", Ctl.Json("get", service.Id, "--show-secrets")
                .GetProperty("secretEnvironment").GetProperty("IT_TOKEN").GetString());
        }
    }
}
