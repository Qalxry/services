using System;
using System.Runtime.InteropServices;

namespace Services.Core.Helpers
{
    /// <summary>
    /// 向其他进程的控制台发送 Ctrl+C（与 NSSM 的做法相同）。
    /// 服务进程没有控制台；目标程序以 CreateNoWindow 启动时拥有一个隐藏控制台，可附加后发送事件。
    /// </summary>
    public static class ConsoleControl
    {
        private const uint CTRL_C_EVENT = 0;
        private static readonly object Lock = new();

        /// <summary>
        /// 向指定进程所在的控制台发送 Ctrl+C。返回是否成功发出（不代表目标程序已退出）。
        /// 调用后本进程永久忽略 Ctrl+C，仅用于停止流程。
        /// </summary>
        public static bool SendCtrlC(int processId)
        {
            // 附加控制台是进程级状态，串行执行
            lock (Lock)
            {
                FreeConsole();
                if (!AttachConsole((uint)processId)) return false;

                try
                {
                    // 让本进程忽略 Ctrl+C，避免包装器自身被事件结束。事件异步投递，脱离控制台后仍可能到达，
                    // 因此不恢复默认处理；该设置会被之后创建的子进程继承，只应在停止阶段（不再启动子进程）调用。
                    SetConsoleCtrlHandler(IntPtr.Zero, true);
                    // 进程组 0：发送给附加到该控制台的所有进程
                    return GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
                }
                finally
                {
                    FreeConsole();
                }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
    }
}
