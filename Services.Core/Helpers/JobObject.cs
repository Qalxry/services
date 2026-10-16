using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Services.Core.Wrapper;

namespace Services.Core.Helpers
{
    /// <summary>
    /// Windows Job Object。
    /// 默认构造的 Job 设置了 KILL_ON_JOB_CLOSE：句柄关闭时（包括持有进程崩溃或被强制结束），系统会结束 Job 内的所有进程，
    /// 避免遗留孤儿进程。<see cref="CreateWithLimits"/> 创建只用于资源限制的 Job（嵌套在前者之内）。
    /// </summary>
    public sealed class JobObject : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const int JobObjectCpuRateControlInformation = 15;
        private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x8;
        private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x100;
        private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x200;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
        private const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

        private IntPtr _handle;

        public JobObject() : this(JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, 0, 0, 0)
        {
        }

        private JobObject(uint limitFlags, ulong processMemory, ulong jobMemory, uint activeProcesses)
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = limitFlags;
            info.BasicLimitInformation.ActiveProcessLimit = activeProcesses;
            info.ProcessMemoryLimit = (UIntPtr)processMemory;
            info.JobMemoryLimit = (UIntPtr)jobMemory;

            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                ThrowAndClose("SetInformationJobObject failed");
        }

        /// <summary>
        /// 创建只施加资源限制的 Job（不设置 KILL_ON_JOB_CLOSE；进程树的回收由包装器所在的 Job 负责）
        /// </summary>
        public static JobObject CreateWithLimits(ResourceLimits limits)
        {
            uint flags = 0;
            if (limits.ProcessMemoryLimitMB > 0) flags |= JOB_OBJECT_LIMIT_PROCESS_MEMORY;
            if (limits.MemoryLimitMB > 0) flags |= JOB_OBJECT_LIMIT_JOB_MEMORY;
            if (limits.MaxProcesses > 0) flags |= JOB_OBJECT_LIMIT_ACTIVE_PROCESS;

            var job = new JobObject(flags,
                (ulong)Math.Max(limits.ProcessMemoryLimitMB, 0) * 1024 * 1024,
                (ulong)Math.Max(limits.MemoryLimitMB, 0) * 1024 * 1024,
                (uint)Math.Max(limits.MaxProcesses, 0));

            if (limits.CpuLimitPercent > 0)
            {
                var cpu = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
                {
                    ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                    // 以 1/100 百分比为单位：10000 表示全部 CPU
                    CpuRate = (uint)Math.Clamp(limits.CpuLimitPercent, 1, 100) * 100
                };
                if (!SetInformationJobObject(job._handle, JobObjectCpuRateControlInformation, ref cpu, (uint)Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>()))
                    job.ThrowAndClose("Setting CPU rate limit failed");
            }
            return job;
        }

        /// <summary>
        /// 将指定进程加入 Job（进程此后创建的子进程同样属于该 Job）
        /// </summary>
        public void AssignProcess(IntPtr processHandle)
        {
            if (!AssignProcessToJobObject(_handle, processHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
        }

        private void ThrowAndClose(string message)
        {
            int error = Marshal.GetLastWin32Error();
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
            throw new Win32Exception(error, message);
        }

        /// <summary>
        /// 将当前进程加入 Job。此后当前进程创建的所有子进程从创建起即属于该 Job，不存在逃逸窗口。
        /// 注意：加入后不应在当前进程退出前调用 Dispose，否则会连同当前进程一起结束。
        /// </summary>
        public void AssignCurrentProcess()
        {
            if (!AssignProcessToJobObject(_handle, GetCurrentProcess()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            public uint ControlFlags;
            public uint CpuRate;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
