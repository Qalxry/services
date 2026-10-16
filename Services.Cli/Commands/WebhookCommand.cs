using Services.Core.Notifications;
using Services.Core.Storage;

namespace Services.Cli
{
    internal static partial class CliApp
    {
        /// <summary>
        /// webhook [show] | set &lt;url&gt; [--events ...] | clear | test [&lt;服务&gt;]
        /// </summary>
        private static async Task<int> WebhookAsync(ParsedArgs args)
        {
            var action = args.Positionals.Count > 0 ? args.Positionals[0].ToLowerInvariant() : "show";
            string message;

            switch (action)
            {
                case "show":
                    if (args.Positionals.Count > 1) throw new UsageException("webhook show 不接受其他参数。", "webhook");
                    message = NotificationStore.ReadGlobal() is { } current
                        ? $"全局 Webhook: {(args.HasFlag("show-secrets") ? current.Url : WebhookSettings.Mask(current.Url))}（事件：{WebhookEvents.Format(current.Events)}）"
                        : "未设置全局 Webhook。";
                    break;

                case "set":
                    if (args.Positionals.Count != 2) throw new UsageException("用法：webhook set <url> [--events failed,unhealthy,restarting]。", "webhook");
                    var url = args.Positionals[1].Trim();
                    var urlError = string.IsNullOrEmpty(url) ? "Webhook 地址不能为空" : WebhookSettings.ValidateUrl(url);
                    if (urlError != null) throw new UsageException(urlError + "。", "webhook");
                    List<string> events;
                    try
                    {
                        events = WebhookEvents.Parse(args.GetValue("events"));
                    }
                    catch (ArgumentException ex)
                    {
                        throw new UsageException($"--events 参数错误：{ex.Message}。", "webhook");
                    }
                    NotificationStore.WriteGlobal(new WebhookSettings(url, events));
                    message = "已设置全局 Webhook。正在运行的服务重启后生效。";
                    break;

                case "clear":
                    NotificationStore.WriteGlobal(WebhookSettings.None);
                    message = "已清除全局 Webhook。正在运行的服务重启后生效。";
                    break;

                case "test":
                    return await WebhookTestAsync(args);

                default:
                    throw new UsageException($"未知的操作 '{action}'，可选：show、set、clear、test。", "webhook");
            }

            WriteWebhookResult(args, true, message);
            return ExitCodes.Success;
        }

        /// <summary>
        /// 向生效的 Webhook（指定服务时为服务级优先，否则为全局）发送一条测试事件
        /// </summary>
        private static async Task<int> WebhookTestAsync(ParsedArgs args)
        {
            string id = "", name = "";
            var settings = NotificationStore.ReadGlobal();
            if (args.Positionals.Count > 1)
            {
                var ops = await OpenAsync();
                var service = await ops.FindAsync(args.Positionals[1]);
                id = service.Id;
                name = service.Name;
                settings = WebhookSettings.Resolve(NotificationStore.ReadService(service.Id), settings);
            }
            if (settings is not { Enabled: true })
                throw new CliException(ExitCodes.Failure, "未设置 Webhook（全局与服务级均未设置）。");

            var payload = WebhookPayload.Create(WebhookEvents.Test, id, name, "ServicesApp webhook test", DateTimeOffset.Now, Environment.MachineName);
            var error = await WebhookNotifier.SendAsync(settings.Url, payload, TimeSpan.FromSeconds(20));
            var message = error == null
                ? $"测试事件已发送到 {WebhookSettings.Mask(settings.Url)}。"
                : $"发送到 {WebhookSettings.Mask(settings.Url)} 失败：{error}";

            WriteWebhookResult(args, error == null, message);
            return error == null ? ExitCodes.Success : ExitCodes.Failure;
        }

        private static void WriteWebhookResult(ParsedArgs args, bool ok, string message)
        {
            if (!args.Json)
            {
                (ok ? Console.Out : Console.Error).WriteLine(message);
                return;
            }

            var current = NotificationStore.ReadGlobal();
            WriteJson(new WebhookResult
            {
                Ok = ok,
                Message = message,
                Webhook = current == null ? null : new WebhookInfo
                {
                    Url = args.HasFlag("show-secrets") ? current.Url : WebhookSettings.Mask(current.Url),
                    Events = current.Events.ToList()
                }
            }, CliJsonContext.Readable.WebhookResult);
        }
    }
}
