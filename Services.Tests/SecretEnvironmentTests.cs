using Services.Core.Configuration;
using Services.Core.Helpers;
using Services.Core.Models;
using Services.Core.Presentation;
using Xunit;

namespace Services.Tests
{
    public class SecretEnvironmentTests
    {
        private static Dictionary<string, string> Dict(params (string Key, string Value)[] pairs) =>
            pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

        private static Service WithSecrets(string id, string name, Dictionary<string, string> secrets, bool readable = true) => new()
        {
            Id = id,
            Name = name,
            ExePath = @"C:\Apps\app.exe",
            WorkingDir = @"C:\Apps",
            SecretEnvironment = secrets,
            SecretsReadable = readable
        };

        [Fact]
        public void RestoreMasked_KeepsSavedValueForUnchangedMask()
        {
            var saved = Dict(("API_KEY", "s3cret"), ("TOKEN", "t0k"));
            var edited = Dict(("API_KEY", ServiceText.SecretMask), ("TOKEN", "new"), ("EXTRA", ServiceText.SecretMask));

            var result = EnvironmentVariables.RestoreMasked(edited, saved, ServiceText.SecretMask);

            Assert.Equal("s3cret", result["API_KEY"]);
            Assert.Equal("new", result["TOKEN"]);
            // 新变量的值恰好是遮蔽文本时按字面保存
            Assert.Equal(ServiceText.SecretMask, result["EXTRA"]);
            Assert.Equal(3, result.Count);
        }

        [Fact]
        public void SecretText_IsMaskedByDefault()
        {
            var secrets = Dict(("B", "2"), ("A", "1"));
            Assert.Equal("A=******\nB=******", ServiceText.SecretEnvironment(secrets, readable: true, showValues: false));
            Assert.Equal("A=1\nB=2", ServiceText.SecretEnvironment(secrets, readable: true, showValues: true));
            Assert.Equal("A=（无权读取）\nB=（无权读取）", ServiceText.SecretEnvironment(secrets, readable: false, showValues: true));
        }

        [Fact]
        public void DetailsRow_IsMasked()
        {
            var service = WithSecrets("id", "App", Dict(("API_KEY", "s3cret")));
            var rows = ServiceDetails.Build(service, null, null, DateTime.Now);
            Assert.Equal("API_KEY=******", rows.Single(r => r.Label == "敏感环境变量").Value);
            Assert.DoesNotContain(rows, r => r.Value?.Contains("s3cret") == true);
        }

        [Fact]
        public void Export_OmitsValuesUnlessRequested()
        {
            var service = WithSecrets("id", "App", Dict(("API_KEY", "s3cret")));

            Assert.Null(ConfigFileMapper.ToDefinition(service).SecretEnvironment!["API_KEY"]);
            Assert.Equal("s3cret", ConfigFileMapper.ToDefinition(service, includeSecretValues: true).SecretEnvironment!["API_KEY"]);
            Assert.DoesNotContain("s3cret", ConfigFileMapper.Serialize(new ConfigFile { Services = { ConfigFileMapper.ToDefinition(service) } }));
        }

        [Fact]
        public void ExportedFileWithoutValues_AppliesAsUnchanged()
        {
            var service = WithSecrets("id", "App", Dict(("API_KEY", "s3cret")));
            var config = ConfigFileMapper.ToConfig(ConfigFileMapper.ToDefinition(service), "services[0]");

            var plan = ApplyPlanner.Build(new[] { config }, new[] { service }, prune: false, fileExists: _ => true);

            Assert.Equal("unchanged", plan.Single().Action);
            Assert.Equal("s3cret", plan.Single().Config!.SecretEnvironment["API_KEY"]);
        }

        [Fact]
        public void NewSecretValue_IsAnUpdate()
        {
            var service = WithSecrets("id", "App", Dict(("API_KEY", "s3cret")));
            var definition = ConfigFileMapper.ToDefinition(service);
            definition.SecretEnvironment!["API_KEY"] = "rotated";

            var plan = ApplyPlanner.Build(new[] { ConfigFileMapper.ToConfig(definition, "services[0]") }, new[] { service }, false, _ => true);

            Assert.Equal("update", plan.Single().Action);
            Assert.Equal(new[] { "secretEnvironment" }, plan.Single().Changes);
            Assert.Equal("rotated", plan.Single().Config!.SecretEnvironment["API_KEY"]);
        }

        [Fact]
        public void NullValue_ForNewServiceOrUnknownVariable_IsRejected()
        {
            var definition = new ServiceDefinition
            {
                Name = "App",
                Exe = @"C:\Apps\app.exe",
                SecretEnvironment = new Dictionary<string, string?> { ["API_KEY"] = null }
            };
            var config = ConfigFileMapper.ToConfig(definition, "services[0]");

            var create = Assert.Throws<ConfigFileException>(() => ApplyPlanner.Build(new[] { config }, Array.Empty<Service>(), false, _ => true));
            Assert.Contains("secretEnvironment.API_KEY", create.Message);

            var other = WithSecrets("id", "App", Dict(("OTHER", "x")));
            var update = Assert.Throws<ConfigFileException>(() =>
                ApplyPlanner.Build(new[] { ConfigFileMapper.ToConfig(definition, "services[0]") }, new[] { other }, false, _ => true));
            Assert.Contains("API_KEY", update.Message);
        }

        [Fact]
        public void SameNameInBothSections_IsRejected()
        {
            var definition = new ServiceDefinition
            {
                Name = "App",
                Exe = @"C:\Apps\app.exe",
                Environment = new Dictionary<string, string> { ["KEY"] = "a" },
                SecretEnvironment = new Dictionary<string, string?> { ["key"] = "b" }
            };
            var ex = Assert.Throws<ConfigFileException>(() => ConfigFileMapper.ToConfig(definition, "services[0]"));
            Assert.Contains("environment", ex.Message);
        }

        [Fact]
        public void InvalidSecretValue_IsRejectedWithPath()
        {
            var definition = new ServiceDefinition
            {
                Name = "App",
                Exe = @"C:\Apps\app.exe",
                SecretEnvironment = new Dictionary<string, string?> { ["KEY"] = "line1\nline2" }
            };
            var ex = Assert.Throws<ConfigFileException>(() => ConfigFileMapper.ToConfig(definition, "services[4]"));
            Assert.Contains("services[4].secretEnvironment", ex.Message);
        }

        [Fact]
        public void UnreadableSecrets_ExplicitValueCountsAsChange()
        {
            var service = WithSecrets("id", "App", Dict(("API_KEY", "")), readable: false);
            var definition = ConfigFileMapper.ToDefinition(service);
            Assert.Equal("unchanged", ApplyPlanner.Build(new[] { ConfigFileMapper.ToConfig(definition, "s") }, new[] { service }, false, _ => true).Single().Action);

            definition.SecretEnvironment!["API_KEY"] = "value";
            Assert.Equal("update", ApplyPlanner.Build(new[] { ConfigFileMapper.ToConfig(definition, "s") }, new[] { service }, false, _ => true).Single().Action);
        }
    }
}
