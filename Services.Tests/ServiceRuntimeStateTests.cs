using Services.Core.Helpers;
using Services.Core.Models;
using Services.Core.Services;
using Xunit;

namespace Services.Tests
{
    public class ServiceRuntimeStateTests : IDisposable
    {
        private static readonly DateTime Start = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Local);

        private readonly string _logDir = Path.Combine(Path.GetTempPath(), "svc_state_tests_" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_logDir, recursive: true); } catch { }
        }

        private static ServiceRuntimeState Sample() => new()
        {
            ServiceId = "WinSvcMgr_Demo_ab12cd34",
            WrapperPid = 1234,
            WrapperStartedAt = Start,
            ProcessPid = 5678,
            ProcessStartedAt = Start.AddSeconds(2.5),
            LastExitCode = -1073741819,
            LastExitAt = Start.AddMinutes(5),
            RestartCount = 2,
            TotalRestarts = 7,
            LastFailureReason = "Process kept failing, gave up after 5 restarts (last exit code 1)",
            UpdatedAt = Start.AddMinutes(6)
        };

        // ---- 序列化（源生成上下文） ----

        [Fact]
        public void Json_RoundTripsAllFields()
        {
            var state = Sample();

            var back = ServiceStateStore.Deserialize(ServiceStateStore.Serialize(state));

            Assert.NotNull(back);
            Assert.Equal(state.ServiceId, back!.ServiceId);
            Assert.Equal(state.WrapperPid, back.WrapperPid);
            Assert.Equal(state.WrapperStartedAt, back.WrapperStartedAt);
            Assert.Equal(state.ProcessPid, back.ProcessPid);
            Assert.Equal(state.ProcessStartedAt, back.ProcessStartedAt);
            Assert.Equal(state.LastExitCode, back.LastExitCode);
            Assert.Equal(state.LastExitAt, back.LastExitAt);
            Assert.Equal(state.RestartCount, back.RestartCount);
            Assert.Equal(state.TotalRestarts, back.TotalRestarts);
            Assert.Equal(state.LastFailureReason, back.LastFailureReason);
            Assert.Equal(state.UpdatedAt, back.UpdatedAt);
        }

        [Fact]
        public void Json_RoundTripsNullableFieldsAsNull()
        {
            var state = new ServiceRuntimeState { ServiceId = "s", WrapperPid = 1, WrapperStartedAt = Start, UpdatedAt = Start };

            var back = ServiceStateStore.Deserialize(ServiceStateStore.Serialize(state));

            Assert.NotNull(back);
            Assert.Null(back!.ProcessPid);
            Assert.Null(back.ProcessStartedAt);
            Assert.Null(back.LastExitCode);
            Assert.Null(back.LastExitAt);
            Assert.Null(back.LastFailureReason);
        }

        [Fact]
        public void Json_UsesCamelCasePropertyNames()
        {
            var json = ServiceStateStore.Serialize(Sample());

            Assert.Contains("\"wrapperPid\": 1234", json);
            Assert.Contains("\"totalRestarts\": 7", json);
            Assert.Contains("\"lastExitCode\": -1073741819", json);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("{\"wrapperPid\": \"abc\"")]
        public void Deserialize_InvalidContent_ReturnsNull(string json)
        {
            Assert.Null(ServiceStateStore.Deserialize(json));
        }

        // ---- 状态文件读写 ----

        [Fact]
        public void Store_WriteThenRead_RoundTripsAndLeavesNoTempFile()
        {
            var store = new ServiceStateStore(_logDir);
            var state = Sample();

            Assert.True(store.Write(state));

            var back = store.Read(state.ServiceId);
            Assert.NotNull(back);
            Assert.Equal(state.TotalRestarts, back!.TotalRestarts);
            Assert.Equal(state.WrapperStartedAt, back.WrapperStartedAt);
            Assert.Equal(Path.Combine(_logDir, "state", state.ServiceId + ".json"), store.GetStatePath(state.ServiceId));
            Assert.Equal(new[] { store.GetStatePath(state.ServiceId) }, Directory.GetFiles(Path.Combine(_logDir, "state")));
        }

        [Fact]
        public void Store_WriteOverwritesExistingFile()
        {
            var store = new ServiceStateStore(_logDir);
            var state = Sample();
            store.Write(state);

            state.TotalRestarts = 99;
            Assert.True(store.Write(state));

            Assert.Equal(99, store.Read(state.ServiceId)!.TotalRestarts);
        }

        [Fact]
        public void Store_Read_MissingOrCorruptFile_ReturnsNull()
        {
            var store = new ServiceStateStore(_logDir);
            Assert.Null(store.Read("missing"));

            Directory.CreateDirectory(Path.Combine(_logDir, "state"));
            File.WriteAllText(store.GetStatePath("broken"), "{ not json");
            Assert.Null(store.Read("broken"));
        }

        [Fact]
        public void Store_Delete_RemovesFileAndToleratesMissing()
        {
            var store = new ServiceStateStore(_logDir);
            var state = Sample();
            store.Write(state);

            store.Delete(state.ServiceId);
            Assert.Null(store.Read(state.ServiceId));

            store.Delete(state.ServiceId);
            store.Delete("never_existed");
        }

        [Fact]
        public void Store_Write_FailureReturnsFalseWithoutThrowing()
        {
            // 把“日志目录”指向一个已存在的文件，使创建 state 子目录必然失败
            Directory.CreateDirectory(_logDir);
            var blocker = Path.Combine(_logDir, "blocker");
            File.WriteAllText(blocker, "x");
            var store = new ServiceStateStore(blocker);

            Assert.False(store.Write(Sample()));
            Assert.Null(store.Read("any"));
            store.Delete("any");
        }

        [Fact]
        public void Store_ServiceIdWithPathCharacters_StaysInsideStateDirectory()
        {
            var store = new ServiceStateStore(_logDir);

            var path = store.GetStatePath(".." + Path.DirectorySeparatorChar + "evil");

            Assert.Equal(Path.Combine(_logDir, "state"), Path.GetDirectoryName(path));
        }

        // ---- 运行时长判定 ----

        [Fact]
        public void GetUptime_RunningAndPidMatches_ReturnsElapsed()
        {
            var uptime = ServiceRuntimeState.GetUptime(Sample(), 1234, true, Start.AddHours(3));

            Assert.Equal(TimeSpan.FromHours(3), uptime);
        }

        [Fact]
        public void GetUptime_NoState_ReturnsNull()
        {
            Assert.Null(ServiceRuntimeState.GetUptime(null, 1234, true, Start.AddHours(1)));
        }

        [Fact]
        public void GetUptime_NotRunning_ReturnsNull()
        {
            Assert.Null(ServiceRuntimeState.GetUptime(Sample(), 1234, false, Start.AddHours(1)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(9999)]
        public void GetUptime_PidMissingOrDifferent_ReturnsNull(int servicePid)
        {
            Assert.Null(ServiceRuntimeState.GetUptime(Sample(), servicePid, true, Start.AddHours(1)));
        }

        [Fact]
        public void GetUptime_ClockBeforeStart_ClampsToZero()
        {
            Assert.Equal(TimeSpan.Zero, ServiceRuntimeState.GetUptime(Sample(), 1234, true, Start.AddMinutes(-5)));
        }

        // ---- 展示文本 ----

        [Fact]
        public void RuntimeText_LastExit_FormatsCodeAndTime()
        {
            Assert.Null(ServiceRuntimeText.LastExit(null, null));
            Assert.Equal("退出码 1（2026-10-15 08:05:00）", ServiceRuntimeText.LastExit(1, new DateTime(2026, 10, 15, 8, 5, 0)));
            Assert.Equal("退出码 0", ServiceRuntimeText.LastExit(0, null));
        }

        [Fact]
        public void RuntimeText_Restarts_ShowsBothCounts()
        {
            Assert.Equal("本轮 2 次，累计 7 次", ServiceRuntimeText.Restarts(2, 7));
        }
    }

    public class DurationFormatterTests
    {
        [Theory]
        [InlineData(0, "0 秒")]
        [InlineData(1, "1 秒")]
        [InlineData(59, "59 秒")]
        [InlineData(60, "1 分 0 秒")]
        [InlineData(312, "5 分 12 秒")]
        [InlineData(3599, "59 分 59 秒")]
        [InlineData(3600, "1 小时 0 分")]
        [InlineData(7380, "2 小时 3 分")]
        [InlineData(86399, "23 小时 59 分")]
        [InlineData(86400, "1 天 0 小时")]
        [InlineData(3 * 86400 + 4 * 3600 + 59, "3 天 4 小时")]
        [InlineData(400 * 86400, "400 天 0 小时")]
        public void Format_UsesTwoUnitsAndTruncates(int seconds, string expected)
        {
            Assert.Equal(expected, DurationFormatter.Format(TimeSpan.FromSeconds(seconds)));
        }

        [Fact]
        public void Format_IgnoresFractionalSeconds()
        {
            Assert.Equal("59 秒", DurationFormatter.Format(TimeSpan.FromMilliseconds(59999)));
        }

        [Fact]
        public void Format_NegativeIsTreatedAsZero()
        {
            Assert.Equal("0 秒", DurationFormatter.Format(TimeSpan.FromSeconds(-30)));
        }
    }
}
