using System;
using System.Runtime.InteropServices;

namespace Services.Core.Helpers
{
    public static class ServiceUtils
    {
        public const uint SERVICE_QUERY_STATUS = 0x0004;
        public const uint SC_MANAGER_CONNECT = 0x0001;
        public const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
        public const uint SERVICE_ALL_ACCESS = 0xF01FF;
        public const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
        public const uint SERVICE_AUTO_START = 0x00000002;
        public const uint SERVICE_ERROR_NORMAL = 0x00000001;
        public const uint DELETE = 0x00010000;
        public const uint SERVICE_CHANGE_CONFIG = 0x0002;
        public const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
        public const uint SERVICE_CONFIG_DESCRIPTION = 1;
        public const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
        public const uint SERVICE_CONFIG_FAILURE_ACTIONS_FLAG = 4;
        private const int ERROR_INVALID_NAME = 123;
        private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;

        /// <summary>独立服务包装器的文件名，与 ServicesApp.exe / servicesctl.exe 位于同一目录</summary>
        public const string WrapperExeName = "servicesvc.exe";
        /// <summary>包装器命令行参数：&lt;包装器&gt; --service-wrapper &lt;服务ID&gt;</summary>
        public const string WrapperArgument = "--service-wrapper";

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_FAILURE_ACTIONS_FLAG
        {
            [MarshalAs(UnmanagedType.Bool)] public bool fFailureActionsOnNonCrashFailures;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_DELAYED_AUTO_START_INFO
        {
            [MarshalAs(UnmanagedType.Bool)] public bool fDelayedAutostart;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SERVICE_DESCRIPTION
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpDescription;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS_PROCESS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
            public uint dwProcessId;
            public uint dwServiceFlags;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateService(
            IntPtr hSCManager,
            string lpServiceName,
            string lpDisplayName,
            uint dwDesiredAccess,
            uint dwServiceType,
            uint dwStartType,
            uint dwErrorControl,
            string lpBinaryPathName,
            string? lpLoadOrderGroup,
            IntPtr lpdwTagId,
            string? lpDependencies,
            string? lpServiceStartName,
            string? lpPassword);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteService(IntPtr hService);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ChangeServiceConfig(
            IntPtr hService,
            uint dwServiceType,
            uint dwStartType,
            uint dwErrorControl,
            string? lpBinaryPathName,
            string? lpLoadOrderGroup,
            IntPtr lpdwTagId,
            string? lpDependencies,
            string? lpServiceStartName,
            string? lpPassword,
            string? lpDisplayName);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfig2W")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ChangeServiceConfig2(IntPtr hService, uint dwInfoLevel, ref SERVICE_DESCRIPTION lpInfo);

        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ChangeServiceConfig2(IntPtr hService, uint dwInfoLevel, ref SERVICE_FAILURE_ACTIONS_FLAG lpInfo);

        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ChangeServiceConfig2(IntPtr hService, uint dwInfoLevel, ref SERVICE_DELAYED_AUTO_START_INFO lpInfo);

        /// <summary>
        /// 设置服务是否延迟自动启动（仅对自动启动类型的服务有意义，调用前应已将启动类型设为 Auto）
        /// </summary>
        public static void SetDelayedAutoStart(string serviceName, bool delayed)
        {
            IntPtr scmHandle = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scmHandle == IntPtr.Zero)
                throw new Exception($"Failed to open SC Manager. Error: {Marshal.GetLastWin32Error()}");

            try
            {
                IntPtr serviceHandle = OpenService(scmHandle, serviceName, SERVICE_CHANGE_CONFIG);
                if (serviceHandle == IntPtr.Zero)
                    throw new Exception($"Failed to open service. Error: {Marshal.GetLastWin32Error()}");

                try
                {
                    var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = delayed };
                    if (!ChangeServiceConfig2(serviceHandle, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info))
                        throw new Exception($"Failed to set delayed auto start. Error: {Marshal.GetLastWin32Error()}");
                }
                finally
                {
                    CloseServiceHandle(serviceHandle);
                }
            }
            finally
            {
                CloseServiceHandle(scmHandle);
            }
        }

        /// <summary>
        /// 系统中是否存在指定名称的服务（含驱动类服务，如 Tcpip；名称不区分大小写）。
        /// 仅在明确返回“服务不存在 / 名称无效”时为 false，其他错误（如无法连接 SCM）视为存在，交由后续操作报告。
        /// </summary>
        public static bool ServiceExists(string serviceName)
        {
            IntPtr scmHandle = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scmHandle == IntPtr.Zero) return true;

            try
            {
                IntPtr serviceHandle = OpenService(scmHandle, serviceName, SERVICE_QUERY_STATUS);
                if (serviceHandle != IntPtr.Zero)
                {
                    CloseServiceHandle(serviceHandle);
                    return true;
                }

                int error = Marshal.GetLastWin32Error();
                return error != ERROR_SERVICE_DOES_NOT_EXIST && error != ERROR_INVALID_NAME;
            }
            finally
            {
                CloseServiceHandle(scmHandle);
            }
        }

        /// <summary>
        /// 设置服务以非 0 退出码停止时是否也触发 SCM 恢复策略（默认仅进程崩溃时触发）
        /// </summary>
        public static void SetFailureActionsOnNonCrashFailures(string serviceName, bool enabled)
        {
            IntPtr scmHandle = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scmHandle == IntPtr.Zero)
                throw new Exception($"Failed to open SC Manager. Error: {Marshal.GetLastWin32Error()}");

            try
            {
                IntPtr serviceHandle = OpenService(scmHandle, serviceName, SERVICE_CHANGE_CONFIG);
                if (serviceHandle == IntPtr.Zero)
                    throw new Exception($"Failed to open service. Error: {Marshal.GetLastWin32Error()}");

                try
                {
                    var info = new SERVICE_FAILURE_ACTIONS_FLAG { fFailureActionsOnNonCrashFailures = enabled };
                    if (!ChangeServiceConfig2(serviceHandle, SERVICE_CONFIG_FAILURE_ACTIONS_FLAG, ref info))
                        throw new Exception($"Failed to set failure actions flag. Error: {Marshal.GetLastWin32Error()}");
                }
                finally
                {
                    CloseServiceHandle(serviceHandle);
                }
            }
            finally
            {
                CloseServiceHandle(scmHandle);
            }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool QueryServiceStatusEx(IntPtr hService, int infoLevel, IntPtr lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

        /// <summary>
        /// 服务 ImagePath：由包装器以服务 ID 启动
        /// </summary>
        public static string BuildWrapperCommand(string wrapperExePath, string serviceId)
        {
            return $"\"{wrapperExePath}\" {WrapperArgument} \"{serviceId}\"";
        }

        /// <summary>
        /// 从服务的 ImagePath 中取出包装器程序路径：仅识别由本工具创建的包装器命令
        /// （"&lt;程序&gt;" --service-wrapper "&lt;服务ID&gt;"），其他服务返回 null
        /// </summary>
        public static string? ParseWrapperExecutable(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            var text = imagePath.Trim();
            if (!text.Contains(WrapperArgument, StringComparison.Ordinal)) return null;

            if (text.StartsWith('"'))
            {
                int end = text.IndexOf('"', 1);
                return end > 1 ? text.Substring(1, end - 1) : null;
            }
            int space = text.IndexOf(' ');
            return space > 0 ? text.Substring(0, space) : null;
        }

        /// <summary>
        /// 将多行启动参数合并为单行命令行（各行去除首尾空白、忽略空行，以空格拼接）
        /// </summary>
        public static string NormalizeArgs(string? args)
        {
            if (string.IsNullOrWhiteSpace(args)) return string.Empty;

            var lines = args.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join(" ", lines);
        }

        public static (string Status, int Pid) GetServiceStatus(string serviceName)
        {
            IntPtr hSCManager = IntPtr.Zero;
            IntPtr hService = IntPtr.Zero;
            IntPtr ptr = IntPtr.Zero;

            try
            {
                hSCManager = OpenSCManager(null, null, SC_MANAGER_CONNECT);
                if (hSCManager == IntPtr.Zero) return ("未知", 0);

                hService = OpenService(hSCManager, serviceName, SERVICE_QUERY_STATUS);
                if (hService == IntPtr.Zero) return ("未知", 0);

                uint bytesNeeded;
                ptr = Marshal.AllocHGlobal(1024);

                if (QueryServiceStatusEx(hService, 0, ptr, 1024, out bytesNeeded))
                {
                    var status = Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(ptr);
                    string statusStr = status.dwCurrentState switch
                    {
                        1 => "已停止",
                        2 => "启动中",
                        3 => "停止中",
                        4 => "运行中",
                        _ => "未知"
                    };
                    return (statusStr, (int)status.dwProcessId);
                }
            }
            catch
            {
                // Ignore exceptions
            }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
                if (hService != IntPtr.Zero) CloseServiceHandle(hService);
                if (hSCManager != IntPtr.Zero) CloseServiceHandle(hSCManager);
            }

            return ("未知", 0);
        }
    }
}
