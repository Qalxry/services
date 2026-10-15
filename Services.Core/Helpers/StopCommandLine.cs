using System;

namespace Services.Core.Helpers
{
    /// <summary>
    /// 停止命令的占位符展开：{exe}、{workdir} 自动加引号，{pid} 为目标进程 ID。
    /// 展开结果交由 cmd.exe 执行，因此可直接使用 PATH 中的命令（如 redis-cli shutdown）。
    /// </summary>
    public static class StopCommandLine
    {
        public static string Expand(string command, string exePath, string workingDir, int pid)
        {
            return command
                .Replace("{exe}", Quote(exePath), StringComparison.OrdinalIgnoreCase)
                .Replace("{workdir}", Quote(workingDir), StringComparison.OrdinalIgnoreCase)
                .Replace("{pid}", pid.ToString(), StringComparison.OrdinalIgnoreCase)
                .Trim();
        }

        /// <summary>
        /// cmd.exe 参数：/d 不执行 AutoRun，/s 配合外层引号原样保留命令中的引号
        /// </summary>
        public static string ToCmdArguments(string expandedCommand) => $"/d /s /c \"{expandedCommand}\"";

        private static string Quote(string value) => $"\"{value.Trim('"')}\"";
    }
}
