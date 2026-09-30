// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Entitlements.Features;
using XiHan.Framework.Entitlements.Features.Abstractions;

namespace XiHan.Framework.Entitlements.Providers;

/// <summary>
/// 功能授权决策提供器的框架默认实现
/// </summary>
/// <remarks>
/// 不接任何外部基础设施，一律按未知功能拒绝，让「没有接入政策来源」落到安全的一侧。
/// 应用要放行任何功能都必须注册自己的 <see cref="IFeatureEntitlementProvider"/> 替换本实现。
/// </remarks>
public class DefaultFeatureEntitlementProvider : IFeatureEntitlementProvider
{
    /// <summary>
    /// 评估某租户对某功能的授权，一律返回未知功能拒绝
    /// </summary>
    /// <param name="tenantId">租户标识，平台为 0，不可为负</param>
    /// <param name="featureKey">功能标识，不可为空白</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>拒绝决策，拒绝原因为 <see cref="FeatureDenyReason.UnknownFeature"/></returns>
    /// <exception cref="ArgumentOutOfRangeException">租户标识为负</exception>
    /// <exception cref="ArgumentException">功能标识为空白</exception>
    public Task<FeatureDecision> EvaluateAsync(
        long tenantId,
        string featureKey,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(FeatureDecision.Denied(tenantId, featureKey, FeatureDenyReason.UnknownFeature));
    }
}
