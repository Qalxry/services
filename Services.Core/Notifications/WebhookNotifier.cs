using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Services.Core.Notifications
{
    /// <summary>
    /// 向 Webhook POST JSON。失败时按 1、2、4 秒退避重试，总时间不超过上限；从不抛出异常，结果以返回值与回调报告。
    /// </summary>
    public static class WebhookNotifier
    {
        public const int MaxAttempts = 4;
        private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);
        private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>
        /// 发送一次通知。返回 null 表示成功，否则为最后一次失败的说明。
        /// </summary>
        /// <param name="totalTimeout">包括重试在内的总时间上限</param>
        public static async Task<string?> SendAsync(string url, WebhookPayload payload, TimeSpan totalTimeout)
        {
            using var overall = new CancellationTokenSource(totalTimeout);
            string? error = null;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                    attemptCts.CancelAfter(AttemptTimeout);
                    using var content = new StringContent(payload.ToJson(), Encoding.UTF8, "application/json");
                    using var response = await Client.PostAsync(url, content, attemptCts.Token);
                    if (response.IsSuccessStatusCode) return null;
                    error = $"HTTP {(int)response.StatusCode}";
                }
                catch (OperationCanceledException)
                {
                    error = "timed out";
                }
                catch (Exception ex)
                {
                    error = ex.GetBaseException().Message;
                }

                if (attempt == MaxAttempts || overall.IsCancellationRequested) break;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), overall.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            return error;
        }
    }
}
