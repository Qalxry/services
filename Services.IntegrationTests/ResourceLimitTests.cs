using Services.IntegrationTests.Infrastructure;

namespace Services.IntegrationTests
{
    public class ResourceLimitTests
    {
        [IntegrationFact]
        public void MemoryLimitStopsLargeAllocation()
        {
            using var service = TestService.Create("MemLimit", "--alloc-mb 400", "--memory-limit", "150", "--no-auto-restart");
            Ctl.Run("start", service.Id);

            service.WaitForLogContaining("Resource limits: memory 150MB");
            // 超出上限：分配失败（托管 OOM）或进程被系统结束，都不应成功分配
            Wait.Until(() => service.HasLog("alloc failed") || service.HasLogContaining("Process exited"), "目标程序因内存上限而失败",
                describe: service.Describe);
            Assert.False(service.HasLog("allocated 400 MB"), service.Describe());

            var limits = service.Get().GetProperty("resourceLimits");
            Assert.Equal(150, limits.GetProperty("memoryMB").GetInt32());
        }

        [IntegrationFact]
        public void AllocationWithinLimitSucceeds()
        {
            using var service = TestService.Create("MemOk", "--alloc-mb 20", "--memory-limit", "300", "--start");
            service.WaitForLog("allocated 20 MB");
        }

        [IntegrationFact]
        public void ProcessLimitPreventsChildProcesses()
        {
            var childFile = Path.Combine(ItEnvironment.NewWorkDir("proclimit"), "child.txt");
            using var service = TestService.Create("ProcLimit", $"--spawn-child {childFile}", "--max-processes", "1", "--start");

            service.WaitForLogContaining("spawn failed");
            Assert.False(File.Exists(childFile));
        }

        [IntegrationFact]
        public void CpuLimitIsAppliedAndShown()
        {
            using var service = TestService.Create("CpuLimit", "", "--cpu-limit", "25", "--start");
            service.WaitForLogContaining("CPU 25%");
            Assert.False(service.HasLogContaining("failed to apply resource limits"), service.Describe());
            Assert.Contains("CPU 25%", Ctl.Run("get", service.Id).Stdout);

            Ctl.Json("update", service.Id, "--cpu-limit", "0");
            Assert.Equal(0, service.Get().GetProperty("resourceLimits").GetProperty("cpuPercent").GetInt32());
        }
    }
}
