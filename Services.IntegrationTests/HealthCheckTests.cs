using System.Net;
using System.Net.Sockets;
using Services.IntegrationTests.Infrastructure;

namespace Services.IntegrationTests
{
    public class HealthCheckTests
    {
        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string? HealthStatus(TestService service)
        {
            var runtime = service.Get().GetProperty("runtime");
            return runtime.ValueKind == System.Text.Json.JsonValueKind.Null ? null : runtime.GetProperty("healthStatus").GetString();
        }

        [IntegrationFact]
        public void TcpCheckReportsHealthy()
        {
            int port = FreePort();
            using var service = TestService.Create("HealthTcp", $"--listen {port}", "--start",
                "--health-check", "tcp", "--health-target", $"127.0.0.1:{port}", "--health-interval", "1", "--health-start-period", "0");

            Wait.Until(() => HealthStatus(service) == "healthy", "健康状态变为 healthy", describe: service.Describe);
            Assert.Equal("tcp", service.Get().GetProperty("healthCheck").GetProperty("type").GetString());
            Assert.Contains("健康状态", Ctl.Run("get", service.Id).Stdout);
        }

        [IntegrationFact]
        public void UnhealthyHttpServiceIsRestarted()
        {
            int port = FreePort();
            var record = Path.Combine(ItEnvironment.NewWorkDir("healthhttp"), "starts.txt");
            using var service = TestService.Create("HealthHttp", $"--record {record} --http {port} --unhealthy-after 2000", "--start",
                "--health-check", "http", "--health-target", $"http://127.0.0.1:{port}/health",
                "--health-interval", "1", "--health-retries", "2", "--health-start-period", "0",
                "--auto-restart", "--restart-delay", "1", "--no-backoff");

            Wait.Until(() => File.Exists(record) && File.ReadAllLines(record).Length >= 2, "不健康后被重启", TimeSpan.FromSeconds(45), service.Describe);
            Assert.True(service.HasLogContaining("Health check failed (2/2): HTTP 500"), service.Describe());
            Assert.True(service.HasLogContaining("Process is unhealthy"), service.Describe());
            Assert.Equal("running", service.State);
        }

        [IntegrationFact]
        public void FailingCommandWithoutAutoRestartFailsService()
        {
            using var service = TestService.Create("HealthCmd", "", "--start", "--no-auto-restart",
                "--health-check", "command", "--health-target", "exit 1",
                "--health-interval", "1", "--health-retries", "1", "--health-start-period", "0");

            service.WaitForState("stopped", TimeSpan.FromSeconds(30));
            var reason = service.Get().GetProperty("runtime").GetProperty("lastFailureReason").GetString();
            Assert.Contains("Health check failed (exit code 1)", reason);
        }

        [IntegrationFact]
        public void InvalidTargetIsRejected()
        {
            var result = Ctl.Run("create", "--name", TestService.NewName("HealthBad"), "--exe", ItEnvironment.TargetPath,
                "--health-check", "tcp", "--health-target", "no-port");
            Assert.Equal(2, result.ExitCode);
        }
    }
}
