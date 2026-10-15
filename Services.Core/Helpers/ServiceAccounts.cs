using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Services.Core.Models;

namespace Services.Core.Helpers
{
    /// <summary>
    /// 服务运行账户：账户名与 SCM ObjectName 的相互转换、授予“作为服务登录”权限、校验密码、授予日志目录写权限。
    /// </summary>
    public static class ServiceAccounts
    {
        public const string LocalSystemName = "LocalSystem";
        public const string LocalServiceName = @"NT AUTHORITY\LocalService";
        public const string NetworkServiceName = @"NT AUTHORITY\NetworkService";
        public const string VirtualAccountPrefix = @"NT SERVICE\";

        /// <summary>
        /// 由服务注册表中的 ObjectName 推断账户类型
        /// </summary>
        public static ServiceAccountType ParseObjectName(string? objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName)) return ServiceAccountType.LocalSystem;

            var name = objectName.Trim();
            if (name.Equals(LocalSystemName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(@".\LocalSystem", StringComparison.OrdinalIgnoreCase))
                return ServiceAccountType.LocalSystem;
            if (name.Equals(LocalServiceName, StringComparison.OrdinalIgnoreCase))
                return ServiceAccountType.LocalService;
            if (name.Equals(NetworkServiceName, StringComparison.OrdinalIgnoreCase))
                return ServiceAccountType.NetworkService;
            if (name.StartsWith(VirtualAccountPrefix, StringComparison.OrdinalIgnoreCase))
                return ServiceAccountType.Virtual;
            return ServiceAccountType.User;
        }

        /// <summary>
        /// 计算写入 SCM 的账户名（ObjectName）。指定用户时使用 userName，其余类型忽略 userName。
        /// </summary>
        public static string ToObjectName(ServiceAccountType type, string serviceId, string? userName) => type switch
        {
            ServiceAccountType.LocalService => LocalServiceName,
            ServiceAccountType.NetworkService => NetworkServiceName,
            ServiceAccountType.Virtual => VirtualAccountPrefix + serviceId,
            ServiceAccountType.User => string.IsNullOrWhiteSpace(userName)
                ? throw new ArgumentException("User name is required for a user account.")
                : userName.Trim(),
            _ => LocalSystemName
        };

        /// <summary>
        /// 为指定用户授予 SeServiceLogonRight（“作为服务登录”）。内置服务账户与虚拟账户无需授予。
        /// </summary>
        public static void GrantLogonAsService(string accountName)
        {
            var sid = ResolveSid(accountName);
            var sidBytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(sidBytes, 0);

            var attributes = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
            uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, POLICY_CREATE_ACCOUNT | POLICY_LOOKUP_NAMES, out var policy);
            if (status != 0)
                throw new Win32Exception((int)LsaNtStatusToWinError(status), "LsaOpenPolicy failed");

            var right = "SeServiceLogonRight";
            var rightBuffer = Marshal.StringToHGlobalUni(right);
            try
            {
                var rights = new[]
                {
                    new LSA_UNICODE_STRING
                    {
                        Buffer = rightBuffer,
                        Length = (ushort)(right.Length * sizeof(char)),
                        MaximumLength = (ushort)((right.Length + 1) * sizeof(char))
                    }
                };
                status = LsaAddAccountRights(policy, sidBytes, rights, 1);
                if (status != 0)
                    throw new Win32Exception((int)LsaNtStatusToWinError(status), "LsaAddAccountRights failed");
            }
            finally
            {
                Marshal.FreeHGlobal(rightBuffer);
                LsaClose(policy);
            }
        }

        /// <summary>
        /// 以服务登录方式验证账户密码，提前发现错误（否则要到启动服务时才报 1069 登录失败）。
        /// 需先授予“作为服务登录”权限。
        /// </summary>
        public static void ValidateCredentials(string accountName, string password)
        {
            var (domain, user) = SplitAccountName(accountName);
            if (!LogonUser(user, domain, password, LOGON32_LOGON_SERVICE, LOGON32_PROVIDER_DEFAULT, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Logon failed for account '{accountName}'");
            CloseHandle(token);
        }

        /// <summary>
        /// 为账户授予目录的修改权限（继承到子目录与文件），用于非 SYSTEM 账户写日志
        /// </summary>
        public static void GrantDirectoryModify(string directory, string accountName)
        {
            Directory.CreateDirectory(directory);
            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                ResolveSid(accountName),
                FileSystemRights.Modify | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }

        private static SecurityIdentifier ResolveSid(string accountName)
        {
            // ".\user" 表示本机账户，NTAccount 无法直接解析
            var name = accountName.StartsWith(@".\", StringComparison.Ordinal)
                ? Environment.MachineName + accountName.Substring(1)
                : accountName;
            return (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
        }

        private static (string? Domain, string User) SplitAccountName(string accountName)
        {
            int slash = accountName.IndexOf('\\');
            if (slash >= 0) return (accountName.Substring(0, slash), accountName.Substring(slash + 1));
            // UPN（user@domain）由 LogonUser 自行解析
            return accountName.Contains('@') ? (null, accountName) : (".", accountName);
        }

        private const int LOGON32_LOGON_SERVICE = 5;
        private const int LOGON32_PROVIDER_DEFAULT = 0;
        private const int POLICY_CREATE_ACCOUNT = 0x00000010;
        private const int POLICY_LOOKUP_NAMES = 0x00000800;

        [StructLayout(LayoutKind.Sequential)]
        private struct LSA_UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LSA_OBJECT_ATTRIBUTES
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint LsaOpenPolicy(IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES objectAttributes, int desiredAccess, out IntPtr policyHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint LsaAddAccountRights(IntPtr policyHandle, byte[] accountSid, LSA_UNICODE_STRING[] userRights, uint countOfRights);

        [DllImport("advapi32.dll")]
        private static extern uint LsaClose(IntPtr policyHandle);

        [DllImport("advapi32.dll")]
        private static extern uint LsaNtStatusToWinError(uint status);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LogonUser(string user, string? domain, string password, int logonType, int logonProvider, out IntPtr token);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
