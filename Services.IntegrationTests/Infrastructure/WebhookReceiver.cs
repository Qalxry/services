using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Services.IntegrationTests.Infrastructure
{
    /// <summary>
    /// 本地最简 HTTP 接收端：记录收到的 POST 内容（JSON），一律返回 200
    /// </summary>
    internal sealed class WebhookReceiver : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentQueue<JsonElement> _received = new();

        public string Url { get; }

        public WebhookReceiver()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/hook";
            _ = Task.Run(AcceptLoopAsync);
        }

        public IReadOnlyList<JsonElement> Received => _received.ToList();

        public JsonElement WaitFor(string eventName, string? serviceId = null, TimeSpan? timeout = null)
        {
            JsonElement? match = null;
            Wait.Until(() =>
            {
                match = Received.Cast<JsonElement?>().FirstOrDefault(e =>
                    e!.Value.GetProperty("event").GetString() == eventName &&
                    (serviceId == null || e.Value.GetProperty("serviceId").GetString() == serviceId));
                return match != null;
            }, $"收到 Webhook 事件 {eventName}", timeout, () => string.Join("\n", Received.Select(r => r.ToString())));
            return match!.Value;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new MemoryStream();
                    var chunk = new byte[8192];
                    int headerEnd = -1, contentLength = 0;
                    while (true)
                    {
                        int read = await stream.ReadAsync(chunk);
                        if (read <= 0) break;
                        buffer.Write(chunk, 0, read);
                        var text = Encoding.UTF8.GetString(buffer.ToArray());
                        if (headerEnd < 0)
                        {
                            headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                            if (headerEnd < 0) continue;
                            foreach (var line in text[..headerEnd].Split("\r\n"))
                            {
                                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                    contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                            }
                        }
                        var bodyBytes = buffer.ToArray().Skip(Encoding.UTF8.GetByteCount(text[..(headerEnd + 4)])).ToArray();
                        if (bodyBytes.Length >= contentLength)
                        {
                            _received.Enqueue(JsonDocument.Parse(bodyBytes).RootElement.Clone());
                            break;
                        }
                    }
                    var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response);
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }
}
