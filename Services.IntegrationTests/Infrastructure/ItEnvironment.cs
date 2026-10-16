using System.Diagnostics;
using System.Security.Principal;

namespace Services.IntegrationTests.Infrastructure
{
    /// <summary>
    /// 集成测试运行环境。需要：
    ///   SERVICES_IT_BIN     已发布的 servicesctl.exe 与 servicesvc.exe 所在目录
    ///   SERVICES_IT_TARGET  已发布的 testtarget.exe（Services.TestTarget）所在目录
    /// 以及管理员权限。设置 SERVICES_IT_REQUIRED=1（CI 中）时条件不满足不再跳过而是失败，避免测试被静默跳过。
    /// 程序被复制到 %ProgramData%\ServicesIT\&lt;运行 ID&gt;\bin，
    /// 该目录对内置服务账户与虚拟账户可读（工作区所在目录不一定如此）。
    /// </summary>
    internal static class ItEnvironment
    {
        public const string NamePrefix = "IT_";

        private static readonly Lazy<State> LazyState = new(Initialize);

        public static bool Required => Environment.GetEnvironmentVariable("SERVICES_IT_REQUIRED") == "1";

        public static string? SkipReason => LazyState.Value.SkipReason;

        public static string BinDir => LazyState.Value.BinDir
            ?? throw new InvalidOperationException($"集成测试环境不可用：{SkipReason}");
        public static string CtlPath => Path.Combine(BinDir, "servicesctl.exe");
        public static string WrapperPath => Path.Combine(BinDir, "servicesvc.exe");
        public static string TargetPath => Path.Combine(BinDir, "testtarget.exe");

        /// <summary>
        /// 当前运行的可写工作目录（LocalSystem 运行的目标程序在此写入标记文件）
        /// </summary>
        public static string WorkDir => LazyState.Value.WorkDir
            ?? throw new InvalidOperationException($"集成测试环境不可用：{SkipReason}");

        /// <summary>
        /// 为单个测试创建独立的空目录
        /// </summary>
        public static string NewWorkDir(string name)
        {
            var dir = Path.Combine(WorkDir, $"{name}_{Guid.NewGuid().ToString("N")[..8]}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private sealed class State
        {
            public string? SkipReason;
            public string? BinDir;
            public string? WorkDir;
        }

        private static State Initialize()
        {
            if (!OperatingSystem.IsWindows())
                return new State { SkipReason = "集成测试仅在 Windows 上运行" };

            var bin = Environment.GetEnvironmentVariable("SERVICES_IT_BIN");
            var target = Environment.GetEnvironmentVariable("SERVICES_IT_TARGET");
            if (string.IsNullOrEmpty(bin) || string.IsNullOrEmpty(target))
                return new State { SkipReason = "未设置 SERVICES_IT_BIN / SERVICES_IT_TARGET，跳过集成测试" };

            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    return new State { SkipReason = "集成测试需要管理员权限" };
            }

            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ServicesIT", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var binDir = Path.Combine(root, "bin");
            var workDir = Path.Combine(root, "work");
            Directory.CreateDirectory(binDir);
            Directory.CreateDirectory(workDir);

            foreach (var name in new[] { "servicesctl.exe", "servicesvc.exe" })
                File.Copy(Path.Combine(bin, name), Path.Combine(binDir, name), overwrite: true);
            foreach (var file in Directory.GetFiles(target))
                File.Copy(file, Path.Combine(binDir, Path.GetFileName(file)), overwrite: true);

            var state = new State { BinDir = binDir, WorkDir = workDir };
            RemoveLeftoverServices(Path.Combine(binDir, "servicesctl.exe"));
            return state;
        }

        /// <summary>
        /// 删除以前中断的运行遗留的测试服务
        /// </summary>
        private static void RemoveLeftoverServices(string ctl)
        {
            try
            {
                var list = Ctl.RunWith(ctl, "list", "--json");
                if (list.ExitCode != 0) return;
                foreach (var service in list.Json().EnumerateArray())
                {
                    var name = service.GetProperty("name").GetString() ?? "";
                    if (!name.StartsWith(NamePrefix, StringComparison.Ordinal)) continue;
                    Ctl.RunWith(ctl, "delete", service.GetProperty("id").GetString()!);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to remove leftover services: {ex.Message}");
            }
        }
    }
}
