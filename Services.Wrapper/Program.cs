using System.Diagnostics;
using System.ServiceProcess;
using Services.Core.Helpers;
using Services.Core.Services;

namespace Services.Wrapper
{
    internal static class Program
    {
        /// <summary>
        /// 用法：servicesvc.exe --service-wrapper &lt;服务ID&gt;（由服务控制管理器启动，不应手动运行）
        /// </summary>
        private static int Main(string[] args)
        {
            if (args.Length < 2 || args[0] != ServiceUtils.WrapperArgument)
            {
                Console.Error.WriteLine($"servicesvc 是 ServicesApp 的服务包装器，由 Windows 服务控制管理器启动，不应手动运行。");
                Console.Error.WriteLine($"用法: servicesvc.exe {ServiceUtils.WrapperArgument} <服务ID>");
                Console.Error.WriteLine("管理服务请使用 ServicesApp 或 servicesctl。");
                return 2;
            }

            // 服务进程的当前目录默认为 System32，切换到程序所在目录
            var dir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
            if (!string.IsNullOrEmpty(dir)) Directory.SetCurrentDirectory(dir);

            using var wrapper = new EmbeddedServiceWrapper(args[1]);
            try
            {
                ServiceBase.Run(wrapper);
            }
            catch (ServiceStartFailedException)
            {
                // 原因已写入服务日志、运行状态与事件日志，退出码已报告给 SCM
                return 1;
            }
            return 0;
        }
    }
}
