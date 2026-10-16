using System;
using System.Threading;
using Services.Core.Wrapper;

namespace Services.Core.Services
{
    /// <summary>
    /// 包装器：钩子命令（启动前 / 停止后）。命令可能耗时，一律在 _gate 锁外执行。
    /// 启动前命令在每次启动目标程序之前执行（含自动重启），失败按启动失败处理；
    /// 停止后命令在目标程序每次退出之后执行（含重启之前与停止服务时），每个进程只执行一次，失败只记录。
    /// </summary>
    public partial class EmbeddedServiceWrapper
    {
        // 最近一次成功启动的目标进程（代数与 PID），以及已认领停止后命令的代数
        private int _startedGeneration;
        private int _startedPid;
        private int _postStopClaimed;
        // 退出事件正在执行停止后命令时为未触发；停止流程等待其完成后再结束服务
        private readonly ManualResetEventSlim _postStopIdle = new(true);

        /// <summary>
        /// 执行启动前命令并启动目标程序（调用方不持有 _gate，状态机处于该代数的 Starting）。返回启动失败时的后续动作。
        /// </summary>
        /// <param name="requestAdditionalTime">在 OnStart 中执行时向 SCM 申请延长启动时间</param>
        private SupervisorDecision LaunchTarget(int generation, Action requestAdditionalTime)
        {
            var hooks = _settings!.Hooks;
            if (hooks.HasPreStart)
            {
                var result = CreateHookRunner().Run(HookKind.PreStart, hooks.PreStart, hooks.TimeoutSeconds,
                    pid: null, exitCode: null, requestAdditionalTime, _stopCts.Token);
                if (!result.Succeeded)
                {
                    lock (_gate)
                    {
                        // 服务已在此期间停止（命令被取消）时状态机返回 None
                        return _machine!.PreStartFailed(generation, result.Error!, DateTime.Now);
                    }
                }
            }

            lock (_gate)
            {
                // 启动前命令执行期间服务被停止
                if (!_machine!.IsStarting(generation)) return SupervisorDecision.None;
                return StartTargetLocked(generation);
            }
        }

        /// <summary>
        /// 记录已启动的目标进程（调用方持有 _gate）
        /// </summary>
        private void TargetStartedLocked(int generation, int pid)
        {
            _startedGeneration = generation;
            _startedPid = pid;
        }

        /// <summary>
        /// 认领该代数目标进程的停止后命令（调用方持有 _gate）。返回进程 PID；
        /// 未配置命令、进程未启动过或已被认领时返回 null。
        /// </summary>
        private int? ClaimPostStopLocked(int generation)
        {
            if (_settings?.Hooks.HasPostStop != true || generation == 0 ||
                generation != _startedGeneration || generation <= _postStopClaimed)
                return null;

            _postStopClaimed = generation;
            _postStopIdle.Reset();
            return _startedPid;
        }

        /// <summary>
        /// 执行已认领的停止后命令。不响应服务停止的取消：清理工作应完成，超时由命令自身的超时控制。
        /// </summary>
        private void RunPostStop(int pid, int? exitCode, int timeoutSeconds, Action requestAdditionalTime)
        {
            try
            {
                CreateHookRunner().Run(HookKind.PostStop, _settings!.Hooks.PostStop, timeoutSeconds,
                    pid, exitCode, requestAdditionalTime, CancellationToken.None);
            }
            finally
            {
                _postStopIdle.Set();
            }
        }

        /// <summary>
        /// 停止服务时等待退出事件中正在执行的停止后命令完成（最多其超时再加 5 秒）
        /// </summary>
        private void WaitForPostStop(int timeoutSeconds, Action requestAdditionalTime)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds + 5);
            while (!_postStopIdle.Wait(2000) && DateTime.UtcNow < deadline)
            {
                try { requestAdditionalTime(); } catch { }
            }
        }

        private HookRunner CreateHookRunner()
        {
            var settings = _settings!;
            return new HookRunner(settings.ExePath, settings.WorkingDir, settings.Environment, LogSystem);
        }
    }
}
