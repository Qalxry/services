using Services.IntegrationTests.Infrastructure;

namespace Services.IntegrationTests
{
    public class WebhookTests
    {
        [IntegrationFact]
        public void FailedServiceSendsWebhook()
        {
            using var receiver = new WebhookReceiver();
            using var service = TestService.Create("Webhook", "--exit 3 --delay 1000", "--no-auto-restart",
                "--webhook-url", receiver.Url, "--webhook-events", "failed");

            // URL 默认遮蔽（端口后无路径以外的内容时保留主机）
            var info = service.Get().GetProperty("webhook");
            Assert.Equal(new[] { "failed" }, info.GetProperty("events").EnumerateArray().Select(e => e.GetString()));
            Assert.DoesNotContain("/hook", info.GetProperty("url").GetString());

            Ctl.Run("start", service.Id);
            var payload = receiver.WaitFor("failed", service.Id);
            Assert.Equal(service.Name, payload.GetProperty("serviceName").GetString());
            Assert.Equal(3, payload.GetProperty("exitCode").GetInt32());
            Assert.Contains("AutoRestart disabled", payload.GetProperty("message").GetString());
            Assert.Equal(Environment.MachineName, payload.GetProperty("host").GetString());
            service.WaitForLogContaining("Webhook notified: failed");
        }

        [IntegrationFact]
        public void GlobalWebhookAndTestCommand()
        {
            using var receiver = new WebhookReceiver();
            try
            {
                Ctl.Run("webhook", "set", receiver.Url, "--events", "failed,restarting").EnsureSuccess();
                var shown = Ctl.Json("webhook", "show");
                Assert.Equal(new[] { "failed", "restarting" },
                    shown.GetProperty("webhook").GetProperty("events").EnumerateArray().Select(e => e.GetString()));

                using var service = TestService.Create("WebhookGlobal", "", "--startup", "manual");
                Ctl.Run("webhook", "test", service.Id).EnsureSuccess();
                var payload = receiver.WaitFor("test", service.Id);
                Assert.Equal(service.Name, payload.GetProperty("serviceName").GetString());
            }
            finally
            {
                Ctl.Run("webhook", "clear");
            }
            Assert.Equal(System.Text.Json.JsonValueKind.Null, Ctl.Json("webhook").GetProperty("webhook").ValueKind);
        }

        [IntegrationFact]
        public void UnreachableWebhookDoesNotBlockService()
        {
            // 没有监听的端口：发送失败只记录日志
            // 持续重启（服务保持运行，后台发送的结果才能写入日志）
            using var service = TestService.Create("WebhookDown", "--exit 3 --delay 500", "--auto-restart", "--restart-delay", "1",
                "--max-restarts", "0", "--no-backoff", "--webhook-url", "http://127.0.0.1:9/hook", "--webhook-events", "restarting", "--start");
            service.WaitForLogContaining("Webhook notification failed (restarting)", TimeSpan.FromSeconds(60));
        }
    }
}
