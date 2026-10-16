using System.Globalization;
using System.Text;
using System.Text.Json;
using Services.Core.Helpers;
using Services.Core.Models;
using Services.Core.Services;

namespace Services.Cli
{
    internal static partial class CliApp
    {
        private const int DefaultLogTail = 200;
        private const int FollowPollInterval = 500;

        private static async Task<int> LogsAsync(ParsedArgs args)
        {
            int tail = args.GetInt("tail", 0, int.MaxValue) ?? DefaultLogTail;
            bool raw = args.HasFlag("raw");
            bool follow = args.HasFlag("follow");

            var stream = (args.GetValue("stream") ?? "all").ToLowerInvariant();
            if (stream is not ("all" or "stdout" or "stderr"))
                throw new UsageException($"--stream 只能是 all、stdout 或 stderr，实际为 '{stream}'。", "logs");

            DateTime? since = args.GetValue("since") is { } sinceText ? LogTimeParser.ParseSince(sinceText, DateTime.Now) : null;

            var ops = await OpenAsync();
            var service = await ops.FindAsync(args.Positionals[0]);

            var logManager = ops.Logs;
            var logPath = logManager.GetLatestLogPath(service.Id);

            if (follow)
                return await FollowLogsAsync(args, service, logManager, logPath, tail, stream, since);

            var lines = logPath == null ? new List<string>() : ReadTail(logPath, tail, line => MatchesLogFilter(line, stream, since));

            if (args.Json)
            {
                var result = new LogsResult { Id = service.Id, LogPath = logPath };
                foreach (var line in lines)
                {
                    result.Lines.Add(ToLogLineInfo(line, raw));
                }
                WriteJson(result, CliJsonContext.Readable.LogsResult);
                return ExitCodes.Success;
            }

            if (logPath == null)
            {
                Console.Error.WriteLine($"服务 '{service.Name}' 暂无日志。");
                return ExitCodes.Success;
            }

            foreach (var line in lines)
            {
                Console.WriteLine(raw ? line : LogLine.StripAnsi(line));
            }
            return ExitCodes.Success;
        }

        /// <summary>
        /// 先输出末尾内容，再每 500ms 轮询新写入的行，直到 Ctrl+C。
        /// 服务重启会生成新的日志文件，检测到最新文件变化时读完旧文件剩余内容后切换。
        /// </summary>
        private static async Task<int> FollowLogsAsync(ParsedArgs args, Service service, LogManager logManager,
            string? logPath, int tail, string stream, DateTime? since)
        {
            bool raw = args.HasFlag("raw");

            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                // 取消默认的终止行为，让循环正常结束并以退出码 0 返回
                e.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += onCancel;

            try
            {
                void Emit(string line)
                {
                    if (!MatchesLogFilter(line, stream, since)) return;

                    if (args.Json)
                        Console.WriteLine(JsonSerializer.Serialize(ToLogLineInfo(line, raw), CliJsonContext.Compact.LogLineInfo));
                    else
                        Console.WriteLine(raw ? line : LogLine.StripAnsi(line));
                }

                long position = 0;
                if (logPath == null)
                {
                    Console.Error.WriteLine($"服务 '{service.Name}' 暂无日志，等待日志生成...（Ctrl+C 退出）");
                }
                else
                {
                    var buffer = new Queue<string>();
                    ReadNewLines(logPath, ref position, line =>
                    {
                        if (!MatchesLogFilter(line, stream, since)) return;
                        buffer.Enqueue(line);
                        if (tail > 0 && buffer.Count > tail) buffer.Dequeue();
                    });
                    foreach (var line in buffer) Emit(line);
                }

                var currentPath = logPath;
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(FollowPollInterval, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    try
                    {
                        var latest = logManager.GetLatestLogPath(service.Id);
                        if (latest != null && !string.Equals(latest, currentPath, StringComparison.OrdinalIgnoreCase))
                        {
                            // 先读完旧文件中尚未输出的内容，再切换到新文件
                            if (currentPath != null)
                            {
                                try { ReadNewLines(currentPath, ref position, Emit); }
                                catch (IOException) { }
                            }
                            Console.Error.WriteLine($"日志文件已切换: {latest}");
                            currentPath = latest;
                            position = 0;
                        }

                        if (currentPath != null)
                            ReadNewLines(currentPath, ref position, Emit);
                    }
                    catch (IOException)
                    {
                        // 文件被清理或暂时无法读取，下一轮重试
                    }
                }

                return ExitCodes.Success;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        private static bool MatchesLogFilter(string line, string stream, DateTime? since)
        {
            var parsed = LogLine.Parse(line);

            // 指定 --since 时，没有时间戳的旧格式行无法判断时间，一律排除
            if (since != null && (parsed.Timestamp == null || parsed.Timestamp < since)) return false;

            return stream == "all" || parsed.Kind == LogLineKind.System ||
                   (stream == "stdout" ? parsed.Kind == LogLineKind.Stdout : parsed.Kind == LogLineKind.Stderr);
        }

        private static LogLineInfo ToLogLineInfo(string line, bool raw)
        {
            var parsed = LogLine.Parse(line);
            return new LogLineInfo
            {
                Timestamp = parsed.Timestamp?.ToString(LogLine.TimestampFormat, CultureInfo.InvariantCulture),
                Stream = parsed.Kind.ToString().ToLowerInvariant(),
                Text = raw ? parsed.Text : LogLine.StripAnsi(parsed.Text)
            };
        }

        /// <summary>
        /// 读取日志文件中满足条件的最后 count 行（count 为 0 时读取全部）。允许服务同时写入。
        /// </summary>
        private static List<string> ReadTail(string path, int count, Func<string, bool> filter)
        {
            var buffer = new Queue<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!filter(line)) continue;
                buffer.Enqueue(line);
                if (count > 0 && buffer.Count > count) buffer.Dequeue();
            }
            return buffer.ToList();
        }

        /// <summary>
        /// 从 position 开始读取文件中新增的完整行（以换行符结尾）并逐行回调，position 前进到最后一个换行符之后；
        /// 未写完的最后一行留待下次读取。文件变小（被截断）时从头开始。
        /// </summary>
        private static void ReadNewLines(string path, ref long position, Action<string> onLine)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < position) position = 0;

            long start = position;
            long remaining = fs.Length - start;
            fs.Seek(start, SeekOrigin.Begin);

            var chunk = new byte[64 * 1024];
            using var lineBytes = new MemoryStream();
            long offset = 0;

            while (remaining > 0)
            {
                int read = fs.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
                if (read <= 0) break;
                remaining -= read;

                int lineStart = 0;
                for (int i = 0; i < read; i++)
                {
                    if (chunk[i] != (byte)'\n') continue;

                    lineBytes.Write(chunk, lineStart, i - lineStart);
                    var line = Encoding.UTF8.GetString(lineBytes.GetBuffer(), 0, (int)lineBytes.Length);
                    onLine(line.TrimEnd('\r'));

                    lineBytes.SetLength(0);
                    lineStart = i + 1;
                    position = start + offset + i + 1;
                }
                lineBytes.Write(chunk, lineStart, read - lineStart);
                offset += read;
            }
        }
    }
}
