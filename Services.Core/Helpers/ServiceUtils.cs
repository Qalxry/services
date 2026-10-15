using System;

namespace Services.Core.Helpers
{
    /// <summary>
    /// 包装器命令行与启动参数的纯逻辑辅助（SCM 调用见 Scm.ScmClient）
    /// </summary>
    public static class ServiceUtils
    {
        /// <summary>独立服务包装器的文件名，与 ServicesApp.exe / servicesctl.exe 位于同一目录</summary>
        public const string WrapperExeName = "servicesvc.exe";
        /// <summary>包装器命令行参数：&lt;包装器&gt; --service-wrapper &lt;服务ID&gt;</summary>
        public const string WrapperArgument = "--service-wrapper";

        /// <summary>
        /// 服务 ImagePath：由包装器以服务 ID 启动
        /// </summary>
        public static string BuildWrapperCommand(string wrapperExePath, string serviceId)
        {
            return $"\"{wrapperExePath}\" {WrapperArgument} \"{serviceId}\"";
        }

        /// <summary>
        /// 从服务的 ImagePath 中取出包装器程序路径：仅识别由本工具创建的包装器命令
        /// （"&lt;程序&gt;" --service-wrapper "&lt;服务ID&gt;"），其他服务返回 null
        /// </summary>
        public static string? ParseWrapperExecutable(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            var text = imagePath.Trim();
            if (!text.Contains(WrapperArgument, StringComparison.Ordinal)) return null;

            if (text.StartsWith('"'))
            {
                int end = text.IndexOf('"', 1);
                return end > 1 ? text.Substring(1, end - 1) : null;
            }
            int space = text.IndexOf(' ');
            return space > 0 ? text.Substring(0, space) : null;
        }

        /// <summary>
        /// 将多行启动参数合并为单行命令行（各行去除首尾空白、忽略空行，以空格拼接）
        /// </summary>
        public static string NormalizeArgs(string? args)
        {
            if (string.IsNullOrWhiteSpace(args)) return string.Empty;

            var lines = args.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join(" ", lines);
        }
    }
}
