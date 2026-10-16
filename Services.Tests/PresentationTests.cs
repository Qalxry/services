using Services.Core.Models;
using Services.Core.Presentation;
using Xunit;

namespace Services.Tests
{
    public class ServiceTextTests
    {
        [Theory]
        [InlineData(ServiceRunState.Running, "运行中", "running")]
        [InlineData(ServiceRunState.Stopped, "已停止", "stopped")]
        [InlineData(ServiceRunState.StartPending, "启动中", "start_pending")]
        [InlineData(ServiceRunState.StopPending, "停止中", "stop_pending")]
        [InlineData(ServiceRunState.Paused, "已暂停", "paused")]
        [InlineData(ServiceRunState.Unknown, "未知", "unknown")]
        public void StateTextAndValue(ServiceRunState state, string text, string value)
        {
            Assert.Equal(text, ServiceText.State(state));
            Assert.Equal(value, ServiceText.StateValue(state));
        }

        [Fact]
        public void EveryStateHasDistinctValue()
        {
            var values = Enum.GetValues<ServiceRunState>().Select(ServiceText.StateValue).ToList();
            Assert.Equal(values.Count, values.Distinct().Count());
        }

        [Fact]
        public void StateWithPid()
        {
            Assert.Equal("运行中 (PID 42)", ServiceText.StateWithPid(ServiceRunState.Running, 42));
            Assert.Equal("已停止", ServiceText.StateWithPid(ServiceRunState.Stopped, 0));
        }

        [Fact]
        public void RestartPolicy()
        {
            Assert.Equal("未启用", ServiceText.RestartPolicy(false, 5, 5, 10, true));
            Assert.Equal("已启用（延迟 5 秒，指数退避，最多 3 次，10 分钟后重置计数）", ServiceText.RestartPolicy(true, 5, 3, 10, true));
            Assert.Equal("已启用（延迟 2 秒，不限次数，1 分钟后重置计数）", ServiceText.RestartPolicy(true, 2, 0, 1, false));
        }

        [Fact]
        public void Hooks()
        {
            Assert.Equal("未设置", ServiceText.Hooks(null, " ", 60));
            Assert.Equal("启动前：migrate.cmd（超时 60 秒）", ServiceText.Hooks("migrate.cmd", null, 60));
            Assert.Equal("启动前：a.cmd；停止后：b.cmd {exitcode}（超时 120 秒）", ServiceText.Hooks("a.cmd", "b.cmd {exitcode}", 120));
        }

        [Fact]
        public void ResourceLimits()
        {
            Assert.Equal("不限制", ServiceText.ResourceLimits(0, 0, 0, 0));
            Assert.Equal("内存 512 MB，CPU 50%", ServiceText.ResourceLimits(512, 0, 50, 0));
            Assert.Equal("单进程内存 64 MB，进程数 3", ServiceText.ResourceLimits(0, 64, 0, 3));
        }

        [Fact]
        public void StopPolicy()
        {
            Assert.Equal("直接结束进程", ServiceText.StopPolicy(ServiceStopMode.Kill, null, 30));
            Assert.Equal("发送 Ctrl+C（10 秒未退出则强制结束）", ServiceText.StopPolicy(ServiceStopMode.CtrlC, null, 10));
            Assert.Equal("执行停止命令：nginx -s quit（30 秒未退出则强制结束）", ServiceText.StopPolicy(ServiceStopMode.Command, "nginx -s quit", 30));
        }
    }

    public class ServiceDetailsTests
    {
        private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0);

        private static Service Sample() => new()
        {
            Id = "WinSvcMgr_App_1",
            Name = "App",
            State = ServiceRunState.Running,
            Pid = 100,
            ExePath = @"C:\app\app.exe",
            StartupType = ServiceStartupType.Auto,
            DelayedAutoStart = true,
            AccountName = "LocalSystem",
            CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5)
        };

        private static string? Value(List<DetailRow> rows, string label) => rows.Single(r => r.Label == label).Value;

        [Fact]
        public void BuildsRowsFromServiceAndRuntime()
        {
            var runtime = new ServiceRuntimeState
            {
                ServiceId = "WinSvcMgr_App_1",
                WrapperPid = 100,
                WrapperStartedAt = Now.AddMinutes(-5),
                RestartCount = 1,
                TotalRestarts = 3,
                LastExitCode = 1,
                LastExitAt = new DateTime(2026, 10, 15, 11, 0, 0),
                LastFailureReason = "boom"
            };

            var rows = ServiceDetails.Build(Sample(), runtime, @"C:\logs\a.log", Now);

            Assert.Equal("运行中 (PID 100)", Value(rows, "状态"));
            Assert.Equal("5 分 0 秒", Value(rows, "运行时长"));
            Assert.Equal("本轮 1 次，累计 3 次", Value(rows, "重启次数"));
            Assert.Equal("退出码 1（2026-10-15 11:00:00）", Value(rows, "上次退出"));
            Assert.Equal("boom", Value(rows, "最近失败原因"));
            Assert.Equal("延迟自动启动", Value(rows, "启动类型"));
            Assert.Equal("2026-01-02 03:04:05", Value(rows, "创建时间"));
            Assert.Equal(@"C:\logs\a.log", Value(rows, "最新日志"));
            Assert.True(rows.Single(r => r.Label == "可执行文件路径").Monospace);
        }

        [Fact]
        public void OptionalRowsAreOmittedOrEmpty()
        {
            var rows = ServiceDetails.Build(Sample(), runtime: null, latestLogPath: null, Now);

            Assert.DoesNotContain(rows, r => r.Label == "最新日志");
            Assert.DoesNotContain(rows, r => r.Label == "最近失败原因");
            Assert.Null(Value(rows, "运行时长"));
            Assert.Null(Value(rows, "重启次数"));
        }

        [Fact]
        public void LabelsAreUnique()
        {
            var rows = ServiceDetails.Build(Sample(), new ServiceRuntimeState { LastFailureReason = "x" }, "log", Now);
            Assert.Equal(rows.Count, rows.Select(r => r.Label).Distinct().Count());
        }
    }
}
