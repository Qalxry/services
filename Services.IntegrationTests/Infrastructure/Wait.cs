using System.Diagnostics;

namespace Services.IntegrationTests.Infrastructure
{
    internal static class Wait
    {
        public static readonly TimeSpan Default = TimeSpan.FromSeconds(30);

        /// <summary>
        /// 轮询直到条件成立，超时则以 describe() 的内容失败（便于定位原因）
        /// </summary>
        public static void Until(Func<bool> condition, string what, TimeSpan? timeout = null, Func<string>? describe = null)
        {
            var limit = timeout ?? Default;
            var watch = Stopwatch.StartNew();
            while (true)
            {
                if (condition()) return;
                if (watch.Elapsed > limit)
                {
                    var detail = describe == null ? "" : "\n" + SafeDescribe(describe);
                    throw new TimeoutException($"等待超时（{limit.TotalSeconds:0} 秒）：{what}{detail}");
                }
                Thread.Sleep(250);
            }
        }

        /// <summary>
        /// 进程是否已不存在。只查询进程列表、不打开进程句柄（服务进程以 SYSTEM 运行，管理员也未必能打开）
        /// </summary>
        public static bool ProcessExited(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        /// <summary>
        /// 强制结束单个进程（不含子进程）。使用 taskkill：它会启用调试权限，可结束以 SYSTEM 运行的进程
        /// </summary>
        public static void KillProcess(int pid)
        {
            var psi = new ProcessStartInfo("taskkill.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add("/F");
            psi.ArgumentList.Add("/PID");
            psi.ArgumentList.Add(pid.ToString());
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"taskkill /F /PID {pid} 失败: {output}");
        }

        private static string SafeDescribe(Func<string> describe)
        {
            try { return describe(); }
            catch (Exception ex) { return $"(无法获取详情: {ex.Message})"; }
        }
    }
}
