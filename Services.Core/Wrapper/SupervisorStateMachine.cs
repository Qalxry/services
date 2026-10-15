using System;

namespace Services.Core.Wrapper
{
    public enum SupervisorState
    {
        /// <summary>尚未启动目标程序</summary>
        Idle,
        /// <summary>正在启动目标程序</summary>
        Starting,
        /// <summary>目标程序运行中</summary>
        Running,
        /// <summary>目标程序已退出，等待重启</summary>
        BackingOff,
        /// <summary>目标程序已退出且不再重启，服务即将停止</summary>
        Exited,
        /// <summary>正在停止服务</summary>
        Stopping,
        /// <summary>服务已停止</summary>
        Stopped
    }

    public enum SupervisorAction
    {
        /// <summary>不做任何事（过期事件，或服务正在停止）</summary>
        None,
        /// <summary>目标程序正常退出，正常停止服务</summary>
        StopService,
        /// <summary>以失败状态停止服务（触发 SCM 恢复策略）</summary>
        FailService,
        /// <summary>等待 Delay 后重启目标程序</summary>
        ScheduleRestart
    }

    /// <summary>
    /// 状态机给出的下一步动作。Attempt 为本次重启在当前计数窗口内的序号；
    /// Reason 在 FailService 时为失败原因，在 ScheduleRestart 时为 "Restart"（程序退出后重启）或 "Retry"（启动失败后重试）。
    /// </summary>
    public readonly record struct SupervisorDecision(
        SupervisorAction Action, TimeSpan Delay = default, int Attempt = 0, int ExitCode = 0, string? Reason = null)
    {
        public static SupervisorDecision None { get; } = new(SupervisorAction.None);
    }

    /// <summary>
    /// 包装器生命周期状态机（纯逻辑，不涉及进程与系统调用，可单元测试）。
    /// 每次启动目标程序分配一个递增的代数（generation），退出、重启等事件都带上产生它的代数，
    /// 过期事件（属于已被替换的进程或已取消的重启）一律忽略。
    /// 本类不做线程同步：调用方须在同一把锁内调用全部方法，并在锁内完成目标程序的启动。
    /// </summary>
    public sealed class SupervisorStateMachine
    {
        // 服务以失败状态停止时报告给 SCM 的 Win32 退出码
        public const int ErrorFileNotFound = 2;
        public const int ErrorProcessAborted = 1067;

        private readonly RestartPolicySettings _policy;
        private readonly RestartTracker _restarts;

        public SupervisorStateMachine(RestartPolicySettings policy)
        {
            _policy = policy;
            _restarts = new RestartTracker(policy);
        }

        public SupervisorState State { get; private set; } = SupervisorState.Idle;

        /// <summary>最近一次启动尝试的代数，从 1 开始</summary>
        public int Generation { get; private set; }

        public int RestartCount => _restarts.Count;
        public int TotalRestarts => _restarts.Total;
        public int MaxRestarts => _policy.MaxRestarts;

        public bool IsStopping => State is SupervisorState.Stopping or SupervisorState.Stopped;

        /// <summary>
        /// 服务启动时的首次启动：Idle → Starting。返回本次启动的代数。
        /// </summary>
        public int BeginInitialStart()
        {
            if (State != SupervisorState.Idle)
                throw new InvalidOperationException($"Cannot start from state {State}");
            State = SupervisorState.Starting;
            return ++Generation;
        }

        /// <summary>
        /// 重启等待结束：仅当仍处于由该代数安排的等待中时 BackingOff → Starting。
        /// 期间服务被停止（或已有更新的启动）时返回 false，不应启动。
        /// </summary>
        public bool TryBeginRestart(int scheduledBy, out int generation)
        {
            generation = 0;
            if (State != SupervisorState.BackingOff || Generation != scheduledBy) return false;

            State = SupervisorState.Starting;
            generation = ++Generation;
            return true;
        }

        /// <summary>
        /// 目标程序已启动：Starting → Running
        /// </summary>
        public void ProcessStarted(int generation)
        {
            if (State == SupervisorState.Starting && Generation == generation)
                State = SupervisorState.Running;
        }

        /// <summary>
        /// 目标程序启动失败。程序不存在时直接以失败停止；其余错误按重启策略重试。
        /// </summary>
        public SupervisorDecision ProcessStartFailed(int generation, bool fileNotFound, string exePath, string error, DateTime now)
        {
            if (State != SupervisorState.Starting || Generation != generation) return SupervisorDecision.None;

            if (fileNotFound)
                return Fail(ErrorFileNotFound, $"Executable not found: {exePath}");

            var reason = $"Failed to start process: {error}";
            if (!_policy.Enabled) return Fail(ErrorProcessAborted, reason);
            return ScheduleRestart(now, "Retry") ?? Fail(ErrorProcessAborted, reason);
        }

        /// <summary>
        /// 目标程序退出。服务停止期间或属于旧进程的退出事件被忽略。
        /// </summary>
        public SupervisorDecision ProcessExited(int generation, int exitCode, DateTime now)
        {
            if (State != SupervisorState.Running || Generation != generation) return SupervisorDecision.None;

            if (exitCode == 0)
            {
                State = SupervisorState.Exited;
                return new SupervisorDecision(SupervisorAction.StopService);
            }

            if (!_policy.Enabled)
                return Fail(ErrorProcessAborted, $"Process exited with code {exitCode} (AutoRestart disabled)");

            return ScheduleRestart(now, "Restart")
                ?? Fail(ErrorProcessAborted, $"Process kept failing, gave up after {_policy.MaxRestarts} restarts (last exit code {exitCode})");
        }

        /// <summary>
        /// 开始停止服务。已在停止中或已停止时返回 false（停止流程只执行一次）。
        /// 之后到达的退出、重启事件都会被忽略。
        /// </summary>
        public bool BeginStop()
        {
            if (IsStopping) return false;
            State = SupervisorState.Stopping;
            return true;
        }

        public void Stopped()
        {
            State = SupervisorState.Stopped;
        }

        private SupervisorDecision? ScheduleRestart(DateTime now, string kind)
        {
            if (_restarts.Next(now) is not { } delay) return null;

            State = SupervisorState.BackingOff;
            return new SupervisorDecision(SupervisorAction.ScheduleRestart, delay, _restarts.Count, Reason: kind);
        }

        private SupervisorDecision Fail(int exitCode, string reason)
        {
            State = SupervisorState.Exited;
            return new SupervisorDecision(SupervisorAction.FailService, ExitCode: exitCode, Reason: reason);
        }
    }
}
