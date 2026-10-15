using System;
using System.Runtime.InteropServices;
using Services.Core.Models;
using static Services.Core.Scm.ScmNative;

namespace Services.Core.Scm
{
    /// <summary>
    /// SCM 操作失败。消息格式为 "Failed to ... Error: &lt;Win32 错误码&gt;"。
    /// </summary>
    public sealed class ScmException : Exception
    {
        public int ErrorCode { get; }

        public ScmException(string action, int errorCode) : base($"Failed to {action}. Error: {errorCode}")
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// 服务控制管理器（SCM）的托管封装：每个方法独立打开并关闭句柄，失败时抛出 <see cref="ScmException"/>。
    /// </summary>
    public static class ScmClient
    {
        /// <summary>
        /// 查询服务状态与进程 ID（未运行时为 0）。服务不存在或无法查询时返回 Unknown，不抛出异常。
        /// </summary>
        public static (ServiceRunState State, int Pid) QueryStatus(string serviceId)
        {
            try
            {
                IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
                if (scm == IntPtr.Zero) return (ServiceRunState.Unknown, 0);
                try
                {
                    IntPtr service = OpenService(scm, serviceId, SERVICE_QUERY_STATUS);
                    if (service == IntPtr.Zero) return (ServiceRunState.Unknown, 0);
                    try
                    {
                        if (!QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, out var status,
                                (uint)Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _))
                            return (ServiceRunState.Unknown, 0);

                        var state = status.dwCurrentState is >= 1 and <= 7 ? (ServiceRunState)status.dwCurrentState : ServiceRunState.Unknown;
                        return (state, (int)status.dwProcessId);
                    }
                    finally
                    {
                        CloseServiceHandle(service);
                    }
                }
                finally
                {
                    CloseServiceHandle(scm);
                }
            }
            catch
            {
                return (ServiceRunState.Unknown, 0);
            }
        }

        /// <summary>
        /// 系统中是否存在指定名称的服务（含驱动类服务，如 Tcpip；名称不区分大小写）。
        /// 仅在明确返回“服务不存在 / 名称无效”时为 false，其他错误（如无法连接 SCM）视为存在，交由后续操作报告。
        /// </summary>
        public static bool Exists(string serviceId)
        {
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return true;
            try
            {
                IntPtr service = OpenService(scm, serviceId, SERVICE_QUERY_STATUS);
                if (service != IntPtr.Zero)
                {
                    CloseServiceHandle(service);
                    return true;
                }

                int error = Marshal.GetLastWin32Error();
                return error != ERROR_SERVICE_DOES_NOT_EXIST && error != ERROR_INVALID_NAME;
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }

        /// <summary>
        /// 创建以独立进程运行的服务
        /// </summary>
        /// <param name="dependencies">依赖的多字符串（以 \0 分隔、\0\0 结尾），无依赖时为 null</param>
        public static void Create(string serviceId, string displayName, ServiceStartupType startType, string binaryPath, string? dependencies)
        {
            IntPtr scm = OpenScm(SC_MANAGER_CREATE_SERVICE);
            try
            {
                IntPtr service = CreateService(scm, serviceId, displayName, SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS,
                    (uint)startType, SERVICE_ERROR_NORMAL, binaryPath, null, IntPtr.Zero, dependencies, null, null);
                if (service == IntPtr.Zero) throw new ScmException("create service", Marshal.GetLastWin32Error());
                CloseServiceHandle(service);
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }

        public static void Delete(string serviceId) =>
            WithService(serviceId, DELETE, "open service for deletion", handle =>
            {
                if (!DeleteService(handle)) throw new ScmException("delete service", Marshal.GetLastWin32Error());
            });

        /// <summary>
        /// 修改服务配置；为 null 的参数保持不变
        /// </summary>
        /// <param name="dependencies">新的依赖多字符串（空依赖为 "\0"）；null 表示不修改</param>
        public static void ChangeConfig(string serviceId, ServiceStartupType? startType = null, string? displayName = null,
            string? dependencies = null, string? binaryPath = null, string what = "update service config") =>
            WithService(serviceId, SERVICE_CHANGE_CONFIG, "open service for update", handle =>
            {
                if (!ChangeServiceConfig(handle, SERVICE_NO_CHANGE,
                        startType is { } type ? (uint)type : SERVICE_NO_CHANGE,
                        SERVICE_NO_CHANGE, binaryPath, null, IntPtr.Zero, dependencies, null, null, displayName))
                    throw new ScmException(what, Marshal.GetLastWin32Error());
            });

        public static void ChangeBinaryPath(string serviceId, string binaryPath) =>
            ChangeConfig(serviceId, binaryPath: binaryPath, what: "change service binary path");

        /// <summary>
        /// 修改运行账户。password 仅指定用户需要；虚拟账户必须传 null（传空字符串会失败，错误 1057），内置账户忽略密码。
        /// </summary>
        public static void ChangeAccount(string serviceId, string objectName, string? password) =>
            WithService(serviceId, SERVICE_CHANGE_CONFIG, "open service", handle =>
            {
                if (!ChangeServiceConfig(handle, SERVICE_NO_CHANGE, SERVICE_NO_CHANGE, SERVICE_NO_CHANGE,
                        null, null, IntPtr.Zero, null, objectName, password, null))
                    throw new ScmException("change service account", Marshal.GetLastWin32Error());
            });

        public static void SetDescription(string serviceId, string description) =>
            WithService(serviceId, SERVICE_CHANGE_CONFIG, "open service", handle =>
            {
                var info = new SERVICE_DESCRIPTION { lpDescription = description };
                if (!ChangeServiceConfig2(handle, SERVICE_CONFIG_DESCRIPTION, ref info))
                    throw new ScmException("set service description", Marshal.GetLastWin32Error());
            });

        /// <summary>
        /// 设置服务是否延迟自动启动（仅对自动启动类型的服务有意义，调用前应已将启动类型设为 Auto）
        /// </summary>
        public static void SetDelayedAutoStart(string serviceId, bool delayed) =>
            WithService(serviceId, SERVICE_CHANGE_CONFIG, "open service", handle =>
            {
                var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = delayed };
                if (!ChangeServiceConfig2(handle, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info))
                    throw new ScmException("set delayed auto start", Marshal.GetLastWin32Error());
            });

        /// <summary>
        /// 设置服务以非 0 退出码停止时是否也触发 SCM 恢复策略（默认仅进程崩溃时触发）
        /// </summary>
        public static void SetFailureActionsOnNonCrashFailures(string serviceId, bool enabled) =>
            WithService(serviceId, SERVICE_CHANGE_CONFIG, "open service", handle =>
            {
                var info = new SERVICE_FAILURE_ACTIONS_FLAG { fFailureActionsOnNonCrashFailures = enabled };
                if (!ChangeServiceConfig2(handle, SERVICE_CONFIG_FAILURE_ACTIONS_FLAG, ref info))
                    throw new ScmException("set failure actions flag", Marshal.GetLastWin32Error());
            });

        private static IntPtr OpenScm(uint access)
        {
            IntPtr scm = OpenSCManager(null, null, access);
            if (scm == IntPtr.Zero) throw new ScmException("open SC Manager", Marshal.GetLastWin32Error());
            return scm;
        }

        private static void WithService(string serviceId, uint access, string openAction, Action<IntPtr> action)
        {
            IntPtr scm = OpenScm(SC_MANAGER_CONNECT);
            try
            {
                IntPtr service = OpenService(scm, serviceId, access);
                if (service == IntPtr.Zero) throw new ScmException(openAction, Marshal.GetLastWin32Error());
                try
                {
                    action(service);
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }
    }
}
