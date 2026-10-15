using Xunit;

// 测试会创建真实的系统服务并读写全局日志目录，串行执行以免相互干扰、结果更稳定
[assembly: CollectionBehavior(DisableTestParallelization = true)]
