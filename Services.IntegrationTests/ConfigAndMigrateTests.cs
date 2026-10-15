using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Services.IntegrationTests.Infrastructure;

namespace Services.IntegrationTests
{
    public class ConfigAndMigrateTests
    {
        private static List<JsonElement> Actions(JsonElement result) => result.GetProperty("actions").EnumerateArray().ToList();

        private static JsonElement ActionFor(JsonElement result, string name) =>
            Actions(result).Single(a => a.GetProperty("name").GetString() == name);

        [IntegrationFact]
        public void ExportThenApplyIsIdempotent()
        {
            using var service = TestService.Create("Export", "--stdout exported", "--env", "IT_A=1",
                "--auto-restart", "--restart-delay", "7", "--stop-mode", "ctrl-c", "--stop-timeout", "9", "--startup", "manual");
            var file = Path.Combine(ItEnvironment.NewWorkDir("export"), "services.json");

            Ctl.Run("export", service.Id, "-o", file).EnsureSuccess();

            var dryRun = Ctl.Json("apply", "-f", file, "--dry-run");
            Assert.Equal("unchanged", ActionFor(dryRun, service.Name).GetProperty("action").GetString());

            // 修改描述后应用：只更新该字段
            var config = JsonNode.Parse(File.ReadAllText(file))!;
            config["services"]![0]!["description"] = "changed by apply";
            File.WriteAllText(file, config.ToJsonString());

            var applied = Ctl.Json("apply", "-f", file);
            var action = ActionFor(applied, service.Name);
            Assert.Equal("update", action.GetProperty("action").GetString());
            Assert.Equal(new[] { "description" }, action.GetProperty("changes").EnumerateArray().Select(c => c.GetString()));
            Assert.Equal("changed by apply", service.Get().GetProperty("description").GetString());

            var again = Ctl.Json("apply", "-f", file);
            Assert.Equal("unchanged", ActionFor(again, service.Name).GetProperty("action").GetString());
        }

        [IntegrationFact]
        public void ApplyCreatesMissingService()
        {
            var name = TestService.NewName("Apply");
            var file = Path.Combine(ItEnvironment.NewWorkDir("apply"), "services.json");
            var config = new JsonObject
            {
                ["version"] = 1,
                ["services"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = name,
                        ["exe"] = ItEnvironment.TargetPath,
                        ["args"] = "--stdout applied",
                        ["startup"] = "manual"
                    }
                }
            };
            File.WriteAllText(file, config.ToJsonString());

            var result = Ctl.Json("apply", "-f", file);
            var action = ActionFor(result, name);
            Assert.Equal("create", action.GetProperty("action").GetString());
            using var service = TestService.Adopt(action.GetProperty("id").GetString()!, name);

            Assert.Equal("manual", service.Get().GetProperty("startupType").GetString());
            var again = Ctl.Json("apply", "-f", file);
            Assert.Equal("unchanged", ActionFor(again, name).GetProperty("action").GetString());
        }

        [IntegrationFact]
        public void MigrateRepointsServiceToCurrentWrapper()
        {
            using var service = TestService.Create("Migrate", "", "--startup", "manual");
            var current = ReadImagePath(service.Id);
            Assert.Contains(WrapperTests.WrapperRoot, current, StringComparison.OrdinalIgnoreCase);

            // 模拟旧版本创建的服务：包装器指向另一个位置
            var legacy = $"\"C:\\Legacy\\ServicesApp.exe\" --service-wrapper \"{service.Id}\"";
            RunSc("config", service.Id, "binPath=", legacy);
            Assert.Equal(legacy, ReadImagePath(service.Id));

            var result = Ctl.Json("migrate");
            Assert.Contains(service.Id, result.GetProperty("migrated").EnumerateArray().Select(m => m.GetString()));
            Assert.Equal(current, ReadImagePath(service.Id));

            var again = Ctl.Json("migrate");
            Assert.DoesNotContain(service.Id, again.GetProperty("migrated").EnumerateArray().Select(m => m.GetString()));

            // 迁移后服务可正常启动
            service.Start();
            Assert.Equal("running", service.State);
        }

        internal static string ReadImagePath(string id)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{id}")!;
            return (string)key.GetValue("ImagePath", "", RegistryValueOptions.DoNotExpandEnvironmentNames)!;
        }

        internal static void RunSc(params string[] args)
        {
            var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"sc.exe {string.Join(' ', args)} 失败: {output}");
        }
    }
}
