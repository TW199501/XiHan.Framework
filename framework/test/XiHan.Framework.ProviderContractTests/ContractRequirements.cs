// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.Framework.ProviderContractTests;

/// <summary>
/// 契约用例的能力前置条件
/// </summary>
internal static class ContractRequirements
{
    /// <summary>
    /// 提供方未声明所需能力时跳过当前用例
    /// </summary>
    /// <param name="declared">提供方声明的能力</param>
    /// <param name="required">用例需要的能力</param>
    public static void Require(ProviderCapabilities declared, ProviderCapabilities required)
    {
        var missing = required & ~declared;
        Assert.SkipWhen(missing != ProviderCapabilities.None, $"提供方未声明 {missing} 能力，跳过本契约用例。");
    }

    /// <summary>
    /// 让已取回的记录重新对所有客户端可见
    /// </summary>
    /// <remarks>
    /// 不做独占领取的提供方无需处理；做独占领取但不支持领取过期的提供方无法验证后续取回，跳过当前用例。
    /// </remarks>
    /// <typeparam name="TProvider">被测契约类型</typeparam>
    /// <param name="fixture">夹具</param>
    /// <returns>任务</returns>
    public static async Task ReleaseClaimsAsync<TProvider>(IProviderContractFixture<TProvider> fixture)
        where TProvider : class
    {
        if (!fixture.Capabilities.HasFlag(ProviderCapabilities.ExclusiveClaim))
        {
            return;
        }

        Require(fixture.Capabilities, ProviderCapabilities.ClaimExpiry);
        await fixture.ExpireClaimsAsync();
    }

    /// <summary>
    /// 把时间截到整秒，避免不同数据库的时间精度影响比较
    /// </summary>
    /// <param name="value">时间</param>
    /// <returns>截到整秒的 UTC 时间</returns>
    public static DateTime TruncateToSeconds(DateTime value)
    {
        return new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }
}
