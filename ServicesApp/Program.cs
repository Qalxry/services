using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Services.Core.Services;

namespace ServicesApp
{
    public static class Program
    {
        private const string ShowWindowEventName = "Global\\Services_App_ShowWindow_Event";
        private const int ASFW_ANY = -1;

        /// <summary>
        /// 已运行实例监听的事件：再次启动程序时由新进程置位，通知已运行实例显示主窗口
        /// </summary>
        public static EventWaitHandle? ShowWindowEvent { get; private set; }

        [STAThread]
        static void Main(string[] args)
        {
            // 兼容旧版本创建、尚未迁移到独立包装器 servicesvc.exe 的服务
            if (args.Length >= 2 && args[0] == Services.Core.Helpers.ServiceUtils.WrapperArgument)
            {
                // Fix: Ensure CurrentDirectory is set to app folder, not System32
                // This is crucial for loading dependencies correctly when running as a Service
                var module = Process.GetCurrentProcess().MainModule;
                if (module != null)
                {
                    var dir = System.IO.Path.GetDirectoryName(module.FileName);
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.SetCurrentDirectory(dir);
                }

                var serviceName = args[1];
                using var wrapper = new EmbeddedServiceWrapper(serviceName);
                try
                {
                    ServiceBase.Run(wrapper);
                }
                catch (ServiceStartFailedException)
                {
                    // 原因已写入服务日志、运行状态与事件日志，退出码已报告给 SCM
                }
                return;
            }

            const string mutexName = "Global\\Services_App_SingleInstance_Mutex";
            using var mutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew)
            {
                // 已运行的实例可能隐藏在托盘（无可见窗口句柄），同名进程中也包含服务包装器进程，
                // 因此不再查找窗口句柄，而是通过命名事件通知已运行实例自行显示主窗口。
                SignalRunningInstance();
                return;
            }

            using var showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            ShowWindowEvent = showWindowEvent;

            WinRT.ComWrappersSupport.InitializeComWrappers();

            Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }

        private static void SignalRunningInstance()
        {
            // 新启动的进程拥有前台权限，允许已运行实例把窗口切到前台
            AllowSetForegroundWindow(ASFW_ANY);

            // 已运行实例可能刚创建互斥量、尚未创建事件，短暂重试
            for (int i = 0; i < 20; i++)
            {
                if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var showWindowEvent))
                {
                    using (showWindowEvent)
                    {
                        showWindowEvent.Set();
                    }
                    return;
                }
                Thread.Sleep(100);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);
    }
}
