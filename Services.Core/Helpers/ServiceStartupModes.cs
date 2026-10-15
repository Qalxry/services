using System;
using Services.Core.Models;

namespace Services.Core.Helpers
{
    /// <summary>
    /// 启动模式 = SCM 启动类型 + “延迟自动启动”标志。延迟自动启动只对自动启动类型有意义，
    /// 因此 UI 把它与三种启动类型并列为四个选项；本类负责选项索引与命令行取值的统一映射（无 Windows 依赖）。
    /// 显示文本见 Presentation.ServiceText.StartupModeNames。
    /// </summary>
    public static class ServiceStartupModes
    {
        /// <summary>
        /// 延迟标志只在自动启动时有效；其他启动类型一律视为不延迟
        /// </summary>
        public static bool NormalizeDelayed(ServiceStartupType type, bool delayed) =>
            type == ServiceStartupType.Auto && delayed;

        /// <summary>
        /// (启动类型, 是否延迟) → 下拉框索引
        /// </summary>
        public static int ToIndex(ServiceStartupType type, bool delayed) => type switch
        {
            ServiceStartupType.Auto => NormalizeDelayed(type, delayed) ? 1 : 0,
            ServiceStartupType.Manual => 2,
            ServiceStartupType.Disabled => 3,
            _ => 0
        };

        /// <summary>
        /// 下拉框索引 → (启动类型, 是否延迟)；索引无效（如未选中的 -1）时按开机自启处理
        /// </summary>
        public static (ServiceStartupType Type, bool Delayed) FromIndex(int index) => index switch
        {
            1 => (ServiceStartupType.Auto, true),
            2 => (ServiceStartupType.Manual, false),
            3 => (ServiceStartupType.Disabled, false),
            _ => (ServiceStartupType.Auto, false)
        };

        /// <summary>
        /// 解析命令行 --startup 的取值（不区分大小写）：auto | delayed-auto | manual | disabled
        /// </summary>
        public static bool TryParse(string? text, out ServiceStartupType type, out bool delayed)
        {
            delayed = false;
            switch (text?.Trim().ToLowerInvariant())
            {
                case "auto": type = ServiceStartupType.Auto; return true;
                case "delayed-auto": type = ServiceStartupType.Auto; delayed = true; return true;
                case "manual": type = ServiceStartupType.Manual; return true;
                case "disabled": type = ServiceStartupType.Disabled; return true;
                default: type = ServiceStartupType.Auto; return false;
            }
        }

        /// <summary>
        /// 命令行 / JSON 中的取值：auto | delayed-auto | manual | disabled
        /// </summary>
        public static string ToCliValue(ServiceStartupType type, bool delayed) =>
            NormalizeDelayed(type, delayed) ? "delayed-auto" : type.ToString().ToLowerInvariant();
    }
}
