using System;
using System.Collections.Generic;
using System.Linq;

namespace Services.Core.Diagnostics
{
    /// <summary>ACL 中的一项（SID 为字符串形式，如 S-1-5-32-545）</summary>
    public sealed record AceInfo(string Sid, int Rights, bool Allow);

    public enum AccessVerdict
    {
        /// <summary>候选身份中有允许规则覆盖所需权限，且没有拒绝规则</summary>
        Granted,
        /// <summary>有拒绝规则命中所需权限</summary>
        Denied,
        /// <summary>没有规则覆盖所需权限（账户可能通过未知的组成员身份获得权限，因此只能判定为“可能无权”）</summary>
        NotGranted
    }

    /// <summary>
    /// 近似的有效权限判断：只考虑账户自身及其已知的内置组（不展开域组 / 嵌套组），用于诊断提示而非安全决策。
    /// </summary>
    public static class AccessEvaluator
    {
        public const string Everyone = "S-1-1-0";
        public const string AuthenticatedUsers = "S-1-5-11";
        public const string Users = "S-1-5-32-545";
        public const string ServiceLogon = "S-1-5-6";
        public const string AllServices = "S-1-5-80-0";
        public const string LocalSystem = "S-1-5-18";
        public const string LocalService = "S-1-5-19";
        public const string NetworkService = "S-1-5-20";
        public const string Administrators = "S-1-5-32-544";

        public static AccessVerdict Evaluate(IEnumerable<AceInfo> aces, IReadOnlyCollection<string> sids, int required)
        {
            var relevant = aces.Where(a => sids.Contains(a.Sid, StringComparer.OrdinalIgnoreCase)).ToList();
            if (relevant.Any(a => !a.Allow && (a.Rights & required) != 0)) return AccessVerdict.Denied;

            int allowed = relevant.Where(a => a.Allow).Aggregate(0, (acc, a) => acc | a.Rights);
            return (allowed & required) == required ? AccessVerdict.Granted : AccessVerdict.NotGranted;
        }

        /// <summary>
        /// 服务以该账户运行时进程令牌中必然包含的身份：账户自身与内置组
        /// （服务进程都含 SERVICE；虚拟账户与内置服务账户还含 ALL SERVICES 等，此处取保守的公共部分）
        /// </summary>
        public static IReadOnlyCollection<string> CandidateSids(string accountSid)
        {
            var sids = new List<string> { accountSid, Everyone, AuthenticatedUsers, Users, ServiceLogon };
            if (accountSid.StartsWith("S-1-5-80-", StringComparison.Ordinal)) sids.Add(AllServices);
            return sids;
        }
    }
}
