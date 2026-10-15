using System;
using Services.Core.Helpers;
using Services.Core.Models;
using Services.Core.Scm;

namespace Services.Core.Services
{
    /// <summary>
    /// 服务运行账户的设置流程（校验、授权、写入 SCM、授予日志目录权限）
    /// </summary>
    internal static class ServiceAccountSetup
    {
        /// <summary>
        /// 指定用户：必须提供密码；授予“作为服务登录”权限并校验密码。在修改服务之前执行，失败时不留下半成品。
        /// </summary>
        public static void PrepareUserAccount(ServiceConfig config, string? password)
        {
            if (config.AccountType != ServiceAccountType.User) return;

            if (string.IsNullOrWhiteSpace(config.AccountName))
                throw new ArgumentException("Account name is required for a user account.");
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password is required for a user account.");

            var accountName = config.AccountName.Trim();
            ServiceAccounts.GrantLogonAsService(accountName);
            ServiceAccounts.ValidateCredentials(accountName, password);
        }

        /// <summary>
        /// 设置服务运行账户，并为非 LocalSystem 账户授予日志目录写权限
        /// </summary>
        public static void Apply(string serviceId, ServiceConfig config, string? password)
        {
            var objectName = ServiceAccounts.ToObjectName(config.AccountType, serviceId, config.AccountName);

            // 虚拟账户在服务存在后才能解析 SID，因此先设置账户再授权目录。
            // 只有指定用户需要密码；虚拟账户的密码必须为 NULL（传空字符串会失败，错误 1057），内置账户忽略密码。
            ScmClient.ChangeAccount(serviceId, objectName, config.AccountType == ServiceAccountType.User ? password ?? "" : null);

            if (config.AccountType != ServiceAccountType.LocalSystem)
            {
                ServiceAccounts.GrantDirectoryModify(new LogManager().GetLogDirectory(), objectName);
            }
        }
    }
}
