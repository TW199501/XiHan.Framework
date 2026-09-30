// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Entitlements.Features;
using XiHan.Framework.Entitlements.Features.Abstractions;

namespace XiHan.Framework.Entitlements.Tests;

/// <summary>
/// 按授权集合判定的功能授权提供器测试桩
/// </summary>
internal sealed class StubFeatureEntitlementProvider : IFeatureEntitlementProvider
{
    /// <summary>
    /// 已授权的功能与租户组合，缺少条目即未授权
    /// </summary>
    public HashSet<(long TenantId, string FeatureKey)> Grants { get; } = [];

    /// <summary>
    /// 按授权集合判定功能是否放行
    /// </summary>
    /// <param name="tenantId">租户标识</param>
    /// <param name="featureKey">功能标识</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>授权决策</returns>
    public Task<FeatureDecision> EvaluateAsync(
        long tenantId,
        string featureKey,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Grants.Contains((tenantId, featureKey))
            ? FeatureDecision.Enabled(tenantId, featureKey, "stub")
            : FeatureDecision.Denied(tenantId, featureKey, FeatureDenyReason.NotEntitled, "stub"));
    }
}
