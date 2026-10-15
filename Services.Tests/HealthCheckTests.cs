using Services.Core.Configuration;
using Services.Core.Health;
using Services.Core.Models;
using Services.Core.Presentation;
using Xunit;

namespace Services.Tests
{
    public class HealthCheckSpecTests
    {
        [Theory]
        [InlineData(HealthCheckType.None, null, true)]
        [InlineData(HealthCheckType.Http, "http://127.0.0.1:8080/health", true)]
        [InlineData(HealthCheckType.Http, "https://example.com", true)]
        [InlineData(HealthCheckType.Http, "ftp://example.com", false)]
        [InlineData(HealthCheckType.Http, "127.0.0.1:8080", false)]
        [InlineData(HealthCheckType.Http, "", false)]
        [InlineData(HealthCheckType.Tcp, "127.0.0.1:5432", true)]
        [InlineData(HealthCheckType.Tcp, "[::1]:80", true)]
        [InlineData(HealthCheckType.Tcp, "localhost", false)]
        [InlineData(HealthCheckType.Tcp, "host:0", false)]
        [InlineData(HealthCheckType.Tcp, "host:70000", false)]
        [InlineData(HealthCheckType.Tcp, ":80", false)]
        [InlineData(HealthCheckType.Command, "curl -f http://x", true)]
        [InlineData(HealthCheckType.Command, "  ", false)]
        public void Validate(HealthCheckType type, string? target, bool valid)
        {
            Assert.Equal(valid, HealthCheckSpec.Validate(type, target) == null);
        }

        [Fact]
        public void ParsesTcpTarget()
        {
            Assert.True(HealthCheckSpec.TryParseTcpTarget(" db.local:5432 ", out var host, out var port));
            Assert.Equal("db.local", host);
            Assert.Equal(5432, port);
            Assert.True(HealthCheckSpec.TryParseTcpTarget("[::1]:8080", out host, out _));
            Assert.Equal("::1", host);
        }

        [Theory]
        [InlineData("HTTP", HealthCheckType.Http)]
        [InlineData("tcp", HealthCheckType.Tcp)]
        [InlineData("command", HealthCheckType.Command)]
        [InlineData("none", HealthCheckType.None)]
        public void ParsesType(string text, HealthCheckType expected)
        {
            Assert.True(HealthCheckSpec.TryParseType(text, out var type));
            Assert.Equal(expected, type);
            Assert.False(HealthCheckSpec.TryParseType("udp", out _));
        }
    }

    public class HealthTrackerTests
    {
        private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

        [Fact]
        public void FailuresDuringStartPeriodAreNotCounted()
        {
            var tracker = new HealthTracker(2, TimeSpan.FromSeconds(30), T0);
            Assert.False(tracker.Record(false, T0.AddSeconds(5)));
            Assert.False(tracker.Record(false, T0.AddSeconds(10)));
            Assert.Equal(HealthStatuses.Starting, tracker.Status);
            Assert.Equal(0, tracker.ConsecutiveFailures);
        }

        [Fact]
        public void BecomesUnhealthyAfterThresholdOnce()
        {
            var tracker = new HealthTracker(3, TimeSpan.Zero, T0);
            Assert.False(tracker.Record(false, T0.AddSeconds(1)));
            Assert.False(tracker.Record(false, T0.AddSeconds(2)));
            Assert.True(tracker.Record(false, T0.AddSeconds(3)));
            Assert.Equal(HealthStatuses.Unhealthy, tracker.Status);
            // 已判定为不健康后不再重复触发
            Assert.False(tracker.Record(false, T0.AddSeconds(4)));
        }

        [Fact]
        public void SuccessResetsFailuresAndEndsStartPeriod()
        {
            var tracker = new HealthTracker(2, TimeSpan.FromMinutes(5), T0);
            Assert.False(tracker.Record(true, T0.AddSeconds(1)));
            Assert.Equal(HealthStatuses.Healthy, tracker.Status);

            // 宽限期内但已就绪：失败开始计数
            Assert.False(tracker.Record(false, T0.AddSeconds(2)));
            Assert.Equal(1, tracker.ConsecutiveFailures);
            Assert.False(tracker.Record(true, T0.AddSeconds(3)));
            Assert.Equal(0, tracker.ConsecutiveFailures);
            Assert.False(tracker.Record(false, T0.AddSeconds(4)));
            Assert.True(tracker.Record(false, T0.AddSeconds(5)));
        }

        [Fact]
        public void FailuresCountAfterStartPeriodEnds()
        {
            var tracker = new HealthTracker(1, TimeSpan.FromSeconds(10), T0);
            Assert.False(tracker.Record(false, T0.AddSeconds(9)));
            Assert.True(tracker.Record(false, T0.AddSeconds(11)));
        }
    }

    public class HealthCheckTextAndConfigTests
    {
        [Fact]
        public void Text()
        {
            Assert.Equal("未启用", ServiceText.HealthCheck(HealthCheckType.None, null, 30, 5, 3, 30));
            Assert.Equal("TCP 127.0.0.1:80（每 10 秒，超时 2 秒，连续 3 次失败视为不健康，启动宽限 0 秒）",
                ServiceText.HealthCheck(HealthCheckType.Tcp, "127.0.0.1:80", 10, 2, 3, 0));
            Assert.Null(ServiceText.HealthStatus(null, null, 0, null));
            Assert.Equal("健康：HTTP 200", ServiceText.HealthStatus("healthy", "HTTP 200", 0, null));
            Assert.Equal("不健康（连续失败 3 次），2026-01-02 03:04:05：timed out",
                ServiceText.HealthStatus("unhealthy", "timed out", 3, new DateTime(2026, 1, 2, 3, 4, 5)));
        }

        private static Service WithHealth() => new()
        {
            Id = "id",
            Name = "App",
            ExePath = @"C:\Apps\app.exe",
            WorkingDir = @"C:\Apps",
            HealthCheckType = HealthCheckType.Http,
            HealthCheckTarget = "http://127.0.0.1/health",
            HealthCheckIntervalSeconds = 10
        };

        [Fact]
        public void ConfigRoundTripAndDiff()
        {
            var service = WithHealth();
            var definition = ConfigFileMapper.ToDefinition(service);
            Assert.Equal("http", definition.HealthCheck!.Type);
            Assert.Empty(ConfigFileMapper.Diff(ServiceConfig.FromService(service), ConfigFileMapper.ToConfig(definition, "s")));

            definition.HealthCheck.FailureThreshold = 5;
            Assert.Equal(new[] { "healthCheck" },
                ConfigFileMapper.Diff(ServiceConfig.FromService(service), ConfigFileMapper.ToConfig(definition, "s")));

            Assert.Null(ConfigFileMapper.ToDefinition(new Service { Name = "x", ExePath = @"C:\x.exe" }).HealthCheck);
        }

        [Fact]
        public void InvalidConfigIsRejectedWithPath()
        {
            var definition = new ServiceDefinition
            {
                Name = "App",
                Exe = @"C:\Apps\app.exe",
                HealthCheck = new HealthCheckDefinition { Type = "tcp", Target = "no-port" }
            };
            Assert.Contains("services[2].healthCheck.target",
                Assert.Throws<ConfigFileException>(() => ConfigFileMapper.ToConfig(definition, "services[2]")).Message);

            definition.HealthCheck = new HealthCheckDefinition { Type = "udp", Target = "x:1" };
            Assert.Contains("healthCheck.type",
                Assert.Throws<ConfigFileException>(() => ConfigFileMapper.ToConfig(definition, "services[2]")).Message);
        }
    }
}
