using System;
using Services.Core.Models;
using Services.Core.Services;

namespace Services.Core.Wrapper
{
    /// <summary>
    /// 维护并写入包装器的运行状态文件。线程安全；任何失败都被忽略，绝不影响服务运行。
    /// </summary>
    public sealed class RuntimeStateRecorder
    {
        private readonly object _lock = new();
        private readonly ServiceStateStore? _store;
        private readonly ServiceRuntimeState? _state;

        /// <summary>
        /// 创建本次运行的状态并写入。上次运行留下的“最近退出 / 失败原因”予以保留
        /// （服务被 SCM 恢复策略重新拉起后仍可看到上一次为何停止），其余计数从零开始。
        /// </summary>
        public RuntimeStateRecorder(string serviceId, int wrapperPid, DateTime now)
        {
            try
            {
                _store = new ServiceStateStore();
                var previous = _store.Read(serviceId);
                _state = new ServiceRuntimeState
                {
                    ServiceId = serviceId,
                    WrapperPid = wrapperPid,
                    WrapperStartedAt = now,
                    LastExitCode = previous?.LastExitCode,
                    LastExitAt = previous?.LastExitAt,
                    LastFailureReason = previous?.LastFailureReason,
                    LastFailureAt = previous?.LastFailureAt,
                    UpdatedAt = now
                };
                _store.Write(_state);
            }
            catch
            {
                _store = null;
                _state = null;
            }
        }

        public void Update(Action<ServiceRuntimeState> update)
        {
            try
            {
                lock (_lock)
                {
                    if (_state == null || _store == null) return;
                    update(_state);
                    _state.UpdatedAt = DateTime.Now;
                    _store.Write(_state);
                }
            }
            catch { }
        }

        public void ProcessStarted(int pid, DateTime at) => Update(s =>
        {
            s.ProcessPid = pid;
            s.ProcessStartedAt = at;
        });

        public void ProcessExited(int exitCode, DateTime at) => Update(s =>
        {
            s.ProcessPid = null;
            s.ProcessStartedAt = null;
            s.LastExitCode = exitCode;
            s.LastExitAt = at;
        });

        public void ProcessGone() => Update(s =>
        {
            s.ProcessPid = null;
            s.ProcessStartedAt = null;
        });

        public void Restarts(int count, int total) => Update(s =>
        {
            s.RestartCount = count;
            s.TotalRestarts = total;
        });

        public void Failed(string reason) => Update(s =>
        {
            s.LastFailureReason = reason;
            s.LastFailureAt = DateTime.Now;
        });
    }
}
