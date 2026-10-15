using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Services.Core.Helpers;

namespace Services.Core.Storage
{
    /// <summary>
    /// 敏感环境变量的存储：Services\&lt;ID&gt;\Parameters\Secrets，每个变量一个 REG_SZ 值。
    /// 子键禁止继承，只允许 SYSTEM、Administrators 完全控制与服务运行账户读取；
    /// 其余用户（服务键默认对所有已认证用户可读）无法读取。
    /// </summary>
    public static class SecretStore
    {
        /// <summary>
        /// 写入全部敏感变量（替换原有内容）并重设 ACL。runAsAccount 为服务运行账户（LocalSystem 时无需额外授权）。
        /// </summary>
        public static void Write(string serviceId, IReadOnlyDictionary<string, string> secrets, string? runAsAccount)
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            var path = RegistryPaths.Secrets(serviceId);

            if (secrets.Count == 0)
            {
                hklm.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                return;
            }

            using var key = hklm.CreateSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.None)
                ?? throw new InvalidOperationException($"Cannot create registry key {path}");
            key.SetAccessControl(BuildSecurity(runAsAccount));

            foreach (var name in key.GetValueNames())
            {
                if (!secrets.ContainsKey(name)) key.DeleteValue(name, throwOnMissingValue: false);
            }
            foreach (var (name, value) in secrets)
            {
                key.SetValue(name, value, RegistryValueKind.String);
            }
        }

        /// <summary>
        /// 读取敏感变量的值。子键不存在时返回空集合；无权读取时返回 null。
        /// </summary>
        public static Dictionary<string, string>? TryRead(string serviceId)
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hklm.OpenSubKey(RegistryPaths.Secrets(serviceId));
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (key == null) return result;

                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValue(name) is string value) result[name] = value;
                }
                return result;
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// 子键的 ACL：SYSTEM、Administrators 完全控制；服务运行账户（非 LocalSystem 时）读取
        /// </summary>
        public static RegistrySecurity BuildSecurity(string? runAsAccount)
        {
            var security = new RegistrySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit;
            security.AddAccessRule(new RegistryAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                RegistryRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new RegistryAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                RegistryRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            if (!string.IsNullOrWhiteSpace(runAsAccount) &&
                ServiceAccounts.ParseObjectName(runAsAccount) != Models.ServiceAccountType.LocalSystem)
            {
                security.AddAccessRule(new RegistryAccessRule(
                    ServiceAccounts.ResolveSid(runAsAccount),
                    RegistryRights.ReadKey, inherit, PropagationFlags.None, AccessControlType.Allow));
            }
            return security;
        }
    }
}
