using System;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Services.Core.Notifications;

namespace Services.Core.Storage
{
    /// <summary>
    /// Webhook 设置的读写。Webhook 地址常含令牌，因此不放在所有用户可读的位置：
    /// 全局设置只允许 SYSTEM、管理员与服务进程（SERVICE 组）读取；服务级设置只允许 SYSTEM、管理员与该服务的运行账户读取。
    /// </summary>
    public static class NotificationStore
    {
        /// <summary>读取全局设置；未设置或无权读取时返回 null</summary>
        public static WebhookSettings? ReadGlobal() => Read(RegistryPaths.GlobalNotifications);

        /// <summary>读取服务级设置；未设置或无权读取时返回 null</summary>
        public static WebhookSettings? ReadService(string serviceId) => Read(RegistryPaths.ServiceNotifications(serviceId));

        /// <summary>服务级设置是否存在但无权读取（非管理员）</summary>
        public static bool IsServiceUnreadable(string serviceId)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(RegistryPaths.ServiceNotifications(serviceId));
                return false;
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        /// <summary>写入全局设置；settings 未启用（URL 为空）时删除</summary>
        public static void WriteGlobal(WebhookSettings settings)
        {
            var security = new RegistrySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddRule(security, WellKnownSidType.LocalSystemSid, RegistryRights.FullControl);
            AddRule(security, WellKnownSidType.BuiltinAdministratorsSid, RegistryRights.FullControl);
            AddRule(security, WellKnownSidType.ServiceSid, RegistryRights.ReadKey);
            Write(RegistryPaths.GlobalNotifications, settings, security);
        }

        /// <summary>写入服务级设置（ACL 同敏感环境变量）；settings 未启用时删除，即沿用全局设置</summary>
        public static void WriteService(string serviceId, WebhookSettings settings, string? runAsAccount) =>
            Write(RegistryPaths.ServiceNotifications(serviceId), settings, SecretStore.BuildSecurity(runAsAccount));

        private static WebhookSettings? Read(string path)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                var url = key?.GetValue(NotificationNames.WebhookUrl) as string;
                if (string.IsNullOrWhiteSpace(url)) return null;

                var events = WebhookEvents.Default as System.Collections.Generic.IReadOnlyList<string>;
                try
                {
                    events = WebhookEvents.Parse(key!.GetValue(NotificationNames.WebhookEvents) as string);
                }
                catch (ArgumentException)
                {
                    // 被手工改坏的事件列表按默认处理
                }
                return new WebhookSettings(url.Trim(), events);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void Write(string path, WebhookSettings settings, RegistrySecurity security)
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (!settings.Enabled)
            {
                hklm.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                return;
            }

            using var key = hklm.CreateSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.None)
                ?? throw new InvalidOperationException($"Cannot create registry key {path}");
            key.SetAccessControl(security);
            key.SetValue(NotificationNames.WebhookUrl, settings.Url.Trim(), RegistryValueKind.String);
            key.SetValue(NotificationNames.WebhookEvents, WebhookEvents.Format(settings.Events), RegistryValueKind.String);
        }

        private static void AddRule(RegistrySecurity security, WellKnownSidType sid, RegistryRights rights) =>
            security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(sid, null), rights,
                InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
    }
}
