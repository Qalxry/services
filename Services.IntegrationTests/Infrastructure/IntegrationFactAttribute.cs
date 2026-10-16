namespace Services.IntegrationTests.Infrastructure
{
    /// <summary>
    /// 运行环境不满足时（非 Windows、未指定程序目录、非管理员）自动跳过的测试；
    /// 设置了 SERVICES_IT_REQUIRED=1 时不跳过，由测试在访问环境时失败
    /// </summary>
    public sealed class IntegrationFactAttribute : FactAttribute
    {
        public IntegrationFactAttribute()
        {
            if (!ItEnvironment.Required && ItEnvironment.SkipReason is { } reason) Skip = reason;
        }
    }
}
