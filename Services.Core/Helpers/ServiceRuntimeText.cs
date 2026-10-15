using System;

namespace Services.Core.Helpers
{
    /// <summary>
    /// 运行状态的展示文本（GUI 详情与 CLI 文本输出共用）
    /// </summary>
    public static class ServiceRuntimeText
    {
        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

        public static string Restarts(int restartCount, int totalRestarts) =>
            $"本轮 {restartCount} 次，累计 {totalRestarts} 次";

        /// <summary>目标进程从未退出过时返回 null</summary>
        public static string? LastExit(int? exitCode, DateTime? exitAt)
        {
            if (exitCode == null && exitAt == null) return null;

            var code = exitCode is { } c ? $"退出码 {c}" : "退出码未知";
            return exitAt is { } at ? $"{code}（{at.ToString(TimeFormat)}）" : code;
        }
    }
}
