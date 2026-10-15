using System.Text.Json;

namespace Services.IntegrationTests.Infrastructure
{
    /// <summary>
    /// 通过 servicesctl 创建的测试服务，Dispose 时删除（失败也不抛出，以免掩盖测试本身的错误）
    /// </summary>
    internal sealed class TestService : IDisposable
    {
        public string Id { get; }
        public string Name { get; }

        private TestService(string id, string name)
        {
            Id = id;
            Name = name;
        }

        /// <summary>
        /// 新名称：IT_&lt;用途&gt;_&lt;随机&gt;，以 IT_ 开头的服务会在下次运行开始时被清理
        /// </summary>
        public static string NewName(string purpose) =>
            $"{ItEnvironment.NamePrefix}{purpose}_{Guid.NewGuid().ToString("N")[..6]}";

        /// <summary>
        /// 创建以 testtarget.exe 为目标程序的服务
        /// </summary>
        /// <param name="targetArgs">testtarget 的参数</param>
        /// <param name="options">额外的 create 选项，如 --auto-restart</param>
        public static TestService Create(string purpose, string targetArgs, params string[] options)
        {
            var name = NewName(purpose);
            var args = new List<string> { "create", "--name", name, "--exe", ItEnvironment.TargetPath, "--args", targetArgs };
            args.AddRange(options);
            var result = Ctl.Json(args.ToArray());
            return new TestService(result.GetProperty("id").GetString()!, name);
        }

        /// <summary>
        /// 接管已由其他方式（如 apply）创建的服务，以便测试结束时删除
        /// </summary>
        public static TestService Adopt(string id, string name) => new(id, name);

        public JsonElement Get() => Ctl.Json("get", Id);

        public string State => Get().GetProperty("state").GetString()!;

        public void Start() => Ctl.Json("start", Id);

        public void Stop() => Ctl.Json("stop", Id);

        public void WaitForState(string state, TimeSpan? timeout = null) =>
            Wait.Until(() => State == state, $"服务 {Name} 进入 {state} 状态", timeout, Describe);

        /// <summary>
        /// 目标程序的进程 ID（来自包装器写入的运行状态）
        /// </summary>
        public int? ProcessPid
        {
            get
            {
                var runtime = Get().GetProperty("runtime");
                if (runtime.ValueKind == JsonValueKind.Null) return null;
                var pid = runtime.GetProperty("processPid");
                return pid.ValueKind == JsonValueKind.Number ? pid.GetInt32() : null;
            }
        }

        public List<LogEntry> Logs()
        {
            var result = Ctl.Json("logs", Id, "--tail", "0");
            return result.GetProperty("lines").EnumerateArray()
                .Select(line => new LogEntry(
                    line.GetProperty("timestamp").GetString(),
                    line.GetProperty("stream").GetString()!,
                    line.GetProperty("text").GetString()!))
                .ToList();
        }

        public bool HasLog(string text, string? stream = null) =>
            Logs().Any(l => l.Text == text && (stream == null || l.Stream == stream));

        public bool HasLogContaining(string text) => Logs().Any(l => l.Text.Contains(text, StringComparison.Ordinal));

        public void WaitForLog(string text, string? stream = null, TimeSpan? timeout = null) =>
            Wait.Until(() => HasLog(text, stream), $"服务 {Name} 的日志出现 \"{text}\"", timeout, Describe);

        public void WaitForLogContaining(string text, TimeSpan? timeout = null) =>
            Wait.Until(() => HasLogContaining(text), $"服务 {Name} 的日志包含 \"{text}\"", timeout, Describe);

        /// <summary>
        /// 失败时附带的诊断信息：服务详情与日志
        /// </summary>
        public string Describe()
        {
            var get = Ctl.Run("get", Id);
            var logs = Ctl.Run("logs", Id, "--tail", "50");
            return $"{get.Stdout}\n--- logs ---\n{logs.Stdout}{logs.Stderr}";
        }

        public void Dispose()
        {
            try
            {
                Ctl.Run("delete", Id);
            }
            catch
            {
            }
        }
    }

    internal sealed record LogEntry(string? Timestamp, string Stream, string Text);
}
