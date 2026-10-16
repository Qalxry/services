using System.Text.Json;
using Services.Core.Configuration;
using Services.Core.Models;
using Services.Core.Notifications;
using Services.Core.Presentation;
using Xunit;

namespace Services.Tests
{
    public class WebhookTests
    {
        [Fact]
        public void ParseEvents()
        {
            Assert.Equal(WebhookEvents.Default, WebhookEvents.Parse(null));
            Assert.Equal(WebhookEvents.Default, WebhookEvents.Parse(" "));
            // 按固定顺序、去重、不区分大小写
            Assert.Equal(new[] { "failed", "restarting" }, WebhookEvents.Parse("Restarting, failed;FAILED"));
            Assert.Throws<ArgumentException>(() => WebhookEvents.Parse("failed,crashed"));
            Assert.Equal("failed,unhealthy", WebhookEvents.Format(WebhookEvents.Default));
        }

        [Fact]
        public void TestEventIsAlwaysSubscribed()
        {
            Assert.True(WebhookEvents.IsSubscribed(new[] { "failed" }, WebhookEvents.Test));
            Assert.False(WebhookEvents.IsSubscribed(new[] { "failed" }, WebhookEvents.Restarting));
        }

        [Fact]
        public void ServiceSettingsOverrideGlobal()
        {
            var global = new WebhookSettings("https://global/x", WebhookEvents.Default);
            var service = new WebhookSettings("https://service/y", new[] { "failed" });

            Assert.Same(service, WebhookSettings.Resolve(service, global));
            Assert.Same(global, WebhookSettings.Resolve(null, global));
            Assert.Same(global, WebhookSettings.Resolve(new WebhookSettings("", WebhookEvents.Default), global));
            Assert.False(WebhookSettings.Resolve(null, null).Enabled);
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("https://hooks.example.com/abc", true)]
        [InlineData("http://127.0.0.1:9000/hook", true)]
        [InlineData("hooks.example.com/abc", false)]
        [InlineData("file:///c:/x", false)]
        public void ValidateUrl(string? url, bool valid) => Assert.Equal(valid, WebhookSettings.ValidateUrl(url) == null);

        [Theory]
        [InlineData("https://hooks.slack.com/services/T000/B000/XXXX", "https://hooks.slack.com/******")]
        [InlineData("https://example.com/?token=abc", "https://example.com/******")]
        [InlineData("https://user:pass@example.com", "https://example.com/******")]
        [InlineData("http://127.0.0.1:9000/", "http://127.0.0.1:9000/")]
        [InlineData("not a url", "******")]
        public void Mask(string url, string expected) => Assert.Equal(expected, WebhookSettings.Mask(url));

        [Fact]
        public void PayloadJson()
        {
            var payload = WebhookPayload.Create("failed", "WinSvcMgr_A_1", "接口服务", "放弃重启",
                new DateTimeOffset(2026, 10, 15, 10, 20, 30, 123, TimeSpan.FromHours(8)), "SERVER01", exitCode: 1);
            var json = payload.ToJson();

            Assert.Contains("\"serviceName\":\"接口服务\"", json);
            Assert.Contains("\"time\":\"2026-10-15T10:20:30.123+08:00\"", json);
            Assert.DoesNotContain("restartCount", json);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("failed", doc.RootElement.GetProperty("event").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("exitCode").GetInt32());
        }

        private static Service WithWebhook() => new()
        {
            Id = "id",
            Name = "App",
            ExePath = @"C:\Apps\app.exe",
            WorkingDir = @"C:\Apps",
            WebhookUrl = "https://hooks.example.com/secret-token",
            WebhookEvents = "failed"
        };

        [Fact]
        public void ConfigExportOmitsUrlAndAppliesAsUnchanged()
        {
            var service = WithWebhook();
            var definition = ConfigFileMapper.ToDefinition(service);
            Assert.Null(definition.Webhook!.Url);
            Assert.Equal(new[] { "failed" }, definition.Webhook.Events);
            Assert.DoesNotContain("secret-token", ConfigFileMapper.Serialize(new ConfigFile { Services = { definition } }));

            var plan = ApplyPlanner.Build(new[] { ConfigFileMapper.ToConfig(definition, "s") }, new[] { service }, false, _ => true);
            Assert.Equal("unchanged", plan.Single().Action);
            Assert.Equal(service.WebhookUrl, plan.Single().Config!.WebhookUrl);
        }

        [Fact]
        public void ConfigChangesAreDetected()
        {
            var service = WithWebhook();
            var definition = ConfigFileMapper.ToDefinition(service, includeSecretValues: true);
            definition.Webhook!.Events = new List<string> { "failed", "unhealthy" };
            Assert.Equal(new[] { "webhook" },
                ConfigFileMapper.Diff(ServiceConfig.FromService(service), ConfigFileMapper.ToConfig(definition, "s")));

            // 删除 webhook 段：改为沿用全局设置
            definition.Webhook = null;
            Assert.Equal(new[] { "webhook" },
                ConfigFileMapper.Diff(ServiceConfig.FromService(service), ConfigFileMapper.ToConfig(definition, "s")));
        }

        [Fact]
        public void ConfigRejectsInvalidValues()
        {
            var definition = new ServiceDefinition { Name = "App", Exe = @"C:\Apps\app.exe", Webhook = new WebhookDefinition { Url = "ftp://x" } };
            Assert.Contains("webhook.url", Assert.Throws<ConfigFileException>(() => ConfigFileMapper.ToConfig(definition, "s")).Message);

            definition.Webhook = new WebhookDefinition { Url = "https://x", Events = new List<string> { "boom" } };
            Assert.Contains("webhook.events", Assert.Throws<ConfigFileException>(() => ConfigFileMapper.ToConfig(definition, "s")).Message);

            // 新建服务时不能沿用（没有已保存的地址）
            definition.Webhook = new WebhookDefinition();
            var config = ConfigFileMapper.ToConfig(definition, "s");
            Assert.Contains("webhook.url", Assert.Throws<ConfigFileException>(() =>
                ApplyPlanner.Build(new[] { config }, Array.Empty<Service>(), false, _ => true)).Message);
        }

        [Fact]
        public void Text()
        {
            Assert.Equal("沿用全局设置", ServiceText.Webhook(null, null, true, false));
            Assert.Equal("https://hooks.example.com/******（事件：failed）", ServiceText.Webhook("https://hooks.example.com/t", "failed", true, false));
            Assert.Equal("（无权读取）", ServiceText.Webhook(null, null, false, false));
        }
    }
}
