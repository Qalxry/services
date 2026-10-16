using System;
using Services.Core.Models;

namespace Services.Core.Presentation
{
    public enum StateFilter
    {
        All,
        Running,
        Stopped,
        /// <summary>启动中、停止中、未知等</summary>
        Other
    }

    /// <summary>
    /// 服务列表的搜索与状态筛选（纯逻辑）。关键字匹配名称、ID、程序路径与描述，不区分大小写；
    /// 多个关键字以空白分隔，须全部匹配。
    /// </summary>
    public sealed record ServiceFilter(string? Query, StateFilter State)
    {
        public static ServiceFilter None { get; } = new(null, StateFilter.All);

        public bool IsActive => !string.IsNullOrWhiteSpace(Query) || State != StateFilter.All;

        public bool Matches(Service service)
        {
            bool stateMatches = State switch
            {
                StateFilter.Running => service.State == ServiceRunState.Running,
                StateFilter.Stopped => service.State == ServiceRunState.Stopped,
                StateFilter.Other => service.State is not (ServiceRunState.Running or ServiceRunState.Stopped),
                _ => true
            };
            if (!stateMatches) return false;
            if (string.IsNullOrWhiteSpace(Query)) return true;

            foreach (var term in Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                bool found = Contains(service.Name, term) || Contains(service.Id, term) ||
                             Contains(service.ExePath, term) || Contains(service.Description, term);
                if (!found) return false;
            }
            return true;
        }

        private static bool Contains(string? text, string term) =>
            text?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    }
}
