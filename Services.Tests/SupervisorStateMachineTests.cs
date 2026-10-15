using Services.Core.Wrapper;
using Xunit;

namespace Services.Tests
{
    public class RestartTrackerTests
    {
        private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

        [Fact]
        public void FixedDelayUntilMaxRestarts()
        {
            var tracker = new RestartTracker(new RestartPolicySettings(true, 5, 2, 10, false));

            Assert.Equal(TimeSpan.FromSeconds(5), tracker.Next(T0));
            Assert.Equal(TimeSpan.FromSeconds(5), tracker.Next(T0.AddSeconds(10)));
            Assert.Null(tracker.Next(T0.AddSeconds(20)));
            Assert.Equal(2, tracker.Total);
        }

        [Fact]
        public void BackoffDoublesUpToSixteenTimes()
        {
            var tracker = new RestartTracker(new RestartPolicySettings(true, 2, 0, 60, true));
            var delays = Enumerable.Range(0, 7).Select(i => tracker.Next(T0.AddSeconds(i))!.Value.TotalSeconds).ToList();
            Assert.Equal(new double[] { 2, 4, 8, 16, 32, 32, 32 }, delays);
        }

        [Fact]
        public void CountResetsAfterStablePeriod()
        {
            var tracker = new RestartTracker(new RestartPolicySettings(true, 1, 2, 10, true));
            tracker.Next(T0);
            tracker.Next(T0.AddMinutes(1));
            Assert.Equal(2, tracker.Count);

            // 距上次重启超过 10 分钟：重新计数，退避也从头开始
            Assert.Equal(TimeSpan.FromSeconds(1), tracker.Next(T0.AddMinutes(12)));
            Assert.Equal(1, tracker.Count);
            Assert.Equal(3, tracker.Total);
        }

        [Fact]
        public void ZeroMaxRestartsMeansUnlimited()
        {
            var tracker = new RestartTracker(new RestartPolicySettings(true, 1, 0, 10, false));
            for (int i = 0; i < 100; i++) Assert.NotNull(tracker.Next(T0.AddSeconds(i)));
        }

        [Fact]
        public void DelayIsClamped()
        {
            Assert.Equal(TimeSpan.FromSeconds(1), new RestartTracker(new RestartPolicySettings(true, 0, 0, 10, false)).Next(T0));
            Assert.Equal(TimeSpan.FromHours(1), new RestartTracker(new RestartPolicySettings(true, 99999, 0, 10, false)).Next(T0));
        }
    }

    public class SupervisorStateMachineTests
    {
        private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

        private static SupervisorStateMachine Running(out int generation, bool autoRestart = true, int maxRestarts = 2)
        {
            var machine = new SupervisorStateMachine(new RestartPolicySettings(autoRestart, 1, maxRestarts, 10, false));
            generation = machine.BeginInitialStart();
            machine.ProcessStarted(generation);
            Assert.Equal(SupervisorState.Running, machine.State);
            return machine;
        }

        [Fact]
        public void ZeroExitStopsService()
        {
            var machine = Running(out var gen);
            var decision = machine.ProcessExited(gen, 0, T0);
            Assert.Equal(SupervisorAction.StopService, decision.Action);
            Assert.Equal(SupervisorState.Exited, machine.State);
        }

        [Fact]
        public void NonZeroExitWithoutAutoRestartFails()
        {
            var machine = Running(out var gen, autoRestart: false);
            var decision = machine.ProcessExited(gen, 3, T0);
            Assert.Equal(SupervisorAction.FailService, decision.Action);
            Assert.Equal(SupervisorStateMachine.ErrorProcessAborted, decision.ExitCode);
            Assert.Contains("AutoRestart disabled", decision.Reason);
        }

        [Fact]
        public void CrashRestartsThenGivesUp()
        {
            var machine = Running(out var gen);

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                var decision = machine.ProcessExited(gen, 3, T0.AddSeconds(attempt));
                Assert.Equal(SupervisorAction.ScheduleRestart, decision.Action);
                Assert.Equal(attempt, decision.Attempt);
                Assert.Equal("Restart", decision.Reason);
                Assert.Equal(SupervisorState.BackingOff, machine.State);

                Assert.True(machine.TryBeginRestart(gen, out var next));
                Assert.Equal(gen + 1, next);
                gen = next;
                machine.ProcessStarted(gen);
            }

            var final = machine.ProcessExited(gen, 3, T0.AddSeconds(10));
            Assert.Equal(SupervisorAction.FailService, final.Action);
            Assert.Contains("gave up after 2 restarts (last exit code 3)", final.Reason);
            Assert.Equal(2, machine.TotalRestarts);
        }

        [Fact]
        public void StopDuringBackoffCancelsRestart()
        {
            var machine = Running(out var gen);
            Assert.Equal(SupervisorAction.ScheduleRestart, machine.ProcessExited(gen, 1, T0).Action);

            Assert.True(machine.BeginStop());

            Assert.False(machine.TryBeginRestart(gen, out _));
            Assert.Equal(SupervisorState.Stopping, machine.State);
        }

        [Fact]
        public void ExitDuringStopIsIgnored()
        {
            var machine = Running(out var gen);
            Assert.True(machine.BeginStop());

            Assert.Equal(SupervisorAction.None, machine.ProcessExited(gen, 1, T0).Action);
            Assert.Equal(SupervisorAction.None, machine.ProcessExited(gen, 0, T0).Action);
            Assert.Equal(SupervisorState.Stopping, machine.State);
        }

        [Fact]
        public void StopRunsOnlyOnce()
        {
            var machine = Running(out _);
            Assert.True(machine.BeginStop());
            Assert.False(machine.BeginStop());
            machine.Stopped();
            Assert.False(machine.BeginStop());
            Assert.Equal(SupervisorState.Stopped, machine.State);
        }

        [Fact]
        public void StaleExitEventIsIgnored()
        {
            var machine = Running(out var first);
            machine.ProcessExited(first, 1, T0);
            machine.TryBeginRestart(first, out var second);
            machine.ProcessStarted(second);

            // 旧进程的退出事件迟到
            Assert.Equal(SupervisorAction.None, machine.ProcessExited(first, 1, T0).Action);
            Assert.Equal(SupervisorState.Running, machine.State);
        }

        [Fact]
        public void StaleRestartTimerIsIgnored()
        {
            var machine = Running(out var first);
            machine.ProcessExited(first, 1, T0);
            Assert.True(machine.TryBeginRestart(first, out var second));
            machine.ProcessStarted(second);

            // 同一次等待被重复触发
            Assert.False(machine.TryBeginRestart(first, out _));
            Assert.Equal(second, machine.Generation);
        }

        [Fact]
        public void ExitAfterServiceDecidedToStopIsIgnored()
        {
            var machine = Running(out var gen);
            Assert.Equal(SupervisorAction.StopService, machine.ProcessExited(gen, 0, T0).Action);
            Assert.Equal(SupervisorAction.None, machine.ProcessExited(gen, 1, T0).Action);
            Assert.True(machine.BeginStop());
        }

        [Fact]
        public void MissingExecutableFailsWithoutRetry()
        {
            var machine = new SupervisorStateMachine(new RestartPolicySettings(true, 1, 0, 10, false));
            var gen = machine.BeginInitialStart();

            var decision = machine.ProcessStartFailed(gen, fileNotFound: true, @"C:\app.exe", "not found", T0);

            Assert.Equal(SupervisorAction.FailService, decision.Action);
            Assert.Equal(SupervisorStateMachine.ErrorFileNotFound, decision.ExitCode);
            Assert.Equal(@"Executable not found: C:\app.exe", decision.Reason);
        }

        [Fact]
        public void StartFailureIsRetriedWhenAutoRestartEnabled()
        {
            var machine = new SupervisorStateMachine(new RestartPolicySettings(true, 1, 1, 10, false));
            var gen = machine.BeginInitialStart();

            var retry = machine.ProcessStartFailed(gen, false, "app.exe", "access denied", T0);
            Assert.Equal(SupervisorAction.ScheduleRestart, retry.Action);
            Assert.Equal("Retry", retry.Reason);

            Assert.True(machine.TryBeginRestart(gen, out var next));
            var fail = machine.ProcessStartFailed(next, false, "app.exe", "access denied", T0.AddSeconds(2));
            Assert.Equal(SupervisorAction.FailService, fail.Action);
            Assert.Equal("Failed to start process: access denied", fail.Reason);
        }

        [Fact]
        public void StartFailureWithoutAutoRestartFails()
        {
            var machine = new SupervisorStateMachine(new RestartPolicySettings(false, 1, 0, 10, false));
            var gen = machine.BeginInitialStart();
            Assert.Equal(SupervisorAction.FailService, machine.ProcessStartFailed(gen, false, "app.exe", "boom", T0).Action);
        }

        [Fact]
        public void InitialStartOnlyOnce()
        {
            var machine = new SupervisorStateMachine(RestartPolicySettings.Default);
            machine.BeginInitialStart();
            Assert.Throws<InvalidOperationException>(() => machine.BeginInitialStart());
        }
    }
}
